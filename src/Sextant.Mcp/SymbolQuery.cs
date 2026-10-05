using System.Text;
using Sextant.Core;

namespace Sextant.Mcp;

/// <summary>How the last segment of a <see cref="SymbolQueryForm"/> names its symbol.</summary>
public enum SymbolNameKind
{
    /// <summary>A plain name, compared with the symbol's simple name.</summary>
    Plain,

    /// <summary>An instance constructor of the qualifier's type (<c>Type.Type</c> with parameters, <c>Type.#ctor</c>, <c>Type..ctor</c>).</summary>
    InstanceConstructor,

    /// <summary>The static constructor of the qualifier's type (<c>Type.#cctor</c>, <c>Type..cctor</c>).</summary>
    StaticConstructor,

    /// <summary>Any constructor of the qualifier's type (<c>Type.Type</c>; instance constructors rank first).</summary>
    AnyConstructor,

    /// <summary>An indexer of the qualifier's type (<c>Type.this[int]</c>, <c>Type.Item[int]</c>).</summary>
    Indexer
}

/// <summary>One segment of a symbol path: a simple name and, when the input spelled one, its generic arity.</summary>
public readonly record struct SymbolPathSegment(string Name, int? Arity);

/// <summary>
/// One reading of a symbol argument: the containing path (<see cref="Qualifier"/>, possibly a suffix such as
/// <c>Type</c> or <c>Ns.Type</c>, or empty), the symbol's own name, and how that name is to be compared. A query can
/// have several readings (<c>Widget.Widget</c> is a member named <c>Widget</c> of <c>Widget</c>, or its constructor),
/// tried in order.
/// </summary>
public sealed record SymbolQueryForm(
    IReadOnlyList<SymbolPathSegment> Qualifier, SymbolPathSegment Name, SymbolNameKind NameKind);

/// <summary>
/// A normalized parameter (or generic argument) type, compared structurally: the last dotted segment of the type name
/// with C# keywords mapped to their CLR names (<c>int</c> = <c>Int32</c>), generic arguments, an array/pointer suffix,
/// and <see cref="Wildcard"/> for a generic type parameter (which matches any type). Parameter names, modifiers
/// (<c>ref</c>/<c>out</c>/<c>in</c>/<c>params</c>/<c>this</c>), nullability and namespaces are ignored, so
/// <c>out global::System.Int32 count</c>, <c>int</c> and the documentation-ID form <c>System.Int32@</c> are equal.
/// </summary>
public sealed record SymbolTypeTerm(string Name, IReadOnlyList<SymbolTypeTerm> Arguments, string Suffix, bool Wildcard)
{
    private static readonly Dictionary<string, string> Keywords = new(StringComparer.Ordinal)
    {
        ["string"] = "String", ["int"] = "Int32", ["long"] = "Int64", ["bool"] = "Boolean", ["object"] = "Object",
        ["dynamic"] = "Object", ["double"] = "Double", ["float"] = "Single", ["decimal"] = "Decimal",
        ["byte"] = "Byte", ["sbyte"] = "SByte", ["short"] = "Int16", ["ushort"] = "UInt16", ["uint"] = "UInt32",
        ["ulong"] = "UInt64", ["char"] = "Char", ["void"] = "Void", ["nint"] = "IntPtr", ["nuint"] = "UIntPtr"
    };

    private static readonly HashSet<string> Modifiers = new(StringComparer.Ordinal)
    {
        "ref", "out", "in", "params", "this", "scoped", "readonly"
    };

    /// <summary>
    /// The deepest generic-argument/tuple nesting <see cref="Parse"/> reads. A deeper type does not parse (null), so a
    /// crafted argument can never exhaust the stack: the recursion is the only unbounded part of the parser.
    /// </summary>
    public const int MaxNestingDepth = 32;

    /// <summary>Parses one parameter (C# or documentation-ID spelling), or returns null when it is not a type.</summary>
    public static SymbolTypeTerm? Parse(string text) => Parse(text, 0);

