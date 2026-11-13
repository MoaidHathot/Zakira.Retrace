using Microsoft.Data.Sqlite;
using Zakira.Retrace.Core.Storage;

namespace Zakira.Retrace.Core.UnitTests;

public sealed class SqliteCancellationTests
{
    [Fact]
    public void Cancellation_interrupts_work_already_running_inside_sqlite()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var cancellation = new CancellationTokenSource();
        connection.EnableQueryCancellation(cancellation.Token);
        var visited = 0;
        connection.CreateFunction("observe", (long value) =>
        {
            if (++visited == 2500)
            {
                cancellation.Cancel();
            }

            return value;
        });
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE numbers(value) AS (
                SELECT 1 UNION ALL SELECT value + 1 FROM numbers WHERE value < 1000000
            )
            SELECT SUM(observe(value)) FROM numbers;
            """;

        var error = Assert.Throws<SqliteException>(() => command.ExecuteScalar());
        error.SqliteErrorCode.Should().Be(9, "SQLITE_INTERRUPT proves cancellation reached SQLite, not just the next await");
        visited.Should().BeInRange(2500, 3500, "the long-running statement stops promptly after cancellation");
    }
}
