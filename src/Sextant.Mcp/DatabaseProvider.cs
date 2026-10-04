using Sextant.Core;
using Sextant.Store;

namespace Sextant.Mcp;

public sealed class DatabaseProvider : IDisposable
{
    private readonly string _dbPath;
    private IndexDatabase? _db;
    private RemoteBaseSnapshotFederation? _remoteFederation;

    public DatabaseProvider(string? dbPath = null, IReadAuthorizer? authorizer = null)
    {
        _dbPath = dbPath ?? SextantConfiguration.FromEnvironment().DbPath;
        Authorizer = authorizer ?? AllowAllReadAuthorizer.Instance;
    }

    /// <summary>
    /// The remote base-snapshot source the query planner falls back to when a request's base snapshot is
    /// absent from the local catalog (issue #60). Null (the default) keeps the pure-local path
    /// byte-identical. Set once at the composition root via <see cref="AttachRemoteFederation"/>.
    /// </summary>
    public IBaseSnapshotSource? RemoteBaseSource { get; private set; }

    /// <summary>
    /// Attaches the composition-root remote federation so its <see cref="RemoteBaseSnapshotFederation.Source"/>
    /// backs <see cref="RemoteBaseSource"/> and its owned <see cref="HttpClient"/> is disposed with this
    /// provider. Idempotent-by-replacement: a previously attached federation is disposed first.
    /// </summary>
    public void AttachRemoteFederation(RemoteBaseSnapshotFederation federation)
    {
        _remoteFederation?.Dispose();
        _remoteFederation = federation;
        RemoteBaseSource = federation.Source;
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

    /// <summary>
    /// Whether the current request MUST name a repository through <see cref="RequestedRepository"/>. When it
    /// returns true and no repository is named, <see cref="TryBeginRead"/> fails with the
    /// <see cref="ResponseBuilder.RepositoryRequiredCode"/> error before touching the database, so the
    /// response depends only on the request and names no repository (it is not an existence oracle). The
    /// default (<c>() =&gt; false</c>) keeps the local path byte-identical: a read with no selector resolves
    /// the single-repository default exactly as before. A host evaluates it per request, so it can require a
    /// selection for some callers only.
    /// </summary>
    public Func<bool> RequireRepositorySelection { get; set; } = () => false;

    /// <summary>
    /// The per-request branch selector (SVC-2): yields the name of the branch the current request reads,
    /// or null for the named repository's default branch. A branch is meaningful only together with a
    /// repository (<see cref="RequestedRepository"/>): a request that names a branch but no repository
    /// fails with the <see cref="ResponseBuilder.RepositoryRequiredCode"/> error in
    /// <see cref="TryBeginRead"/>, and a branch with no complete snapshot resolves to nothing (never the
    /// default branch or the unselected fallback). The default (<c>() =&gt; null</c>) keeps the local path
    /// byte-identical.
    /// </summary>
    public Func<string?> RequestedBranch { get; set; } = () => null;

    /// <summary>
    /// Extra guidance appended to the <see cref="ResponseBuilder.RepositoryRequiredCode"/> error (how to name a
    /// repository, and which ones the caller may read), or null for none. The host evaluates it only when that error
    /// is returned, so it may read the caller's own grants; it must never describe catalog content the caller cannot
    /// read. The default (<c>() =&gt; null</c>) keeps the error's bytes unchanged.
    /// </summary>
    public Func<string?> RepositoryRequiredGuidance { get; set; } = () => null;

    /// <summary>
    /// Whether the repository <see cref="RequestedRepository"/> yields for the current request was selected by the host
    /// because the request named none (e.g. the caller can read exactly one repository). When true, a successful read
    /// stamps the repository and <c>repository_selection: "implicit"</c> into <c>meta.snapshot</c>, so the answer
    /// says which repository it came from. The default (<c>() =&gt; false</c>) leaves provenance unchanged.
    /// </summary>
    public Func<bool> RepositorySelectedImplicitly { get; set; } = () => false;

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
    /// Before either, a request that must select a repository (<see cref="RequireRepositorySelection"/>), or
    /// that names a branch (<see cref="RequestedBranch"/>), but names no repository fails with the
    /// request-shaped <see cref="ResponseBuilder.BuildRepositoryRequired"/> error.
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

        // Checked FIRST, before readiness or authorization, so the verdict depends only on the request. A
        // named branch needs a named repository too: a branch alone selects nothing.
        if ((RequireRepositorySelection() || !string.IsNullOrWhiteSpace(RequestedBranch()))
            && string.IsNullOrWhiteSpace(RequestedRepository()))
        {
            failureResponse = ResponseBuilder.BuildRepositoryRequired(RepositoryRequiredGuidance());
            return false;
        }

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
                    ready, out context, out failureResponse, mode, Authorizer, compatibility, RequestedRepository,
                    RequestedBranch))
                return false; // the gate already produced the uniform not-found

            database = ready;
            context = StampImplicitSelection(context);
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
                database, out context, out failureResponse, mode, Authorizer, compatibility, RequestedRepository,
                RequestedBranch))
            return false;

        context = StampImplicitSelection(context);
        failureResponse = string.Empty;
        return true;
    }

    // Says which repository answered a read the host selected for the caller. Only a READ THAT SUCCEEDED is stamped,
    // and only with the repository the request already resolved to, so nothing is revealed that the read did not serve.
    private FederatedReadContext StampImplicitSelection(FederatedReadContext context)
    {
        if (context.Provenance is not { } provenance || !RepositorySelectedImplicitly()
            || RequestedRepository() is not { Length: > 0 } repository)
            return context;
        return context.WithProvenance(provenance with { Repository = repository, RepositorySelection = "implicit" });
    }

    public void Dispose()
    {
        _remoteFederation?.Dispose();
        _db?.Dispose();
    }
}
