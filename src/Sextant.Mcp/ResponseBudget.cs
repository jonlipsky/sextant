using System.Globalization;

namespace Sextant.Mcp;

/// <summary>
/// The most characters one tool result may hold. An MCP client caps what it accepts from a tool (Claude Code
/// rejects a result over <c>MAX_MCP_OUTPUT_TOKENS</c>, 25,000 by default, which it estimates from the character
/// count), and a rejected result leaves the agent guessing. So every list-returning tool fits its rows to this
/// budget: a paged tool stops at the last row that fits and returns <c>meta.next_cursor</c> exactly as it does at
/// <c>limit</c>, and an unpaged one stops there and says how to narrow the query. At least one row is always
/// returned, and <c>meta.total</c> stays exact.
/// </summary>
/// <remarks>
/// The default, 20,000 characters, is measured on the text the client receives (on the service, after the remote
/// output pass). The 57,953-character result Claude Code refused counted as more than 25,000 tokens, so it
/// estimates at least one token per 2.3 characters; 20,000 characters is then at most about 8,700 tokens, and
/// still under 13,500 at a pessimistic 1.5 characters per token, so a page stays well inside the default cap with
/// room for the client's own framing.
/// </remarks>
public static class ResponseBudget
{
    /// <summary>The budget when none is configured.</summary>
    public const int DefaultMaxChars = 20_000;

    /// <summary>The smallest budget honoured: room for the response envelope and a row.</summary>
    public const int MinMaxChars = 1_000;

    /// <summary>The <c>meta.page_truncated_by</c> value of a result cut by this budget.</summary>
    public const string SizeTruncation = "size";

    /// <summary>The effective budget: the default when absent or not positive, else at least <see cref="MinMaxChars"/>.</summary>
    public static int Clamp(int? configured) =>
        configured is null or <= 0 ? DefaultMaxChars : Math.Max(configured.Value, MinMaxChars);

    /// <summary>
    /// The length of <paramref name="text"/> as the client will receive it: on the remote surface, after
    /// <see cref="RemoteResponsePresenter"/> (repository-relative paths, lean meta).
    /// </summary>
    internal static int Measure(string text, FederatedReadContext? context) =>
        context is { Paths.IsRemote: true }
            ? RemoteResponsePresenter.Present(text, context, context.Paths).Length
            : text.Length;

    /// <summary>
    /// The rendering of the most rows (at least one) of <paramref name="count"/> whose measured text fits
    /// <paramref name="maxChars"/>. <paramref name="render"/> builds the response for a row count; its size grows
    /// with the count, so the largest fitting count is found by bisection.
    /// </summary>
    public static string Fit(int count, int maxChars, Func<int, string> render, Func<string, int> measure)
    {
        var all = render(count);
        if (count <= 1 || measure(all) <= maxChars)
            return all;

        // Every count below `count` renders the cut variant (cursor or narrowing hint), so sizes are monotone here.
        var best = render(1);
        if (measure(best) > maxChars)
            return best;
        int low = 1, high = count - 1;
        while (low < high)
        {
            var mid = low + (high - low + 1) / 2;
            var text = render(mid);
            if (measure(text) <= maxChars)
            {
                low = mid;
                best = text;
            }
            else
            {
                high = mid - 1;
            }
        }
        return best;
    }

    /// <summary>The message of a page cut by size, which still has a cursor.</summary>
    internal static string PageCutMessage(int rows, int maxChars) => string.Create(CultureInfo.InvariantCulture,
        $"Page cut to {rows} rows to stay under {maxChars:N0} characters. Pass meta.next_cursor for the next rows, or narrow the query.");

    /// <summary>The message of an unpaged result cut by size, which has no cursor.</summary>
    internal static string ResultCutMessage(int rows, int total, int maxChars) => string.Create(CultureInfo.InvariantCulture,
        $"Showing {rows} of {total} results to stay under {maxChars:N0} characters. Narrow the query to see the rest.");
}
