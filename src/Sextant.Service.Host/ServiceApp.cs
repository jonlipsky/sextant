using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Sextant.Mcp;
using Sextant.Service;

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
        // so the /mcp + /query planes never block behind a running control-plane index.
        builder.Services.AddSingleton(new DatabaseProvider(options.CatalogDbPath));
        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly(typeof(DatabaseProvider).Assembly);
    }

    /// <summary>Wires the auth middleware and maps the control/query/health endpoints onto a built app.</summary>
    public static void MapEndpoints(WebApplication app, ServiceOptions options)
    {
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            if (path.StartsWithSegments("/control"))
            {
                if (!Authorized(context, options.ControlToken)) { await Deny(context); return; }
            }
            else if (path.StartsWithSegments("/mcp") || path.StartsWithSegments("/query"))
            {
                if (!Authorized(context, options.QueryToken)) { await Deny(context); return; }
            }
            await next();
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

        control.MapPost("/ensure", async (EnsureSnapshotRequest request, SnapshotService service, CancellationToken ct) =>
        {
            var result = await service.EnsureSnapshotAsync(request, ct);
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

        control.MapPost("/retention", (bool? execute, SnapshotService service) =>
        {
            var report = service.RunRetention(execute ?? false);
            return Results.Json(report, ServiceJson.Options);
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
            async (string identityHash, long? cursor, int? limit, CancellationToken ct) =>
            {
                await using var connection = OpenReadConnection(options.CatalogDbPath);
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

    private static bool Authorized(HttpContext context, string? expectedToken)
    {
        if (string.IsNullOrEmpty(expectedToken))
            return true; // No token configured for this plane → open (local development default).

        var header = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (header.StartsWith(prefix, StringComparison.Ordinal))
        {
            var provided = header[prefix.Length..].Trim();
            return CryptographicEquals(provided, expectedToken);
        }
        return false;
    }

    private static Task Deny(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        return context.Response.WriteAsync("Unauthorized");
    }

    // Constant-time comparison so a token check does not leak length/content via timing.
    private static bool CryptographicEquals(string a, string b)
    {
        var ba = System.Text.Encoding.UTF8.GetBytes(a);
        var bb = System.Text.Encoding.UTF8.GetBytes(b);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(ba, bb);
    }
}
