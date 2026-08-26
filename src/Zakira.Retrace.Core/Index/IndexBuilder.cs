using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Core.Configuration;
using Zakira.Retrace.Core.Embeddings;

namespace Zakira.Retrace.Core.Index;

/// <summary>What an index build or refresh did.</summary>
public sealed record IndexBuildResult
{
    /// <summary>Per-source outcomes.</summary>
    public IReadOnlyList<SourceIndexResult> Sources { get; init; } = [];

    /// <summary>Sessions added or updated.</summary>
    public int SessionsIndexed => Sources.Sum(source => source.SessionsIndexed);

    /// <summary>Sessions skipped because their content hash was unchanged.</summary>
    public int SessionsSkipped => Sources.Sum(source => source.SessionsSkipped);

    /// <summary>Sessions removed because they no longer exist in their source.</summary>
    public int SessionsRemoved => Sources.Sum(source => source.SessionsRemoved);

    /// <summary>Passages written.</summary>
    public int ChunksIndexed => Sources.Sum(source => source.ChunksIndexed);

    /// <summary>Vectors computed.</summary>
    public int VectorsComputed => Sources.Sum(source => source.VectorsComputed);

    /// <summary>Total wall-clock duration.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Embedding model used, or null when indexing was keyword-only.</summary>
    public string? EmbeddingModel { get; init; }
}

/// <summary>One source's contribution to a build.</summary>
public sealed record SourceIndexResult
{
    /// <summary>Source id.</summary>
    public required string SourceId { get; init; }

    /// <summary>Sessions added or updated.</summary>
    public int SessionsIndexed { get; init; }

    /// <summary>Sessions left alone because nothing changed.</summary>
    public int SessionsSkipped { get; init; }

    /// <summary>Sessions deleted from the index.</summary>
    public int SessionsRemoved { get; init; }

    /// <summary>Passages written.</summary>
    public int ChunksIndexed { get; init; }

    /// <summary>Vectors computed.</summary>
    public int VectorsComputed { get; init; }

    /// <summary>Why this source contributed nothing, when it did not.</summary>
    public string? SkipReason { get; init; }
}

/// <summary>Options for a build or refresh.</summary>
public sealed record IndexBuildOptions
{
    /// <summary>Restrict to these sources. Empty means every enabled source.</summary>
    public IReadOnlyList<string> SourceIds { get; init; } = [];

    /// <summary>
    /// Ignore watermarks and content hashes and re-read everything. Needed after a chunking or
    /// embedding-model change, which the stored hashes cannot detect.
    /// </summary>
    public bool Force { get; init; }

    /// <summary>Remove indexed sessions that no longer exist in their source. Costs a full id scan.</summary>
    public bool Prune { get; init; }

    /// <summary>Compute vectors. Turning this off produces a keyword-only index far faster.</summary>
    public bool Embed { get; init; } = true;

    /// <summary>
    /// Also re-process sessions that are already indexed but hold no vectors.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The incremental skip is driven by the content hash alone, which asks "has this conversation
    /// changed?" and not "is this session fully indexed?". Those come apart as soon as a session is
    /// indexed without an embedder — which is exactly what an automatic refresh does — because the
    /// hash then records content that was only ever made keyword-searchable. Such a session is
    /// skipped by every later refresh and stays invisible to semantic search indefinitely.
    /// </para>
    /// <para>
    /// This flag closes that gap without disturbing the fast path: it is off by default, and the
    /// query that identifies the affected sessions only runs when it is on and an embedder exists.
    /// It is opt-in rather than automatic because the backlog can be enormous — a source that was
    /// deliberately left keyword-only would otherwise turn a routine refresh into a multi-hour job.
    /// Scope it with <see cref="SourceIds"/> or <see cref="Since"/>.
    /// </para>
    /// <para>
    /// Unlike <see cref="Force"/>, this re-processes only the sessions that are actually missing
    /// vectors, so the cost is proportional to the gap rather than to the whole source.
    /// </para>
    /// </remarks>
    public bool Backfill { get; init; }

