using Microsoft.Data.Sqlite;

namespace Zakira.Retrace.Core.Storage;

/// <summary>
/// Opens a harness's SQLite session store for reading without disturbing the harness.
/// </summary>
/// <remarks>
/// <para>
/// Every supported harness keeps its database open, in WAL mode, for as long as it is running.
/// That makes naive access hazardous in two directions: a write from Retrace could corrupt an
/// active session, and a read that acquires the wrong lock could stall the harness.
/// </para>
/// <para>
/// The mitigations here are, in order:
/// </para>
/// <list type="number">
///   <item><description>Open with <c>Mode=ReadOnly</c> and set <c>PRAGMA query_only</c>, so a write
///   is refused by SQLite itself rather than relying on discipline in calling code.</description></item>
///   <item><description>Disable connection pooling, so a file handle is never held across calls and
///   the harness can always rotate or delete its own database.</description></item>
///   <item><description>Set a short <c>busy_timeout</c> so a contended read fails fast instead of
///   blocking a CLI command indefinitely.</description></item>
///   <item><description>Fall back to a snapshot copy of the database plus its <c>-wal</c> and
///   <c>-shm</c> sidecars when the live file cannot be opened. This costs a copy but is the only
///   way to read a database whose directory is not writable, which SQLite requires for WAL even in
///   read-only mode.</description></item>
/// </list>
/// </remarks>
public sealed class ReadOnlyDatabase : IAsyncDisposable
{
    private readonly string? snapshotDirectory;

    private ReadOnlyDatabase(SqliteConnection connection, string sourcePath, bool isSnapshot, string? snapshotDirectory)
    {
        Connection = connection;
        SourcePath = sourcePath;
        IsSnapshot = isSnapshot;
        this.snapshotDirectory = snapshotDirectory;
    }

    /// <summary>The open, read-only connection.</summary>
    public SqliteConnection Connection { get; }

    /// <summary>Path of the database that was requested, not of the snapshot.</summary>
    public string SourcePath { get; }

    /// <summary>Whether reads are being served from a temporary copy rather than the live file.</summary>
    public bool IsSnapshot { get; }

    /// <summary>
    /// Opens a database for reading, snapshotting it first if the live file will not open.
    /// </summary>
    /// <param name="path">Database file to read.</param>
    /// <param name="snapshotDirectory">Directory to place fallback snapshots in.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<ReadOnlyDatabase> OpenAsync(string path, string snapshotDirectory, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Database not found: {path}", path);
        }

        try
        {
            var connection = await OpenReadOnlyAsync(path, cancellationToken).ConfigureAwait(false);
            return new ReadOnlyDatabase(connection, path, isSnapshot: false, snapshotDirectory: null);
        }
        catch (SqliteException)
        {
            // Most commonly SQLITE_CANTOPEN because the WAL sidecar cannot be created or mapped.
            // A snapshot sidesteps the whole problem at the cost of a copy.
            var snapshotPath = await CreateSnapshotAsync(path, snapshotDirectory, cancellationToken).ConfigureAwait(false);
            var connection = await OpenReadOnlyAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
            return new ReadOnlyDatabase(connection, path, isSnapshot: true, Path.GetDirectoryName(snapshotPath));
        }
    }

    /// <summary>
    /// Reports whether a database can be opened at all, without throwing. Used by health checks.
    /// </summary>
    public static async Task<bool> CanOpenAsync(string path, string snapshotDirectory, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            await using var database = await OpenAsync(path, snapshotDirectory, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<SqliteConnection> OpenReadOnlyAsync(string path, CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            // Pooling would keep a handle on a file the harness owns. Never worth it here: these
            // connections are opened once per command, not per query.
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var pragma = connection.CreateCommand())
        {
            // query_only is belt-and-braces on top of Mode=ReadOnly: it also blocks writes that
            // would otherwise be attempted against temp tables or attached databases.
            pragma.CommandText = "PRAGMA query_only = 1; PRAGMA busy_timeout = 3000;";
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return connection;
    }

    private static async Task<string> CreateSnapshotAsync(string path, string snapshotDirectory, CancellationToken cancellationToken)
    {
        var target = Path.Combine(snapshotDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);

        var name = Path.GetFileName(path);
        var destination = Path.Combine(target, name);

        // Copy the WAL and SHM sidecars too. Without them the snapshot would silently lose every
        // transaction the harness has committed but not yet checkpointed, which for a long-running
        // session can be the entire recent conversation.
        await CopyIfPresentAsync(path, destination, cancellationToken).ConfigureAwait(false);
        await CopyIfPresentAsync(path + "-wal", destination + "-wal", cancellationToken).ConfigureAwait(false);
        await CopyIfPresentAsync(path + "-shm", destination + "-shm", cancellationToken).ConfigureAwait(false);

        return destination;
    }

    private static async Task CopyIfPresentAsync(string source, string destination, CancellationToken cancellationToken)
    {
        if (!File.Exists(source))
        {
            return;
        }

        // FileShare.ReadWrite is required: the harness holds the file open for writing.
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Connection.DisposeAsync().ConfigureAwait(false);

        if (snapshotDirectory is not null && Directory.Exists(snapshotDirectory))
        {
            try
            {
                Directory.Delete(snapshotDirectory, recursive: true);
            }
            catch (IOException)
            {
                // A leftover snapshot in the temp directory is harmless; it will be swept by the OS.
            }
            catch (UnauthorizedAccessException)
            {
                // Same.
            }
        }
    }
}
