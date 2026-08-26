using Microsoft.Extensions.Logging.Abstractions;
using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Core.Configuration;
using Zakira.Retrace.Core.Embeddings;
using Zakira.Retrace.Core.Index;
using Zakira.Retrace.Core.Search;
using Zakira.Retrace.Core.Services;

namespace Zakira.Retrace.Core.UnitTests;

/// <summary>
/// Covers the index end to end: chunking, incremental refresh, keyword and vector retrieval, and
/// fusion. A deterministic fake embedding provider stands in for ONNX so these run anywhere and
/// never download a model.
/// </summary>
public sealed class IndexTests
{
    // ---- fakes -----------------------------------------------------------------------------

    /// <summary>
    /// Produces stable vectors by hashing tokens into buckets.
    /// </summary>
    /// <remarks>
    /// This is a bag-of-words projection, not a semantic model, but it has the two properties the
    /// index actually depends on: identical text yields identical vectors, and texts sharing
    /// vocabulary score higher against each other than unrelated texts do.
    /// </remarks>
    private sealed class FakeEmbeddingProvider(string modelId = "fake-test-model", int dimensions = 64) : IEmbeddingProvider
    {
        public string ModelId { get; } = modelId;

        public int Dimensions { get; } = dimensions;

        public ValueTask<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, EmbeddingSide side, CancellationToken cancellationToken)
        {
            var results = new List<float[]>(texts.Count);

            foreach (var text in texts)
            {
                var vector = new float[Dimensions];
                foreach (var token in text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                {
                    var bucket = (int)((uint)token.GetHashCode(StringComparison.Ordinal) % (uint)Dimensions);
                    vector[bucket] += 1f;
                }

                VectorMath.NormalizeInPlace(vector);
                results.Add(vector);
            }

            return ValueTask.FromResult<IReadOnlyList<float[]>>(results);
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeEmbeddingFactory(string modelId = "fake-test-model", bool available = true) : IEmbeddingProviderFactory
    {
        public string ModelId { get; } = modelId;

        public bool IsAvailable { get; } = available;

        public Task<IEmbeddingProvider> CreateAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IEmbeddingProvider>(new FakeEmbeddingProvider(ModelId));
    }

    /// <summary>An in-memory source, so index tests do not depend on any harness.</summary>
    private sealed class FakeSource(string id, List<SessionTranscript> transcripts) : ISessionSource, IIncrementalSource
    {
        public string Id { get; } = id;

        public string DisplayName => $"Fake ({Id})";

        public SourceCapabilities Capabilities => SourceCapabilities.Incremental;

        public List<SessionTranscript> Transcripts { get; } = transcripts;

        public ValueTask<SourceAvailability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(SourceAvailability.Available("(memory)", Transcripts.Count));

        public async IAsyncEnumerable<SessionSummary> ListAsync(SessionFilter filter, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            foreach (var transcript in Transcripts)
            {
                yield return transcript.Summary;
            }
        }

        public ValueTask<SessionTranscript?> GetAsync(string nativeId, TranscriptOptions options, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Transcripts.FirstOrDefault(item => item.Summary.Ref.NativeId == nativeId));

        public ValueTask<ResumeCommand?> GetResumeCommandAsync(string nativeId, ResumeOptions options, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ResumeCommand?>(null);

        public ValueTask<string?> GetWatermarkAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<string?>(Transcripts.Count == 0
                ? null
                : Transcripts.Max(item => item.Summary.UpdatedAt).ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture));

        public async IAsyncEnumerable<SessionSummary> ListChangedSinceAsync(string? watermark, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            var cutoff = long.TryParse(watermark, out var epoch) ? DateTimeOffset.FromUnixTimeMilliseconds(epoch) : (DateTimeOffset?)null;

            foreach (var transcript in Transcripts)
            {
                if (cutoff is null || transcript.Summary.UpdatedAt > cutoff)
                {
                    yield return transcript.Summary;
                }
            }
        }

        public async IAsyncEnumerable<string> ListAllIdsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            foreach (var transcript in Transcripts)
            {
                yield return transcript.Summary.Ref.NativeId;
            }
        }
    }

    private static SessionTranscript MakeTranscript(
        string id,
        string title,
        string userText,
        string assistantText,
        DateTimeOffset? updated = null,
        string workspace = @"P:\Github\Fixture",
        string contentHash = "v1")
    {
        // Truncated to whole milliseconds because that is the precision every real harness stores
        // and every watermark round-trips through. Leaving sub-millisecond ticks here would make
        // the fixture re-enumerate on each refresh for a reason no real source exhibits.
        var raw = updated ?? DateTimeOffset.UtcNow.AddDays(-1);
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(raw.ToUnixTimeMilliseconds());

        return new SessionTranscript
        {
            Summary = new SessionSummary
            {
                Ref = new SessionRef("fake", id),
                Title = title,
                Preview = userText,
                Workspace = new WorkspaceInfo { Path = workspace },
                Agent = "build",
                Models = ["test-model"],
                CreatedAt = timestamp.AddMinutes(-10),
                UpdatedAt = timestamp,
                Stats = new SessionStats { MessageCount = 2 },
                ContentHash = contentHash
            },
            Turns =
            [
                new Turn { Index = 0, Role = TurnRole.User, Timestamp = timestamp, Blocks = [new TextBlock(userText)] },
                new Turn
                {
                    Index = 1,
                    Role = TurnRole.Assistant,
                    Timestamp = timestamp,
                    Blocks =
                    [
                        new TextBlock(assistantText),
                        new ToolCallBlock { ToolName = "bash", Title = "run tests", Output = "All tests passed. LONGTOOLTOKEN" }
                    ]
                }
            ],
            Files = [new TouchedFile { Path = Path.Combine(workspace, "Program.cs"), Tool = "edit" }],
            TotalTurns = 2
        };
    }

    private static (IndexBuilder Builder, IndexSearcher Searcher, RetraceConfig Config) CreateIndex(
        TempDirectory temp,
        List<SessionTranscript> transcripts,
        IEmbeddingProviderFactory? factory = null)
    {
        var config = new RetraceConfig();
        config.Index.Path = Path.Combine(temp.Path, "index.db");

        var paths = new RetracePaths();
        var source = new FakeSource("fake", transcripts);
        var embeddingFactory = factory ?? new FakeEmbeddingFactory();

        var builder = new IndexBuilder(paths, config, [source], embeddingFactory, NullLogger<IndexBuilder>.Instance);
        var searcher = new IndexSearcher(paths, config, embeddingFactory);

        return (builder, searcher, config);
    }

    // ---- chunking --------------------------------------------------------------------------

    [Fact]
    public void Chunker_marks_tool_output_as_searchable_but_not_embeddable()
    {
        var transcript = MakeTranscript("s1", "Title", "user question", "assistant answer");

        var chunks = new SessionChunker().Chunk(transcript).ToArray();

        var toolChunk = chunks.Single(chunk => chunk.Role == TurnRole.Tool);

        // Tool output is the bulk of every session store and embeds badly, so it is keyword-only.
        toolChunk.Text.Should().Contain("All tests passed");
        toolChunk.IsEmbeddable.Should().BeFalse();

        chunks.Where(chunk => chunk.Role is TurnRole.User or TurnRole.Assistant)
            .Should().AllSatisfy(chunk => chunk.IsEmbeddable.Should().BeTrue());
    }

    [Fact]
    public void Chunker_emits_a_header_chunk_so_a_session_is_findable_by_its_title()
    {
        var transcript = MakeTranscript("s1", "Distinctive session title", "user question", "assistant answer");

        var chunks = new SessionChunker().Chunk(transcript).ToArray();

        chunks[0].Role.Should().Be(TurnRole.System);
        chunks[0].Text.Should().Contain("Distinctive session title");
        chunks[0].IsEmbeddable.Should().BeTrue();
    }

    [Fact]
    public void Chunker_splits_long_text_with_overlap()
    {
        var longText = string.Join(' ', Enumerable.Range(0, 2000).Select(index => $"word{index}"));
        var transcript = MakeTranscript("s1", "Title", "short", longText);

        var policy = new ChunkPolicy { MaxCharacters = 400, OverlapCharacters = 80, MinCharacters = 16 };
        var chunks = new SessionChunker(policy).Chunk(transcript).ToArray();

        var assistantChunks = chunks.Where(chunk => chunk.Role == TurnRole.Assistant).ToArray();
        assistantChunks.Length.Should().BeGreaterThan(1);
        assistantChunks.Should().AllSatisfy(chunk => chunk.Text.Length.Should().BeLessThanOrEqualTo(500));
        chunks.Select(chunk => chunk.Ordinal).Should().OnlyHaveUniqueItems();
    }

    // ---- fts5 query building ---------------------------------------------------------------

    [Theory]
    [InlineData("hello world", "\"hello\"* OR \"world\"*")]
    [InlineData("the and of", "")]
    [InlineData("a", "")]
    public void Fts5_query_builds_a_prefix_or_expression_and_drops_noise(string input, string expected)
    {
        Fts5Query.Build(input).Should().Be(expected);
    }

    [Fact]
    public void Fts5_query_preserves_a_quoted_phrase_as_a_phrase()
    {
        // The one operator worth supporting, because every search box has it.
        Fts5Query.Build("\"exact phrase\" other").Should().Be("\"exact phrase\" OR \"other\"*");
    }

    [Theory]
    [InlineData("\"unbalanced")]
    [InlineData("NEAR(")]
    [InlineData("a AND")]
    [InlineData("-")]
    [InlineData("*")]
    [InlineData("^^^")]
    [InlineData("( ) \"")]
    [InlineData("foo\" OR \"bar")]
    public void Fts5_query_neutralises_every_metacharacter(string hostile)
    {
        var built = Fts5Query.Build(hostile);

        // Every token is quoted, so nothing the user types can be interpreted as FTS5 syntax.
        var unquoted = built.Replace("\"\"", string.Empty, StringComparison.Ordinal);
        unquoted.Count(character => character == '"').Should().Match(count => count % 2 == 0);
        built.Should().NotContain("NEAR(");
    }

    // ---- build and refresh -----------------------------------------------------------------

    [Fact]
    public async Task Build_indexes_sessions_chunks_vectors_and_files()
    {
        using var temp = new TempDirectory();
        var transcripts = new List<SessionTranscript>
        {
            MakeTranscript("s1", "Vector search design", "How do I add vector search?", "Use SQLite plus ONNX embeddings."),
            MakeTranscript("s2", "Retry policy", "Why does the retry loop hang?", "Because the backoff never resets.")
        };

        var (builder, searcher, _) = CreateIndex(temp, transcripts);

        var result = await builder.BuildAsync(new IndexBuildOptions(), TestContext.Current.CancellationToken);

        result.SessionsIndexed.Should().Be(2);
        result.ChunksIndexed.Should().BeGreaterThan(2);
        result.VectorsComputed.Should().BeGreaterThan(0);
        result.EmbeddingModel.Should().Be("fake-test-model");

        var status = await searcher.GetStatusAsync(TestContext.Current.CancellationToken);
        status.Exists.Should().BeTrue();
        status.SessionCount.Should().Be(2);
        status.VectorCount.Should().BeGreaterThan(0);

        var files = await searcher.GetFilesAsync("retrace://fake/s1", TestContext.Current.CancellationToken);
        files.Should().ContainSingle();
    }

    [Fact]
    public async Task Refresh_skips_sessions_whose_content_hash_is_unchanged()
    {
        using var temp = new TempDirectory();
        var transcripts = new List<SessionTranscript>
        {
            MakeTranscript("s1", "One", "question one", "answer one"),
            MakeTranscript("s2", "Two", "question two", "answer two")
        };

        var (builder, _, _) = CreateIndex(temp, transcripts);

        var first = await builder.BuildAsync(new IndexBuildOptions(), TestContext.Current.CancellationToken);
        first.SessionsIndexed.Should().Be(2);

        // Nothing changed: the second pass must do no work at all. This is the property that keeps
        // an auto-refresh before every query affordable.
        var second = await builder.BuildAsync(new IndexBuildOptions(), TestContext.Current.CancellationToken);
        second.SessionsIndexed.Should().Be(0);
    }

    [Fact]
    public async Task Refresh_reindexes_a_session_whose_content_hash_moved()
    {
        using var temp = new TempDirectory();
        var transcripts = new List<SessionTranscript>
        {
            MakeTranscript("s1", "One", "question one", "answer one", contentHash: "v1")
        };

        var (builder, searcher, _) = CreateIndex(temp, transcripts);
        await builder.BuildAsync(new IndexBuildOptions(), TestContext.Current.CancellationToken);

        transcripts[0] = MakeTranscript(
            "s1",
            "One updated",
            "question one",
            "answer one with a brandnewtoken",
            updated: DateTimeOffset.UtcNow,
            contentHash: "v2");

        var second = await builder.BuildAsync(new IndexBuildOptions(), TestContext.Current.CancellationToken);
        second.SessionsIndexed.Should().Be(1);

        var hits = await searcher.SearchAsync(
            new SearchQuery { Text = "brandnewtoken", Mode = SearchMode.Lexical, Top = 5 },
            TestContext.Current.CancellationToken);

        hits.Should().ContainSingle();
        hits[0].Session.Title.Should().Be("One updated");

        // Re-indexing replaces rather than appends: the old text must be gone from the FTS index.
        var stale = await searcher.SearchAsync(
            new SearchQuery { Text = "\"answer one\"", Mode = SearchMode.Lexical, Top = 5 },
            TestContext.Current.CancellationToken);

        stale.Should().ContainSingle("the session is still indexed once, not twice");
    }

    [Fact]
    public async Task Prune_removes_sessions_that_vanished_from_their_source()
    {
        using var temp = new TempDirectory();
        var transcripts = new List<SessionTranscript>
        {
            MakeTranscript("s1", "One", "question one", "answer one"),
            MakeTranscript("s2", "Two", "question two", "answer two")
        };

        var (builder, searcher, _) = CreateIndex(temp, transcripts);
        await builder.BuildAsync(new IndexBuildOptions(), TestContext.Current.CancellationToken);

        transcripts.RemoveAt(1);

        var result = await builder.BuildAsync(new IndexBuildOptions { Prune = true }, TestContext.Current.CancellationToken);

        result.SessionsRemoved.Should().Be(1);
        var remaining = await searcher.ListAsync(SessionFilter.All, TestContext.Current.CancellationToken);
        remaining.Should().ContainSingle();
    }

    // ---- scoped builds ---------------------------------------------------------------------

    [Fact]
    public async Task Since_indexes_only_sessions_inside_the_window()
    {
        using var temp = new TempDirectory();
        var transcripts = new List<SessionTranscript>
        {
            MakeTranscript("recent", "Recent", "question", "answer", DateTimeOffset.UtcNow.AddDays(-2)),
            MakeTranscript("old", "Old", "question", "answer", DateTimeOffset.UtcNow.AddDays(-200))
        };

        var (builder, searcher, _) = CreateIndex(temp, transcripts);

        var result = await builder.BuildAsync(
            new IndexBuildOptions { Since = DateTimeOffset.UtcNow.AddDays(-30) },
            TestContext.Current.CancellationToken);

        result.SessionsIndexed.Should().Be(1);

        var indexed = await searcher.ListAsync(SessionFilter.All, TestContext.Current.CancellationToken);
        indexed.Should().ContainSingle().Which.Ref.NativeId.Should().Be("recent");
    }

    [Fact]
    public async Task Until_bounds_the_other_end_of_the_window()
    {
        using var temp = new TempDirectory();
        var transcripts = new List<SessionTranscript>
        {
            MakeTranscript("recent", "Recent", "question", "answer", DateTimeOffset.UtcNow.AddDays(-2)),
            MakeTranscript("old", "Old", "question", "answer", DateTimeOffset.UtcNow.AddDays(-200))
        };

        var (builder, searcher, _) = CreateIndex(temp, transcripts);

        await builder.BuildAsync(
            new IndexBuildOptions { Until = DateTimeOffset.UtcNow.AddDays(-30) },
            TestContext.Current.CancellationToken);

        var indexed = await searcher.ListAsync(SessionFilter.All, TestContext.Current.CancellationToken);
        indexed.Should().ContainSingle().Which.Ref.NativeId.Should().Be("old");
    }

    [Fact]
    public async Task A_scoped_build_leaves_the_excluded_sessions_reachable_by_a_later_full_build()
    {
        using var temp = new TempDirectory();
        var transcripts = new List<SessionTranscript>
        {
            MakeTranscript("recent", "Recent", "question", "answer", DateTimeOffset.UtcNow.AddDays(-2)),
            MakeTranscript("old", "Old", "question", "answer", DateTimeOffset.UtcNow.AddDays(-200))
        };

        var (builder, searcher, _) = CreateIndex(temp, transcripts);

        var scoped = await builder.BuildAsync(
            new IndexBuildOptions { Since = DateTimeOffset.UtcNow.AddDays(-30) },
            TestContext.Current.CancellationToken);

        scoped.SessionsIndexed.Should().Be(1);

        // This is the invariant that makes --since safe. Had the scoped build advanced the
        // watermark to the source maximum, the incremental pass below would conclude that
        // everything older was already handled and the old session would be lost permanently.
        var full = await builder.BuildAsync(new IndexBuildOptions(), TestContext.Current.CancellationToken);

        full.SessionsIndexed.Should().Be(1, "the session excluded by the window is picked up now");
        full.SessionsSkipped.Should().Be(1, "the one already indexed is skipped on its content hash");

        var indexed = await searcher.ListAsync(SessionFilter.All, TestContext.Current.CancellationToken);
        indexed.Select(session => session.Ref.NativeId).Should().BeEquivalentTo("recent", "old");
    }

    [Fact]
    public async Task An_unscoped_build_does_advance_the_watermark()
    {
        using var temp = new TempDirectory();
        var transcripts = new List<SessionTranscript>
        {
            MakeTranscript("s1", "One", "question", "answer")
        };

        var (builder, _, _) = CreateIndex(temp, transcripts);

        await builder.BuildAsync(new IndexBuildOptions(), TestContext.Current.CancellationToken);

        // The counterpart to the test above: without scoping, a second pass must do no work,
        // which only happens if the watermark moved.
        var second = await builder.BuildAsync(new IndexBuildOptions(), TestContext.Current.CancellationToken);

        second.SessionsIndexed.Should().Be(0);
        second.SessionsSkipped.Should().Be(0, "the watermark alone should exclude it, before any hash check");
    }

    [Fact]
    public async Task Since_alone_does_not_re_embed_an_already_indexed_session_but_force_does()
    {
        using var temp = new TempDirectory();

        // Long enough to clear the 24-character floor below which a passage is not worth a vector.
        var transcripts = new List<SessionTranscript>
        {
            MakeTranscript(
                "s1",
                "Retry policy investigation",
                "Why does the retry loop keep timing out under load?",
                "The backoff never resets between attempts, so the delay grows without bound.",
                DateTimeOffset.UtcNow.AddDays(-2))
        };

        var (builder, _, config) = CreateIndex(temp, transcripts);

        // Keyword-only first, so the session exists in the index with no vectors.
        var keywordOnly = await builder.BuildAsync(new IndexBuildOptions { Embed = false }, TestContext.Current.CancellationToken);
        keywordOnly.VectorsComputed.Should().Be(0);

        var window = new IndexBuildOptions { Since = DateTimeOffset.UtcNow.AddDays(-30) };

        // Scoping alone adds nothing. On an already-refreshed index the watermark short-circuits
        // before the window is even considered; had it not, the unchanged content hash would skip
        // the session anyway. Either way no vectors appear, which is precisely why --force exists
        // and why the CLI help spells the combination out.
        var withoutForce = await builder.BuildAsync(window, TestContext.Current.CancellationToken);
        withoutForce.SessionsIndexed.Should().Be(0);
        withoutForce.VectorsComputed.Should().Be(0);

        var withForce = await builder.BuildAsync(window with { Force = true }, TestContext.Current.CancellationToken);
        withForce.SessionsIndexed.Should().Be(1);
        withForce.VectorsComputed.Should().BePositive();

        var searcher = new IndexSearcher(new RetracePaths(), config, new FakeEmbeddingFactory());
        var status = await searcher.GetStatusAsync(TestContext.Current.CancellationToken);
        status.VectorCount.Should().BePositive();
    }

    // ---- vector backfill -------------------------------------------------------------------

    [Fact]
    public async Task A_keyword_only_session_is_invisible_to_a_normal_refresh()
    {
        using var temp = new TempDirectory();
        var transcripts = new List<SessionTranscript>
        {
            MakeTranscript("s1", "Retry policy", "why does the retry loop time out", "the backoff never resets between attempts", DateTimeOffset.UtcNow.AddDays(-2))
        };

        var (builder, searcher, _) = CreateIndex(temp, transcripts);

        await builder.BuildAsync(new IndexBuildOptions { Embed = false }, TestContext.Current.CancellationToken);

        // This is the trap the backfill flag exists for, pinned so it cannot quietly return. The
        // content hash answers "did the conversation change?", and the answer is no, so a refresh
        // that is perfectly willing to embed still walks straight past a session that has no
        // vectors at all. Left alone, every session an automatic refresh picks up stays absent
        // from semantic search forever.
        var refreshed = await builder.BuildAsync(new IndexBuildOptions(), TestContext.Current.CancellationToken);

        refreshed.SessionsIndexed.Should().Be(0);
        refreshed.VectorsComputed.Should().Be(0);

        var status = await searcher.GetStatusAsync(TestContext.Current.CancellationToken);
        status.VectorCount.Should().Be(0);
        status.Sources.Single().SessionsWithVectors.Should().Be(0);
    }

    [Fact]
    public async Task Backfill_adds_vectors_to_a_session_that_was_indexed_without_them()
    {
        using var temp = new TempDirectory();
        var transcripts = new List<SessionTranscript>
        {
            MakeTranscript("s1", "Retry policy", "why does the retry loop time out", "the backoff never resets between attempts", DateTimeOffset.UtcNow.AddDays(-2))
        };

        var (builder, searcher, _) = CreateIndex(temp, transcripts);

        await builder.BuildAsync(new IndexBuildOptions { Embed = false }, TestContext.Current.CancellationToken);

        var backfilled = await builder.BuildAsync(new IndexBuildOptions { Backfill = true }, TestContext.Current.CancellationToken);

        backfilled.SessionsIndexed.Should().Be(1);
        backfilled.VectorsComputed.Should().BePositive();

        var status = await searcher.GetStatusAsync(TestContext.Current.CancellationToken);
        status.Sources.Single().SessionsWithVectors.Should().Be(1);
    }

    [Fact]
    public async Task Backfill_leaves_already_embedded_sessions_alone()
    {
        using var temp = new TempDirectory();
        var transcripts = new List<SessionTranscript>
        {
            MakeTranscript("done", "Already embedded", "a question about retry policy", "a detailed answer about backoff", DateTimeOffset.UtcNow.AddDays(-2))
        };

        var (builder, _, _) = CreateIndex(temp, transcripts);

        await builder.BuildAsync(new IndexBuildOptions(), TestContext.Current.CancellationToken);

        transcripts.Add(MakeTranscript("missing", "Keyword only", "a question about caching layers", "a detailed answer about eviction", DateTimeOffset.UtcNow.AddDays(-1)));
        await builder.BuildAsync(new IndexBuildOptions { Embed = false }, TestContext.Current.CancellationToken);

        var backfilled = await builder.BuildAsync(new IndexBuildOptions { Backfill = true }, TestContext.Current.CancellationToken);

        // Cost proportional to the gap, not to the source. This is the whole difference between
        // --backfill and --force, and it is what makes the flag usable on a large store.
        backfilled.SessionsIndexed.Should().Be(1);
        backfilled.SessionsSkipped.Should().Be(1);
    }

    [Fact]
    public async Task Backfill_without_an_embedder_is_a_no_op_rather_than_a_failure()
    {
        using var temp = new TempDirectory();
        var transcripts = new List<SessionTranscript>
        {
            MakeTranscript("s1", "Retry policy", "why does the retry loop time out", "the backoff never resets between attempts", DateTimeOffset.UtcNow.AddDays(-2))
        };

        var (builder, _, _) = CreateIndex(temp, transcripts);

        await builder.BuildAsync(new IndexBuildOptions { Embed = false }, TestContext.Current.CancellationToken);

        // Asking to backfill vectors with embedding turned off is contradictory, but it is the
        // kind of contradiction a script arrives at by combining flags rather than a mistake worth
        // aborting over. Doing nothing quietly beats re-reading every transcript to produce
        // nothing, which is what a naive reading of the flag would cause.
        var result = await builder.BuildAsync(
            new IndexBuildOptions { Backfill = true, Embed = false },
            TestContext.Current.CancellationToken);

        result.SessionsIndexed.Should().Be(0);
        result.VectorsComputed.Should().Be(0);
    }

    [Fact]
    public async Task Backfill_reaches_sessions_that_sit_behind_the_watermark()
    {
        using var temp = new TempDirectory();
        var transcripts = new List<SessionTranscript>
        {
            MakeTranscript("s1", "Retry policy", "why does the retry loop time out", "the backoff never resets between attempts", DateTimeOffset.UtcNow.AddDays(-2))
        };

        var (builder, searcher, _) = CreateIndex(temp, transcripts);

        // A keyword-only build advances the watermark past the very sessions it left unembedded,
        // so ListChangedSinceAsync will never offer them again. Without bypassing the watermark for
        // candidate selection the flag would appear to work and silently do nothing at all.
        await builder.BuildAsync(new IndexBuildOptions { Embed = false }, TestContext.Current.CancellationToken);

        var before = await searcher.GetStatusAsync(TestContext.Current.CancellationToken);
        before.Sources.Single().Watermark.Should().NotBeNullOrWhiteSpace();

        var backfilled = await builder.BuildAsync(new IndexBuildOptions { Backfill = true }, TestContext.Current.CancellationToken);

        backfilled.VectorsComputed.Should().BePositive();
    }

    [Fact]
    public async Task Backfill_does_not_chase_a_session_that_has_nothing_worth_embedding()
    {
        using var temp = new TempDirectory();

        // Every passage here is shorter than the embedding threshold, so this session will never
        // produce a vector however often it is processed.
        var transcripts = new List<SessionTranscript>
        {
            MakeTranscript("tiny", "Hi", "hello", "hi there", DateTimeOffset.UtcNow.AddDays(-2))
        };

        var (builder, searcher, _) = CreateIndex(temp, transcripts);

        await builder.BuildAsync(new IndexBuildOptions(), TestContext.Current.CancellationToken);

        var status = await searcher.GetStatusAsync(TestContext.Current.CancellationToken);
        status.Sources.Single().SessionsWithVectors.Should().Be(0);

        // Judging purely by "has no vector" would re-read this session on every backfill for the
        // life of the index, and leave the reported coverage permanently short of complete for a
        // reason no amount of work could fix.
        var first = await builder.BuildAsync(new IndexBuildOptions { Backfill = true }, TestContext.Current.CancellationToken);
        var second = await builder.BuildAsync(new IndexBuildOptions { Backfill = true }, TestContext.Current.CancellationToken);

        first.SessionsIndexed.Should().Be(0);
        second.SessionsIndexed.Should().Be(0);
        second.Sources.Single().SessionsSkipped.Should().Be(1);
    }

    [Fact]
    public async Task A_forced_scoped_build_preserves_the_watermark_rather_than_erasing_it()
    {
        using var temp = new TempDirectory();
        var transcripts = new List<SessionTranscript>
        {
            MakeTranscript("recent", "Recent", "a question about retry policy", "a detailed answer about backoff", DateTimeOffset.UtcNow.AddDays(-2)),
            MakeTranscript("old", "Old", "a question about retry policy", "a detailed answer about backoff", DateTimeOffset.UtcNow.AddDays(-200))
        };

        var (builder, searcher, _) = CreateIndex(temp, transcripts);

        await builder.BuildAsync(new IndexBuildOptions(), TestContext.Current.CancellationToken);

        var before = await searcher.GetStatusAsync(TestContext.Current.CancellationToken);
        var watermarkBefore = before.Sources.Single().Watermark;
        watermarkBefore.Should().NotBeNullOrWhiteSpace();

        // --force bypasses the watermark for candidate selection, but that must not be confused
        // with discarding it. Erasing it here would push the next refresh into a needless full
        // re-enumeration of the whole source.
        await builder.BuildAsync(
            new IndexBuildOptions { Force = true, Since = DateTimeOffset.UtcNow.AddDays(-30) },
            TestContext.Current.CancellationToken);

        var after = await searcher.GetStatusAsync(TestContext.Current.CancellationToken);
        after.Sources.Single().Watermark.Should().Be(watermarkBefore);
    }

    // ---- automatic refresh -----------------------------------------------------------------

    /// <summary>A source that takes its time producing each transcript, to exercise the deadline.</summary>
    private sealed class SlowSource(string id, List<SessionTranscript> transcripts, TimeSpan delay)
        : ISessionSource, IIncrementalSource
    {
        private readonly FakeSource inner = new(id, transcripts);

        public int GetCalls;

        public string Id => inner.Id;

        public string DisplayName => inner.DisplayName;

        public SourceCapabilities Capabilities => inner.Capabilities;

        public ValueTask<SourceAvailability> ProbeAsync(CancellationToken cancellationToken) =>
            inner.ProbeAsync(cancellationToken);

        public IAsyncEnumerable<SessionSummary> ListAsync(SessionFilter filter, CancellationToken cancellationToken) =>
            inner.ListAsync(filter, cancellationToken);

        public async ValueTask<SessionTranscript?> GetAsync(string nativeId, TranscriptOptions options, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref GetCalls);
            await Task.Delay(delay, cancellationToken);
            return await inner.GetAsync(nativeId, options, cancellationToken);
        }

        public ValueTask<ResumeCommand?> GetResumeCommandAsync(string nativeId, ResumeOptions options, CancellationToken cancellationToken) =>
            inner.GetResumeCommandAsync(nativeId, options, cancellationToken);

        public ValueTask<string?> GetWatermarkAsync(CancellationToken cancellationToken) =>
            inner.GetWatermarkAsync(cancellationToken);

        public IAsyncEnumerable<SessionSummary> ListChangedSinceAsync(string? watermark, CancellationToken cancellationToken) =>
            inner.ListChangedSinceAsync(watermark, cancellationToken);

        public IAsyncEnumerable<string> ListAllIdsAsync(CancellationToken cancellationToken) =>
            inner.ListAllIdsAsync(cancellationToken);
    }

