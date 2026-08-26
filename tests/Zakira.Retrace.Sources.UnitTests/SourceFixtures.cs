using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Zakira.Retrace.Sources.UnitTests;

/// <summary>
/// Builds synthetic session stores that match each harness's real on-disk schema.
/// </summary>
/// <remarks>
/// These exist so the source implementations are covered on any machine, including CI, where none
/// of the harnesses are installed. The DDL and JSON shapes here were taken from real stores, so a
/// test passing against a fixture is meaningful evidence rather than a tautology.
/// </remarks>
internal static class SourceFixtures
{
    /// <summary>Creates a scratch directory that deletes itself.</summary>
    public static TempDirectory NewDirectory() => new();

    // ---- OpenCode --------------------------------------------------------------------------

    /// <summary>
    /// Writes an <c>opencode.db</c> matching OpenCode's current Drizzle-managed schema.
    /// </summary>
    public static void CreateOpenCodeDatabase(string dataDirectory, int sessions = 3, int messagesPerSession = 4)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, "opencode.db");

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());

        connection.Open();

        Execute(connection, """
            CREATE TABLE `project` (
                `id` text PRIMARY KEY, `worktree` text NOT NULL, `vcs` text, `name` text,
                `time_created` integer NOT NULL, `time_updated` integer NOT NULL, `sandboxes` text NOT NULL
            );
            CREATE TABLE `session` (
                `id` text PRIMARY KEY, `project_id` text NOT NULL, `parent_id` text, `slug` text NOT NULL,
                `directory` text NOT NULL, `title` text NOT NULL, `version` text NOT NULL,
                `summary_additions` integer, `summary_deletions` integer, `summary_files` integer,
                `time_created` integer NOT NULL, `time_updated` integer NOT NULL, `time_archived` integer,
                `agent` text, `model` text, `cost` real,
                `tokens_input` integer, `tokens_output` integer, `tokens_reasoning` integer
            );
            CREATE TABLE `message` (
                `id` text PRIMARY KEY, `session_id` text NOT NULL,
                `time_created` integer NOT NULL, `time_updated` integer NOT NULL, `data` text NOT NULL
            );
            CREATE TABLE `part` (
                `id` text PRIMARY KEY, `message_id` text NOT NULL, `session_id` text NOT NULL,
                `time_created` integer NOT NULL, `time_updated` integer NOT NULL, `data` text NOT NULL
            );
            CREATE INDEX `message_session_idx` ON `message` (`session_id`);
            CREATE INDEX `part_session_idx` ON `part` (`session_id`);
            """);

        var baseTime = DateTimeOffset.UtcNow.AddDays(-10).ToUnixTimeMilliseconds();

        Execute(connection,
            "INSERT INTO project VALUES ('proj1', 'P:/Github/Fixture', 'git', NULL, $t, $t, '[]');",
            ("$t", baseTime));

        for (var index = 0; index < sessions; index++)
        {
            var sessionId = $"ses_fixture{index:D4}";
            var created = baseTime + (index * 3_600_000L);
            var updated = created + 600_000L;

            Execute(connection, """
                INSERT INTO session VALUES (
                    $id, 'proj1', NULL, $slug, 'P:/Github/Fixture', $title, '1.2.3',
                    12, 4, 2, $created, $updated, NULL,
                    'build', '{"id":"claude-opus-5","providerID":"github-copilot"}', 1.25,
                    1000, 500, 0
                );
                """,
                ("$id", sessionId),
                ("$slug", $"fixture-{index}"),
                ("$title", $"Fixture session {index} about vector search"),
                ("$created", created),
                ("$updated", updated));

            for (var message = 0; message < messagesPerSession; message++)
            {
                var messageId = $"msg_{index:D4}_{message:D4}";
                var isUser = message % 2 == 0;
                var timestamp = created + (message * 1000L);

                var messageData = isUser
                    ? JsonSerializer.Serialize(new
                    {
                        role = "user",
                        time = new { created = timestamp },
                        agent = "build",
                        model = new { providerID = "github-copilot", modelID = "claude-opus-5" }
                    })
                    : JsonSerializer.Serialize(new
                    {
                        parentID = "msg_x",
                        role = "assistant",
                        mode = "build",
                        agent = "build",
                        modelID = "claude-opus-5",
                        providerID = "github-copilot",
                        time = new { created = timestamp },
                        tokens = new { input = 10, output = 20 }
                    });

                Execute(connection,
                    "INSERT INTO message VALUES ($id, $session, $t, $t, $data);",
                    ("$id", messageId), ("$session", sessionId), ("$t", timestamp), ("$data", messageData));

                var text = isUser
                    ? $"How do I implement hybrid search in session {index}?"
                    : $"Use SQLite FTS5 for keyword matching and ONNX embeddings for semantic similarity in session {index}.";

                Execute(connection,
                    "INSERT INTO part VALUES ($id, $message, $session, $t, $t, $data);",
                    ("$id", $"prt_{index:D4}_{message:D4}_0"),
                    ("$message", messageId),
                    ("$session", sessionId),
                    ("$t", timestamp),
                    ("$data", JsonSerializer.Serialize(new { type = "text", text })));

                if (!isUser)
                {
                    // A tool part and a step marker, so tests cover both the parsed and the
                    // deliberately-skipped part types.
                    Execute(connection,
                        "INSERT INTO part VALUES ($id, $message, $session, $t, $t, $data);",
                        ("$id", $"prt_{index:D4}_{message:D4}_1"),
                        ("$message", messageId),
                        ("$session", sessionId),
                        ("$t", timestamp),
                        ("$data", JsonSerializer.Serialize(new
                        {
                            type = "tool",
                            callID = $"call_{index}_{message}",
                            tool = "bash",
                            state = new
                            {
                                status = "completed",
                                input = new { command = "dotnet build" },
                                output = "Build succeeded.",
                                title = "dotnet build",
                                time = new { start = timestamp, end = timestamp + 500 }
                            }
                        })));

                    Execute(connection,
                        "INSERT INTO part VALUES ($id, $message, $session, $t, $t, $data);",
                        ("$id", $"prt_{index:D4}_{message:D4}_2"),
                        ("$message", messageId),
                        ("$session", sessionId),
                        ("$t", timestamp),
                        ("$data", """{"type":"step-finish","cost":0}"""));
                }
            }
        }
    }

    // ---- Copilot CLI -----------------------------------------------------------------------

    /// <summary>Writes a <c>session-store.db</c> matching Copilot CLI schema version 6.</summary>
    public static void CreateCopilotCliDatabase(string dataDirectory, int sessions = 3, int turnsPerSession = 2, int emptySessions = 2)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, "session-store.db");

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());

        connection.Open();

        Execute(connection, """
            CREATE TABLE schema_version (version INTEGER NOT NULL);
            CREATE TABLE sessions (
                id TEXT PRIMARY KEY, cwd TEXT, repository TEXT, branch TEXT, summary TEXT,
                created_at TEXT, updated_at TEXT, host_type TEXT
            );
            CREATE TABLE turns (
                id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL,
                turn_index INTEGER NOT NULL, user_message TEXT, assistant_response TEXT,
                timestamp TEXT, UNIQUE(session_id, turn_index)
            );
            CREATE TABLE session_files (
                id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL,
                file_path TEXT NOT NULL, tool_name TEXT, turn_index INTEGER,
                UNIQUE(session_id, file_path)
            );
            CREATE TABLE session_refs (
                id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL,
                ref_type TEXT NOT NULL, ref_value TEXT NOT NULL, turn_index INTEGER
            );
            CREATE TABLE assistant_usage_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL, turn_index INTEGER,
                agent_id TEXT, model TEXT NOT NULL, input_tokens INTEGER, output_tokens INTEGER,
                reasoning_tokens INTEGER, total_nano_aiu INTEGER, created_at TEXT
            );
            CREATE VIRTUAL TABLE search_index USING fts5(
                content, session_id UNINDEXED, source_type UNINDEXED, source_id UNINDEXED
            );
            INSERT INTO schema_version VALUES (6);
            """);

        var baseTime = DateTimeOffset.UtcNow.AddDays(-5);

        for (var index = 0; index < sessions; index++)
        {
            var sessionId = $"00000000-0000-0000-0000-{index:D12}";
            var created = baseTime.AddHours(index);

            Execute(connection, """
                INSERT INTO sessions VALUES ($id, 'W:\Github\Fixture', 'Owner/Fixture', 'main', $summary, $created, $updated, 'cli');
                """,
                ("$id", sessionId),
                ("$summary", $"Investigate the retry policy in session {index}. It keeps timing out."),
                ("$created", created.ToString("O", CultureInfo.InvariantCulture)),
                ("$updated", created.AddMinutes(10).ToString("O", CultureInfo.InvariantCulture)));

            for (var turn = 0; turn < turnsPerSession; turn++)
            {
                var userMessage = $"Investigate the retry policy in session {index}, turn {turn}.";
                var assistantResponse = $"The retry policy uses exponential backoff with jitter. Session {index}, turn {turn}.";

                Execute(connection, """
                    INSERT INTO turns (session_id, turn_index, user_message, assistant_response, timestamp)
                    VALUES ($id, $turn, $user, $assistant, $ts);
                    """,
                    ("$id", sessionId), ("$turn", turn),
                    ("$user", userMessage), ("$assistant", assistantResponse),
                    ("$ts", created.AddMinutes(turn).ToString("O", CultureInfo.InvariantCulture)));

                Execute(connection, """
                    INSERT INTO search_index (content, session_id, source_type, source_id)
                    VALUES ($content, $id, 'turn', $source);
                    """,
                    ("$content", userMessage + "\n" + assistantResponse),
                    ("$id", sessionId),
                    ("$source", $"{sessionId}:turn:{turn}"));

                Execute(connection, """
                    INSERT INTO assistant_usage_events (session_id, turn_index, agent_id, model, input_tokens, output_tokens, reasoning_tokens, total_nano_aiu, created_at)
                    VALUES ($id, $turn, 'default', 'claude-opus-4.8', 100, 50, 0, 2000000000, $ts);
                    """,
                    ("$id", sessionId), ("$turn", turn),
                    ("$ts", created.ToString("O", CultureInfo.InvariantCulture)));
            }

            Execute(connection,
                "INSERT INTO session_files (session_id, file_path, tool_name, turn_index) VALUES ($id, $path, 'edit', 0);",
                ("$id", sessionId), ("$path", $"W:\\Github\\Fixture\\Retry{index}.cs"));
        }

        // Copilot CLI writes a row on every launch, so the store is full of turn-less shells.
        // Fixtures include them because filtering them out is the source's job.
        for (var index = 0; index < emptySessions; index++)
        {
            Execute(connection, """
                INSERT INTO sessions VALUES ($id, 'W:\Github\Fixture', NULL, NULL, NULL, $created, $created, 'cli');
                """,
                ("$id", $"11111111-0000-0000-0000-{index:D12}"),
                ("$created", baseTime.AddDays(-1).ToString("O", CultureInfo.InvariantCulture)));
        }
    }

    // ---- Copilot in VS Code ----------------------------------------------------------------

    /// <summary>Writes a workspace storage tree containing both chat session file formats.</summary>
    public static (string WorkspaceStorage, string JsonSessionId, string JsonlSessionId) CreateVsCodeWorkspaceStorage(string root, string workspaceFolder)
    {
        var workspaceStorage = Path.Combine(root, "Code", "User", "workspaceStorage");
        var hash = "0123456789abcdef0123456789abcdef";
        var workspace = Path.Combine(workspaceStorage, hash);
        var chatSessions = Path.Combine(workspace, "chatSessions");
        Directory.CreateDirectory(chatSessions);

        // The folder is stored as a percent-encoded file URI, which is what makes hash-to-path
        // resolution non-trivial.
        var uri = "file:///" + workspaceFolder.Replace('\\', '/').Replace(":", "%3A", StringComparison.Ordinal);
        File.WriteAllText(
            Path.Combine(workspace, "workspace.json"),
            JsonSerializer.Serialize(new { folder = uri }));

        var jsonSessionId = "aaaaaaaa-1111-2222-3333-444444444444";
        var jsonlSessionId = "bbbbbbbb-1111-2222-3333-444444444444";

        var created = DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeMilliseconds();

        // --- whole-document format (version 3) ---
        var document = new JsonObject
        {
            ["version"] = 3,
            ["requesterUsername"] = "tester",
            ["responderUsername"] = "GitHub Copilot",
            ["initialLocation"] = "panel",
            ["sessionId"] = jsonSessionId,
            ["creationDate"] = created,
            ["lastMessageDate"] = created + 60_000,
            ["isImported"] = false,
            ["requests"] = new JsonArray
            {
                new JsonObject
                {
                    ["requestId"] = "req1",
                    ["timestamp"] = created,
                    ["modelId"] = "copilot/claude-opus-4.6",
                    ["agent"] = new JsonObject { ["id"] = "github.copilot.editsAgent", ["name"] = "agent" },
                    ["message"] = new JsonObject { ["text"] = "Where is the retry policy configured?" },
                    ["response"] = new JsonArray
                    {
                        new JsonObject { ["value"] = "The retry policy lives in " },
                        new JsonObject { ["value"] = "`RetryOptions.cs` and uses exponential backoff." },
                        new JsonObject
                        {
                            ["kind"] = "toolInvocationSerialized",
                            ["toolId"] = "copilot_searchCodebase",
                            ["toolCallId"] = "tc1",
                            ["isComplete"] = true,
                            ["invocationMessage"] = new JsonObject { ["value"] = "Searching codebase for \"retry policy\"" }
                        },
                        new JsonObject { ["kind"] = "thinking", ["value"] = "The user wants configuration, not usage." }
                    },
                    ["contentReferences"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["reference"] = new JsonObject { ["scheme"] = "file", ["path"] = "/" + workspaceFolder.Replace('\\', '/') + "/RetryOptions.cs" }
                        }
                    }
                }
            }
        };

        File.WriteAllText(Path.Combine(chatSessions, jsonSessionId + ".json"), document.ToJsonString());

        // --- delta-log format ---
        // Line 0 is a snapshot with no requests; the conversation only exists as later appends,
        // which is exactly the case a naive reader gets wrong.
        var lines = new List<string>
        {
            new JsonObject
            {
                ["kind"] = 0,
                ["v"] = new JsonObject
                {
                    ["version"] = 3,
                    ["creationDate"] = created,
                    ["initialLocation"] = "panel",
                    ["responderUsername"] = "GitHub Copilot",
                    ["sessionId"] = jsonlSessionId,
                    ["requests"] = new JsonArray(),
                    ["inputState"] = new JsonObject
                    {
                        ["mode"] = new JsonObject { ["id"] = "agent" },
                        ["selectedModel"] = new JsonObject
                        {
                            ["metadata"] = new JsonObject { ["id"] = "claude-opus-4.7" }
                        }
                    }
                }
            }.ToJsonString(),

            // kind 2 = append to the array at path k.
            new JsonObject
            {
                ["kind"] = 2,
                ["k"] = new JsonArray { "requests" },
                ["v"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["requestId"] = "req-delta",
                        ["timestamp"] = created + 1000,
                        ["modelId"] = "copilot/claude-opus-4.7",
                        ["agent"] = new JsonObject { ["id"] = "github.copilot.editsAgent" },
                        ["message"] = new JsonObject { ["text"] = "Add jitter to the backoff calculation." },
                        ["response"] = new JsonArray()
                    }
                }
            }.ToJsonString(),

            // Streaming response fragments, appended to requests[0].response.
            new JsonObject
            {
                ["kind"] = 2,
                ["k"] = new JsonArray { "requests", 0, "response" },
                ["v"] = new JsonArray { new JsonObject { ["value"] = "Jitter is added by multiplying " } }
            }.ToJsonString(),

            new JsonObject
            {
                ["kind"] = 2,
                ["k"] = new JsonArray { "requests", 0, "response" },
                ["v"] = new JsonArray { new JsonObject { ["value"] = "the delay by a random factor." } }
            }.ToJsonString(),

            // kind 1 = set at path k.
            new JsonObject
            {
                ["kind"] = 1,
                ["k"] = new JsonArray { "customTitle" },
                ["v"] = "Backoff jitter"
            }.ToJsonString(),

            new JsonObject
            {
                ["kind"] = 1,
                ["k"] = new JsonArray { "lastMessageDate" },
                ["v"] = created + 120_000
            }.ToJsonString()
        };

        File.WriteAllLines(Path.Combine(chatSessions, jsonlSessionId + ".jsonl"), lines);

        return (workspaceStorage, jsonSessionId, jsonlSessionId);
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }
}

/// <summary>A scratch directory removed on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    /// <summary>Creates the directory.</summary>
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Zakira.Retrace.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>Full path.</summary>
    public string Path { get; }

    /// <summary>Combines a relative path under this directory.</summary>
    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A locked scratch file is not worth failing a test over; the OS will reclaim it.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }
}
