using System.ComponentModel;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class GetNamespaceTreeTool
{
    [McpServerTool(Name = "get_namespace_tree"),
     Description("Namespaces, or the symbols in one. Use to orient instead of listing directories.")]
    public static string GetNamespaceTree(
        DatabaseProvider dbProvider,
        [Description("e.g. global::Company.Core")]
        string? namespace_prefix = null,
        [Description(ToolText.ProjectId)]
        string? project_id = null,
        int depth = 1)
    {
        if (!dbProvider.TryBeginRead(out var db, out var readContext, out var authError))
            return authError;

        using var conn = db.OpenReadConnection();
        var symbolStore = new SymbolStore(conn) { Scope = readContext.Scope };
        var projectStore = new ProjectStore(conn) { Scope = readContext.Scope };

        long? projectDbId = null;
        if (project_id != null)
        {
            var proj = projectStore.GetByCanonicalId(project_id);
            if (proj == null)
                return ResponseBuilder.BuildError(ResponseBuilder.InvalidArgumentCode,
                    $"Unknown project_id '{project_id}'. Use a project canonical ID as listed by get_index_status.",
                    readContext.Provenance);
            projectDbId = proj.Value.id;
        }

        // Stored type names carry the 'global::' alias; accept a prefix with or without it.
        if (namespace_prefix != null)
        {
            namespace_prefix = namespace_prefix.Trim().TrimEnd('.');
            if (namespace_prefix.Length == 0 || namespace_prefix == "global::")
                namespace_prefix = null;
            else if (!namespace_prefix.StartsWith("global::", StringComparison.Ordinal))
                namespace_prefix = "global::" + namespace_prefix;
        }

        var allTypeFqns = symbolStore.GetAllTypeFqns(projectDbId);

        // Extract namespaces from FQNs
        var namespaces = allTypeFqns
            .Select(ExtractNamespace)
            .Where(ns => ns != null)
            .Cast<string>()
            .Distinct()
            .ToList();

        // Filter to prefix
        if (namespace_prefix != null)
        {
            var all = namespaces;
            namespaces = namespaces
                .Where(ns => ns.StartsWith(namespace_prefix + ".") || ns == namespace_prefix)
                .ToList();
            if (namespaces.Count == 0)
                return UnknownNamespace(namespace_prefix, all, project_id, readContext.Provenance);
        }

        // Build namespace tree at requested depth
        var prefixForGrouping = namespace_prefix ?? "";
        var prefixDepth = prefixForGrouping.Length == 0 ? 0 : prefixForGrouping.Split('.').Length;

        var childNamespaces = namespaces
            .Where(ns => ns != prefixForGrouping)
            .Select(ns =>
            {
                var parts = ns.Split('.');
                var targetDepth = Math.Min(prefixDepth + depth, parts.Length);
                return string.Join(".", parts[..targetDepth]);
            })
            .Distinct()
            .Where(ns => ns != prefixForGrouping)
            .Select(ns => new
            {
                name = ns,
                symbol_count = allTypeFqns.Count(fqn =>
                {
                    var fqnNs = ExtractNamespace(fqn);
                    return fqnNs != null && (fqnNs == ns || fqnNs.StartsWith(ns + "."));
                })
            })
            .OrderBy(ns => ns.name)
            .ToList<object>();

        // Get direct type symbols in this namespace
        var directSymbols = new List<object>();
        if (namespace_prefix != null)
        {
            var symbolsInNs = symbolStore.GetByFqnPrefix(namespace_prefix + ".", projectDbId);
            directSymbols = symbolsInNs
                .Where(s =>
                {
                    var ns = ExtractNamespace(s.FullyQualifiedName);
                    return ns == namespace_prefix;
                })
                .Select(s => (object)new
                {
                    fully_qualified_name = s.FullyQualifiedName,
                    kind = s.Kind.ToString().ToLowerInvariant(),
                    display_name = s.DisplayName
                })
                .ToList();
        }

        // The child namespaces, then the namespace's own types, are the rows the size budget keeps in order; the
        // response wraps the kept ones in the one namespace row.
        var rows = childNamespaces.Select(n => (Namespace: true, Row: n))
            .Concat(directSymbols.Select(s => (Namespace: false, Row: s)))
            .ToList();
        return ResponseBuilder.BuildBounded(rows, readContext, provenance: readContext.Provenance,
            shape: kept => new List<object>
            {
                new
                {
                    @namespace = namespace_prefix ?? "(root)",
                    child_namespaces = kept.Where(r => r.Namespace).Select(r => r.Row).ToList(),
                    symbols = kept.Where(r => !r.Namespace).Select(r => r.Row).ToList()
                }
            });
    }

    private static string UnknownNamespace(
        string prefix, List<string> namespaces, string? projectId, SnapshotProvenance? provenance)
    {
        var lastSegment = prefix[(prefix.LastIndexOf('.') + 1)..].Replace("global::", string.Empty);
        var close = namespaces
            .Where(ns => ns.Contains(lastSegment, StringComparison.OrdinalIgnoreCase))
            .OrderBy(ns => ns.Length)
            .ThenBy(ns => ns, StringComparer.Ordinal)
            .Take(5)
            .ToList();
        var message = $"No namespace '{prefix}' contains an indexed type{(projectId != null ? " in that project" : string.Empty)}.";
        message += close.Count > 0
            ? $" Closest: {string.Join(", ", close)}."
            : " Omit namespace_prefix to list the top-level namespaces.";
        return ResponseBuilder.BuildError(ResponseBuilder.InvalidArgumentCode, message, provenance);
    }

    internal static string? ExtractNamespace(string fullyQualifiedName)
    {
        // FQN format: "global::Namespace.Sub.TypeName" or "global::Namespace.Sub.TypeName.MemberName"
        // For types, namespace is everything before the last segment
        var lastDot = fullyQualifiedName.LastIndexOf('.');
        if (lastDot < 0) return null;
        return fullyQualifiedName[..lastDot];
    }
}
