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

    /// <summary>
    /// The Phase-17 request-level repository selector (criterion 1): yields the remote URL of the
    /// repository the current request is authorized to and querying, or null when none is named. The
    /// default (<c>() =&gt; null</c>) keeps the single-node local path byte-identical — the read planner
    /// then resolves the single-repository default exactly as before. The HTTP service replaces it with an
    /// accessor backed by the authenticated request (e.g. an <c>X-Sextant-Repository</c> header) so a
    /// multi-tenant catalog is queryable-and-scoped rather than deny-all.
    /// </summary>
    public Func<string?> RequestedRepository { get; set; } = () => null;

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

    /// <summary>
    /// The single entry point every read tool uses to obtain a ready database AND an authorized, scoped
    /// <see cref="FederatedReadContext"/> in one fail-closed step (Phase 17, criterion 1). It centralizes
    /// the ORDER of the two checks so no tool can leak an existence signal:
    /// <list type="bullet">
    /// <item>Under an ENFORCED policy it authorizes FIRST, before surfacing any readiness/existence
    /// signal. Any failure — a not-ready/unprovisioned index, or a denied read (unauthorized principal,
    /// cross-tenant repository, unidentifiable/nonexistent repository) — collapses to the ONE uniform
    /// not-found response (<see cref="ResponseBuilder.BuildNotFound"/>), so an unauthorized caller cannot
    /// tell any of these cases apart.</item>
    /// <item>On the zero-policy local path it preserves the exact pre-Phase-17 order — the actionable
    /// rebuild/not-ready message first, then the permissive gate (which always allows) — so a single-node
    /// local index stays byte-identical.</item>
    /// </list>
    /// Returns true with a ready <paramref name="database"/> and resolved <paramref name="context"/>; on
    /// false, <paramref name="failureResponse"/> is the ready-made JSON the tool returns verbatim.
    /// </summary>
    public bool TryBeginRead(
        out IndexDatabase database,
        out FederatedReadContext context,
        out string failureResponse,
        FederationMode mode = FederationMode.Federated,
        CompatibilityInputs? compatibility = null)
    {
        database = null!;
        context = null!;

        if (Authorizer.IsEnforcing)
        {
            // Authorize BEFORE any readiness/existence signal. A not-ready index cannot be attributed to
            // an authorized repository, so it fails closed as the SAME uniform not-found as a denial —
            // an unauthorized caller learns neither whether the service is provisioned nor whether their
            // requested repository exists.
            var ready = GetReadyDatabase(out _);
            if (ready == null)
            {
                failureResponse = ResponseBuilder.BuildNotFound();
                return false;
            }

            if (!ReadContextGate.TryResolve(
                    ready, out context, out failureResponse, mode, Authorizer, compatibility, RequestedRepository))
                return false; // the gate already produced the uniform not-found

            database = ready;
            failureResponse = string.Empty;
            return true;
        }

        // Zero-policy local default: readiness first (actionable message), then the permissive gate —
        // byte-identical to the pre-Phase-17 tool bodies.
        database = GetReadyDatabase(out var notReady)!;
        if (database == null)
        {
            failureResponse = ResponseBuilder.BuildEmpty(notReady);
            return false;
        }

        if (!ReadContextGate.TryResolve(
                database, out context, out failureResponse, mode, Authorizer, compatibility, RequestedRepository))
            return false;

        failureResponse = string.Empty;
        return true;
    }

    public void Dispose()
    {
        _db?.Dispose();
    }
}
