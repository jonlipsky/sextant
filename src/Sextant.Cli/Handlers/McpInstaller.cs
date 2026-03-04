using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sextant.Cli.Handlers;

public static class McpInstaller
{
    private static readonly string[] SupportedTools = ["claude-code", "cursor", "copilot", "vscode", "codex", "opencode"];

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static void Install(string tool, string? dbPath = null, string? profile = null)
    {
        tool = tool.ToLowerInvariant();
        ValidateTool(tool);

        var (command, args) = BuildMcpServerConfig(dbPath, profile);

        switch (tool)
        {
            case "claude-code":
                InstallJsonConfig(".mcp.json", "mcpServers", command, args);
                EnsureClaudeMdSection();
                EnsureAgentAndSkill();
                break;
            case "cursor":
                InstallJsonConfig(Path.Combine(".cursor", "mcp.json"), "mcpServers", command, args);
                break;
            case "copilot":
            case "vscode":
                InstallJsonConfig(Path.Combine(".vscode", "mcp.json"), "servers", command, args);
                break;
            case "codex":
                InstallCodexConfig(command, args);
                break;
            case "opencode":
                InstallOpenCodeConfig(command, args);
                break;
        }

        EnsureGitignore();
    }

    public static void Uninstall(string tool)
    {
        tool = tool.ToLowerInvariant();
        ValidateTool(tool);

        switch (tool)
        {
            case "claude-code":
                UninstallJsonConfig(".mcp.json", "mcpServers");
                break;
            case "cursor":
                UninstallJsonConfig(Path.Combine(".cursor", "mcp.json"), "mcpServers");
                break;
            case "copilot":
            case "vscode":
                UninstallJsonConfig(Path.Combine(".vscode", "mcp.json"), "servers");
                break;
            case "codex":
                UninstallCodexConfig();
                break;
            case "opencode":
                UninstallOpenCodeConfig();
                break;
        }
    }

    // Overloads for testing with explicit base directory and command/args
    internal static void Install(string tool, string baseDir, string? dbPath = null, string? cliProjectPath = null, string? profile = null)
    {
        tool = tool.ToLowerInvariant();
        ValidateTool(tool);

        // Tests always use dotnet run --project
        cliProjectPath ??= FindCliProjectPath();
        var args = BuildDotnetRunArgs(cliProjectPath, dbPath, profile);
        var command = "dotnet";

        switch (tool)
        {
            case "claude-code":
                InstallJsonConfig(Path.Combine(baseDir, ".mcp.json"), "mcpServers", command, args, useFullPath: true);
                EnsureClaudeMdSection(baseDir);
                EnsureAgentAndSkill(baseDir);
                break;
            case "cursor":
                InstallJsonConfig(Path.Combine(baseDir, ".cursor", "mcp.json"), "mcpServers", command, args, useFullPath: true);
                break;
            case "copilot":
            case "vscode":
                InstallJsonConfig(Path.Combine(baseDir, ".vscode", "mcp.json"), "servers", command, args, useFullPath: true);
                break;
            case "codex":
                InstallCodexConfig(command, args, baseDir);
                break;
            case "opencode":
                InstallOpenCodeConfig(command, args, baseDir);
                break;
        }
    }

    internal static void Uninstall(string tool, string baseDir)
    {
        tool = tool.ToLowerInvariant();
        ValidateTool(tool);

        switch (tool)
        {
            case "claude-code":
                UninstallJsonConfig(Path.Combine(baseDir, ".mcp.json"), "mcpServers", useFullPath: true);
                break;
            case "cursor":
                UninstallJsonConfig(Path.Combine(baseDir, ".cursor", "mcp.json"), "mcpServers", useFullPath: true);
                break;
            case "copilot":
            case "vscode":
                UninstallJsonConfig(Path.Combine(baseDir, ".vscode", "mcp.json"), "servers", useFullPath: true);
                break;
            case "codex":
                UninstallCodexConfig(baseDir);
                break;
            case "opencode":
                UninstallOpenCodeConfig(baseDir);
                break;
        }
    }

