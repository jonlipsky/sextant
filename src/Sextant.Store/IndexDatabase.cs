using System.Reflection;
using Microsoft.Data.Sqlite;

namespace Sextant.Store;

public sealed class IndexDatabase : IDisposable
{
    private readonly string _connectionString;
    private readonly string _dbPath;
    private readonly IndexWriteOptions _writeOptions;
    private SqliteConnection? _connection;

    public IndexDatabase(string dbPath, IndexWriteOptions? writeOptions = null)
    {
        _dbPath = Path.GetFullPath(dbPath);
        _writeOptions = writeOptions ?? IndexWriteOptions.Default;

        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    /// <summary>Absolute path to the main database file.</summary>
    public string DbPath => _dbPath;

    /// <summary>Write-path tuning (batch size, WAL controls, retry) applied to this database.</summary>
    public IndexWriteOptions WriteOptions => _writeOptions;

    /// <summary>Current size of the main database file in bytes (0 if it does not exist yet).</summary>
    public long MainDbBytes => FileLengthOrZero(_dbPath);

    /// <summary>Current size of the write-ahead log in bytes (0 if it does not exist).</summary>
    public long WalBytes => FileLengthOrZero(_dbPath + "-wal");

    /// <summary>Current size of the shared-memory index file in bytes (0 if it does not exist).</summary>
    public long ShmBytes => FileLengthOrZero(_dbPath + "-shm");

    private static long FileLengthOrZero(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Checkpoints the write-ahead log and truncates it, folding pending WAL pages into
    /// the main database file so <see cref="MainDbBytes"/> reflects the final size.
    /// </summary>
    public void Checkpoint()
    {
        var conn = GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        cmd.ExecuteNonQuery();
    }

    /// <summary>Opens a bounded, batched write session over the single writer connection.</summary>
    public IndexWriteSession BeginWriteSession(IndexWriteOptions? options = null)
        => new(GetConnection(), options ?? _writeOptions, InvalidateConnection);

    public SqliteConnection GetConnection()
    {
        if (_connection != null)
            return _connection;

        _connection = new SqliteConnection(_connectionString);
        _connection.Open();
        ConfigurePragmas(_connection);
        return _connection;
    }

    /// <summary>
    /// Discards the memoized writer connection so the next <see cref="GetConnection"/> opens a fresh
    /// one. Called when a write session's rollback fails non-benignly and the connection's transaction
    /// state is unknown: reusing it could run the next write inside a still-open transaction. The next
    /// caller re-runs the configured pragmas on the new connection.
    /// </summary>
    public void InvalidateConnection()
    {
        try
        {
            _connection?.Dispose();
        }
        catch (SqliteException)
        {
            // The connection is already in a bad state; dropping the reference is what matters.
        }
        _connection = null;
    }

    public SqliteConnection CreateReadOnlyConnection()
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = new SqliteConnectionStringBuilder(_connectionString).DataSource,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        };
        var conn = new SqliteConnection(csb.ToString());
        conn.Open();
        return conn;
    }

    private void ConfigurePragmas(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
            PRAGMA wal_autocheckpoint = {_writeOptions.WalAutocheckpointPages};
            PRAGMA journal_size_limit = {_writeOptions.JournalSizeLimitBytes};
            """;
        cmd.ExecuteNonQuery();
    }

    public void RunMigrations()
    {
        var conn = GetConnection();
        EnsureSchemaVersionTable(conn);

        var currentVersion = GetSchemaVersion(conn);
        var migrations = LoadMigrations();

        foreach (var (version, sql) in migrations.Where(m => m.version > currentVersion).OrderBy(m => m.version))
        {
            using var transaction = conn.BeginTransaction();
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();

                SetSchemaVersion(conn, transaction, version);
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        Recover();
    }

    /// <summary>
    /// Startup recovery. Folds a valid write-ahead log into the main database (recovering a WAL left
    /// by a prior process and truncating it), then abandons any staging generations that a dead
    /// process left behind so a partially written run can never be mistaken for the current index.
    /// Safe to call on every open; a no-op on a clean, fully checkpointed database.
    /// </summary>
    public void Recover()
    {
        var conn = GetConnection();

        try
        {
            using var checkpoint = conn.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            checkpoint.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // A busy/locked checkpoint is non-fatal; the WAL is still valid and folds in later.
        }

        if (!TableExists(conn, "index_runs"))
            return;

        new IndexRunStore(conn).AbandonStaleRuns(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private static bool TableExists(SqliteConnection conn, string name)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name;";
        cmd.Parameters.AddWithValue("@name", name);
        return cmd.ExecuteScalar() != null;
    }

    private static void EnsureSchemaVersionTable(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_version (
                version INTEGER NOT NULL,
                applied_at INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private static int GetSchemaVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void SetSchemaVersion(SqliteConnection conn, SqliteTransaction transaction, int version)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "INSERT INTO schema_version (version, applied_at) VALUES (@version, @applied_at);";
        cmd.Parameters.AddWithValue("@version", version);
        cmd.Parameters.AddWithValue("@applied_at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }

    private static List<(int version, string sql)> LoadMigrations()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var prefix = "Sextant.Store.Migrations.";
        var result = new List<(int version, string sql)>();

        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix) && n.EndsWith(".sql")))
        {
            var fileName = name[prefix.Length..];
            if (int.TryParse(fileName.Split('_')[0], out var version))
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream);
                result.Add((version, reader.ReadToEnd()));
            }
        }

        return result;
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }
}
