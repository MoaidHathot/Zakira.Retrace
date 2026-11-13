using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Core.Configuration;
using Zakira.Retrace.Core.Embeddings;
using Zakira.Retrace.Core.Index;
using Zakira.Retrace.Core.Json;

namespace Zakira.Retrace.Core.UnitTests;

/// <summary>Repeatable, opt-in performance evidence against synthetic data, with no machine-specific pass/fail timing threshold.</summary>
public sealed class SearchPerformanceTests
{
    private sealed class NoEmbeddings : IEmbeddingProviderFactory
    {
        public string ModelId => "none";
        public bool IsAvailable => false;
        public Task<IEmbeddingProvider> CreateAsync(CancellationToken cancellationToken) => throw new InvalidOperationException();
    }

    [Fact]
    public async Task Records_search_latency_on_a_large_reproducible_corpus()
    {
        var directory = Environment.GetEnvironmentVariable("RETRACE_BENCHMARK_DIRECTORY");
        if (string.IsNullOrEmpty(directory))
        {
            Assert.Skip("Set RETRACE_BENCHMARK_DIRECTORY to record reproducible search timings.");
            return;
        }

        using var temp = new TempDirectory();
        var config = new RetraceConfig();
        config.Index.Path = Path.Combine(temp.Path, "benchmark.db");
        config.Embeddings.Enabled = false;
        var token = TestContext.Current.CancellationToken;
        await using (var connection = await RetraceIndexSchema.OpenAsync(config.Index.Path, token))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                WITH RECURSIVE numbers(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM numbers WHERE n < 10000)
                INSERT INTO session(uri, source_id, native_id, title, created_utc, updated_utc, message_count, content_hash, indexed_utc)
                SELECT 'retrace://' || CASE n%3 WHEN 0 THEN 'opencode' WHEN 1 THEN 'copilot-cli' ELSE 'copilot-vscode' END || '/' || n,
                       CASE n%3 WHEN 0 THEN 'opencode' WHEN 1 THEN 'copilot-cli' ELSE 'copilot-vscode' END,
                       CAST(n AS TEXT), 'Synthetic session ' || n, $now - n*1000, $now - (n%90)*86400000, 20, 'v1', $now
                FROM numbers;
                WITH RECURSIVE numbers(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM numbers WHERE n < 200000)
                INSERT INTO chunk(session_rowid, ordinal, turn_index, role, text)
                SELECT (n-1)/20+1, (n-1)%20, (n-1)%20, 'Assistant',
                       CASE n%4
                         WHEN 0 THEN 'Investigate the retry policy and exponential backoff with jitter. Test the service under load.'
                         WHEN 1 THEN 'Build a searchable vector index with caching and database connection pooling.'
                         WHEN 2 THEN 'Implement terminal rendering, markdown previews and responsive keyboard input.'
                         ELSE 'Fix the authentication middleware and configure deployment for this workspace.'
                       END || ' Passage ' || n
                FROM numbers;
                INSERT INTO chunk_fts(chunk_fts) VALUES('rebuild');
                INSERT INTO source_state(source_id, watermark, last_indexed_utc, session_count, chunk_count)
                SELECT source_id, 'v1', $now, COUNT(*), COUNT(*)*20 FROM session GROUP BY source_id;
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await command.ExecuteNonQueryAsync(token);
        }

        var searcher = new IndexSearcher(new RetracePaths(), config, new NoEmbeddings());
        var timings = new Dictionary<string, object>(StringComparer.Ordinal);
        var plans = new Dictionary<string, string[]>(StringComparer.Ordinal);
        await using (var connection = (await RetraceIndexSchema.OpenForReadAsync(config.Index.Path, token))!)
        {
            foreach (var legacy in new[] { true, false })
            {
                var count = 0;
                timings[legacy ? "legacyBm25CandidateQuery" : "rankCandidateQuery"] = await Measure(async () =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = legacy ? """
                        SELECT c.rowid, c.session_rowid, bm25(chunk_fts) AS rank
                        FROM chunk_fts JOIN chunk c ON c.rowid = chunk_fts.rowid
                        WHERE chunk_fts MATCH '"retry"* OR "backoff"*' ORDER BY rank LIMIT 500;
                        """ : """
                        SELECT c.rowid, c.session_rowid, chunk_fts.rank
                        FROM chunk_fts JOIN chunk c ON c.rowid = chunk_fts.rowid
                        JOIN session s ON s.rowid = c.session_rowid
                        WHERE s.is_archived = 0 AND chunk_fts MATCH '"retry"* OR "backoff"*'
                        ORDER BY chunk_fts.rank LIMIT 500;
                        """;
                    await using var reader = await command.ExecuteReaderAsync(token);
                    count = 0;
                    while (await reader.ReadAsync(token))
                    {
                        count++;
                    }
                });
                count.Should().Be(500);
            }

