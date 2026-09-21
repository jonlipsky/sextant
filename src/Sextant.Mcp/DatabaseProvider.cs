using Sextant.Core;
using Sextant.Store;

namespace Sextant.Mcp;

public sealed class DatabaseProvider : IDisposable
{
    private readonly string _dbPath;
    private IndexDatabase? _db;

    public DatabaseProvider(string? dbPath = null, IReadAuthorizer? authorizer = null)
    {
        _dbPath = dbPath ?? SextantConfiguration.FromEnvironment().DbPath;
        Authorizer = authorizer ?? AllowAllReadAuthorizer.Instance;
    }

    /// <summary>
    /// The read authorizer every tool threads through <see cref="ReadContextGate"/> and the cross-repo
    /// resolvers (criterion 1). The default is the permissive <see cref="AllowAllReadAuthorizer"/> so a
    /// single-node local index stays zero-friction and byte-identical; the HTTP service injects a real
    /// enforced authorizer that fails closed when a policy is configured.
    /// </summary>
    public IReadAuthorizer Authorizer { get; }

    public bool DatabaseExists => File.Exists(_dbPath);

    public IndexDatabase? GetDatabase()
    {
        if (!DatabaseExists)
            return null;

        _db ??= new IndexDatabase(_dbPath);
        return _db;
    }

    /// <summary>
    /// Returns the open database only when it is servable as a complete index; otherwise returns null
    /// and yields an actionable message the caller returns verbatim (no database, an older/newer schema,
    /// or a compact schema that has never been rebuilt). Centralizes the Phase 7 criterion-6 rebuild
    /// gate so every MCP tool surfaces the same message instead of throwing on the changed schema or
    /// silently returning empty results from a partially rebuilt index.
    /// </summary>
    public IndexDatabase? GetReadyDatabase(out string notReadyMessage)
    {
        var db = GetDatabase();
        if (db == null)
        {
            notReadyMessage = "No index database found.";
            return null;
        }

        var readiness = db.CheckReadiness();
        if (!readiness.Ready)
        {
            notReadyMessage = readiness.Message!;
            return null;
        }

        notReadyMessage = string.Empty;
        return db;
    }

    public void Dispose()
    {
        _db?.Dispose();
    }
}
