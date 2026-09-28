using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using Sextant.Mcp;

namespace Sextant.Service.Grants;

/// <summary>
/// <c>list_repositories</c> (SVC-4): the repositories the verified caller may read, with each one's branches and
/// their snapshot status. It is a service-only tool (the local stdio server never registers it) that reads the
/// grant catalog rather than an index, so it takes no repository selection and is exempt from the reserved
/// <c>repository</c>/<c>branch</c> arguments. A request with no verified caller gets <c>caller_required</c>.
/// </summary>
[McpServerToolType]
public static class ListRepositoriesTool
{
    internal const string CallerRequiredCode = "caller_required";

    private const string CallerRequiredMessage =
        "list_repositories lists the repositories visible to a verified caller; send a caller assertion.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    [McpServerTool(Name = "list_repositories"), Description(
        "List the repositories you can query, with each repository's branches and the status of their index " +
        "(complete, partial, pending or missing). Pass one of these repositories as the `repository` argument of " +
        "the other tools.")]
    public static string ListRepositories(SnapshotService service, CallerContext caller)
    {
        if (caller.Current is not { } principal)
            return ResponseBuilder.BuildError(CallerRequiredCode, CallerRequiredMessage);

        var repositories = service.ListVisibleRepositories(principal);
        var freshness = repositories
            .SelectMany(r => r.Branches)
            .Select(b => b.PublishedAt ?? 0L)
            .DefaultIfEmpty(0L)
            .Max();
        return JsonSerializer.Serialize(new
        {
            Repositories = repositories,
            Meta = new MetaObject
            {
                QueriedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                IndexFreshness = freshness,
                ResultCount = repositories.Count
            }
        }, JsonOptions);
    }
}
