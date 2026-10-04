using Sextant.Core;
using Sextant.Mcp.Tools;
using Sextant.Store;

namespace Sextant.Mcp;

/// <summary>
/// One candidate definition, emitted in response metadata (an ambiguous or unresolved symbol argument, or the
/// project versions sharing one resolved symbol). <see cref="FullyQualifiedName"/> is the symbol's qualified name in a
/// form the tools accept back (see <see cref="SymbolNamer"/>).
/// </summary>
public sealed record SymbolCandidate(
    string ProjectId,
    string SymbolKey,
    string FullyQualifiedName,
    string Kind,
    string FilePath,
    int LineStart);

/// <summary>
/// The project versions that share ONE resolved symbol (the same symbol key in several projects or target
/// frameworks). The tool answers for <see cref="SelectedProjectId"/> and discloses the others here.
/// </summary>
public sealed class SymbolAmbiguity
{
    public required IReadOnlyList<SymbolCandidate> Candidates { get; init; }
    public required string SelectedProjectId { get; init; }
    public required string SelectedSymbolKey { get; init; }
}

/// <summary>The outcome of a <see cref="SymbolResolver.Lookup"/>.</summary>
public enum SymbolLookupStatus
{
    /// <summary>Exactly one symbol is the best match (possibly declared in several project versions).</summary>
    Resolved,

    /// <summary>Several different symbols match equally well; the caller must name one.</summary>
    Ambiguous,

    /// <summary>No symbol matches (in scope, of the requested kind).</summary>
    NotFound,

    /// <summary>The argument is not a symbol name in any accepted form.</summary>
    Invalid
}

/// <summary>A near miss offered when a symbol argument resolves to nothing, with why it was not taken.</summary>
public sealed record SymbolSuggestion(SymbolInfo Symbol, string? Reason);

/// <summary>What a tool asks of a symbol argument.</summary>
public sealed record SymbolLookupOptions
{
    /// <summary>The kinds the tool accepts, or null for any.</summary>
    public IReadOnlySet<SymbolKind>? Kinds { get; init; }

    /// <summary>How the accepted kinds read in a message ("type", "method or constructor"); "symbol" when null.</summary>
    public string? KindDescription { get; init; }

    /// <summary>When the caller (not the tool) chose <see cref="Kinds"/>, e.g. find_symbol's <c>kind</c>.</summary>
    public bool KindsExplicit { get; init; }

    /// <summary>Restricts matches to these project ids (a <c>project_id</c>/<c>scope</c> argument), or null.</summary>
    public IReadOnlySet<long>? ProjectIds { get; init; }

    /// <summary>Restricts matches to symbols declared in this file (a <c>file:</c> scope), or null.</summary>
    public string? FilePath { get; init; }

    /// <summary>How the restriction reads in a message ("in project 'x'"), or null.</summary>
    public string? ScopeDescription { get; init; }

    public static readonly SymbolLookupOptions Any = new();

    /// <summary>Types only (get_type_members, get_type_hierarchy, get_type_dependents).</summary>
    public static readonly SymbolLookupOptions Types = new() { Kinds = SymbolQuery.TypeKinds, KindDescription = "type" };
}

/// <summary>The result of resolving one symbol argument.</summary>
public sealed class SymbolLookup
{
    public required SymbolLookupStatus Status { get; init; }
    public required SymbolQuery Query { get; init; }
    public required SymbolLookupOptions Options { get; init; }

    /// <summary>The resolved symbol (the first project version declaring it), when <see cref="SymbolLookupStatus.Resolved"/>.</summary>
    public SymbolInfo? Symbol { get; init; }

    /// <summary>The other project versions declaring the resolved symbol, or null when it is declared once.</summary>
    public SymbolAmbiguity? Ambiguity { get; init; }

    /// <summary>
    /// The matching symbols, one per symbol key, best first: the equally good ones first (<see cref="TopMatchCount"/>
    /// of them), then every lower-ranked match.
    /// </summary>
    public IReadOnlyList<SymbolInfo> Matches { get; init; } = [];

    /// <summary>How many of <see cref="Matches"/> are equally good (more than one = ambiguous).</summary>
    public int TopMatchCount { get; init; }

    /// <summary>The closest near misses when <see cref="SymbolLookupStatus.NotFound"/> (at most five).</summary>
    public IReadOnlyList<SymbolSuggestion> Suggestions { get; init; } = [];
}

