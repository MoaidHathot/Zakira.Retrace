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

        var backend = new FakeBackend();
        backend.Transcripts.Add(FakeBackend.MakeTranscript("opencode", "ses_3aa5f96adffelfPdJUmLd91g7p", "Fix retry logic in HttpClient", @"W:\Github\Alpha", "The retries never back off and hammer the service", "I will add exponential backoff to the retry loop and cap it at five attempts"));
        backend.Transcripts.Add(FakeBackend.MakeTranscript("copilot-cli", "7f1c2d3e-aaaa-bbbb-cccc-0123456789ab", "Investigate flaky integration test", @"W:\Github\Beta", "Why is the integration test flaky on CI?", "The test depends on wall-clock time; freeze it with a fake clock"));
        backend.Transcripts.Add(FakeBackend.MakeTranscript("copilot-vscode", "c0ffee00-1234-5678-9abc-def012345678", "Write the TUI browser", @"W:\Github\Gamma", "Let us build a terminal browser for sessions", "A cell buffer makes panes and overlays easy to compose"));

        using var browser = SessionBrowser.CreateHeadless(backend, new BrowserOptions { Version = "0.3.0" }, 132, 34);
        browser.Start();
        await browser.SettleAsync();
        var frames = new List<string> { "=== browse ===", browser.Screen };

        browser.Type("retry");
        await browser.SettleAsync();
        frames.Add("=== search ===");
        frames.Add(browser.Screen);

        browser.Press(new KeyEvent(KeyKind.Enter));
        await browser.SettleAsync();
        frames.Add("=== reader ===");
        frames.Add(browser.Screen);

        browser.Press(new KeyEvent(KeyKind.Character, 'r'));
        await browser.SettleAsync();
        frames.Add("=== confirm ===");
        frames.Add(browser.Screen);

        browser.Press(new KeyEvent(KeyKind.Escape));
        browser.Press(new KeyEvent(KeyKind.Character, '?'));
        frames.Add("=== help ===");
        frames.Add(browser.Screen);

        await File.WriteAllTextAsync(path, string.Join('\n', frames), TestContext.Current.CancellationToken);
    }
}
