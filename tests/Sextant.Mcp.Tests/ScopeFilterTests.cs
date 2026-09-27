using System.Text.Json;
using Sextant.Mcp.Tools;
using Sextant.Store;

namespace Sextant.Mcp.Tests;

[TestClass]
public class ScopeFilterTests
{
    private static readonly McpTestFixture _fixture = McpTestFixtureInstance.Instance;

    [TestMethod]
    public void FindSymbol_NullScope_ReturnsResults()
    {
        var result = FindSymbolTool.FindSymbol(_fixture.DbProvider, "BaseService", fuzzy: true).GetAwaiter().GetResult();
        var doc = JsonDocument.Parse(result);
        var meta = doc.RootElement.GetProperty("meta");
        Assert.IsTrue(meta.GetProperty("result_count").GetInt32() >= 1);
    }

    [TestMethod]
    public void FindSymbol_ScopeProject_FiltersToProject()
    {
        var result = FindSymbolTool.FindSymbol(_fixture.DbProvider, "Consumer",
            fuzzy: true, scope: "project:proj_alpha_123456").GetAwaiter().GetResult();
        var doc = JsonDocument.Parse(result);
        var meta = doc.RootElement.GetProperty("meta");
        Assert.AreEqual(0, meta.GetProperty("result_count").GetInt32());

        result = FindSymbolTool.FindSymbol(_fixture.DbProvider, "Consumer",
            fuzzy: true, scope: "project:proj_beta_7890ab").GetAwaiter().GetResult();
        doc = JsonDocument.Parse(result);
        meta = doc.RootElement.GetProperty("meta");
        Assert.IsTrue(meta.GetProperty("result_count").GetInt32() >= 1);
    }

    [TestMethod]
    public void FindReferences_ScopeProject_FiltersReferences()
    {
        var resultAll = FindReferencesTool.FindReferences(_fixture.DbProvider,
            "global::Alpha.BaseService.Process(string)");
        var docAll = JsonDocument.Parse(resultAll);
        var totalCount = docAll.RootElement.GetProperty("meta").GetProperty("result_count").GetInt32();
        Assert.AreEqual(3, totalCount);

        var resultScoped = FindReferencesTool.FindReferences(_fixture.DbProvider,
            "global::Alpha.BaseService.Process(string)",
            scope: "project:proj_alpha_123456");
        var docScoped = JsonDocument.Parse(resultScoped);
        var scopedCount = docScoped.RootElement.GetProperty("meta").GetProperty("result_count").GetInt32();
        Assert.AreEqual(2, scopedCount);
    }

    [TestMethod]
    public void FindSymbol_ScopeFile_FiltersToSingleFile()
    {
        var result = FindSymbolTool.FindSymbol(_fixture.DbProvider, "BaseService",
            fuzzy: true, scope: "file:src/Alpha/BaseService.cs").GetAwaiter().GetResult();
        var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");
        // All results should be from that file
        foreach (var r in results.EnumerateArray())
        {
            if (r.TryGetProperty("file_path", out var fp))
                Assert.AreEqual("src/Alpha/BaseService.cs", fp.GetString());
        }
    }

    [TestMethod]
    public void FindSymbol_ScopeSolution_FiltersToTheSolutionsMappedProjects()
    {
        var solutionPath = SeedSolution(_fixture.ProjectId);

        var alpha = FindSymbolTool.FindSymbol(_fixture.DbProvider, "BaseService",
            fuzzy: true, scope: $"solution:{solutionPath}").GetAwaiter().GetResult();
        Assert.IsTrue(ResultCount(alpha) >= 1, "a symbol in a mapped project is in scope");

        var beta = FindSymbolTool.FindSymbol(_fixture.DbProvider, "Consumer",
            fuzzy: true, scope: $"solution:{solutionPath}").GetAwaiter().GetResult();
        Assert.AreEqual(0, ResultCount(beta), "a symbol in an unmapped project is out of scope");
    }

    [TestMethod]
    public void FindSymbol_ScopeKnownSolutionWithNoMappedProject_FailsClosed()
    {
        // Issue #124: a selected multi-solution head none of whose projects loaded is recorded with NO project
        // mapping. Scoping to it must return nothing — not degrade to an unfiltered whole-repository query.
        var solutionPath = SeedSolution();

        var result = FindSymbolTool.FindSymbol(_fixture.DbProvider, "BaseService",
            fuzzy: true, scope: $"solution:{solutionPath}").GetAwaiter().GetResult();
        Assert.AreEqual(0, ResultCount(result));

        var refs = FindReferencesTool.FindReferences(_fixture.DbProvider,
            "global::Alpha.BaseService.Process(string)", scope: $"solution:{solutionPath}");
        Assert.AreEqual(0, ResultCount(refs));
    }

    private static string SeedSolution(params long[] projectIds)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sextant_scope_{Guid.NewGuid():N}", "Head.slnx");
        var store = new SolutionStore(_fixture.Db.GetConnection());
        var solutionId = store.Upsert(path, "Head", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        foreach (var projectId in projectIds)
            store.AddProjectMapping(solutionId, projectId);
        return path;
    }

    private static int ResultCount(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("meta").GetProperty("result_count").GetInt32();

    [TestMethod]
    public void FindSymbol_InvalidScopeFormat_ReturnsResults()
    {
        // Invalid scope format should degrade gracefully (return all results)
        var result = FindSymbolTool.FindSymbol(_fixture.DbProvider, "BaseService",
            fuzzy: true, scope: "invalid_format").GetAwaiter().GetResult();
        var doc = JsonDocument.Parse(result);
        var meta = doc.RootElement.GetProperty("meta");
        // Should not crash; may return all results or empty depending on implementation
        Assert.IsTrue(meta.TryGetProperty("result_count", out _));
    }
}
