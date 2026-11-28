using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Sextant.Mcp;

/// <summary>
/// Reserves bounded read capacity around a complete MCP call. Indexing never acquires these slots, so a busy
/// writer cannot consume the capacity needed to answer queries against the last committed generation.
/// </summary>
public static class QueryReadAdmissionFilter
{
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> CallToolFilter() =>
        next => async (context, cancellationToken) =>
        {
            var services = context.Services
                ?? throw new InvalidOperationException("Query admission requires the request services.");
            var provider = services.GetRequiredService<DatabaseProvider>();
            var lease = await provider.TryAcquireReadSlotAsync(cancellationToken).ConfigureAwait(false);
            if (lease is null)
                return BusyResult();

            using (lease)
            {
                try
                {
                    return await next(context, cancellationToken).ConfigureAwait(false);
                }
                catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
                {
                    return BusyResult();
                }
            }
        };

    internal static CallToolResult BusyResult() =>
        new()
        {
            IsError = true,
            Content = [new TextContentBlock { Text = ResponseBuilder.BuildIndexBusy() }]
        };
}