/// <summary>
/// Resolves a symbol argument as coding agents type it (see <see cref="SymbolQuery"/>) to indexed definitions, and
/// reports when it cannot: a tool that needs ONE symbol never silently picks among several different ones
/// (issue #163), and an argument that matches nothing gets its closest candidates.
/// <para>
/// It works over the stored data as is (no re-index): a member's container comes from its Roslyn documentation-ID
/// symbol key (<c>M:Ns.Type.Method(System.Int32)</c>), its parameter types from the same key, and a type's path from
/// its key or fully qualified name. Lookups seek <c>ix_symbols_project_name_nocase</c> by simple name per scoped
/// project version and <c>ix_symbols_key</c> by key prefix for a type's members, so a lookup's cost follows the
/// number of same-named symbols, not the index size.
/// </para>
/// <para>
/// Ranking, best first: an exact symbol-key match; an earlier reading of the argument
/// (<see cref="SymbolQuery.Forms"/>); a full path over a path suffix over a bare name; case-sensitive over
/// case-insensitive; fewer parameters matched only through a generic type parameter; and, when no kind was asked for,
/// a type over a member over a type parameter (so a class is not shadowed by its constructors or by a property of the
/// same name). When several different symbols tie for best, the result is <see cref="SymbolLookupStatus.Ambiguous"/>.
/// </para>
/// </summary>
public static class SymbolResolver
{
    private const int NameLookupLimit = 2000;
    private const int ContainerLimit = 20;
    private const int MemberScanLimit = 5000;
    private const int MaxSuggestions = 5;

    private static readonly string[] MemberKeyPrefixes = ["M:", "P:", "F:", "E:"];

    /// <summary>Resolves <paramref name="input"/> within <paramref name="store"/>'s scope.</summary>
    public static SymbolLookup Lookup(
        SymbolStore store, ProjectStore projects, string? input, SymbolLookupOptions? options = null)
    {
        options ??= SymbolLookupOptions.Any;
        var constructorsOnly = options.Kinds is { Count: 1 } kinds && kinds.Contains(SymbolKind.Constructor);
        var query = SymbolQuery.Parse(input, constructorsOnly);

        var rows = new Dictionary<long, SymbolInfo>();
        var exactIds = new HashSet<long>();
        foreach (var key in ExactKeys(query))
        {
            foreach (var row in store.GetBySymbolKeyInScope(key))
            {
                rows[row.Id] = row;
                exactIds.Add(row.Id);
            }
        }
        if (query.Error is not null && rows.Count == 0)
            return new SymbolLookup { Status = SymbolLookupStatus.Invalid, Query = query, Options = options };
        foreach (var form in query.Forms)
        {
            foreach (var row in Retrieve(store, form))
                rows.TryAdd(row.Id, row);
        }

        var accepted = new List<(SymbolInfo Row, MatchScore Score)>();
        var parameterMisses = new List<SymbolInfo>();
        var kindMisses = new List<(SymbolInfo Row, MatchScore Score)>();
        var scopeMisses = new List<(SymbolInfo Row, MatchScore Score)>();
        foreach (var row in rows.Values)
        {
            var shape = RowShape.Of(row);
            MatchScore? best = exactIds.Contains(row.Id) ? MatchScore.ExactKey : null;
            var parameterMiss = false;
            for (var i = 0; i < query.Forms.Count && best is null; i++)
            {
                var score = Match(query.Forms[i], i, query.Parameters, row, shape, options, out var missedParameters);
                parameterMiss |= missedParameters;
                if (score is { } s)
                    best = s;
            }
            if (best is null)
            {
                if (parameterMiss)
                    parameterMisses.Add(row);
                continue;
            }
            if (!KindAllowed(row.Kind, options.Kinds) || !KindAllowed(row.Kind, query.KindConstraint))
                kindMisses.Add((row, best.Value));
            else if (!InRestriction(row, options))
                scopeMisses.Add((row, best.Value));
            else
                accepted.Add((row, best.Value));
        }

        if (accepted.Count == 0)
        {
            var suggestions = Suggest(store, query, options, parameterMisses, kindMisses, scopeMisses);
            return new SymbolLookup
            {
                Status = SymbolLookupStatus.NotFound, Query = query, Options = options, Suggestions = suggestions
            };
        }

        accepted.Sort((a, b) =>
        {
            var c = a.Score.CompareTo(b.Score);
            if (c != 0)
                return c;
            c = a.Row.ProjectId.CompareTo(b.Row.ProjectId);
            return c != 0 ? c : a.Row.Id.CompareTo(b.Row.Id);
        });
        var bestScore = accepted[0].Score;
        var byKey = new List<SymbolInfo>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var topCount = 0;
        foreach (var (row, score) in accepted)
        {
            if (!seenKeys.Add(row.SymbolKey))
                continue;
            byKey.Add(row);
            if (score.CompareTo(bestScore) == 0)
                topCount++;
        }

        if (topCount > 1)
        {
            return new SymbolLookup
            {
                Status = SymbolLookupStatus.Ambiguous, Query = query, Options = options, Matches = byKey,
                TopMatchCount = topCount
            };
        }

        var symbol = byKey[0];
        var sameKey = accepted.Where(a => a.Row.SymbolKey == symbol.SymbolKey).Select(a => a.Row).ToList();
        return new SymbolLookup
        {
            Status = SymbolLookupStatus.Resolved, Query = query, Options = options, Symbol = symbol, Matches = byKey,
            TopMatchCount = 1,
            Ambiguity = sameKey.Count > 1 ? SameKeyAmbiguity(store, projects, symbol, sameKey) : null
        };
    }

