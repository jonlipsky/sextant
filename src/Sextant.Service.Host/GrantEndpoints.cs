using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sextant.Service.CallerIdentity;
using Sextant.Service.Grants;
using Sextant.Store;

namespace Sextant.Service.Host;

/// <summary>
/// The SVC-4 grant routes under <c>/control</c>. Every route needs the control bearer (the middleware) AND a verified
/// caller assertion with the route's actor; the principal a grant is written for comes ONLY from that assertion.
/// <list type="bullet">
///   <item>no caller: 401 <c>{"error":"caller_required"}</c> (a present but invalid or disallowed assertion was
///   already refused by <see cref="CallerAssertionGate.AdmitControlAsync"/> with 401/403);</item>
///   <item>the wrong actor: 403 <see cref="GrantReason.WrongActor"/>; a user whose subject is the reserved
///   tenant-wide principal <c>*</c>: 403 <c>caller_not_allowed</c>;</item>
///   <item>a body or query that names a principal: 400 <see cref="GrantReason.PrincipalInBody"/>;</item>
///   <item>a refused repository URL: 400 with its SVC-5 reason; a bad branch: 400
///   <see cref="GrantReason.BranchNotAllowed"/>; a new grant over a limit: 409 <see cref="GrantReason.GrantLimit"/>.</item>
/// </list>
/// Every PUT and DELETE outcome, accepted or refused, is audited as <c>grant</c>; the listings are not.
/// </summary>
internal static class GrantEndpoints
{
    /// <summary>The largest grant request body accepted.</summary>
    internal const int MaxBodyBytes = 16 * 1024;

    /// <summary>The longest branch name a grant holds.</summary>
    internal const int MaxBranchLength = 255;

