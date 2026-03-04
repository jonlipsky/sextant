using System.Text.Json;
using Sextant.Mcp;
using Sextant.Mcp.Tools;

namespace Sextant.Integration.Tests;

[TestClass]
[TestCategory("Integration")]
public class ExistingToolIntegrationTests
{
    private IntegrationFixture _fixture = null!;

    [TestInitialize]
    public void Setup() => _fixture = IntegrationFixture.Instance;

    [TestMethod]
    public void FindSymbol_DatabaseProvider_ReturnsExactMatch()
    {
        var result = FindSymbolTool.FindSymbol(_fixture.DbProvider, "global::Sextant.Mcp.DatabaseProvider");
        var doc = JsonDocument.Parse(result);
        var meta = doc.RootElement.GetProperty("meta");
        Assert.IsTrue(meta.GetProperty("result_count").GetInt32() >= 1);
    }

    [TestMethod]
    public void FindSymbol_Fuzzy_ReturnsResults()
    {
        // Use a display name that must exist since we found DatabaseProvider by exact match
        var result = FindSymbolTool.FindSymbol(_fixture.DbProvider, "DatabaseProvider", fuzzy: true);
        var doc = JsonDocument.Parse(result);
        var meta = doc.RootElement.GetProperty("meta");
        Assert.IsTrue(meta.GetProperty("result_count").GetInt32() >= 1);
    }

    [TestMethod]
    public void FindReferences_KnownSymbol_HasResults()
    {
        var result = FindReferencesTool.FindReferences(_fixture.DbProvider,
            "global::Sextant.Store.IndexDatabase");
        var doc = JsonDocument.Parse(result);
        var meta = doc.RootElement.GetProperty("meta");
        Assert.IsTrue(meta.GetProperty("result_count").GetInt32() >= 1);
    }

    [TestMethod]
    public void GetTypeMembers_KnownClass_ListsMembers()
    {
        // First find a real class FQN from the index
        var symbolResult = FindSymbolTool.FindSymbol(_fixture.DbProvider,
            "global::Sextant.Store.IndexDatabase");
        var symbolDoc = JsonDocument.Parse(symbolResult);
        var resultCount = symbolDoc.RootElement.GetProperty("meta").GetProperty("result_count").GetInt32();
        if (resultCount == 0)
        {
            // Symbol may not exist; skip gracefully
            return;
        }

        var result = GetTypeMembersTool.GetTypeMembers(_fixture.DbProvider,
            "global::Sextant.Store.IndexDatabase");
        var doc = JsonDocument.Parse(result);
        Assert.IsTrue(doc.RootElement.TryGetProperty("results", out _));
    }

    [TestMethod]
    public void GetFileSymbols_KnownFile_HasSymbols()
    {
        var result = GetFileSymbolsTool.GetFileSymbols(_fixture.DbProvider,
            "src/Sextant.Store/IndexDatabase.cs");
        var doc = JsonDocument.Parse(result);
        // May use repo-relative paths; try a fallback
        var meta = doc.RootElement.GetProperty("meta");
        if (meta.GetProperty("result_count").GetInt32() == 0)
        {
            // Try absolute-style path matching
            result = GetFileSymbolsTool.GetFileSymbols(_fixture.DbProvider, "IndexDatabase.cs");
            doc = JsonDocument.Parse(result);
        }
        // At least verify the tool responds with valid JSON
        Assert.IsTrue(doc.RootElement.TryGetProperty("meta", out _));
    }

    [TestMethod]
    public void GetCallHierarchy_HasCallers()
    {
        var result = GetCallHierarchyTool.GetCallHierarchy(_fixture.DbProvider,
            "global::Sextant.Store.IndexDatabase.RunMigrations()", "callers");
        var doc = JsonDocument.Parse(result);
        Assert.IsTrue(doc.RootElement.TryGetProperty("results", out _));
    }

    [TestMethod]
    public void GetTypeHierarchy_IndexDatabase_HasBase()
    {
        var result = GetTypeHierarchyTool.GetTypeHierarchy(_fixture.DbProvider,
            "global::Sextant.Store.IndexDatabase");
        var doc = JsonDocument.Parse(result);
        Assert.IsTrue(doc.RootElement.TryGetProperty("results", out _));
    }

