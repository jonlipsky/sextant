using System.Text.Json;
using Microsoft.Extensions.AI;
using Sextant.Mcp.Tools;

namespace Sextant.Mcp.LlmAssist;

public sealed class ToolRegistry
{
    private readonly DatabaseProvider _dbProvider;
    private readonly Dictionary<string, ToolRegistration> _tools = new();

    public ToolRegistry(DatabaseProvider dbProvider)
    {
        _dbProvider = dbProvider;
        RegisterAll();
    }

    public string Dispatch(string toolName, string argumentsJson)
    {
        if (!_tools.TryGetValue(toolName, out var registration))
            return JsonSerializer.Serialize(new { error = $"Unknown tool: {toolName}" });

        try
        {
            var args = JsonDocument.Parse(argumentsJson).RootElement;
            return registration.Handler(args);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    public List<AITool> BuildAiTools()
    {
        return _tools.Values.Select(t =>
        {
            var schema = JsonDocument.Parse(t.ParametersSchemaJson).RootElement;
            return new RegistryAIFunction(t.Name, t.Description, schema, this) as AITool;
        }).ToList();
    }

    private void RegisterAll()
    {
        Register("find_symbol",
            "Exact or fuzzy lookup of a symbol by name. Use fuzzy=true for FTS5 search.",
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["name"] = new { type = "string", description = "The symbol name or fully qualified name to search for" },
                    ["kind"] = new { type = "string", description = "Optional symbol kind filter (class, method, property, etc.)" },
                    ["project_id"] = new { type = "string", description = "Optional project canonical ID filter" },
                    ["fuzzy"] = new { type = "boolean", description = "Use FTS5 fuzzy search instead of exact match" },
                    ["include_source"] = new { type = "boolean", description = "Include the symbol's source declaration" },
                    ["scope"] = new { type = "string", description = "Scope filter: 'file:/path', 'project:canonical_id', 'solution:/path', or 'all'" }
                },
                required = new[] { "name" }
            },
            args => FindSymbolTool.FindSymbol(
                _dbProvider,
                args.GetProperty("name").GetString()!,
                GetOptionalString(args, "kind"),
                GetOptionalString(args, "project_id"),
                GetOptionalBool(args, "fuzzy") ?? false,
                GetOptionalBool(args, "include_source") ?? false,
                GetOptionalString(args, "scope")
            ));

        Register("semantic_search",
            "FTS5 search over symbol names and documentation.",
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["query"] = new { type = "string", description = "The search query" },
                    ["kind"] = new { type = "string", description = "Optional symbol kind filter" },
                    ["max_results"] = new { type = "integer", description = "Maximum number of results" },
                    ["scope"] = new { type = "string", description = "Scope filter" }
                },
                required = new[] { "query" }
            },
            args => SemanticSearchTool.SemanticSearch(
                _dbProvider,
                args.GetProperty("query").GetString()!,
                GetOptionalString(args, "kind"),
                GetOptionalInt(args, "max_results") ?? 20,
                GetOptionalString(args, "scope")
            ));

        Register("find_references",
            "Find all usages of a symbol by its fully qualified name.",
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["symbol_fqn"] = new { type = "string", description = "The fully qualified name of the symbol" },
                    ["include_projects"] = new { type = "string", description = "Comma-separated canonical IDs to filter results" },
                    ["group_by"] = new { type = "string", description = "Group results by: 'project', 'file', 'kind', or comma-separated" },
                    ["include_source"] = new { type = "boolean", description = "Include source code lines around each reference" },
                    ["scope"] = new { type = "string", description = "Scope filter" },
                    ["access_kind"] = new { type = "string", description = "Filter by access kind: 'read', 'write', 'readwrite'" }
                },
                required = new[] { "symbol_fqn" }
            },
            args => FindReferencesTool.FindReferences(
                _dbProvider,
                args.GetProperty("symbol_fqn").GetString()!,
                GetOptionalString(args, "include_projects"),
                GetOptionalString(args, "group_by"),
                GetOptionalBool(args, "include_source") ?? false,
                GetOptionalString(args, "scope"),
                GetOptionalString(args, "access_kind")
            ));

        Register("get_call_hierarchy",
            "Get callers or callees of a method with depth control.",
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["symbol_fqn"] = new { type = "string", description = "The fully qualified name of the method" },
                    ["direction"] = new { type = "string", description = "Direction: 'callers' or 'callees'" },
                    ["depth"] = new { type = "integer", description = "Maximum depth to traverse" },
                    ["include_source"] = new { type = "boolean", description = "Include source code snippet at each call site" }
                },
                required = new[] { "symbol_fqn", "direction" }
            },
            args => GetCallHierarchyTool.GetCallHierarchy(
                _dbProvider,
                args.GetProperty("symbol_fqn").GetString()!,
                args.GetProperty("direction").GetString()!,
                GetOptionalInt(args, "depth") ?? 5,
                GetOptionalBool(args, "include_source") ?? false
            ));

        Register("get_type_hierarchy",
            "Get base and/or derived types of a type.",
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["symbol_fqn"] = new { type = "string", description = "The fully qualified name of the type" },
                    ["direction"] = new { type = "string", description = "Direction: 'up' (base types), 'down' (derived types), or 'both'" }
                },
                required = new[] { "symbol_fqn" }
            },
            args => GetTypeHierarchyTool.GetTypeHierarchy(
                _dbProvider,
                args.GetProperty("symbol_fqn").GetString()!,
                GetOptionalString(args, "direction") ?? "both"
            ));

        Register("get_type_members",
            "Get all members of a type with their signatures.",
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["symbol_fqn"] = new { type = "string", description = "The fully qualified name of the type" },
                    ["include_inherited"] = new { type = "boolean", description = "Include inherited members" }
                },
                required = new[] { "symbol_fqn" }
            },
            args => GetTypeMembersTool.GetTypeMembers(
                _dbProvider,
                args.GetProperty("symbol_fqn").GetString()!,
                GetOptionalBool(args, "include_inherited") ?? false
            ));

        Register("get_implementors",
            "Get types implementing an interface or overriding a member.",
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["symbol_fqn"] = new { type = "string", description = "The fully qualified name of the interface or member" }
                },
                required = new[] { "symbol_fqn" }
            },
            args => GetImplementorsTool.GetImplementors(
                _dbProvider,
                args.GetProperty("symbol_fqn").GetString()!
            ));

        Register("get_api_surface",
            "Get public/protected symbols for a project, optionally diffed against a previous commit.",
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["project_id"] = new { type = "string", description = "Canonical ID of the project" },
                    ["compare_to_commit"] = new { type = "string", description = "Git commit to compare against (optional)" }
                },
                required = new[] { "project_id" }
            },
            args => GetApiSurfaceTool.GetApiSurface(
                _dbProvider,
                args.GetProperty("project_id").GetString()!,
                GetOptionalString(args, "compare_to_commit")
            ));

        Register("get_namespace_tree",
            "Get the namespace hierarchy for the indexed codebase, or list symbols within a specific namespace.",
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["namespace_prefix"] = new { type = "string", description = "Namespace to explore. Omit to get top-level namespaces." },
                    ["project_id"] = new { type = "string", description = "Optional project canonical ID to scope to a single project" },
                    ["depth"] = new { type = "integer", description = "Depth of namespace levels to return (default 1)" }
                },
                required = Array.Empty<string>()
            },
            args => GetNamespaceTreeTool.GetNamespaceTree(
                _dbProvider,
                GetOptionalString(args, "namespace_prefix"),
                GetOptionalString(args, "project_id"),
                GetOptionalInt(args, "depth") ?? 1
            ));

        Register("get_source_context",
            "Get source code lines from a file with optional context around a target line.",
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["file_path"] = new { type = "string", description = "Absolute path to the source file" },
                    ["line"] = new { type = "integer", description = "Target line number (1-indexed)" },
                    ["context_lines"] = new { type = "integer", description = "Number of context lines before and after" }
                },
                required = new[] { "file_path", "line" }
            },
            args => GetSourceContextTool.GetSourceContext(
                args.GetProperty("file_path").GetString()!,
                args.GetProperty("line").GetInt32(),
                GetOptionalInt(args, "context_lines") ?? 5
            ));

        Register("get_file_symbols",
            "Get all symbols defined in a source file.",
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["file_path"] = new { type = "string", description = "The source file path" }
                },
                required = new[] { "file_path" }
            },
            args => GetFileSymbolsTool.GetFileSymbols(
                _dbProvider,
                args.GetProperty("file_path").GetString()!
            ));

        Register("find_by_attribute",
            "Find symbols decorated with a given attribute.",
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["attribute_fqn"] = new { type = "string", description = "The fully qualified name of the attribute" },
                    ["kind"] = new { type = "string", description = "Optional symbol kind filter" },
                    ["scope"] = new { type = "string", description = "Scope filter" }
                },
                required = new[] { "attribute_fqn" }
            },
            args => FindByAttributeTool.FindByAttribute(
                _dbProvider,
                args.GetProperty("attribute_fqn").GetString()!,
                GetOptionalString(args, "kind"),
                GetOptionalString(args, "scope")
            ));

        Register("find_unreferenced",
            "Find symbols that have zero references — useful for dead-code detection.",
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["kind"] = new { type = "string", description = "Optional symbol kind filter" },
                    ["project_id"] = new { type = "string", description = "Optional project canonical ID filter" },
                    ["accessibility"] = new { type = "string", description = "Optional accessibility filter (public, internal, etc.)" }
                },
                required = Array.Empty<string>()
            },
            args => FindUnreferencedTool.FindUnreferenced(
                _dbProvider,
                GetOptionalString(args, "kind"),
                GetOptionalString(args, "project_id"),
                true,
                GetOptionalString(args, "accessibility")
            ));

        Register("get_type_dependents",
            "Find all types that depend on a given type — through fields, parameters, return types, or inheritance.",
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["symbol_fqn"] = new { type = "string", description = "Fully qualified name of the type" },
                    ["dependency_kind"] = new { type = "string", description = "Filter: 'inherits', 'implements', 'returns', 'parameter_of', 'instantiates', or 'all'" }
                },
                required = new[] { "symbol_fqn" }
            },
            args => GetTypeDependentsTool.GetTypeDependents(
                _dbProvider,
                args.GetProperty("symbol_fqn").GetString()!,
                GetOptionalString(args, "dependency_kind") ?? "all"
            ));

        Register("find_tests",
            "Find test methods, optionally filtered to tests that reference a specific production symbol.",
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["for_symbol"] = new { type = "string", description = "FQN of a production symbol to find tests for" },
                    ["framework"] = new { type = "string", description = "Test framework filter: 'xunit', 'nunit', 'mstest', or 'all'" },
                    ["max_results"] = new { type = "integer", description = "Maximum results (default 50)" }
                },
                required = Array.Empty<string>()
            },
            args => FindTestsTool.FindTests(
                _dbProvider,
                GetOptionalString(args, "for_symbol"),
                GetOptionalString(args, "framework") ?? "all",
                GetOptionalInt(args, "max_results") ?? 50
            ));

        Register("find_comments",
            "Find TODO, HACK, FIXME, BUG, and NOTE comments in the codebase.",
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["tag"] = new { type = "string", description = "Filter by tag: 'TODO', 'HACK', 'FIXME', 'BUG', 'NOTE', or 'all'" },
                    ["search"] = new { type = "string", description = "Search within comment text" },
                    ["project_id"] = new { type = "string", description = "Optional project canonical ID filter" },
                    ["in_symbol"] = new { type = "string", description = "FQN of enclosing symbol" },
                    ["max_results"] = new { type = "integer", description = "Maximum results (default 50)" }
                },
                required = Array.Empty<string>()
            },
            args => FindCommentsTool.FindComments(
                _dbProvider,
                GetOptionalString(args, "tag") ?? "all",
                GetOptionalString(args, "search"),
                GetOptionalString(args, "project_id"),
                GetOptionalString(args, "in_symbol"),
                GetOptionalInt(args, "max_results") ?? 50
            ));

        Register("trace_value",
            "Trace the flow of a value through method calls — find what arguments callers pass or where return values go.",
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["method_fqn"] = new { type = "string", description = "FQN of the method to trace" },
                    ["direction"] = new { type = "string", description = "'origins' (what flows IN) or 'destinations' (where output goes)" },
                    ["parameter"] = new { type = "string", description = "Parameter name or index to trace (for origins)" },
                    ["depth"] = new { type = "integer", description = "Maximum depth of transitive tracing (default 2)" }
                },
                required = new[] { "method_fqn", "direction" }
            },
            args => TraceValueTool.TraceValue(
                _dbProvider,
                args.GetProperty("method_fqn").GetString()!,
                args.GetProperty("direction").GetString()!,
                GetOptionalString(args, "parameter"),
                GetOptionalInt(args, "depth") ?? 2
            ));

        Register("find_by_signature",
            "Find methods/properties by signature characteristics: return type, parameter types, parameter count.",
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["return_type"] = new { type = "string", description = "Return type to match (partial match)" },
                    ["parameter_type"] = new { type = "string", description = "Parameter type to match (partial match)" },
                    ["parameter_count"] = new { type = "integer", description = "Exact number of parameters" },
                    ["kind"] = new { type = "string", description = "Symbol kind filter (default: method)" },
                    ["project_id"] = new { type = "string", description = "Optional project canonical ID filter" },
                    ["max_results"] = new { type = "integer", description = "Maximum results (default 50)" }
                },
                required = Array.Empty<string>()
            },
            args => FindBySignatureTool.FindBySignature(
                _dbProvider,
                GetOptionalString(args, "return_type"),
                GetOptionalString(args, "parameter_type"),
                GetOptionalInt(args, "parameter_count"),
                GetOptionalString(args, "kind"),
                GetOptionalString(args, "project_id"),
                GetOptionalInt(args, "max_results") ?? 50
            ));

        Register("get_impact",
            "Get cross-project consumers of a symbol, submodule pin status, and breaking change classification.",
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["symbol_fqn"] = new { type = "string", description = "Fully qualified name of the symbol" }
                },
                required = new[] { "symbol_fqn" }
            },
            args => GetImpactTool.GetImpact(
                _dbProvider,
                args.GetProperty("symbol_fqn").GetString()!
            ));

        Register("get_project_dependencies",
            "Get the dependency graph for a project, including direct and transitive dependencies.",
            new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["project_id"] = new { type = "string", description = "Canonical ID of the project" },
                    ["transitive"] = new { type = "boolean", description = "Include transitive dependencies (default: false)" }
                },
                required = new[] { "project_id" }
            },
            args => GetProjectDependenciesTool.GetProjectDependencies(
                _dbProvider,
                args.GetProperty("project_id").GetString()!,
                GetOptionalBool(args, "transitive") ?? false
            ));
    }

    private void Register(string name, string description, object schema, Func<JsonElement, string> handler)
    {
        var schemaJson = JsonSerializer.Serialize(schema);
        _tools[name] = new ToolRegistration(name, description, schemaJson, handler);
    }

    private static string? GetOptionalString(JsonElement args, string property)
        => args.TryGetProperty(property, out var val) && val.ValueKind == JsonValueKind.String
            ? val.GetString()
            : null;

    private static bool? GetOptionalBool(JsonElement args, string property)
        => args.TryGetProperty(property, out var val) && val.ValueKind != JsonValueKind.Null
            ? val.GetBoolean()
            : null;

    private static int? GetOptionalInt(JsonElement args, string property)
        => args.TryGetProperty(property, out var val) && val.ValueKind == JsonValueKind.Number
            ? val.GetInt32()
            : null;

    private record ToolRegistration(
        string Name,
        string Description,
        string ParametersSchemaJson,
        Func<JsonElement, string> Handler
    );

    private sealed class RegistryAIFunction : AIFunction
    {
        private readonly string _name;
        private readonly string _description;
        private readonly JsonElement _schema;
        private readonly ToolRegistry _registry;

        public RegistryAIFunction(string name, string description, JsonElement schema, ToolRegistry registry)
        {
            _name = name;
            _description = description;
            _schema = schema;
            _registry = registry;
        }

        public override string Name => _name;
        public override string Description => _description;
        public override JsonElement JsonSchema => _schema;

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            var argsJson = JsonSerializer.Serialize(arguments);
            var result = _registry.Dispatch(_name, argsJson);
            return new ValueTask<object?>(result);
        }
    }
}
