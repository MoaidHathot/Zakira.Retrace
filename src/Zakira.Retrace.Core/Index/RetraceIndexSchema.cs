using Microsoft.Data.Sqlite;

namespace Zakira.Retrace.Core.Index;

/// <summary>
/// Creates and opens the Retrace search index.
/// </summary>
/// <remarks>
/// <para>
/// Unlike the harness stores, this database belongs to Retrace, so it is opened read/write and can
/// use WAL. It lives under the data directory rather than the config directory because it is a
/// derived cache: deleting it costs a rebuild and nothing else.
/// </para>
/// <para>Layout:</para>
/// <list type="bullet">
///   <item><description><c>session</c> — one row per session, holding everything a listing needs.</description></item>
///   <item><description><c>chunk</c> + <c>chunk_fts</c> — passages and their FTS5 index. The FTS
///   table is external-content over <c>chunk</c>, so passage text is stored once.</description></item>
///   <item><description><c>chunk_embedding</c> — per-passage vectors, used to rank inside a shortlist.</description></item>
///   <item><description><c>session_embedding</c> — one centroid per session, used to build that
///   shortlist without scanning every passage vector.</description></item>
///   <item><description><c>source_state</c> — per-source watermarks, which is what makes refresh
///   incremental rather than a full rebuild.</description></item>
/// </list>
/// </remarks>
public static class RetraceIndexSchema
{
    /// <summary>
    /// Bumped whenever the layout changes incompatibly. A mismatch triggers a full rebuild rather
    /// than an in-place migration: the index is derived data, so recreating it is always safe and
    /// always cheaper than maintaining migration code.
    /// </summary>
    public const int Version = 1;

    /// <summary>
    /// Shortest chunk worth embedding.
    /// </summary>
    /// <remarks>
    /// Below this a passage is mostly greeting or punctuation, and its vector describes the model's
    /// priors more than the conversation. Such chunks stay fully keyword-searchable. This lives on
    /// the schema rather than in the builder because three places have to agree on it — the writer
    /// that decides what to embed, the backfill that decides what still needs embedding, and the
    /// status that reports coverage — and a disagreement between any two of them shows up as a
    /// session that looks permanently unembedded and is re-read forever.
    /// </remarks>
    public const int MinimumEmbeddableCharacters = 24;

