using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sextant.Mcp;
using Sextant.Mcp.Tools;
using Sextant.Service;
using Sextant.Service.CallerIdentity;
using Sextant.Service.Grants;
using Sextant.Service.Observability;
using Sextant.Service.Search;
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
///   token. A user caller (a verified <c>act=user</c> assertion) reaches only ensure, status, resolve and the grant
///   routes (<see cref="ControlCallerRules"/>).</item>
///   <item><c>/mcp</c> + <c>/query/*</c> — authenticated HTTP MCP semantic queries and remote base-snapshot
///   pages. Requires the QUERY token (or a read-policy principal, or a delegate token whose reads are decided
///   per verified caller assertion, SVC-3 <see cref="CallerAssertionGate"/>). Criterion 4: a complete snapshot
///   is queryable here WITHOUT ProcessStack.</item>
/// </list>
/// A null token disables that plane's auth (local single-node development). Query reads use a connection
/// independent of the service's writer, so a running index never blocks a low-latency query.
/// </summary>
public static class ServiceApp
{
    /// <summary>The request header that names the repository a query-plane read selects.</summary>
    internal const string RepositoryHeader = "X-Sextant-Repository";

    /// <summary>Registers the service, MCP tools, and read database provider into the builder's container.</summary>
    public static void RegisterServices(WebApplicationBuilder builder, ServiceOptions options, SnapshotService service)
    {
        // SVC-3: refuse inconsistent caller-identity settings (fail closed), however the options were built.
        options.ValidateCallerIdentity();
        // SX-6d: refuse an unauthenticated control plane unless it was explicitly (and loudly) opted into.
        options.ValidateControlPlane();
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
        builder.Services.AddSingleton(sp => new CallerAssertionGate(
            options, sp.GetService<TimeProvider>() ?? TimeProvider.System, sp.GetRequiredService<ILoggerFactory>()));
        // SVC-4: the current request's verified caller, for the service-side list_repositories tool.
        builder.Services.AddSingleton(sp => new CallerContext(
            CallerAssertionGate.CallerPrincipalAccessor(sp.GetRequiredService<IHttpContextAccessor>())));
        builder.Services.AddSingleton(sp =>
        {
            var http = sp.GetRequiredService<IHttpContextAccessor>();
            var gate = sp.GetRequiredService<CallerAssertionGate>();
            var repositoryUrls = RepositoryUrlResolver(options.CatalogDbPath);
            IReadAuthorizer authorizer = options.ReadPolicy.Enabled
                ? new PolicyReadAuthorizer(
                    options.ReadPolicy,
                    PrincipalTokenAccessor(http),
                    repositoryUrls)
                : AllowAllReadAuthorizer.Instance;
            // SVC-3/SVC-4: a delegate-token read is decided per verified caller by its grants (a request with no
            // caller sees nothing), so a delegate token opens nothing on its own; every other request keeps the
            // authorizer above.
            if (gate.DelegateTokensConfigured)
                authorizer = new CallerReadAuthorizer(
                    authorizer,
                    new GrantReadAuthorizer(CallerVisibility.Accessor(http, service), repositoryUrls),
                    gate.DelegateRequestAccessor(http));

            // The request names the repository it reads via the X-Sextant-Repository header or, per call, the
            // reserved `repository`/`branch` tool arguments (SVC-2, ToolSelectionFilters), and the read planner
            // pins THAT repository's default-branch (or named-branch) snapshot. It is wired whether or not a
            // read policy is enabled: under a policy the authorizer still decides whether the principal may
            // read it, and without one the selector scopes an otherwise unselected (all-repository) read. The
            // accessors read the ambient request at call time, so the singleton provider stays correct under
            // concurrent requests. A request with no selector reads the unselected default unless
            // RequireRepositorySelection says it must name one.
            return new DatabaseProvider(options.CatalogDbPath, authorizer)
            {
                RequestedRepository = RequestedRepositoryAccessor(http, service),
                RepositorySelectedImplicitly = RepositorySelectedImplicitlyAccessor(http, service),
                RepositoryRequiredGuidance = RepositoryRequiredGuidanceAccessor(http, service, options.RepositoryUrlPolicy),
                RequestedBranch = RequestedBranchAccessor(http),
                RequireRepositorySelection = RepositorySelectionRequirement(options, CallerAssertionGate.CallerPrincipalAccessor(http)),
                // Issue #145: refuse absolute path inputs and record each admitted read for the output pass
                // (RemoteOutputFilter), which makes paths repository-relative and the snapshot meta lean.
                RemoteSurface = true,
                MaxResponseChars = options.MaxResponseChars,
                ReadAdmitted = context => RemoteOutputFilter.Record(http.HttpContext, context)
            };
        });
        // Explicit ALLOWLIST for the remote HTTP MCP surface (hardening review, criterion 1): register ONLY
        // the index-query tools that route through DatabaseProvider.TryBeginRead and thus enforce the
        // fail-closed read authorizer + per-repository scope. Assembly-wide registration would also expose
        // local-only tools that bypass the gate — get_source_context (reads an ARBITRARY absolute path off
        // disk) and get_daemon_status (probes a local daemon) — which on a multi-tenant service is an
        // arbitrary-file-read / cross-tenant leak. A default-deny allowlist also means a newly added tool is
        // NOT silently exposed remotely until it is vetted and added here. The local stdio server
        // (McpServerSetup) keeps the full assembly-wide set — it is the zero-policy single-node path.
        // STATELESS HTTP transport, so any proxy or pooled MCP client works. Each POST is an independent
        // request-response that needs no prior `initialize` and no Mcp-Session-Id header. A full SDK client
        // (initialize, then tools/list + tools/call over one pooled connection) and a proxy that answers
        // `initialize` itself and forwards only `tools/list` + `tools/call` verbatim are served alike, and
        // any request can land on any replica (no session affinity). The default (stateful) Streamable-HTTP
        // transport would reject a non-initialize POST that lacks the session header with HTTP 400 ("A new
        // session can only be created by an initialize request. Include a valid Mcp-Session-Id header for
        // non-initialize requests."). Stateless is also the architecturally correct model for a multi-tenant,
        // read-only query surface (horizontally scalable). McpClientCompatibilityTests and the stateless
        // proxy-client tests in ServiceHttpTests pin both client shapes. The auth/scope middleware runs
        // per-POST on the /mcp path BEFORE MapMcp, so it is unaffected; the local stdio server
        // (McpServerSetup) is a separate path and stays stateful/unchanged.
        builder.Services.AddMcpServer(server => server.ServerInstructions = ServerInstructions)
            .WithHttpTransport(transport => transport.Stateless = true)
            // Cast to IEnumerable<Type> is REQUIRED: RemoteQueryTools is typed IReadOnlyList<Type>, and a
            // bare `.WithTools(RemoteQueryTools)` binds to the generic overload
            // `WithTools<TToolType>(TToolType target)` (an exact identity match beats the IEnumerable<Type>
            // conversion the non-generic overload needs). That overload treats the LIST OBJECT itself as a
            // tool type, finds no [McpServerTool] methods on IReadOnlyList<Type>, and registers ZERO tools —
            // so tools/list/tools/call answer -32601. The cast selects the non-generic
            // WithTools(IEnumerable<Type>) overload that reflects each element type's attributed methods.
            .WithTools((IEnumerable<Type>)RemoteQueryTools)
            // SVC-2: per-call repository/branch selection through reserved tool arguments. The list filter
            // advertises `repository`/`branch` on every repository-scoped tool; the call filter strips them
            // and records the call's ToolCallSelection, which the DatabaseProvider accessors read. Further
            // per-call checks over the same selection are added to this pipeline. SVC-3's caller check is
            // added FIRST, so it runs before (outside) every other call filter: a delegate call without a
            // verified caller is answered before its arguments are even read.
            .WithRequestFilters(filters => filters
                .AddCallToolFilter(CallerAssertionGate.CallToolFilter())
                // Issue #145: the first list filter, so it runs outermost and compacts every other filter's output.
                .AddListToolsFilter(ToolSchemaCompaction.ListToolsFilter())
                .AddListToolsFilter(ToolSelectionFilters.ListToolsFilter(
                    options.RepositoryUrlPolicy, RepositoryScopedTools,
                    selectionRequired: options.RequireRepositorySelection,
                    implicitSelection: options.DelegateTokens.Count > 0))
                .AddListToolsFilter(SearchSymbolsTool.ListToolsFilter())
                .AddCallToolFilter(ToolSelectionFilters.CallToolFilter(options.RepositoryUrlPolicy, RepositoryScopedTools))
                // A tool result carrying a structured meta.error is an MCP tool error (isError: true), so a client
                // never mistakes a failed call for an empty answer (issue #163). It reads the presented text below,
                // which keeps meta.error.
                .AddCallToolFilter(ToolErrorResults.CallToolFilter())
                // Issue #145: added LAST, so it wraps the tool itself and presents its result (repository-relative
                // paths, lean snapshot meta) after the selection filter has recorded the call's repository.
                .AddCallToolFilter(RemoteOutputFilter.CallToolFilter()));
    }

