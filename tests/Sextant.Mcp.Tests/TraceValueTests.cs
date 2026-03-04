using System.Text.Json;
using Sextant.Mcp.Tools;

namespace Sextant.Mcp.Tests;

[TestClass]
public class TraceValueTests
{
    private static readonly McpTestFixture _fixture = McpTestFixtureInstance.Instance;

    [TestMethod]
    public void TraceValue_Origins_ReturnsArgumentFlowData()
    {
        var result = TraceValueTool.TraceValue(_fixture.DbProvider,
            "global::Alpha.BaseService.Process(string)", "origins");
        var doc = JsonDocument.Parse(result);
        var meta = doc.RootElement.GetProperty("meta");
        Assert.IsTrue(meta.GetProperty("result_count").GetInt32() >= 1);

        var results = doc.RootElement.GetProperty("results");
        var inputGroup = results.EnumerateArray()
            .FirstOrDefault(r => r.GetProperty("parameter_name").GetString() == "input");
        Assert.IsTrue(inputGroup.ValueKind != JsonValueKind.Undefined,
            "Should have an 'input' parameter group");

        var callers = inputGroup.GetProperty("callers");
        Assert.IsTrue(callers.GetArrayLength() >= 2);

        var hasMemberAccess = false;
        var hasLiteral = false;
        foreach (var caller in callers.EnumerateArray())
        {
            Assert.IsTrue(caller.TryGetProperty("argument_expression", out _));
            Assert.IsTrue(caller.TryGetProperty("argument_kind", out _));
            var kind = caller.GetProperty("argument_kind").GetString();
            if (kind == "member_access") hasMemberAccess = true;
            if (kind == "literal") hasLiteral = true;
        }
        Assert.IsTrue(hasMemberAccess, "Should include member_access argument kind");
        Assert.IsTrue(hasLiteral, "Should include literal argument kind");
    }

    [TestMethod]
    public void TraceValue_Destinations_ReturnsReturnFlowData()
    {
        var result = TraceValueTool.TraceValue(_fixture.DbProvider,
            "global::Alpha.BaseService.Process(string)", "destinations");
        var doc = JsonDocument.Parse(result);
        var meta = doc.RootElement.GetProperty("meta");
        Assert.IsTrue(meta.GetProperty("result_count").GetInt32() >= 2);

        var results = doc.RootElement.GetProperty("results");
        var destinations = results.EnumerateArray().ToList();

        var kinds = destinations.Select(d => d.GetProperty("destination_kind").GetString()).ToList();
        Assert.IsTrue(kinds.Contains("assignment"));
        Assert.IsTrue(kinds.Contains("discard"));

        var assignment = destinations.First(d =>
            d.GetProperty("destination_kind").GetString() == "assignment");
        Assert.AreEqual("result", assignment.GetProperty("destination_variable").GetString());
    }

    [TestMethod]
    public void TraceValue_Origins_SpecificParameter_FiltersToThatParam()
    {
        var result = TraceValueTool.TraceValue(_fixture.DbProvider,
            "global::Alpha.BaseService.Process(string)", "origins", parameter: "input");
        var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");
        Assert.AreEqual(1, results.GetArrayLength());
        Assert.AreEqual("input", results[0].GetProperty("parameter_name").GetString());
    }

    [TestMethod]
    public void TraceValue_InvalidDirection_ReturnsEmpty()
    {
        var result = TraceValueTool.TraceValue(_fixture.DbProvider,
            "global::Alpha.BaseService.Process(string)", "invalid_direction");
        var doc = JsonDocument.Parse(result);
        var meta = doc.RootElement.GetProperty("meta");
        Assert.AreEqual(0, meta.GetProperty("result_count").GetInt32());
    }

    [TestMethod]
    public void TraceValue_NonExistentMethod_ReturnsEmpty()
    {
        var result = TraceValueTool.TraceValue(_fixture.DbProvider,
            "global::Nonexistent.Method()", "origins");
        var doc = JsonDocument.Parse(result);
        var meta = doc.RootElement.GetProperty("meta");
        Assert.AreEqual(0, meta.GetProperty("result_count").GetInt32());
    }
}