    // The stored keys the argument may spell exactly: its documentation ID or src:/meta: key, and (for an index whose
    // keys are display names, from before documentation-ID keys) the argument itself with and without `global::`.
    private static IEnumerable<string> ExactKeys(SymbolQuery query)
    {
        var keys = new List<string>();
        if (query.ExactKey is { } key)
            keys.Add(key);
        var raw = query.Raw.Trim();
        var bare = raw.StartsWith("global::", StringComparison.Ordinal) ? raw["global::".Length..] : raw;
        if (bare.Length > 0 && !RowShape.IsDocumentationKey(bare))
        {
            keys.Add(bare);
            keys.Add("global::" + bare);
        }
        return keys.Distinct(StringComparer.Ordinal);
    }

    private static SymbolAmbiguity SameKeyAmbiguity(
        SymbolStore store, ProjectStore projects, SymbolInfo selected, List<SymbolInfo> sameKey)
    {
        var namer = new SymbolNamer(store);
        var canonical = FindSymbolTool.BuildCanonicalIdCache(projects);
        return new SymbolAmbiguity
        {
            Candidates = sameKey
                .Select(row => namer.Candidate(row, FindSymbolTool.ResolveCanonicalId(row.ProjectId, canonical)))
                .ToList(),
            SelectedProjectId = FindSymbolTool.ResolveCanonicalId(selected.ProjectId, canonical),
            SelectedSymbolKey = selected.SymbolKey
        };
    }

    private static bool KindAllowed(SymbolKind kind, IReadOnlySet<SymbolKind>? kinds) => kinds is null || kinds.Contains(kind);

    private static bool InRestriction(SymbolInfo row, SymbolLookupOptions options) =>
        (options.ProjectIds is null || options.ProjectIds.Contains(row.ProjectId))
        && (options.FilePath is null || SameFile(row.FilePath, options.FilePath));

