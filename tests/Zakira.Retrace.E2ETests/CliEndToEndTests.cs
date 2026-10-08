using System.Text.Json;

namespace Zakira.Retrace.E2ETests;

/// <summary>
/// Drives the packaged binary the way a user and an MCP client actually do.
/// </summary>
/// <remarks>
/// These are the tests that would catch a regression the unit suites cannot see: a command that
/// throws before reaching its handler, an exit code that lies, JSON output that is not valid JSON,
/// or a log line escaping onto stdout and corrupting the MCP stream.
/// </remarks>
public sealed class CliEndToEndTests
{
    [Fact]
    public async Task Version_prints_a_bare_semantic_version()
    {
        using var runner = new RetraceRunner();

        var result = await runner.RunAsync("version");

        result.ExitCode.Should().Be(0);
        result.StandardOutput.Trim().Should().MatchRegex(@"^\d+\.\d+\.\d+");
    }

    [Fact]
    public async Task First_run_creates_the_config_file_with_every_option()
    {
        using var runner = new RetraceRunner();

        var result = await runner.RunAsync("info");

        result.ExitCode.Should().Be(0);
        File.Exists(runner.ConfigPath).Should().BeTrue("the first command that needs config should generate it");

        var json = await File.ReadAllTextAsync(runner.ConfigPath, TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);

        var root = document.RootElement;
        root.TryGetProperty("sources", out _).Should().BeTrue();
        root.TryGetProperty("index", out _).Should().BeTrue();
        root.TryGetProperty("search", out _).Should().BeTrue();
        root.TryGetProperty("embeddings", out _).Should().BeTrue();
        root.TryGetProperty("output", out _).Should().BeTrue();

        // Options whose default is null still have to be present, since the file doubles as the
        // documentation of what can be tuned.
        json.Should().Contain("\"dataDirectory\": null");
    }

    [Fact]
    public async Task Config_path_reports_the_overridden_location()
    {
        using var runner = new RetraceRunner();

        var result = await runner.RunAsync("config", "path");

        result.ExitCode.Should().Be(0);
        result.StandardOutput.Trim().Should().Be(runner.ConfigPath);
    }

    [Fact]
    public async Task Config_get_and_set_round_trip_through_the_file()
    {
        using var runner = new RetraceRunner();

        (await runner.RunAsync("config", "set", "search.rrfK", "77")).ExitCode.Should().Be(0);

        var get = await runner.RunAsync("config", "get", "search.rrfK");
        get.ExitCode.Should().Be(0);
        get.StandardOutput.Trim().Should().Be("77");
    }

    [Fact]
    public async Task Config_set_rejects_an_unknown_key_with_a_non_zero_exit_code()
    {
        using var runner = new RetraceRunner();

        var result = await runner.RunAsync("config", "set", "search.notAKey", "1");

        // A silent success here would let a typo become dead configuration the user never notices.
        result.ExitCode.Should().NotBe(0);
        result.StandardError.Should().Contain("Unknown configuration key");
        result.StandardError.Should().NotContain("Unhandled exception", "deliberate errors must not print a stack trace");
    }

    [Fact]
    public async Task Sources_emits_valid_json_for_every_source()
    {
        using var runner = new RetraceRunner();

        var result = await runner.RunAsync("sources", "--output", "json");

        result.ExitCode.Should().Be(0);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var sources = document.RootElement.EnumerateArray().ToArray();

        sources.Should().HaveCount(3);
        sources.Select(source => source.GetProperty("id").GetString())
            .Should().BeEquivalentTo("opencode", "copilot-cli", "copilot-vscode");

        // Availability depends on the machine, but the shape must not.
        sources.Should().AllSatisfy(source =>
        {
            source.TryGetProperty("available", out _).Should().BeTrue();
            source.TryGetProperty("capabilities", out _).Should().BeTrue();
        });
    }

    [Fact]
    public async Task Doctor_reports_checks_and_never_crashes()
    {
        using var runner = new RetraceRunner();

        var result = await runner.RunAsync("doctor", "--output", "json");

        // Exit code 1 just means a warning (for example, no embedding model yet), which is a
        // legitimate state on a fresh machine.
        result.ExitCode.Should().BeOneOf(0, 1);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var checks = document.RootElement.EnumerateArray().ToArray();

        checks.Should().NotBeEmpty();
        checks.Select(check => check.GetProperty("name").GetString()).Should().Contain("config");
    }

    [Fact]
    public async Task Search_without_an_index_explains_how_to_build_one()
    {
        using var runner = new RetraceRunner();

        var result = await runner.RunAsync("search", "anything");

        result.ExitCode.Should().NotBe(0);
        result.StandardError.Should().Contain("retrace index build");
        result.StandardError.Should().NotContain("Unhandled exception");
    }

    [Fact]
    public async Task Show_with_an_unknown_session_fails_with_a_clear_message()
    {
        using var runner = new RetraceRunner();

        var result = await runner.RunAsync("show", "definitely-not-a-real-session-id");

        result.ExitCode.Should().NotBe(0);
        result.StandardError.Should().Contain("No session matched");
        result.StandardError.Should().NotContain("Unhandled exception");
    }

