using System.Text.Json;
using Sextant.Mcp.Tools;

namespace Sextant.Mcp.Tests;

[TestClass]
public class NewToolMetaTests
{
    private static readonly McpTestFixture _fixture = McpTestFixtureInstance.Instance;

    [TestMethod]
    public void AllNewToolResponses_IncludeMetaObject()
    {
        var responses = new[]
        {
            FindCommentsTool.FindComments(_fixture.DbProvider),
            FindBySignatureTool.FindBySignature(_fixture.DbProvider, return_type: "void"),
            GetTypeDependentsTool.GetTypeDependents(_fixture.DbProvider, "global::Alpha.BaseService"),
            FindTestsTool.FindTests(_fixture.DbProvider),
            GetNamespaceTreeTool.GetNamespaceTree(_fixture.DbProvider),
            TraceValueTool.TraceValue(_fixture.DbProvider, "global::Alpha.BaseService.Process(string)", "origins")
        };

        foreach (var response in responses)
        {
            var doc = JsonDocument.Parse(response);
            var meta = doc.RootElement.GetProperty("meta");
            Assert.IsTrue(meta.GetProperty("queried_at").GetInt64() > 0);
            Assert.IsTrue(meta.TryGetProperty("index_freshness", out _));
            Assert.IsTrue(meta.TryGetProperty("result_count", out _));
        }
    }
}
