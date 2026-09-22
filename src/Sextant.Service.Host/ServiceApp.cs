using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Sextant.Mcp;
using Sextant.Mcp.Tools;
using Sextant.Service;
using Sextant.Service.Observability;
using Sextant.Store;

namespace Sextant.Service.Host;

/// <summary>
/// The composition root for the standalone index service's HTTP surface. It deliberately SEPARATES the
/// control plane from the query plane (spec: separate control endpoints from query endpoints):
/// <list type="bullet">
///   <item><c>/health</c> — service AVAILABILITY (is the process up + catalog reachable), no auth.</item>
///   <item><c>/ready</c> — worker CAPACITY (can this node accept production work), no auth; 503 when a
///   query-only node has no worker, so an operator can tell "up" from "can index".</item>
///   <item><c>/control/*</c> — ensure-snapshot, status, branch resolution, retention. Requires the CONTROL
///   token.</item>
///   <item><c>/mcp</c> + <c>/query/*</c> — authenticated HTTP MCP semantic queries and remote base-snapshot
///   pages. Requires the QUERY token. Criterion 4: a complete snapshot is queryable here WITHOUT
///   ProcessStack.</item>
/// </list>
/// A null token disables that plane's auth (local single-node development). Query reads use a connection
/// independent of the service's writer, so a running index never blocks a low-latency query.
/// </summary>
public static class ServiceApp
{
    /// <summary>Registers the service, MCP tools, and read database provider into the builder's container.</summary>
    public static void RegisterServices(WebApplicationBuilder builder, ServiceOptions options, SnapshotService service)
    {
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(service);
        // Bind request bodies with the SAME snake_case wire format the service emits (ServiceJson), so a
        // control request body like { "repository_remote_url": ... } binds to the contract records.
        builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower;
            o.SerializerOptions.DefaultIgnoreCondition =
                System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
        });
        // A SEPARATE read connection for queries (Phase-9 WAL supports concurrent readers with the writer),
        // so the /mcp + /query planes never block behind a running control-plane index. When a read policy
        // is configured (Phase 17, criterion 1) the provider carries a real fail-closed PolicyReadAuthorizer
        // resolved from the ambient HTTP principal; with no policy it stays the permissive local default so
        // single-node operation is byte-identical.
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(sp =>
        {
            IReadAuthorizer authorizer = options.ReadPolicy.Enabled
                ? new PolicyReadAuthorizer(
                    options.ReadPolicy,
                    PrincipalTokenAccessor(sp.GetRequiredService<IHttpContextAccessor>()),
                    RepositoryUrlResolver(options.CatalogDbPath))
                : AllowAllReadAuthorizer.Instance;
            var provider = new DatabaseProvider(options.CatalogDbPath, authorizer);

            // Under an enforced multi-tenant policy the request names its authorized repository via the
            // X-Sextant-Repository header, so the read planner pins THAT repository's snapshot instead of
            // deny-all (Phase 17, criterion 1). The accessor reads the ambient request at call time, so the
            // singleton provider stays correct under concurrent requests. With no policy the default
            // (() => null) keeps the single-repository local path byte-identical.
            if (options.ReadPolicy.Enabled)
                provider.RequestedRepository =
                    RequestedRepositoryAccessor(sp.GetRequiredService<IHttpContextAccessor>());

            return provider;
        });
        // Explicit ALLOWLIST for the remote HTTP MCP surface (hardening review, criterion 1): register ONLY
        // the index-query tools that route through DatabaseProvider.TryBeginRead and thus enforce the
        // fail-closed read authorizer + per-repository scope. Assembly-wide registration would also expose
        // local-only tools that bypass the gate — get_source_context (reads an ARBITRARY absolute path off
        // disk) and get_daemon_status (probes a local daemon) — which on a multi-tenant service is an
        // arbitrary-file-read / cross-tenant leak. A default-deny allowlist also means a newly added tool is
        // NOT silently exposed remotely until it is vetted and added here. The local stdio server
        // (McpServerSetup) keeps the full assembly-wide set — it is the zero-policy single-node path.
        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithTools(RemoteQueryTools);
    }

    /// <summary>
    /// The vetted tool types exposed over the remote HTTP MCP surface. Every one takes a
    /// <see cref="DatabaseProvider"/> and enters through <c>TryBeginRead</c> (fail-closed authz + scope).
    /// Local-only tools that bypass that gate (<c>get_source_context</c>, <c>get_daemon_status</c>) are
    /// deliberately EXCLUDED so they are never reachable by a remote principal.
    /// </summary>
    internal static readonly IReadOnlyList<Type> RemoteQueryTools =
    [
        typeof(FindSymbolTool), typeof(FindReferencesTool), typeof(FindByAttributeTool),
        typeof(FindBySignatureTool), typeof(FindCommentsTool), typeof(FindTestsTool),
        typeof(FindUnreferencedTool), typeof(FindCrossRepositoryUsagesTool), typeof(FindSubmoduleConsumersTool),
        typeof(GetApiSurfaceTool), typeof(GetCallHierarchyTool), typeof(GetFileSymbolsTool),
        typeof(GetImpactTool), typeof(GetImplementorsTool), typeof(GetIndexStatusTool),
        typeof(GetNamespaceTreeTool), typeof(GetProjectDependenciesTool), typeof(GetTypeDependentsTool),
        typeof(GetTypeHierarchyTool), typeof(GetTypeMembersTool), typeof(SemanticSearchTool),
        typeof(TraceValueTool), typeof(ResearchCodebaseTool)
    ];

    /// <summary>Wires the auth middleware and maps the control/query/health endpoints onto a built app.</summary>
    public static void MapEndpoints(WebApplication app, ServiceOptions options)
    {
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            if (path.StartsWithSegments("/control"))
            {
                // Bind isolation (issue #61): when a distinct query port is configured, the control plane is
                // reachable ONLY on the control port. A control request arriving on the public query port is
                // a uniform 404 — it must not even reveal that the control plane exists on that port.
                if (WrongPlanePort(context, options, controlPath: true)) { await NotFound(context); return; }

                // Least-privilege contributor token (issue #71): /control/contribute additionally accepts a
                // dedicated ContributeToken so a CI/client contributor never needs the full control token.
                // Every OTHER control endpoint still requires the control token, so a contributor token
                // cannot reach ensure/status/resolve/retention.
                if (path.StartsWithSegments("/control/contribute"))
                {
                    if (!AuthorizedForContribution(context, options)) { await Deny(context); return; }
                }
                else if (!Authorized(context, options.ControlToken)) { await Deny(context); return; }
            }
            else if (path.StartsWithSegments("/mcp") || path.StartsWithSegments("/query"))
            {
                // Bind isolation (issue #61): the query plane is reachable ONLY on the query port when one is
                // configured; a query request on the internal control port is a uniform 404.
                if (WrongPlanePort(context, options, controlPath: false)) { await NotFound(context); return; }
                if (!AuthorizedForQuery(context, options)) { await Deny(context); return; }
            }
            await next();
        });

        // Query-plane latency instrumentation (criterion 5, query latency): times /mcp + /query requests
        // and feeds the live ServiceMetrics histogram. Runs only for the query planes so control-plane
        // operator calls do not pollute the query-latency signal. A request counts as FAILED when it
        // returns a 5xx OR when the pipeline throws — an unhandled exception unwinds through this finally
        // while the response status is still 200, so we must observe the throw explicitly rather than
        // trusting the status code, otherwise server-side faults would be mis-recorded as successes.
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            if (!path.StartsWithSegments("/mcp") && !path.StartsWithSegments("/query"))
            {
                await next();
                return;
            }
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var threw = false;
            try
            {
                await next();
            }
            catch
            {
                threw = true;
                throw;
            }
            finally
            {
                var elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var service = context.RequestServices.GetService<SnapshotService>();
                service?.Metrics.RecordQuery(elapsedMs, failed: threw || context.Response.StatusCode >= 500);
            }
        });

        MapHealth(app);
        MapControl(app);
        MapQuery(app, options);
        app.MapMcp("/mcp");
    }

    private static void MapHealth(WebApplication app)
    {
        // AVAILABILITY: the service process is up and can answer. Always 200 while running.
        app.MapGet("/health", (SnapshotService service) =>
            Results.Json(new { status = service.IsAvailable ? "available" : "unavailable" }, ServiceJson.Options));

        // READINESS: worker CAPACITY. 503 when this node cannot accept production work (query-only), so an
        // operator distinguishes service availability from worker capacity.
        app.MapGet("/ready", (SnapshotService service) =>
            service.HasWorkerCapacity
                ? Results.Json(new { status = "ready", worker_capacity = true }, ServiceJson.Options)
                : Results.Json(new { status = "no_worker_capacity", worker_capacity = false }, ServiceJson.Options,
                    statusCode: StatusCodes.Status503ServiceUnavailable));
    }

    private static void MapControl(WebApplication app)
    {
        var control = app.MapGroup("/control");

        control.MapPost("/ensure", async (EnsureSnapshotRequest request, HttpRequest req, SnapshotService service, CancellationToken ct) =>
        {
            var result = await service.EnsureSnapshotAsync(request, ct, ExtractBearer(req));
            return Results.Json(result, ServiceJson.Options);
        });

        control.MapGet("/status/{jobId:long}", (long jobId, SnapshotService service) =>
        {
            var status = service.GetStatus(jobId);
            return status is null ? Results.NotFound() : Results.Json(status, ServiceJson.Options);
        });

        control.MapGet("/resolve", (string repository, string? branch, SnapshotService service) =>
        {
            var row = service.ResolveBranch(repository, branch);
            return row is null ? Results.NotFound() : Results.Json(row, ServiceJson.Options);
        });

        control.MapPost("/retention", (bool? execute, HttpRequest req, SnapshotService service) =>
        {
            var report = service.RunRetention(execute ?? false, ExtractBearer(req));
            return Results.Json(report, ServiceJson.Options);
        });

        // Observability surface (criterion 5). These live under /control so they inherit the CONTROL-token
        // gate — they are OPERATOR-ONLY and must NEVER be reachable by a query-plane tenant, because they
        // aggregate cross-tenant repository scopes, counts, and cost (criterion-1 leakage guard).

        // Metrics: JSON by default, or Prometheus text exposition with ?format=prometheus for a scraper.
        control.MapGet("/metrics", (string? format, SnapshotService service) =>
        {
            var snapshot = service.CollectMetrics();
            return string.Equals(format, "prometheus", StringComparison.OrdinalIgnoreCase)
                ? Results.Text(PrometheusExposition.Render(snapshot), "text/plain; version=0.0.4")
                : Results.Json(snapshot, ServiceJson.Options);
        });

        // Durable audit trail (security + cost attribution). Optional action/repository filters.
        control.MapGet("/audit", (int? limit, string? action, string? repository, SnapshotService service) =>
        {
            var entries = service.RecentAudit(limit ?? 100, action, repository);
            return Results.Json(new { entries, result_count = entries.Count }, ServiceJson.Options);
        });

        // Pilot readiness gate (criterion 7): evaluates the documented exit criteria for a workload class,
        // including the issue-#76 hard precondition for untrusted multi-tenant pilots. The security-
        // relevant capabilities (#76 hard isolation, a proven backup, a secured control plane) are derived
        // from the SERVICE's actual state, never from request parameters — a caller must not be able to
        // assert the #76 precondition into existence by passing a query flag.
        control.MapGet("/pilot", (string? workload, SnapshotService service) =>
        {
            var workloadClass = string.Equals(workload, "untrusted", StringComparison.OrdinalIgnoreCase)
                ? Rollout.PilotWorkloadClass.UntrustedMultiTenant
                : Rollout.PilotWorkloadClass.TrustedSingleTenant;
            var report = service.EvaluatePilotReadiness(workloadClass);
            return Results.Json(report, ServiceJson.Options);
        });

        // Backup (criterion 6). Writes a consistent catalog + artifact backup to the requested directory.
        // Restore runs OFFLINE via `sextant service restore` (it must precede service startup).
        control.MapPost("/backup", (string dir, HttpRequest req, SnapshotService service) =>
        {
            if (string.IsNullOrWhiteSpace(dir))
                return Results.BadRequest("query parameter 'dir' is required.");
            var manifest = service.CreateBackup(dir, ExtractBearer(req));
            return Results.Json(manifest, ServiceJson.Options);
        });

        // Client/CI contribution ingest (Phase 16). The raw artifact bytes are the request body; finalize /
        // branch / default_branch are query params. The contributor identity is the bearer token (the
        // control token already gated entry; the authorizer sees the same token). The whole supply-chain
        // gate (authz + hash/capability verify) runs inside the service; a rejected contribution returns 422
        // with structured diagnostics and is never published (acceptance criterion 3).
        control.MapPost("/contribute", async (HttpRequest req, SnapshotService service, CancellationToken ct) =>
        {
            // Let the SERVICE's own MaxArtifactBytes cap govern the untrusted upload, enforced INCREMENTALLY
            // as the body streams in (SnapshotService.IngestContributionAsync(Stream,…)), so an oversized
            // upload is rejected without buffering it whole. Lift the transport's default request-body limit
            // for this one endpoint so it cannot pre-empt our cap with an opaque 413/400 (the artifact cap is
            // the single source of truth on this boundary).
            var sizeFeature = req.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false })
                sizeFeature.MaxRequestBodySize = null;

            // Parse the booleans STRICTLY: an ABSENT value takes the documented default, but an UNPARSEABLE
            // value is a 400 — never silently coerced. `finalize` defaults true (single-environment
            // contract), so a typo'd `?finalize=maybe` must not silently publish an incomplete assembly.
            var finalize = true;
            if (req.Query.ContainsKey("finalize") && !bool.TryParse(req.Query["finalize"], out finalize))
                return Results.BadRequest("query parameter 'finalize' must be 'true' or 'false'.");
            var isDefault = false;
            if (req.Query.ContainsKey("default_branch") && !bool.TryParse(req.Query["default_branch"], out isDefault))
                return Results.BadRequest("query parameter 'default_branch' must be 'true' or 'false'.");
            var branch = req.Query["branch"].ToString();

            var result = await service.IngestContributionAsync(
                req.Body,
                ExtractBearer(req),
                finalize,
                string.IsNullOrWhiteSpace(branch) ? null : branch,
                isDefault,
                ct);

            return Results.Json(result, ServiceJson.Options,
                statusCode: result.Accepted ? StatusCodes.Status200OK : StatusCodes.Status422UnprocessableEntity);
        });
    }

    private static string? ExtractBearer(HttpRequest req)
    {
        var header = req.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim()
            : string.IsNullOrWhiteSpace(header) ? null : header;
    }

    private static void MapQuery(WebApplication app, ServiceOptions options)
    {
        // Remote base-snapshot paging (issue #51): serves one immutable page of a snapshot's symbols by its
        // portable identity hash, with a stable cursor. Uses a short-lived read connection per request so
        // it never contends with the writer or with concurrent queries on a shared connection.
        app.MapGet("/query/snapshots/{identityHash}/symbols",
            async (string identityHash, long? cursor, int? limit, HttpContext http, CancellationToken ct) =>
            {
                await using var connection = OpenReadConnection(options.CatalogDbPath);

                // Under an enabled read policy, a query-plane principal may only page a snapshot belonging to
                // a repository it is authorized to read. An unauthorized OR unknown snapshot returns an
                // IDENTICAL 404, so this artifact surface is not a cross-tenant existence/artifact oracle
                // (criterion 1: reveal no artifact access). With no policy configured this is a no-op.
                if (options.ReadPolicy.Enabled && !AuthorizedForSnapshot(connection, options, http, identityHash))
                    return Results.NotFound();

                var source = new LocalBaseSnapshotSource(connection);
                var page = await source.FetchSymbolsAsync(new SnapshotPageRequest
                {
                    IdentityHash = identityHash,
                    Cursor = cursor?.ToString(),
                    Limit = limit ?? 500
                }, ct);
                return Results.Json(page, ServiceJson.Options);
            });
    }

    private static SqliteConnection OpenReadConnection(string dbPath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = true
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    /// <summary>
    /// Authorizes a federation snapshot-page request against the enabled read policy: the requesting
    /// principal must be granted the snapshot's repository. An unknown snapshot is treated as unauthorized
    /// so the caller cannot distinguish "forbidden" from "absent" (criterion 1, no artifact/existence
    /// oracle). Only called when <see cref="ReadAuthorizationPolicy.Enabled"/>.
    /// </summary>
    private static bool AuthorizedForSnapshot(SqliteConnection conn, ServiceOptions options, HttpContext http, string identityHash)
    {
        var snapshot = new SnapshotStore(conn).GetByIdentityHash(identityHash);
        if (snapshot is null)
            return false;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT remote_url FROM repositories WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", snapshot.RepositoryId);
        var url = cmd.ExecuteScalar() as string;
        return url is not null && options.ReadPolicy.Allows(BearerToken(http), url);
    }

    private static bool Authorized(HttpContext context, string? expectedToken)
    {
        if (string.IsNullOrEmpty(expectedToken))
            return true; // No token configured for this plane → open (local development default).

        return Matches(context, expectedToken);
    }

    /// <summary>
    /// Query-plane authentication. When a read policy is configured (Phase 17, criterion 1) the plane
    /// authenticates KNOWN principals — the per-repository authorization then happens fail-closed inside
    /// <see cref="PolicyReadAuthorizer"/>. With no policy it falls back to the single shared query token
    /// (byte-identical to before).
    /// </summary>
    private static bool AuthorizedForQuery(HttpContext context, ServiceOptions options)
    {
        if (options.ReadPolicy.Enabled)
            return options.ReadPolicy.IsKnownPrincipal(BearerToken(context));

        return Authorized(context, options.QueryToken);
    }

    /// <summary>
    /// Contribution-endpoint authentication (issue #71). The full control token is a superset and always
    /// authorizes; a configured least-privilege contributor token also authorizes. When NEITHER token is
    /// configured the endpoint is open (dev default) — matching the plane-open semantics elsewhere. A null
    /// token is never treated as a credential to match, so a locked control plane is not silently opened by
    /// an unset contributor token.
    /// </summary>
    private static bool AuthorizedForContribution(HttpContext context, ServiceOptions options)
    {
        if (string.IsNullOrEmpty(options.ControlToken) && string.IsNullOrEmpty(options.ContributeToken))
            return true;

        return Matches(context, options.ControlToken) || Matches(context, options.ContributeToken);
    }

    /// <summary>Extracts the raw bearer token from the Authorization header, or null when absent.</summary>
    private static string? BearerToken(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.Ordinal) ? header[prefix.Length..].Trim() : null;
    }

    /// <summary>
    /// Constant-time bearer-token match. Returns false for a null/empty expected token (an unset token is
    /// NOT a credential), so it can be OR-combined for the contribution superset without an unset token
    /// opening a locked plane.
    /// </summary>
    private static bool Matches(HttpContext context, string? expectedToken)
    {
        if (string.IsNullOrEmpty(expectedToken))
            return false;

        return BearerToken(context) is { } provided && CryptographicEquals(provided, expectedToken);
    }

    /// <summary>Resolves the ambient request principal's bearer token for <see cref="PolicyReadAuthorizer"/>.</summary>
    private static Func<string?> PrincipalTokenAccessor(IHttpContextAccessor accessor) =>
        () => accessor.HttpContext is { } ctx ? BearerToken(ctx) : null;

    /// <summary>
    /// Resolves the caller-declared authorized repository (its git remote URL) from the
    /// <c>X-Sextant-Repository</c> request header for the Phase-17 multi-tenant read selector. Reads the
    /// ambient request at call time so a singleton <see cref="DatabaseProvider"/> stays request-correct;
    /// returns null when the header is absent/blank (the read planner then denies rather than guessing).
    /// The header only NAMES the repository — authorization is still enforced by
    /// <see cref="PolicyReadAuthorizer"/> against the principal's token, so a caller cannot read another
    /// tenant merely by naming it.
    /// </summary>
    private static Func<string?> RequestedRepositoryAccessor(IHttpContextAccessor accessor) =>
        () =>
        {
            if (accessor.HttpContext is not { } ctx)
                return null;
            var value = ctx.Request.Headers["X-Sextant-Repository"].ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        };

    /// <summary>
    /// Maps a repository row id to its remote URL for <see cref="PolicyReadAuthorizer"/>, caching results
    /// (a repository's remote URL is immutable) so an authorization check does not re-open the catalog per
    /// call. Uses an independent short-lived read connection so it never contends with the writer.
    /// </summary>
    private static Func<long, string?> RepositoryUrlResolver(string dbPath)
    {
        var cache = new System.Collections.Concurrent.ConcurrentDictionary<long, string?>();
        return id => cache.GetOrAdd(id, key =>
        {
            using var conn = OpenReadConnection(dbPath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT remote_url FROM repositories WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", key);
            return cmd.ExecuteScalar() as string;
        });
    }

    private static Task Deny(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        return context.Response.WriteAsync("Unauthorized");
    }

    /// <summary>
    /// Control/query bind isolation (issue #61). Returns true when a request has reached the WRONG plane's
    /// port and must be refused: when a distinct query port is configured, control endpoints are served ONLY
    /// on the control port and query endpoints ONLY on the query port, so an operator can expose just the
    /// query port publicly while keeping the control plane on an internal interface (criterion 1 surface
    /// hardening). It is a NO-OP when no separate query port is configured (planes share a port, the
    /// pre-Phase-17 behavior) or under the in-memory TestServer, whose connection reports port 0.
    /// </summary>
    private static bool WrongPlanePort(HttpContext context, ServiceOptions options, bool controlPath)
    {
        var port = context.Connection.LocalPort;
        if (port == 0)
            return false; // TestServer / no real socket → isolation not applicable.
        return IsWrongPlanePort(port, options.ControlPort, options.QueryPort, controlPath);
    }

    /// <summary>
    /// The pure bind-isolation decision (issue #61), split out for unit testing. Returns true when a request
    /// on <paramref name="localPort"/> has reached the wrong plane. No-op (false) when no distinct query
    /// port is configured. When a distinct query port is set, control endpoints belong on the control port
    /// and query endpoints on the query port; anything else is refused.
    /// </summary>
    internal static bool IsWrongPlanePort(int localPort, int controlPort, int? queryPort, bool controlPath)
    {
        if (queryPort is not int qp || qp == controlPort)
            return false;
        return controlPath ? localPort != controlPort : localPort != qp;
    }

    /// <summary>
    /// Uniform 404 used to HIDE a plane that exists on a different port (no cross-plane oracle). It emits the
    /// SAME response an unmapped route produces — status 404 with an empty body and no extra headers — so a
    /// wrong-plane request is byte-for-byte indistinguishable from a route that does not exist at all
    /// (issue #61 / criterion 1). Writing any body here (e.g. "Not Found") would itself be the oracle.
    /// </summary>
    private static Task NotFound(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }

    // Constant-time comparison so a token check does not leak length/content via timing.
    private static bool CryptographicEquals(string a, string b)
    {
        var ba = System.Text.Encoding.UTF8.GetBytes(a);
        var bb = System.Text.Encoding.UTF8.GetBytes(b);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(ba, bb);
    }
}
