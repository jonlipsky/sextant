using System.ComponentModel;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class FindByAttributeTool
{
    [McpServerTool(Name = "find_by_attribute"), Description("Find symbols decorated with a given attribute.")]
    public static string FindByAttribute(
        DatabaseProvider dbProvider,
        [Description("The fully qualified name of the attribute")] string attribute_fqn,
        [Description("Optional symbol kind filter")] string? kind = null,
        [Description("Scope filter: 'file:/path', 'project:canonical_id', 'solution:/path', or 'all'")] string? scope = null)
    {
        if (!dbProvider.TryBeginRead(out var db, out var readContext, out var authError))
            return authError;

        using var conn = db.OpenReadConnection();
        var symbolStore = new SymbolStore(conn) { Scope = readContext.Scope };

        if (!SymbolKindNames.TryParse(kind, out var kinds, out _, out var kindError))
            return ResponseBuilder.BuildError(ResponseBuilder.InvalidArgumentCode, kindError!, readContext.Provenance);

        // An unknown project/solution or an unrecognized scope is an error, never a silently unfiltered query.
        var scopeFilter = ScopeResolver.Resolve(scope, conn, readContext.Scope);
        if (scopeFilter.Error != null)
            return scopeFilter.ErrorResponse(readContext.Provenance);

        // The attribute as agents write it: `global::Ns.FooAttribute`, `Ns.FooAttribute`, `FooAttribute`, `Foo`,
        // `[Foo]`. One attribute must match; several different ones are named back instead of being merged.
        var attribute = AttributeName.Parse(attribute_fqn);
        if (attribute is null)
            return ResponseBuilder.BuildError(ResponseBuilder.InvalidArgumentCode,
                $"'{attribute_fqn}' is not an attribute name. Pass e.g. 'Obsolete', 'System.ObsoleteAttribute' or " +
                "'global::System.ObsoleteAttribute'.", readContext.Provenance);
        var candidates = symbolStore.GetByAttributeFragment(attribute.Fragment);
        var attributeNames = candidates
            .SelectMany(s => AttributeName.ListOf(s.Attributes))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var matchedNames = attribute.BestMatches(attributeNames);
        if (matchedNames.Count > 1)
            return ResponseBuilder.BuildError(ResponseBuilder.AmbiguousSymbolCode,
                $"'{attribute_fqn.Trim()}' matches {matchedNames.Count} different attributes: " +
                $"{string.Join("; ", matchedNames.Take(5))}{(matchedNames.Count > 5 ? $" (and {matchedNames.Count - 5} more)" : string.Empty)}. " +
                "Pass one of these names exactly.", readContext.Provenance);
        var matches = matchedNames.Count == 0
            ? []
            : candidates.Where(s => AttributeName.ListOf(s.Attributes).Contains(matchedNames[0], StringComparer.Ordinal)).ToList();
        var namer = new SymbolNamer(symbolStore);

        var results = new List<object>();
        long freshness = 0;
        foreach (var s in matches)
        {
            if (kinds != null && !kinds.Contains(s.Kind))
                continue;

            if (!scopeFilter.IsEmpty)
            {
                if (scopeFilter.FilePath != null && s.FilePath != scopeFilter.FilePath)
                    continue;
                if (scopeFilter.ProjectIds != null && !scopeFilter.ProjectIds.Contains(s.ProjectId))
                    continue;
            }

            if (freshness == 0 || s.LastIndexedAt < freshness)
                freshness = s.LastIndexedAt;

            results.Add(new
            {
                fully_qualified_name = namer.QualifiedName(s),
                display_name = s.DisplayName,
                kind = s.Kind.ToString().ToLowerInvariant(),
                file_path = s.FilePath,
                line_start = s.LineStart,
                accessibility = SymbolStore.FormatAccessibility(s.Accessibility),
                attributes = s.Attributes
            });
        }

        string? message = null;
        if (matchedNames.Count == 0)
            message = $"No indexed symbol carries an attribute named '{attribute_fqn.Trim()}'.";
        else if (results.Count == 0)
            message = $"No symbol carries {matchedNames[0]} with the given kind and scope filters.";
        else if (!string.Equals(matchedNames[0], attribute_fqn.Trim(), StringComparison.Ordinal))
            message = $"Matched attribute {matchedNames[0]}.";
        return ResponseBuilder.Build(results, freshness, provenance: readContext.Provenance, message: message);
    }

    /// <summary>An attribute argument, matched against stored attribute names (<c>global::Ns.FooAttribute</c>).</summary>
    private sealed class AttributeName
    {
        private const string Suffix = "Attribute";
        private readonly string[] _segments;

        private AttributeName(string[] segments) => _segments = segments;

        /// <summary>The text every matching stored name contains (its simple name without the suffix).</summary>
        public string Fragment => _segments[^1].EndsWith(Suffix, StringComparison.Ordinal) && _segments[^1].Length > Suffix.Length
            ? _segments[^1][..^Suffix.Length]
            : _segments[^1];

        public static AttributeName? Parse(string? raw)
        {
            var text = (raw ?? string.Empty).Trim();
            if (text.StartsWith('[') && text.EndsWith(']'))
                text = text[1..^1].Trim();
            var paren = text.IndexOf('(');
            if (paren >= 0)
                text = text[..paren].Trim();
            text = Strip(text);
            var segments = text.Split('.');
            return text.Length == 0 || segments.Any(s => s.Length == 0 || s.Any(char.IsWhiteSpace))
                ? null
                : new AttributeName(segments);
        }

        public static List<string> ListOf(string? attributes)
        {
            if (string.IsNullOrEmpty(attributes))
                return [];
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<List<string>>(attributes) ?? [];
            }
            catch (System.Text.Json.JsonException)
            {
                return [];
            }
        }

        /// <summary>
        /// The stored names this argument matches, best tier only: the exact name, then the full path with the
        /// <c>Attribute</c> suffix added, then a path suffix (with or without it), each case-sensitive before
        /// case-insensitive.
        /// </summary>
        public List<string> BestMatches(IReadOnlyList<string> stored)
        {
            foreach (var comparison in new[] { StringComparison.Ordinal, StringComparison.OrdinalIgnoreCase })
            {
                foreach (var fullPath in new[] { true, false })
                {
                    var found = stored.Where(name => Matches(Strip(name).Split('.'), fullPath, comparison)).ToList();
                    if (found.Count > 0)
                        return found;
                }
            }
            return [];
        }

        private bool Matches(string[] path, bool fullPath, StringComparison comparison)
        {
            if (_segments.Length > path.Length || (fullPath && _segments.Length != path.Length))
                return false;
            var offset = path.Length - _segments.Length;
            for (var i = 0; i < _segments.Length - 1; i++)
            {
                if (!string.Equals(_segments[i], path[offset + i], comparison))
                    return false;
            }
            var name = path[^1];
            return string.Equals(_segments[^1], name, comparison) || string.Equals(_segments[^1] + Suffix, name, comparison);
        }

        private static string Strip(string name) =>
            name.StartsWith("global::", StringComparison.Ordinal) ? name["global::".Length..] : name;
    }
}
