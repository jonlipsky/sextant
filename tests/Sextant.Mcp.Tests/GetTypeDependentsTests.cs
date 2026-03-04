using System.Text.Json;
using Sextant.Mcp.Tools;

namespace Sextant.Mcp.Tests;

[TestClass]
public class GetTypeDependentsTests
{
    private static readonly McpTestFixture _fixture = McpTestFixtureInstance.Instance;

    [TestMethod]
    public void GetTypeDependents_FindsInheritingTypes()
    {
        var result = GetTypeDependentsTool.GetTypeDependents(_fixture.DbProvider,
            "global::Alpha.BaseService", "inherits");
        var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");
        Assert.AreEqual(1, results.GetArrayLength());
        Assert.AreEqual("DerivedService", results[0].GetProperty("display_name").GetString());
    }

    [TestMethod]
    public void GetTypeDependents_NoDependents_ReturnsEmpty()
    {
        var result = GetTypeDependentsTool.GetTypeDependents(_fixture.DbProvider,
            "global::Alpha.DerivedService");
        var doc = JsonDocument.Parse(result);
        var meta = doc.RootElement.GetProperty("meta");
        Assert.AreEqual(0, meta.GetProperty("result_count").GetInt32());
    }

    [TestMethod]
    public void GetTypeDependents_FindsImplementingTypes()
    {
        var result = GetTypeDependentsTool.GetTypeDependents(_fixture.DbProvider,
            "global::Alpha.IProcessor", "implements");
        var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");
        Assert.IsTrue(results.GetArrayLength() >= 1);

        var names = results.EnumerateArray()
            .Select(r => r.GetProperty("display_name").GetString())
            .ToList();
        Assert.IsTrue(names.Contains("DerivedService"));
    }

    [TestMethod]
    public void GetTypeDependents_AllKind_ReturnsAllRelationships()
    {
        var result = GetTypeDependentsTool.GetTypeDependents(_fixture.DbProvider,
            "global::Alpha.BaseService", "all");
        var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");
        Assert.IsTrue(results.GetArrayLength() >= 1);
    }

    [TestMethod]
    public void GetTypeDependents_InheritsFilter_NarrowsResults()
    {
        var result = GetTypeDependentsTool.GetTypeDependents(_fixture.DbProvider,
            "global::Alpha.BaseService", "inherits");
        var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");
        Assert.AreEqual(1, results.GetArrayLength());
        Assert.AreEqual("DerivedService", results[0].GetProperty("display_name").GetString());
    }
}
