using Microsoft.Extensions.AI;

namespace Sextant.Mcp.Tests.LlmAssist;

internal sealed class MockChatClient : IChatClient
{
    private readonly Queue<ChatResponse> _responses = new();

    public List<(IList<ChatMessage> Messages, ChatOptions? Options)> Calls { get; } = [];

    public void EnqueueResponse(ChatResponse response)
    {
        _responses.Enqueue(response);
    }

    public void EnqueueTextResponse(string text)
    {
        var msg = new ChatMessage(ChatRole.Assistant, text);
        _responses.Enqueue(new ChatResponse([msg])
        {
            FinishReason = ChatFinishReason.Stop
        });
    }

    public void EnqueueToolCallResponse(params (string id, string name, string argsJson)[] toolCalls)
    {
        var contents = new List<AIContent>();
        foreach (var (id, name, argsJson) in toolCalls)
        {
            var args = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson);
            contents.Add(new FunctionCallContent(id, name, args));
        }

        var msg = new ChatMessage(ChatRole.Assistant, contents);
        _responses.Enqueue(new ChatResponse([msg])
        {
            FinishReason = ChatFinishReason.ToolCalls
        });
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Calls.Add((chatMessages.ToList(), options));

        if (_responses.Count == 0)
            throw new InvalidOperationException("No more mock responses enqueued.");

        return Task.FromResult(_responses.Dequeue());
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