    // Field names that would name a principal, compared case-insensitively in the body and the query.
    private static readonly HashSet<string> PrincipalFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "tenant_id", "tenantid", "tid", "principal", "sub", "user", "user_id", "userid"
    };

    public static void Map(RouteGroupBuilder control)
    {
        control.MapPut("/grants/self", (HttpRequest req, SnapshotService service, ServiceOptions options, CancellationToken ct) =>
            PutAsync(req, service, options, GrantScope.Self, ct));
        control.MapDelete("/grants/self", (HttpRequest req, SnapshotService service, ServiceOptions options, CancellationToken ct) =>
            DeleteAsync(req, service, options, GrantScope.Self, ct));
        control.MapGet("/grants/self", (HttpRequest req, SnapshotService service) =>
            ListOwn(req, service));
        control.MapPut("/grants/tenant", (HttpRequest req, SnapshotService service, ServiceOptions options, CancellationToken ct) =>
            PutAsync(req, service, options, GrantScope.Tenant, ct));
        control.MapDelete("/grants/tenant", (HttpRequest req, SnapshotService service, ServiceOptions options, CancellationToken ct) =>
            DeleteAsync(req, service, options, GrantScope.Tenant, ct));
        control.MapGet("/grants", (string? scope, HttpRequest req, SnapshotService service) =>
            ListTargets(scope, req, service));
    }

    private static async Task<IResult> PutAsync(
        HttpRequest req, SnapshotService service, ServiceOptions options, GrantScope scope, CancellationToken ct)
    {
        var operation = Operation("put", scope);
        var auditor = ServiceApp.AuditActor(req);
        try
        {
            if (Admit(req, scope) is { Refusal: { } refused, Reason: { } admitReason })
                return await DeniedAsync(service, operation, admitReason, null, auditor, refused, ct);
            var caller = CallerRequest.Get(req.HttpContext)!.Principal!;

            var body = await ReadRequestAsync(req, ct);
            if (body.Reason is { } bodyReason)
                return await DeniedAsync(service, operation, bodyReason, null, auditor, Rejected(StatusCodes.Status400BadRequest, bodyReason), ct);

            var decision = options.RepositoryUrlPolicy.Evaluate(body.Repository);
            if (!decision.Ok)
                return await DeniedAsync(service, operation, decision.Reason!, null, auditor, Rejected(StatusCodes.Status400BadRequest, decision.Reason!), ct);
            var repository = body.Repository!;
            var key = RepositoryGrantKey.Of(repository);

            if (NormalizeBranch(body.Branch, allowAllBranches: false) is not { } branch)
                return await DeniedAsync(service, operation, GrantReason.BranchNotAllowed, key, auditor,
                    Rejected(StatusCodes.Status400BadRequest, GrantReason.BranchNotAllowed), ct);

            var result = await service.PutGrantAsync(caller, scope, repository, key, branch, auditor, ct);
            if (result.Refusal is { } refusal)
                return Rejected(StatusCodes.Status409Conflict, refusal);
            var grant = result.Grant!;
            return Results.Json(new
            {
                grant = new { repository = grant.RemoteUrl, branch = grant.Branch, source = grant.Source, created_at = grant.CreatedAt },
                created = result.Created
            }, ServiceJson.Options);
        }
        catch (Exception ex) when (IsUnavailable(ex, ct))
        {
            return Unavailable();
        }
    }

    private static async Task<IResult> DeleteAsync(
        HttpRequest req, SnapshotService service, ServiceOptions options, GrantScope scope, CancellationToken ct)
    {
        var operation = Operation("delete", scope);
        var auditor = ServiceApp.AuditActor(req);
        try
        {
            if (Admit(req, scope) is { Refusal: { } refused, Reason: { } admitReason })
                return await DeniedAsync(service, operation, admitReason, null, auditor, refused, ct);
            var caller = CallerRequest.Get(req.HttpContext)!.Principal!;

            var repositoryValues = req.Query["repository"];
            var branchValues = req.Query["branch"];
            if (repositoryValues.Count > 1 || branchValues.Count > 1)
                return await DeniedAsync(service, operation, GrantReason.InvalidBody, null, auditor,
                    Rejected(StatusCodes.Status400BadRequest, GrantReason.InvalidBody), ct);

            var repository = repositoryValues.ToString();
            var decision = RepositoryGrantKey.EvaluateForRevocation(options.RepositoryUrlPolicy, repository);
            if (!decision.Ok)
                return await DeniedAsync(service, operation, decision.Reason!, null, auditor, Rejected(StatusCodes.Status400BadRequest, decision.Reason!), ct);
            var key = RepositoryGrantKey.Of(repository);

            if (NormalizeBranch(branchValues.Count == 0 ? null : branchValues.ToString(), allowAllBranches: true) is not { } branch)
                return await DeniedAsync(service, operation, GrantReason.BranchNotAllowed, key, auditor,
                    Rejected(StatusCodes.Status400BadRequest, GrantReason.BranchNotAllowed), ct);

            var deleted = await service.DeleteGrantsAsync(caller, scope, key, branch, auditor, ct);
            return Results.Json(new { deleted }, ServiceJson.Options);
        }
        catch (Exception ex) when (IsUnavailable(ex, ct))
        {
            return Unavailable();
        }
    }

    private static IResult ListOwn(HttpRequest req, SnapshotService service)
    {
        if (Admit(req, GrantScope.Self) is { Refusal: { } refused })
            return refused;
        var grants = service.ListGrants(CallerRequest.Get(req.HttpContext)!.Principal!, GrantScope.Self);
        return Results.Json(new { grants, result_count = grants.Count }, ServiceJson.Options);
    }

    private static IResult ListTargets(string? scope, HttpRequest req, SnapshotService service)
    {
        if (Admit(req, GrantScope.Tenant) is { Refusal: { } refused })
            return refused;
        if (!string.Equals(scope, "tenant", StringComparison.Ordinal))
            return Rejected(StatusCodes.Status400BadRequest, GrantReason.InvalidScope);
        // Reconcile targets: the distinct (repository, branch) set with counts, never a principal.
        var targets = service.ListGrantTargets(CallerRequest.Get(req.HttpContext)!.Principal!)
            .Select(t => new { repository = t.RemoteUrl, branch = t.Branch, sources = t.Sources, watchers = t.Watchers })
            .ToList();
        return Results.Json(new { targets, result_count = targets.Count }, ServiceJson.Options);
    }

    private readonly record struct Admission(IResult? Refusal, string? Reason);

    // The caller, actor and query checks every grant route shares.
    private static Admission Admit(HttpRequest req, GrantScope scope)
    {
        if (CallerRequest.Get(req.HttpContext)?.Principal is not { } caller)
        {
            req.HttpContext.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_request\"";
            return new(Error(StatusCodes.Status401Unauthorized, CallerAssertionGate.RequiredCode), CallerAssertionGate.RequiredCode);
        }
        var expected = scope == GrantScope.Self ? CallerActor.User : CallerActor.Application;
        if (caller.Actor != expected)
            return new(Rejected(StatusCodes.Status403Forbidden, GrantReason.WrongActor), GrantReason.WrongActor);
        // A user whose subject is the reserved tenant-wide principal would read and write the tenant's '*' grants.
        if (!SnapshotService.CanWriteGrants(caller, scope))
            return new(Error(StatusCodes.Status403Forbidden, CallerAssertionGate.NotAllowedCode), CallerAssertionGate.NotAllowedCode);
        if (req.Query.Keys.Any(PrincipalFields.Contains))
            return new(Rejected(StatusCodes.Status400BadRequest, GrantReason.PrincipalInBody), GrantReason.PrincipalInBody);
        return default;
    }

    private readonly record struct GrantRequest(string? Repository, string? Branch, string? Reason);

    // Reads a PUT body: a JSON object with a string `repository` and an optional string (or null) `branch`, at most
    // MaxBodyBytes. A body naming a principal anywhere at its top level is refused before anything else is read.
    private static async Task<GrantRequest> ReadRequestAsync(HttpRequest req, CancellationToken ct)
    {
        var invalid = new GrantRequest(null, null, GrantReason.InvalidBody);
        if (req.ContentLength > MaxBodyBytes)
            return invalid;
        byte[] bytes;
        using (var buffer = new MemoryStream())
        {
            var chunk = new byte[4096];
            int read;
            while ((read = await req.Body.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > MaxBodyBytes)
                    return invalid;
                await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
            }
            bytes = buffer.ToArray();
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            return invalid;
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return invalid;
            if (root.EnumerateObject().Any(p => PrincipalFields.Contains(p.Name)))
                return new GrantRequest(null, null, GrantReason.PrincipalInBody);

            string? repository = null;
            string? branch = null;
            int repositories = 0, branches = 0;
            foreach (var property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "repository" when property.Value.ValueKind == JsonValueKind.String:
                        repository = property.Value.GetString();
                        repositories++;
                        break;
                    case "branch" when property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Null:
                        branch = property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.GetString();
                        branches++;
                        break;
                    case "repository" or "branch":
                        return invalid;
                }
            }
            return repositories > 1 || branches > 1 ? invalid : new GrantRequest(repository, branch, null);
        }
    }

    /// <summary>
    /// The branch a grant holds: trimmed, empty (the default branch) when omitted or blank. <c>*</c> (every branch) is
    /// accepted only where <paramref name="allowAllBranches"/> (a revocation). Null when the name is not allowed: longer
    /// than <see cref="MaxBranchLength"/>, or with whitespace, a control character or any other <c>*</c>.
    /// </summary>
    internal static string? NormalizeBranch(string? branch, bool allowAllBranches)
    {
        var value = branch?.Trim() ?? string.Empty;
        if (value.Length == 0)
            return RepositoryGrantStore.DefaultBranch;
        if (value == RepositoryGrantStore.AllBranches)
            return allowAllBranches ? value : null;
        if (value.Length > MaxBranchLength || value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c == '*'))
            return null;
        return value;
    }

    private static string Operation(string verb, GrantScope scope) =>
        $"{verb}_{(scope == GrantScope.Self ? "self" : "tenant")}";

    private static async Task<IResult> DeniedAsync(
        SnapshotService service, string operation, string reason, string? repositoryKey, AuditCaller auditor,
        IResult refusal, CancellationToken ct)
    {
        await service.RecordGrantDeniedAsync(operation, reason, repositoryKey, auditor, ct);
        return refusal;
    }

    private static IResult Rejected(int statusCode, string reason) =>
        Results.Json(new { status = "rejected", reason }, ServiceJson.Options, statusCode: statusCode);

    private static IResult Error(int statusCode, string code) =>
        Results.Json(new { error = code }, ServiceJson.Options, statusCode: statusCode);

    // The service cannot take the write right now (shutting down, lease lost, catalog locked): nothing was written.
    private static bool IsUnavailable(Exception ex, CancellationToken ct) =>
        ex is GrantStoreUnavailableException or ObjectDisposedException
        || ex is OperationCanceledException && !ct.IsCancellationRequested;

    private static IResult Unavailable() =>
        Results.Json(new { status = "unavailable", reason = "the index service cannot record grants right now; retry the request" },
            ServiceJson.Options, statusCode: StatusCodes.Status503ServiceUnavailable);
}