    /// <summary>Opens the index read/write, creating and initialising it when absent.</summary>
    public static async Task<SqliteConnection> OpenAsync(string path, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());

        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var pragma = connection.CreateCommand())
        {
            // WAL keeps a long index build from blocking concurrent readers, which matters because
            // an auto-refresh can be triggered by one command while another is querying.
            // NORMAL synchronous is the right trade for derived data.
            pragma.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA foreign_keys = ON;
                PRAGMA temp_store = MEMORY;
                """;
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <summary>Opens the index read-only, or returns null when it has not been built.</summary>
    public static async Task<SqliteConnection?> OpenForReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());

        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var existing = await ReadSchemaVersionAsync(connection, cancellationToken).ConfigureAwait(false);

        if (existing == Version)
        {
            return;
        }

        if (existing is not null)
        {
            await DropAllAsync(connection, cancellationToken).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS meta (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS source_state (
                source_id        TEXT PRIMARY KEY,
                watermark        TEXT,
                last_indexed_utc INTEGER NOT NULL,
                session_count    INTEGER NOT NULL DEFAULT 0,
                chunk_count      INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS session (
                rowid           INTEGER PRIMARY KEY AUTOINCREMENT,
                uri             TEXT NOT NULL UNIQUE,
                source_id       TEXT NOT NULL,
                native_id       TEXT NOT NULL,
                title           TEXT NOT NULL,
                preview         TEXT,
                workspace_path  TEXT,
                repository      TEXT,
                branch          TEXT,
                agent           TEXT,
                models          TEXT,
                created_utc     INTEGER NOT NULL,
                updated_utc     INTEGER NOT NULL,
                message_count   INTEGER,
                tool_call_count INTEGER,
                total_tokens    INTEGER,
                cost            REAL,
                additions       INTEGER,
                deletions       INTEGER,
                files_changed   INTEGER,
                is_archived     INTEGER NOT NULL DEFAULT 0,
                parent_native_id TEXT,
                content_hash    TEXT NOT NULL,
                indexed_utc     INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS session_source_updated_idx ON session (source_id, updated_utc DESC);
            CREATE INDEX IF NOT EXISTS session_updated_idx        ON session (updated_utc DESC);
            CREATE INDEX IF NOT EXISTS session_workspace_idx      ON session (workspace_path);
            CREATE INDEX IF NOT EXISTS session_native_idx         ON session (native_id);

            CREATE TABLE IF NOT EXISTS chunk (
                rowid         INTEGER PRIMARY KEY AUTOINCREMENT,
                session_rowid INTEGER NOT NULL REFERENCES session(rowid) ON DELETE CASCADE,
                ordinal       INTEGER NOT NULL,
                turn_index    INTEGER,
                role          TEXT NOT NULL,
                timestamp_utc INTEGER,
                text          TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS chunk_session_idx ON chunk (session_rowid, ordinal);

            CREATE VIRTUAL TABLE IF NOT EXISTS chunk_fts USING fts5 (
                text,
                content      = 'chunk',
                content_rowid = 'rowid',
                tokenize     = 'unicode61 remove_diacritics 2'
            );

            CREATE TABLE IF NOT EXISTS chunk_embedding (
                chunk_rowid INTEGER PRIMARY KEY REFERENCES chunk(rowid) ON DELETE CASCADE,
                vector      BLOB NOT NULL
            );

            CREATE TABLE IF NOT EXISTS session_embedding (
                session_rowid INTEGER PRIMARY KEY REFERENCES session(rowid) ON DELETE CASCADE,
                vector        BLOB NOT NULL
            );

            CREATE TABLE IF NOT EXISTS session_file (
                session_rowid INTEGER NOT NULL REFERENCES session(rowid) ON DELETE CASCADE,
                path          TEXT NOT NULL,
                tool          TEXT,
                PRIMARY KEY (session_rowid, path)
            );

            CREATE INDEX IF NOT EXISTS session_file_path_idx ON session_file (path);

            CREATE TABLE IF NOT EXISTS tag (
                session_uri TEXT NOT NULL,
                tag         TEXT NOT NULL,
                origin      TEXT NOT NULL DEFAULT 'manual',
                created_utc INTEGER NOT NULL,
                PRIMARY KEY (session_uri, tag)
            );

            CREATE INDEX IF NOT EXISTS tag_tag_idx ON tag (tag);
            """;

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await SetMetaAsync(connection, "schemaVersion", Version.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int?> ReadSchemaVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='meta' LIMIT 1;";
        if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null or DBNull)
        {
            return null;
        }

        var value = await GetMetaAsync(connection, "schemaVersion", cancellationToken).ConfigureAwait(false);
        return int.TryParse(value, out var parsed) ? parsed : null;
    }

    private static async Task DropAllAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TABLE IF EXISTS chunk_fts;
            DROP TABLE IF EXISTS chunk_embedding;
            DROP TABLE IF EXISTS session_embedding;
            DROP TABLE IF EXISTS session_file;
            DROP TABLE IF EXISTS chunk;
            DROP TABLE IF EXISTS session;
            DROP TABLE IF EXISTS source_state;
            DROP TABLE IF EXISTS meta;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads a metadata value.</summary>
    public static async Task<string?> GetMetaAsync(SqliteConnection connection, string key, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result as string;
    }

    /// <summary>Writes a metadata value.</summary>
    public static async Task SetMetaAsync(SqliteConnection connection, string key, string? value, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        if (value is null)
        {
            command.CommandText = "DELETE FROM meta WHERE key = $key;";
            command.Parameters.AddWithValue("$key", key);
        }
        else
        {
            command.CommandText = "INSERT INTO meta(key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Metadata key holding the embedding model the vectors were produced with.</summary>
    public const string EmbeddingModelKey = "embeddingModel";

    /// <summary>Metadata key holding the embedding dimension.</summary>
    public const string EmbeddingDimensionsKey = "embeddingDimensions";

    /// <summary>Metadata key holding the timestamp of the last completed refresh.</summary>
    public const string LastRefreshKey = "lastRefreshUtc";
}