    private static bool SameFile(string a, string b) =>
        string.Equals(a.Replace('\\', '/'), b.Replace('\\', '/'),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // ==== retrieval ===================================================================================

    private static IEnumerable<SymbolInfo> Retrieve(SymbolStore store, SymbolQueryForm form)
    {
        switch (form.NameKind)
        {
            case SymbolNameKind.Plain:
            {
                var byName = store.GetByDisplayName(form.Name.Name, limit: NameLookupLimit);
                if (byName.Count < NameLookupLimit || form.Qualifier.Count == 0)
                    return byName;
                // A very common name: the qualifier's types bound the search too.
                return byName.Concat(FindContainers(store, form.Qualifier).SelectMany(t => Members(store, t)));
            }
            case SymbolNameKind.Indexer:
                return FindContainers(store, form.Qualifier)
                    .SelectMany(t => ImmediateMembers(store, "P:" + TypeDocPath(t) + ".", t.ProjectId))
                    .Where(m => m.Kind == SymbolKind.Indexer)
                    .ToList();
            default:
                var rows = new List<SymbolInfo>();
                foreach (var type in FindContainers(store, form.Qualifier))
                {
                    if (form.NameKind != SymbolNameKind.StaticConstructor)
                        rows.AddRange(ImmediateMembers(store, "M:" + TypeDocPath(type) + ".#ctor", type.ProjectId));
                    if (form.NameKind != SymbolNameKind.InstanceConstructor)
                        rows.AddRange(ImmediateMembers(store, "M:" + TypeDocPath(type) + ".#cctor", type.ProjectId));
                }
                return rows;
        }
    }

    /// <summary>
    /// The types (with documentation-ID keys) whose path ends with <paramref name="qualifier"/>, best first (full path,
    /// then case-sensitive); at most <see cref="ContainerLimit"/>.
    /// </summary>
    internal static List<SymbolInfo> FindContainers(SymbolStore store, IReadOnlyList<SymbolPathSegment> qualifier)
    {
        if (qualifier.Count == 0)
            return [];
        return store.GetByDisplayName(qualifier[^1].Name, SymbolQuery.TypeKinds, limit: 500)
            .Where(t => t.SymbolKey.StartsWith("T:", StringComparison.Ordinal))
            .Select(t => (Type: t, Tier: PathTier(qualifier, RowShape.Of(t).FullPath)))
            .Where(x => x.Tier is not null)
            .OrderBy(x => x.Tier!.Value.Tier).ThenBy(x => x.Tier!.Value.CaseInsensitive)
            .ThenBy(x => x.Type.ProjectId).ThenBy(x => x.Type.Id)
            .Select(x => x.Type)
            .Take(ContainerLimit)
            .ToList();
    }

    /// <summary>The documentation-ID path of a type row (<c>Ns.Outer`1.Inner</c>), from its <c>T:</c> key.</summary>
    internal static string TypeDocPath(SymbolInfo type) => type.SymbolKey[2..];

    /// <summary>
    /// Every method, constructor, property, indexer, field and event declared directly in <paramref name="type"/>
    /// (across all its partial declarations, never a nested type's members), in key order. Empty for a type whose key
    /// is not a documentation ID.
    /// </summary>
    public static List<SymbolInfo> Members(SymbolStore store, SymbolInfo type)
    {
        if (!type.SymbolKey.StartsWith("T:", StringComparison.Ordinal))
            return [];
        var path = TypeDocPath(type);
        return MemberKeyPrefixes.SelectMany(prefix => ImmediateMembers(store, prefix + path + ".", type.ProjectId)).ToList();
    }

    // The rows of the project whose key starts with `keyPrefix` and that belong to the type itself (nothing after the
    // prefix names a nested type's member).
    private static IEnumerable<SymbolInfo> ImmediateMembers(SymbolStore store, string keyPrefix, long projectId) =>
        store.GetByKeyPrefix(projectId, keyPrefix, MemberScanLimit)
            .Where(m => IsImmediate(m.SymbolKey, keyPrefix.Length));

    private static bool IsImmediate(string key, int start)
    {
        var depth = 0;
        for (var i = start; i < key.Length; i++)
        {
            var c = key[i];
            if (c == '(' && depth == 0)
                return true;
            if (c == '{')
                depth++;
            else if (c == '}')
                depth--;
            else if (c == '.' && depth == 0)
                return false;
        }
        return true;
    }

    // ==== matching ====================================================================================

    /// <summary>How well a row matches; lower is better (compared field by field).</summary>
    internal readonly record struct MatchScore(
        int Exact, int Form, int Tier, int CaseInsensitive, int Wildcards, int KindPenalty, int StaticConstructor)
        : IComparable<MatchScore>
    {
        public static MatchScore ExactKey => new(0, 0, 0, 0, 0, 0, 0);

        public int CompareTo(MatchScore other)
        {
            var c = Exact.CompareTo(other.Exact);
            if (c == 0) c = Form.CompareTo(other.Form);
            if (c == 0) c = Tier.CompareTo(other.Tier);
            if (c == 0) c = CaseInsensitive.CompareTo(other.CaseInsensitive);
            if (c == 0) c = Wildcards.CompareTo(other.Wildcards);
            if (c == 0) c = KindPenalty.CompareTo(other.KindPenalty);
            if (c == 0) c = StaticConstructor.CompareTo(other.StaticConstructor);
            return c;
        }
    }

    /// <summary>
    /// Matches one reading of the argument against a row, or null. <paramref name="parameterMiss"/> is set when the
    /// name and container match but the parameter list does not (a near miss worth suggesting).
    /// </summary>
    internal static MatchScore? Match(
        SymbolQueryForm form, int formIndex, IReadOnlyList<SymbolTypeTerm>? parameters, SymbolInfo row, RowShape shape,
        SymbolLookupOptions options, out bool parameterMiss)
    {
        parameterMiss = false;
        var caseInsensitive = 0;
        var staticConstructor = 0;
        switch (form.NameKind)
        {
            case SymbolNameKind.Plain:
                if (row.Kind == SymbolKind.Constructor)
                    return null;
                if (!string.Equals(form.Name.Name, shape.Name.Name, StringComparison.Ordinal))
                {
                    if (!string.Equals(form.Name.Name, shape.Name.Name, StringComparison.OrdinalIgnoreCase))
                        return null;
                    caseInsensitive = 1;
                }
                if (form.Name.Arity is { } arity && arity != (shape.Name.Arity ?? 0))
                    return null;
                break;
            case SymbolNameKind.InstanceConstructor:
                if (row.Kind != SymbolKind.Constructor || shape.Name.Name != ".ctor")
                    return null;
                break;
            case SymbolNameKind.StaticConstructor:
                if (row.Kind != SymbolKind.Constructor || shape.Name.Name != ".cctor")
                    return null;
                break;
            case SymbolNameKind.AnyConstructor:
                if (row.Kind != SymbolKind.Constructor)
                    return null;
                staticConstructor = shape.Name.Name == ".cctor" ? 1 : 0;
                break;
            case SymbolNameKind.Indexer:
                if (row.Kind != SymbolKind.Indexer)
                    return null;
                break;
        }

        if (PathTier(form.Qualifier, shape.Container) is not { } tier)
            return null;
        caseInsensitive |= tier.CaseInsensitive;

        var wildcards = 0;
        if (parameters is not null)
        {
            if (shape.Parameters is null
                || row.Kind is not (SymbolKind.Method or SymbolKind.Constructor or SymbolKind.Indexer)
                || shape.Parameters.Count != parameters.Count)
            {
                parameterMiss = true;
                return null;
            }
            for (var i = 0; i < parameters.Count; i++)
            {
                if (!SymbolTypeTerm.Matches(parameters[i], shape.Parameters[i], ref wildcards))
                {
                    parameterMiss = true;
                    return null;
                }
            }
        }

        var kindPenalty = form.NameKind != SymbolNameKind.Plain || options.KindsExplicit
            ? 0
            : SymbolQuery.TypeKinds.Contains(row.Kind) ? 0 : row.Kind == SymbolKind.TypeParameter ? 2 : 1;
        return new MatchScore(1, formIndex, tier.Tier, caseInsensitive, wildcards, kindPenalty, staticConstructor);
    }

    /// <summary>
    /// Whether <paramref name="query"/> is a suffix of <paramref name="path"/> (segment names, and arities the query
    /// spelled): tier 1 = the whole path, 2 = a proper suffix, 3 = the query names no path at all.
    /// </summary>
    internal static (int Tier, int CaseInsensitive)? PathTier(
        IReadOnlyList<SymbolPathSegment> query, IReadOnlyList<SymbolPathSegment> path)
    {
        if (query.Count > path.Count)
            return null;
        var caseInsensitive = 0;
        var offset = path.Count - query.Count;
        for (var i = 0; i < query.Count; i++)
        {
            var q = query[i];
            var p = path[offset + i];
            if (!string.Equals(q.Name, p.Name, StringComparison.Ordinal))
            {
                if (!string.Equals(q.Name, p.Name, StringComparison.OrdinalIgnoreCase))
                    return null;
                caseInsensitive = 1;
            }
            if (q.Arity is { } arity && arity != (p.Arity ?? 0))
                return null;
        }
        var tier = query.Count == path.Count ? 1 : query.Count > 0 ? 2 : 3;
        return (tier, caseInsensitive);
    }

    // ==== suggestions =================================================================================

    private static List<SymbolSuggestion> Suggest(
        SymbolStore store, SymbolQuery query, SymbolLookupOptions options,
        List<SymbolInfo> parameterMisses,
        List<(SymbolInfo Row, MatchScore Score)> kindMisses,
        List<(SymbolInfo Row, MatchScore Score)> scopeMisses)
    {
        var suggestions = new List<SymbolSuggestion>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(SymbolInfo row, string? reason)
        {
            if (suggestions.Count < MaxSuggestions && seen.Add(row.SymbolKey))
                suggestions.Add(new SymbolSuggestion(row, reason));
        }

        bool Acceptable(SymbolInfo row) =>
            KindAllowed(row.Kind, options.Kinds) && KindAllowed(row.Kind, query.KindConstraint) && InRestriction(row, options);

        foreach (var row in parameterMisses.Where(Acceptable).OrderBy(r => r.ProjectId).ThenBy(r => r.Id))
            Add(row, "different parameters");
        foreach (var (row, _) in kindMisses.OrderBy(m => m.Score).ThenBy(m => m.Row.ProjectId).ThenBy(m => m.Row.Id))
            Add(row, $"a {KindName(row.Kind)}");
        foreach (var (row, _) in scopeMisses.OrderBy(m => m.Score).ThenBy(m => m.Row.ProjectId).ThenBy(m => m.Row.Id))
            Add(row, "outside the requested scope");
        if (suggestions.Count >= MaxSuggestions || query.Forms.Count == 0)
            return suggestions;

        var form = query.Forms.FirstOrDefault(f => f.NameKind == SymbolNameKind.Plain) ?? query.Forms[0];
        var name = form.NameKind == SymbolNameKind.Plain ? form.Name.Name : null;

        // A qualified member name whose type exists: the type's members with the closest names (or, for a
        // constructor/indexer the type does not declare, the type itself).
        var containers = FindContainers(store, form.Qualifier);
        if (name is not null && containers.Count > 0)
        {
            var members = containers.Take(5).SelectMany(t => Members(store, t))
                .Where(m => m.Kind != SymbolKind.Constructor && Acceptable(m))
                .Select(m => (Row: m, Distance: Distance(name, RowShape.Of(m).Name.Name)))
                .Where(x => x.Distance <= Math.Max(3, name.Length / 2))
                .OrderBy(x => x.Distance).ThenBy(x => x.Row.SymbolKey, StringComparer.Ordinal)
                .ToList();
            foreach (var (row, _) in members)
                Add(row, null);
        }
        else if (name is null)
        {
            foreach (var type in containers.Where(t => InRestriction(t, options)))
                Add(type, null);
        }

        // Names starting like the argument's name (a typo late in the name, or a missing suffix).
        if (name is not null && suggestions.Count < MaxSuggestions)
        {
            foreach (var row in NearNames(store, name, form.Qualifier, options.Kinds ?? query.KindConstraint, options))
                Add(row, null);
        }

        // A qualified name whose type was not found: the types named like its last qualifier segment.
        if (form.Qualifier.Count > 0 && containers.Count == 0 && suggestions.Count < MaxSuggestions)
        {
            var typeQualifier = form.Qualifier.Take(form.Qualifier.Count - 1).ToList();
            foreach (var row in NearNames(store, form.Qualifier[^1].Name, typeQualifier, SymbolQuery.TypeKinds, options))
                Add(row, null);
        }
        return suggestions;
    }

    // Rows whose simple name shares the longest prefix with `name` (case-insensitive), shortening the prefix down to
    // half the name (at least 3 characters) over at most six index seeks, ranked by edit distance.
    private static List<SymbolInfo> NearNames(
        SymbolStore store, string name, IReadOnlyList<SymbolPathSegment> qualifier, IReadOnlySet<SymbolKind>? kinds,
        SymbolLookupOptions options)
    {
        var minimum = Math.Min(name.Length, Math.Max(3, name.Length / 2));
        var step = Math.Max(1, (name.Length - minimum + 5) / 6);
        var found = new Dictionary<long, SymbolInfo>();
        for (var length = name.Length; length >= minimum && length > 0 && found.Count < 20; length -= step)
        {
            foreach (var row in store.GetByDisplayNamePrefix(name[..length], kinds, options.ProjectIds, limit: 50))
            {
                if (row.Kind != SymbolKind.Constructor && InRestriction(row, options))
                    found.TryAdd(row.Id, row);
            }
        }
        var qualifierName = qualifier.Count > 0 ? qualifier[^1].Name : null;
        return found.Values
            .Select(row =>
            {
                var shape = RowShape.Of(row);
                var distance = Distance(name, shape.Name.Name);
                if (qualifierName is not null)
                    distance += shape.Container.Count > 0 ? Distance(qualifierName, shape.Container[^1].Name) : qualifierName.Length;
                return (Row: row, Distance: distance);
            })
            .OrderBy(x => x.Distance)
            .ThenBy(x => SymbolQuery.TypeKinds.Contains(x.Row.Kind) ? 0 : 1)
            .ThenBy(x => x.Row.SymbolKey, StringComparer.Ordinal)
            .Select(x => x.Row)
            .ToList();
    }

    /// <summary>The case-insensitive Levenshtein distance of two names.</summary>
    internal static int Distance(string a, string b)
    {
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    /// <summary>The lowercase kind name the tools print ("method", "class").</summary>
    public static string KindName(SymbolKind kind) => kind.ToString().ToLowerInvariant();

    /// <summary>"<c>global::Ns.Type.Method(int)</c> (method)": a symbol as messages name it.</summary>
    public static string Describe(SymbolNamer namer, SymbolInfo symbol) =>
        $"{namer.QualifiedName(symbol)} ({KindName(symbol.Kind)})";

    // ==== tool helpers ================================================================================

    /// <summary>
    /// The tool response for a lookup that did not resolve to exactly one symbol: <c>invalid_argument</c>,
    /// <c>symbol_not_found</c> (with the closest candidates) or <c>ambiguous_symbol</c> (with the matching ones), each
    /// a structured error the MCP layer marks <c>isError</c>, whose message says what to pass instead. Every candidate
    /// is named in a form the tool accepts back.
    /// </summary>
    public static string ErrorResponse(
        SymbolStore store, ProjectStore projects, SymbolLookup lookup, SnapshotProvenance? provenance)
    {
        var raw = lookup.Query.Raw.Trim();
        switch (lookup.Status)
        {
            case SymbolLookupStatus.Invalid:
                return ResponseBuilder.BuildError(ResponseBuilder.InvalidArgumentCode, lookup.Query.Error!, provenance);
            case SymbolLookupStatus.Ambiguous:
                return AmbiguousResponse(
                    raw, Candidates(store, projects, lookup.Matches.Take(Math.Min(lookup.TopMatchCount, MaxAmbiguousCandidates))),
                    lookup.TopMatchCount, where: null, provenance);
            default:
            {
                var candidates = Candidates(store, projects, lookup.Suggestions.Select(s => s.Symbol));
                var what = lookup.Options.KindDescription ?? "symbol";
                var where = lookup.Options.ScopeDescription is { } scope ? " " + scope : string.Empty;
                var message = $"No {what} matches '{raw}'{where}.";
                if (candidates.Count > 0)
                {
                    var described = candidates.Select((c, i) =>
                        lookup.Suggestions[i].Reason is { } reason ? $"{Describe(c)} [{reason}]" : Describe(c));
                    message += $" Closest: {string.Join("; ", described)}. Pass one of these names exactly.";
                }
                else
                {
                    message += " Check the spelling, or search with find_symbol (fuzzy=true).";
                }
                return ResponseBuilder.BuildSymbolError(
                    ResponseBuilder.SymbolNotFoundCode, message, candidates, null, provenance);
            }
        }
    }

    /// <summary>The most candidates an <c>ambiguous_symbol</c> error carries in its metadata.</summary>
    public const int MaxAmbiguousCandidates = 10;

    /// <summary>
    /// The <c>ambiguous_symbol</c> error for an argument <paramref name="raw"/> that <paramref name="matchCount"/>
    /// different symbols match equally well (<paramref name="candidates"/> being the first of them), naming the first
    /// five in the message.
    /// </summary>
    public static string AmbiguousResponse(
        string raw, IReadOnlyList<SymbolCandidate> candidates, int matchCount, string? where,
        SnapshotProvenance? provenance)
    {
        var listed = string.Join("; ", candidates.Take(MaxSuggestions).Select(Describe));
        var more = matchCount > MaxSuggestions ? $" (and {matchCount - MaxSuggestions} more)" : string.Empty;
        var message =
            $"'{raw}' matches {matchCount} different symbols{(where is null ? string.Empty : " " + where)}, and this " +
            $"tool needs exactly one. Candidates: {listed}{more}. Pass one of these names exactly (or its symbol_key), " +
            "or qualify the name with its containing type and parameter list, e.g. 'Type.Method(int)'.";
        return ResponseBuilder.BuildSymbolError(
            ResponseBuilder.AmbiguousSymbolCode, message, candidates, matchCount, provenance);
    }

    private static List<SymbolCandidate> Candidates(SymbolStore store, ProjectStore projects, IEnumerable<SymbolInfo> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0)
            return [];
        var namer = new SymbolNamer(store);
        var canonical = FindSymbolTool.BuildCanonicalIdCache(projects);
        return list.Select(row => namer.Candidate(row, FindSymbolTool.ResolveCanonicalId(row.ProjectId, canonical))).ToList();
    }

    private static string Describe(SymbolCandidate c) =>
        $"{c.FullyQualifiedName} ({c.Kind}{(string.IsNullOrEmpty(c.FilePath) ? string.Empty : $", {c.FilePath}:{c.LineStart}")})";

    /// <summary>
    /// A short note for a resolved lookup that also matched lower-ranked symbols (e.g. a class chosen over a property
    /// of the same name), naming one of them so the caller can target it instead; null when nothing else matched.
    /// </summary>
    public static string? ResolutionNote(SymbolStore store, SymbolLookup lookup)
    {
        if (lookup.Status != SymbolLookupStatus.Resolved || lookup.Matches.Count < 2 || lookup.Symbol is null)
            return null;
        var namer = new SymbolNamer(store);
        var others = lookup.Matches.Count - 1;
        var example = lookup.Matches[1];
        return $"Resolved '{lookup.Query.Raw.Trim()}' to {namer.QualifiedName(lookup.Symbol)} " +
               $"({KindName(lookup.Symbol.Kind)}); {others} other symbol{(others == 1 ? " also matches" : "s also match")}, " +
               $"e.g. {namer.QualifiedName(example)} ({KindName(example.Kind)}). Pass a qualified name to target another.";
    }
}

/// <summary>
/// The path of an indexed row as the resolver compares it: its containing segments, its own name segment, and (for a
/// method, constructor or indexer with a documentation-ID key) its parameter types.
/// </summary>
internal sealed record RowShape(
    IReadOnlyList<SymbolPathSegment> Container, SymbolPathSegment Name, IReadOnlyList<SymbolTypeTerm>? Parameters)
{
    /// <summary>The container followed by the name (a type's full path).</summary>
    public IReadOnlyList<SymbolPathSegment> FullPath => [.. Container, Name];

    public static bool IsDocumentationKey(string key) =>
        key.Length > 2 && key[1] == ':' && key[0] is 'T' or 'M' or 'P' or 'F' or 'E';

    public static RowShape Of(SymbolInfo row)
    {
        if (IsDocumentationKey(row.SymbolKey))
        {
            var body = row.SymbolKey[2..];
            IReadOnlyList<SymbolTypeTerm>? parameters = null;
            var open = body.IndexOf('(');
            var head = body;
            if (open >= 0)
            {
                var close = SymbolTypeTerm.MatchingClose(body, open);
                if (close > open)
                    parameters = SymbolQuery.ParseParameterList(body[(open + 1)..close]);
                head = body[..open];
            }
            else if (row.Kind is SymbolKind.Method or SymbolKind.Constructor)
            {
                parameters = [];
            }
            var segments = SymbolTypeTerm.SplitTopLevel(head, '.').Select(DocumentationSegment).ToList();
            var container = segments.Take(segments.Count - 1).ToList();
            var name = row.Kind == SymbolKind.Constructor ? new SymbolPathSegment(row.DisplayName, null) : segments[^1];
            return new RowShape(container, name, parameters);
        }

        if (SymbolQuery.TypeKinds.Contains(row.Kind))
        {
            var fqn = row.FullyQualifiedName.StartsWith("global::", StringComparison.Ordinal)
                ? row.FullyQualifiedName["global::".Length..]
                : row.FullyQualifiedName;
            var segments = SymbolTypeTerm.SplitTopLevel(fqn, '.').Select(GenericSegment).ToList();
            return new RowShape(segments.Take(segments.Count - 1).ToList(), segments[^1], null);
        }
        return new RowShape([], new SymbolPathSegment(row.DisplayName, null), null);
    }

    // `Name`, `Name`1`, `Name``1`, `#ctor`, or an explicit implementation `Ns#IFoo#Bar` (named `Bar`).
    private static SymbolPathSegment DocumentationSegment(string part)
    {
        var s = part;
        var hash = s.LastIndexOf('#');
        if (hash > 0)
            s = s[(hash + 1)..];
        var tick = s.IndexOf('`');
        if (tick < 0)
            return new SymbolPathSegment(s, null);
        return new SymbolPathSegment(s[..tick], int.TryParse(s[tick..].TrimStart('`'), out var n) ? n : null);
    }

    // `Name` or `Name<T, U>` (arity 2).
    private static SymbolPathSegment GenericSegment(string part)
    {
        var open = part.IndexOf('<');
        return open < 0 || !part.EndsWith('>')
            ? new SymbolPathSegment(part, null)
            : new SymbolPathSegment(part[..open], SymbolTypeTerm.SplitTopLevel(part[(open + 1)..^1], ',').Count);
    }
}

/// <summary>
/// Names indexed symbols in a form the tools accept back as a symbol argument, so an agent can paste any name a tool
/// printed into the next call: a type is its fully qualified name (<c>global::Ns.Outer.Inner</c>); a method,
/// constructor, property or indexer is its declaration signature (<c>global::Ns.Type.Method(int, string)</c>,
/// <c>global::Ns.Type.Type(int)</c>, <c>global::Ns.Type.this[int]</c>); a field or event is its type's name plus its
/// own; and anything those spellings would not resolve back to the same symbol (an explicit interface implementation,
/// an operator, a static constructor) is its symbol key (a documentation ID such as <c>M:Ns.Type.op_Addition(…)</c>).
/// </summary>
public sealed class SymbolNamer(SymbolStore store)
{
    private readonly Dictionary<(long, string), SymbolInfo?> _types = new();

