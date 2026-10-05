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
        Assert.AreEqual(1, doc.RootElement.GetProperty("results").GetArrayLength());

        var first = doc.RootElement.GetProperty("results")[0];
        Assert.AreEqual("(root)", first.GetProperty("namespace").GetString());

        var childNamespaces = first.GetProperty("child_namespaces");
        Assert.IsTrue(childNamespaces.GetArrayLength() >= 1);
        // result_count counts the listed entries (child namespaces and types), like every other wrapped result.
        Assert.AreEqual(childNamespaces.GetArrayLength() + first.GetProperty("symbols").GetArrayLength(),
            meta.GetProperty("result_count").GetInt32());
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
        // Beta is a leaf namespace (it declares Consumer and has no sub-namespace)
        var result = GetNamespaceTreeTool.GetNamespaceTree(_fixture.DbProvider,
            namespace_prefix: "global::Beta");
        var doc = JsonDocument.Parse(result);
        var first = doc.RootElement.GetProperty("results")[0];
        var childNamespaces = first.GetProperty("child_namespaces");
        Assert.AreEqual(0, childNamespaces.GetArrayLength());
    }

    [TestMethod]
    public void GetNamespaceTree_PrefixWithoutGlobalAlias_DrillsIntoNamespace()
    {
        var result = GetNamespaceTreeTool.GetNamespaceTree(_fixture.DbProvider, namespace_prefix: "Alpha");
        var first = JsonDocument.Parse(result).RootElement.GetProperty("results")[0];
        Assert.AreEqual("global::Alpha", first.GetProperty("namespace").GetString());
        Assert.IsTrue(first.GetProperty("symbols").GetArrayLength() >= 2);
    }

    [TestMethod]
    public void GetNamespaceTree_UnknownNamespace_IsAnErrorNamingTheClosest()
    {
        // A namespace that declares no indexed type is an error, never an empty tree an agent reads as "no types".
        var result = GetNamespaceTreeTool.GetNamespaceTree(_fixture.DbProvider, namespace_prefix: "Alfa.Beta");
        var meta = JsonDocument.Parse(result).RootElement.GetProperty("meta");
        Assert.AreEqual("invalid_argument", meta.GetProperty("error").GetProperty("code").GetString());
        StringAssert.Contains(meta.GetProperty("error").GetProperty("message").GetString()!, "global::Beta");
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
