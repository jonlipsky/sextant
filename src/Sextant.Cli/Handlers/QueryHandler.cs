namespace Sextant.Cli.Handlers;

internal static class QueryHandler
{
    public static int Run(string tool, string[] toolArgs, string? db, string? profile)
    {
        var config = Core.SextantConfiguration.Load();
        var dbPath = DbResolver.Resolve(db, profile, config);
        if (dbPath == null) return 1;

        using var dbProvider = new Mcp.DatabaseProvider(dbPath);

        string result;
        try
        {
            result = tool.ToLowerInvariant() switch
            {
                "find-symbol" => RunFindSymbol(dbProvider, toolArgs),
                "find-references" => RunFindReferences(dbProvider, toolArgs),
                "get-type-members" => RunGetTypeMembers(dbProvider, toolArgs),
                "get-file-symbols" => RunGetFileSymbols(dbProvider, toolArgs),
                "get-call-hierarchy" => RunGetCallHierarchy(dbProvider, toolArgs, config),
                "get-implementors" => RunGetImplementors(dbProvider, toolArgs),
                "get-type-hierarchy" => RunGetTypeHierarchy(dbProvider, toolArgs),
                "semantic-search" => RunSemanticSearch(dbProvider, toolArgs, config),
                "get-dependencies" => RunGetDependencies(dbProvider, toolArgs),
                "get-impact" => RunGetImpact(dbProvider, toolArgs),
                "get-api-surface" => RunGetApiSurface(dbProvider, toolArgs),
                "get-index-status" => RunGetIndexStatus(dbProvider),
                "find-unreferenced" => RunFindUnreferenced(dbProvider, toolArgs),
                _ => throw new ArgumentException($"Unknown tool: {tool}")
            };
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(result);
            var pretty = System.Text.Json.JsonSerializer.Serialize(doc, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(pretty);
        }
        catch
        {
            Console.WriteLine(result);
        }

        return 0;
    }

    private static string GetArg(string[] args, int index, string name)
    {
        if (index >= args.Length)
            throw new ArgumentException($"Missing required argument: {name}");
        return args[index];
    }

    private static string? GetOption(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == flag)
                return args[i + 1];
        return null;
    }

    private static bool HasFlag(string[] args, string flag) => args.Contains(flag);

    private static string RunFindSymbol(Mcp.DatabaseProvider db, string[] args)
    {
        var name = GetArg(args, 0, "<name>");
        var fuzzy = HasFlag(args, "--fuzzy");
        var kind = GetOption(args, "--kind");
        return Mcp.Tools.FindSymbolTool.FindSymbol(db, name, kind, null, fuzzy);
    }

    private static string RunFindReferences(Mcp.DatabaseProvider db, string[] args)
        => Mcp.Tools.FindReferencesTool.FindReferences(db, GetArg(args, 0, "<symbol-fqn>"));

    private static string RunGetTypeMembers(Mcp.DatabaseProvider db, string[] args)
        => Mcp.Tools.GetTypeMembersTool.GetTypeMembers(db, GetArg(args, 0, "<symbol-fqn>"));

    private static string RunGetFileSymbols(Mcp.DatabaseProvider db, string[] args)
        => Mcp.Tools.GetFileSymbolsTool.GetFileSymbols(db, GetArg(args, 0, "<file-path>"));

    private static string RunGetCallHierarchy(Mcp.DatabaseProvider db, string[] args, Core.SextantConfiguration config)
    {
        var fqn = GetArg(args, 0, "<symbol-fqn>");
        var direction = GetOption(args, "--direction") ?? "callees";
        var depthStr = GetOption(args, "--depth");
        var depth = depthStr != null ? int.Parse(depthStr) : config.MaxCallHierarchyDepth;
        return Mcp.Tools.GetCallHierarchyTool.GetCallHierarchy(db, fqn, direction, depth);
    }

    private static string RunGetImplementors(Mcp.DatabaseProvider db, string[] args)
        => Mcp.Tools.GetImplementorsTool.GetImplementors(db, GetArg(args, 0, "<symbol-fqn>"));

    private static string RunGetTypeHierarchy(Mcp.DatabaseProvider db, string[] args)
    {
        var fqn = GetArg(args, 0, "<symbol-fqn>");
        var direction = GetOption(args, "--direction") ?? "both";
        return Mcp.Tools.GetTypeHierarchyTool.GetTypeHierarchy(db, fqn, direction);
    }

    private static string RunSemanticSearch(Mcp.DatabaseProvider db, string[] args, Core.SextantConfiguration config)
    {
        var query = GetArg(args, 0, "<query>");
        var kind = GetOption(args, "--kind");
        var maxStr = GetOption(args, "--max");
        var max = maxStr != null ? int.Parse(maxStr) : config.FtsMaxResults;
        return Mcp.Tools.SemanticSearchTool.SemanticSearch(db, query, kind, max);
    }

    private static string RunGetDependencies(Mcp.DatabaseProvider db, string[] args)
    {
        var projectId = GetArg(args, 0, "<project-id>");
        var transitive = HasFlag(args, "--transitive");
        return Mcp.Tools.GetProjectDependenciesTool.GetProjectDependencies(db, projectId, transitive);
    }

    private static string RunGetImpact(Mcp.DatabaseProvider db, string[] args)
        => Mcp.Tools.GetImpactTool.GetImpact(db, GetArg(args, 0, "<symbol-fqn>"));

    private static string RunGetApiSurface(Mcp.DatabaseProvider db, string[] args)
    {
        var projectId = GetArg(args, 0, "<project-id>");
        var diff = GetOption(args, "--diff");
        return Mcp.Tools.GetApiSurfaceTool.GetApiSurface(db, projectId, diff);
    }

    private static string RunGetIndexStatus(Mcp.DatabaseProvider db)
        => Mcp.Tools.GetIndexStatusTool.GetIndexStatus(db);

    private static string RunFindUnreferenced(Mcp.DatabaseProvider db, string[] args)
    {
        var kind = GetOption(args, "--kind");
        var projectId = GetOption(args, "--project");
        var includeTests = HasFlag(args, "--include-tests");
        var accessibility = GetOption(args, "--accessibility");
        return Mcp.Tools.FindUnreferencedTool.FindUnreferenced(db, kind, projectId, !includeTests, accessibility);
    }
}
