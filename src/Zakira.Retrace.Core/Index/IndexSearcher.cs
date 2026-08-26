using System.Globalization;
using Microsoft.Data.Sqlite;
using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Core.Configuration;
using Zakira.Retrace.Core.Embeddings;
using Zakira.Retrace.Core.Search;
using Zakira.Retrace.Core.Storage;

namespace Zakira.Retrace.Core.Index;

/// <summary>
/// Queries the Retrace index.
/// </summary>
/// <remarks>
/// <para>Search runs in four stages:</para>
/// <list type="number">
///   <item><description><b>Metadata prefilter.</b> Source, workspace, dates, agent, model, and tags
///   become SQL, narrowing the candidate sessions before any ranking work happens.</description></item>
///   <item><description><b>Lexical.</b> FTS5 <c>MATCH</c> ordered by <c>bm25</c>, over-fetching so
///   fusion has enough to work with.</description></item>
///   <item><description><b>Semantic, in two tiers.</b> Cosine against one centroid per session
///   produces a shortlist; only then are per-passage vectors loaded, and only for those sessions.
///   Scoring every passage vector on every query would mean reading the entire vector table each
///   time, which does not scale past a few thousand sessions.</description></item>
///   <item><description><b>Fusion.</b> Reciprocal rank fusion over the two ranked lists, then
///   recency and workspace boosts.</description></item>
/// </list>
/// <para>
/// RRF is used rather than a weighted sum of raw scores because BM25 and cosine live on
/// incomparable scales: BM25 is unbounded and corpus-dependent, cosine is bounded to [-1, 1].
/// Combining the two directly means an arbitrary constant decides the outcome. Fusing ranks
/// sidesteps that entirely.
/// </para>
/// </remarks>
public sealed class IndexSearcher(
    RetracePaths paths,
    RetraceConfig config,
    IEmbeddingProviderFactory embeddingFactory)
{
    /// <summary>Resolved index path.</summary>
    public string IndexPath => string.IsNullOrWhiteSpace(config.Index.Path)
        ? paths.DefaultIndexPath
        : paths.ExpandPath(config.Index.Path);

    /// <summary>Whether an index exists on disk.</summary>
    public bool Exists => File.Exists(IndexPath);

    /// <summary>Lists sessions from the index without ranking.</summary>
    public async Task<IReadOnlyList<SessionSummary>> ListAsync(SessionFilter filter, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var (where, parameters) = BuildFilterSql(filter, alias: "s");
        var order = filter.Sort switch
        {
            SessionSortOrder.Created => "s.created_utc DESC",
            SessionSortOrder.Size => "COALESCE(s.message_count, 0) DESC",
            _ => "s.updated_utc DESC"
        };

        var sql = $"""
            SELECT {SessionProjection}
            FROM session s
            {where}
            ORDER BY {order}
            {(filter.Limit > 0 ? $"LIMIT {filter.Limit}" : string.Empty)};
            """;

        var results = new List<SessionSummary>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(MapSession(reader));
        }

        return results;
    }

    /// <summary>Resolves a URI, native id, or unambiguous id prefix to one session.</summary>
    public async Task<SessionSummary?> ResolveAsync(string identifier, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ResolveCoreAsync(connection, identifier, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs a content search.</summary>
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var candidateSessions = await ReadCandidateSessionRowIdsAsync(connection, query.Filter, cancellationToken).ConfigureAwait(false);
        if (candidateSessions.Count == 0)
        {
            return [];
        }

        var mode = ResolveMode(query.Mode);

        var lexical = mode is SearchMode.Hybrid or SearchMode.Lexical
            ? await SearchLexicalAsync(connection, query, candidateSessions, cancellationToken).ConfigureAwait(false)
            : [];

        var semantic = mode is SearchMode.Hybrid or SearchMode.Semantic
            ? await SearchSemanticAsync(connection, query, candidateSessions, cancellationToken).ConfigureAwait(false)
            : [];

        if (lexical.Count == 0 && semantic.Count == 0)
        {
            return [];
        }

        var fused = Fuse(lexical, semantic);
        var top = fused
            .OrderByDescending(entry => entry.Value.Score)
            .Take(Math.Max(query.Top, 1) * 2)
            .ToArray();

        var sessions = await ReadSessionsAsync(connection, top.Select(entry => entry.Key).ToArray(), cancellationToken).ConfigureAwait(false);

        var hits = new List<SearchHit>(top.Length);
        var now = DateTimeOffset.UtcNow;
        var currentWorkspace = query.Filter.WorkspacePath;

        foreach (var (sessionRowId, scores) in top)
        {
            if (!sessions.TryGetValue(sessionRowId, out var session))
            {
                continue;
            }

            var score = ApplyBoosts(scores.Score, session, now, currentWorkspace);
            var snippets = await ReadSnippetsAsync(connection, sessionRowId, scores.ChunkRowIds, query, cancellationToken).ConfigureAwait(false);

            hits.Add(new SearchHit
            {
                Session = session,
                Score = score,
                LexicalScore = scores.Lexical,
                SemanticScore = scores.Semantic,
                Snippets = snippets
            });
        }

        return [.. hits.OrderByDescending(hit => hit.Score).Take(Math.Max(query.Top, 1))];
    }

    /// <summary>Reads the files a session touched.</summary>
    public async Task<IReadOnlyList<TouchedFile>> GetFilesAsync(string uri, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var files = new List<TouchedFile>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.path, f.tool
            FROM session_file f
            JOIN session s ON s.rowid = f.session_rowid
            WHERE s.uri = $uri
            ORDER BY f.path;
            """;
        command.Parameters.AddWithValue("$uri", uri);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            files.Add(new TouchedFile { Path = reader.GetString(0), Tool = reader.GetNullableString(1) });
        }

        return files;
    }

    /// <summary>Reports index size, freshness, and per-source state.</summary>
    public async Task<IndexStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (!Exists)
        {
            return new IndexStatus { Path = IndexPath, Exists = false };
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var sources = new List<SourceIndexState>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT source_id, watermark, last_indexed_utc, session_count, chunk_count FROM source_state ORDER BY source_id;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                sources.Add(new SourceIndexState
                {
                    SourceId = reader.GetString(0),
                    Watermark = reader.GetNullableString(1),
                    LastIndexed = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                    SessionCount = (int)reader.GetInt64(3),
                    ChunkCount = (int)reader.GetInt64(4)
                });
            }
        }

        // Sessions carrying a vector, and sessions that could carry one, per source. Derived live
        // rather than denormalised into source_state, because they change for reasons a build does
        // not record: a keyword-only refresh adds sessions without vectors, and only a join can
        // tell the two apart.
        var embedded = new Dictionary<string, (int WithVectors, int Embeddable)>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT s.source_id,
                       SUM(CASE WHEN EXISTS (SELECT 1 FROM session_embedding se WHERE se.session_rowid = s.rowid) THEN 1 ELSE 0 END),
                       SUM(CASE WHEN EXISTS (
                               SELECT 1 FROM chunk c
                               WHERE c.session_rowid = s.rowid
                                 AND c.role <> 'Tool'
                                 AND LENGTH(c.text) >= $minChars) THEN 1 ELSE 0 END)
                FROM session s
                GROUP BY s.source_id;
                """;
            command.Parameters.AddWithValue("$minChars", RetraceIndexSchema.MinimumEmbeddableCharacters);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                embedded[reader.GetString(0)] = ((int)reader.GetInt64(1), (int)reader.GetInt64(2));
            }
        }

        for (var i = 0; i < sources.Count; i++)
        {
            var counts = embedded.TryGetValue(sources[i].SourceId, out var found) ? found : default;
            sources[i] = sources[i] with
            {
                SessionsWithVectors = counts.WithVectors,
                SessionsEmbeddable = counts.Embeddable
            };
        }

        var vectors = await connection.ExecuteScalarInt64Async("SELECT COUNT(*) FROM chunk_embedding;", cancellationToken).ConfigureAwait(false) ?? 0;
        var lastRefresh = await RetraceIndexSchema.GetMetaAsync(connection, RetraceIndexSchema.LastRefreshKey, cancellationToken).ConfigureAwait(false);

        return new IndexStatus
        {
            Path = IndexPath,
            Exists = true,
            SizeBytes = new FileInfo(IndexPath).Length,
            Sources = sources,
            VectorCount = (int)vectors,
            EmbeddingModel = await RetraceIndexSchema.GetMetaAsync(connection, RetraceIndexSchema.EmbeddingModelKey, cancellationToken).ConfigureAwait(false),
            LastRefresh = long.TryParse(lastRefresh, out var epoch) ? DateTimeOffset.FromUnixTimeMilliseconds(epoch) : null
        };
    }

    private SearchMode ResolveMode(SearchMode requested)
    {
        // Hybrid silently degrades to lexical when no model is installed. Failing instead would be
        // hostile: keyword search is fully functional on its own and is the sensible default when
        // the optional model has not been downloaded.
        if (requested == SearchMode.Hybrid && (!config.Embeddings.Enabled || !embeddingFactory.IsAvailable))
        {
            return SearchMode.Lexical;
        }

        return requested;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = await RetraceIndexSchema.OpenForReadAsync(IndexPath, cancellationToken).ConfigureAwait(false);
        return connection ?? throw new IndexNotBuiltException(IndexPath);
    }

    private static async Task<SessionSummary?> ResolveCoreAsync(SqliteConnection connection, string identifier, CancellationToken cancellationToken)
    {
        // Exact URI, then exact native id, then unique prefix. Prefix resolution comes last so an
        // id that is also a valid prefix of another never becomes ambiguous.
        var exact = await QueryOneAsync(connection, $"SELECT {SessionProjection} FROM session s WHERE s.uri = $value LIMIT 2;", identifier, cancellationToken).ConfigureAwait(false);
        if (exact.Count == 1)
        {
            return exact[0];
        }

        exact = await QueryOneAsync(connection, $"SELECT {SessionProjection} FROM session s WHERE s.native_id = $value LIMIT 2;", identifier, cancellationToken).ConfigureAwait(false);
        if (exact.Count == 1)
        {
            return exact[0];
        }

        if (exact.Count > 1)
        {
            throw new SessionNotFoundException(identifier, [.. exact.Select(session => session.Ref.Uri)]);
        }

        // Accept "source/id-prefix" as well as a bare prefix.
        var separator = identifier.LastIndexOf('/');
        var prefix = separator >= 0 ? identifier[(separator + 1)..] : identifier;
        if (prefix.Length < 4)
        {
            return null;
        }

        var matches = await QueryOneAsync(
            connection,
            $"SELECT {SessionProjection} FROM session s WHERE s.native_id LIKE $value || '%' LIMIT 6;",
            prefix,
            cancellationToken).ConfigureAwait(false);

        return matches.Count switch
        {
            1 => matches[0],
            > 1 => throw new SessionNotFoundException(identifier, [.. matches.Select(session => session.Ref.Uri)]),
            _ => null
        };
    }

    private static async Task<List<SessionSummary>> QueryOneAsync(SqliteConnection connection, string sql, string value, CancellationToken cancellationToken)
    {
        var results = new List<SessionSummary>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$value", value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(MapSession(reader));
        }

        return results;
    }

    private static async Task<HashSet<long>> ReadCandidateSessionRowIdsAsync(SqliteConnection connection, SessionFilter filter, CancellationToken cancellationToken)
    {
        var (where, parameters) = BuildFilterSql(filter, alias: "s");

        var ids = new HashSet<long>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT s.rowid FROM session s {where};";
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    private async Task<List<RankedChunk>> SearchLexicalAsync(
        SqliteConnection connection,
        SearchQuery query,
        HashSet<long> candidateSessions,
        CancellationToken cancellationToken)
    {
        var match = Fts5Query.Build(query.Text);
        if (match.Length == 0)
        {
            return [];
        }

        var limit = Math.Min(
            Math.Max(query.Top * config.Search.LexicalCandidateMultiplier, 50),
            config.Search.LexicalCandidateCap);

        var results = new List<RankedChunk>();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.rowid, c.session_rowid, bm25(chunk_fts) AS rank
            FROM chunk_fts
            JOIN chunk c ON c.rowid = chunk_fts.rowid
            WHERE chunk_fts MATCH $match
            ORDER BY rank
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$match", match);
        command.Parameters.AddWithValue("$limit", limit);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var sessionRowId = reader.GetInt64(1);
                if (candidateSessions.Contains(sessionRowId))
                {
                    results.Add(new RankedChunk(reader.GetInt64(0), sessionRowId, -reader.GetDouble(2)));
                }
            }
        }
        catch (SqliteException)
        {
            // Fts5Query quotes every token, so this should be unreachable; returning nothing rather
            // than throwing keeps a semantic-only result set usable if it ever is reached.
            return [];
        }

        return results;
    }

    private async Task<List<RankedChunk>> SearchSemanticAsync(
        SqliteConnection connection,
        SearchQuery query,
        HashSet<long> candidateSessions,
        CancellationToken cancellationToken)
    {
        if (!embeddingFactory.IsAvailable)
        {
            return [];
        }

        await EnsureModelMatchesAsync(connection, cancellationToken).ConfigureAwait(false);

        using var embedder = await embeddingFactory.CreateAsync(cancellationToken).ConfigureAwait(false);
        var embedded = await embedder.EmbedAsync([query.Text], EmbeddingSide.Query, cancellationToken).ConfigureAwait(false);
        if (embedded.Count == 0 || embedded[0].Length == 0)
        {
            return [];
        }

        var queryVector = embedded[0];

        // Tier one: rank sessions by their centroid. One vector per session keeps this pass small
        // enough to be a full scan even on a large index.
        var shortlist = query.Deep
            ? candidateSessions
            : await ShortlistSessionsAsync(connection, queryVector, candidateSessions, cancellationToken).ConfigureAwait(false);

        if (shortlist.Count == 0)
        {
            return [];
        }

        // Tier two: rank passages, but only within the shortlist.
        return await RankChunksAsync(connection, queryVector, shortlist, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HashSet<long>> ShortlistSessionsAsync(
        SqliteConnection connection,
        float[] queryVector,
        HashSet<long> candidateSessions,
        CancellationToken cancellationToken)
    {
        var scored = new List<(long SessionRowId, float Score)>();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT session_rowid, vector FROM session_embedding;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var sessionRowId = reader.GetInt64(0);
            if (!candidateSessions.Contains(sessionRowId))
            {
                continue;
            }

            var vector = VectorMath.Deserialize((byte[])reader[1]);
            // Both sides are unit length, so the dot product is the cosine and skips two norms.
            var score = VectorMath.Dot(queryVector, vector);
            if (score > 0f)
            {
                scored.Add((sessionRowId, score));
            }
        }

        return [.. scored
            .OrderByDescending(entry => entry.Score)
            .Take(Math.Max(config.Search.SemanticSessionShortlist, 1))
            .Select(entry => entry.SessionRowId)];
    }

    private static async Task<List<RankedChunk>> RankChunksAsync(
        SqliteConnection connection,
        float[] queryVector,
        HashSet<long> sessionRowIds,
        CancellationToken cancellationToken)
    {
        var results = new List<RankedChunk>();

        const int batchSize = 200;
        var ids = sessionRowIds.ToArray();

        for (var offset = 0; offset < ids.Length; offset += batchSize)
        {
            var batch = ids.Skip(offset).Take(batchSize).ToArray();
            var placeholders = string.Join(",", batch.Select((_, index) => $"$p{index}"));

            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT e.chunk_rowid, c.session_rowid, e.vector
                FROM chunk_embedding e
                JOIN chunk c ON c.rowid = e.chunk_rowid
                WHERE c.session_rowid IN ({placeholders});
                """;
            for (var index = 0; index < batch.Length; index++)
            {
                command.Parameters.AddWithValue($"$p{index}", batch[index]);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var vector = VectorMath.Deserialize((byte[])reader[2]);
                var score = VectorMath.Dot(queryVector, vector);
                if (score > 0f)
                {
                    results.Add(new RankedChunk(reader.GetInt64(0), reader.GetInt64(1), score));
                }
            }
        }

        return [.. results.OrderByDescending(chunk => chunk.Score)];
    }

    private Dictionary<long, FusedScore> Fuse(List<RankedChunk> lexical, List<RankedChunk> semantic)
    {
        var fused = new Dictionary<long, FusedScore>();
        var k = Math.Max(config.Search.RrfK, 1);

        Accumulate(lexical, config.Search.LexicalWeight, isLexical: true);
        Accumulate(semantic, config.Search.SemanticWeight, isLexical: false);

        return fused;

        void Accumulate(List<RankedChunk> ranked, double weight, bool isLexical)
        {
            // Rank is assigned per session, using each session's best-ranked passage. Without that,
            // a long session containing the query term twenty times would occupy the entire result
            // list purely by repetition.
            var bestRankPerSession = new Dictionary<long, int>();
            var rank = 0;

            foreach (var chunk in ranked)
            {
                rank++;
                if (!bestRankPerSession.ContainsKey(chunk.SessionRowId))
                {
                    bestRankPerSession[chunk.SessionRowId] = rank;
                }

                if (!fused.TryGetValue(chunk.SessionRowId, out var entry))
                {
                    entry = new FusedScore();
                    fused[chunk.SessionRowId] = entry;
                }

                if (entry.ChunkRowIds.Count < 8)
                {
                    entry.ChunkRowIds.Add(chunk.ChunkRowId);
                }
            }

            foreach (var (sessionRowId, bestRank) in bestRankPerSession)
            {
                var contribution = weight / (k + bestRank);
                var entry = fused[sessionRowId];
                entry.Score += contribution;

                if (isLexical)
                {
                    entry.Lexical = contribution;
                }
                else
                {
                    entry.Semantic = contribution;
                }
            }
        }
    }

    private double ApplyBoosts(double score, SessionSummary session, DateTimeOffset now, string? currentWorkspace)
    {
        var boosted = score;

        if (config.Search.RecencyBoost > 0 && config.Search.RecencyHalfLifeDays > 0)
        {
            var ageDays = Math.Max((now - session.UpdatedAt).TotalDays, 0);
            var decay = Math.Pow(0.5, ageDays / config.Search.RecencyHalfLifeDays);
            boosted += score * config.Search.RecencyBoost * decay;
        }

        if (config.Search.WorkspaceBoost > 0
            && !string.IsNullOrWhiteSpace(currentWorkspace)
            && Text.TextUtilities.IsUnder(session.Workspace?.Path, currentWorkspace))
        {
            boosted += score * config.Search.WorkspaceBoost;
        }

        return boosted;
    }

    private static async Task<IReadOnlyList<SearchSnippet>> ReadSnippetsAsync(
        SqliteConnection connection,
        long sessionRowId,
        List<long> chunkRowIds,
        SearchQuery query,
        CancellationToken cancellationToken)
    {
        if (chunkRowIds.Count == 0)
        {
            return [];
        }

        var wanted = Math.Max(query.SnippetsPerSession, 1);
        var ids = chunkRowIds.Take(wanted).ToArray();
        var placeholders = string.Join(",", ids.Select((_, index) => $"$p{index}"));
        var match = Fts5Query.Build(query.Text);

        var snippets = new List<SearchSnippet>(ids.Length);

        await using var command = connection.CreateCommand();

        // Ask FTS5 for the highlighted excerpt when the query is lexical; fall back to the raw text
        // for a semantic-only hit, where there is no term to highlight.
        if (match.Length > 0)
        {
            command.CommandText = $"""
                SELECT c.role, c.turn_index, c.timestamp_utc, c.text,
                       snippet(chunk_fts, 0, '<<', '>>', '…', 20) AS excerpt
                FROM chunk c
                JOIN chunk_fts ON chunk_fts.rowid = c.rowid AND chunk_fts MATCH $match
                WHERE c.rowid IN ({placeholders});
                """;
            command.Parameters.AddWithValue("$match", match);
        }
        else
        {
            command.CommandText = $"""
                SELECT c.role, c.turn_index, c.timestamp_utc, c.text, NULL AS excerpt
                FROM chunk c
                WHERE c.rowid IN ({placeholders});
                """;
        }

        for (var index = 0; index < ids.Length; index++)
        {
            command.Parameters.AddWithValue($"$p{index}", ids[index]);
        }

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var highlighted = reader.GetNullableString(4);
                var text = highlighted is null
                    ? Text.TextUtilities.Preview(reader.GetString(3), 240)
                    : highlighted.Replace("<<", string.Empty, StringComparison.Ordinal).Replace(">>", string.Empty, StringComparison.Ordinal);

                snippets.Add(new SearchSnippet
                {
                    Role = Enum.TryParse<TurnRole>(reader.GetString(0), out var role) ? role : TurnRole.Assistant,
                    TurnIndex = reader.GetNullableInt32(1) ?? 0,
                    Timestamp = reader.GetNullableInt64(2) is { } epoch ? DateTimeOffset.FromUnixTimeMilliseconds(epoch) : null,
                    Text = text,
                    Highlighted = highlighted
                });
            }
        }
        catch (SqliteException)
        {
            return [];
        }

        return snippets;
    }

    private static async Task<Dictionary<long, SessionSummary>> ReadSessionsAsync(SqliteConnection connection, long[] rowIds, CancellationToken cancellationToken)
    {
        var sessions = new Dictionary<long, SessionSummary>();
        if (rowIds.Length == 0)
        {
            return sessions;
        }

        var placeholders = string.Join(",", rowIds.Select((_, index) => $"$p{index}"));

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT s.rowid, {SessionProjection} FROM session s WHERE s.rowid IN ({placeholders});";
        for (var index = 0; index < rowIds.Length; index++)
        {
            command.Parameters.AddWithValue($"$p{index}", rowIds[index]);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sessions[reader.GetInt64(0)] = MapSession(reader, offset: 1);
        }

        return sessions;
    }

    private async Task EnsureModelMatchesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var indexedModel = await RetraceIndexSchema.GetMetaAsync(connection, RetraceIndexSchema.EmbeddingModelKey, cancellationToken).ConfigureAwait(false);
        if (indexedModel is null)
        {
            return;
        }

        var runtimeModel = embeddingFactory.ModelId;
        if (!string.Equals(indexedModel, runtimeModel, StringComparison.OrdinalIgnoreCase))
        {
            throw new EmbeddingModelMismatchException(indexedModel, runtimeModel);
        }
    }

    private const string SessionProjection = """
        s.uri, s.source_id, s.native_id, s.title, s.preview,
               s.workspace_path, s.repository, s.branch, s.agent, s.models,
               s.created_utc, s.updated_utc, s.message_count, s.tool_call_count,
               s.total_tokens, s.cost, s.additions, s.deletions, s.files_changed,
               s.is_archived, s.parent_native_id, s.content_hash
        """;

    private static SessionSummary MapSession(SqliteDataReader reader, int offset = 0)
    {
        var models = reader.GetNullableString(offset + 9);

        return new SessionSummary
        {
            Ref = new SessionRef(reader.GetString(offset + 1), reader.GetString(offset + 2)),
            Title = reader.GetString(offset + 3),
            Preview = reader.GetNullableString(offset + 4),
            Workspace = new WorkspaceInfo
            {
                Path = reader.GetNullableString(offset + 5),
                Repository = reader.GetNullableString(offset + 6),
                Branch = reader.GetNullableString(offset + 7)
            },
            Agent = reader.GetNullableString(offset + 8),
            Models = string.IsNullOrWhiteSpace(models) ? [] : models.Split(',', StringSplitOptions.RemoveEmptyEntries),
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(offset + 10)),
            UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(offset + 11)),
            Stats = new SessionStats
            {
                MessageCount = reader.GetNullableInt32(offset + 12),
                ToolCallCount = reader.GetNullableInt32(offset + 13),
                TotalTokens = reader.GetNullableInt64(offset + 14),
                Cost = reader.GetNullableDouble(offset + 15),
                Additions = reader.GetNullableInt32(offset + 16),
                Deletions = reader.GetNullableInt32(offset + 17),
                FilesChanged = reader.GetNullableInt32(offset + 18)
            },
            IsArchived = reader.GetInt64(offset + 19) != 0,
            ParentNativeId = reader.GetNullableString(offset + 20),
            ContentHash = reader.GetString(offset + 21)
        };
    }

    private static (string Where, IReadOnlyList<(string Name, object Value)> Parameters) BuildFilterSql(SessionFilter filter, string alias)
    {
        var clauses = new List<string>();
        var parameters = new List<(string, object)>();

        if (filter.SourceIds.Count > 0)
        {
            var names = filter.SourceIds.Select((_, index) => $"$source{index}").ToArray();
            clauses.Add($"{alias}.source_id IN ({string.Join(",", names)})");
            for (var index = 0; index < filter.SourceIds.Count; index++)
            {
                parameters.Add(($"$source{index}", filter.SourceIds[index]));
            }
        }

        if (!filter.IncludeArchived)
        {
            clauses.Add($"{alias}.is_archived = 0");
        }

        if (filter.Since is { } since)
        {
            clauses.Add($"{alias}.updated_utc >= $since");
            parameters.Add(("$since", since.ToUnixTimeMilliseconds()));
        }

        if (filter.Until is { } until)
        {
            clauses.Add($"{alias}.updated_utc <= $until");
            parameters.Add(("$until", until.ToUnixTimeMilliseconds()));
        }

        if (!string.IsNullOrWhiteSpace(filter.WorkspacePath))
        {
            var normalized = filter.WorkspacePath.TrimEnd('/', '\\');
            clauses.Add($"""
                ({alias}.workspace_path = $workspace COLLATE NOCASE
                 OR substr({alias}.workspace_path, 1, length($workspace) + 1) IN ($workspaceSlash, $workspaceBackslash) COLLATE NOCASE)
                """);
            parameters.Add(("$workspace", normalized));
            parameters.Add(("$workspaceSlash", normalized + "/"));
            parameters.Add(("$workspaceBackslash", normalized + "\\"));
        }

        if (!string.IsNullOrWhiteSpace(filter.Repository))
        {
            clauses.Add($"({alias}.repository LIKE $repository OR {alias}.workspace_path LIKE $repository)");
            parameters.Add(("$repository", $"%{filter.Repository}%"));
        }

        if (!string.IsNullOrWhiteSpace(filter.Branch))
        {
            clauses.Add($"{alias}.branch = $branch COLLATE NOCASE");
            parameters.Add(("$branch", filter.Branch));
        }

        if (!string.IsNullOrWhiteSpace(filter.Agent))
        {
            clauses.Add($"{alias}.agent LIKE $agent");
            parameters.Add(("$agent", $"%{filter.Agent}%"));
        }

        if (!string.IsNullOrWhiteSpace(filter.Model))
        {
            clauses.Add($"{alias}.models LIKE $model");
            parameters.Add(("$model", $"%{filter.Model}%"));
        }

        if (filter.MinMessages is { } minimum)
        {
            clauses.Add($"COALESCE({alias}.message_count, 0) >= {minimum.ToString(CultureInfo.InvariantCulture)}");
        }

        foreach (var (tag, index) in filter.Tags.Select((tag, index) => (tag, index)))
        {
            clauses.Add($"EXISTS (SELECT 1 FROM tag t WHERE t.session_uri = {alias}.uri AND t.tag = $tag{index} COLLATE NOCASE)");
            parameters.Add(($"$tag{index}", tag));
        }

        return (clauses.Count == 0 ? string.Empty : "WHERE " + string.Join("\n  AND ", clauses), parameters);
    }

    private sealed record RankedChunk(long ChunkRowId, long SessionRowId, double Score);

    private sealed class FusedScore
    {
        public double Score { get; set; }

        public double Lexical { get; set; }

        public double Semantic { get; set; }

        public List<long> ChunkRowIds { get; } = [];
    }
}

/// <summary>Index size, freshness, and per-source state.</summary>
public sealed record IndexStatus
{
    /// <summary>Where the index lives.</summary>
    public required string Path { get; init; }

    /// <summary>Whether it has been built.</summary>
    public required bool Exists { get; init; }

    /// <summary>File size on disk.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Per-source state.</summary>
    public IReadOnlyList<SourceIndexState> Sources { get; init; } = [];

    /// <summary>Total sessions.</summary>
    public int SessionCount => Sources.Sum(source => source.SessionCount);

    /// <summary>Total passages.</summary>
    public int ChunkCount => Sources.Sum(source => source.ChunkCount);

    /// <summary>Total vectors.</summary>
    public int VectorCount { get; init; }

    /// <summary>Embedding model the vectors came from.</summary>
    public string? EmbeddingModel { get; init; }

    /// <summary>When the index was last refreshed.</summary>
    public DateTimeOffset? LastRefresh { get; init; }
}

/// <summary>One source's state inside the index.</summary>
public sealed record SourceIndexState
{
    /// <summary>Source id.</summary>
    public required string SourceId { get; init; }

    /// <summary>Opaque change cursor recorded at the last refresh.</summary>
    public string? Watermark { get; init; }

    /// <summary>When this source was last refreshed.</summary>
    public DateTimeOffset LastIndexed { get; init; }

    /// <summary>Indexed sessions.</summary>
    public int SessionCount { get; init; }

    /// <summary>Indexed passages.</summary>
    public int ChunkCount { get; init; }

    /// <summary>
    /// Indexed sessions that carry a vector.
    /// </summary>
    /// <remarks>
    /// Below <see cref="SessionCount"/> whenever sessions were indexed without an embedder, which
    /// is what an automatic refresh does. Those sessions are fully keyword-searchable and entirely
    /// absent from semantic results, a distinction nothing else in the status makes visible.
    /// </remarks>
    public int SessionsWithVectors { get; init; }

    /// <summary>
    /// Indexed sessions that hold at least one passage long enough to embed.
    /// </summary>
    /// <remarks>
    /// The honest denominator for vector coverage. A handful of sessions are nothing but a greeting
    /// and can never produce a vector, so measuring against <see cref="SessionCount"/> would report
    /// a shortfall that no amount of indexing could ever close.
    /// </remarks>
    public int SessionsEmbeddable { get; init; }
}