    internal static void EnsureGitignore(string? baseDir = null)
    {
        var dir = baseDir ?? Directory.GetCurrentDirectory();
        var gitignorePath = Path.Combine(dir, ".gitignore");

        if (File.Exists(gitignorePath))
        {
            var content = File.ReadAllText(gitignorePath);
            // Check if .sextant is already ignored (as a line by itself, with optional trailing slash)
            var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            if (lines.Any(l => l.TrimEnd('/') == ".sextant"))
                return;

            // Append to existing file
            var needsNewline = content.Length > 0 && !content.EndsWith('\n');
            using var writer = File.AppendText(gitignorePath);
            if (needsNewline)
                writer.WriteLine();
            writer.WriteLine(".sextant/");
        }
        else
        {
            File.WriteAllText(gitignorePath, ".sextant/\n");
        }

        Console.WriteLine($"Added .sextant/ to {gitignorePath}");
    }

    private const string ClaudeMdSectionMarker = "<!-- sextant:begin -->";
    private const string ClaudeMdSectionEnd = "<!-- sextant:end -->";

    private static readonly string ClaudeMdSection = $"""
        {ClaudeMdSectionMarker}
        ## Sextant Semantic Index

        This project has a Sextant semantic index. **Always prefer `mcp__sextant__*` tools** for .NET codebase exploration before using Explore agents, Grep, or manual file reading. These tools query a pre-built Roslyn-based index and return results instantly.

        **Start here:**
        - `get_index_status` — See what's indexed and how fresh the data is
        - `find_symbol` / `semantic_search` — Find symbols by name or text query
        - `research_codebase` — Ask natural language questions about the codebase

        **Explore structure:**
        - `get_file_symbols` — Understand a file before reading it
        - `get_type_members` — List type members with signatures
        - `get_type_hierarchy` / `get_implementors` — Inheritance and interface implementations
        - `get_project_dependencies` — Project dependency graph
        - `get_api_surface` — Public API surface of a project

        **Trace usage:**
        - `find_references` — All usages of a symbol across the solution
        - `get_call_hierarchy` — Method call chains (callers/callees)
        - `get_impact` — Assess change impact before refactoring
        {ClaudeMdSectionEnd}
        """.Replace("        ", ""); // Remove leading indentation from raw string literal

    internal static void EnsureClaudeMdSection(string? baseDir = null)
    {
        var dir = baseDir ?? Directory.GetCurrentDirectory();
        var claudeMdPath = Path.Combine(dir, "CLAUDE.md");

        if (File.Exists(claudeMdPath))
        {
            var content = File.ReadAllText(claudeMdPath);

            // Already has a sextant section — replace it
            if (content.Contains(ClaudeMdSectionMarker))
            {
                var startIdx = content.IndexOf(ClaudeMdSectionMarker, StringComparison.Ordinal);
                var endIdx = content.IndexOf(ClaudeMdSectionEnd, StringComparison.Ordinal);
                if (endIdx > startIdx)
                {
                    endIdx += ClaudeMdSectionEnd.Length;
                    // Consume trailing newline if present
                    if (endIdx < content.Length && content[endIdx] == '\n')
                        endIdx++;
                    content = content[..startIdx] + ClaudeMdSection + content[endIdx..];
                    File.WriteAllText(claudeMdPath, content);
                    Console.WriteLine($"Updated sextant section in {claudeMdPath}");
                }
                return;
            }

            // Append to existing file
            var needsNewline = content.Length > 0 && !content.EndsWith('\n');
            using var writer = File.AppendText(claudeMdPath);
            if (needsNewline)
                writer.WriteLine();
            writer.WriteLine();
            writer.Write(ClaudeMdSection);
            Console.WriteLine($"Added sextant section to {claudeMdPath}");
        }
        else
        {
            File.WriteAllText(claudeMdPath, ClaudeMdSection);
            Console.WriteLine($"Created {claudeMdPath} with sextant section");
        }
    }

