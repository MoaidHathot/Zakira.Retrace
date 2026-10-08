using System.Diagnostics;
using System.Globalization;
using System.Text;
using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Core.Index;
using Zakira.Retrace.Core.Text;
using Zakira.Retrace.Tui.Terminal;

namespace Zakira.Retrace.Tui;

public sealed partial class SessionBrowser
{
    // ---- resume ------------------------------------------------------------------------------

    private void BeginResume(bool fork)
    {
        var row = Selected;
        if (row is null)
        {
            return;
        }

        var reference = row.Session.Ref;
        SetStatus("preparing resume command\u2026", StatusKind.Busy, milliseconds: 10000);

        var task = Task.Run(async () =>
        {
            try
            {
                var command = await backend.GetResumeCommandAsync(reference, new ResumeOptions { Fork = fork }, CancellationToken.None).ConfigureAwait(false);
                Post(() => ShowResumeConfirm(row.Session, command));
            }
            catch (Exception ex)
            {
                Post(() => SetStatus(ex.Message, StatusKind.Error));
            }
        });

        Track(task);
    }

    private void ShowResumeConfirm(SessionSummary session, ResumeCommand command)
    {
        status = null;

        var lines = new List<StyledSpan[]>();
        lines.Add([new StyledSpan(TextUtilities.Flatten(session.Title), Theme.Heading)]);
        lines.Add([new StyledSpan(session.Ref.Uri, Theme.Uri)]);
        lines.Add([]);
        lines.Add([new StyledSpan("command  ", Theme.Dim), new StyledSpan(command.DisplayCommand, Theme.Heading)]);
        lines.Add([new StyledSpan("in       ", Theme.Dim), new StyledSpan(command.WorkingDirectory ?? "(current directory)", Theme.Secondary)]);

        if (command.WorkingDirectory is { Length: > 0 } directory && !Directory.Exists(directory))
        {
            lines.Add([new StyledSpan("that directory no longer exists; the harness will start where retrace was launched", Theme.Error)]);
        }

        if (!command.RestoresConversation)
        {
            lines.Add([]);
            lines.Add([new StyledSpan("This does not restore the conversation.", Theme.Warn)]);
        }

        if (command.Notes is { Length: > 0 } notes)
        {
            foreach (var wrapped in TextWidth.Wrap(TextUtilities.Flatten(notes), Math.Max(20, buffer.Width - 10)))
            {
                lines.Add([new StyledSpan(wrapped, Theme.Dim)]);
            }
        }

        lines.Add([]);
        lines.Add([new StyledSpan("retrace exits and hands the terminal to the harness.", Theme.Dim)]);

        confirm = new ConfirmState(
            "Resume session",
            lines,
            Accept: () =>
            {
                result = new BrowserResult(BrowserExit.Resume, session, command);
                running = false;
            },
            Copy: () => CopyText(command.DisplayCommand, "resume command"),
            AcceptLabel: "run");

        mode = Mode.Confirm;
    }

    private void CopyResumeCommand()
    {
        var row = Selected;
        if (row is null)
        {
            return;
        }

        var reference = row.Session.Ref;
        var task = Task.Run(async () =>
        {
            try
            {
                var command = await backend.GetResumeCommandAsync(reference, ResumeOptions.Default, CancellationToken.None).ConfigureAwait(false);
                Post(() => CopyText(command.DisplayCommand, "resume command"));
            }
            catch (Exception ex)
            {
                Post(() => SetStatus(ex.Message, StatusKind.Error));
            }
        });

        Track(task);
    }

    // ---- clipboard / open --------------------------------------------------------------------

    private void CopyText(string? text, string what)
    {
        if (string.IsNullOrEmpty(text))
        {
            SetStatus($"nothing to copy: no {what} recorded", StatusKind.Error);
            return;
        }

        if (headless)
        {
            LastCopied = text;
            SetStatus($"copied {what}", StatusKind.Ok);
            return;
        }

        var tool = Clipboard.TryCopy(text);
        if (tool is null)
        {
            // No clipboard tool on this machine; ask the terminal to do it instead.
            screen.WriteClipboardEscape(text);
            SetStatus($"copied {what} via the terminal (OSC 52)", StatusKind.Ok);
            return;
        }

        SetStatus($"copied {what}", StatusKind.Ok);
    }

    /// <summary>The last text copied while headless, for tests.</summary>
    public string? LastCopied { get; private set; }

    private void OpenWorkspaceFolder()
    {
        var directory = Selected?.Session.Workspace?.Path;
        if (string.IsNullOrEmpty(directory))
        {
            SetStatus("no working directory recorded for this session", StatusKind.Error);
            return;
        }

        if (!Directory.Exists(directory))
        {
            SetStatus($"directory no longer exists: {directory}", StatusKind.Error);
            return;
        }

        if (headless)
        {
            SetStatus($"opened {directory}", StatusKind.Ok);
            return;
        }

        try
        {
            var startInfo = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = false, CreateNoWindow = true }
                : OperatingSystem.IsMacOS()
                    ? new ProcessStartInfo("open", directory) { UseShellExecute = false }
                    : new ProcessStartInfo("xdg-open", directory) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };

