using Zakira.Retrace.Tui.Terminal;

namespace Zakira.Retrace.Tui.UnitTests;

/// <summary>Writes a frame to disk so the layout can be eyeballed; disabled unless RETRACE_TUI_SNAPSHOT is set.</summary>
public sealed class SnapshotTests
{
    [Fact]
    public async Task Writes_a_snapshot_when_asked()
    {
        var path = Environment.GetEnvironmentVariable("RETRACE_TUI_SNAPSHOT");
        if (string.IsNullOrEmpty(path))
        {
            Assert.Skip("Set RETRACE_TUI_SNAPSHOT to a file path to capture frames.");
            return;
        }

        var backend = new FakeBackend { SourceIds = ["opencode", "copilot-cli", "copilot-vscode"] };
        backend.Transcripts.Add(FakeBackend.MakeTranscript("opencode", "ses_3aa5f96adffelfPdJUmLd91g7p", "Fix retry logic in HttpClient", @"W:\Github\Alpha", "The retries never back off and hammer the service", "## Retry policy\nUse **exponential backoff** with `jitter`.\n```csharp\n    var delay = Retry(5); // cap attempts\n```\n> Keep the UI responsive.\n- [x] Add cancellation\n- [x] Test filters"));
        backend.Transcripts.Add(FakeBackend.MakeTranscript("copilot-cli", "7f1c2d3e-aaaa-bbbb-cccc-0123456789ab", "Investigate flaky integration test", @"W:\Github\Beta", "Why is the integration test flaky on CI?", "The test depends on wall-clock time; freeze it with a fake clock"));
        backend.Transcripts.Add(FakeBackend.MakeTranscript("copilot-vscode", "c0ffee00-1234-5678-9abc-def012345678", "Write the TUI browser", @"W:\Github\Gamma", "Let us build a terminal browser for sessions", "A cell buffer makes panes and overlays easy to compose"));

        using var browser = SessionBrowser.CreateHeadless(backend, new BrowserOptions { Version = "0.4.0" }, 132, 34);
        browser.Start();
        await browser.SettleAsync();
        var frames = new List<string> { "=== browse ===", browser.Screen };

        browser.Type("provider:opencode retry sort:newest group:provider");
        await browser.SettleAsync();
        frames.Add("=== search ===");
        frames.Add(browser.Screen);

        browser.Press(new KeyEvent(KeyKind.Function, Number: 2));
        frames.Add("=== provider controls ===");
        frames.Add(browser.Screen);
        browser.Press(new KeyEvent(KeyKind.Escape));

        browser.Press(new KeyEvent(KeyKind.Enter));
        await browser.SettleAsync();
        frames.Add("=== reader ===");
        frames.Add(browser.Screen);

        browser.Press(new KeyEvent(KeyKind.Character, 'v'));
        frames.Add("=== literal reader ===");
        frames.Add(browser.Screen);
        browser.Press(new KeyEvent(KeyKind.Character, 'v'));

        browser.Press(new KeyEvent(KeyKind.Character, 'r'));
        await browser.SettleAsync();
        frames.Add("=== confirm ===");
        frames.Add(browser.Screen);

        browser.Press(new KeyEvent(KeyKind.Escape));
        browser.Press(new KeyEvent(KeyKind.Character, '?'));
        frames.Add("=== help ===");
        frames.Add(browser.Screen);

        browser.Press(new KeyEvent(KeyKind.End));
        frames.Add("=== help syntax ===");
        frames.Add(browser.Screen);

        foreach (var (width, height) in new[] { (80, 24), (60, 18), (40, 12) })
        {
            using var narrow = SessionBrowser.CreateHeadless(backend, new BrowserOptions { InitialQuery = "provider:opencode group:provider" }, width, height);
            narrow.Start();
            await narrow.SettleAsync();
            frames.Add($"=== {width}x{height} browse ===");
            frames.Add(narrow.Screen);
            narrow.Press(new KeyEvent(KeyKind.Function, Number: 3));
            frames.Add($"=== {width}x{height} filters ===");
            frames.Add(narrow.Screen);
        }

        await File.WriteAllTextAsync(path, string.Join('\n', frames), TestContext.Current.CancellationToken);
    }
}