    private const string AgentTemplate = """
        ---
        description: "Use this agent for .NET codebase exploration, symbol lookup, dependency analysis, and architectural questions. Routes queries through the Sextant semantic index for instant, accurate results. Prefer this over the Explore agent for .NET projects with a Sextant index."
        tools:
          - mcp__sextant__find_symbol
          - mcp__sextant__semantic_search
          - mcp__sextant__find_references
          - mcp__sextant__get_call_hierarchy
          - mcp__sextant__get_type_hierarchy
          - mcp__sextant__get_type_members
          - mcp__sextant__get_implementors
          - mcp__sextant__get_file_symbols
          - mcp__sextant__get_project_dependencies
          - mcp__sextant__get_api_surface
          - mcp__sextant__get_impact
          - mcp__sextant__get_index_status
          - mcp__sextant__research_codebase
          - Read
          - Glob
          - Grep
        ---
        You are a codebase researcher powered by the Sextant semantic index.

        ALWAYS use sextant MCP tools (mcp__sextant__*) as your primary means of exploration.
        Only fall back to Read/Glob/Grep when sextant tools don't cover the query.

        Workflow:
        1. Start with `get_index_status` to understand what's indexed
        2. Use `find_symbol` or `semantic_search` for discovery
        3. Use `find_references`, `get_call_hierarchy`, `get_type_hierarchy`, `get_implementors` for tracing relationships
        4. Use `get_file_symbols` or `get_type_members` to understand structure
        5. Use `get_impact` before suggesting refactoring
        6. Use `Read` only when you need to see actual source code that sextant tools don't provide

        Return concise, structured answers. Include file paths and line numbers for all referenced symbols.
        """;

    private const string SkillTemplate = """
        ---
        description: "Research the .NET codebase using the Sextant semantic index. Use for architecture questions, finding symbols, tracing dependencies, and understanding code structure."
        ---
        # Sextant Research

        The user wants to explore or ask a question about the codebase using Sextant.

        Use the `mcp__sextant__research_codebase` tool with the user's question. If the user provided a specific scope (file, project, or solution), pass it via the `scope` parameter.

        If the question is simple and direct (e.g., "find usages of X", "what implements Y"), use the specific sextant tool directly instead of research_codebase:
        - Symbol lookup → `mcp__sextant__find_symbol` or `mcp__sextant__semantic_search`
        - References → `mcp__sextant__find_references`
        - Call chains → `mcp__sextant__get_call_hierarchy`
        - Inheritance → `mcp__sextant__get_type_hierarchy`
        - Implementations → `mcp__sextant__get_implementors`
        - Type members → `mcp__sextant__get_type_members`
        - File overview → `mcp__sextant__get_file_symbols`
        - Dependencies → `mcp__sextant__get_project_dependencies`
        - Public API → `mcp__sextant__get_api_surface`
        - Change impact → `mcp__sextant__get_impact`

        Present results concisely with file paths and line numbers.
        """;

    internal static void EnsureAgentAndSkill(string? baseDir = null)
    {
        var dir = baseDir ?? Directory.GetCurrentDirectory();

        // Install agent
        var agentDir = Path.Combine(dir, ".claude", "agents");
        Directory.CreateDirectory(agentDir);
        var agentPath = Path.Combine(agentDir, "sextant-researcher.md");
        var agentContent = AgentTemplate.Replace("        ", ""); // Remove raw string literal indentation
        File.WriteAllText(agentPath, agentContent);
        Console.WriteLine($"Installed sextant-researcher agent to {agentPath}");

        // Install skill
        var skillDir = Path.Combine(dir, ".claude", "skills", "sextant");
        Directory.CreateDirectory(skillDir);
        var skillPath = Path.Combine(skillDir, "SKILL.md");
        var skillContent = SkillTemplate.Replace("        ", ""); // Remove raw string literal indentation
        File.WriteAllText(skillPath, skillContent);
        Console.WriteLine($"Installed /sextant skill to {skillPath}");
    }

    private static void ValidateTool(string tool)
    {
        if (!SupportedTools.Contains(tool))
            throw new ArgumentException(
                $"Unknown tool: '{tool}'. Supported tools: {string.Join(", ", SupportedTools)}");
    }