    /// <summary>The symbol's qualified name in an accepted form.</summary>
    public string QualifiedName(SymbolInfo symbol)
    {
        if (SymbolQuery.TypeKinds.Contains(symbol.Kind) || !RowShape.IsDocumentationKey(symbol.SymbolKey))
            return SymbolQuery.TypeKinds.Contains(symbol.Kind) || symbol.Kind == SymbolKind.TypeParameter
                ? symbol.FullyQualifiedName
                : symbol.SymbolKey;

        string? candidate = null;
        if (symbol.Kind is SymbolKind.Method or SymbolKind.Constructor or SymbolKind.Property or SymbolKind.Indexer
            && !string.IsNullOrEmpty(symbol.Signature) && symbol.DisplayName != ".cctor")
        {
            candidate = "global::" + symbol.Signature;
        }
        if ((candidate is null || !RoundTrips(candidate, symbol))
            && symbol.Kind is SymbolKind.Field or SymbolKind.Event or SymbolKind.Property)
        {
            var container = ContainingType(symbol)?.FullyQualifiedName;
            candidate = container is null ? null : container + "." + RowShape.Of(symbol).Name.Name;
        }
        return candidate is not null && RoundTrips(candidate, symbol) ? candidate : symbol.SymbolKey;
    }

    /// <summary>A <see cref="SymbolCandidate"/> for <paramref name="symbol"/>, named by <see cref="QualifiedName"/>.</summary>
    public SymbolCandidate Candidate(SymbolInfo symbol, string canonicalProjectId) =>
        new(canonicalProjectId, symbol.SymbolKey, QualifiedName(symbol), SymbolResolver.KindName(symbol.Kind),
            symbol.FilePath, symbol.LineStart);

