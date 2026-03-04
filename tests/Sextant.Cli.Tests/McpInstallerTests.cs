using System.Text.Json;
using System.Text.Json.Nodes;
using Sextant.Cli.Handlers;

namespace Sextant.Cli.Tests;

[TestClass]
public class McpInstallerTests
{
    private string _tempDir = null!;
    private string _fakeCliPath = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "sextant-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _fakeCliPath = "/fake/path/to/Sextant.Cli";
    }

    [TestCleanup]
    public void TestCleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public void Install_ClaudeCode_CreatesValidMcpJson()
    {
        McpInstaller.Install("claude-code", _tempDir, cliProjectPath: _fakeCliPath);

        var configPath = Path.Combine(_tempDir, ".mcp.json");
        Assert.IsTrue(File.Exists(configPath));

        var root = JsonNode.Parse(File.ReadAllText(configPath))!;
        var server = root["mcpServers"]!["sextant"]!;
        Assert.AreEqual("dotnet", server["command"]!.GetValue<string>());

        var args = server["args"]!.AsArray();
        Assert.IsTrue(args.Select(a => a!.GetValue<string>()).Contains("run"));
        Assert.IsTrue(args.Select(a => a!.GetValue<string>()).Contains("--project"));
        Assert.IsTrue(args.Select(a => a!.GetValue<string>()).Contains("serve"));
        Assert.IsTrue(args.Select(a => a!.GetValue<string>()).Contains("--stdio"));
    }

    [TestMethod]
    public void Install_ClaudeCode_MergesWithExistingConfig()
    {
        var configPath = Path.Combine(_tempDir, ".mcp.json");
        var existing = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["other-server"] = new JsonObject
                {
                    ["command"] = "node",
                    ["args"] = new JsonArray("server.js")
                }
            }
        };
        File.WriteAllText(configPath, existing.ToJsonString());

        McpInstaller.Install("claude-code", _tempDir, cliProjectPath: _fakeCliPath);

        var root = JsonNode.Parse(File.ReadAllText(configPath))!;
        var servers = root["mcpServers"]!.AsObject();
        Assert.IsTrue(servers.ContainsKey("sextant"));
        Assert.IsTrue(servers.ContainsKey("other-server"));
        Assert.AreEqual("node", servers["other-server"]!["command"]!.GetValue<string>());
    }

    [TestMethod]
    public void Install_Cursor_CreatesConfigInDotCursorDir()
    {
        McpInstaller.Install("cursor", _tempDir, cliProjectPath: _fakeCliPath);

        var configPath = Path.Combine(_tempDir, ".cursor", "mcp.json");
        Assert.IsTrue(File.Exists(configPath));

        var root = JsonNode.Parse(File.ReadAllText(configPath))!;
        Assert.IsNotNull(root["mcpServers"]!["sextant"]);
    }

    [TestMethod]
    public void Install_VsCode_UsesServersKey()
    {
        McpInstaller.Install("vscode", _tempDir, cliProjectPath: _fakeCliPath);

        var configPath = Path.Combine(_tempDir, ".vscode", "mcp.json");
        Assert.IsTrue(File.Exists(configPath));

        var root = JsonNode.Parse(File.ReadAllText(configPath))!;
        Assert.IsNull(root["mcpServers"]);
        Assert.IsNotNull(root["servers"]!["sextant"]);
    }

    [TestMethod]
    public void Install_Codex_WritesToml()
    {
        McpInstaller.Install("codex", _tempDir, cliProjectPath: _fakeCliPath);

        var configPath = Path.Combine(_tempDir, ".codex", "config.toml");
        Assert.IsTrue(File.Exists(configPath));

        var content = File.ReadAllText(configPath);
        StringAssert.Contains(content, "[mcp_servers.sextant]");
        StringAssert.Contains(content, "command = \"dotnet\"");
        StringAssert.Contains(content, "args = [");
        StringAssert.Contains(content, "\"serve\"");
        StringAssert.Contains(content, "\"--stdio\"");
    }

    [TestMethod]
    public void Install_OpenCode_WritesNestedMcpConfig()
    {
        McpInstaller.Install("opencode", _tempDir, cliProjectPath: _fakeCliPath);

        var configPath = Path.Combine(_tempDir, "opencode.json");
        Assert.IsTrue(File.Exists(configPath));

        var root = JsonNode.Parse(File.ReadAllText(configPath))!;
        var server = root["mcp"]!["mcpServers"]!["sextant"]!;
        Assert.AreEqual("stdio", server["type"]!.GetValue<string>());
        Assert.IsNotNull(server["command"]);
        var cmd = server["command"]!.AsArray();
        Assert.AreEqual("dotnet", cmd[0]!.GetValue<string>());
    }

    [TestMethod]
    public void Uninstall_RemovesServerEntry()
    {
        // Install first
        McpInstaller.Install("claude-code", _tempDir, cliProjectPath: _fakeCliPath);

        // Add another server to verify it survives
        var configPath = Path.Combine(_tempDir, ".mcp.json");
        var root = JsonNode.Parse(File.ReadAllText(configPath))!;
        root["mcpServers"]!.AsObject()["other"] = new JsonObject { ["command"] = "test" };
        File.WriteAllText(configPath, root.ToJsonString());

        // Uninstall
        McpInstaller.Uninstall("claude-code", _tempDir);

        var updated = JsonNode.Parse(File.ReadAllText(configPath))!;
        var servers = updated["mcpServers"]!.AsObject();
        Assert.IsFalse(servers.ContainsKey("sextant"));
        Assert.IsTrue(servers.ContainsKey("other"));
    }

    [TestMethod]
    public void Install_ClaudeCode_CreatesClaudeMdSection()
    {
        McpInstaller.Install("claude-code", _tempDir, cliProjectPath: _fakeCliPath);

        var claudeMdPath = Path.Combine(_tempDir, "CLAUDE.md");
        Assert.IsTrue(File.Exists(claudeMdPath));

        var content = File.ReadAllText(claudeMdPath);
        StringAssert.Contains(content, "<!-- sextant:begin -->");
        StringAssert.Contains(content, "<!-- sextant:end -->");
        StringAssert.Contains(content, "Sextant Semantic Index");
        StringAssert.Contains(content, "find_symbol");
        StringAssert.Contains(content, "research_codebase");
    }

    [TestMethod]
    public void Install_ClaudeCode_AppendsToExistingClaudeMd()
    {
        var claudeMdPath = Path.Combine(_tempDir, "CLAUDE.md");
        File.WriteAllText(claudeMdPath, "# My Project\n\nExisting instructions.\n");

        McpInstaller.Install("claude-code", _tempDir, cliProjectPath: _fakeCliPath);

        var content = File.ReadAllText(claudeMdPath);
        StringAssert.StartsWith(content, "# My Project");
        StringAssert.Contains(content, "Existing instructions.");
        StringAssert.Contains(content, "<!-- sextant:begin -->");
    }

    [TestMethod]
    public void Install_ClaudeCode_UpdatesExistingSextantSection()
    {
        var claudeMdPath = Path.Combine(_tempDir, "CLAUDE.md");
        File.WriteAllText(claudeMdPath, "# My Project\n\n<!-- sextant:begin -->\nold content\n<!-- sextant:end -->\n\n# Other Section\n");

        McpInstaller.Install("claude-code", _tempDir, cliProjectPath: _fakeCliPath);

        var content = File.ReadAllText(claudeMdPath);
        StringAssert.Contains(content, "# My Project");
        StringAssert.Contains(content, "# Other Section");
        StringAssert.Contains(content, "Sextant Semantic Index");
        // Old content should be replaced
        Assert.IsFalse(content.Contains("old content"));
    }

    [TestMethod]
    public void Install_ClaudeCode_CreatesAgentFile()
    {
        McpInstaller.Install("claude-code", _tempDir, cliProjectPath: _fakeCliPath);

        var agentPath = Path.Combine(_tempDir, ".claude", "agents", "sextant-researcher.md");
        Assert.IsTrue(File.Exists(agentPath));

        var content = File.ReadAllText(agentPath);
        StringAssert.Contains(content, "description:");
        StringAssert.Contains(content, "mcp__sextant__find_symbol");
        StringAssert.Contains(content, "mcp__sextant__research_codebase");
        StringAssert.Contains(content, "Sextant semantic index");
    }

    [TestMethod]
    public void Install_ClaudeCode_CreatesSkillFile()
    {
        McpInstaller.Install("claude-code", _tempDir, cliProjectPath: _fakeCliPath);

        var skillPath = Path.Combine(_tempDir, ".claude", "skills", "sextant", "SKILL.md");
        Assert.IsTrue(File.Exists(skillPath));

        var content = File.ReadAllText(skillPath);
        StringAssert.Contains(content, "description:");
        StringAssert.Contains(content, "mcp__sextant__research_codebase");
    }

    [TestMethod]
    public void Install_ClaudeCode_AgentOverwritesOnReinstall()
    {
        McpInstaller.Install("claude-code", _tempDir, cliProjectPath: _fakeCliPath);

        var agentPath = Path.Combine(_tempDir, ".claude", "agents", "sextant-researcher.md");
        var firstContent = File.ReadAllText(agentPath);

        // Install again
        McpInstaller.Install("claude-code", _tempDir, cliProjectPath: _fakeCliPath);

        var secondContent = File.ReadAllText(agentPath);
        Assert.AreEqual(firstContent, secondContent);
    }

    [TestMethod]
    public void Install_Cursor_DoesNotCreateAgentOrSkill()
    {
        McpInstaller.Install("cursor", _tempDir, cliProjectPath: _fakeCliPath);

        Assert.IsFalse(Directory.Exists(Path.Combine(_tempDir, ".claude", "agents")));
        Assert.IsFalse(Directory.Exists(Path.Combine(_tempDir, ".claude", "skills", "sextant")));
    }

    [TestMethod]
    public void Install_Cursor_DoesNotCreateClaudeMd()
    {
        McpInstaller.Install("cursor", _tempDir, cliProjectPath: _fakeCliPath);

        var claudeMdPath = Path.Combine(_tempDir, "CLAUDE.md");
        Assert.IsFalse(File.Exists(claudeMdPath));
    }

    [TestMethod]
    public void Install_UnknownTool_Throws()
    {
        var ex = Assert.ThrowsExactly<ArgumentException>(() =>
            McpInstaller.Install("unknown-tool", _tempDir, cliProjectPath: _fakeCliPath));

        StringAssert.Contains(ex.Message, "Unknown tool");
        StringAssert.Contains(ex.Message, "claude-code");
        StringAssert.Contains(ex.Message, "cursor");
    }

    [TestMethod]
    public void Install_WithDbPath_IncludesDbArgs()
    {
        McpInstaller.Install("claude-code", _tempDir, dbPath: "/tmp/test.db", cliProjectPath: _fakeCliPath);

        var configPath = Path.Combine(_tempDir, ".mcp.json");
        var root = JsonNode.Parse(File.ReadAllText(configPath))!;
        var args = root["mcpServers"]!["sextant"]!["args"]!.AsArray()
            .Select(a => a!.GetValue<string>()).ToList();

        Assert.IsTrue(args.Contains("--db"));
        Assert.IsTrue(args.Contains("/tmp/test.db"));
    }
}