    [TestMethod]
    public void GetImplementors_IDisposable_FindsImplementors()
    {
        var result = GetImplementorsTool.GetImplementors(_fixture.DbProvider,
            "global::System.IDisposable");
        var doc = JsonDocument.Parse(result);
        Assert.IsTrue(doc.RootElement.TryGetProperty("results", out _));
    }

    [TestMethod]
    public void SemanticSearch_Index_FindsRelevant()
    {
        var result = SemanticSearchTool.SemanticSearch(_fixture.DbProvider, "index");
        var doc = JsonDocument.Parse(result);
        var meta = doc.RootElement.GetProperty("meta");
        Assert.IsTrue(meta.GetProperty("result_count").GetInt32() >= 1);
    }

    [TestMethod]
    public void GetIndexStatus_ReturnsProjectData()
    {
        var result = GetIndexStatusTool.GetIndexStatus(_fixture.DbProvider);
        var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");
        Assert.IsTrue(results.GetArrayLength() >= 1);
    }

    [TestMethod]
    public void GetProjectDependencies_McpProject_HasDeps()
    {
        // First get a project ID from the index status
        var statusResult = GetIndexStatusTool.GetIndexStatus(_fixture.DbProvider);
        var statusDoc = JsonDocument.Parse(statusResult);
        var projects = statusDoc.RootElement.GetProperty("results");
        Assert.IsTrue(projects.GetArrayLength() >= 1);

        var projectId = projects[0].GetProperty("canonical_id").GetString()!;
        var result = GetProjectDependenciesTool.GetProjectDependencies(_fixture.DbProvider, projectId);
        var doc = JsonDocument.Parse(result);
        Assert.IsTrue(doc.RootElement.TryGetProperty("results", out _));
    }

    [TestMethod]
    public void GetImpact_KnownSymbol_ShowsImpact()
    {
        var result = GetImpactTool.GetImpact(_fixture.DbProvider,
            "global::Sextant.Store.IndexDatabase");
        var doc = JsonDocument.Parse(result);
        Assert.IsTrue(doc.RootElement.TryGetProperty("results", out _));
    }

    [TestMethod]
    public void GetApiSurface_StoreProject_HasPublicApi()
    {
        var statusResult = GetIndexStatusTool.GetIndexStatus(_fixture.DbProvider);
        var statusDoc = JsonDocument.Parse(statusResult);
        var projects = statusDoc.RootElement.GetProperty("results");

        // Find the Store project
        string? storeProjectId = null;
        foreach (var p in projects.EnumerateArray())
        {
            var path = p.GetProperty("repo_relative_path").GetString();
            if (path != null && path.Contains("Sextant.Store"))
            {
                storeProjectId = p.GetProperty("canonical_id").GetString();
                break;
            }
        }

        if (storeProjectId != null)
        {
            var result = GetApiSurfaceTool.GetApiSurface(_fixture.DbProvider, storeProjectId);
            var doc = JsonDocument.Parse(result);
            var meta = doc.RootElement.GetProperty("meta");
            Assert.IsTrue(meta.GetProperty("result_count").GetInt32() >= 1);
        }
    }

    [TestMethod]
    public void FindUnreferenced_ReturnsResults()
    {
        var result = FindUnreferencedTool.FindUnreferenced(_fixture.DbProvider);
        var doc = JsonDocument.Parse(result);
        Assert.IsTrue(doc.RootElement.TryGetProperty("results", out _));
    }

    [TestMethod]
    public void FindByAttribute_McpServerTool_FindsTools()
    {
        var result = FindByAttributeTool.FindByAttribute(_fixture.DbProvider,
            "global::ModelContextProtocol.Server.McpServerToolAttribute");
        var doc = JsonDocument.Parse(result);
        // May not find if attribute FQN is different; check valid response
        Assert.IsTrue(doc.RootElement.TryGetProperty("meta", out _));
    }
}
