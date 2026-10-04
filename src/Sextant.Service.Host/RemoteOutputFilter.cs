using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Sextant.Mcp;

namespace Sextant.Service.Host;

/// <summary>
/// The remote surface's output pass (issue #145). Every <c>tools/call</c> result on the service's <c>/mcp</c> is
/// rewritten by <see cref="RemoteResponsePresenter"/> after the tool runs, so no tool can leak a worker checkout
/// path or skip the lean <c>meta.snapshot</c>. It presents against the read the tool was admitted with
/// (recorded by <see cref="DatabaseProvider.ReadAdmitted"/> in the request's items); a call that never read (a
/// request-shaped error, a grant-only tool) still has every known checkout root removed. The local stdio
/// server registers no filters, so its output is unchanged.
/// </summary>
internal static class RemoteOutputFilter
{
    private static readonly object ItemsKey = new();

    /// <summary>Records the read the current request's tool was admitted with.</summary>
    public static void Record(HttpContext? http, FederatedReadContext context)
    {
        if (http is not null)
            http.Items[ItemsKey] = context;
    }

    /// <summary>The read recorded for the current request, or null when the tool did not read.</summary>
    public static FederatedReadContext? Get(HttpContext? http) =>
        http is not null && http.Items.TryGetValue(ItemsKey, out var value) ? value as FederatedReadContext : null;

    /// <summary>The call filter: runs the tool, then presents each text block of its result.</summary>
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> CallToolFilter() =>
        next => async (context, cancellationToken) =>
        {
            var result = await next(context, cancellationToken);
            var services = context.Services
                ?? throw new InvalidOperationException("The remote output pass requires the request services.");
            var http = services.GetService<IHttpContextAccessor>()?.HttpContext;
            var read = Get(http);
            var paths = read?.Paths
                ?? new PathPresenter(services.GetRequiredService<DatabaseProvider>().GetDatabase(), isRemote: true);
            foreach (var block in result.Content)
            {
                if (block is not TextContentBlock text)
                    continue;
                var original = text.Text;
                var presented = RemoteResponsePresenter.Present(original, read, paths);
                if (string.Equals(presented, original, StringComparison.Ordinal))
                    continue;
                text.Text = presented;
                // A tool that mirrors its text as structuredContent (search_symbols) keeps the two identical.
                if (result.StructuredContent is { } structured && structured.GetRawText() == original)
                {
                    using var document = JsonDocument.Parse(presented);
                    result.StructuredContent = document.RootElement.Clone();
                }
            }
            return result;
        };
}
