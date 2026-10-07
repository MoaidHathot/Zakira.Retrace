using Microsoft.Extensions.Logging.Abstractions;
using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Core.Configuration;
using Zakira.Retrace.Core.Embeddings;
using Zakira.Retrace.Core.Index;
using Zakira.Retrace.Core.Services;

namespace Zakira.Retrace.Core.UnitTests;

/// <summary>
/// Which sources a build touches, as decided by <c>enabled</c> and <c>indexByDefault</c>.
/// </summary>
/// <remarks>
/// The scenario behind these: a harness driven mostly by automation produces thousands of sessions
/// nobody searches, and reading them dominated every <c>index build</c>. The user wants it kept
/// available but out of the default indexing path, and wants to be able to pull it in on demand.
/// </remarks>
public sealed class IndexPolicyTests
{
    private sealed class RecordingSource(string id) : ISessionSource, IIncrementalSource
    {
        public int ListCalls;

        public int GetCalls;

        public string Id { get; } = id;

        public string DisplayName => $"Recording ({Id})";

        public SourceCapabilities Capabilities => SourceCapabilities.Incremental;

        private SessionTranscript Transcript => new()
        {
            Summary = new SessionSummary
            {
                Ref = new SessionRef(Id, $"{Id}-session"),
                Title = $"A session from {Id}",
                CreatedAt = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
                UpdatedAt = new DateTimeOffset(2026, 9, 1, 1, 0, 0, TimeSpan.Zero),
                ContentHash = "v1"
            },
            Turns = [new Turn { Index = 0, Role = TurnRole.User, Blocks = [new TextBlock($"hello from {Id} with enough words to be indexed")] }],
            TotalTurns = 1
        };

        public ValueTask<SourceAvailability> ProbeAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(SourceAvailability.Available("(memory)", 1));

        public async IAsyncEnumerable<SessionSummary> ListAsync(SessionFilter filter, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ListCalls);
            await Task.CompletedTask;
            yield return Transcript.Summary;
        }

