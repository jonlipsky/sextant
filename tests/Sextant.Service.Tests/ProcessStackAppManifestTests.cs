using System.Reflection;
using ModelContextProtocol.Server;
using Sextant.Mcp.Tools;
using Sextant.Service.Host;

namespace Sextant.Service.Tests;

/// <summary>
/// The Sextant ProcessStack app (<c>apps/processstack/sextant/psapp.yaml</c>) re-exposes the service's query
/// tools through <c>mcp.connectionTools</c> over the <c>sextant-query</c> connection, forwarded verbatim with
/// the caller's signed identity. Its <c>include</c> list must be exactly the service's remote allowlist
/// (<see cref="ServiceApp.RemoteQueryTools"/>) minus <c>research_codebase</c>, which calls an LLM on the
/// service's side: a tool added to or dropped from the service surface fails here until the app is triaged.
/// The app builds against a private SDK, so this reads the manifest as text (a narrow line parser for the
/// fixed shape the app uses) rather than referencing the app.
/// </summary>
[TestClass]
public class ProcessStackAppManifestTests
{
    private const string QueryConnection = "sextant-query";
    private static readonly string[] LeftOutOfTheApp = ["research_codebase"];

    [TestMethod]
    public void ConnectionTools_AreTheRemoteQueryToolsMinusResearchCodebase()
    {
        var manifest = Manifest.Load();
        var entry = SingleConnectionToolsEntry(manifest);

        CollectionAssert.AllItemsAreUnique(entry.Include, "connectionTools.include lists each tool once");
        var expected = RemoteToolNames().Except(LeftOutOfTheApp, StringComparer.Ordinal).ToList();
        CollectionAssert.AreEquivalent(expected, entry.Include,
            "connectionTools.include must be exactly the service's remote query tools minus research_codebase");
    }

    [TestMethod]
    public void ConnectionTools_NeverExposeLocalOnlyOrLlmTools()
    {
        var entry = SingleConnectionToolsEntry(Manifest.Load());
        var forbidden = ToolNames(typeof(GetSourceContextTool), typeof(GetDaemonStatusTool), typeof(GetBaseSnapshotSymbolsTool))
            .Concat(LeftOutOfTheApp)
            .ToList();
        Assert.AreEqual(4, forbidden.Count, "every forbidden tool type declares a name");

        var exposed = entry.Include.Intersect(forbidden, StringComparer.Ordinal).ToList();
        Assert.IsEmpty(exposed, $"the app must not re-expose {string.Join(", ", exposed)}");
    }

    [TestMethod]
    public void ConnectionTools_UseTheQueryConnectionUnprefixed()
    {
        var manifest = Manifest.Load();
        var entry = SingleConnectionToolsEntry(manifest);

        Assert.AreEqual(QueryConnection, entry.Connection);
        Assert.AreEqual("\"\"", entry.Prefix, "the tools keep the service's own names (an empty prefix)");
        Assert.IsTrue(int.TryParse(entry.TimeoutSeconds, out var timeout) && timeout is >= 1 and <= 600,
            $"timeoutSeconds must be 1-600, got '{entry.TimeoutSeconds}'");
        Assert.IsFalse(string.IsNullOrWhiteSpace(entry.Description), "the connectionTools entry has a description");

        Assert.IsTrue(manifest.Connections.TryGetValue(QueryConnection, out var connection),
            $"psapp.yaml declares the {QueryConnection} connection");
        Assert.AreEqual("mcp", connection.Type, $"{QueryConnection} is an mcp connection");
        Assert.IsFalse(connection.HasConfig,
            $"{QueryConnection} carries no config: the endpoint and bearer are entered when the app is installed");
    }

    [TestMethod]
    public void ConnectionTools_DoNotCollideWithTheAppsOwnTools()
    {
        var manifest = Manifest.Load();
        var entry = SingleConnectionToolsEntry(manifest);

        Assert.IsNotEmpty(manifest.McpInclude, "the app exposes its own processes as MCP tools");
        var clashes = entry.Include
            .Intersect(manifest.McpInclude.Concat(manifest.Entrypoints), StringComparer.Ordinal)
            .ToList();
        Assert.IsEmpty(clashes, $"a query tool name must not shadow an app entrypoint: {string.Join(", ", clashes)}");
    }

    private static ConnectionToolsEntry SingleConnectionToolsEntry(Manifest manifest)
    {
        Assert.AreEqual(1, manifest.ConnectionTools.Count, "the app re-exposes one connection's tools");
        return manifest.ConnectionTools[0];
    }

    private static List<string> RemoteToolNames() => ToolNames([.. ServiceApp.RemoteQueryTools]);

    private static List<string> ToolNames(params Type[] toolTypes) =>
        [.. toolTypes
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .OfType<string>()];

    private sealed record ConnectionToolsEntry(
        string? Connection, string? Prefix, string? TimeoutSeconds, string? Description, List<string> Include);

    private sealed record Connection(string? Type, bool HasConfig);

