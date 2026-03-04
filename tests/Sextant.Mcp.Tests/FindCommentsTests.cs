using System.Text.Json;
using Sextant.Mcp.Tools;

namespace Sextant.Mcp.Tests;

[TestClass]
public class FindCommentsTests
{
    private static readonly McpTestFixture _fixture = McpTestFixtureInstance.Instance;

    [TestMethod]
    public void FindComments_ReturnsAllComments()
    {
        var result = FindCommentsTool.FindComments(_fixture.DbProvider);
        var doc = JsonDocument.Parse(result);
        var meta = doc.RootElement.GetProperty("meta");
        Assert.AreEqual(4, meta.GetProperty("result_count").GetInt32());

        var results = doc.RootElement.GetProperty("results");
        foreach (var comment in results.EnumerateArray())
        {
            Assert.IsTrue(comment.TryGetProperty("tag", out _));
            Assert.IsTrue(comment.TryGetProperty("text", out _));
            Assert.IsTrue(comment.TryGetProperty("file_path", out _));
            Assert.IsTrue(comment.TryGetProperty("line", out _));
        }
    }

    [TestMethod]
    public void FindComments_TagTodo_FiltersToTodoOnly()
    {
        var result = FindCommentsTool.FindComments(_fixture.DbProvider, tag: "TODO");
        var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");
        Assert.AreEqual(2, results.GetArrayLength());

        foreach (var comment in results.EnumerateArray())
        {
            Assert.AreEqual("TODO", comment.GetProperty("tag").GetString());
        }
    }

    [TestMethod]
    public void FindComments_TagFixme_ReturnsSingleResult()
    {
        var result = FindCommentsTool.FindComments(_fixture.DbProvider, tag: "FIXME");
        var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");
        Assert.AreEqual(1, results.GetArrayLength());
        StringAssert.Contains(results[0].GetProperty("text").GetString(), "Memory leak");
    }

    [TestMethod]
    public void FindComments_ProjectFilter_ScopesToProject()
    {
        var result = FindCommentsTool.FindComments(_fixture.DbProvider,
            project_id: "proj_beta_7890ab");
        var doc = JsonDocument.Parse(result);
        var meta = doc.RootElement.GetProperty("meta");
        Assert.AreEqual(1, meta.GetProperty("result_count").GetInt32());
        StringAssert.Contains(
            doc.RootElement.GetProperty("results")[0].GetProperty("text").GetString(), "retry");
    }

    [TestMethod]
    public void FindComments_SearchRetry_FiltersByTextContent()
    {
        var result = FindCommentsTool.FindComments(_fixture.DbProvider, search: "retry");
        var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");
        Assert.IsTrue(results.GetArrayLength() >= 1);

        foreach (var comment in results.EnumerateArray())
        {
            var text = comment.GetProperty("text").GetString()!;
            Assert.IsTrue(text.Contains("retry", StringComparison.OrdinalIgnoreCase));
        }
    }
}
