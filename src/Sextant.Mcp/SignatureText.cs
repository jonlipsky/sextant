namespace Sextant.Mcp;

/// <summary>
/// Reads the return type and parameter list out of a symbol's printed signature for <c>find_by_signature</c>. It
/// accepts both spellings the index holds: a declaration (<c>static Task&lt;int&gt; Get(this string id, int n = 5)</c>,
/// migration 026) and the legacy display (<c>Ns.Type.Get(string, int)</c>, which has no return type and no parameter
/// names). Brackets, generic arguments and string or character literals in default values are skipped, so a comma or
/// parenthesis inside them never splits a parameter.
/// </summary>
public static class SignatureText
{
    private static readonly HashSet<string> MemberModifiers = new(StringComparer.Ordinal)
    {
        "public", "private", "protected", "internal", "file", "static", "abstract", "virtual", "override", "sealed",
        "extern", "readonly", "const", "required", "async", "new", "unsafe", "volatile", "partial", "event", "delegate",
        "fixed",
    };

    private static readonly HashSet<string> ParameterModifiers = new(StringComparer.Ordinal)
    {
        "this", "params", "ref", "out", "in", "scoped", "readonly",
    };

    /// <summary>The parsed parts of a signature.</summary>
    /// <param name="ReturnType">The return, property, field or event type; null for a constructor, a destructor or a
    /// legacy signature (which has none).</param>
    /// <param name="ParameterTypes">The parameter types in order (names, modifiers and defaults removed); empty when
    /// the member has no parameter list.</param>
    public sealed record Parts(string? ReturnType, IReadOnlyList<string> ParameterTypes);

    /// <summary>Parses <paramref name="signature"/>; an empty or null input has no return type and no parameters.</summary>
    public static Parts Parse(string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return new Parts(null, []);

        var core = signature[..TopLevelIndexOf(signature, IsBodyStart)].TrimEnd();
        string head;
        string? parameters = null;
        if (core.EndsWith(')') && MatchingOpen(core, core.Length - 1) is var open and >= 0)
        {
            head = core[..open];
            parameters = core[(open + 1)..^1];
        }
        else if (core.EndsWith(']') && MatchingOpen(core, core.Length - 1) is var bracket and >= 0
                 && IsIndexerName(core[..bracket]))
        {
            head = core[..bracket];
            parameters = core[(bracket + 1)..^1];
        }
        else
        {
            head = core;
        }

        var types = parameters is null || parameters.Trim().Length == 0
            ? []
            : SplitTopLevel(parameters, ',').Select(ParameterType).ToList();
        return new Parts(ReturnTypeOf(head), types);
    }

    private static string? ReturnTypeOf(string head)
    {
        var tokens = SplitTopLevel(head.Trim(), ' ').Where(t => t.Length > 0).ToList();
        var start = 0;
        while (start < tokens.Count - 1 && MemberModifiers.Contains(tokens[start]))
            start++;
        tokens = tokens.Skip(start).ToList();

        var op = tokens.IndexOf("operator");
        if (op >= 0)
        {
            // `implicit operator int` names its result after the keyword; `Foo operator +` before it.
            if (op > 0 && tokens[op - 1] is "implicit" or "explicit")
                return tokens.Skip(op + 1).FirstOrDefault(t => t != "checked");
            return op > 0 ? string.Join(' ', tokens.Take(op)) : null;
        }

        // A single token is a constructor, a destructor or a legacy `Ns.Type.Member` (no return type).
        return tokens.Count < 2 ? null : string.Join(' ', tokens.Take(tokens.Count - 1));
    }

    private static string ParameterType(string parameter)
    {
        var text = parameter[..TopLevelIndexOf(parameter, (s, i) => s[i] == '=')].Trim();
        var tokens = SplitTopLevel(text, ' ').Where(t => t.Length > 0).ToList();
        var start = 0;
        while (start < tokens.Count - 1 && ParameterModifiers.Contains(tokens[start]))
            start++;
        tokens = tokens.Skip(start).ToList();
        // `Type name` in a declaration; a legacy parameter is only its type.
        return tokens.Count < 2 ? string.Join(' ', tokens) : string.Join(' ', tokens.Take(tokens.Count - 1));
    }

    private static bool IsIndexerName(string head)
    {
        var trimmed = head.TrimEnd();
        return trimmed == "this" || trimmed.EndsWith(" this", StringComparison.Ordinal)
            || trimmed.EndsWith(".this", StringComparison.Ordinal);
    }

    // The start of a property's accessor list (`{ get; }`) or a constant's value (` = 10`, never `operator ==`).
    private static bool IsBodyStart(string s, int i) =>
        s[i] == '{' || (s[i] == '=' && i > 0 && s[i - 1] == ' ' && i + 1 < s.Length && s[i + 1] == ' ');

    /// <summary>The first index outside brackets and literals where <paramref name="match"/> holds, else the length.</summary>
    private static int TopLevelIndexOf(string text, Func<string, int, bool> match)
    {
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '"' or '\'')
            {
                i = SkipLiteral(text, i);
                continue;
            }
            if (depth == 0 && match(text, i))
                return i;
            if (c is '(' or '[' or '<' or '{')
                depth++;
            else if (c is ')' or ']' or '>' or '}' && depth > 0)
                depth--;
        }
        return text.Length;
    }

    /// <summary>The index of the bracket that opens the one closing at <paramref name="close"/>, or -1.</summary>
    private static int MatchingOpen(string text, int close)
    {
        // Scan forward so literals are skipped the same way as everywhere else.
        var stack = new Stack<int>();
        for (var i = 0; i <= close; i++)
        {
            var c = text[i];
            if (c is '"' or '\'')
            {
                i = SkipLiteral(text, i);
                continue;
            }
            if (c is '(' or '[' or '<' or '{')
                stack.Push(i);
            else if (c is ')' or ']' or '>' or '}' && stack.Count > 0)
            {
                var open = stack.Pop();
                if (i == close)
                    return open;
            }
        }
        return -1;
    }

    private static List<string> SplitTopLevel(string text, char separator)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '"' or '\'')
            {
                i = SkipLiteral(text, i);
                continue;
            }
            if (c is '(' or '[' or '<' or '{')
                depth++;
            else if (c is ')' or ']' or '>' or '}' && depth > 0)
                depth--;
            else if (c == separator && depth == 0)
            {
                parts.Add(text[start..i].Trim());
                start = i + 1;
            }
        }
        parts.Add(text[start..].Trim());
        return parts;
    }

    /// <summary>The index of the quote that closes the literal opening at <paramref name="open"/> (escapes honoured).</summary>
    private static int SkipLiteral(string text, int open)
    {
        var quote = text[open];
        for (var i = open + 1; i < text.Length; i++)
        {
            if (text[i] == '\\')
                i++;
            else if (text[i] == quote)
                return i;
        }
        return text.Length - 1;
    }
}
