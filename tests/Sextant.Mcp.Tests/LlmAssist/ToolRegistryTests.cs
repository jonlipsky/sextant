using Sextant.Mcp;
using Sextant.Mcp.LlmAssist;

namespace Sextant.Mcp.Tests.LlmAssist;

[TestClass]
public class ToolRegistryTests : IDisposable
{
    private readonly McpTestFixture _fixture = new();

    [TestMethod]
    public void BuildAiTools_Returns10Tools()
    {
        var registry = new ToolRegistry(_fixture.DbProvider);
        var tools = registry.BuildAiTools();

        Assert.AreEqual(10, tools.Count);
    }

    [TestMethod]
    public void BuildAiTools_ContainsExpectedToolNames()
    {
        var registry = new ToolRegistry(_fixture.DbProvider);
        var tools = registry.BuildAiTools();
        var names = tools.Select(t => t.Name).OrderBy(n => n).ToList();

        CollectionAssert.Contains(names, "find_symbol");
        CollectionAssert.Contains(names, "semantic_search");
        CollectionAssert.Contains(names, "find_references");
        CollectionAssert.Contains(names, "get_call_hierarchy");
        CollectionAssert.Contains(names, "get_type_hierarchy");
        CollectionAssert.Contains(names, "get_type_members");
        CollectionAssert.Contains(names, "get_implementors");
        CollectionAssert.Contains(names, "get_api_surface");
        CollectionAssert.Contains(names, "get_namespace_tree");
        CollectionAssert.Contains(names, "get_source_context");
    }

    [TestMethod]
    public void Dispatch_FindSymbol_ReturnsResults()
    {
        var registry = new ToolRegistry(_fixture.DbProvider);

        var result = registry.Dispatch("find_symbol",
            """{"name":"global::Alpha.BaseService"}""");

        Assert.IsTrue(result.Contains("BaseService"));
        Assert.IsTrue(result.Contains("result_count"));
    }

    [TestMethod]
    public void Dispatch_FindSymbol_Fuzzy_ReturnsResults()
    {
        var registry = new ToolRegistry(_fixture.DbProvider);

        var result = registry.Dispatch("find_symbol",
            """{"name":"BaseService","fuzzy":true}""");

        Assert.IsTrue(result.Contains("BaseService"));
    }

    [TestMethod]
    public void Dispatch_UnknownTool_ReturnsError()
    {
        var registry = new ToolRegistry(_fixture.DbProvider);

        var result = registry.Dispatch("nonexistent_tool", "{}");

        Assert.IsTrue(result.Contains("error"));
        Assert.IsTrue(result.Contains("Unknown tool"));
    }

    [TestMethod]
    public void Dispatch_InvalidJson_ReturnsError()
    {
        var registry = new ToolRegistry(_fixture.DbProvider);

        var result = registry.Dispatch("find_symbol", "not json");

        Assert.IsTrue(result.Contains("error"));
    }

    [TestMethod]
    public void Dispatch_SemanticSearch_ReturnsResults()
    {
        var registry = new ToolRegistry(_fixture.DbProvider);

        var result = registry.Dispatch("semantic_search",
            """{"query":"Service"}""");

        Assert.IsTrue(result.Contains("result_count"));
    }

    [TestMethod]
    public void Dispatch_GetTypeHierarchy_ReturnsResults()
    {
        var registry = new ToolRegistry(_fixture.DbProvider);

        var result = registry.Dispatch("get_type_hierarchy",
            """{"symbol_fqn":"global::Alpha.DerivedService"}""");

        Assert.IsTrue(result.Contains("BaseService"));
    }

    [TestMethod]
    public void Dispatch_GetImplementors_ReturnsResults()
    {
        var registry = new ToolRegistry(_fixture.DbProvider);

        var result = registry.Dispatch("get_implementors",
            """{"symbol_fqn":"global::Alpha.IProcessor"}""");

        Assert.IsTrue(result.Contains("DerivedService"));
    }

    public void Dispose() => _fixture.Dispose();
}
