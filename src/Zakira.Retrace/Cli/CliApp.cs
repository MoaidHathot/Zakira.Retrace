using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Core.Configuration;
using Zakira.Retrace.Core.DependencyInjection;
using Zakira.Retrace.Core.Embeddings;
using Zakira.Retrace.Core.Index;
using Zakira.Retrace.Core.Json;
using Zakira.Retrace.Core.Services;
using Zakira.Retrace.Mcp;
using Zakira.Retrace.Tui;
using Zakira.Retrace.Tui.Terminal;

namespace Zakira.Retrace.Cli;

/// <summary>
/// Builds and runs the <c>retrace</c> command tree.
/// </summary>
public static class CliApp
{
    /// <summary>Parses and executes a command line.</summary>
    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        // The host is created before parsing so global options such as --config and --verbose can
        // be read from the raw arguments; System.CommandLine does not expose them until an action
        // runs, and several commands need the host during their own construction.
        var configPath = ExtractOptionValue(args, "--config");
        var verbose = args.Contains("--verbose", StringComparer.Ordinal) || args.Contains("-v", StringComparer.Ordinal);

        await using var host = new RetraceHost(configPath, verbose);

        var root = BuildRoot(host, stdout, stderr);
        var parseResult = root.Parse(args);

        // System.CommandLine would otherwise swallow exceptions, print its own stack trace, and
        // still exit 0. Turning its handler off lets RetraceException reach Program.cs, which
        // renders a one-line message and returns a meaningful exit code.
        var invocation = new InvocationConfiguration
        {
            EnableDefaultExceptionHandler = false,
            Output = stdout,
            Error = stderr
        };

