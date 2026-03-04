using System.Text.Json;
using Sextant.Mcp.Tools;

namespace Sextant.Mcp.Tests;

[TestClass]
public class FindTestsTests
{
    private static readonly McpTestFixture _fixture = McpTestFixtureInstance.Instance;

    [TestMethod]
    public void FindTests_DiscoversByAttribute()
    {
        var result = FindTestsTool.FindTests(_fixture.DbProvider);
        var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");
        Assert.IsTrue(results.GetArrayLength() >= 2);

        foreach (var test in results.EnumerateArray())
        {
            Assert.IsTrue(test.TryGetProperty("test_framework", out _));
            Assert.IsTrue(test.TryGetProperty("fully_qualified_name", out _));
        }
    }

    [TestMethod]
    public void FindTests_ForSymbol_FiltersToRelevantTests()
    {
        var result = FindTestsTool.FindTests(_fixture.DbProvider,
            for_symbol: "global::Alpha.BaseService.Process(string)");
        var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");
        Assert.IsTrue(results.GetArrayLength() >= 1);

        var names = results.EnumerateArray()
            .Select(r => r.GetProperty("display_name").GetString())
            .ToList();
        Assert.IsTrue(names.Contains("Process_ReturnsExpected"));
    }

    [TestMethod]
    public void FindTests_FrameworkXunit_FiltersToXunitOnly()
    {
        var result = FindTestsTool.FindTests(_fixture.DbProvider, framework: "xunit");
        var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");
        Assert.IsTrue(results.GetArrayLength() >= 2);

        foreach (var test in results.EnumerateArray())
        {
            Assert.AreEqual("xunit", test.GetProperty("test_framework").GetString());
        }
    }

    [TestMethod]
    public void FindTests_ForSymbol_NamingConventionFallback()
    {
        // Init method has no references from test files, so naming convention kicks in
        var result = FindTestsTool.FindTests(_fixture.DbProvider,
            for_symbol: "global::Alpha.BaseService.Init()");
        var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");
        Assert.IsTrue(results.GetArrayLength() >= 1);

        var names = results.EnumerateArray()
            .Select(r => r.GetProperty("display_name").GetString())
            .ToList();
        Assert.IsTrue(names.Contains("Init_Works"));
    }

    [TestMethod]
    public void FindTests_MaxResults_IsRespected()
    {
        var result = FindTestsTool.FindTests(_fixture.DbProvider, max_results: 1);
        var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");
        Assert.IsTrue(results.GetArrayLength() <= 1);
    }

    [TestMethod]
    public void FindTests_NonTestSymbol_WithNoTests_ReturnsEmpty()
    {
        var result = FindTestsTool.FindTests(_fixture.DbProvider,
            for_symbol: "global::Beta.Consumer");
        var doc = JsonDocument.Parse(result);
        var meta = doc.RootElement.GetProperty("meta");
        Assert.AreEqual(0, meta.GetProperty("result_count").GetInt32());
    }

    [TestMethod]
    public void FindTests_NunitFramework_FindsNunitTests()
    {
        var result = FindTestsTool.FindTests(_fixture.DbProvider, framework: "nunit");
        var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");
        Assert.IsTrue(results.GetArrayLength() >= 1);

        foreach (var test in results.EnumerateArray())
        {
            Assert.AreEqual("nunit", test.GetProperty("test_framework").GetString());
        }
    }
}
