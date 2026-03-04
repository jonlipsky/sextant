using System.Reflection;
using Microsoft.Data.Sqlite;

namespace Sextant.Store;

public sealed class IndexDatabase : IDisposable
{
    private readonly string _connectionString;
    private SqliteConnection? _connection;

    public IndexDatabase(string dbPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public SqliteConnection GetConnection()
    {
        if (_connection != null)
            return _connection;

        _connection = new SqliteConnection(_connectionString);
        _connection.Open();
        ConfigurePragmas(_connection);
        return _connection;
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

    private static void ConfigurePragmas(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;
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
