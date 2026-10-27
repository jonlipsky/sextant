using System.Text;

namespace Sextant.Store;

/// <summary>
/// Turns a name an agent typed (<c>Process.Runner</c>, <c>IStore&lt;T&gt;</c>, <c>Run(int)</c>, <c>global::A.B</c>) into
/// an FTS5 query that cannot be a syntax error: each identifier token becomes a quoted prefix phrase, all of which must
/// match.
/// </summary>
public static class FtsQueryText
{
    /// <summary>
    /// The safe query for <paramref name="text"/> (a leading <c>global::</c> dropped, split on every character that is not
    /// a letter or digit, e.g. <c>"Process"* "Runner"*</c>), or null when no token remains.
    /// </summary>
    public static string? ToSafeQuery(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var span = text.AsSpan().Trim();
        if (span.StartsWith("global::", StringComparison.Ordinal))
            span = span["global::".Length..];

        return Query(TokenList(WithoutArguments(span)) is { Count: > 0 } t ? t : TokenList(span));
    }

    /// <summary>
    /// The queries to try, in order, for a name FTS5 rejected as written: all its tokens (<see cref="ToSafeQuery"/>), then
    /// the last token alone (the member of <c>Type.Member</c>, the type of <c>Ns.Type</c>: the index holds display names,
    /// never namespaces), then every token joined into one (<c>Process.Runner</c> names <c>ProcessRunner</c>). Empty
    /// when no token remains.
    /// </summary>
    public static IReadOnlyList<string> SafeQueries(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        var span = text.AsSpan().Trim();
        if (span.StartsWith("global::", StringComparison.Ordinal))
            span = span["global::".Length..];

        var tokens = TokenList(WithoutArguments(span));
        if (tokens.Count == 0)
            tokens = TokenList(span);
        if (tokens.Count == 0)
            return [];

        var queries = new List<string> { Query(tokens)! };
        if (tokens.Count > 1)
        {
            queries.Add(Query([tokens[^1]])!);
            queries.Add(Query([string.Concat(tokens)])!);
        }
        return queries;
    }

    private static string? Query(List<string> tokens) =>
        tokens.Count == 0 ? null : string.Join(' ', tokens.Select(t => $"\"{t}\"*"));

    // `IStore<T>` and `Run(int)` name a symbol whose display name has neither the type arguments nor the parameters,
    // so requiring those tokens would match nothing.
    private static string WithoutArguments(ReadOnlySpan<char> text)
    {
        var kept = new StringBuilder();
        var depth = 0;
        foreach (var c in text)
        {
            if (c is '<' or '(')
                depth++;
            else if (c is '>' or ')')
                depth = Math.Max(0, depth - 1);
            else if (depth == 0)
                kept.Append(c);
        }
        return kept.ToString();
    }

    private static List<string> TokenList(ReadOnlySpan<char> span)
    {
        var tokens = new List<string>();
        var start = -1;
        for (var i = 0; i <= span.Length; i++)
        {
            var inToken = i < span.Length && char.IsLetterOrDigit(span[i]);
            if (inToken && start < 0)
                start = i;
            else if (!inToken && start >= 0)
            {
                tokens.Add(span[start..i].ToString());
                start = -1;
            }
        }
        return tokens;
    }

    private static List<string> TokenList(string text) => TokenList(text.AsSpan());
}
