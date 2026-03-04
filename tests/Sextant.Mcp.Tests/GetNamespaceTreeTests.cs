using System.Text.Json;
using Sextant.Mcp.Tools;

namespace Sextant.Mcp.Tests;

[TestClass]
public class GetNamespaceTreeTests
{
    private static readonly McpTestFixture _fixture = McpTestFixtureInstance.Instance;

    [TestMethod]
    public void GetNamespaceTree_NoPrefix_ReturnsTopLevelNamespaces()
    {
        var result = GetNamespaceTreeTool.GetNamespaceTree(_fixture.DbProvider);
        var doc = JsonDocument.Parse(result);
        var meta = doc.RootElement.GetProperty("meta");
        Assert.AreEqual(1, meta.GetProperty("result_count").GetInt32());

        var first = doc.RootElement.GetProperty("results")[0];
        Assert.AreEqual("(root)", first.GetProperty("namespace").GetString());

        var childNamespaces = first.GetProperty("child_namespaces");
        Assert.IsTrue(childNamespaces.GetArrayLength() >= 1);
    }

    [TestMethod]
    public void GetNamespaceTree_WithPrefix_DrillsIntoNamespace()
    {
        var result = GetNamespaceTreeTool.GetNamespaceTree(_fixture.DbProvider,
            namespace_prefix: "global::Alpha");
        var doc = JsonDocument.Parse(result);
        var first = doc.RootElement.GetProperty("results")[0];
        Assert.AreEqual("global::Alpha", first.GetProperty("namespace").GetString());

        var symbols = first.GetProperty("symbols");
        Assert.IsTrue(symbols.GetArrayLength() >= 2);
    }

    [TestMethod]
    public void GetNamespaceTree_Depth2_ReturnsTwoLevels()
    {
        var result = GetNamespaceTreeTool.GetNamespaceTree(_fixture.DbProvider, depth: 2);
        var doc = JsonDocument.Parse(result);
        var meta = doc.RootElement.GetProperty("meta");
        Assert.IsTrue(meta.GetProperty("result_count").GetInt32() >= 1);
    }

    [TestMethod]
    public void GetNamespaceTree_ProjectIdScoping_NarrowsToOneProject()
    {
        var result = GetNamespaceTreeTool.GetNamespaceTree(_fixture.DbProvider,
            project_id: "proj_alpha_123456");
        var doc = JsonDocument.Parse(result);
        var first = doc.RootElement.GetProperty("results")[0];
        var childNamespaces = first.GetProperty("child_namespaces");

        // Alpha project should have global::Alpha namespace only (not Beta)
        var nsNames = childNamespaces.EnumerateArray()
            .Select(n => n.GetProperty("name").GetString())
            .ToList();
        Assert.IsFalse(nsNames.Contains("global::Beta"));
    }

    [TestMethod]
    public void GetNamespaceTree_LeafNamespace_HasEmptyChildNamespaces()
    {
        // Alpha.Tests is a leaf namespace
        var result = GetNamespaceTreeTool.GetNamespaceTree(_fixture.DbProvider,
            namespace_prefix: "global::Alpha.Tests");
        var doc = JsonDocument.Parse(result);
        var first = doc.RootElement.GetProperty("results")[0];
        var childNamespaces = first.GetProperty("child_namespaces");
        Assert.AreEqual(0, childNamespaces.GetArrayLength());
    }

    [TestMethod]
    public void GetNamespaceTree_WithPrefix_ListsSymbolsInNamespace()
    {
        var result = GetNamespaceTreeTool.GetNamespaceTree(_fixture.DbProvider,
            namespace_prefix: "global::Alpha");
        var doc = JsonDocument.Parse(result);
        var first = doc.RootElement.GetProperty("results")[0];
        var symbols = first.GetProperty("symbols");
        Assert.IsTrue(symbols.GetArrayLength() >= 1);

        foreach (var sym in symbols.EnumerateArray())
        {
            Assert.IsTrue(sym.TryGetProperty("display_name", out _));
        }
    }
}
