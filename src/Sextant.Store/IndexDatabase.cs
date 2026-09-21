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

    /// <summary>
    /// Opens a FRESH, short-lived reader connection independent of the single writer connection, so
    /// concurrent MCP/query reads never share one <see cref="SqliteConnection"/> (issue #57 —
    /// <see cref="SqliteConnection"/> is not thread-safe, and a shared reader corrupts under parallel
    /// <c>/mcp</c> tool invocations). WAL lets these readers run alongside the writer without blocking.
    /// The connection is pooled (open/close is cheap) and PRIVATE-cache (true concurrent readers, unlike
    /// shared-cache which serializes them); the CALLER owns disposal (wrap in <c>using</c>). Uses
    /// ReadWrite mode deliberately: a read-only WAL connection can fail to open when the <c>-wal</c>/
    /// <c>-shm</c> sidecars must be created. Mirrors the proven <c>/query</c> read-connection recipe.
    /// </summary>
    public SqliteConnection OpenReadConnection()
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = true
        };
        var conn = new SqliteConnection(csb.ToString());
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
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

    /// <summary>
    /// Applies pending migrations, then reconciles the database via <see cref="Recover"/>.
    /// </summary>
    /// <param name="recover">
    /// When true (default) run <see cref="Recover"/> after migrating. Pass false to apply schema DDL
    /// ONLY — used by a cooperative writer (issue #38) that must acquire the writer lease BEFORE
    /// recovery so it never abandons a live writer's staging generation. That caller applies DDL with
    /// <c>recover:false</c>, acquires the lease, and only then calls <see cref="Recover"/> itself.
    /// </param>
    public void RunMigrations(bool recover = true)
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

        if (recover)
            Recover();
    }

    /// <summary>The highest migration version embedded in this build.</summary>
    public static int LatestSchemaVersion => LoadMigrations().Max(m => m.version);

    /// <summary>The schema version currently recorded in the database file (0 if none).</summary>
    public int CurrentSchemaVersion
    {
        get
        {
            var conn = GetConnection();
            EnsureSchemaVersionTable(conn);
            return GetSchemaVersion(conn);
        }
    }

    /// <summary>
    /// Reports whether this database can be served as a complete index, or must be rebuilt first.
    /// Criterion 6 (Phase 7): opening an index built at an older, incompatible schema — or one whose
    /// compact schema has been applied but never re-populated by a full run — must surface an
    /// actionable rebuild message and never be mistaken for a complete new-generation index. The two
    /// signals are the <c>schema_version</c> and the <c>index_runs</c> last-complete pointer.
    /// </summary>
    public IndexReadiness CheckReadiness()
    {
        // Use a fresh per-call connection, NOT the shared memoized GetConnection(): every MCP tool invocation
        // runs this readiness check, and concurrent /mcp requests would otherwise execute these commands
        // simultaneously on the single non-thread-safe shared connection (issue #57 residual / #15). A pooled
        // read connection gives each caller its own handle so readiness is concurrency-safe like the queries.
        using var conn = OpenReadConnection();
        EnsureSchemaVersionTable(conn);
        var current = GetSchemaVersion(conn);
        var expected = LatestSchemaVersion;

        if (current < expected)
            return IndexReadiness.NotReady(
                $"This index was built with an older Sextant schema (v{current}) and is incompatible with this build (v{expected}). " +
                "The Phase 7 compaction changed the on-disk format, so the old data cannot be reused. " +
                $"Rebuild it with a full index (e.g. `sextant index`) at '{_dbPath}'.");

        if (current > expected)
            return IndexReadiness.NotReady(
                $"This index was built with a newer Sextant schema (v{current}) than this build supports (v{expected}). Upgrade Sextant.");

        // Schema is current. Decide whether a complete generation is actually available.
        var hasRunLedger = TableExists(conn, "index_runs");
        var hasComplete = hasRunLedger && new IndexRunStore(conn).GetLastCompleteRun() != null;
        var hasAnyRun = hasRunLedger && CountRows(conn, "index_runs") > 0;
        var hasSymbols = TableExists(conn, "symbols") && CountRows(conn, "symbols") > 0;

        // Ready when a complete generation is published, OR — for a directly-populated database with no
        // run-ledger entries at all (e.g. a seeded test) — when symbols are present. A database that has
        // run entries but no COMPLETE one is a partial or abandoned rebuild (the first post-migration
        // run committed some batches, then crashed before publishing): it has symbols but must NOT be
        // served as a finished index. A freshly-migrated compact index has neither symbols nor runs.
        if (!hasComplete && !(hasSymbols && !hasAnyRun))
            return IndexReadiness.NotReady(
                $"The index schema is current (v{expected}) but no complete index generation exists yet — the compact schema requires a full rebuild. " +
                $"Run a full index (e.g. `sextant index`) at '{_dbPath}'.");

        return IndexReadiness.ReadyIndex;
    }

    private static long CountRows(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM \"{table}\";";
        return Convert.ToInt64(cmd.ExecuteScalar());
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