    /// <summary>An embedding factory that records whether anything asked it for a provider.</summary>
    private sealed class CountingEmbeddingFactory : IEmbeddingProviderFactory
    {
        public int Creations;

        public string ModelId => "fake-test-model";

        public bool IsAvailable => true;

        public Task<IEmbeddingProvider> CreateAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Creations);
            return Task.FromResult<IEmbeddingProvider>(new FakeEmbeddingProvider(ModelId));
        }
    }

    private static (SessionCatalog Catalog, SlowSource Source, RetraceConfig Config, IndexBuilder Builder) CreateCatalog(
        TempDirectory temp,
        List<SessionTranscript> transcripts,
        TimeSpan delay,
        IEmbeddingProviderFactory? factory = null)
    {
        var config = new RetraceConfig();
        config.Index.Path = Path.Combine(temp.Path, "index.db");

        // The cooldown exists to stop a burst of queries re-probing every source; it would
        // otherwise suppress the very refresh these tests are about.
        config.Index.AutoRefreshMinIntervalMinutes = 0;

        var paths = new RetracePaths();
        var source = new SlowSource("fake", transcripts, delay);
        var embeddingFactory = factory ?? new FakeEmbeddingFactory();

        var builder = new IndexBuilder(paths, config, [source], embeddingFactory, NullLogger<IndexBuilder>.Instance);
        var searcher = new IndexSearcher(paths, config, embeddingFactory);
        var catalog = new SessionCatalog(config, [source], searcher, builder, NullLogger<SessionCatalog>.Instance);

        return (catalog, source, config, builder);
    }

    [Fact]
    public async Task Automatic_refresh_does_not_embed()
    {
        using var temp = new TempDirectory();
        var transcripts = new List<SessionTranscript>
        {
            MakeTranscript("s1", "First", "a question about retry policy", "a detailed answer about backoff", DateTimeOffset.UtcNow.AddDays(-2))
        };

        // Listing goes through the same refresh path as search but never embeds a query of its
        // own, so any model load here could only have come from the refresh.
        var factory = new CountingEmbeddingFactory();
        var (catalog, source, _, builder) = CreateCatalog(temp, transcripts, TimeSpan.Zero, factory);

        await builder.BuildAsync(new IndexBuildOptions { Embed = false }, TestContext.Current.CancellationToken);

        transcripts.Add(MakeTranscript("s2", "Second", "a question about caching layers", "a detailed answer about eviction", DateTimeOffset.UtcNow));

        var sessions = await catalog.ListAsync(
            new SessionFilter(),
            QueryMode.Indexed,
            TestContext.Current.CancellationToken);

        // Inference is the dominant cost of indexing; running it in front of an interactive query
        // is what made search hang for minutes. The new session still had to become visible.
        factory.Creations.Should().Be(0);
        sessions.Should().HaveCount(2);
        source.GetCalls.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Automatic_refresh_gives_up_at_its_deadline_and_still_answers_the_query()
    {
        using var temp = new TempDirectory();
        var transcripts = new List<SessionTranscript>
        {
            MakeTranscript("seed", "Seed", "a question about retry policy", "a detailed answer about backoff", DateTimeOffset.UtcNow.AddDays(-2))
        };

        var (catalog, _, config, builder) = CreateCatalog(temp, transcripts, TimeSpan.FromMilliseconds(300));
        config.Index.AutoRefreshMaxSeconds = 1;

        await builder.BuildAsync(new IndexBuildOptions { Embed = false }, TestContext.Current.CancellationToken);

        // Far more outstanding work than the budget allows.
        for (var i = 0; i < 40; i++)
        {
            transcripts.Add(MakeTranscript(
                $"new{i}",
                $"New {i}",
                "a question about caching layers",
                "a detailed answer about eviction",
                DateTimeOffset.UtcNow));
        }

        var started = System.Diagnostics.Stopwatch.StartNew();
        var hits = await catalog.SearchAsync(
            new SearchQuery { Text = "backoff" },
            QueryMode.Indexed,
            TestContext.Current.CancellationToken);
        started.Stop();

        // The whole point: the query is bounded by the budget, not by how far behind the index is.
        started.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(8));
        hits.Should().NotBeEmpty();
        catalog.PendingSources.Should().Contain("fake");
    }

    [Fact]
    public async Task A_refresh_that_runs_out_of_time_still_stamps_the_cooldown()
    {
        using var temp = new TempDirectory();
        var transcripts = new List<SessionTranscript>
        {
            MakeTranscript("seed", "Seed", "a question about retry policy", "a detailed answer about backoff", DateTimeOffset.UtcNow.AddDays(-2))
        };

        var (catalog, _, config, builder) = CreateCatalog(temp, transcripts, TimeSpan.FromMilliseconds(300));
        config.Index.AutoRefreshMaxSeconds = 1;

        await builder.BuildAsync(new IndexBuildOptions { Embed = false }, TestContext.Current.CancellationToken);

        for (var i = 0; i < 40; i++)
        {
            transcripts.Add(MakeTranscript(
                $"new{i}",
                $"New {i}",
                "a question about caching layers",
                "a detailed answer about eviction",
                DateTimeOffset.UtcNow));
        }

        var before = DateTimeOffset.UtcNow;
        await catalog.SearchAsync(new SearchQuery { Text = "backoff" }, QueryMode.Indexed, TestContext.Current.CancellationToken);

        // Recording the timestamp only on success left the cooldown disengaged after an
        // interrupted refresh, so every subsequent query restarted the same doomed work.
        var searcher = new IndexSearcher(new RetracePaths(), config, new FakeEmbeddingFactory());
        var status = await searcher.GetStatusAsync(TestContext.Current.CancellationToken);

        status.LastRefresh.Should().NotBeNull();
        status.LastRefresh!.Value.Should().BeOnOrAfter(before.AddSeconds(-1));
    }

    [Fact]
    public async Task A_genuine_cancellation_is_not_mistaken_for_the_refresh_budget_expiring()
    {
        using var temp = new TempDirectory();
        var transcripts = new List<SessionTranscript>
        {
            MakeTranscript("seed", "Seed", "a question about retry policy", "a detailed answer about backoff", DateTimeOffset.UtcNow.AddDays(-2))
        };

        var (catalog, _, config, builder) = CreateCatalog(temp, transcripts, TimeSpan.FromMilliseconds(200));
        config.Index.AutoRefreshMaxSeconds = 60;

        await builder.BuildAsync(new IndexBuildOptions { Embed = false }, TestContext.Current.CancellationToken);

        for (var i = 0; i < 40; i++)
        {
            transcripts.Add(MakeTranscript(
                $"new{i}",
                $"New {i}",
                "a question about caching layers",
                "a detailed answer about eviction",
                DateTimeOffset.UtcNow));
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        // Swallowing this would mean Ctrl-C silently turned into "carry on with stale results".
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => catalog.SearchAsync(new SearchQuery { Text = "backoff" }, QueryMode.Indexed, cts.Token));
    }

    [Fact]
    public async Task Scoping_is_reported_so_a_partial_index_is_not_mistaken_for_a_complete_one()
    {
        new IndexBuildOptions().IsScoped.Should().BeFalse();
        new IndexBuildOptions { Since = DateTimeOffset.UtcNow }.IsScoped.Should().BeTrue();
        new IndexBuildOptions { Until = DateTimeOffset.UtcNow }.IsScoped.Should().BeTrue();

        await Task.CompletedTask;
    }

    // ---- search ----------------------------------------------------------------------------

    [Fact]
    public async Task Lexical_search_finds_matching_content_and_returns_snippets()
    {
        using var temp = new TempDirectory();
        var transcripts = new List<SessionTranscript>
        {
            MakeTranscript("s1", "Vector search", "How do I add vector search?", "Use SQLite FTS5 and ONNX embeddings."),
            MakeTranscript("s2", "Retry policy", "Why does the retry loop hang?", "Because the backoff never resets.")
        };

        var (builder, searcher, _) = CreateIndex(temp, transcripts);
        await builder.BuildAsync(new IndexBuildOptions(), TestContext.Current.CancellationToken);

        var hits = await searcher.SearchAsync(
            new SearchQuery { Text = "backoff", Mode = SearchMode.Lexical, Top = 5, SnippetsPerSession = 2 },
            TestContext.Current.CancellationToken);

        hits.Should().ContainSingle();
        hits[0].Session.Ref.NativeId.Should().Be("s2");
        hits[0].Snippets.Should().NotBeEmpty();
        hits[0].Snippets[0].Highlighted.Should().Contain("<<");
    }

    [Fact]
    public async Task Search_finds_tool_output_by_keyword_even_though_it_is_never_embedded()
    {
        using var temp = new TempDirectory();
        var transcripts = new List<SessionTranscript>
        {
            MakeTranscript("s1", "Build session", "run the tests", "Done.")
        };

        var (builder, searcher, _) = CreateIndex(temp, transcripts);
        await builder.BuildAsync(new IndexBuildOptions(), TestContext.Current.CancellationToken);

        // "which session ran that command" is a real question, and it can only be answered from
        // tool output, which is why tool text is keyword-indexed despite not being embedded.
        var hits = await searcher.SearchAsync(
            new SearchQuery { Text = "LONGTOOLTOKEN", Mode = SearchMode.Lexical, Top = 5 },
            TestContext.Current.CancellationToken);

        hits.Should().ContainSingle();
    }

    [Fact]
    public async Task Hybrid_search_returns_results_a_keyword_query_alone_would_miss()
    {
        using var temp = new TempDirectory();
        var transcripts = new List<SessionTranscript>
        {
            MakeTranscript("s1", "Alpha", "aaa bbb ccc", "ddd eee fff"),
            MakeTranscript("s2", "Beta", "ggg hhh iii", "jjj kkk lll")
        };

        var (builder, searcher, _) = CreateIndex(temp, transcripts);
        await builder.BuildAsync(new IndexBuildOptions(), TestContext.Current.CancellationToken);

        var lexical = await searcher.SearchAsync(
            new SearchQuery { Text = "aaa ddd", Mode = SearchMode.Lexical, Top = 5 },
            TestContext.Current.CancellationToken);

        var hybrid = await searcher.SearchAsync(
            new SearchQuery { Text = "aaa ddd", Mode = SearchMode.Hybrid, Top = 5 },
            TestContext.Current.CancellationToken);

        lexical.Should().NotBeEmpty();
        hybrid.Should().NotBeEmpty();
        hybrid[0].Session.Ref.NativeId.Should().Be("s1");
        hybrid[0].SemanticScore.Should().BeGreaterThan(0, "hybrid must actually consult the vector signal");
    }

    [Fact]
    public async Task Hybrid_degrades_to_lexical_when_no_embedding_model_is_installed()
    {
        using var temp = new TempDirectory();
        var transcripts = new List<SessionTranscript>
        {
            MakeTranscript("s1", "Alpha", "distinctivetoken here", "answer")
        };

        // Index with vectors, then query on a machine where the model is unavailable.
        var (builder, _, config) = CreateIndex(temp, transcripts);
        await builder.BuildAsync(new IndexBuildOptions(), TestContext.Current.CancellationToken);

        var offlineSearcher = new IndexSearcher(new RetracePaths(), config, new FakeEmbeddingFactory(available: false));

        var hits = await offlineSearcher.SearchAsync(
            new SearchQuery { Text = "distinctivetoken", Mode = SearchMode.Hybrid, Top = 5 },
            TestContext.Current.CancellationToken);

        // Failing here would make an optional dependency mandatory in practice.
        hits.Should().ContainSingle();
        hits[0].SemanticScore.Should().Be(0);
    }

    [Fact]
    public async Task Querying_with_a_different_embedding_model_fails_loudly()
    {
        using var temp = new TempDirectory();
        var transcripts = new List<SessionTranscript> { MakeTranscript("s1", "Alpha", "question", "answer") };

        var (builder, _, config) = CreateIndex(temp, transcripts, new FakeEmbeddingFactory("model-a"));
        await builder.BuildAsync(new IndexBuildOptions(), TestContext.Current.CancellationToken);

        var mismatched = new IndexSearcher(new RetracePaths(), config, new FakeEmbeddingFactory("model-b"));

        var act = async () => await mismatched.SearchAsync(
            new SearchQuery { Text = "question", Mode = SearchMode.Semantic, Top = 5 },
            TestContext.Current.CancellationToken);

        // Vectors from different models are not comparable; silently returning nonsense would be
        // far worse than an error the user can act on.
        await act.Should().ThrowAsync<EmbeddingModelMismatchException>();
    }

    [Fact]
    public async Task Force_rebuild_recovers_from_an_embedding_model_change()
    {
        using var temp = new TempDirectory();
        var transcripts = new List<SessionTranscript> { MakeTranscript("s1", "Alpha", "question", "answer") };

        var (builder, _, config) = CreateIndex(temp, transcripts, new FakeEmbeddingFactory("model-a"));
        await builder.BuildAsync(new IndexBuildOptions(), TestContext.Current.CancellationToken);

        var newBuilder = new IndexBuilder(
            new RetracePaths(),
            config,
            [new FakeSource("fake", transcripts)],
            new FakeEmbeddingFactory("model-b"),
            NullLogger<IndexBuilder>.Instance);

        var withoutForce = async () => await newBuilder.BuildAsync(new IndexBuildOptions(), TestContext.Current.CancellationToken);
        await withoutForce.Should().ThrowAsync<EmbeddingModelMismatchException>();

        var result = await newBuilder.BuildAsync(new IndexBuildOptions { Force = true }, TestContext.Current.CancellationToken);
        result.EmbeddingModel.Should().Be("model-b");
    }

    // ---- filtering and resolution ----------------------------------------------------------

    [Fact]
    public async Task Filters_narrow_results_before_ranking()
    {
        using var temp = new TempDirectory();
        var recent = DateTimeOffset.UtcNow.AddHours(-1);
        var old = DateTimeOffset.UtcNow.AddDays(-90);

        var transcripts = new List<SessionTranscript>
        {
            MakeTranscript("s1", "Recent", "shared token", "answer", recent, @"P:\Github\Alpha"),
            MakeTranscript("s2", "Old", "shared token", "answer", old, @"P:\Github\Beta")
        };

        var (builder, searcher, _) = CreateIndex(temp, transcripts);
        await builder.BuildAsync(new IndexBuildOptions(), TestContext.Current.CancellationToken);

        var byWorkspace = await searcher.SearchAsync(
            new SearchQuery
            {
                Text = "shared",
                Mode = SearchMode.Lexical,
                Filter = SessionFilter.All with { WorkspacePath = @"P:\Github\Alpha" }
            },
            TestContext.Current.CancellationToken);

        var bySince = await searcher.SearchAsync(
            new SearchQuery
            {
                Text = "shared",
                Mode = SearchMode.Lexical,
                Filter = SessionFilter.All with { Since = DateTimeOffset.UtcNow.AddDays(-7) }
            },
            TestContext.Current.CancellationToken);

        byWorkspace.Should().ContainSingle().Which.Session.Ref.NativeId.Should().Be("s1");
        bySince.Should().ContainSingle().Which.Session.Ref.NativeId.Should().Be("s1");
    }

    [Fact]
    public async Task Workspace_filter_matches_subdirectories_but_not_sibling_prefixes()
    {
        using var temp = new TempDirectory();
        var transcripts = new List<SessionTranscript>
        {
            MakeTranscript("s1", "Nested", "token", "answer", workspace: @"P:\Github\Alpha\src"),
            // A sibling whose path shares a prefix with the filter must not match; naive
            // StartsWith comparison gets this wrong.
            MakeTranscript("s2", "Sibling", "token", "answer", workspace: @"P:\Github\AlphaBeta")
        };

        var (builder, searcher, _) = CreateIndex(temp, transcripts);
        await builder.BuildAsync(new IndexBuildOptions(), TestContext.Current.CancellationToken);

        var results = await searcher.ListAsync(
            SessionFilter.All with { WorkspacePath = @"P:\Github\Alpha" },
            TestContext.Current.CancellationToken);

        results.Should().ContainSingle().Which.Ref.NativeId.Should().Be("s1");
    }

    [Fact]
    public async Task Resolve_accepts_a_uri_a_native_id_and_a_unique_prefix()
    {
        using var temp = new TempDirectory();
        var transcripts = new List<SessionTranscript>
        {
            MakeTranscript("abcdef123456", "One", "question", "answer"),
            MakeTranscript("zzzzzz999999", "Two", "question", "answer")
        };

        var (builder, searcher, _) = CreateIndex(temp, transcripts);
        await builder.BuildAsync(new IndexBuildOptions(), TestContext.Current.CancellationToken);

        (await searcher.ResolveAsync("retrace://fake/abcdef123456", TestContext.Current.CancellationToken))!
            .Title.Should().Be("One");

        (await searcher.ResolveAsync("abcdef123456", TestContext.Current.CancellationToken))!
            .Title.Should().Be("One");

        (await searcher.ResolveAsync("abcdef", TestContext.Current.CancellationToken))!
            .Title.Should().Be("One");

        (await searcher.ResolveAsync("fake/abcdef", TestContext.Current.CancellationToken))!
            .Title.Should().Be("One");
    }

    [Fact]
    public async Task Resolve_reports_ambiguity_rather_than_guessing()
    {
        using var temp = new TempDirectory();
        var transcripts = new List<SessionTranscript>
        {
            MakeTranscript("prefix_aaa", "One", "question", "answer"),
            MakeTranscript("prefix_bbb", "Two", "question", "answer")
        };

        var (builder, searcher, _) = CreateIndex(temp, transcripts);
        await builder.BuildAsync(new IndexBuildOptions(), TestContext.Current.CancellationToken);

        var act = async () => await searcher.ResolveAsync("prefix", TestContext.Current.CancellationToken);

        var exception = await act.Should().ThrowAsync<SessionNotFoundException>();
        exception.Which.Candidates.Should().HaveCount(2);
    }

    // ---- vector math -----------------------------------------------------------------------

    [Fact]
    public void Vector_serialisation_round_trips_exactly()
    {
        var vector = new float[] { 0.5f, -0.25f, 1f, 0f, 3.14159f };

        var restored = VectorMath.Deserialize(VectorMath.Serialize(vector));

        restored.Should().BeEquivalentTo(vector);
    }

    [Fact]
    public async Task Cosine_similarity_orders_related_text_above_unrelated_text()
    {
        using var provider = new FakeEmbeddingProvider();

        var vectors = await provider.EmbedAsync(
            ["sqlite vector search index", "sqlite vector search database", "unrelated cooking recipe"],
            EmbeddingSide.Document,
            TestContext.Current.CancellationToken);

        var related = VectorMath.CosineSimilarity(vectors[0], vectors[1]);
        var unrelated = VectorMath.CosineSimilarity(vectors[0], vectors[2]);

        related.Should().BeGreaterThan(unrelated);
    }

    [Fact]
    public void Centroid_of_identical_vectors_is_that_vector()
    {
        var vector = new float[] { 0.6f, 0.8f, 0f, 0f };

        var centroid = VectorMath.Centroid([vector, vector, vector]);

        VectorMath.CosineSimilarity(centroid, vector).Should().BeApproximately(1f, 0.0001f);
    }

    [Fact]
    public void Deserialize_rejects_a_truncated_blob_instead_of_producing_garbage()
    {
        VectorMath.Deserialize([1, 2, 3]).Should().BeEmpty();
        VectorMath.Deserialize([]).Should().BeEmpty();
    }
}