            var ids = string.Join(',', Enumerable.Range(1, 150).Select(value => value * 4));
            foreach (var variant in new[] { "rowidIn", "rangeAndMembership", "ftsFirstJoin" })
            {
                var sql = variant switch
                {
                    "rangeAndMembership" => $"SELECT rowid, snippet(chunk_fts, 0, '<<', '>>', '…', 20) FROM chunk_fts WHERE chunk_fts MATCH $match AND rowid BETWEEN 4 AND 600 AND +rowid IN ({ids});",
                    "ftsFirstJoin" => $"SELECT chunk_fts.rowid, snippet(chunk_fts, 0, '<<', '>>', '…', 20) FROM chunk_fts CROSS JOIN chunk c ON c.rowid = chunk_fts.rowid WHERE chunk_fts MATCH $match AND c.rowid IN ({ids});",
                    _ => $"SELECT rowid, snippet(chunk_fts, 0, '<<', '>>', '…', 20) FROM chunk_fts WHERE chunk_fts MATCH $match AND rowid IN ({ids});"
                };
                var count = 0;
                timings["snippets_" + variant] = await Measure(async () =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = sql;
                    command.Parameters.AddWithValue("$match", "\"retry\"* OR \"backoff\"*");
                    await using var reader = await command.ExecuteReaderAsync(token);
                    count = 0;
                    while (await reader.ReadAsync(token))
                    {
                        count++;
                        _ = reader.GetString(1);
                    }
                });
                count.Should().Be(150);
                await using var explain = connection.CreateCommand();
                explain.CommandText = "EXPLAIN QUERY PLAN " + sql;
                explain.Parameters.AddWithValue("$match", "\"retry\"* OR \"backoff\"*");
                await using var plan = await explain.ExecuteReaderAsync(token);
                var details = new List<string>();
                while (await plan.ReadAsync(token))
                {
                    details.Add(plan.GetString(3));
                }

                plans[variant] = [.. details];
            }
        }

        foreach (var filtered in new[] { false, true })
        {
            var query = new SearchQuery
            {
                Text = "retry backoff",
                Mode = SearchMode.Lexical,
                Top = 50,
                Filter = filtered ? new SessionFilter { SourceIds = ["opencode"], Since = DateTimeOffset.UtcNow.AddDays(-7) } : SessionFilter.All
            };
            var verified = await searcher.SearchAsync(query, token);
            verified.Should().HaveCount(50);
            verified.Should().AllSatisfy(hit => hit.Snippets.Should().NotBeEmpty());
            if (filtered)
            {
                verified.Should().AllSatisfy(hit => hit.Session.Ref.SourceId.Should().Be("opencode"));
            }

            // Assertions deliberately sit outside the timed region: assertion stack capture is
            // much more expensive than rendering an individual hit and is not part of a search.
            timings[filtered ? "fullProviderWeekSearch" : "fullKeywordSearch"] = await Measure(async () => { await searcher.SearchAsync(query, token); });
            if (!filtered)
            {
                timings["fullKeywordWithoutSnippets"] = await Measure(async () => { await searcher.SearchAsync(query with { SnippetsPerSession = 0 }, token); });
            }
        }

        timings["headerOverview"] = await Measure(async () => { await searcher.GetStatusAsync(token, includeVectorCoverage: false); });
        timings["fullIndexStatus"] = await Measure(async () => { await searcher.GetStatusAsync(token); });

        config.Search.LexicalCandidateCap = 200000;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(20));
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => searcher.SearchAsync(new SearchQuery
        {
            Text = "retry backoff vector cache terminal workspace",
            Mode = SearchMode.Lexical,
            Top = 10000,
            SnippetsPerSession = 0
        }, cancellation.Token));
        timings["cancelledSearchMilliseconds"] = watch.Elapsed.TotalMilliseconds;

        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "search.json"), JsonSerializer.Serialize(new
        {
            sessions = 10000,
            chunks = 200000,
            samples = 7,
            runtime = Environment.Version.ToString(),
            operatingSystem = Environment.OSVersion.ToString(),
            processors = Environment.ProcessorCount,
            timings,
            plans
        }, RetraceJson.IndentedOptions).Replace("\r\n", "\n", StringComparison.Ordinal), token);
    }

    private static async Task<object> Measure(Func<Task> action)
    {
        await action();
        await action();
        var samples = new List<double>();
        for (var index = 0; index < 7; index++)
        {
            var watch = Stopwatch.StartNew();
            await action();
            samples.Add(watch.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        return new { medianMilliseconds = samples[3], maximumMilliseconds = samples[^1] };
    }
}