    /// <summary>
    /// The parts of psapp.yaml this test pins, read line by line. It understands only the block-style shape the
    /// manifest uses (two-space indents, <c>- key: value</c> list items, no flow collections or anchors) and
    /// fails with a clear message when that shape changes rather than guessing.
    /// </summary>
    private sealed class Manifest
    {
        public List<string> McpInclude { get; } = [];
        public List<ConnectionToolsEntry> ConnectionTools { get; } = [];
        public List<string> Entrypoints { get; } = [];
        public Dictionary<string, Connection> Connections { get; } = new(StringComparer.Ordinal);

        public static Manifest Load()
        {
            var path = Path.Combine(RepositoryRoot(), "apps", "processstack", "sextant", "psapp.yaml");
            Assert.IsTrue(File.Exists(path), $"the app manifest exists at {path}");
            var lines = File.ReadAllLines(path)
                .Select(StripComment)
                .Where(l => l.Trim().Length > 0)
                .ToList();

            var manifest = new Manifest();
            manifest.ReadMcp(Block(lines, "mcp"));
            manifest.ReadEntrypoints(Block(lines, "entrypoints"));
            manifest.ReadConnections(Block(lines, "connections"));
            return manifest;
        }

        private void ReadMcp(List<string> block)
        {
            McpInclude.AddRange(Items(Block(block, "include", indent: 2), indent: 4));

            var tools = Block(block, "connectionTools", indent: 2);
            foreach (var item in ListItems(tools, indent: 4))
            {
                var fields = Fields(item, indent: 6);
                ConnectionTools.Add(new ConnectionToolsEntry(
                    fields.GetValueOrDefault("connection"),
                    fields.GetValueOrDefault("prefix"),
                    fields.GetValueOrDefault("timeoutSeconds"),
                    fields.GetValueOrDefault("description"),
                    Items(Block(item, "include", indent: 6), indent: 8)));
            }
        }

        private void ReadEntrypoints(List<string> block)
        {
            foreach (var item in ListItems(block, indent: 2))
            {
                var name = Fields(item, indent: 4).GetValueOrDefault("name");
                Assert.IsFalse(string.IsNullOrEmpty(name), "every entrypoint has a name");
                Entrypoints.Add(name);
            }
        }

        private void ReadConnections(List<string> block)
        {
            foreach (var item in ListItems(block, indent: 2))
            {
                var fields = Fields(item, indent: 4);
                var id = fields.GetValueOrDefault("id");
                Assert.IsFalse(string.IsNullOrEmpty(id), "every connection has an id");
                Connections[id] = new Connection(fields.GetValueOrDefault("type"), fields.ContainsKey("config"));
            }
        }

        /// <summary>The lines nested under <c>key:</c> at <paramref name="indent"/>, or none when it is absent.</summary>
        private static List<string> Block(List<string> lines, string key, int indent = 0)
        {
            var header = new string(' ', indent) + key + ":";
            var start = lines.FindIndex(l => l.TrimEnd() == header);
            if (start < 0)
                return [];
            return [.. lines.Skip(start + 1).TakeWhile(l => IndentOf(l) > indent)];
        }

        /// <summary>Each <c>- …</c> item at <paramref name="indent"/>, rewritten so its first key sits with the rest.</summary>
        private static List<List<string>> ListItems(List<string> lines, int indent)
        {
            var dash = new string(' ', indent) + "- ";
            var items = new List<List<string>>();
            foreach (var line in lines)
            {
                if (line.StartsWith(dash, StringComparison.Ordinal))
                    items.Add([new string(' ', indent + 2) + line[dash.Length..]]);
                else
                {
                    Assert.IsNotEmpty(items, $"unexpected line before the first list item: '{line}'");
                    items[^1].Add(line);
                }
            }
            return items;
        }

        /// <summary>The scalar items of a <c>- value</c> list at <paramref name="indent"/>.</summary>
        private static List<string> Items(List<string> lines, int indent)
        {
            var dash = new string(' ', indent) + "- ";
            return [.. lines.Select(l =>
            {
                Assert.IsTrue(l.StartsWith(dash, StringComparison.Ordinal), $"expected a '- name' item, got '{l}'");
                return l[dash.Length..].Trim();
            })];
        }

        /// <summary>The <c>key: value</c> pairs at <paramref name="indent"/> (a nested block's value is empty).</summary>
        private static Dictionary<string, string> Fields(List<string> lines, int indent)
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in lines.Where(l => IndentOf(l) == indent))
            {
                var colon = line.IndexOf(':', StringComparison.Ordinal);
                Assert.IsTrue(colon > 0, $"expected a 'key: value' line, got '{line}'");
                var key = line[..colon].Trim();
                Assert.IsTrue(fields.TryAdd(key, line[(colon + 1)..].Trim()), $"duplicate key '{key}'");
            }
            return fields;
        }

        // Only whole-line comments and ' #' comments outside quotes: enough for the manifest's plain scalars.
        private static string StripComment(string line)
        {
            if (line.TrimStart().StartsWith('#'))
                return "";
            var quoted = false;
            for (var i = 0; i < line.Length; i++)
            {
                if (line[i] == '"')
                    quoted = !quoted;
                else if (!quoted && line[i] == '#' && i > 0 && line[i - 1] == ' ')
                    return line[..i].TrimEnd();
            }
            return line.TrimEnd();
        }

        private static int IndentOf(string line) => line.Length - line.TrimStart(' ').Length;
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Sextant.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException("Could not find Sextant.slnx above the test output directory.");
    }
}