    /// <summary>
    /// Only index sessions updated at or after this instant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists so a large source can be embedded in affordable slices rather than all at once.
    /// Note that it only decides which sessions are <em>considered</em>: a session already in the
    /// index with an unchanged content hash is still skipped, so scoping alone will not add vectors
    /// to previously indexed sessions. Combine it with <see cref="Backfill"/> to fill in the ones
    /// missing vectors, or <see cref="Force"/> to re-read them regardless.
    /// </para>
    /// </remarks>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Only index sessions updated at or before this instant.</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>Whether this build covers only part of a source's history.</summary>
    public bool IsScoped => Since is not null || Until is not null;

    /// <summary>Called with progress messages so long builds are not silent.</summary>
    public IProgress<IndexProgress>? Progress { get; init; }
}

/// <summary>A progress report from an ongoing build.</summary>
public sealed record IndexProgress(string SourceId, int Processed, int? Total, string Message);

/// <summary>
/// Writes and refreshes the Retrace index.
/// </summary>
/// <remarks>
/// Refresh is incremental in two independent layers, because either alone leaves too much work:
/// a source watermark narrows the candidate set to sessions whose store says they changed, and a
/// per-session content hash then skips the ones whose indexable content did not actually move. The
/// second layer matters because several harnesses touch a session's row for reasons that do not
/// alter the conversation.
/// </remarks>
public sealed class IndexBuilder(
    RetracePaths paths,
    RetraceConfig config,
    IEnumerable<ISessionSource> sources,
    IEmbeddingProviderFactory embeddingFactory,
    ILogger<IndexBuilder> logger)
{
    private readonly ISessionSource[] sources = [.. sources];


    /// <summary>Resolved index path.</summary>
    public string IndexPath => string.IsNullOrWhiteSpace(config.Index.Path)
        ? paths.DefaultIndexPath
        : paths.ExpandPath(config.Index.Path);

    /// <summary>Builds or refreshes the index.</summary>
    public async Task<IndexBuildResult> BuildAsync(IndexBuildOptions options, CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();

        await using var connection = await RetraceIndexSchema.OpenAsync(IndexPath, cancellationToken).ConfigureAwait(false);

        using var embedder = options.Embed && config.Embeddings.Enabled
            ? await embeddingFactory.CreateAsync(cancellationToken).ConfigureAwait(false)
            : null;

        await EnsureEmbeddingCompatibilityAsync(connection, embedder, options.Force, cancellationToken).ConfigureAwait(false);

        var selected = options.SourceIds.Count == 0
            ? sources
            : [.. sources.Where(source => options.SourceIds.Contains(source.Id, StringComparer.OrdinalIgnoreCase))];

        var results = new List<SourceIndexResult>();

        try
        {
            foreach (var source in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(await IndexSourceAsync(connection, source, embedder, options, cancellationToken).ConfigureAwait(false));
            }
        }
        finally
        {
            // Stamped even when the build is cancelled or runs out of its time budget. Recording
            // it only on success meant an interrupted refresh left the cooldown disengaged, so the
            // very next query started the same expensive work again and could never get past it.
            // Every session that did complete is already committed, so the timestamp is honest:
            // it records when we last made progress, not that the source is now exhaustive.
            await RetraceIndexSchema.SetMetaAsync(
                connection,
                RetraceIndexSchema.LastRefreshKey,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
                CancellationToken.None).ConfigureAwait(false);
        }

        return new IndexBuildResult
        {
            Sources = results,
            Duration = started.Elapsed,
            EmbeddingModel = embedder?.ModelId
        };
    }

    private async Task<SourceIndexResult> IndexSourceAsync(
        SqliteConnection connection,
        ISessionSource source,
        IEmbeddingProvider? embedder,
        IndexBuildOptions options,
        CancellationToken cancellationToken)
    {
        var availability = await source.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!availability.IsAvailable)
        {
            return new SourceIndexResult { SourceId = source.Id, SkipReason = availability.Reason };
        }

        // Two distinct uses of the stored watermark, kept separate on purpose.
        //   storedWatermark  - what the index currently records, read unconditionally so a scoped
        //                      build can put it back afterwards.
        //   candidateWatermark - what bounds candidate selection; --force deliberately ignores it.
        // Conflating the two meant `--force --since` erased the watermark entirely, forcing the
        // next refresh into a full re-enumeration for no reason.
        //
        // A backfill must ignore it too. The sessions it exists to repair are precisely the ones an
        // earlier refresh already consumed and recorded, so they sit behind the watermark and
        // ListChangedSinceAsync would never offer them. Enumerating the source is cheap; the cost
        // that matters is reading and embedding transcripts, and the skip below still confines that
        // to sessions genuinely missing vectors.
        var storedWatermark = await ReadWatermarkAsync(connection, source.Id, cancellationToken).ConfigureAwait(false);
        var bypassWatermark = options.Force || options.Backfill;
        var candidateWatermark = bypassWatermark ? null : storedWatermark;

        var newWatermark = source is IIncrementalSource incremental
            ? await incremental.GetWatermarkAsync(cancellationToken).ConfigureAwait(false)
            : null;

        var existingHashes = await ReadContentHashesAsync(connection, source.Id, cancellationToken).ConfigureAwait(false);

        // Only queried for a backfill, and only when there is an embedder to backfill with, so the
        // ordinary refresh path is unaffected.
        var noVectorsNeeded = options.Backfill && embedder is not null
            ? await ReadSessionsNotNeedingVectorsAsync(connection, source.Id, cancellationToken).ConfigureAwait(false)
            : null;

        var chunker = new SessionChunker(new ChunkPolicy
        {
            MaxCharacters = config.Index.ChunkCharacters,
            OverlapCharacters = config.Index.ChunkOverlapCharacters,
            MinCharacters = config.Index.MinChunkCharacters,
            IncludeToolOutput = config.Index.IncludeToolOutput,
            MaxToolOutputCharacters = config.Index.MaxToolOutputCharacters,
            IncludeReasoning = config.Index.IncludeReasoning
        });

        var transcriptOptions = TranscriptOptions.Default with
        {
            IncludeToolOutput = config.Index.IncludeToolOutput,
            IncludeReasoning = config.Index.IncludeReasoning,
            MaxToolOutputCharacters = config.Index.MaxToolOutputCharacters
        };

        var indexed = 0;
        var skipped = 0;
        var chunks = 0;
        var vectors = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // The scoping bounds are pushed into the filter on the non-incremental path so they become
        // SQL, and applied in memory on the incremental path, where ListChangedSinceAsync accepts
        // only a watermark.
        var candidates = source is IIncrementalSource incrementalSource && !bypassWatermark
            ? incrementalSource.ListChangedSinceAsync(candidateWatermark, cancellationToken)
            : source.ListAsync(
                SessionFilter.All with
                {
                    IncludeArchived = true,
                    Since = options.Since,
                    Until = options.Until
                },
                cancellationToken);

        await foreach (var summary in candidates.ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (options.Since is { } since && summary.UpdatedAt < since)
            {
                continue;
            }

            if (options.Until is { } until && summary.UpdatedAt > until)
            {
                continue;
            }

            seen.Add(summary.Ref.NativeId);

            // The content hash answers "has this conversation changed?", which is not the same
            // question as "is this session fully indexed?". They diverge whenever a session was
            // written without an embedder, so a backfill additionally requires that the session
            // needs no vectors before it is allowed to skip.
            if (!options.Force
                && existingHashes.TryGetValue(summary.Ref.Uri, out var hash)
                && string.Equals(hash, summary.ContentHash, StringComparison.Ordinal)
                && (noVectorsNeeded is null || noVectorsNeeded.Contains(summary.Ref.Uri)))
            {
                skipped++;
                continue;
            }

            SessionTranscript? transcript;
            try
            {
                transcript = await source.GetAsync(summary.Ref.NativeId, transcriptOptions, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // One unreadable session must not abort a build over thousands of others.
                logger.LogDebug(ex, "Skipped session {Uri} while indexing.", summary.Ref.Uri);
                continue;
            }

            if (transcript is null)
            {
                continue;
            }

            var passages = chunker.Chunk(transcript).ToArray();
            var written = await WriteSessionAsync(connection, transcript, passages, embedder, cancellationToken).ConfigureAwait(false);

            indexed++;
            chunks += passages.Length;
            vectors += written;

            if (indexed % 25 == 0)
            {
                options.Progress?.Report(new IndexProgress(source.Id, indexed, availability.SessionCount, $"indexed {indexed} session(s)"));
            }
        }

        var removed = options.Prune
            ? await PruneAsync(connection, source, cancellationToken).ConfigureAwait(false)
            : 0;

        // A scoped build deliberately covers only part of the source, so it must not advance the
        // watermark. Recording the source's current maximum would make the next unscoped refresh
        // conclude that everything older had already been handled, and the excluded sessions would
        // never be backfilled. Preserving the previous watermark costs one cheap re-enumeration
        // later, which the content-hash check absorbs without reading a transcript.
        var watermarkToRecord = options.IsScoped ? storedWatermark : newWatermark;
        await WriteSourceStateAsync(connection, source.Id, watermarkToRecord, cancellationToken).ConfigureAwait(false);

        options.Progress?.Report(new IndexProgress(source.Id, indexed, availability.SessionCount, "done"));

        return new SourceIndexResult
        {
            SourceId = source.Id,
            SessionsIndexed = indexed,
            SessionsSkipped = skipped,
            SessionsRemoved = removed,
            ChunksIndexed = chunks,
            VectorsComputed = vectors
        };
    }

    private async Task<int> WriteSessionAsync(
        SqliteConnection connection,
        SessionTranscript transcript,
        IReadOnlyList<SessionChunk> passages,
        IEmbeddingProvider? embedder,
        CancellationToken cancellationToken)
    {
        var summary = transcript.Summary;

        // Embedding happens before the transaction opens. Inference on a batch can take hundreds of
        // milliseconds, and holding a write transaction across it would block every concurrent
        // reader for the whole build.
        var embeddable = passages.Where(chunk => chunk.IsEmbeddable && chunk.Text.Length >= RetraceIndexSchema.MinimumEmbeddableCharacters).ToArray();
        IReadOnlyList<float[]> vectors = [];

        if (embedder is not null && embeddable.Length > 0)
        {
            vectors = await EmbedInBatchesAsync(embedder, embeddable, cancellationToken).ConfigureAwait(false);
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Delete-then-insert rather than upsert: chunk counts change between versions of a session,
        // and cascading the delete keeps chunk, fts, and vector rows from ever drifting apart.
        await DeleteSessionAsync(connection, transaction, summary.Ref.Uri, cancellationToken).ConfigureAwait(false);

        var sessionRowId = await InsertSessionAsync(connection, transaction, summary, cancellationToken).ConfigureAwait(false);

        var vectorIndex = 0;
        var written = 0;

        foreach (var chunk in passages)
        {
            var chunkRowId = await InsertChunkAsync(connection, transaction, sessionRowId, chunk, cancellationToken).ConfigureAwait(false);

            if (embedder is null || !chunk.IsEmbeddable || chunk.Text.Length < RetraceIndexSchema.MinimumEmbeddableCharacters)
            {
                continue;
            }

            if (vectorIndex < vectors.Count)
            {
                var vector = vectors[vectorIndex++];
                if (vector.Length > 0)
                {
                    await InsertChunkEmbeddingAsync(connection, transaction, chunkRowId, vector, cancellationToken).ConfigureAwait(false);
                    written++;
                }
            }
        }

        if (vectors.Count > 0)
        {
            var centroid = VectorMath.Centroid(vectors);
            if (centroid.Length > 0)
            {
                await InsertSessionEmbeddingAsync(connection, transaction, sessionRowId, centroid, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (var file in transcript.Files)
        {
            await InsertFileAsync(connection, transaction, sessionRowId, file, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return written;
    }

    private async Task<IReadOnlyList<float[]>> EmbedInBatchesAsync(
        IEmbeddingProvider embedder,
        SessionChunk[] chunks,
        CancellationToken cancellationToken)
    {
        var batchSize = Math.Max(1, config.Embeddings.BatchSize);
        var results = new List<float[]>(chunks.Length);

        for (var offset = 0; offset < chunks.Length; offset += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = chunks.Skip(offset).Take(batchSize).Select(chunk => chunk.Text).ToArray();
            var embedded = await embedder.EmbedAsync(batch, EmbeddingSide.Document, cancellationToken).ConfigureAwait(false);
            results.AddRange(embedded);
        }

        return results;
    }

    private static async Task<long> InsertSessionAsync(SqliteConnection connection, SqliteTransaction transaction, SessionSummary summary, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO session (
                uri, source_id, native_id, title, preview,
                workspace_path, repository, branch, agent, models,
                created_utc, updated_utc, message_count, tool_call_count,
                total_tokens, cost, additions, deletions, files_changed,
                is_archived, parent_native_id, content_hash, indexed_utc
            ) VALUES (
                $uri, $sourceId, $nativeId, $title, $preview,
                $workspacePath, $repository, $branch, $agent, $models,
                $createdUtc, $updatedUtc, $messageCount, $toolCallCount,
                $totalTokens, $cost, $additions, $deletions, $filesChanged,
                $isArchived, $parentNativeId, $contentHash, $indexedUtc
            )
            RETURNING rowid;
            """;

        command.Parameters.AddWithValue("$uri", summary.Ref.Uri);
        command.Parameters.AddWithValue("$sourceId", summary.Ref.SourceId);
        command.Parameters.AddWithValue("$nativeId", summary.Ref.NativeId);
        command.Parameters.AddWithValue("$title", summary.Title);
        command.Parameters.AddWithValue("$preview", (object?)summary.Preview ?? DBNull.Value);
        command.Parameters.AddWithValue("$workspacePath", (object?)summary.Workspace?.Path ?? DBNull.Value);
        command.Parameters.AddWithValue("$repository", (object?)summary.Workspace?.Repository ?? DBNull.Value);
        command.Parameters.AddWithValue("$branch", (object?)summary.Workspace?.Branch ?? DBNull.Value);
        command.Parameters.AddWithValue("$agent", (object?)summary.Agent ?? DBNull.Value);
        command.Parameters.AddWithValue("$models", summary.Models.Count > 0 ? string.Join(',', summary.Models) : DBNull.Value);
        command.Parameters.AddWithValue("$createdUtc", summary.CreatedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$updatedUtc", summary.UpdatedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$messageCount", (object?)summary.Stats.MessageCount ?? DBNull.Value);
        command.Parameters.AddWithValue("$toolCallCount", (object?)summary.Stats.ToolCallCount ?? DBNull.Value);
        command.Parameters.AddWithValue("$totalTokens", (object?)summary.Stats.TotalTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("$cost", (object?)summary.Stats.Cost ?? DBNull.Value);
        command.Parameters.AddWithValue("$additions", (object?)summary.Stats.Additions ?? DBNull.Value);
        command.Parameters.AddWithValue("$deletions", (object?)summary.Stats.Deletions ?? DBNull.Value);
        command.Parameters.AddWithValue("$filesChanged", (object?)summary.Stats.FilesChanged ?? DBNull.Value);
        command.Parameters.AddWithValue("$isArchived", summary.IsArchived ? 1 : 0);
        command.Parameters.AddWithValue("$parentNativeId", (object?)summary.ParentNativeId ?? DBNull.Value);
        command.Parameters.AddWithValue("$contentHash", summary.ContentHash);
        command.Parameters.AddWithValue("$indexedUtc", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task<long> InsertChunkAsync(SqliteConnection connection, SqliteTransaction transaction, long sessionRowId, SessionChunk chunk, CancellationToken cancellationToken)
    {
        long chunkRowId;

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO chunk (session_rowid, ordinal, turn_index, role, timestamp_utc, text)
                VALUES ($sessionRowId, $ordinal, $turnIndex, $role, $timestampUtc, $text)
                RETURNING rowid;
                """;
            command.Parameters.AddWithValue("$sessionRowId", sessionRowId);
            command.Parameters.AddWithValue("$ordinal", chunk.Ordinal);
            command.Parameters.AddWithValue("$turnIndex", chunk.TurnIndex);
            command.Parameters.AddWithValue("$role", chunk.Role.ToString());
            command.Parameters.AddWithValue("$timestampUtc", (object?)chunk.Timestamp?.ToUnixTimeMilliseconds() ?? DBNull.Value);
            command.Parameters.AddWithValue("$text", chunk.Text);

            chunkRowId = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }

        // chunk_fts is external-content, so rows are not maintained automatically. Inserting here
        // rather than via triggers keeps the write path explicit and avoids trigger recursion when
        // a session is deleted and reinserted in the same transaction.
        await using (var fts = connection.CreateCommand())
        {
            fts.Transaction = transaction;
            fts.CommandText = "INSERT INTO chunk_fts (rowid, text) VALUES ($rowid, $text);";
            fts.Parameters.AddWithValue("$rowid", chunkRowId);
            fts.Parameters.AddWithValue("$text", chunk.Text);
            await fts.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return chunkRowId;
    }

    private static async Task InsertChunkEmbeddingAsync(SqliteConnection connection, SqliteTransaction transaction, long chunkRowId, float[] vector, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO chunk_embedding (chunk_rowid, vector) VALUES ($rowid, $vector);";
        command.Parameters.AddWithValue("$rowid", chunkRowId);
        command.Parameters.Add("$vector", SqliteType.Blob).Value = VectorMath.Serialize(vector);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertSessionEmbeddingAsync(SqliteConnection connection, SqliteTransaction transaction, long sessionRowId, float[] vector, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO session_embedding (session_rowid, vector) VALUES ($rowid, $vector);";
        command.Parameters.AddWithValue("$rowid", sessionRowId);
        command.Parameters.Add("$vector", SqliteType.Blob).Value = VectorMath.Serialize(vector);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertFileAsync(SqliteConnection connection, SqliteTransaction transaction, long sessionRowId, TouchedFile file, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO session_file (session_rowid, path, tool) VALUES ($rowid, $path, $tool);";
        command.Parameters.AddWithValue("$rowid", sessionRowId);
        command.Parameters.AddWithValue("$path", file.Path);
        command.Parameters.AddWithValue("$tool", (object?)file.Tool ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteSessionAsync(SqliteConnection connection, SqliteTransaction transaction, string uri, CancellationToken cancellationToken)
    {
        // Remove FTS rows explicitly: an external-content FTS5 table does not observe cascading
        // deletes on its content table, so skipping this leaks orphaned index entries that would
        // still match queries.
        await using (var fts = connection.CreateCommand())
        {
            fts.Transaction = transaction;
            fts.CommandText = """
                INSERT INTO chunk_fts (chunk_fts, rowid, text)
                SELECT 'delete', c.rowid, c.text
                FROM chunk c
                JOIN session s ON s.rowid = c.session_rowid
                WHERE s.uri = $uri;
                """;
            fts.Parameters.AddWithValue("$uri", uri);
            await fts.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM session WHERE uri = $uri;";
        command.Parameters.AddWithValue("$uri", uri);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> PruneAsync(SqliteConnection connection, ISessionSource source, CancellationToken cancellationToken)
    {
        if (source is not IIncrementalSource incremental)
        {
            return 0;
        }

        var live = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var id in incremental.ListAllIdsAsync(cancellationToken).ConfigureAwait(false))
        {
            live.Add(id);
        }

        var stale = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT native_id, uri FROM session WHERE source_id = $sourceId;";
            command.Parameters.AddWithValue("$sourceId", source.Id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!live.Contains(reader.GetString(0)))
                {
                    stale.Add(reader.GetString(1));
                }
            }
        }

        if (stale.Count == 0)
        {
            return 0;
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var uri in stale)
        {
            await DeleteSessionAsync(connection, transaction, uri, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return stale.Count;
    }

    private static async Task<string?> ReadWatermarkAsync(SqliteConnection connection, string sourceId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT watermark FROM source_state WHERE source_id = $sourceId;";
        command.Parameters.AddWithValue("$sourceId", sourceId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    private static async Task<Dictionary<string, string>> ReadContentHashesAsync(SqliteConnection connection, string sourceId, CancellationToken cancellationToken)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT uri, content_hash FROM session WHERE source_id = $sourceId;";
        command.Parameters.AddWithValue("$sourceId", sourceId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            hashes[reader.GetString(0)] = reader.GetString(1);
        }

        return hashes;
    }

    /// <summary>
    /// The uris of sessions a backfill has no reason to touch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two populations, deliberately merged. Sessions that already hold a session-level vector are
    /// done: <see cref="WriteSessionAsync"/> writes one whenever it embeds anything at all, so its
    /// absence is a reliable marker that the session was written without an embedder.
    /// </para>
    /// <para>
    /// Sessions with nothing worth embedding matter just as much. A conversation whose only passage
    /// is shorter than <see cref="RetraceIndexSchema.MinimumEmbeddableCharacters"/> will never produce a vector no
    /// matter how often it is processed, so judging by vector presence alone would re-read it on
    /// every backfill in perpetuity and leave the coverage figure permanently short of complete.
    /// The predicate here mirrors the one in <see cref="WriteSessionAsync"/>; they must agree.
    /// </para>
    /// </remarks>
    private static async Task<HashSet<string>> ReadSessionsNotNeedingVectorsAsync(SqliteConnection connection, string sourceId, CancellationToken cancellationToken)
    {
        var uris = new HashSet<string>(StringComparer.Ordinal);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.uri
            FROM session s
            WHERE s.source_id = $sourceId
              AND (
                    EXISTS (SELECT 1 FROM session_embedding se WHERE se.session_rowid = s.rowid)
                 OR NOT EXISTS (
                        SELECT 1 FROM chunk c
                        WHERE c.session_rowid = s.rowid
                          AND c.role <> 'Tool'
                          AND LENGTH(c.text) >= $minChars)
                  );
            """;
        command.Parameters.AddWithValue("$sourceId", sourceId);
        command.Parameters.AddWithValue("$minChars", RetraceIndexSchema.MinimumEmbeddableCharacters);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            uris.Add(reader.GetString(0));
        }

        return uris;
    }

    private static async Task WriteSourceStateAsync(SqliteConnection connection, string sourceId, string? watermark, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO source_state (source_id, watermark, last_indexed_utc, session_count, chunk_count)
            VALUES (
                $sourceId,
                $watermark,
                $now,
                (SELECT COUNT(*) FROM session WHERE source_id = $sourceId),
                (SELECT COUNT(*) FROM chunk c JOIN session s ON s.rowid = c.session_rowid WHERE s.source_id = $sourceId)
            )
            ON CONFLICT(source_id) DO UPDATE SET
                watermark        = excluded.watermark,
                last_indexed_utc = excluded.last_indexed_utc,
                session_count    = excluded.session_count,
                chunk_count      = excluded.chunk_count;
            """;
        command.Parameters.AddWithValue("$sourceId", sourceId);
        command.Parameters.AddWithValue("$watermark", (object?)watermark ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureEmbeddingCompatibilityAsync(
        SqliteConnection connection,
        IEmbeddingProvider? embedder,
        bool force,
        CancellationToken cancellationToken)
    {
        var indexedModel = await RetraceIndexSchema.GetMetaAsync(connection, RetraceIndexSchema.EmbeddingModelKey, cancellationToken).ConfigureAwait(false);

        if (embedder is null)
        {
            return;
        }

        if (indexedModel is not null && !string.Equals(indexedModel, embedder.ModelId, StringComparison.OrdinalIgnoreCase))
        {
            if (!force)
            {
                throw new EmbeddingModelMismatchException(indexedModel, embedder.ModelId);
            }

            // A forced build with a different model invalidates every stored vector, so they are
            // cleared rather than left to be compared against an incompatible query vector.
            await using var clear = connection.CreateCommand();
            clear.CommandText = "DELETE FROM chunk_embedding; DELETE FROM session_embedding;";
            await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await RetraceIndexSchema.SetMetaAsync(connection, RetraceIndexSchema.EmbeddingModelKey, embedder.ModelId, cancellationToken).ConfigureAwait(false);
        await RetraceIndexSchema.SetMetaAsync(
            connection,
            RetraceIndexSchema.EmbeddingDimensionsKey,
            embedder.Dimensions.ToString(CultureInfo.InvariantCulture),
            cancellationToken).ConfigureAwait(false);
    }
}
