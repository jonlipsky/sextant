using Sextant.Mcp;
using Sextant.Mcp.LlmAssist;

namespace Sextant.Mcp.Tests.LlmAssist;

[TestClass]
public class ResearchAgentTests : IDisposable
{
    private readonly McpTestFixture _fixture = new();

    private ResearchAgent CreateAgent(MockChatClient mockClient)
    {
        var config = new LlmConfiguration { Model = "test-model", MaxToolCalls = 15 };
        var toolRegistry = new ToolRegistry(_fixture.DbProvider);
        return new ResearchAgent(mockClient, toolRegistry, config);
    }

    [TestMethod]
    public async Task RunAsync_DirectAnswer_ReturnsResult()
    {
        var mockClient = new MockChatClient();
        mockClient.EnqueueTextResponse("The BaseService class handles core logic.");

        var agent = CreateAgent(mockClient);
        var result = await agent.RunAsync(
            "What does BaseService do?", null, null, null, "brief", _fixture.Db, CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual("The BaseService class handles core logic.", result.Answer);
        Assert.AreEqual(0, result.ToolCallsUsed);
        Assert.AreEqual("test-model", result.Model);
        Assert.IsFalse(result.Truncated);
    }

    [TestMethod]
    public async Task RunAsync_WithToolCalls_DispatchesAndReturnsAnswer()
    {
        var mockClient = new MockChatClient();

        // First response: tool call
        mockClient.EnqueueToolCallResponse(
            ("tc1", "find_symbol", """{"name":"global::Alpha.BaseService"}"""));

        // Second response: final answer
        mockClient.EnqueueTextResponse(
            "global::Alpha.BaseService is a class in src/Alpha/BaseService.cs at line 1.");

        var agent = CreateAgent(mockClient);
        var result = await agent.RunAsync(
            "Find BaseService", null, null, null, "brief", _fixture.Db, CancellationToken.None);

        Assert.AreEqual(1, result.ToolCallsUsed);
        Assert.IsTrue(result.Answer.Contains("BaseService"));
        Assert.IsFalse(result.Truncated);
    }

    [TestMethod]
    public async Task RunAsync_ToolCallBudgetExhausted_ForcesAndReturnsTruncated()
    {
        var mockClient = new MockChatClient();

        // Enqueue tool calls up to budget (budget = 2 for this test)
        mockClient.EnqueueToolCallResponse(
            ("tc1", "find_symbol", """{"name":"global::Alpha.BaseService"}"""));
        mockClient.EnqueueToolCallResponse(
            ("tc2", "semantic_search", """{"query":"Service"}"""));

        // Final forced summary
        mockClient.EnqueueTextResponse("Summary after budget exhausted.");

        var config = new LlmConfiguration { Model = "test-model", MaxToolCalls = 2 };
        var toolRegistry = new ToolRegistry(_fixture.DbProvider);
        var agent = new ResearchAgent(mockClient, toolRegistry, config);

        var result = await agent.RunAsync(
            "Describe services", null, null, null, "brief", _fixture.Db, CancellationToken.None);

        Assert.AreEqual(2, result.ToolCallsUsed);
        Assert.IsTrue(result.Truncated);
        Assert.AreEqual("Summary after budget exhausted.", result.Answer);
    }

    [TestMethod]
    public async Task RunAsync_ExtractsSources_FromFqnsInAnswer()
    {
        var mockClient = new MockChatClient();
        mockClient.EnqueueTextResponse(
            "The type global::Alpha.BaseService has method global::Alpha.BaseService.Init().");

        var agent = CreateAgent(mockClient);
        var result = await agent.RunAsync(
            "Tell me about BaseService", null, null, null, "brief", _fixture.Db, CancellationToken.None);

        Assert.IsTrue(result.Sources.Count >= 1);
        Assert.IsTrue(result.Sources.Any(s => s.Fqn.Contains("BaseService")));
    }

    [TestMethod]
    public async Task RunAsync_ApiError_ReturnsErrorMessage()
    {
        var mockClient = new MockChatClient();
        // No responses enqueued — will throw

        var agent = CreateAgent(mockClient);
        var result = await agent.RunAsync(
            "Test question", null, null, null, "brief", _fixture.Db, CancellationToken.None);

        Assert.IsTrue(result.Answer.Contains("LLM API error"));
        Assert.AreEqual(0, result.ToolCallsUsed);
    }

    [TestMethod]
    public async Task RunAsync_MaxToolCallsOverride_IsRespected()
    {
        var mockClient = new MockChatClient();

        // One tool call, then should hit budget
        mockClient.EnqueueToolCallResponse(
            ("tc1", "find_symbol", """{"name":"global::Alpha.BaseService"}"""));
        mockClient.EnqueueTextResponse("Done.");

        var agent = CreateAgent(mockClient);
        var result = await agent.RunAsync(
            "Find BaseService", null, null, 1, "brief", _fixture.Db, CancellationToken.None);

        Assert.IsTrue(result.Truncated);
        Assert.AreEqual(1, result.ToolCallsUsed);
    }

    [TestMethod]
    public async Task RunAsync_TracksIndexFreshness()
    {
        var mockClient = new MockChatClient();
        mockClient.EnqueueToolCallResponse(
            ("tc1", "find_symbol", """{"name":"global::Alpha.BaseService"}"""));
        mockClient.EnqueueTextResponse("Found it.");

        var agent = CreateAgent(mockClient);
        var result = await agent.RunAsync(
            "Find BaseService", null, null, null, "brief", _fixture.Db, CancellationToken.None);

        // IndexFreshness should be set from the tool result's meta
        Assert.IsTrue(result.IndexFreshness > 0);
    }

    public void Dispose() => _fixture.Dispose();
}
