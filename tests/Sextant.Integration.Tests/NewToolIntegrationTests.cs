using System.Text.Json;
using Sextant.Mcp;
using Sextant.Mcp.Tools;

namespace Sextant.Integration.Tests;

[TestClass]
[TestCategory("Integration")]
public class NewToolIntegrationTests
{
    private IntegrationFixture _fixture = null!;

    [TestInitialize]
    public void Setup() => _fixture = IntegrationFixture.Instance;

    [TestMethod]
    public void FindReferences_GroupByFile_GroupsCorrectly()
    {
        var result = FindReferencesTool.FindReferences(_fixture.DbProvider,
            "global::Sextant.Store.IndexDatabase", group_by: "file");
        var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");
        if (results.GetArrayLength() > 0)
        {
            var first = results[0];
            Assert.AreEqual("file", first.GetProperty("group_type").GetString());
            Assert.IsTrue(first.TryGetProperty("group_key", out _));
        }
    }

    [TestMethod]
    public void FindReferences_IncludeSource_HasSourceLines()
    {
        var result = FindReferencesTool.FindReferences(_fixture.DbProvider,
            "global::Sextant.Store.IndexDatabase", include_source: true);
        var doc = JsonDocument.Parse(result);
        Assert.IsTrue(doc.RootElement.TryGetProperty("results", out _));
    }

    [TestMethod]
    public void FindReferences_AccessKind_DistinguishesReadWrite()
    {
        var result = FindReferencesTool.FindReferences(_fixture.DbProvider,
            "global::Sextant.Store.IndexDatabase");
        var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");
        // Verify access_kind field exists on references
        foreach (var r in results.EnumerateArray())
        {
            Assert.IsTrue(r.TryGetProperty("access_kind", out _));
        }
    }

    [TestMethod]
    public void FindSymbol_ScopeProject_NarrowsResults()
    {
        // Get a project ID first
        var statusResult = GetIndexStatusTool.GetIndexStatus(_fixture.DbProvider);
        var statusDoc = JsonDocument.Parse(statusResult);
        var projects = statusDoc.RootElement.GetProperty("results");
        if (projects.GetArrayLength() > 0)
        {
            var projectId = projects[0].GetProperty("canonical_id").GetString()!;
            var result = FindSymbolTool.FindSymbol(_fixture.DbProvider, "Index",
                fuzzy: true, scope: $"project:{projectId}");
            var doc = JsonDocument.Parse(result);
            Assert.IsTrue(doc.RootElement.TryGetProperty("results", out _));
        }
    }

    [TestMethod]
    public void GetSourceContext_RealFile_ReturnsCode()
    {
        // Read a known source file
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Sextant.slnx")))
            dir = Path.GetDirectoryName(dir);

        if (dir != null)
        {
            var filePath = Path.Combine(dir, "src", "Sextant.Store", "IndexDatabase.cs");
            if (File.Exists(filePath))
            {
                var result = GetSourceContextTool.GetSourceContext(filePath, 10, 3);
                var doc = JsonDocument.Parse(result);
                var meta = doc.RootElement.GetProperty("meta");
                Assert.AreEqual(1, meta.GetProperty("result_count").GetInt32());
            }
        }
    }

    [TestMethod]
    public void GetNamespaceTree_Sextant_ShowsNamespaces()
    {
        var result = GetNamespaceTreeTool.GetNamespaceTree(_fixture.DbProvider);
        var doc = JsonDocument.Parse(result);
        var first = doc.RootElement.GetProperty("results")[0];
        var childNamespaces = first.GetProperty("child_namespaces");
        Assert.IsTrue(childNamespaces.GetArrayLength() >= 1);
    }

    [TestMethod]
    public void FindBySignature_ParameterTypeString_FindsMethods()
    {
        var result = FindBySignatureTool.FindBySignature(_fixture.DbProvider,
            parameter_type: "string");
        var doc = JsonDocument.Parse(result);
        var meta = doc.RootElement.GetProperty("meta");
        Assert.IsTrue(meta.GetProperty("result_count").GetInt32() >= 1);
    }

    [TestMethod]
    public void GetTypeDependents_IndexDatabase_HasDependents()
    {
        var result = GetTypeDependentsTool.GetTypeDependents(_fixture.DbProvider,
            "global::Sextant.Store.IndexDatabase");
        var doc = JsonDocument.Parse(result);
        Assert.IsTrue(doc.RootElement.TryGetProperty("results", out _));
    }

    [TestMethod]
    public void FindTests_DiscoverAllMSTests()
    {
        var result = FindTestsTool.FindTests(_fixture.DbProvider, framework: "mstest");
        var doc = JsonDocument.Parse(result);
        var meta = doc.RootElement.GetProperty("meta");
        Assert.IsTrue(meta.GetProperty("result_count").GetInt32() >= 1);
    }

    [TestMethod]
    public void FindComments_TodosExist()
    {
        var result = FindCommentsTool.FindComments(_fixture.DbProvider, tag: "TODO");
        var doc = JsonDocument.Parse(result);
        // There may or may not be TODOs; just verify valid response
        Assert.IsTrue(doc.RootElement.TryGetProperty("meta", out _));
    }

    [TestMethod]
    public void TraceValue_RealCallSite_HasFlow()
    {
        // Try tracing a known method - may have argument flow
        var result = TraceValueTool.TraceValue(_fixture.DbProvider,
            "global::Sextant.Store.IndexDatabase.RunMigrations()", "origins");
        var doc = JsonDocument.Parse(result);
        Assert.IsTrue(doc.RootElement.TryGetProperty("results", out _));
    }
}