/// <summary>
/// The verified caller's repository visibility for the current request (SVC-4). The grant catalog is read at most
/// once per request and cached in <see cref="HttpContext.Items"/> (never across requests, so a revocation takes
/// effect on the caller's next call).
/// </summary>
internal static class CallerVisibility
{
    private static readonly object KeysItem = new();
    private static readonly object ImplicitItem = new();

    /// <summary>The current caller's visible repository keys, or null when the request carries no verified caller.</summary>
    public static IReadOnlySet<string>? VisibleKeys(HttpContext? http, SnapshotService service)
    {
        if (http is null || CallerRequest.Get(http)?.Principal is not { } caller)
            return null;
        if (http.Items.TryGetValue(KeysItem, out var cached) && cached is IReadOnlySet<string> keys)
            return keys;
        keys = service.GetVisibleRepositoryKeys(caller);
        http.Items[KeysItem] = keys;
        return keys;
    }

    /// <summary>Whether the current caller may read <paramref name="remoteUrl"/> (false with no caller).</summary>
    public static bool IsVisible(HttpContext http, SnapshotService service, string? remoteUrl) =>
        !string.IsNullOrWhiteSpace(remoteUrl)
        && VisibleKeys(http, service) is { } keys
        && keys.Contains(RepositoryGrantKey.Of(remoteUrl));

    /// <summary>
    /// The implicit selection of a delegate caller's read that names no repository: its one visible repository with a
    /// complete default-branch snapshot, or null (the read then fails with <c>repository_required</c>).
    /// </summary>
    public static string? ImplicitRepository(HttpContext http, SnapshotService service)
    {
        if (CallerRequest.Get(http) is not { IsDelegate: true, Principal: not null })
            return null;
        if (http.Items.TryGetValue(ImplicitItem, out var cached))
            return (cached as StrongBox<string?>)?.Value;
        var selected = VisibleKeys(http, service) is { } keys ? service.ResolveImplicitRepository(keys) : null;
        http.Items[ImplicitItem] = new StrongBox<string?>(selected);
        return selected;
    }

    private sealed class StrongBox<T>(T value)
    {
        public T Value { get; } = value;
    }

    /// <summary>The per-request key accessor <see cref="GrantReadAuthorizer"/> is built over.</summary>
    public static Func<IReadOnlySet<string>?> Accessor(IHttpContextAccessor accessor, SnapshotService service) =>
        () => VisibleKeys(accessor.HttpContext, service);
}