            using var process = Process.Start(startInfo);
            SetStatus($"opened {directory}", StatusKind.Ok);
        }
        catch (Exception ex)
        {
            SetStatus($"could not open folder: {ex.Message}", StatusKind.Error);
        }
    }

    // ---- export ------------------------------------------------------------------------------

    private void ExportTranscript()
    {
        var row = Selected;
        if (row is null)
        {
            return;
        }

        var reference = row.Session.Ref;
        var fileName = $"{reference.SourceId}-{SafeFileName(reference.NativeId)}.md";
        var path = Path.Combine(Directory.GetCurrentDirectory(), fileName);
        SetStatus("exporting\u2026", StatusKind.Busy, milliseconds: 30000);

        var task = Task.Run(async () =>
        {
            try
            {
                var full = await backend.GetTranscriptAsync(reference, TranscriptOptions.Full, CancellationToken.None).ConfigureAwait(false);
                var markdown = RenderMarkdown(full);

                if (!headless)
                {
                    await File.WriteAllTextAsync(path, markdown, CancellationToken.None).ConfigureAwait(false);
                }

                Post(() => SetStatus($"exported {fileName}", StatusKind.Ok, milliseconds: 6000));
            }
            catch (Exception ex)
            {
                Post(() => SetStatus($"export failed: {ex.Message}", StatusKind.Error));
            }
        });

        Track(task);
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
        }

        return builder.Length > 64 ? builder.ToString(0, 64) : builder.ToString();
    }

    /// <summary>Markdown export, in the same shape as <c>retrace export</c>.</summary>
    private static string RenderMarkdown(SessionTranscript transcript)
    {
        var summary = transcript.Summary;
        var builder = new StringBuilder();

        builder.Append("# ").AppendLine(summary.Title).AppendLine();
        builder.AppendLine("```yaml");
        builder.Append("uri: ").AppendLine(summary.Ref.Uri);
        builder.Append("source: ").AppendLine(summary.Ref.SourceId);
        builder.Append("created: ").AppendLine(summary.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        builder.Append("updated: ").AppendLine(summary.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));

        if (summary.Workspace?.Path is { Length: > 0 } path)
        {
            builder.Append("workspace: ").AppendLine(path);
        }

        if (summary.Agent is { Length: > 0 } agent)
        {
            builder.Append("agent: ").AppendLine(agent);
        }

        if (summary.Models.Count > 0)
        {
            builder.Append("models: [").Append(string.Join(", ", summary.Models)).AppendLine("]");
        }

        builder.AppendLine("```").AppendLine();

        foreach (var turn in transcript.Turns)
        {
            builder.Append("## ").Append(TextUtilities.RoleLabel(turn.Role));
            if (turn.Model is { Length: > 0 })
            {
                builder.Append(" \u00b7 ").Append(turn.Model);
            }

            if (turn.Timestamp is { } stamp)
            {
                builder.Append(" \u00b7 ").Append(stamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            }

            builder.AppendLine().AppendLine();

            foreach (var block in turn.Blocks)
            {
                switch (block)
                {
                    case TextBlock text:
                        builder.AppendLine(text.Text).AppendLine();
                        break;

                    case ReasoningBlock reasoning:
                        builder.AppendLine("> [!NOTE] Reasoning");
                        foreach (var line in reasoning.Text.Split('\n'))
                        {
                            builder.Append("> ").AppendLine(line.TrimEnd());
                        }

                        builder.AppendLine();
                        break;

                    case ToolCallBlock tool:
                        builder.Append("**Tool** `").Append(tool.ToolName).Append('`');
                        if (!string.IsNullOrWhiteSpace(tool.Title))
                        {
                            builder.Append(" \u2014 ").Append(tool.Title);
                        }

                        builder.AppendLine().AppendLine();
                        if (!string.IsNullOrWhiteSpace(tool.Output))
                        {
                            builder.AppendLine("```").AppendLine(tool.Output.TrimEnd()).AppendLine("```").AppendLine();
                        }

                        break;

                    case PatchBlock patch:
                        builder.Append("**Edit** `").Append(patch.Path).Append('`').AppendLine().AppendLine();
                        if (!string.IsNullOrWhiteSpace(patch.Diff))
                        {
                            builder.AppendLine("```diff").AppendLine(patch.Diff.TrimEnd()).AppendLine("```").AppendLine();
                        }

                        break;
                }
            }
        }

        if (transcript.Files.Count > 0)
        {
            builder.AppendLine("## Files touched").AppendLine();
            foreach (var file in transcript.Files)
            {
                builder.Append("- `").Append(file.Path).AppendLine("`");
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    // ---- tags --------------------------------------------------------------------------------

    private void BeginTagInput()
    {
        var row = Selected;
        if (row is null)
        {
            return;
        }

        var reference = row.Session.Ref;
        var current = row.Session.Tags.Count > 0 ? "current: " + string.Join(" ", row.Session.Tags.Select(tag => "#" + tag)) : "no tags yet";

        input = new InputState(
            "Tags (space-separated; -name removes)",
            current,
            text =>
            {
                var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (tokens.Length == 0)
                {
                    return;
                }

                var remove = tokens.Where(token => token.StartsWith('-')).Select(token => token.TrimStart('-', '#')).Where(token => token.Length > 0).ToArray();
                var add = tokens.Where(token => !token.StartsWith('-')).Select(token => token.TrimStart('#')).Where(token => token.Length > 0).ToArray();

                var task = Task.Run(async () =>
                {
                    try
                    {
                        var tags = await backend.UpdateTagsAsync(reference, add, remove, CancellationToken.None).ConfigureAwait(false);
                        Post(() =>
                        {
                            ReplaceRowTags(reference, tags);
                            SetStatus(tags.Count == 0 ? "tags cleared" : "tags: " + string.Join(" ", tags.Select(tag => "#" + tag)), StatusKind.Ok, milliseconds: 5000);
                        });
                    }
                    catch (Exception ex)
                    {
                        Post(() => SetStatus($"tagging failed: {ex.Message}", StatusKind.Error));
                    }
                });

                Track(task);
            });

        mode = Mode.Input;
    }

    private void ReplaceRowTags(SessionRef reference, IReadOnlyList<string> tags)
    {
        var updated = new List<Row>(rows.Count);
        foreach (var row in rows)
        {
            updated.Add(row.Session.Ref == reference
                ? row with { Session = row.Session with { Tags = tags } }
                : row);
        }

        rows = updated;
        if (transcript is not null && loadedRef == reference)
        {
            transcript = transcript with { Summary = transcript.Summary with { Tags = tags } };
            formattedWidth = -1;
        }
    }

    // ---- pick --------------------------------------------------------------------------------

    private void Pick()
    {
        var row = Selected;
        if (row is null)
        {
            return;
        }

        var session = row.Session;

        switch (options.Pick)
        {
            case PickKind.Uri:
                Finish(session.Ref.Uri);
                return;

            case PickKind.Id:
                Finish(session.Ref.NativeId);
                return;

            case PickKind.Directory:
                if (string.IsNullOrEmpty(session.Workspace?.Path))
                {
                    SetStatus("this session has no recorded directory", StatusKind.Error);
                    return;
                }

                Finish(session.Workspace.Path);
                return;

            case PickKind.Command:
            {
                var reference = session.Ref;
                var task = Task.Run(async () =>
                {
                    try
                    {
                        var command = await backend.GetResumeCommandAsync(reference, ResumeOptions.Default, CancellationToken.None).ConfigureAwait(false);
                        Post(() => Finish(command.DisplayCommand));
                    }
                    catch (Exception ex)
                    {
                        Post(() => SetStatus(ex.Message, StatusKind.Error));
                    }
                });

                Track(task);
                return;
            }
        }

        void Finish(string output)
        {
            result = new BrowserResult(BrowserExit.Print, session, Output: output);
            running = false;
        }
    }

    // ---- index refresh -----------------------------------------------------------------------

    private void RefreshIndex()
    {
        if (refreshing)
        {
            SetStatus("a refresh is already running");
            return;
        }

        refreshing = true;
        activity = "refreshing index";
        dirty = true;

        var progress = ActivityProgress(report => $"indexing {report.SourceId} \u00b7 {report.Processed:N0} session(s)");

        var task = Task.Run(async () =>
        {
            try
            {
                var built = await backend.RefreshIndexAsync(progress, lifetime.Token).ConfigureAwait(false);
                Post(() =>
                {
                    refreshing = false;
                    activity = null;
                    var skipped = built.Sources.Where(source => source.SkipReason is not null).Select(source => source.SourceId).ToArray();
                    var note = skipped.Length > 0 ? $" \u00b7 skipped {string.Join(", ", skipped)}" : string.Empty;
                    SetStatus($"indexed {built.SessionsIndexed:N0} session(s) in {built.Duration.TotalSeconds:F1}s{note}", StatusKind.Ok, milliseconds: 6000);
                    ScheduleQuery(immediate: true);
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Post(() =>
                {
                    refreshing = false;
                    activity = null;
                    SetStatus($"refresh failed: {ex.Message}", StatusKind.Error, milliseconds: 8000);
                });
            }
        }, lifetime.Token);

        Track(task);
    }
}
