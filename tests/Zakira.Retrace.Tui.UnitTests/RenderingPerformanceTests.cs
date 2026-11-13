using System.Diagnostics;
using System.Text.Json;
using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Core.Json;

namespace Zakira.Retrace.Tui.UnitTests;

public sealed class RenderingPerformanceTests
{
    [Fact]
    public async Task Records_large_preview_rendering_and_cached_frame_cost()
    {
        var directory = Environment.GetEnvironmentVariable("RETRACE_BENCHMARK_DIRECTORY");
        if (string.IsNullOrEmpty(directory))
        {
            Assert.Skip("Set RETRACE_BENCHMARK_DIRECTORY to record rendering timings.");
            return;
        }

        var backend = new FakeBackend();
        var transcript = FakeBackend.MakeTranscript("opencode", "performance", "Rich preview performance", "/workspace");
        var text = "## Retry policy\nUse **exponential backoff** with `jitter`.\n```csharp\n    var delay = Retry(42); // keep indentation\n```\n| Result | State |\n| --- | --- |\n| Retry | ready |";
        transcript = transcript with
        {
            Turns = [.. Enumerable.Range(0, 400).Select(index => new Turn { Index = index, Role = TurnRole.Assistant, Blocks = [new TextBlock(text)] })],
            TotalTurns = 400
        };
        backend.Transcripts.Add(transcript);
        using var browser = SessionBrowser.CreateHeadless(backend, new BrowserOptions(), 140, 40);
        browser.Start();
        await browser.SettleAsync();

        var watch = Stopwatch.StartNew();
        var frame = browser.Screen;
        var firstFrame = watch.Elapsed.TotalMilliseconds;
        frame.Should().Contain("Rich preview performance").And.Contain("Retry policy");

        var samples = new List<double>();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 100; index++)
        {
            watch.Restart();
            frame = browser.Screen;
            samples.Add(watch.Elapsed.TotalMilliseconds);
        }

        var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        samples.Sort();
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "rendering.json"), JsonSerializer.Serialize(new
        {
            characters = text.Length * 400,
            turns = 400,
            width = 140,
            height = 40,
            firstFrameMilliseconds = firstFrame,
            cachedMedianMilliseconds = samples[50],
            cachedMaximumMilliseconds = samples[^1],
            allocatedBytesPerCachedFrame = bytes / 100
        }, RetraceJson.IndentedOptions).Replace("\r\n", "\n", StringComparison.Ordinal), TestContext.Current.CancellationToken);
    }
}
