using System.Text.Json;
using System.Text.RegularExpressions;
using Sextant.Mcp.Tools;

namespace Sextant.Mcp.Tests;

[TestClass]
public class FindBySignatureTests
{
    private static readonly McpTestFixture _fixture = McpTestFixtureInstance.Instance;

    [TestMethod]
    public void FindBySignature_ReturnTypeVoid_FindsVoidMethods()
    {
        var result = FindBySignatureTool.FindBySignature(_fixture.DbProvider,
            return_type: "void");
        var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");
        Assert.IsTrue(results.GetArrayLength() >= 1);

        foreach (var item in results.EnumerateArray())
        {
            var sig = item.GetProperty("signature").GetString();
            Assert.IsNotNull(sig);
            StringAssert.Contains(sig, "void");
        }
    }

    [TestMethod]
    public void FindBySignature_ParameterTypeString_FindsMethodsWithStringParam()
    {
        var result = FindBySignatureTool.FindBySignature(_fixture.DbProvider,
            parameter_type: "string");
        var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");
        Assert.IsTrue(results.GetArrayLength() >= 1);

        var fqns = results.EnumerateArray()
            .Select(r => r.GetProperty("fully_qualified_name").GetString())
            .ToList();
        Assert.IsTrue(fqns.Contains("global::Alpha.BaseService.Process(string)"));
    }

    [TestMethod]
    public void FindBySignature_ParameterCountZero_FindsParameterlessMethods()
    {
        var result = FindBySignatureTool.FindBySignature(_fixture.DbProvider,
            parameter_count: 0, project_id: "proj_alpha_123456");
        var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");
        Assert.IsTrue(results.GetArrayLength() >= 1);

        foreach (var item in results.EnumerateArray())
        {
            var sig = item.GetProperty("signature").GetString();
            Assert.IsNotNull(sig);
            Assert.IsTrue(Regex.IsMatch(sig, @"\(\s*\)"));
        }
    }

    [TestMethod]
    public void FindBySignature_ReturnTypeTask_FindsAsyncMethods()
    {
        var result = FindBySignatureTool.FindBySignature(_fixture.DbProvider,
            return_type: "Task");
        var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");
        Assert.IsTrue(results.GetArrayLength() >= 1);

        var fqns = results.EnumerateArray()
            .Select(r => r.GetProperty("fully_qualified_name").GetString())
            .ToList();
        Assert.IsTrue(fqns.Contains("global::Alpha.BaseService.LoadAsync()"));
    }

    [TestMethod]
    public void FindBySignature_ParameterCount3_WithGenericParam()
    {
        // Dictionary<string, int> counts as 1 parameter
        var result = FindBySignatureTool.FindBySignature(_fixture.DbProvider,
            parameter_count: 3);
        var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");
        Assert.IsTrue(results.GetArrayLength() >= 1);

        var fqns = results.EnumerateArray()
            .Select(r => r.GetProperty("fully_qualified_name").GetString())
            .ToList();
        Assert.IsTrue(fqns.Contains("global::Alpha.BaseService.Merge(string, Dictionary<string, int>, bool)"));
    }

    [TestMethod]
    public void FindBySignature_CombinedFilters_ReturnTypeVoidAndParamString()
    {
        var result = FindBySignatureTool.FindBySignature(_fixture.DbProvider,
            return_type: "void", parameter_type: "string");
        var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");
        Assert.IsTrue(results.GetArrayLength() >= 1);

        foreach (var item in results.EnumerateArray())
        {
            var sig = item.GetProperty("signature").GetString()!;
            StringAssert.Contains(sig, "void");
            StringAssert.Contains(sig, "string");
        }
    }

    [TestMethod]
    public void FindBySignature_KindProperty_ReturnTypeString_FindsStringProperties()
    {
        var result = FindBySignatureTool.FindBySignature(_fixture.DbProvider,
            kind: "property", return_type: "string");
        var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");
        Assert.IsTrue(results.GetArrayLength() >= 1);

        var fqns = results.EnumerateArray()
            .Select(r => r.GetProperty("fully_qualified_name").GetString())
            .ToList();
        Assert.IsTrue(fqns.Contains("global::Alpha.BaseService.Name"));
    }

    [TestMethod]
    public void FindBySignature_NoMatches_ReturnsEmptyResult()
    {
        var result = FindBySignatureTool.FindBySignature(_fixture.DbProvider,
            return_type: "NonExistentType12345");
        var doc = JsonDocument.Parse(result);
        var meta = doc.RootElement.GetProperty("meta");
        Assert.AreEqual(0, meta.GetProperty("result_count").GetInt32());
    }
}
