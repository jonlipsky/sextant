using System.Text.Json;
using Sextant.Mcp.Tools;

namespace Sextant.Mcp.Tests;

[TestClass]
public class FindReferencesGroupingTests
{
    private static readonly McpTestFixture _fixture = McpTestFixtureInstance.Instance;

    [TestMethod]
    public void FindReferences_NullGroupBy_ReturnsFlatList()
    {
        var result = FindReferencesTool.FindReferences(_fixture.DbProvider,
            "global::Alpha.BaseService.Process(string)");
        var doc = JsonDocument.Parse(result);
        var meta = doc.RootElement.GetProperty("meta");
        Assert.AreEqual(3, meta.GetProperty("result_count").GetInt32());

        var results = doc.RootElement.GetProperty("results");
        Assert.AreEqual(3, results.GetArrayLength());
        Assert.IsTrue(results[0].TryGetProperty("file_path", out _));
        Assert.IsFalse(results[0].TryGetProperty("group_key", out _));
    }

    [TestMethod]
    public void FindReferences_GroupByProject_GroupsByProjectId()
    {
        var result = FindReferencesTool.FindReferences(_fixture.DbProvider,
            "global::Alpha.BaseService.Process(string)", group_by: "project");
        var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");

        Assert.AreEqual(2, results.GetArrayLength());
        foreach (var group in results.EnumerateArray())
        {
            Assert.AreEqual("project", group.GetProperty("group_type").GetString());
            Assert.IsTrue(group.TryGetProperty("group_key", out _));
            Assert.IsTrue(group.GetProperty("count").GetInt32() >= 1);
            Assert.IsTrue(group.TryGetProperty("items", out _));
        }
    }

    [TestMethod]
    public void FindReferences_GroupByFile_GroupsByFilePath()
    {
        var result = FindReferencesTool.FindReferences(_fixture.DbProvider,
            "global::Alpha.BaseService.Process(string)", group_by: "file");
        var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");

        Assert.AreEqual(3, results.GetArrayLength());
        foreach (var group in results.EnumerateArray())
        {
            Assert.AreEqual("file", group.GetProperty("group_type").GetString());
            var key = group.GetProperty("group_key").GetString();
            Assert.IsNotNull(key);
            StringAssert.Contains(key, ".cs");
        }
    }

    [TestMethod]
    public void FindReferences_GroupByProjectFile_NestsGroups()
    {
        var result = FindReferencesTool.FindReferences(_fixture.DbProvider,
            "global::Alpha.BaseService.Process(string)", group_by: "project,file");
        var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");

        Assert.AreEqual(2, results.GetArrayLength());
        foreach (var projectGroup in results.EnumerateArray())
        {
            Assert.AreEqual("project", projectGroup.GetProperty("group_type").GetString());
            var items = projectGroup.GetProperty("items");
            foreach (var fileGroup in items.EnumerateArray())
            {
                Assert.AreEqual("file", fileGroup.GetProperty("group_type").GetString());
                Assert.IsTrue(fileGroup.TryGetProperty("items", out _));
            }
        }
    }

    [TestMethod]
    public void FindReferences_GroupByKind_GroupsByReferenceKind()
    {
        var result = FindReferencesTool.FindReferences(_fixture.DbProvider,
            "global::Alpha.BaseService.Process(string)", group_by: "kind");
        var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");

        Assert.IsTrue(results.GetArrayLength() >= 1);
        foreach (var group in results.EnumerateArray())
        {
            Assert.AreEqual("kind", group.GetProperty("group_type").GetString());
            Assert.IsTrue(group.TryGetProperty("group_key", out _));
            Assert.IsTrue(group.GetProperty("count").GetInt32() >= 1);
        }
    }

    [TestMethod]
    public void FindReferences_GroupByProjectFileKind_TripleNesting()
    {
        var result = FindReferencesTool.FindReferences(_fixture.DbProvider,
            "global::Alpha.BaseService.Process(string)", group_by: "project,file,kind");
        var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");

        Assert.AreEqual(2, results.GetArrayLength());
        foreach (var projectGroup in results.EnumerateArray())
        {
            Assert.AreEqual("project", projectGroup.GetProperty("group_type").GetString());
            foreach (var fileGroup in projectGroup.GetProperty("items").EnumerateArray())
            {
                Assert.AreEqual("file", fileGroup.GetProperty("group_type").GetString());
                foreach (var kindGroup in fileGroup.GetProperty("items").EnumerateArray())
                {
                    Assert.AreEqual("kind", kindGroup.GetProperty("group_type").GetString());
                    Assert.IsTrue(kindGroup.GetProperty("count").GetInt32() >= 1);
                }
            }
        }
    }

    [TestMethod]
    public void FindReferences_EmptyResultsWithGrouping_ReturnsEmptyResultsArray()
    {
        var result = FindReferencesTool.FindReferences(_fixture.DbProvider,
            "global::Nonexistent.Symbol", group_by: "project");
        var doc = JsonDocument.Parse(result);
        var meta = doc.RootElement.GetProperty("meta");
        Assert.AreEqual(0, meta.GetProperty("result_count").GetInt32());
    }
}