    internal static bool IsGlobalTool()
    {
        // When installed as a .NET global tool, the assembly runs from
        // ~/.dotnet/tools/.store/... and no Sextant.Cli.csproj exists nearby.
        // When running from source via "dotnet run", the csproj is findable.
        var assemblyDir = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(assemblyDir);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Sextant.Cli.csproj")))
                return false;
            var srcCli = Path.Combine(dir.FullName, "src", "Sextant.Cli");
            if (Directory.Exists(srcCli) && File.Exists(Path.Combine(srcCli, "Sextant.Cli.csproj")))
                return false;
            dir = dir.Parent;
        }
        return true;
    }

    internal static string FindCliProjectPath()
    {
        var assemblyDir = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(assemblyDir);
        while (dir != null)
        {
            var csproj = Path.Combine(dir.FullName, "Sextant.Cli.csproj");
            if (File.Exists(csproj))
                return dir.FullName;

            var srcCli = Path.Combine(dir.FullName, "src", "Sextant.Cli");
            if (Directory.Exists(srcCli) && File.Exists(Path.Combine(srcCli, "Sextant.Cli.csproj")))
                return srcCli;

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate Sextant.Cli project directory. Ensure you are running from within the Sextant repository.");
    }

    private static (string command, JsonArray args) BuildMcpServerConfig(string? dbPath, string? profile)
    {
        if (IsGlobalTool())
        {
            // Installed as a global tool: use "sextant serve --stdio"
            var args = new JsonArray { "serve", "--stdio" };
            AppendDbProfileArgs(args, dbPath, profile);
            return ("sextant", args);
        }
        else
        {
            // Running from source: use "dotnet run --project <path> -- serve --stdio"
            var cliProjectPath = FindCliProjectPath();
            var args = BuildDotnetRunArgs(cliProjectPath, dbPath, profile);
            return ("dotnet", args);
        }
    }

    private static JsonArray BuildDotnetRunArgs(string cliProjectPath, string? dbPath, string? profile)
    {
        var args = new JsonArray
        {
            "run",
            "--project",
            cliProjectPath,
            "--",
            "serve",
            "--stdio"
        };

        AppendDbProfileArgs(args, dbPath, profile);
        return args;
    }

    private static void AppendDbProfileArgs(JsonArray args, string? dbPath, string? profile)
    {
        if (dbPath != null)
        {
            args.Add("--db");
            args.Add(dbPath);
        }

        if (profile != null)
        {
            args.Add("--profile");
            args.Add(profile);
        }
    }

    private static void InstallJsonConfig(string configPath, string rootKey, string command, JsonArray args, bool useFullPath = false)
    {
        var fullPath = useFullPath ? configPath : Path.Combine(Directory.GetCurrentDirectory(), configPath);

        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        JsonNode root;
        if (File.Exists(fullPath))
        {
            var existing = File.ReadAllText(fullPath);
            root = JsonNode.Parse(existing) ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        var serverEntry = new JsonObject
        {
            ["command"] = command,
            ["args"] = args
        };

        if (root[rootKey] is not JsonObject servers)
        {
            servers = new JsonObject();
            root[rootKey] = servers;
        }

        servers["sextant"] = serverEntry;

        File.WriteAllText(fullPath, root.ToJsonString(WriteOptions));
        Console.WriteLine($"Installed sextant MCP server to {fullPath}");
    }

    private static void UninstallJsonConfig(string configPath, string rootKey, bool useFullPath = false)
    {
        var fullPath = useFullPath ? configPath : Path.Combine(Directory.GetCurrentDirectory(), configPath);

        if (!File.Exists(fullPath))
        {
            Console.WriteLine($"Config file not found: {fullPath}");
            return;
        }

        var existing = File.ReadAllText(fullPath);
        var root = JsonNode.Parse(existing);
        if (root?[rootKey] is JsonObject servers)
        {
            servers.Remove("sextant");
            File.WriteAllText(fullPath, root.ToJsonString(WriteOptions));
            Console.WriteLine($"Removed sextant from {fullPath}");
        }
        else
        {
            Console.WriteLine($"No sextant entry found in {fullPath}");
        }
    }

    private static void InstallCodexConfig(string command, JsonArray args, string? baseDir = null)
    {
        var configPath = baseDir != null
            ? Path.Combine(baseDir, ".codex", "config.toml")
            : Path.Combine(Directory.GetCurrentDirectory(), ".codex", "config.toml");

        var dir = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var argsStrings = new List<string>();
        foreach (var arg in args)
            argsStrings.Add(arg!.GetValue<string>());

        var section = BuildCodexSection(command, argsStrings);

        if (File.Exists(configPath))
        {
            var content = File.ReadAllText(configPath);
            content = RemoveCodexSection(content);
            content = content.TrimEnd() + "\n\n" + section + "\n";
            File.WriteAllText(configPath, content);
        }
        else
        {
            File.WriteAllText(configPath, section + "\n");
        }

        Console.WriteLine($"Installed sextant MCP server to {configPath}");
    }

    private static void UninstallCodexConfig(string? baseDir = null)
    {
        var configPath = baseDir != null
            ? Path.Combine(baseDir, ".codex", "config.toml")
            : Path.Combine(Directory.GetCurrentDirectory(), ".codex", "config.toml");

        if (!File.Exists(configPath))
        {
            Console.WriteLine($"Config file not found: {configPath}");
            return;
        }

        var content = File.ReadAllText(configPath);
        var updated = RemoveCodexSection(content);
        File.WriteAllText(configPath, updated.TrimEnd() + "\n");
        Console.WriteLine($"Removed sextant from {configPath}");
    }

    private static string BuildCodexSection(string command, List<string> argsStrings)
    {
        var quotedArgs = string.Join(", ", argsStrings.Select(a => $"\"{a}\""));
        return $"[mcp_servers.sextant]\ncommand = \"{command}\"\nargs = [{quotedArgs}]";
    }

    private static string RemoveCodexSection(string content)
    {
        var lines = content.Split('\n').ToList();
        var startIdx = -1;
        var endIdx = -1;

        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].TrimStart().StartsWith("[mcp_servers.sextant]"))
            {
                startIdx = i;
                for (var j = i + 1; j < lines.Count; j++)
                {
                    if (lines[j].TrimStart().StartsWith("[") && !lines[j].TrimStart().StartsWith("[mcp_servers.sextant]"))
                    {
                        endIdx = j;
                        break;
                    }
                }
                endIdx = endIdx == -1 ? lines.Count : endIdx;
                break;
            }
        }

        if (startIdx == -1)
            return content;

        if (startIdx > 0 && string.IsNullOrWhiteSpace(lines[startIdx - 1]))
            startIdx--;

        lines.RemoveRange(startIdx, endIdx - startIdx);
        return string.Join("\n", lines);
    }

    private static void InstallOpenCodeConfig(string command, JsonArray args, string? baseDir = null)
    {
        var configPath = baseDir != null
            ? Path.Combine(baseDir, "opencode.json")
            : Path.Combine(Directory.GetCurrentDirectory(), "opencode.json");

        JsonNode root;
        if (File.Exists(configPath))
        {
            var existing = File.ReadAllText(configPath);
            root = JsonNode.Parse(existing) ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        // OpenCode uses command: ["command", "arg1", "arg2", ...] (array format)
        var cmdArray = new JsonArray { command };
        foreach (var arg in args)
            cmdArray.Add(arg!.GetValue<string>());

        var serverEntry = new JsonObject
        {
            ["type"] = "stdio",
            ["command"] = cmdArray
        };

        if (root["mcp"] is not JsonObject mcpNode)
        {
            mcpNode = new JsonObject();
            root["mcp"] = mcpNode;
        }

        if (mcpNode["mcpServers"] is not JsonObject mcpServers)
        {
            mcpServers = new JsonObject();
            mcpNode["mcpServers"] = mcpServers;
        }

        mcpServers["sextant"] = serverEntry;

        File.WriteAllText(configPath, root.ToJsonString(WriteOptions));
        Console.WriteLine($"Installed sextant MCP server to {configPath}");
    }

    private static void UninstallOpenCodeConfig(string? baseDir = null)
    {
        var configPath = baseDir != null
            ? Path.Combine(baseDir, "opencode.json")
            : Path.Combine(Directory.GetCurrentDirectory(), "opencode.json");

        if (!File.Exists(configPath))
        {
            Console.WriteLine($"Config file not found: {configPath}");
            return;
        }

        var existing = File.ReadAllText(configPath);
        var root = JsonNode.Parse(existing);
        if (root?["mcp"]?["mcpServers"] is JsonObject mcpServers)
        {
            mcpServers.Remove("sextant");
            File.WriteAllText(configPath, root.ToJsonString(WriteOptions));
            Console.WriteLine($"Removed sextant from {configPath}");
        }
        else
        {
            Console.WriteLine($"No sextant entry found in {configPath}");
        }
    }
}