    private static SymbolTypeTerm? Parse(string text, int depth)
    {
        if (depth > MaxNestingDepth)
            return null;
        var s = text.Trim();
        // Leading attribute sections: [NotNull] string value.
        while (s.StartsWith('['))
        {
            var close = MatchingClose(s, 0);
            if (close < 0)
                return null;
            s = s[(close + 1)..].TrimStart();
        }
        var eq = IndexOfTopLevel(s, '=');
        if (eq >= 0)
            s = s[..eq].TrimEnd();
        var tokens = SplitTopLevelWhitespace(s).Where(t => !Modifiers.Contains(t)).ToList();
        if (tokens.Count == 0)
            return null;
        // `Type name` (and `Type name` after modifiers): the last token is the parameter's name.
        s = tokens.Count >= 2 ? string.Join(' ', tokens.Take(tokens.Count - 1)) : tokens[0];
        return ParseType(s, depth);
    }

    private static SymbolTypeTerm? ParseType(string text, int depth)
    {
        if (depth > MaxNestingDepth)
            return null;
        var s = text.Trim();
        if (s.Length == 0)
            return null;
        s = s.TrimEnd('@', '?', '!', ' ');
        var suffix = new StringBuilder();
        while (s.Length > 0)
        {
            if (s[^1] == '*')
            {
                suffix.Insert(0, '*');
                s = s[..^1].TrimEnd();
                continue;
            }
            if (s[^1] == ']')
            {
                var open = MatchingOpen(s, s.Length - 1);
                if (open <= 0)
                    return null;
                // [0:,0:] (documentation ID) and [,] (C#) are the same rank-2 array.
                var rank = s[(open + 1)..^1].Count(c => c == ',');
                suffix.Insert(0, "[" + new string(',', rank) + "]");
                s = s[..open].TrimEnd().TrimEnd('?');
                continue;
            }
            break;
        }
        if (s.Length == 0)
            return null;
        if (s.StartsWith("global::", StringComparison.Ordinal))
            s = s["global::".Length..];

        if (s[0] == '(')
        {
            var close = MatchingClose(s, 0);
            if (close != s.Length - 1)
                return null;
            var elements = SplitTopLevel(s[1..^1], ',').Select(e => Parse(e, depth + 1)).ToList();
            if (elements.Count < 2 || elements.Any(e => e is null))
                return null;
            return new SymbolTypeTerm("ValueTuple", elements!, suffix.ToString(), false);
        }

        var segments = SplitTopLevel(s, '.');
        var last = segments[^1].Trim();
        if (last.Length == 0)
            return null;
        var args = new List<SymbolTypeTerm>();
        var genericOpen = last.IndexOfAny(['<', '{']);
        if (genericOpen >= 0)
        {
            var close = MatchingClose(last, genericOpen);
            if (close != last.Length - 1)
                return null;
            foreach (var arg in SplitTopLevel(last[(genericOpen + 1)..^1], ','))
            {
                // An open generic (`List<>`) names no argument types: leave them unconstrained.
                if (arg.Trim().Length == 0)
                {
                    args.Clear();
                    break;
                }
                var parsed = ParseType(arg, depth + 1);
                if (parsed is null)
                    return null;
                args.Add(parsed);
            }
            last = last[..genericOpen].Trim();
        }
        if (last.StartsWith('`'))
            return new SymbolTypeTerm(last, [], suffix.ToString(), Wildcard: true);
        var tick = last.IndexOf('`');
        if (tick > 0)
            last = last[..tick];
        if (last.StartsWith('@'))
            last = last[1..];
        if (last.Length == 0)
            return null;
        if (Keywords.TryGetValue(last, out var clr))
            last = clr;
        // Nullable<T> and T? are the same parameter type.
        if (last == "Nullable" && args.Count == 1)
            return args[0] with { Suffix = args[0].Suffix + suffix };
        return new SymbolTypeTerm(last, args, suffix.ToString(), false);
    }

    /// <summary>
    /// Whether <paramref name="query"/> matches <paramref name="row"/>; <paramref name="wildcards"/> counts the
    /// positions matched only through a generic type parameter (fewer is a closer match). An unparameterized query
    /// name (<c>List</c>) matches any instantiation (<c>List&lt;string&gt;</c>).
    /// </summary>
    public static bool Matches(SymbolTypeTerm query, SymbolTypeTerm row, ref int wildcards)
    {
        if (!string.Equals(query.Suffix, row.Suffix, StringComparison.Ordinal))
            return false;
        if (query.Wildcard || row.Wildcard)
        {
            wildcards++;
            return true;
        }
        if (!string.Equals(query.Name, row.Name, StringComparison.OrdinalIgnoreCase))
            return false;
        if (query.Arguments.Count == 0)
            return true;
        if (query.Arguments.Count != row.Arguments.Count)
            return false;
        for (var i = 0; i < query.Arguments.Count; i++)
        {
            if (!Matches(query.Arguments[i], row.Arguments[i], ref wildcards))
                return false;
        }
        return true;
    }