    /// <summary>The type declaring <paramref name="member"/> in the same project version (from its key), or null.</summary>
    public SymbolInfo? ContainingType(SymbolInfo member)
    {
        if (!RowShape.IsDocumentationKey(member.SymbolKey))
            return null;
        var path = ContainerDocPath(member.SymbolKey);
        if (path is null)
            return null;
        if (!_types.TryGetValue((member.ProjectId, path), out var type))
        {
            type = store.GetBySymbolKey("T:" + path, member.ProjectId);
            _types[(member.ProjectId, path)] = type;
        }
        return type;
    }

    // The documentation-ID path of the type declaring the key's symbol: everything before the last top-level '.' of
    // the key body (before its parameter list).
    private static string? ContainerDocPath(string key)
    {
        var body = key[2..];
        var open = body.IndexOf('(');
        var head = open >= 0 ? body[..open] : body;
        var parts = SymbolTypeTerm.SplitTopLevel(head, '.');
        return parts.Count < 2 ? null : string.Join('.', parts.Take(parts.Count - 1));
    }

    // Whether `name` would resolve back to `symbol` by its whole path and parameters (kind aside).
    private static bool RoundTrips(string name, SymbolInfo symbol)
    {
        var query = SymbolQuery.Parse(name);
        if (query.Error is not null)
            return false;
        var shape = RowShape.Of(symbol);
        for (var i = 0; i < query.Forms.Count; i++)
        {
            var score = SymbolResolver.Match(query.Forms[i], i, query.Parameters, symbol, shape, SymbolLookupOptions.Any, out _);
            if (score is { Tier: 1 })
                return true;
        }
        return false;
    }
}