        return await parseResult.InvokeAsync(invocation, cancellationToken).ConfigureAwait(false);
    }

    private static RootCommand BuildRoot(RetraceHost host, TextWriter stdout, TextWriter stderr)
    {
        var root = new RootCommand(
            "retrace — search, read, and resume AI coding sessions across OpenCode, Copilot CLI, and Copilot in VS Code.");

        root.Options.Add(CommonOptions.Config);
        root.Options.Add(CommonOptions.Output);
        root.Options.Add(CommonOptions.Verbose);
        root.Options.Add(CommonOptions.Quiet);
        root.Options.Add(CommonOptions.Color);

        root.Subcommands.Add(BuildVersionCommand(stdout));
        root.Subcommands.Add(BuildInfoCommand(host, stdout));
        root.Subcommands.Add(BuildSourcesCommand(host, stdout));
        root.Subcommands.Add(BuildDoctorCommand(host, stdout));
        root.Subcommands.Add(BuildTuiCommand(host, stdout, stderr));
        root.Subcommands.Add(BuildListCommand(host, stdout));
        root.Subcommands.Add(BuildSearchCommand(host, stdout));
        root.Subcommands.Add(BuildShowCommand(host, stdout));
        root.Subcommands.Add(BuildExportCommand(host, stdout, stderr));
        root.Subcommands.Add(BuildFilesCommand(host, stdout));
        root.Subcommands.Add(BuildResumeCommand(host, stdout, stderr));
        root.Subcommands.Add(BuildTagCommand(host, stdout));
        root.Subcommands.Add(BuildIndexCommand(host, stdout, stderr));
        root.Subcommands.Add(BuildDepsCommand(host, stdout, stderr));
        root.Subcommands.Add(BuildConfigCommand(host, stdout));
        root.Subcommands.Add(BuildMcpCommand(host, stderr));

        root.SetAction(async (parseResult, cancellationToken) =>
        {
            // A person at a terminal typing just `retrace` wants the browser. A script or an agent
            // with redirected streams gets the pointer to --help and a non-zero exit, as before.
            if (HasInteractiveTerminal())
            {
                return await RunBrowserAsync(host, stdout, stderr, query: null, SessionFilter.All, PickKind.None, noMouse: false, cancellationToken).ConfigureAwait(false);
            }

            stderr.WriteLine(parseResult.CommandResult.Command.Description);
            stderr.WriteLine();
            stderr.WriteLine("Run 'retrace' in a terminal to browse sessions interactively, or 'retrace --help' to see every command.");
            return 1;
        });

        return root;
    }

    // ---- tui -------------------------------------------------------------------------------

    private static Command BuildTuiCommand(RetraceHost host, TextWriter stdout, TextWriter stderr)
    {
        var filters = new FilterOptions();
        var query = new Argument<string?>("query")
        {
            Description = "Search to start with, as if typed into the search box.",
            Arity = ArgumentArity.ZeroOrOne
        };

        var pick = new Option<string?>("--pick")
        {
            Description = "Run as a picker: Enter prints the chosen session's uri, id, dir, or command to stdout and exits. "
                + "Draws on stderr, so it works inside $(...) — for example `cd (retrace tui --pick dir)`."
        };

        var noMouse = new Option<bool>("--no-mouse") { Description = "Leave the mouse to the terminal, so native text selection keeps working." };

        var command = new Command("tui", "Browse, search, read, and resume sessions interactively. Also what a bare `retrace` opens.");
        command.Aliases.Add("ui");
        command.Aliases.Add("browse");
        command.Arguments.Add(query);
        filters.AddTo(command);
        command.Options.Add(pick);
        command.Options.Add(noMouse);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var pickKind = parseResult.GetValue(pick)?.Trim().ToLowerInvariant() switch
            {
                null or "" => PickKind.None,
                "uri" => PickKind.Uri,
                "id" => PickKind.Id,
                "dir" or "directory" or "cwd" or "path" => PickKind.Directory,
                "command" or "cmd" or "resume" => PickKind.Command,
                var other => throw new RetraceException($"Unknown --pick value '{other}'. Use uri, id, dir, or command.")
            };

            if (!HasInteractiveTerminal())
            {
                throw new RetraceException(
                    "The interactive browser requires a terminal. Use `retrace search` and `retrace show` for scripted use, "
                    + "or `retrace tui --pick ...` inside a command substitution so it can draw on stderr.");
            }

            return await RunBrowserAsync(
                host,
                stdout,
                stderr,
                parseResult.GetValue(query),
                filters.Build(parseResult, limit: 0, SessionSortOrder.Recent),
                pickKind,
                parseResult.GetValue(noMouse),
                cancellationToken).ConfigureAwait(false);
        });

        return command;
    }

    /// <summary>Stdin is a terminal and at least one of stdout or stderr is, which is all the browser needs to draw.</summary>
    private static bool HasInteractiveTerminal() =>
        !Console.IsInputRedirected && !(Console.IsOutputRedirected && Console.IsErrorRedirected);

    /// <summary>Runs the browser and carries out whatever it chose once the terminal is restored.</summary>
    private static async Task<int> RunBrowserAsync(
        RetraceHost host,
        TextWriter stdout,
        TextWriter stderr,
        string? query,
        SessionFilter filter,
        PickKind pick,
        bool noMouse,
        CancellationToken cancellationToken)
    {
        // Built once for the whole session, so the embedding model is loaded a single time
        // instead of on every keystroke's search.
        host.LongLived = true;

        var config = await host.GetConfigAsync(cancellationToken).ConfigureAwait(false);
        var catalog = await host.GetServiceAsync<SessionCatalog>(cancellationToken).ConfigureAwait(false);
        var tags = await host.GetServiceAsync<TagStore>(cancellationToken).ConfigureAwait(false);
        var searcher = await host.GetServiceAsync<IndexSearcher>(cancellationToken).ConfigureAwait(false);
        var embeddings = await host.GetServiceAsync<IEmbeddingProviderFactory>(cancellationToken).ConfigureAwait(false);

        var browserOptions = new BrowserOptions
        {
            InitialQuery = query,
            Filter = filter,
            Pick = pick,
            SearchMode = ParseSearchMode(config.Search.Mode),
            Mouse = config.Tui.Mouse && !noMouse,
            PreviewMaxCharacters = config.Tui.PreviewMaxCharacters,
            ShowToolOutput = config.Tui.ShowToolOutput,
            ShowReasoning = config.Tui.ShowReasoning,
            ListLimit = config.Tui.ListLimit,
            SearchLimit = config.Tui.SearchLimit,
            RelativeDates = config.Output.DateFormat.Equals("relative", StringComparison.OrdinalIgnoreCase),
            ColorDepth = TerminalScreen.ParseColorDepth(config.Tui.ColorDepth),
            Version = RetraceVersion.Current
        };

        BrowserResult outcome;
        using (var browser = new SessionBrowser(new CatalogBrowserBackend(catalog, tags, searcher, embeddings, config), browserOptions))
        {
            outcome = browser.Run(cancellationToken);
        }

        switch (outcome.Exit)
        {
            case BrowserExit.Resume when outcome.Resume is { } resume:
                stderr.WriteLine(ConsoleStyle.Dim($"Resuming {outcome.Session?.Ref.Uri}"));
                stderr.WriteLine(ConsoleStyle.Dim($"Running: {resume.DisplayCommand}"));
                if (resume.WorkingDirectory is { Length: > 0 } cwd)
                {
                    stderr.WriteLine(ConsoleStyle.Dim($"     in: {cwd}"));
                }

                return await ResumeLauncher.RunAsync(resume, cancellationToken).ConfigureAwait(false);

            case BrowserExit.Print when outcome.Output is { } output:
                stdout.WriteLine(output);
                return 0;

            default:
                return 0;
        }
    }

    // ---- version / info / sources / doctor -------------------------------------------------

    private static Command BuildVersionCommand(TextWriter stdout)
    {
        var command = new Command("version", "Print the Retrace version.");
        command.SetAction(_ =>
        {
            stdout.WriteLine(RetraceVersion.Current);
            return 0;
        });

        return command;
    }

    private static Command BuildInfoCommand(RetraceHost host, TextWriter stdout)
    {
        var command = new Command("info", "Show resolved paths, configuration, and capabilities.");
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var config = await host.GetConfigAsync(cancellationToken).ConfigureAwait(false);
            var services = await host.GetServicesAsync(cancellationToken).ConfigureAwait(false);
            var searcher = (IndexSearcher)services.GetService(typeof(IndexSearcher))!;
            var factory = (IEmbeddingProviderFactory)services.GetService(typeof(IEmbeddingProviderFactory))!;
            var provisioner = (EmbeddingModelProvisioner)services.GetService(typeof(EmbeddingModelProvisioner))!;

            var payload = new
            {
                version = RetraceVersion.Current,
                configPath = host.Store.ConfigPath,
                configExists = host.Store.Exists,
                configRoot = host.Paths.ConfigRoot,
                dataDirectory = host.Paths.DataDirectory,
                indexPath = searcher.IndexPath,
                indexExists = searcher.Exists,
                modelsDirectory = provisioner.ModelsRoot,
                embeddings = new
                {
                    enabled = config.Embeddings.Enabled,
                    model = factory.ModelId,
                    installed = factory.IsAvailable,
                    modelPath = provisioner.GetModelPath(factory.ModelId),
                    tokenizerPath = provisioner.GetTokenizerPath(factory.ModelId)
                },
                search = new
                {
                    mode = config.Search.Mode,
                    fusion = "rrf",
                    rrfK = config.Search.RrfK,
                    lexicalWeight = config.Search.LexicalWeight,
                    semanticWeight = config.Search.SemanticWeight
                }
            };

            if (CommonOptions.GetFormat(parseResult) != OutputFormat.Text)
            {
                stdout.WriteLine(JsonSerializer.Serialize(payload, RetraceJson.IndentedOptions));
                return 0;
            }

            stdout.WriteLine(ConsoleStyle.Bold($"Zakira.Retrace {RetraceVersion.Current}"));
            stdout.WriteLine();
            WriteField(stdout, "config", host.Store.ConfigPath + (host.Store.Exists ? string.Empty : ConsoleStyle.Dim("  (created on first run)")));
            WriteField(stdout, "config root", host.Paths.ConfigRoot);
            WriteField(stdout, "data directory", host.Paths.DataDirectory);
            WriteField(stdout, "index", searcher.IndexPath + (searcher.Exists ? string.Empty : ConsoleStyle.Dim("  (not built)")));
            WriteField(stdout, "models", provisioner.ModelsRoot);
            stdout.WriteLine();
            WriteField(stdout, "embedding model", factory.ModelId + (factory.IsAvailable
                ? ConsoleStyle.Green("  installed")
                : ConsoleStyle.Yellow("  not installed — run `retrace deps install onnx`")));
            WriteField(stdout, "search mode", config.Search.Mode);
            WriteField(stdout, "fusion", $"rrf(k={config.Search.RrfK}) lexical×{config.Search.LexicalWeight} semantic×{config.Search.SemanticWeight}");
            return 0;
        });

        return command;
    }

    private static Command BuildSourcesCommand(RetraceHost host, TextWriter stdout)
    {
        var command = new Command("sources", "List the session sources and whether each is readable.");
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var catalog = await host.GetServiceAsync<SessionCatalog>(cancellationToken).ConfigureAwait(false);
            var probes = await catalog.ProbeAllAsync(cancellationToken).ConfigureAwait(false);

            if (CommonOptions.GetFormat(parseResult) != OutputFormat.Text)
            {
                var payload = probes.Select(probe => new
                {
                    id = probe.Source.Id,
                    name = probe.Source.DisplayName,
                    enabled = probe.Enabled,
                    indexByDefault = probe.IndexedByDefault,
                    available = probe.Availability.IsAvailable,
                    reason = probe.Availability.Reason,
                    dataPath = probe.Availability.DataPath,
                    sessionCount = probe.Availability.SessionCount,
                    harnessVersion = probe.Availability.HarnessVersion,
                    capabilities = probe.Source.Capabilities.ToString(),
                    details = probe.Availability.Details
                });

                stdout.WriteLine(JsonSerializer.Serialize(payload, RetraceJson.IndentedOptions));
                return 0;
            }

            foreach (var (source, availability, enabled, indexedByDefault) in probes)
            {
                var status = !enabled
                    ? ConsoleStyle.Dim("disabled")
                    : availability.IsAvailable
                        ? ConsoleStyle.Green("available")
                        : ConsoleStyle.Yellow("unavailable");

                var count = availability.SessionCount is { } sessions ? $"  {sessions:N0} session(s)" : string.Empty;
                var indexing = enabled && !indexedByDefault ? ConsoleStyle.Yellow("  manual indexing") : string.Empty;
                stdout.WriteLine($"{ConsoleStyle.Bold(source.Id.PadRight(16))} {status}{ConsoleStyle.Dim(count)}{indexing}");
                stdout.WriteLine($"  {ConsoleStyle.Dim(source.DisplayName)}");

                if (availability.DataPath is { Length: > 0 } path)
                {
                    stdout.WriteLine($"  {ConsoleStyle.Dim(path)}");
                }

                if (availability.Reason is { Length: > 0 } reason)
                {
                    stdout.WriteLine($"  {ConsoleStyle.Yellow(reason)}");
                }

                if (enabled && !indexedByDefault)
                {
                    stdout.WriteLine($"  {ConsoleStyle.Dim($"not indexed by default; `retrace index build --source {source.Id}` indexes it on request")}");
                }

                foreach (var detail in availability.Details)
                {
                    stdout.WriteLine($"  {ConsoleStyle.Dim("· " + detail)}");
                }

                stdout.WriteLine($"  {ConsoleStyle.Dim("capabilities: " + source.Capabilities)}");
                stdout.WriteLine();
            }

            return 0;
        });

        return command;
    }

    private static Command BuildDoctorCommand(RetraceHost host, TextWriter stdout)
    {
        var command = new Command("doctor", "Check configuration, sources, index, and models.");
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var config = await host.GetConfigAsync(cancellationToken).ConfigureAwait(false);
            var catalog = await host.GetServiceAsync<SessionCatalog>(cancellationToken).ConfigureAwait(false);
            var searcher = await host.GetServiceAsync<IndexSearcher>(cancellationToken).ConfigureAwait(false);
            var factory = await host.GetServiceAsync<IEmbeddingProviderFactory>(cancellationToken).ConfigureAwait(false);

            var probes = await catalog.ProbeAllAsync(cancellationToken).ConfigureAwait(false);
            var status = await searcher.GetStatusAsync(cancellationToken).ConfigureAwait(false);

            var checks = new List<(string Name, bool Ok, string Detail)>
            {
                ("config", true, host.Store.Exists ? host.Store.ConfigPath : $"{host.Store.ConfigPath} (defaults)"),
                ("data directory", CanUseDirectory(host.Paths.DataDirectory), host.Paths.DataDirectory)
            };

            foreach (var (source, availability, enabled, indexedByDefault) in probes)
            {
                checks.Add((
                    $"source: {source.Id}",
                    !enabled || availability.IsAvailable,
                    !enabled
                        ? "disabled in configuration"
                        : availability.IsAvailable
                            ? $"{availability.SessionCount:N0} session(s) at {availability.DataPath}"
                              + (indexedByDefault ? string.Empty : "  (manual indexing: not included in `index build` unless named with --source)")
                            : availability.Reason ?? "unavailable"));
            }

            checks.Add((
                "index",
                status.Exists,
                status.Exists
                    ? $"{status.SessionCount:N0} session(s), {status.ChunkCount:N0} chunk(s), {status.VectorCount:N0} vector(s), {status.SizeBytes / 1024.0 / 1024.0:F1} MB"
                    : "not built — run `retrace index build`"));

            if (status.Exists)
            {
                // Both numbers were already on screen, one row apart, and nothing compared them.
                // That is how an 804-session gap stayed invisible until it turned into latency.
                //
                // Two things make the comparison honest. The count comes from ListAsync, not the
                // probe above: the probe counts what is on disk, and copilot-vscode alone holds
                // dozens of empty chat files that `includeEmptySessions` deliberately discards.
                // And it counts distinct native ids, because the index is keyed by them — the same
                // VS Code chat session is present in two workspace storage directories often
                // enough that a raw file count reports a backlog no refresh can ever clear.
                // Either mistake makes this check cry wolf, which is worse than not having it.
                var behind = new List<string>();
                var manual = new List<string>();

                foreach (var (source, availability, enabled, indexedByDefault) in probes)
                {
                    if (!enabled || !availability.IsAvailable)
                    {
                        continue;
                    }

                    // A source taken out of the default indexing path is behind on purpose.
                    // Counting its backlog would pin a permanent warning on a deliberate choice,
                    // so it is named separately and never fails the check.
                    if (!indexedByDefault)
                    {
                        manual.Add(source.Id);
                        continue;
                    }

                    var live = new HashSet<string>(StringComparer.Ordinal);
                    await foreach (var session in source.ListAsync(SessionFilter.All, cancellationToken))
                    {
                        live.Add(session.Ref.NativeId);
                    }

                    var indexed = status.Sources.FirstOrDefault(state => state.SourceId == source.Id)?.SessionCount ?? 0;
                    if (live.Count > indexed)
                    {
                        behind.Add($"{source.Id} +{live.Count - indexed:N0}");
                    }
                }

                var manualNote = manual.Count == 0
                    ? string.Empty
                    : $"  (manual indexing, not checked: {string.Join(", ", manual)})";

                checks.Add((
                    "index freshness",
                    behind.Count == 0,
                    (behind.Count == 0
                        ? "every source is fully indexed"
                        : $"not yet indexed: {string.Join(", ", behind)} — run `retrace index refresh`") + manualNote));

                // Reported, never judged. A source can be missing vectors because a keyword-only
                // automatic refresh picked it up, or because it was deliberately left keyword-only
                // — and nothing here can tell those apart. Flagging the second as a fault would
                // pin a permanent warning on a choice the user made on purpose, so this states the
                // numbers and leaves the conclusion to the reader.
                var coverage = status.Sources
                    .Where(state => state.SessionCount > 0)
                    .Select(state => $"{state.SourceId} {state.SessionsWithVectors:N0}/{state.SessionsEmbeddable:N0}");

                checks.Add((
                    "vector coverage",
                    true,
                    config.Embeddings.Enabled
                        ? string.Join("  ·  ", coverage) + "  (add missing with `retrace index refresh --backfill --source <id>`)"
                        : "embeddings disabled; existing vectors are unused"));
            }

            checks.Add((
                "embedding model",
                !config.Embeddings.Enabled || factory.IsAvailable,
                !config.Embeddings.Enabled
                    ? "disabled; search runs keyword-only"
                    : factory.IsAvailable
                        ? $"{factory.ModelId} installed"
                        : $"{factory.ModelId} not installed — run `retrace deps install onnx` (search falls back to keyword-only)"));

            if (status is { Exists: true, EmbeddingModel: { } indexedModel })
            {
                checks.Add((
                    "embedding consistency",
                    string.Equals(indexedModel, factory.ModelId, StringComparison.OrdinalIgnoreCase),
                    string.Equals(indexedModel, factory.ModelId, StringComparison.OrdinalIgnoreCase)
                        ? $"index and runtime both use {indexedModel}"
                        : $"index was built with {indexedModel} but runtime uses {factory.ModelId} — run `retrace index build --force`"));
            }

            if (CommonOptions.GetFormat(parseResult) != OutputFormat.Text)
            {
                stdout.WriteLine(JsonSerializer.Serialize(
                    checks.Select(check => new { name = check.Name, ok = check.Ok, detail = check.Detail }),
                    RetraceJson.IndentedOptions));
                return checks.All(check => check.Ok) ? 0 : 1;
            }

            var width = checks.Max(check => check.Name.Length);
            foreach (var (name, ok, detail) in checks)
            {
                var mark = ok ? ConsoleStyle.Green("ok  ") : ConsoleStyle.Yellow("warn");
                stdout.WriteLine($"{mark}  {name.PadRight(width)}  {ConsoleStyle.Dim(detail)}");
            }

            return checks.All(check => check.Ok) ? 0 : 1;
        });

        return command;
    }

    // ---- list / search ---------------------------------------------------------------------

    private static Command BuildListCommand(RetraceHost host, TextWriter stdout)
    {
        var filters = new FilterOptions();
        var limit = new Option<int>("--limit", "-n") { Description = "Maximum sessions to show.", DefaultValueFactory = _ => 25 };
        var sort = new Option<string>("--sort") { Description = "Ordering: recent (default), created, or size.", DefaultValueFactory = _ => "recent" };

        var command = new Command("list", "List sessions, most recently updated first.");
        filters.AddTo(command);
        command.Options.Add(limit);
        command.Options.Add(sort);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var config = await host.GetConfigAsync(cancellationToken).ConfigureAwait(false);
            var catalog = await host.GetServiceAsync<SessionCatalog>(cancellationToken).ConfigureAwait(false);

            var filter = filters.Build(parseResult, parseResult.GetValue(limit), ParseSort(parseResult.GetValue(sort)));
            var mode = filters.IsLive(parseResult) ? QueryMode.Live : QueryMode.Indexed;

            IReadOnlyList<SessionSummary> sessions;
            try
            {
                sessions = await catalog.ListAsync(filter, mode, cancellationToken).ConfigureAwait(false);
            }
            catch (IndexNotBuiltException)
            {
                // Listing does not require an index, so falling back keeps a first run useful
                // before anyone has run `retrace index build`.
                sessions = await catalog.ListAsync(filter, QueryMode.Live, cancellationToken).ConfigureAwait(false);
            }

            WritePendingNotice(stdout, catalog, parseResult);
            return WriteSessions(stdout, sessions, parseResult, config);
        });

        return command;
    }

    private static Command BuildSearchCommand(RetraceHost host, TextWriter stdout)
    {
        var filters = new FilterOptions();
        var query = new Argument<string>("query") { Description = "What to search for. Quote a phrase for an exact match." };
        var top = new Option<int>("--top", "-n") { Description = "Maximum results.", DefaultValueFactory = _ => 0 };
        var snippets = new Option<int?>("--snippets") { Description = "Snippets to show per result. 0 shows none, for a compact one-line-per-hit listing." };
        var lexicalOnly = new Option<bool>("--lexical-only") { Description = "Keyword matching only." };
        var semanticOnly = new Option<bool>("--semantic-only") { Description = "Vector similarity only." };
        var deep = new Option<bool>("--deep") { Description = "Score every chunk vector instead of a session shortlist. Slower, higher recall." };
        var scores = new Option<bool>("--scores") { Description = "Show fused and per-signal scores." };

        var command = new Command("search", "Search session content across every source.");
        command.Arguments.Add(query);
        filters.AddTo(command);
        command.Options.Add(top);
        command.Options.Add(snippets);
        command.Options.Add(lexicalOnly);
        command.Options.Add(semanticOnly);
        command.Options.Add(deep);
        command.Options.Add(scores);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var config = await host.GetConfigAsync(cancellationToken).ConfigureAwait(false);
            var catalog = await host.GetServiceAsync<SessionCatalog>(cancellationToken).ConfigureAwait(false);

            var requestedTop = parseResult.GetValue(top);
            var requestedSnippets = parseResult.GetValue(snippets);

            var mode = (parseResult.GetValue(lexicalOnly), parseResult.GetValue(semanticOnly)) switch
            {
                (true, true) => throw new RetraceException("--lexical-only and --semantic-only are mutually exclusive."),
                (true, false) => SearchMode.Lexical,
                (false, true) => SearchMode.Semantic,
                _ => ParseSearchMode(config.Search.Mode)
            };

            var searchQuery = new SearchQuery
            {
                Text = parseResult.GetValue(query) ?? string.Empty,
                Filter = filters.Build(parseResult, limit: 0, SessionSortOrder.Recent),
                Top = requestedTop > 0 ? requestedTop : config.Search.DefaultTop,
                SnippetsPerSession = requestedSnippets ?? config.Search.SnippetsPerSession,
                Mode = mode,
                Deep = parseResult.GetValue(deep)
            };

            var hits = await catalog
                .SearchAsync(searchQuery, filters.IsLive(parseResult) ? QueryMode.Live : QueryMode.Indexed, cancellationToken)
                .ConfigureAwait(false);

            switch (CommonOptions.GetFormat(parseResult))
            {
                case OutputFormat.Json:
                    stdout.WriteLine(JsonSerializer.Serialize(hits.Select(ToJsonHit), RetraceJson.IndentedOptions));
                    break;

                case OutputFormat.Ndjson:
                    foreach (var hit in hits)
                    {
                        stdout.WriteLine(JsonSerializer.Serialize(ToJsonHit(hit), RetraceJson.Options));
                    }

                    break;

                default:
                    TextRenderer.RenderSearchHits(
                        stdout,
                        hits,
                        config.Output.DateFormat.Equals("relative", StringComparison.OrdinalIgnoreCase),
                        parseResult.GetValue(scores));
                    break;
            }

            WritePendingNotice(stdout, catalog, parseResult);
            return hits.Count > 0 ? 0 : 1;
        });

        return command;
    }

    // ---- show / export / files -------------------------------------------------------------

    private static Command BuildShowCommand(RetraceHost host, TextWriter stdout)
    {
        var identifier = new Argument<string>("session") { Description = "Session URI, native id, or unambiguous id prefix." };
        var turns = new Option<string?>("--turns") { Description = "Turn range, such as 0-20, 5-, or -10." };
        var withTools = new Option<bool>("--tools") { Description = "Include captured tool output." };
        var withReasoning = new Option<bool>("--reasoning") { Description = "Include model reasoning." };
        var withDiffs = new Option<bool>("--diffs") { Description = "Include unified diffs on edits." };
        var maxChars = new Option<int>("--max-chars") { Description = "Approximate character budget. 0 means unlimited.", DefaultValueFactory = _ => 0 };
        var maxToolChars = new Option<int>("--max-tool-chars") { Description = "Truncate each tool output to this length.", DefaultValueFactory = _ => 2000 };

        var command = new Command("show", "Print a session transcript.");
        command.Arguments.Add(identifier);
        command.Options.Add(turns);
        command.Options.Add(withTools);
        command.Options.Add(withReasoning);
        command.Options.Add(withDiffs);
        command.Options.Add(maxChars);
        command.Options.Add(maxToolChars);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var catalog = await host.GetServiceAsync<SessionCatalog>(cancellationToken).ConfigureAwait(false);
            var (from, to) = ParseTurnRange(parseResult.GetValue(turns));

            var options = new TranscriptOptions
            {
                IncludeToolOutput = parseResult.GetValue(withTools),
                IncludeReasoning = parseResult.GetValue(withReasoning),
                IncludeDiffs = parseResult.GetValue(withDiffs),
                FromTurn = from,
                ToTurn = to,
                MaxCharacters = parseResult.GetValue(maxChars),
                MaxToolOutputCharacters = parseResult.GetValue(maxToolChars)
            };

            var transcript = await catalog
                .GetTranscriptAsync(parseResult.GetValue(identifier)!, options, cancellationToken)
                .ConfigureAwait(false);

            switch (CommonOptions.GetFormat(parseResult))
            {
                case OutputFormat.Json or OutputFormat.Ndjson:
                    stdout.WriteLine(JsonSerializer.Serialize(ToJsonTranscript(transcript), RetraceJson.IndentedOptions));
                    break;

                case OutputFormat.Markdown:
                    stdout.Write(TextRenderer.RenderTranscriptMarkdown(transcript, options.IncludeToolOutput));
                    break;

                default:
                    TextRenderer.RenderTranscript(stdout, transcript, options.IncludeToolOutput, options.MaxToolOutputCharacters);
                    break;
            }

            return 0;
        });

        return command;
    }

    private static Command BuildExportCommand(RetraceHost host, TextWriter stdout, TextWriter stderr)
    {
        var identifier = new Argument<string>("session") { Description = "Session URI, native id, or unambiguous id prefix." };
        var format = new Option<string>("--format", "-f") { Description = "Export format: markdown (default) or json.", DefaultValueFactory = _ => "markdown" };
        var output = new Option<string?>("--out") { Description = "Write to this file instead of standard output." };
        var noTools = new Option<bool>("--no-tools") { Description = "Exclude tool output from the export." };

        var command = new Command("export", "Export a full session transcript.");
        command.Arguments.Add(identifier);
        command.Options.Add(format);
        command.Options.Add(output);
        command.Options.Add(noTools);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var catalog = await host.GetServiceAsync<SessionCatalog>(cancellationToken).ConfigureAwait(false);
            var includeTools = !parseResult.GetValue(noTools);

            // Export means everything by default; a truncated export is a silent data-loss bug
            // waiting to be discovered later.
            var options = TranscriptOptions.Full with { IncludeToolOutput = includeTools };

            var transcript = await catalog
                .GetTranscriptAsync(parseResult.GetValue(identifier)!, options, cancellationToken)
                .ConfigureAwait(false);

            var isJson = parseResult.GetValue(format)?.Equals("json", StringComparison.OrdinalIgnoreCase) == true;
            var content = isJson
                ? JsonSerializer.Serialize(ToJsonTranscript(transcript), RetraceJson.IndentedOptions)
                : TextRenderer.RenderTranscriptMarkdown(transcript, includeTools);

            var path = parseResult.GetValue(output);
            if (string.IsNullOrWhiteSpace(path))
            {
                stdout.Write(content);
                return 0;
            }

            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
            stderr.WriteLine(ConsoleStyle.Dim($"Wrote {new FileInfo(path).Length:N0} bytes to {path}"));
            return 0;
        });

        return command;
    }

    private static Command BuildFilesCommand(RetraceHost host, TextWriter stdout)
    {
        var identifier = new Argument<string>("session") { Description = "Session URI, native id, or unambiguous id prefix." };

        var command = new Command("files", "List the files a session touched.");
        command.Arguments.Add(identifier);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var catalog = await host.GetServiceAsync<SessionCatalog>(cancellationToken).ConfigureAwait(false);
            var files = await catalog.GetFilesAsync(parseResult.GetValue(identifier)!, cancellationToken).ConfigureAwait(false);

            if (CommonOptions.GetFormat(parseResult) != OutputFormat.Text)
            {
                stdout.WriteLine(JsonSerializer.Serialize(
                    files.Select(file => new { path = file.Path, tool = file.Tool, turnIndex = file.TurnIndex }),
                    RetraceJson.IndentedOptions));
                return 0;
            }

            if (files.Count == 0)
            {
                stdout.WriteLine(ConsoleStyle.Dim("No files recorded for this session."));
                return 0;
            }

            foreach (var file in files)
            {
                var tool = file.Tool is { Length: > 0 } ? ConsoleStyle.Dim($"  ({file.Tool})") : string.Empty;
                stdout.WriteLine($"{file.Path}{tool}");
            }

            return 0;
        });

        return command;
    }

    // ---- resume ----------------------------------------------------------------------------

    private static Command BuildResumeCommand(RetraceHost host, TextWriter stdout, TextWriter stderr)
    {
        var identifier = new Argument<string>("session") { Description = "Session URI, native id, or unambiguous id prefix." };
        var exec = new Option<bool>("--exec") { Description = "Run the command instead of printing it." };
        var fork = new Option<bool>("--fork") { Description = "Branch the session instead of continuing it, where supported." };
        var agent = new Option<string?>("--agent") { Description = "Override the agent the resumed session starts with." };
        var model = new Option<string?>("--model") { Description = "Override the model the resumed session starts with." };

        var command = new Command("resume", "Show (or run) the command that reopens a session in its own harness.");
        command.Arguments.Add(identifier);
        command.Options.Add(exec);
        command.Options.Add(fork);
        command.Options.Add(agent);
        command.Options.Add(model);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var catalog = await host.GetServiceAsync<SessionCatalog>(cancellationToken).ConfigureAwait(false);
            var target = parseResult.GetValue(identifier)!;

            var summary = await catalog.ResolveAsync(target, cancellationToken).ConfigureAwait(false);
            var resume = await catalog.GetResumeCommandAsync(target, new ResumeOptions
            {
                Fork = parseResult.GetValue(fork),
                Agent = parseResult.GetValue(agent),
                Model = parseResult.GetValue(model)
            }, cancellationToken).ConfigureAwait(false);

            if (CommonOptions.GetFormat(parseResult) != OutputFormat.Text)
            {
                stdout.WriteLine(JsonSerializer.Serialize(new
                {
                    session = summary.Ref.Uri,
                    title = summary.Title,
                    executable = resume.Executable,
                    arguments = resume.Arguments,
                    workingDirectory = resume.WorkingDirectory,
                    command = resume.DisplayCommand,
                    restoresConversation = resume.RestoresConversation,
                    notes = resume.Notes
                }, RetraceJson.IndentedOptions));

                return 0;
            }

            if (!parseResult.GetValue(exec))
            {
                stdout.WriteLine($"{ConsoleStyle.Dim("Session :")} {ConsoleStyle.Cyan(summary.Ref.Uri)}");
                stdout.WriteLine($"{ConsoleStyle.Dim("Title   :")} {summary.Title}");

                if (resume.WorkingDirectory is { Length: > 0 } workdir)
                {
                    stdout.WriteLine($"{ConsoleStyle.Dim("Workdir :")} {workdir}");
                }

                stdout.WriteLine($"{ConsoleStyle.Dim("Command :")} {ConsoleStyle.Bold(resume.DisplayCommand)}");

                if (!resume.RestoresConversation)
                {
                    stdout.WriteLine();
                    stdout.WriteLine(ConsoleStyle.Yellow("Note: this does not restore the conversation."));
                }

                if (resume.Notes is { Length: > 0 } notes)
                {
                    stdout.WriteLine(ConsoleStyle.Dim(notes));
                }

                return 0;
            }

            var startInfo = ResumeLauncher.Build(resume);
            stderr.WriteLine(ConsoleStyle.Dim($"Running: {resume.DisplayCommand}"));
            if (startInfo.WorkingDirectory is { Length: > 0 } cwd)
            {
                stderr.WriteLine(ConsoleStyle.Dim($"     in: {cwd}"));
            }

            return await ResumeLauncher.RunAsync(resume, cancellationToken).ConfigureAwait(false);
        });

        return command;
    }

    // ---- tag -------------------------------------------------------------------------------

    private static Command BuildTagCommand(RetraceHost host, TextWriter stdout)
    {
        var identifier = new Argument<string?>("session")
        {
            Description = "Session to tag. Omit to list every tag in use.",
            Arity = ArgumentArity.ZeroOrOne
        };

        var add = new Option<string[]>("--add") { Description = "Tags to add. Repeatable.", AllowMultipleArgumentsPerToken = true };
        var remove = new Option<string[]>("--remove") { Description = "Tags to remove. Repeatable.", AllowMultipleArgumentsPerToken = true };

        var command = new Command("tag", "Add, remove, or list session tags.");
        command.Arguments.Add(identifier);
        command.Options.Add(add);
        command.Options.Add(remove);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var tags = await host.GetServiceAsync<TagStore>(cancellationToken).ConfigureAwait(false);
            var target = parseResult.GetValue(identifier);

            if (string.IsNullOrWhiteSpace(target))
            {
                var all = await tags.ListAllAsync(cancellationToken).ConfigureAwait(false);

                if (CommonOptions.GetFormat(parseResult) != OutputFormat.Text)
                {
                    stdout.WriteLine(JsonSerializer.Serialize(
                        all.Select(entry => new { tag = entry.Tag, count = entry.Count }),
                        RetraceJson.IndentedOptions));
                    return 0;
                }

                if (all.Count == 0)
                {
                    stdout.WriteLine(ConsoleStyle.Dim("No tags yet. Add one with `retrace tag <session> --add <name>`."));
                    return 0;
                }

                foreach (var (tag, count) in all)
                {
                    stdout.WriteLine($"{tag.PadRight(24)} {ConsoleStyle.Dim($"{count} session(s)")}");
                }

                return 0;
            }

            var catalog = await host.GetServiceAsync<SessionCatalog>(cancellationToken).ConfigureAwait(false);
            var summary = await catalog.ResolveAsync(target, cancellationToken).ConfigureAwait(false);

            var toAdd = parseResult.GetValue(add) ?? [];
            var toRemove = parseResult.GetValue(remove) ?? [];

            if (toAdd.Length > 0)
            {
                await tags.AddAsync(summary.Ref.Uri, toAdd, origin: "manual", cancellationToken).ConfigureAwait(false);
            }

            if (toRemove.Length > 0)
            {
                await tags.RemoveAsync(summary.Ref.Uri, toRemove, cancellationToken).ConfigureAwait(false);
            }

            var current = await tags.GetAsync(summary.Ref.Uri, cancellationToken).ConfigureAwait(false);

            if (CommonOptions.GetFormat(parseResult) != OutputFormat.Text)
            {
                stdout.WriteLine(JsonSerializer.Serialize(new
                {
                    session = summary.Ref.Uri,
                    tags = current.Select(tag => new { tag = tag.Tag, origin = tag.Origin })
                }, RetraceJson.IndentedOptions));
                return 0;
            }

            stdout.WriteLine($"{ConsoleStyle.Cyan(summary.Ref.Uri)}  {summary.Title}");
            stdout.WriteLine(current.Count == 0
                ? ConsoleStyle.Dim("  (no tags)")
                : "  " + string.Join(" ", current.Select(tag => "#" + tag.Tag)));

            return 0;
        });

        return command;
    }

    // ---- index -----------------------------------------------------------------------------

    private static Command BuildIndexCommand(RetraceHost host, TextWriter stdout, TextWriter stderr)
    {
        var sourceOption = new Option<string[]>("--source", "-s")
        {
            Description = "Restrict to these sources. Repeatable.",
            AllowMultipleArgumentsPerToken = true
        };

        var force = new Option<bool>("--force") { Description = "Re-read everything, ignoring watermarks and content hashes." };
        var prune = new Option<bool>("--prune") { Description = "Remove indexed sessions that no longer exist in their source." };
        var noEmbed = new Option<bool>("--no-embed") { Description = "Skip vectors. Much faster; search runs keyword-only." };

        var backfill = new Option<bool>("--backfill")
        {
            Description = "Also add vectors to sessions that are already indexed but have none, such as those picked up by an "
                + "automatic refresh. Only the sessions actually missing vectors are re-read, so this is far cheaper than --force. "
                + "Scope it with --source or --since: a source left keyword-only on purpose can represent hours of work."
        };

        var since = new Option<string?>("--since")
        {
            Description = "Only index sessions updated since this point, as a date (2026-01-31) or a duration (90d, 12h). "
                + "On its own it only narrows which sessions are considered; an already-indexed session is still skipped, so pair it "
                + "with --backfill to add missing vectors or --force to re-read regardless. "
                + "A scoped build does not advance the watermark, so a later unscoped refresh still backfills what was excluded."
        };

        var until = new Option<string?>("--until")
        {
            Description = "Only index sessions updated before this point. Same formats as --since."
        };

        var build = new Command("build", "Build or update the search index.");
        build.Options.Add(sourceOption);
        build.Options.Add(force);
        build.Options.Add(prune);
        build.Options.Add(noEmbed);
        build.Options.Add(backfill);
        build.Options.Add(since);
        build.Options.Add(until);
        build.SetAction((parseResult, cancellationToken) => RunBuildAsync(parseResult, cancellationToken));

        var refresh = new Command("refresh", "Update the index with sessions that changed since the last run.");
        refresh.Options.Add(sourceOption);
        refresh.Options.Add(backfill);
        refresh.Options.Add(since);
        refresh.Options.Add(until);
        refresh.SetAction((parseResult, cancellationToken) => RunBuildAsync(parseResult, cancellationToken));

        var status = new Command("status", "Show index size, freshness, and per-source state.");
        status.SetAction(async (parseResult, cancellationToken) =>
        {
            var searcher = await host.GetServiceAsync<IndexSearcher>(cancellationToken).ConfigureAwait(false);
            var state = await searcher.GetStatusAsync(cancellationToken).ConfigureAwait(false);

            if (CommonOptions.GetFormat(parseResult) != OutputFormat.Text)
            {
                stdout.WriteLine(JsonSerializer.Serialize(state, RetraceJson.IndentedOptions));
                return 0;
            }

            if (!state.Exists)
            {
                stdout.WriteLine(ConsoleStyle.Yellow($"No index at {state.Path}."));
                stdout.WriteLine(ConsoleStyle.Dim("Run `retrace index build` to create it."));
                return 1;
            }

            WriteField(stdout, "path", state.Path);
            WriteField(stdout, "size", $"{state.SizeBytes / 1024.0 / 1024.0:F1} MB");
            WriteField(stdout, "sessions", state.SessionCount.ToString("N0", CultureInfo.InvariantCulture));
            WriteField(stdout, "chunks", state.ChunkCount.ToString("N0", CultureInfo.InvariantCulture));
            WriteField(stdout, "vectors", state.VectorCount.ToString("N0", CultureInfo.InvariantCulture));
            WriteField(stdout, "model", state.EmbeddingModel ?? ConsoleStyle.Dim("(keyword-only)"));
            WriteField(stdout, "refreshed", state.LastRefresh?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "never");
            stdout.WriteLine();

            foreach (var source in state.Sources)
            {
                stdout.WriteLine($"{ConsoleStyle.Bold(source.SourceId.PadRight(16))} "
                    + $"{source.SessionCount,7:N0} session(s)  {source.ChunkCount,9:N0} chunk(s)  "
                    + ConsoleStyle.Dim(source.LastIndexed.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
            }

            return 0;
        });

        var clear = new Command("clear", "Delete the index.");
        clear.SetAction(async (parseResult, cancellationToken) =>
        {
            var searcher = await host.GetServiceAsync<IndexSearcher>(cancellationToken).ConfigureAwait(false);

            if (!File.Exists(searcher.IndexPath))
            {
                stdout.WriteLine(ConsoleStyle.Dim("No index to delete."));
                return 0;
            }

            // WAL and shared-memory sidecars have to go too; leaving them behind makes the next
            // build reopen a database that SQLite considers partially present.
            foreach (var suffix in (string[])["", "-wal", "-shm"])
            {
                var path = searcher.IndexPath + suffix;
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            stdout.WriteLine($"Deleted {searcher.IndexPath}");
            return 0;
        });

        return new Command("index", "Manage the local search index.") { build, refresh, status, clear };

        async Task<int> RunBuildAsync(ParseResult parseResult, CancellationToken cancellationToken)
        {
            var catalog = await host.GetServiceAsync<SessionCatalog>(cancellationToken).ConfigureAwait(false);
            var quiet = parseResult.GetValue(CommonOptions.Quiet);

            var progress = quiet
                ? null
                : new Progress<IndexProgress>(report =>
                    stderr.WriteLine(ConsoleStyle.Dim($"  {report.SourceId}: {report.Message}")));

            var options = new IndexBuildOptions
            {
                SourceIds = parseResult.GetValue(sourceOption) ?? [],
                Force = parseResult.GetValue(force),
                Prune = parseResult.GetValue(prune),
                Embed = !parseResult.GetValue(noEmbed),
                Backfill = parseResult.GetValue(backfill),
                Since = FilterOptions.ParseTimeBound(parseResult.GetValue(since), isLowerBound: true),
                Until = FilterOptions.ParseTimeBound(parseResult.GetValue(until), isLowerBound: false),
                Progress = progress
            };

            var result = await catalog.RefreshIndexAsync(options, cancellationToken).ConfigureAwait(false);

            if (CommonOptions.GetFormat(parseResult) != OutputFormat.Text)
            {
                stdout.WriteLine(JsonSerializer.Serialize(result, RetraceJson.IndentedOptions));
                return 0;
            }

            foreach (var source in result.Sources)
            {
                if (source.SkipReason is { Length: > 0 } reason)
                {
                    stdout.WriteLine($"{ConsoleStyle.Bold(source.SourceId.PadRight(16))} {ConsoleStyle.Yellow("skipped")}  {ConsoleStyle.Dim(reason)}");
                    continue;
                }

                stdout.WriteLine($"{ConsoleStyle.Bold(source.SourceId.PadRight(16))} "
                    + $"{source.SessionsIndexed,6:N0} indexed  {source.SessionsSkipped,6:N0} unchanged  "
                    + $"{source.SessionsRemoved,5:N0} removed  {source.ChunksIndexed,8:N0} chunk(s)  {source.VectorsComputed,8:N0} vector(s)");
            }

            stdout.WriteLine();
            stdout.WriteLine(ConsoleStyle.Dim(
                $"{result.SessionsIndexed:N0} session(s) in {result.Duration.TotalSeconds:F1}s"
                + (result.EmbeddingModel is null ? " (keyword-only)" : $" using {result.EmbeddingModel}")));

            if (options.IsScoped)
            {
                // Say this out loud. A scoped build leaves the index deliberately incomplete, and
                // the watermark is left alone so the gap can still be closed later; a user who does
                // not know that would reasonably assume the source was fully indexed.
                stdout.WriteLine(ConsoleStyle.Dim(
                    "Scoped build: sessions outside the window were not indexed, and the watermark was left unchanged "
                    + "so a later `retrace index build` still picks them up."));
            }

            return 0;
        }
    }

    // ---- deps ------------------------------------------------------------------------------

    private static Command BuildDepsCommand(RetraceHost host, TextWriter stdout, TextWriter stderr)
    {
        var target = new Argument<string?>("target")
        {
            Description = "What to install. Currently only 'onnx' (the embedding model).",
            Arity = ArgumentArity.ZeroOrOne
        };

        var modelOption = new Option<string?>("--model")
        {
            Description = $"Model id to install. Known: {string.Join(", ", KnownEmbeddingModels.Ids)}."
        };

        var force = new Option<bool>("--force") { Description = "Re-download even when files are already present." };

        var install = new Command("install", "Download the local embedding model.");
        install.Arguments.Add(target);
        install.Options.Add(modelOption);
        install.Options.Add(force);

        install.SetAction(async (parseResult, cancellationToken) =>
        {
            var requested = parseResult.GetValue(target);
            if (!string.IsNullOrWhiteSpace(requested)
                && !requested.Equals("onnx", StringComparison.OrdinalIgnoreCase)
                && !requested.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                throw new RetraceException($"Unknown dependency target '{requested}'. The only target is 'onnx'.");
            }

            var provisioner = await host.GetServiceAsync<EmbeddingModelProvisioner>(cancellationToken).ConfigureAwait(false);
            var quiet = parseResult.GetValue(CommonOptions.Quiet);

            var progress = quiet ? null : new Progress<string>(message => stderr.WriteLine(ConsoleStyle.Dim("  " + message)));

            var result = await provisioner
                .InstallAsync(parseResult.GetValue(modelOption), parseResult.GetValue(force), progress, cancellationToken)
                .ConfigureAwait(false);

            if (CommonOptions.GetFormat(parseResult) != OutputFormat.Text)
            {
                stdout.WriteLine(JsonSerializer.Serialize(result, RetraceJson.IndentedOptions));
                return 0;
            }

            stdout.WriteLine($"{result.ModelId}: {(result.Downloaded ? ConsoleStyle.Green("installed") : ConsoleStyle.Dim("already present"))}");
            stdout.WriteLine(ConsoleStyle.Dim($"  {result.Directory}"));

            foreach (var file in result.Files)
            {
                stdout.WriteLine(ConsoleStyle.Dim("  " + file));
            }

            return 0;
        });

        var status = new Command("status", "Show which embedding models are installed.");
        status.SetAction(async (parseResult, cancellationToken) =>
        {
            var provisioner = await host.GetServiceAsync<EmbeddingModelProvisioner>(cancellationToken).ConfigureAwait(false);
            var factory = await host.GetServiceAsync<IEmbeddingProviderFactory>(cancellationToken).ConfigureAwait(false);

            var models = KnownEmbeddingModels.All
                .Select(model => new
                {
                    id = model.Id,
                    active = model.Id.Equals(factory.ModelId, StringComparison.OrdinalIgnoreCase),
                    installed = provisioner.IsInstalled(model.Id),
                    dimensions = model.Dimensions,
                    approximateSizeMb = model.ApproximateSizeMegabytes,
                    directory = provisioner.GetModelDirectory(model.Id)
                })
                .ToArray();

            if (CommonOptions.GetFormat(parseResult) != OutputFormat.Text)
            {
                stdout.WriteLine(JsonSerializer.Serialize(models, RetraceJson.IndentedOptions));
                return 0;
            }

            foreach (var model in models)
            {
                var marker = model.active ? ConsoleStyle.Cyan("*") : " ";
                var state = model.installed ? ConsoleStyle.Green("installed") : ConsoleStyle.Dim("not installed");
                stdout.WriteLine($"{marker} {model.id.PadRight(28)} {state}  {ConsoleStyle.Dim($"{model.dimensions}d, ~{model.approximateSizeMb} MB")}");
            }

            return 0;
        });

        return new Command("deps", "Manage optional local dependencies.") { install, status };
    }

    // ---- config ----------------------------------------------------------------------------

    private static Command BuildConfigCommand(RetraceHost host, TextWriter stdout)
    {
        var path = new Command("path", "Print the resolved config file path.");
        path.SetAction(_ =>
        {
            stdout.WriteLine(host.Store.ConfigPath);
            return 0;
        });

        var init = new Command("init", "Create retrace.json with every option at its default value.");
        var forceInit = new Option<bool>("--force") { Description = "Overwrite an existing file." };
        init.Options.Add(forceInit);
        init.SetAction(async (parseResult, cancellationToken) =>
        {
            if (host.Store.Exists && !parseResult.GetValue(forceInit))
            {
                stdout.WriteLine(ConsoleStyle.Yellow($"{host.Store.ConfigPath} already exists. Pass --force to overwrite."));
                return 1;
            }

            await host.Store.SaveAsync(new RetraceConfig(), cancellationToken).ConfigureAwait(false);
            stdout.WriteLine($"Wrote {host.Store.ConfigPath}");
            return 0;
        });

        var list = new Command("list", "List every configuration key and its current value.");
        list.SetAction(async (parseResult, cancellationToken) =>
        {
            var config = await host.GetConfigAsync(cancellationToken).ConfigureAwait(false);
            var flat = ConfigStore.Flatten(config);

            if (CommonOptions.GetFormat(parseResult) != OutputFormat.Text)
            {
                stdout.WriteLine(JsonSerializer.Serialize(flat, RetraceJson.IndentedOptions));
                return 0;
            }

            var width = flat.Keys.Max(key => key.Length);
            foreach (var (key, value) in flat)
            {
                stdout.WriteLine($"{key.PadRight(width)}  {ConsoleStyle.Dim(value ?? "null")}");
            }

            return 0;
        });

        var getKey = new Argument<string>("key") { Description = "Dotted configuration key, such as search.rrfK." };
        var get = new Command("get", "Print one configuration value.");
        get.Arguments.Add(getKey);
        get.SetAction(async (parseResult, cancellationToken) =>
        {
            var config = await host.GetConfigAsync(cancellationToken).ConfigureAwait(false);
            var key = parseResult.GetValue(getKey)!;

            if (!ConfigStore.TryGet(config, key, out var value))
            {
                throw new RetraceException($"Unknown configuration key '{key}'. Run `retrace config list` to see valid keys.");
            }

            stdout.WriteLine(value ?? "null");
            return 0;
        });

        var setKey = new Argument<string>("key") { Description = "Dotted configuration key." };
        var setValue = new Argument<string>("value") { Description = "New value. Use 'null' to clear an optional setting." };
        var set = new Command("set", "Change one configuration value.");
        set.Arguments.Add(setKey);
        set.Arguments.Add(setValue);
        set.SetAction(async (parseResult, cancellationToken) =>
        {
            var config = await host.GetConfigAsync(cancellationToken).ConfigureAwait(false);
            var key = parseResult.GetValue(setKey)!;

            var updated = ConfigStore.Set(config, key, parseResult.GetValue(setValue));
            await host.Store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);

            ConfigStore.TryGet(updated, key, out var stored);
            stdout.WriteLine($"{key} = {stored ?? "null"}");
            return 0;
        });

        return new Command("config", "Read and write retrace.json.") { path, init, list, get, set };
    }

    // ---- mcp -------------------------------------------------------------------------------

    private static Command BuildMcpCommand(RetraceHost host, TextWriter stderr)
    {
        var transport = new Option<string>("--transport")
        {
            Description = "MCP transport: stdio (default) or http.",
            DefaultValueFactory = _ => "stdio"
        };

        var port = new Option<int>("--port")
        {
            Description = "Port for the http transport.",
            DefaultValueFactory = _ => 8770
        };

        var serve = new Command("serve", "Run Retrace as an MCP server.");
        serve.Options.Add(transport);
        serve.Options.Add(port);
        serve.SetAction(async (parseResult, cancellationToken) =>
            await McpHost.RunAsync(
                host,
                parseResult.GetValue(transport) ?? "stdio",
                parseResult.GetValue(port),
                parseResult.GetValue(CommonOptions.Verbose),
                stderr,
                cancellationToken).ConfigureAwait(false));

        return new Command("mcp", "Model Context Protocol server.") { serve };
    }

    // ---- shared helpers --------------------------------------------------------------------

    /// <summary>
    /// Tells the user when the automatic top-up could not bring the index fully current.
    /// </summary>
    /// <remarks>
    /// The top-up is deliberately time-boxed so a query is never slow. The cost of that choice is
    /// that results can be behind, and the only honest thing to do is say so rather than let the
    /// user infer it from a session that mysteriously is not there.
    /// </remarks>
    private static void WritePendingNotice(TextWriter writer, SessionCatalog catalog, ParseResult parseResult)
    {
        if (catalog.PendingSources.Count == 0
            || parseResult.GetValue(CommonOptions.Quiet)
            || CommonOptions.GetFormat(parseResult) != OutputFormat.Text)
        {
            return;
        }

        writer.WriteLine(ConsoleStyle.Dim(
            $"Index is behind for {string.Join(", ", catalog.PendingSources)} — run `retrace index refresh` to catch up."));
    }

    private static int WriteSessions(TextWriter stdout, IReadOnlyList<SessionSummary> sessions, ParseResult parseResult, RetraceConfig config)
    {
        switch (CommonOptions.GetFormat(parseResult))
        {
            case OutputFormat.Json:
                stdout.WriteLine(JsonSerializer.Serialize(sessions.Select(ToJsonSession), RetraceJson.IndentedOptions));
                break;

            case OutputFormat.Ndjson:
                foreach (var session in sessions)
                {
                    stdout.WriteLine(JsonSerializer.Serialize(ToJsonSession(session), RetraceJson.Options));
                }

                break;

            default:
                TextRenderer.RenderSessions(
                    stdout,
                    sessions,
                    config.Output.PreviewCharacters,
                    config.Output.DateFormat.Equals("relative", StringComparison.OrdinalIgnoreCase));
                break;
        }

        return sessions.Count > 0 ? 0 : 1;
    }

    /// <summary>
    /// Whether a directory exists or can be created. `doctor` should surface a data directory that
    /// cannot be written before a build fails on it much later.
    /// </summary>
    private static bool CanUseDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            return true;
        }

        try
        {
            Directory.CreateDirectory(path);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void WriteField(TextWriter writer, string label, string value) =>
        writer.WriteLine($"{ConsoleStyle.Dim((label + ":").PadRight(18))} {value}");

    private static SessionSortOrder ParseSort(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "created" => SessionSortOrder.Created,
        "size" => SessionSortOrder.Size,
        _ => SessionSortOrder.Recent
    };

    private static SearchMode ParseSearchMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "lexical" or "keyword" => SearchMode.Lexical,
        "semantic" or "vector" => SearchMode.Semantic,
        _ => SearchMode.Hybrid
    };

    /// <summary>Parses <c>0-20</c>, <c>5-</c>, <c>-10</c>, or a bare turn number.</summary>
    private static (int? From, int? To) ParseTurnRange(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (null, null);
        }

        var trimmed = value.Trim();
        var separator = trimmed.IndexOf('-', StringComparison.Ordinal);

        if (separator < 0)
        {
            return int.TryParse(trimmed, out var single)
                ? (single, single)
                : throw new RetraceException($"Could not parse turn range '{value}'. Try 0-20, 5-, -10, or a single number.");
        }

        var left = trimmed[..separator].Trim();
        var right = trimmed[(separator + 1)..].Trim();

        int? from = left.Length == 0 ? null : int.TryParse(left, out var parsedFrom) ? parsedFrom : throw Invalid();
        int? to = right.Length == 0 ? null : int.TryParse(right, out var parsedTo) ? parsedTo : throw Invalid();

        return (from, to);

        RetraceException Invalid() => new($"Could not parse turn range '{value}'. Try 0-20, 5-, -10, or a single number.");
    }

    /// <summary>
    /// Reads an option's value straight from the raw argument array, for the few settings that must
    /// be known before the command tree is built.
    /// </summary>
    private static string? ExtractOptionValue(string[] args, string name)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index].Equals(name, StringComparison.Ordinal) && index + 1 < args.Length)
            {
                return args[index + 1];
            }

            if (args[index].StartsWith(name + "=", StringComparison.Ordinal))
            {
                return args[index][(name.Length + 1)..];
            }
        }

        return null;
    }

    internal static object ToJsonSession(SessionSummary session) => new
    {
        uri = session.Ref.Uri,
        source = session.Ref.SourceId,
        id = session.Ref.NativeId,
        title = session.Title,
        preview = session.Preview,
        workspace = session.Workspace?.Path,
        repository = session.Workspace?.Repository,
        branch = session.Workspace?.Branch,
        agent = session.Agent,
        models = session.Models,
        createdAt = session.CreatedAt,
        updatedAt = session.UpdatedAt,
        messageCount = session.Stats.MessageCount,
        toolCallCount = session.Stats.ToolCallCount,
        totalTokens = session.Stats.TotalTokens,
        cost = session.Stats.Cost,
        filesChanged = session.Stats.FilesChanged,
        tags = session.Tags,
        isArchived = session.IsArchived
    };

    internal static object ToJsonHit(SearchHit hit) => new
    {
        session = ToJsonSession(hit.Session),
        score = hit.Score,
        lexicalScore = hit.LexicalScore,
        semanticScore = hit.SemanticScore,
        snippets = hit.Snippets.Select(snippet => new
        {
            role = snippet.Role.ToString(),
            turnIndex = snippet.TurnIndex,
            timestamp = snippet.Timestamp,
            text = snippet.Text
        })
    };

    internal static object ToJsonTranscript(SessionTranscript transcript) => new
    {
        session = ToJsonSession(transcript.Summary),
        totalTurns = transcript.TotalTurns,
        nextTurnIndex = transcript.NextTurnIndex,
        isTruncated = transcript.IsTruncated,
        turns = transcript.Turns.Select(turn => new
        {
            index = turn.Index,
            role = turn.Role.ToString(),
            timestamp = turn.Timestamp,
            model = turn.Model,
            agent = turn.Agent,
            blocks = turn.Blocks.Select(ToJsonBlock)
        }),
        files = transcript.Files.Select(file => new { path = file.Path, tool = file.Tool }),
        references = transcript.References.Select(reference => new { type = reference.Type, value = reference.Value })
    };

    private static object ToJsonBlock(ContentBlock block) => block switch
    {
        TextBlock text => new { kind = "text", text = text.Text },
        ReasoningBlock reasoning => new { kind = "reasoning", text = reasoning.Text },
        ToolCallBlock tool => new
        {
            kind = "tool",
            tool = tool.ToolName,
            title = tool.Title,
            status = tool.Status,
            input = tool.InputJson,
            output = tool.Output
        },
        PatchBlock patch => new
        {
            kind = "patch",
            path = patch.Path,
            additions = patch.Additions,
            deletions = patch.Deletions,
            diff = patch.Diff
        },
        _ => new { kind = "unknown" }
    };
}