    // ==== shared text helpers =========================================================================

    internal static bool IsOpen(char c) => c is '<' or '(' or '[' or '{';
    internal static bool IsClose(char c) => c is '>' or ')' or ']' or '}';

    /// <summary>The index of the bracket closing the one at <paramref name="open"/>, or -1.</summary>
    internal static int MatchingClose(string s, int open)
    {
        var depth = 0;
        for (var i = open; i < s.Length; i++)
        {
            if (IsOpen(s[i]))
                depth++;
            else if (IsClose(s[i]) && --depth == 0)
                return i;
        }
        return -1;
    }

    private static int MatchingOpen(string s, int close)
    {
        var depth = 0;
        for (var i = close; i >= 0; i--)
        {
            if (IsClose(s[i]))
                depth++;
            else if (IsOpen(s[i]) && --depth == 0)
                return i;
        }
        return -1;
    }

    internal static int IndexOfTopLevel(string s, char target, int start = 0)
    {
        var depth = 0;
        for (var i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (depth == 0 && c == target)
                return i;
            if (IsOpen(c))
                depth++;
            else if (IsClose(c))
                depth--;
        }
        return -1;
    }

    /// <summary>Splits <paramref name="s"/> on <paramref name="separator"/> outside any bracket pair.</summary>
    internal static List<string> SplitTopLevel(string s, char separator)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (IsOpen(c))
                depth++;
            else if (IsClose(c))
                depth--;
            else if (depth == 0 && c == separator)
            {
                parts.Add(s[start..i]);
                start = i + 1;
            }
        }
        parts.Add(s[start..]);
        return parts;
    }

    internal static List<string> SplitTopLevelWhitespace(string s)
    {
        var parts = new List<string>();
        var depth = 0;
        var current = new StringBuilder();
        foreach (var c in s)
        {
            if (IsOpen(c))
                depth++;
            else if (IsClose(c))
                depth--;
            if (depth == 0 && char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0)
            parts.Add(current.ToString());
        return parts;
    }
}

/// <summary>
/// A symbol argument as a coding agent types it, parsed once and matched against indexed rows by
/// <see cref="SymbolResolver"/>. Accepted spellings (all with or without a leading <c>global::</c>):
/// <list type="bullet">
///   <item>a simple name: <c>ProcessRunner</c>, <c>PublishAsync</c>;</item>
///   <item>a qualified name, fully or partly: <c>Ns.Type</c>, <c>Type.Member</c>, <c>Ns.Type.Member</c>,
///   <c>Outer.Inner</c> (<c>Outer+Inner</c> too), generic types as <c>List&lt;T&gt;</c> or <c>List`1</c>;</item>
///   <item>an optional parameter list: <c>Type.Method(int, string)</c>, <c>Type.Method()</c>, also with parameter
///   names, modifiers or a leading return type (<c>Task&lt;bool&gt; Type.Method(out int count)</c>);</item>
///   <item>constructors: <c>Type.Type(int)</c>, <c>Type..ctor</c>, <c>Type.#ctor</c>, <c>Type..cctor</c>; indexers:
///   <c>Type.this[int]</c>;</item>
///   <item>a Roslyn documentation ID: <c>T:Ns.Type</c>, <c>M:Ns.Type.Method(System.Int32)</c>, <c>P:</c>, <c>F:</c>,
///   <c>E:</c> (the stored symbol key, matched exactly first); and Sextant's own <c>src:</c>/<c>meta:</c> keys.</item>
/// </list>
/// </summary>
public sealed class SymbolQuery
{
    private static readonly SymbolKind[] TypeKindList =
    [
        SymbolKind.Class, SymbolKind.Interface, SymbolKind.Struct, SymbolKind.Enum, SymbolKind.Delegate, SymbolKind.Record
    ];

