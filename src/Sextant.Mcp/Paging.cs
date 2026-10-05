using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sextant.Mcp;

/// <summary>
/// One page of a large-result tool: where it starts, how many rows it holds, and the binding a continuation
/// cursor must match. Built by <see cref="Paging.TryBegin"/>; consumed by <see cref="ResponseBuilder.BuildPage{T}"/>.
/// </summary>
public sealed record PageRequest(int Offset, int Limit, string Binding)
{
    /// <summary>
    /// The most characters this page's response may hold (<see cref="ResponseBudget"/>):
    /// <see cref="ResponseBuilder.BuildPage{T}"/> ends the page at the last row that fits, before <see cref="Limit"/>.
    /// </summary>
    public int MaxChars { get; init; } = ResponseBudget.DefaultMaxChars;

    /// <summary>How long a response is as the client receives it (see <see cref="ResponseBudget"/>).</summary>
    internal Func<string, int> Measure { get; init; } = static text => text.Length;

    /// <summary>The rows of <paramref name="all"/> on this page.</summary>
    public List<T> Slice<T>(IReadOnlyList<T> all) =>
        Offset >= all.Count ? [] : all.Skip(Offset).Take(Limit).ToList();
}

/// <summary>
/// Bounded results for the tools that can return hundreds of rows: a <c>limit</c> (default
/// <see cref="DefaultLimit"/>, at most <see cref="MaxLimit"/>), an opaque <c>cursor</c> that resumes after the
/// previous page (<c>meta.next_cursor</c>), and <c>meta.total</c>
/// always. A cursor is base64url JSON <c>{v, o, b}</c>: its offset and a digest binding it to the tool, the
/// query-shaping arguments and the pinned snapshot, so a cursor reused with other arguments, or after the
/// index moved to a new snapshot, is <see cref="InvalidCursorCode"/> instead of resuming at a position that
/// now means something else. The digest is an integrity check, not authorization: every page re-runs the
/// tool's own read gate.
/// </summary>
public static class Paging
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    /// <summary>The <c>meta.error.code</c> of a cursor this query did not issue.</summary>
    public const string InvalidCursorCode = "invalid_cursor";

    /// <summary>How many groups a truncation summary lists per dimension before folding the rest.</summary>
    public const int SummaryTop = 10;

    public const string LimitDescription = "Max 200.";
    public const string CursorDescription = "meta.next_cursor";

    private const int CursorVersion = 1;
    private const int MaxCursorLength = 512;

    private const string InvalidCursorMessage =
        "This cursor was not issued for this query. Pass meta.next_cursor back unchanged with the same arguments; " +
        "if the index has changed since, re-run without a cursor.";

    /// <summary>The effective page size: the default when absent or not positive, else capped at the maximum.</summary>
    public static int ClampLimit(int? limit) => limit is null or <= 0 ? DefaultLimit : Math.Min(limit.Value, MaxLimit);

    /// <summary>
    /// Starts a page of <paramref name="tool"/>. <paramref name="queryArguments"/> are every argument that
    /// shapes the full result (not <c>limit</c> or <c>cursor</c>). False, with the ready-made error response,
    /// when <paramref name="cursor"/> is not one this query issued for the same snapshot.
    /// </summary>
    public static bool TryBegin(
        string tool, int? limit, string? cursor, FederatedReadContext context, out PageRequest page, out string error,
        params object?[] queryArguments)
    {
        var binding = Binding(tool, context.SelectedSnapshotId, queryArguments);
        var size = ClampLimit(limit);
        page = new PageRequest(0, size, binding)
        {
            MaxChars = context.MaxResponseChars,
            Measure = text => ResponseBudget.Measure(text, context)
        };
        error = string.Empty;
        if (string.IsNullOrEmpty(cursor))
            return true;
        if (!TryDecodeCursor(cursor, binding, out var offset))
        {
            error = ResponseBuilder.BuildError(InvalidCursorCode, InvalidCursorMessage);
            return false;
        }
        page = page with { Offset = offset };
        return true;
    }

    /// <summary>
    /// Counts <paramref name="items"/> by <paramref name="key"/>: the <see cref="SummaryTop"/> largest groups
    /// (count descending, then key), then one <c>(N more)</c> entry holding the rest's total.
    /// </summary>
    public static Dictionary<string, int> CountBy<T>(IEnumerable<T> items, Func<T, string?> key)
    {
        var groups = items.GroupBy(i => key(i) ?? "(none)", StringComparer.Ordinal)
            .Select(g => (Key: g.Key, Count: g.Count()))
            .OrderByDescending(g => g.Count).ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
        var summary = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (k, count) in groups.Take(SummaryTop))
            summary[k] = count;
        if (groups.Count > SummaryTop)
            summary[$"({groups.Count - SummaryTop} more)"] = groups.Skip(SummaryTop).Sum(g => g.Count);
        return summary;
    }

    internal static string EncodeCursor(int offset, string binding)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["v"] = CursorVersion,
            ["o"] = offset,
            ["b"] = Digest(offset, binding)
        });
        return Base64Url.EncodeToString(json);
    }

    private static bool TryDecodeCursor(string cursor, string binding, out int offset)
    {
        offset = 0;
        if (cursor.Length > MaxCursorLength)
            return false;
        try
        {
            using var doc = JsonDocument.Parse(Base64Url.DecodeFromChars(cursor), new JsonDocumentOptions { MaxDepth = 2 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("v", out var v) || !v.TryGetInt32(out var version) || version != CursorVersion
                || !root.TryGetProperty("o", out var o) || !o.TryGetInt32(out var decoded) || decoded <= 0
                || !root.TryGetProperty("b", out var b) || b.ValueKind != JsonValueKind.String)
                return false;
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(b.GetString()!), Encoding.UTF8.GetBytes(Digest(decoded, binding))))
                return false;
            offset = decoded;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }
    }

    private static string Binding(string tool, long? snapshotId, object?[] arguments)
    {
        var canonical = new StringBuilder();
        Append(canonical, tool);
        Append(canonical, snapshotId?.ToString(CultureInfo.InvariantCulture));
        foreach (var argument in arguments)
            Append(canonical, argument switch
            {
                null => null,
                bool flag => flag ? "true" : "false",
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => argument.ToString()
            });
        return canonical.ToString();
    }

    // Length-prefixed so no two argument lists share a canonical form; a null is distinct from "".
    private static void Append(StringBuilder canonical, string? value) =>
        canonical.Append(value is null ? "-1" : value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':').Append(value).Append('\n');

    private static string Digest(int offset, string binding) =>
        Base64Url.EncodeToString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{offset.ToString(CultureInfo.InvariantCulture)}\n{binding}")))[..22];
}