        public ValueTask<SessionTranscript?> GetAsync(string nativeId, TranscriptOptions options, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref GetCalls);
            return ValueTask.FromResult<SessionTranscript?>(Transcript);
        }

        public ValueTask<ResumeCommand?> GetResumeCommandAsync(string nativeId, ResumeOptions options, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ResumeCommand?>(null);

        public ValueTask<string?> GetWatermarkAsync(CancellationToken cancellationToken) => ValueTask.FromResult<string?>("wm-1");

        public IAsyncEnumerable<SessionSummary> ListChangedSinceAsync(string? watermark, CancellationToken cancellationToken) =>
            ListAsync(SessionFilter.All, cancellationToken);

        public async IAsyncEnumerable<string> ListAllIdsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield return Transcript.Summary.Ref.NativeId;
        }
    }

    private sealed class NoEmbeddingFactory : IEmbeddingProviderFactory
    {
        public string ModelId => "none";

        public bool IsAvailable => false;

        public Task<IEmbeddingProvider> CreateAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("no embeddings in this test");
    }

    private static (IndexBuilder Builder, IndexSearcher Searcher, SessionCatalog Catalog, RecordingSource OpenCode, RecordingSource CopilotCli) Create(TempDirectory temp, RetraceConfig config)
    {
        config.Index.Path = Path.Combine(temp.Path, "index.db");
        config.Index.AutoRefreshMinIntervalMinutes = 0;
        config.Embeddings.Enabled = false;

        var paths = new RetracePaths();
        var opencode = new RecordingSource("opencode");
        var copilot = new RecordingSource("copilot-cli");
        var factory = new NoEmbeddingFactory();

        var builder = new IndexBuilder(paths, config, [opencode, copilot], factory, NullLogger<IndexBuilder>.Instance);
        var searcher = new IndexSearcher(paths, config, factory);
        var catalog = new SessionCatalog(config, [opencode, copilot], searcher, builder, NullLogger<SessionCatalog>.Instance);

        return (builder, searcher, catalog, opencode, copilot);
    }

    [Fact]
    public async Task Unscoped_build_skips_a_source_that_is_not_indexed_by_default_and_says_why()
    {
        using var temp = new TempDirectory();
        var config = new RetraceConfig();
        config.Sources.CopilotCli.IndexByDefault = false;
        var (builder, searcher, _, opencode, copilot) = Create(temp, config);

        var result = await builder.BuildAsync(new IndexBuildOptions(), TestContext.Current.CancellationToken);

        opencode.GetCalls.Should().Be(1);
        copilot.GetCalls.Should().Be(0, "the whole point is that the expensive source is never read");
        copilot.ListCalls.Should().Be(0);

        var skipped = result.Sources.Single(source => source.SourceId == "copilot-cli");
        skipped.SkipReason.Should().Contain("indexByDefault");
        skipped.SkipReason.Should().Contain("--source copilot-cli");

        var status = await searcher.GetStatusAsync(TestContext.Current.CancellationToken);
        status.Sources.Select(source => source.SourceId).Should().Equal("opencode");
    }

    [Fact]
    public async Task Naming_the_source_explicitly_indexes_it_anyway()
    {
        using var temp = new TempDirectory();
        var config = new RetraceConfig();
        config.Sources.CopilotCli.IndexByDefault = false;
        var (builder, searcher, _, opencode, copilot) = Create(temp, config);

        var result = await builder.BuildAsync(new IndexBuildOptions { SourceIds = ["copilot-cli"] }, TestContext.Current.CancellationToken);

        copilot.GetCalls.Should().Be(1);
        opencode.GetCalls.Should().Be(0);
        result.Sources.Should().ContainSingle().Which.SkipReason.Should().BeNull();

        // What was indexed on request stays searchable afterwards.
        var hits = await searcher.SearchAsync(new SearchQuery { Text = "hello", Mode = SearchMode.Lexical }, TestContext.Current.CancellationToken);
        hits.Should().ContainSingle().Which.Session.Ref.SourceId.Should().Be("copilot-cli");
    }

    [Fact]
    public async Task A_disabled_source_is_never_indexed_even_when_named()
    {
        using var temp = new TempDirectory();
        var config = new RetraceConfig();
        config.Sources.CopilotCli.Enabled = false;
        var (builder, _, _, _, copilot) = Create(temp, config);

        var unscoped = await builder.BuildAsync(new IndexBuildOptions(), TestContext.Current.CancellationToken);
        var named = await builder.BuildAsync(new IndexBuildOptions { SourceIds = ["copilot-cli"] }, TestContext.Current.CancellationToken);

        copilot.GetCalls.Should().Be(0);

        // Silently absent from an unscoped build, which would otherwise nag forever; explained when asked for by name.
        unscoped.Sources.Should().NotContain(source => source.SourceId == "copilot-cli");
        named.Sources.Should().ContainSingle().Which.SkipReason.Should().Contain("enabled = false");
    }

    [Fact]
    public async Task Automatic_top_up_ignores_a_source_that_is_not_indexed_by_default()
    {
        using var temp = new TempDirectory();
        var config = new RetraceConfig();
        config.Sources.CopilotCli.IndexByDefault = false;
        var (builder, _, catalog, _, copilot) = Create(temp, config);

        await builder.BuildAsync(new IndexBuildOptions(), TestContext.Current.CancellationToken);

        // Nothing recorded for copilot-cli, so its watermark "moved" from the catalog's point of
        // view. The top-up must still leave it alone, or the setting would only move the cost
        // from `index refresh` into every search.
        var sessions = await catalog.ListAsync(SessionFilter.All, QueryMode.Indexed, TestContext.Current.CancellationToken);

        sessions.Should().ContainSingle().Which.Ref.SourceId.Should().Be("opencode");
        copilot.GetCalls.Should().Be(0);
        catalog.PendingSources.Should().BeEmpty();
    }

    [Fact]
    public async Task A_source_left_out_of_indexing_is_still_listable_live_and_probed_as_enabled()
    {
        using var temp = new TempDirectory();
        var config = new RetraceConfig();
        config.Sources.CopilotCli.IndexByDefault = false;
        var (_, _, catalog, _, _) = Create(temp, config);

        var live = await catalog.ListAsync(SessionFilter.All, QueryMode.Live, TestContext.Current.CancellationToken);
        live.Select(session => session.Ref.SourceId).Should().BeEquivalentTo("opencode", "copilot-cli");

        var probes = await catalog.ProbeAllAsync(TestContext.Current.CancellationToken);
        var copilot = probes.Single(probe => probe.Source.Id == "copilot-cli");
        copilot.Enabled.Should().BeTrue();
        copilot.IndexedByDefault.Should().BeFalse();
        probes.Single(probe => probe.Source.Id == "opencode").IndexedByDefault.Should().BeTrue();
    }

    [Fact]
    public void Config_set_round_trips_the_new_flag()
    {
        var updated = ConfigStore.Set(new RetraceConfig(), "sources.copilot-cli.indexByDefault", "false");

        updated.Sources.CopilotCli.IndexByDefault.Should().BeFalse();
        updated.Sources.CopilotCli.Enabled.Should().BeTrue();
        updated.Sources.IsIndexedByDefault("copilot-cli").Should().BeFalse();
        updated.Sources.IsIndexedByDefault("opencode").Should().BeTrue();
        updated.Sources.IsIndexedByDefault("some-future-source").Should().BeTrue("unknown sources default to participating");
    }
}
