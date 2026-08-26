using System.Text.Json;

namespace Zakira.Retrace.E2ETests;

/// <summary>
/// Drives the MCP server over real stdio, the way an MCP client does.
/// </summary>
/// <remarks>
/// The most valuable assertion in this file is the one about stdout purity. In stdio mode stdout
/// carries JSON-RPC framing and nothing else; a single log line or stray <c>Console.WriteLine</c>
/// reaching it desynchronises the stream and hangs the client with no useful error. That failure
/// is invisible to unit tests and trivially reintroduced, so it is pinned here.
/// </remarks>
public sealed class McpEndToEndTests
{
    private const string Initialize =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"e2e","version":"1"}}}""";

    private const string Initialized = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";

    [Fact]
    public async Task Server_initializes_and_advertises_tool_and_resource_capabilities()
    {
        using var runner = new RetraceRunner();

        var result = await runner.RunMcpAsync([Initialize, Initialized], expectedResponses: 1);

        var response = FindResponse(result, id: 1);
        response.Should().NotBeNull();

        var capabilities = response!.Value.GetProperty("result").GetProperty("capabilities");
        capabilities.TryGetProperty("tools", out _).Should().BeTrue();
        capabilities.TryGetProperty("resources", out _).Should().BeTrue();

        response.Value.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString()
            .Should().Be("Zakira.Retrace");
    }

    [Fact]
    public async Task Every_stdout_line_is_valid_json_rpc_and_logs_go_to_stderr()
    {
        using var runner = new RetraceRunner();

        var result = await runner.RunMcpAsync([Initialize, Initialized, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}"""], expectedResponses: 2);

        result.Lines.Should().NotBeEmpty();

        foreach (var line in result.Lines)
        {
            var parse = () => JsonDocument.Parse(line);
            parse.Should().NotThrow($"stdout must carry only JSON-RPC, but found: {Truncate(line)}");

            using var document = JsonDocument.Parse(line);
            document.RootElement.TryGetProperty("jsonrpc", out var version).Should().BeTrue();
            version.GetString().Should().Be("2.0");
        }

        // The banner proves stderr is where diagnostics land.
        result.StandardError.Should().Contain("Zakira.Retrace");
    }

    [Fact]
    public async Task Tools_list_exposes_the_documented_tool_set()
    {
        using var runner = new RetraceRunner();

        var result = await runner.RunMcpAsync([Initialize, Initialized, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}"""], expectedResponses: 2);

        var response = FindResponse(result, id: 2);
        response.Should().NotBeNull("tools/list must answer");

        var tools = response!.Value.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
        var names = tools.Select(tool => tool.GetProperty("name").GetString()).ToArray();

        names.Should().BeEquivalentTo(
            "sessions-search",
            "sessions-list",
            "session-get",
            "session-files",
            "session-resume-command",
            "session-tags",
            "sources-list",
            "index-status",
            "index-refresh");

        // Tool names must satisfy the ^[a-zA-Z0-9_-]{1,128}$ pattern some clients enforce, which
        // is why compound names use hyphens rather than dots.
        names.Should().AllSatisfy(name => name.Should().MatchRegex("^[a-zA-Z0-9_-]{1,128}$"));

        // Descriptions are the only thing an agent uses to choose a tool.
        tools.Should().AllSatisfy(tool =>
        {
            tool.GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace();
            tool.TryGetProperty("inputSchema", out _).Should().BeTrue();
        });
    }

    [Fact]
    public async Task Resources_list_exposes_the_catalogue_resources()
    {
        using var runner = new RetraceRunner();

        var result = await runner.RunMcpAsync([Initialize, Initialized, """{"jsonrpc":"2.0","id":2,"method":"resources/list"}"""], expectedResponses: 2);

        var response = FindResponse(result, id: 2);
        response.Should().NotBeNull();

        var uris = response!.Value.GetProperty("result").GetProperty("resources")
            .EnumerateArray()
            .Select(resource => resource.GetProperty("uri").GetString())
            .ToArray();

        uris.Should().Contain("retrace://sources");
        uris.Should().Contain("retrace://sessions/recent");
    }

    [Fact]
    public async Task Sources_list_tool_returns_structured_content()
    {
        using var runner = new RetraceRunner();

        var result = await runner.RunMcpAsync(
        [
            Initialize,
            Initialized,
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"sources-list","arguments":{}}}"""
        ], expectedResponses: 2);

        var response = FindResponse(result, id: 2);
        response.Should().NotBeNull("sources-list must answer");

        var text = response!.Value.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        text.Should().NotBeNullOrWhiteSpace();

        using var payload = JsonDocument.Parse(text!);
        var sources = payload.RootElement.GetProperty("sources").EnumerateArray().ToArray();

        sources.Should().HaveCount(3);
        sources.Select(source => source.GetProperty("id").GetString())
            .Should().BeEquivalentTo("opencode", "copilot-cli", "copilot-vscode");
    }

    [Fact]
    public async Task A_failing_tool_returns_an_error_result_rather_than_killing_the_server()
    {
        using var runner = new RetraceRunner();

        var result = await runner.RunMcpAsync(
        [
            Initialize,
            Initialized,
            // No index exists yet, so this cannot succeed.
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"session-get","arguments":{"session":"not-a-real-session"}}}""",
            // The server must still be answering afterwards.
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"sources-list","arguments":{}}}"""
        ], expectedResponses: 3);

        var failure = FindResponse(result, id: 2);
        failure.Should().NotBeNull("a tool failure must be reported, not swallowed");

        var recovered = FindResponse(result, id: 3);
        recovered.Should().NotBeNull("one failing tool call must not take down the session");
    }

    [Fact]
    public async Task Session_get_defaults_exclude_tool_output_in_the_advertised_schema()
    {
        using var runner = new RetraceRunner();

        var result = await runner.RunMcpAsync([Initialize, Initialized, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}"""], expectedResponses: 2);

        var response = FindResponse(result, id: 2);
        response.Should().NotBeNull();

        var sessionGet = response!.Value.GetProperty("result").GetProperty("tools")
            .EnumerateArray()
            .Single(tool => tool.GetProperty("name").GetString() == "session-get");

        var properties = sessionGet.GetProperty("inputSchema").GetProperty("properties");

        // Tool output is the bulk of a session's size, so an agent must opt in rather than be
        // handed it by default and burn its context window.
        properties.GetProperty("includeToolOutput").GetProperty("default").GetBoolean().Should().BeFalse();
        properties.GetProperty("maxCharacters").GetProperty("default").GetInt32().Should().Be(20000);
    }

    private static JsonElement? FindResponse(McpResult result, int id)
    {
        foreach (var line in result.Lines)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                if (document.RootElement.TryGetProperty("id", out var messageId)
                    && messageId.ValueKind == JsonValueKind.Number
                    && messageId.GetInt32() == id)
                {
                    // Clone so the element outlives the document.
                    return document.RootElement.Clone();
                }
            }
        }

        return null;
    }

    private static string Truncate(string value) => value.Length <= 200 ? value : value[..200] + "…";
}