    /// <summary>
    /// The server's <c>instructions</c> (returned by <c>initialize</c>): when to reach for these tools and how to
    /// call them. Kept short (pinned by a test), because a client may put it in every prompt. It names only tools on
    /// the remote surface (<see cref="RemoteQueryTools"/>; pinned by <c>RemoteToolSurfaceGuardTests</c>).
    /// </summary>
    internal const string ServerInstructions =
        "Sextant answers .NET code questions from a Roslyn index. Use it instead of grep or reading files " +
        "to find declarations, references, callers, implementations, type hierarchies, members and file outlines. " +
        "Pass `repository` (owner/repo or host/owner/repo) on each call; list_repositories shows what you can read, " +
        "and search_symbols searches all of them by name. Paths are repository-relative (src/App/Foo.cs). Large " +
        "results are paged: read meta.total and the summary, narrow with scope (file:, project:, solution:) or pass " +
        "meta.next_cursor. A meta.snapshot.warning means results may be incomplete.";

    /// <summary>
    /// The tool types exposed over the remote HTTP MCP surface: the eight tools coding agents actually call
    /// (agent-behaviour harness, 48 transcripts: no other tool was ever called), plus the service-only
    /// <c>search_symbols</c>. Fewer tools means better tool selection and a smaller <c>tools/list</c>; the local stdio
    /// server (<c>McpServerSetup</c>) keeps the full assembly-wide set. Every index-query tool takes a
    /// <see cref="DatabaseProvider"/> and enters through <c>TryBeginRead</c> (fail-closed authz + scope); the two
    /// exceptions, <see cref="ListRepositoriesTool"/> and <see cref="SearchSymbolsTool"/>, answer only a verified
    /// caller and read only what that caller's grants make visible.
    /// This is a default-deny ALLOWLIST: a newly added tool is never exposed remotely until it is added here, and
    /// every text the remote surface sends (descriptions, instructions, warnings, errors) may name only these tools
    /// (<c>RemoteToolSurfaceGuardTests</c>). Local-only tools that bypass or out-scope the read gate must never be
    /// added: <c>get_source_context</c> (reads an arbitrary absolute path), <c>get_daemon_status</c> (probes a local
    /// daemon), and <c>get_base_snapshot_symbols</c> — the last takes a caller-supplied <c>identity_hash</c> that is
    /// NOT bound to the repository scope <c>TryBeginRead</c> authorizes, so on the multi-tenant surface a principal
    /// scoped to repo A could name repo B's snapshot. Cross-service snapshot federation on the service surface goes
    /// through the per-hash-authorized <c>/query/snapshots/{identityHash}/symbols</c> HTTP endpoint instead.
    /// </summary>
    internal static readonly IReadOnlyList<Type> RemoteQueryTools =
    [
        typeof(FindSymbolTool), typeof(FindReferencesTool), typeof(GetCallHierarchyTool),
        typeof(GetImplementorsTool), typeof(GetTypeHierarchyTool), typeof(GetTypeMembersTool),
        typeof(GetFileSymbolsTool),
        // SVC-4: a service-only tool over the caller's grants (no index read, so no TryBeginRead gate); it answers
        // only a verified caller and lists only the repositories that caller may read.
        typeof(ListRepositoriesTool),
        // SVC-F: a service-only search across the caller's visible snapshots, re-resolved from its grants on every call.
        typeof(SearchSymbolsTool)
    ];

