using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace CloudOrc.ControlAgent.Sync;

/// <summary>
/// Durable local state of the sync feature in its OWN SQLite file (sync.db next to the agent state
/// database). Deliberately separate: AgentStateDatabase refuses to open a newer schema, so adding sync
/// tables there would break the updater's rollback to an older agent build.
///
/// Tables:
///   sync_policy  one row per connection (the signed policy as received)
///   sync_roots   per-root generation / state / last checkpoint
///   sync_files   per-file fingerprint cache (size + mtime → sha256) and upload state
/// Never holds secrets (part grants live in memory only).
/// </summary>
public sealed class SyncDatabase : IDisposable
{
    private const int CurrentSchemaVersion = 1;
    private readonly Lock _sync = new();
    private readonly SqliteConnection _connection;

    public SyncDatabase(string databasePath, ILogger<SyncDatabase> logger)
    {
        // Sync must NEVER stop the agent (backup, commands) from starting: a corrupt / locked sync.db is
        // moved aside and recreated; if even that fails the cache lives in memory until the next restart.
        try
        {
            _connection = OpenFile(databasePath, logger);
        }
        catch (Exception first) when (first is SqliteException or IOException or UnauthorizedAccessException)
        {
            logger.LogError(first, "sync.db could not be opened; moving it aside and starting with an empty sync cache.");
            try
            {
                MoveAside(databasePath);
                _connection = OpenFile(databasePath, logger);
            }
            catch (Exception second) when (second is SqliteException or IOException or UnauthorizedAccessException)
            {
                logger.LogError(second, "sync.db is unusable; sync state is kept in memory until the agent restarts.");
                _connection = new SqliteConnection("Data Source=:memory:");
                _connection.Open();
                Initialize(_connection, logger);
            }
        }
    }

    /// <summary>True when the sync cache is not persisted (sync.db unusable).</summary>
    public bool InMemory => _connection.DataSource is ":memory:" or "";

    private static SqliteConnection OpenFile(string databasePath, ILogger logger)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        try
        {
            connection.Open();
            Exec(connection, "PRAGMA journal_mode=WAL;");
            Exec(connection, "PRAGMA synchronous=FULL;");
            Exec(connection, "PRAGMA busy_timeout=5000;");
            Initialize(connection, logger);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void Initialize(SqliteConnection connection, ILogger logger)
    {
        using var v = connection.CreateCommand();
        v.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(v.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (version > CurrentSchemaVersion)
        {
            // An older agent after a rollback: sync state is a cache that can be rebuilt by a rescan.
            logger.LogWarning("sync.db schema {Version} is newer than this build supports; recreating the sync cache.", version);
            Exec(connection, "DROP TABLE IF EXISTS sync_files; DROP TABLE IF EXISTS sync_roots; DROP TABLE IF EXISTS sync_policy; PRAGMA user_version = 0;");
            version = 0;
        }

        if (version < 1)
        {
            Exec(connection, SchemaV1);
            Exec(connection, "PRAGMA user_version = 1;");
        }
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void MoveAside(string databasePath)
    {
        SqliteConnection.ClearAllPools();
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var p = databasePath + suffix;
            if (File.Exists(p))
            {
                File.Move(p, $"{databasePath}.broken-{stamp}{suffix}", overwrite: true);
            }
        }
    }

    public T Run<T>(Func<SqliteConnection, T> action)
    {
        lock (_sync)
        {
            return action(_connection);
        }
    }

    public void Run(Action<SqliteConnection> action)
    {
        lock (_sync)
        {
            action(_connection);
        }
    }

    public void InTransaction(Action<SqliteConnection, SqliteTransaction> action)
    {
        lock (_sync)
        {
            using var tx = _connection.BeginTransaction();
            action(_connection, tx);
            tx.Commit();
        }
    }

    public static SqliteCommand Command(SqliteConnection c, string sql, SqliteTransaction? tx = null, params (string Name, object? Value)[] parameters)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return cmd;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _connection.Dispose();
        }
    }

    private void Execute(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private const string SchemaV1 = """
        CREATE TABLE IF NOT EXISTS sync_policy(
            connection_id  TEXT PRIMARY KEY,
            document       TEXT NOT NULL,
            effective_hash TEXT NOT NULL,
            paused         INTEGER NOT NULL DEFAULT 0,
            applied_at     TEXT NOT NULL);

        CREATE TABLE IF NOT EXISTS sync_roots(
            root_id            TEXT PRIMARY KEY,
            connection_id      TEXT NOT NULL,
            path               TEXT NOT NULL,
            generation         INTEGER NOT NULL DEFAULT 0,
            state              TEXT NOT NULL DEFAULT 'IDLE',
            failure_code       TEXT,
            next_attempt_at    TEXT,
            last_full_scan_at  TEXT,
            last_checkpoint_at TEXT,
            rescan_requested   INTEGER NOT NULL DEFAULT 1);

        CREATE TABLE IF NOT EXISTS sync_files(
            root_id        TEXT NOT NULL,
            rel_key        TEXT NOT NULL,
            rel_path       TEXT NOT NULL,
            size           INTEGER NOT NULL,
            mtime_ticks    INTEGER NOT NULL,
            sha256         TEXT NOT NULL,
            state          TEXT NOT NULL,
            attempts       INTEGER NOT NULL DEFAULT 0,
            last_error     TEXT,
            cloud_version  TEXT,
            PRIMARY KEY(root_id, rel_key));
        """;
}