    [Fact]
    public async Task Invalid_duration_is_rejected_with_guidance()
    {
        using var runner = new RetraceRunner();

        var result = await runner.RunAsync("list", "--since", "yesterday-ish");

        result.ExitCode.Should().NotBe(0);
        result.StandardError.Should().Contain("7d");
    }

    [Fact]
    public async Task Index_status_on_a_fresh_machine_says_so_instead_of_failing_obscurely()
    {
        using var runner = new RetraceRunner();

        var result = await runner.RunAsync("index", "status");

        result.ExitCode.Should().Be(1);
        result.StandardOutput.Should().Contain("retrace index build");
    }

    [Fact]
    public async Task Deps_status_lists_the_known_embedding_models()
    {
        using var runner = new RetraceRunner();

        var result = await runner.RunAsync("deps", "status", "--output", "json");

        result.ExitCode.Should().Be(0);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var models = document.RootElement.EnumerateArray().ToArray();

        models.Should().HaveCount(3);
        models.Select(model => model.GetProperty("id").GetString())
            .Should().Contain("bge-small-en-v1.5");
        models.Count(model => model.GetProperty("active").GetBoolean()).Should().Be(1);
    }

    [Fact]
    public async Task Deps_install_rejects_an_unknown_target()
    {
        using var runner = new RetraceRunner();

        var result = await runner.RunAsync("deps", "install", "tesseract");

        result.ExitCode.Should().NotBe(0);
        result.StandardError.Should().Contain("Unknown dependency target");
    }

    [Fact]
    public async Task Help_lists_every_top_level_command()
    {
        using var runner = new RetraceRunner();

        var result = await runner.RunAsync("--help");

        result.ExitCode.Should().Be(0);

        foreach (var command in (string[])
                 ["sources", "doctor", "info", "tui", "list", "search", "show", "export", "files", "resume", "tag", "index", "deps", "config", "mcp"])
        {
            result.All.Should().Contain(command);
        }
    }

    [Fact]
    public async Task Tui_without_a_terminal_fails_with_guidance_instead_of_garbage()
    {
        using var runner = new RetraceRunner();

        // The runner redirects every stream, which is exactly the situation a script or an agent
        // puts the tool in. The browser must refuse cleanly rather than spray escape codes.
        var result = await runner.RunAsync("tui");

        result.ExitCode.Should().Be(1);
        result.StandardError.Should().Contain("requires a terminal");
        result.StandardOutput.Should().NotContain("\u001b[?1049h", "the alternate screen must never be entered without a terminal");
        result.StandardError.Should().NotContain("Unhandled exception");
    }

    [Fact]
    public async Task Tui_rejects_an_unknown_pick_kind()
    {
        using var runner = new RetraceRunner();

        var result = await runner.RunAsync("tui", "--pick", "banana");

        result.ExitCode.Should().Be(1);
        result.StandardError.Should().Contain("uri, id, dir, or command");
    }

    [Fact]
    public async Task Sources_reports_the_indexing_policy_and_index_build_honours_it()
    {
        using var runner = new RetraceRunner();

        (await runner.RunAsync("config", "set", "sources.copilot-cli.indexByDefault", "false")).ExitCode.Should().Be(0);

        var sources = await runner.RunAsync("sources", "--output", "json");
        sources.ExitCode.Should().Be(0);

        using var document = JsonDocument.Parse(sources.StandardOutput);
        var copilot = document.RootElement.EnumerateArray().Single(source => source.GetProperty("id").GetString() == "copilot-cli");
        copilot.GetProperty("enabled").GetBoolean().Should().BeTrue();
        copilot.GetProperty("indexByDefault").GetBoolean().Should().BeFalse();

        // Whether or not the harness is installed on this machine, the build must report the
        // source as skipped for the configured reason rather than reading it. Both builds are
        // scoped to the last hour: the policy does not depend on volume, and these run against
        // whatever real stores the machine has.
        var build = await runner.RunAsync("index", "build", "--no-embed", "--since", "1h", "--source", "copilot-cli", "--output", "json");
        build.ExitCode.Should().Be(0);

        var unscoped = await runner.RunAsync("index", "build", "--no-embed", "--since", "1h", "--output", "json");
        unscoped.ExitCode.Should().Be(0);
        using var buildDocument = JsonDocument.Parse(unscoped.StandardOutput);
        var skipped = buildDocument.RootElement.GetProperty("sources").EnumerateArray()
            .Single(source => source.GetProperty("sourceId").GetString() == "copilot-cli");
        skipped.GetProperty("skipReason").GetString().Should().Contain("indexByDefault");
    }

    [Fact]
    public async Task Running_with_no_arguments_points_at_help_and_exits_non_zero()
    {
        using var runner = new RetraceRunner();

        var result = await runner.RunAsync();

        result.ExitCode.Should().Be(1);
        result.StandardError.Should().Contain("--help");
    }
}