    /// <summary>The kinds that are types.</summary>
    public static readonly IReadOnlySet<SymbolKind> TypeKinds = new HashSet<SymbolKind>(TypeKindList);

    private SymbolQuery(string raw)
    {
        Raw = raw;
    }

    /// <summary>The argument as received.</summary>
    public string Raw { get; }

    /// <summary>A stored symbol key to look up exactly first (a documentation ID or a <c>src:</c>/<c>meta:</c> key), or null.</summary>
    public string? ExactKey { get; private init; }

    /// <summary>The readings of the argument, best first. Empty for a <c>src:</c>/<c>meta:</c> key (exact lookup only).</summary>
    public IReadOnlyList<SymbolQueryForm> Forms { get; private init; } = [];

    /// <summary>The parameter list the argument gave (empty list = <c>()</c>), or null when it gave none.</summary>
    public IReadOnlyList<SymbolTypeTerm>? Parameters { get; private init; }

    /// <summary>The kinds a documentation-ID prefix allows (<c>M:</c> = methods and constructors), or null.</summary>
    public IReadOnlySet<SymbolKind>? KindConstraint { get; private init; }

    /// <summary>Why the argument cannot name a symbol, or null when it parsed.</summary>
    public string? Error { get; private init; }

    /// <summary>Whether the argument names a containing path (more than a simple name).</summary>
    public bool IsQualified => Forms.Any(f => f.Qualifier.Count > 0);

    /// <summary>The simple name the argument ends with (for messages and suggestions).</summary>
    public string SimpleName => Forms.Count > 0 ? Forms[0].Name.Name : Raw.Trim();

    /// <summary>The accepted spellings, for an <c>invalid_argument</c> message.</summary>
    public const string AcceptedForms =
        "Pass a type or member name, optionally qualified and with a parameter list: 'Type', 'Ns.Type', " +
        "'Type.Member', 'Ns.Type.Method(int, string)', 'Type.Type(int)' for a constructor, 'Type.this[int]' for an " +
        "indexer (a leading 'global::' is optional), or a documentation ID such as 'M:Ns.Type.Method(System.Int32)'.";

    /// <summary>The longest symbol argument parsed; a longer one is an <c>invalid_argument</c> error.</summary>
    public const int MaxLength = 2048;

    /// <summary>
    /// Parses <paramref name="raw"/>. When <paramref name="constructorsOnly"/> is set (the caller asks only for
    /// constructors), a plain path also reads as the constructors of the type it names.
    /// </summary>
    public static SymbolQuery Parse(string? raw, bool constructorsOnly = false)
    {
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0)
            return Invalid(raw ?? string.Empty, "The symbol argument is empty.");
        if (text.Length > MaxLength)
            return Invalid(raw!, $"The symbol argument is longer than {MaxLength} characters.");
        if (text.StartsWith("global::", StringComparison.Ordinal))
            text = text["global::".Length..].TrimStart();

        if (text.StartsWith("src:", StringComparison.Ordinal) || text.StartsWith("meta:", StringComparison.Ordinal))
            return new SymbolQuery(raw!) { ExactKey = text };

        if (text.Length > 2 && text[1] == ':' && "TMPFEN".Contains(text[0]))
            return ParseDocumentationId(raw!, text);