    /// <summary>
    /// The names of the <see cref="RemoteQueryTools"/> that read one selected repository and so take the
    /// reserved <c>repository</c>/<c>branch</c> arguments (SVC-2); see
    /// <see cref="ToolSelectionFilters.SelectionExemptTools"/> for the exceptions.
    /// </summary>
    internal static readonly IReadOnlySet<string> RepositoryScopedTools =
        ToolSelectionFilters.RepositoryScopedToolNames(RemoteQueryTools);

    /// <summary>Wires the auth middleware and maps the control/query/health endpoints onto a built app.</summary>
    public static void MapEndpoints(WebApplication app, ServiceOptions options)
    {
        if (options.ControlPlaneIsOpen)
            app.Logger.LogWarning("{Warning}", ServiceOptions.OpenControlPlaneWarning);
        var callerGate = app.Services.GetRequiredService<CallerAssertionGate>();
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
                var isContribution = path.StartsWithSegments("/control/contribute");
                if (isContribution)
                {
                    if (!AuthorizedForContribution(context, options)) { await Deny(context); return; }
                }
                else if (!Authorized(context, options.ControlToken)) { await Deny(context); return; }

                // SVC-3: an optional caller assertion names the control call's caller (audit actor).
                if (!await callerGate.AdmitControlAsync(context, isContribution)) return;
                // Issue #193: a user caller reaches only the control routes that apply their own act=user rule.
                if (!await ControlCallerRules.AdmitAsync(context)) return;
            }
            else if (path.StartsWithSegments("/mcp") || path.StartsWithSegments("/query"))
            {
                // Bind isolation (issue #61): the query plane is reachable ONLY on the query port when one is
                // configured; a query request on the internal control port is a uniform 404.
                if (WrongPlanePort(context, options, controlPath: false)) { await NotFound(context); return; }

                // SVC-3: a delegate token authenticates the request, but what it may read is decided per
                // verified caller (CallerAssertionGate); every other bearer goes through the existing gate.
                var isDelegate = callerGate.IsDelegateBearer(context);
                if (!isDelegate && !AuthorizedForQuery(context, options)) { await Deny(context); return; }
                if (!await callerGate.AdmitQueryAsync(context, isDelegate, isMcp: path.StartsWithSegments("/mcp"))) return;
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

        // Issue #148: production runs on the SERVICE lifetime, so the request token `ct` only bounds how long
        // this HTTP caller waits — a client disconnect/timeout never cancels or requeues the index; the job
        // stays running and publishes normally, and a re-ensure attaches to it. `?wait=false` returns 202 as
        // soon as the job is registered (status queued/running + job_id) so the caller polls /control/status;
        // issue #158: when the registration itself waits for the writer past ServiceOptions.ControlWriteWait, it
        // returns 202 queued with the job id the identity WILL be registered under (status resolves it at once).
        control.MapPost("/ensure", async (EnsureSnapshotRequest request, bool? wait, HttpRequest req, SnapshotService service, ServiceOptions options, CancellationToken ct) =>
        {
            EnsureSnapshotResult result;
            try
            {
                // SVC-5: refuse an unsafe/unlisted repository URL BEFORE any job row exists. The decision's
                // canonical form is a policy key only — the request keeps its submitted spelling, because
                // snapshot identity hashes it. The 400 body carries only the reason code (never the URL).
                var decision = options.RepositoryUrlPolicy.Evaluate(request.RepositoryRemoteUrl);
                if (!decision.Ok)
                {
                    await service.RecordEnsureDeniedAsync(decision.Reason!, AuditActor(req), ct);
                    return Results.Json(new { status = "rejected", reason = decision.Reason }, ServiceJson.Options,
                        statusCode: StatusCodes.Status400BadRequest);
                }
                // SVC-4: a user caller may only ensure a repository it can read (a grant of its own or its
                // tenant's). An application caller (a trigger) and an assertion-less control call are unchanged.
                // Visibility is decided BEFORE any body rule below, so a user that cannot see the repository gets
                // the same not_granted whatever the body asks for.
                var user = CallerRequest.Get(req.HttpContext)?.Principal is { Actor: CallerActor.User } caller ? caller : null;
                if (user is not null && !service.IsRepositoryVisible(user, request.RepositoryRemoteUrl))
                {
                    await service.RecordEnsureNotGrantedAsync(RepositoryGrantKey.Of(request.RepositoryRemoteUrl), AuditActor(req), ct);
                    return Results.Json(new { status = "rejected", reason = GrantReason.NotGranted }, ServiceJson.Options,
                        statusCode: StatusCodes.Status403Forbidden);
                }
                // SVC-6/7: malformed branch guards (both expected_head_commit and branch_head_sequence, or an
                // unknown branch_update) are refused before any job row exists, audited like the URL policy.
                if (request.BranchGuardProblem() is { } guardProblem)
                {
                    await service.RecordEnsureDeniedAsync(guardProblem, AuditActor(req), ct);
                    return Results.Json(new { status = "rejected", reason = guardProblem }, ServiceJson.Options,
                        statusCode: StatusCodes.Status400BadRequest);
                }
                // SX-6d (issue #198): a user caller may not change shared branch state beyond a guarded advance: no
                // default_branch claim, no head sequence, and either the head CAS or branch_update: none. Decided from
                // the body alone (no catalog read), before any job row exists; audited like not_granted.
                if (user is not null && request.UserCallerBranchProblem() is { } userProblem)
                {
                    await service.RecordUserEnsureDeniedAsync(RepositoryGrantKey.Of(request.RepositoryRemoteUrl), userProblem, AuditActor(req), ct);
                    return Results.Json(new { status = "rejected", reason = userProblem }, ServiceJson.Options,
                        statusCode: StatusCodes.Status400BadRequest);
                }
                // Issue #199: nor may a user pick the repository's default by being its first branch: the #104
                // first-branch default goes only to the branch the remote itself names as its default.
                if (user is not null)
                    request = request with { RestrictsImplicitDefault = true };
                result = wait == false
                    ? await service.BeginEnsureSnapshotAsync(request, AuditActor(req), ct)
                    : await service.EnsureSnapshotAsync(request, ct, AuditActor(req));
            }
            catch (Exception ex) when ((ex is OperationCanceledException && !ct.IsCancellationRequested) || ex is GrantStoreUnavailableException)
            {
                // The ensure was stopped by the SERVICE, not this caller: it is shutting down (or its worker
                // cancelled itself), or a not_granted refusal could not be recorded. Claim nothing about the job —
                // it may have been requeued, never registered, or already settled — the retried ensure reports
                // (and re-attaches to) its real state.
                return Results.Json(
                    new { status = "unavailable", reason = "the index service stopped this ensure before it completed (e.g. it is shutting down); retry the ensure" },
                    ServiceJson.Options, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            // A non-terminal result is 202 Accepted so the orchestrator polls /status rather than treating it
            // as a settled 200 outcome: either the ensure ran but the identity was requeued for a later
            // re-attempt (a TRANSIENT provisioning failure bounded by the attempt cap; nothing re-runs it in the
            // background, so the next attempt is the orchestrator's next ensure for the same commit), or
            // `wait=false` returned while production is still queued/running.
            var statusCode = SnapshotJobStatus.IsTerminal(result.Status)
                ? StatusCodes.Status200OK
                : StatusCodes.Status202Accepted;
            return Results.Json(result, ServiceJson.Options, statusCode: statusCode);
        }).DecidesUserCallers();

        // Status/resolve read an independent WAL read connection (issue #148), so they answer promptly while a
        // worker holds the single writer for a long index. SVC-4: a user caller sees only a job on a repository it
        // can read; any other job is the same 404 as an unknown id.
        control.MapGet("/status/{jobId:long}", (long jobId, HttpRequest req, SnapshotService service) =>
        {
            var status = service.GetStatus(jobId);
            if (status is not null
                && CallerRequest.Get(req.HttpContext)?.Principal is { Actor: CallerActor.User } user
                && !service.IsRepositoryVisible(user, status.Job.RepositoryUrl))
                status = null;
            return status is null ? Results.NotFound() : Results.Json(status, ServiceJson.Options);
        }).DecidesUserCallers();

        // SVC-4: a user caller resolves only a repository it can read. The grant is checked BEFORE the branch is
        // resolved, so an ungranted repository is the same bare 404 as an absent one (no existence oracle).
        control.MapGet("/resolve", (string repository, string? branch, HttpRequest req, SnapshotService service) =>
        {
            if (CallerRequest.Get(req.HttpContext)?.Principal is { Actor: CallerActor.User } user
                && !service.IsRepositoryVisible(user, repository))
                return Results.NotFound();

            var head = service.ResolveBranchHead(repository, branch);
            if (head is null)
                return Results.NotFound();

            // Additive (issue #119): the snapshot's durable coverage rides alongside the snapshot row so a
            // caller can tell a partial snapshot from a complete one without a second request. Additive
            // (SVC-6): the branch it resolved through and the snapshot's commit.
            var body = System.Text.Json.JsonSerializer.SerializeToNode(head.Snapshot, ServiceJson.Options)!.AsObject();
            if (head.Coverage is { } coverage)
                body["coverage"] = System.Text.Json.JsonSerializer.SerializeToNode(coverage, ServiceJson.Options);
            if (head.CommitSha is { } commitSha)
                body["commit_sha"] = commitSha;
            body["branch"] = head.Branch;
            body["is_default"] = head.IsDefault;
            if (head.HeadSequence is long headSequence)
                body["head_sequence"] = headSequence;
            // Additive: whether the snapshot (`identity_hash`, already in the row) is what an ensure of its commit would
            // build on this node now. Both fields are omitted together, never null, when it is not reported (no commit,
            // or a contributed head), so a client treats an absent identity_current as current (as against an older
            // service).
            if (head.CurrentIdentityHash is { } currentIdentityHash)
            {
                body["current_identity_hash"] = currentIdentityHash;
                body["identity_current"] = head.IdentityCurrent is true;
            }
            return Results.Json(body, ServiceJson.Options);
        }).DecidesUserCallers();

        // SVC-6: retire a branch pointer (the branch was deleted upstream). The SVC-5 repository URL policy
        // applies as at ensure intake; the 400 body carries only the reason code (never the URL). A missing
        // branch is 200 {retired:false} (idempotent); the default branch and a head-CAS mismatch are 409.
        // Issue #158: the retirement is service-owned and takes its turn on the writer in submission order. When it
        // has not applied within ServiceOptions.ControlWriteWait (a production holds the writer), the answer is
        // 202 {status:"accepted"}: it applies once the writer frees, with both guards evaluated and its retire audit
        // row written then, even if this caller has gone.
        // Issue #193: retire is application/operator-only. A user caller is refused (403 caller_not_allowed,
        // audited retire/denied) before the body is read, even for a repository it can see: retiring a branch
        // of a repository other users watch is a destructive cross-user action.
        control.MapPost("/branches/retire", async (RetireBranchRequest request, HttpRequest req, SnapshotService service, ServiceOptions options, CancellationToken ct) =>
        {
            RetireBranchResult? result;
            try
            {
                var decision = options.RepositoryUrlPolicy.Evaluate(request.Repository);
                var refusal = !decision.Ok
                    ? decision.Reason!
                    : string.IsNullOrWhiteSpace(request.Branch) ? BranchGuardReason.BranchRequired : null;
                if (refusal is not null)
                {
                    await service.RecordRetireDeniedAsync(refusal, AuditActor(req), ct);
                    return Results.Json(new { status = "rejected", reason = refusal }, ServiceJson.Options,
                        statusCode: StatusCodes.Status400BadRequest);
                }

                result = await service.BeginRetireBranchAsync(request, AuditActor(req), ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The service is shutting down and admitted nothing: the retirement was not queued.
                return Results.Json(
                    new { status = "unavailable", reason = "the index service is shutting down and did not accept this retirement; retry it" },
                    ServiceJson.Options, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            if (result is null)
                return Results.Json(new { status = "accepted" }, ServiceJson.Options, statusCode: StatusCodes.Status202Accepted);
            return result.Reason is null
                ? Results.Json(new { retired = result.Retired }, ServiceJson.Options)
                : Results.Json(new { status = "rejected", reason = result.Reason }, ServiceJson.Options,
                    statusCode: StatusCodes.Status409Conflict);
        }).AuditsRefusedUserCallsAs(AuditAction.Retire);

        // Issue #193: retention, backup and the observability routes below are operator-only. A user caller is
        // refused with 403 caller_not_allowed (retention and backup audit the refusal); see ControlCallerRules.
        control.MapPost("/retention", async (bool? execute, HttpRequest req, SnapshotService service, CancellationToken ct) =>
        {
            var report = await service.RunRetentionAsync(execute ?? false, AuditActor(req), ct);
            return Results.Json(report, ServiceJson.Options);
        }).AuditsRefusedUserCallsAs(AuditAction.Retention);

        // SVC-4: repository grants (per-caller visibility and reconcile targets).
        GrantEndpoints.Map(control);

        // Observability surface (criterion 5). These live under /control so they inherit the CONTROL-token
        // gate — they are OPERATOR-ONLY and must NEVER be reachable by a query-plane tenant, because they
        // aggregate cross-tenant repository scopes, counts, and cost (criterion-1 leakage guard). For the same
        // reason a user caller is refused (issue #193): it must never read other callers' audit rows or metrics.

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
        control.MapPost("/backup", async (string dir, HttpRequest req, SnapshotService service, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(dir))
                return Results.BadRequest("query parameter 'dir' is required.");
            var manifest = await service.CreateBackupAsync(dir, AuditActor(req), ct);
            return Results.Json(manifest, ServiceJson.Options);
        }).AuditsRefusedUserCallsAs(AuditAction.Backup);

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

    /// <summary>
    /// The principal an audited control call is attributed to (SVC-3): the verified caller
    /// (<see cref="CallerPrincipal.AuditPrincipal"/>, plus its attribution suffix, SVC-4) when the call carried a
    /// caller assertion, else its bearer. The service hashes the principal before it is stored.
    /// </summary>
    internal static AuditCaller AuditActor(HttpRequest req) =>
        CallerRequest.Get(req.HttpContext)?.Principal is { } caller ? AuditCaller.ForCaller(caller) : ExtractBearer(req);

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
                // (criterion 1: reveal no artifact access). With no policy configured this is a no-op. A
                // delegate caller (SVC-3) is decided by the per-caller authorizer instead, with the same 404.
                if (http.RequestServices.GetRequiredService<CallerAssertionGate>().IsDelegateRequest(http))
                {
                    if (!AuthorizedForDelegateSnapshot(connection, http, identityHash))
                        return Results.NotFound();
                }
                else if (options.ReadPolicy.Enabled && !AuthorizedForSnapshot(connection, options, http, identityHash))
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

    /// <summary>
    /// Authorizes a delegate caller's snapshot-page request (SVC-3) through the provider's per-caller read
    /// authorizer. An unknown snapshot is unauthorized, so the 404 stays uniform.
    /// </summary>
    private static bool AuthorizedForDelegateSnapshot(SqliteConnection conn, HttpContext http, string identityHash)
    {
        var snapshot = new SnapshotStore(conn).GetByIdentityHash(identityHash);
        return snapshot is not null
            && http.RequestServices.GetRequiredService<DatabaseProvider>().Authorizer.Authorize(snapshot).Allowed;
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
    internal static string? BearerToken(HttpContext context)
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
    /// Resolves the caller-declared repository (its git remote URL) for the current request: the call's
    /// <see cref="ToolCallSelection"/> when <see cref="ToolSelectionFilters"/> recorded one (the reserved
    /// <c>repository</c> argument, the header, or a cross-repository tool's provider default), else the
    /// <c>X-Sextant-Repository</c> request header. Reads the ambient request at call time so a
    /// singleton <see cref="DatabaseProvider"/> stays request-correct; returns null when nothing names a
    /// repository (the read planner then reads the unselected default, or fails with
    /// <c>repository_required</c> when <see cref="RepositorySelectionRequirement"/> demands a selection).
    /// The selector only NAMES the repository — under a read policy, authorization is still enforced by
    /// <see cref="PolicyReadAuthorizer"/> against the principal's token, so a caller cannot read another
    /// tenant merely by naming it. SVC-4 implicit selection: a delegate caller's read that names neither a
    /// repository nor a branch reads the caller's ONE visible repository with a complete default-branch snapshot
    /// (<see cref="CallerVisibility.ImplicitRepository"/>); with none or several it stays unselected, so the read
    /// fails with <c>repository_required</c>.
    /// </summary>
    private static Func<string?> RequestedRepositoryAccessor(IHttpContextAccessor accessor, SnapshotService service) =>
        () => accessor.HttpContext is { } ctx ? RequestedRepositorySelection(ctx, service).Repository : null;

    /// <summary>
    /// Whether <see cref="RequestedRepositoryAccessor"/>'s repository for the current request is the SVC-4 implicit
    /// selection (the request named none), so the response says which repository answered.
    /// </summary>
    private static Func<bool> RepositorySelectedImplicitlyAccessor(IHttpContextAccessor accessor, SnapshotService service) =>
        () => accessor.HttpContext is { } ctx && RequestedRepositorySelection(ctx, service).Implicit;

    private static (string? Repository, bool Implicit) RequestedRepositorySelection(HttpContext ctx, SnapshotService service)
    {
        var selection = ToolCallSelection.Get(ctx);
        var named = selection is not null ? selection.Repository : RepositoryHeaderValue(ctx);
        if (named is not null || selection?.Branch is not null)
            return (named, false);
        var selected = CallerVisibility.ImplicitRepository(ctx, service);
        return (selected, selected is not null);
    }

    /// <summary>The most repositories a <c>repository_required</c> error lists before pointing at <c>list_repositories</c>.</summary>
    internal const int RepositoryRequiredListLimit = 20;

    /// <summary>
    /// The guidance appended to a <c>repository_required</c> error: how to name a repository and, for a verified
    /// caller, the repositories ITS grants make visible (the same set <c>list_repositories</c> returns to it), so an
    /// agent can retry at once. A request without a verified caller gets only the syntax hint, so the error never
    /// describes catalog content to an unidentified caller.
    /// </summary>
    private static Func<string?> RepositoryRequiredGuidanceAccessor(
        IHttpContextAccessor accessor, SnapshotService service, RepositoryUrlPolicy policy) =>
        () =>
        {
            var example = policy.Hosts.Count == 1 ? "owner/repo" : "host/owner/repo";
            var how = $"Pass the 'repository' argument (e.g. '{example}') or the {RepositoryHeader} header.";
            if (accessor.HttpContext is not { } ctx || CallerRequest.Get(ctx)?.Principal is not { } caller)
                return how;
            var visible = service.ListVisibleRepositories(caller)
                .Select(r => ShortRepositoryName(r.Repository, policy))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (visible.Count == 0)
                return how + " The caller has no repository grants yet, so there is nothing to read until one is added.";
            var shown = string.Join(", ", visible.Take(RepositoryRequiredListLimit).Select(name => $"'{name}'"));
            var more = visible.Count > RepositoryRequiredListLimit
                ? $" and {visible.Count - RepositoryRequiredListLimit} more (call list_repositories for all)"
                : string.Empty;
            return $"{how} Repositories this caller can read: {shown}{more}.";
        };

    /// <summary>
    /// A repository URL in the shortest spelling the <c>repository</c> argument accepts: <c>owner/repo</c> when the
    /// policy has one host and the URL is on it, else <c>host/owner/repo</c>; any other shape is returned as stored.
    /// </summary>
    internal static string ShortRepositoryName(string url, RepositoryUrlPolicy policy)
    {
        var decision = policy.Evaluate(url);
        if (!decision.Ok || decision.Host is not { } host || decision.Owner is not { } owner || decision.Repo is not { } repo)
            return url;
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            repo = repo[..^4];
        return policy.Hosts.Count == 1 && string.Equals(host, policy.Hosts[0], StringComparison.OrdinalIgnoreCase)
            ? $"{owner}/{repo}"
            : $"{host}/{owner}/{repo}";
    }

    /// <summary>
    /// Resolves the branch the current call selected through the reserved <c>branch</c> tool argument
    /// (SVC-2), or null for the selected repository's default branch.
    /// </summary>
    private static Func<string?> RequestedBranchAccessor(IHttpContextAccessor accessor) =>
        () => ToolCallSelection.Get(accessor.HttpContext)?.Branch;

    /// <summary>The trimmed <c>X-Sextant-Repository</c> header, or null when it is absent or blank.</summary>
    internal static string? RepositoryHeaderValue(HttpContext context)
    {
        var value = context.Request.Headers[RepositoryHeader].ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// Decides, per request, whether a query-plane read must name a repository
    /// (<see cref="DatabaseProvider.RequireRepositorySelection"/>): when the operator switch
    /// <see cref="ServiceOptions.RequireRepositorySelection"/> is on, or when the request carries a verified caller
    /// (SVC-3), since a caller's reads are scoped to the repositories it may read. With both off, a legacy caller
    /// that sends no selector keeps reading the unselected default. Any other per-request reason to require a
    /// selection composes here with <c>||</c>.
    /// </summary>
    private static Func<bool> RepositorySelectionRequirement(ServiceOptions options, Func<CallerPrincipal?> caller) =>
        () => options.RequireRepositorySelection || caller() is not null;

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
