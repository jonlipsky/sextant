using System.ComponentModel;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

public static class GetNamespaceTreeTool
{
    [McpServerTool(Name = "get_namespace_tree"),
     Description("Get the namespace hierarchy for the indexed codebase, or list symbols within a specific namespace.")]
    public static string GetNamespaceTree(
        DatabaseProvider dbProvider,
        [Description("Namespace to explore. Omit to get top-level namespaces. Use 'global::Company.Core' to drill into a namespace.")]
        string? namespace_prefix = null,
        [Description("Optional project canonical ID to scope to a single project")]
        string? project_id = null,
        [Description("Depth of namespace levels to return (default 1 = immediate children only)")]
        int depth = 1)
    {
        var db = dbProvider.GetDatabase();
        if (db == null)
            return ResponseBuilder.BuildEmpty("No index database found.");

        var conn = db.GetConnection();
        var symbolStore = new SymbolStore(conn);
        var projectStore = new ProjectStore(conn);

        long? projectDbId = null;
        if (project_id != null)
        {
            var proj = projectStore.GetByCanonicalId(project_id);
            if (proj == null)
                return ResponseBuilder.BuildEmpty("Project not found.");
            projectDbId = proj.Value.id;
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
            namespaces = namespaces
                .Where(ns => ns.StartsWith(namespace_prefix + ".") || ns == namespace_prefix)
                .ToList();
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

        var result = new List<object>
        {
            new
            {
                @namespace = namespace_prefix ?? "(root)",
                child_namespaces = childNamespaces,
                symbols = directSymbols
            }
        };

        return ResponseBuilder.Build(result, null);
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