        return ParseCSharp(raw!, text, constructorsOnly);
    }

    private static SymbolQuery Invalid(string raw, string error) => new(raw) { Error = error };

    private static readonly (string Entity, char Character)[] Entities =
        [("&lt;", '<'), ("&gt;", '>'), ("&amp;", '&'), ("&quot;", '"'), ("&#39;", '\'')];

    /// <summary>
    /// <paramref name="raw"/> with the HTML entities a client may have escaped it with (<c>&amp;lt;</c>, <c>&amp;gt;</c>,
    /// <c>&amp;amp;</c>, <c>&amp;quot;</c>, <c>&amp;#39;</c>) decoded in one pass, so <c>List&amp;lt;T&amp;gt;</c> reads as
    /// <c>List&lt;T&gt;</c>; null when it holds none of them.
    /// </summary>
    internal static string? Unescape(string? raw)
    {
        if (raw is null || !raw.Contains('&'))
            return null;
        var text = new StringBuilder(raw.Length);
        var changed = false;
        for (var i = 0; i < raw.Length; i++)
        {
            var entity = raw[i] == '&'
                ? Array.FindIndex(Entities, e => string.CompareOrdinal(raw, i, e.Entity, 0, e.Entity.Length) == 0)
                : -1;
            if (entity < 0)
            {
                text.Append(raw[i]);
                continue;
            }
            text.Append(Entities[entity].Character);
            i += Entities[entity].Entity.Length - 1;
            changed = true;
        }
        return changed ? text.ToString() : null;
    }

    /// <summary>
    /// The argument's trailing <c>Type.Member</c>: each reading whose path has more than one qualifying segment, cut to
    /// the last one (<c>Wrong.Ns.Type.Method(int)</c> reads as <c>Type.Method(int)</c>), with the parameter list and
    /// kind constraint kept. Null when no reading has such a path.
    /// </summary>
    internal SymbolQuery? TrailingMember()
    {
        if (Error is not null)
            return null;
        var forms = Forms.Where(f => f.Qualifier.Count > 1)
            .Select(f => f with { Qualifier = [f.Qualifier[^1]] })
            .ToList();
        return forms.Count == 0
            ? null
            : new SymbolQuery(Raw) { Forms = forms, Parameters = Parameters, KindConstraint = KindConstraint };
    }

    /// <summary>
    /// The argument's trailing <c>Type</c>: the simple name of a qualified plain reading without a parameter list
    /// (<c>Wrong.Ns.Type</c> reads as <c>Type</c>), restricted to types, so a member name is never read on its own.
    /// Null when the argument has a parameter list, no qualified plain reading, or a kind constraint that excludes
    /// every type.
    /// </summary>
    internal SymbolQuery? TrailingType()
    {
        if (Error is not null || Parameters is not null)
            return null;
        var form = Forms.FirstOrDefault(f => f.NameKind == SymbolNameKind.Plain && f.Qualifier.Count > 0);
        if (form is null)
            return null;
        var kinds = KindConstraint is null ? TypeKinds : new HashSet<SymbolKind>(TypeKinds.Where(KindConstraint.Contains));
        return kinds.Count == 0
            ? null
            : new SymbolQuery(Raw) { Forms = [form with { Qualifier = [] }], KindConstraint = kinds };
    }

    private static SymbolQuery ParseDocumentationId(string raw, string text)
    {
        if (text[0] == 'N')
            return Invalid(raw, "Namespaces are not indexed as symbols; name a type or member in the namespace instead.");
        var body = text[2..];
        var paramOpen = body.IndexOf('(');
        IReadOnlyList<SymbolTypeTerm>? parameters = null;
        var head = body;
        if (paramOpen >= 0)
        {
            var close = SymbolTypeTerm.MatchingClose(body, paramOpen);
            if (close < 0)
                return Invalid(raw, $"The parameter list of '{raw.Trim()}' is not closed. {AcceptedForms}");
            var parsed = ParseParameterList(body[(paramOpen + 1)..close]);
            if (parsed is null)
                return Invalid(raw, $"The parameter list of '{raw.Trim()}' could not be read. {AcceptedForms}");
            parameters = parsed;
            head = body[..paramOpen];
        }
        else if (text[0] == 'M')
        {
            // A documentation ID of a parameterless method has no parentheses.
            parameters = [];
        }

        var segments = new List<SymbolPathSegment>();
        foreach (var part in SymbolTypeTerm.SplitTopLevel(head, '.'))
        {
            var segment = DocumentationSegment(part);
            if (segment is null)
                return Invalid(raw, $"'{raw.Trim()}' is not a valid documentation ID. {AcceptedForms}");
            segments.Add(segment.Value);
        }
        if (segments.Count == 0)
            return Invalid(raw, $"'{raw.Trim()}' is not a valid documentation ID. {AcceptedForms}");

        var name = segments[^1];
        var qualifier = segments.Take(segments.Count - 1).ToList();
        var nameKind = SymbolNameKind.Plain;
        IReadOnlySet<SymbolKind> kinds;
        switch (text[0])
        {
            case 'T':
                kinds = TypeKinds;
                break;
            case 'M':
                kinds = new HashSet<SymbolKind> { SymbolKind.Method, SymbolKind.Constructor };
                if (name.Name == "#ctor")
                    nameKind = SymbolNameKind.InstanceConstructor;
                else if (name.Name == "#cctor")
                    nameKind = SymbolNameKind.StaticConstructor;
                break;
            case 'P':
                kinds = new HashSet<SymbolKind> { SymbolKind.Property, SymbolKind.Indexer };
                if (parameters is not null)
                    nameKind = SymbolNameKind.Indexer;
                break;
            case 'F':
                kinds = new HashSet<SymbolKind> { SymbolKind.Field };
                break;
            default:
                kinds = new HashSet<SymbolKind> { SymbolKind.Event };
                break;
        }
        if (nameKind != SymbolNameKind.Plain && qualifier.Count == 0)
            return Invalid(raw, $"'{raw.Trim()}' names a constructor or indexer without its type. {AcceptedForms}");
        return new SymbolQuery(raw)
        {
            ExactKey = text,
            Forms = [new SymbolQueryForm(qualifier, name, nameKind)],
            Parameters = parameters,
            KindConstraint = kinds
        };
    }

    // One '.'-segment of a documentation ID: `Name`, `Name`1` (type arity), `Name``1` (method arity), `#ctor`, or an
    // explicit implementation `Ns#IFoo#Bar` (its own name is after the last '#').
    private static SymbolPathSegment? DocumentationSegment(string part)
    {
        var s = part.Trim();
        if (s.Length == 0)
            return null;
        if (s is "#ctor" or "#cctor")
            return new SymbolPathSegment(s, null);
        var hash = s.LastIndexOf('#');
        if (hash >= 0)
            s = s[(hash + 1)..];
        int? arity = null;
        var tick = s.IndexOf('`');
        if (tick >= 0)
        {
            if (!int.TryParse(s[tick..].TrimStart('`'), out var n))
                return null;
            arity = n;
            s = s[..tick];
        }
        return s.Length == 0 ? null : new SymbolPathSegment(s, arity);
    }

    private static SymbolQuery ParseCSharp(string raw, string text, bool constructorsOnly)
    {
        var display = raw.Trim();
        // The parameter list starts at the first top-level '(' (or, for an indexer, '[').
        var paramOpen = IndexOfTopLevelOutsideAngles(text, '(');
        var bracketList = false;
        if (paramOpen < 0)
        {
            paramOpen = IndexOfTopLevelOutsideAngles(text, '[');
            bracketList = paramOpen >= 0;
        }

        IReadOnlyList<SymbolTypeTerm>? parameters = null;
        var head = text;
        if (paramOpen >= 0)
        {
            var close = SymbolTypeTerm.MatchingClose(text, paramOpen);
            if (close < 0 || text[(close + 1)..].Trim().Length > 0)
                return Invalid(raw, $"'{display}' has an unbalanced or trailing parameter list. {AcceptedForms}");
            parameters = ParseParameterList(text[(paramOpen + 1)..close]);
            if (parameters is null)
                return Invalid(raw, $"The parameter list of '{display}' could not be read. {AcceptedForms}");
            head = text[..paramOpen].Trim();
        }

        // A leading return type or modifiers (`public Task Ns.Type.Method`): the name is the last token.
        var tokens = SymbolTypeTerm.SplitTopLevelWhitespace(head);
        if (tokens.Count == 0)
            return Invalid(raw, $"'{display}' names no symbol. {AcceptedForms}");
        head = tokens[^1];
        if (head.StartsWith("global::", StringComparison.Ordinal))
            head = head["global::".Length..];

        // Reflection's nested-type separator, and the metadata names of constructors.
        head = head.Replace('+', '.');
        if (head.EndsWith("..ctor", StringComparison.Ordinal))
            head = head[..^"..ctor".Length] + ".#ctor";
        else if (head.EndsWith("..cctor", StringComparison.Ordinal))
            head = head[..^"..cctor".Length] + ".#cctor";
        else if (head is ".ctor" or ".cctor")
            head = "#" + head[1..];

        var segments = new List<SymbolPathSegment>();
        foreach (var part in SymbolTypeTerm.SplitTopLevel(head, '.'))
        {
            var segment = CSharpSegment(part);
            if (segment is null)
                return Invalid(raw, $"'{display}' is not a symbol name. {AcceptedForms}");
            segments.Add(segment.Value);
        }

        var name = segments[^1];
        var qualifier = segments.Take(segments.Count - 1).ToList();
        var forms = new List<SymbolQueryForm>();
        if (name.Name is "#ctor" or "#cctor")
        {
            if (qualifier.Count == 0)
                return Invalid(raw, $"'{display}' names a constructor without its type: pass 'Type.Type' or 'Type..ctor'.");
            forms.Add(new SymbolQueryForm(qualifier, name,
                name.Name == "#ctor" ? SymbolNameKind.InstanceConstructor : SymbolNameKind.StaticConstructor));
        }
        else if (bracketList || name.Name == "this")
        {
            if (name.Name is not ("this" or "Item") || qualifier.Count == 0)
                return Invalid(raw, $"'{display}' is not an indexer: pass 'Type.this[int]'. {AcceptedForms}");
            forms.Add(new SymbolQueryForm(qualifier, name, SymbolNameKind.Indexer));
        }
        else
        {
            var plain = new SymbolQueryForm(qualifier, name, SymbolNameKind.Plain);
            SymbolQueryForm? constructor = qualifier.Count > 0 && string.Equals(qualifier[^1].Name, name.Name, StringComparison.Ordinal)
                ? new SymbolQueryForm(qualifier, name, SymbolNameKind.AnyConstructor)
                : null;
            if (constructorsOnly)
                forms.Add(new SymbolQueryForm(segments, name, SymbolNameKind.AnyConstructor));
            if (constructor is not null && parameters is not null)
                forms.Add(constructor);
            forms.Add(plain);
            if (constructor is not null && parameters is null)
                forms.Add(constructor);
        }

        return new SymbolQuery(raw) { Forms = forms, Parameters = parameters };
    }

    // The first top-level `target` that is not inside <...> (generic arguments), or -1.
    private static int IndexOfTopLevelOutsideAngles(string s, char target)
    {
        var depth = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (depth == 0 && c == target)
                return i;
            if (c is '<' or '{' || (depth > 0 && SymbolTypeTerm.IsOpen(c)))
                depth++;
            else if (depth > 0 && SymbolTypeTerm.IsClose(c))
                depth--;
        }
        return -1;
    }

    // One '.'-segment of a C# name: an identifier (optionally `@`-escaped) with optional generic arguments
    // (`List<T>`, `Dictionary<,>`) or arity (`List`1`), or `#ctor`/`#cctor`.
    private static SymbolPathSegment? CSharpSegment(string part)
    {
        var s = part.Trim();
        if (s.Length == 0)
            return null;
        if (s is "#ctor" or "#cctor")
            return new SymbolPathSegment(s, null);
        int? arity = null;
        var open = s.IndexOfAny(['<', '{']);
        if (open >= 0)
        {
            var close = SymbolTypeTerm.MatchingClose(s, open);
            if (close != s.Length - 1)
                return null;
            arity = SymbolTypeTerm.SplitTopLevel(s[(open + 1)..^1], ',').Count;
            s = s[..open].Trim();
        }
        var tick = s.IndexOf('`');
        if (tick >= 0)
        {
            if (!int.TryParse(s[tick..].TrimStart('`'), out var n))
                return null;
            arity = n;
            s = s[..tick];
        }
        if (s.StartsWith('@'))
            s = s[1..];
        if (s.Length == 0 || !(char.IsLetter(s[0]) || s[0] == '_'))
            return null;
        foreach (var c in s)
        {
            if (!(char.IsLetterOrDigit(c) || c == '_'))
                return null;
        }
        return new SymbolPathSegment(s, arity);
    }

    /// <summary>Parses the inside of a parameter list (C# or documentation ID), or null when a parameter is not a type.</summary>
    internal static IReadOnlyList<SymbolTypeTerm>? ParseParameterList(string inside)
    {
        if (inside.Trim().Length == 0)
            return [];
        var terms = new List<SymbolTypeTerm>();
        foreach (var part in SymbolTypeTerm.SplitTopLevel(inside, ','))
        {
            var term = SymbolTypeTerm.Parse(part);
            if (term is null)
                return null;
            terms.Add(term);
        }
        return terms;
    }
}
