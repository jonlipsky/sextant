using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Sextant.Store;

namespace Sextant.Mcp.LlmAssist;

public sealed class ResearchAgent
{
    private readonly IChatClient _chatClient;
    private readonly ToolRegistry _toolRegistry;
    private readonly LlmConfiguration _config;

    public ResearchAgent(IChatClient chatClient, ToolRegistry toolRegistry, LlmConfiguration config)
    {
        _chatClient = chatClient;
        _toolRegistry = toolRegistry;
        _config = config;
    }

    public async Task<ResearchResult> RunAsync(
        string question,
        string? projectId,
        string? scope,
        int? maxToolCalls,
        string detailLevel,
        IndexDatabase? db,
        CancellationToken ct)
    {
        var budget = maxToolCalls ?? _config.MaxToolCalls;
        var maxTokens = detailLevel == "detailed" ? 2048 : 1024;

        var systemPrompt = BuildSystemPrompt(scope, projectId, detailLevel, db);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, question)
        };

        var aiTools = _toolRegistry.BuildAiTools();

        var toolCallCount = 0;
        long minFreshness = long.MaxValue;

        while (true)
        {
            var options = new ChatOptions
            {
                MaxOutputTokens = maxTokens,
                Tools = aiTools
            };

            ChatResponse response;
            try
            {
                response = await _chatClient.GetResponseAsync(messages, options, ct);
            }
            catch (Exception ex)
            {
                return new ResearchResult(
                    $"LLM API error: {ex.Message}",
                    [],
                    toolCallCount,
                    _config.Model,
                    minFreshness == long.MaxValue ? 0 : minFreshness,
                    false
                );
            }

            // Check if the response contains tool calls
            var responseToolCalls = response.Messages
                .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
                .ToList();

            if (responseToolCalls.Count == 0)
            {
                // Final answer — extract text from the response
                var answer = ExtractText(response) ?? "(No response from model)";
                var sources = ExtractSources(answer, db);
                return new ResearchResult(
                    answer,
                    sources,
                    toolCallCount,
                    _config.Model,
                    minFreshness == long.MaxValue ? 0 : minFreshness,
                    false
                );
            }

            // Add the assistant response messages to conversation
            messages.AddRange(response.Messages);

            // Dispatch tool calls and add results
            var toolResultMessage = new ChatMessage(ChatRole.Tool, (string?)null);
            foreach (var fc in responseToolCalls)
            {
                var argsJson = fc.Arguments != null
                    ? JsonSerializer.Serialize(fc.Arguments)
                    : "{}";
                var result = _toolRegistry.Dispatch(fc.Name, argsJson);
                toolResultMessage.Contents.Add(new FunctionResultContent(fc.CallId, result));
                toolCallCount++;
                TrackFreshness(result, ref minFreshness);
            }
            messages.Add(toolResultMessage);

            // Check budget
            if (toolCallCount >= budget)
            {
                messages.Add(new ChatMessage(ChatRole.User,
                    "Tool call budget exhausted. Summarize your findings now based on what you have gathered so far."));

                var summaryOptions = new ChatOptions
                {
                    MaxOutputTokens = maxTokens
                    // No tools — force text response
                };

                try
                {
                    var finalResponse = await _chatClient.GetResponseAsync(messages, summaryOptions, ct);
                    var answer = ExtractText(finalResponse) ?? "(No response from model)";
                    var sources = ExtractSources(answer, db);
                    return new ResearchResult(
                        answer,
                        sources,
                        toolCallCount,
                        _config.Model,
                        minFreshness == long.MaxValue ? 0 : minFreshness,
                        true
                    );
                }
                catch (Exception ex)
                {
                    return new ResearchResult(
                        $"LLM API error during summarization: {ex.Message}",
                        [],
                        toolCallCount,
                        _config.Model,
                        minFreshness == long.MaxValue ? 0 : minFreshness,
                        true
                    );
                }
            }
        }
    }

    private static string? ExtractText(ChatResponse response)
    {
        var texts = response.Messages
            .SelectMany(m => m.Contents.OfType<TextContent>())
            .Select(t => t.Text)
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();

        return texts.Count > 0 ? string.Join("\n", texts) : null;
    }

    private static string BuildSystemPrompt(string? scope, string? projectId, string detailLevel, IndexDatabase? db)
    {
        var scopeConstraint = "No scope constraint — search across the entire indexed codebase.";
        if (scope != null)
            scopeConstraint = $"Scope constraint: {scope}";
        else if (projectId != null)
            scopeConstraint = $"Scope constraint: project:{projectId}";

        var detailInstruction = detailLevel == "detailed"
            ? "Provide a thorough explanation with full context."
            : "Keep your answer to 2-4 paragraphs maximum.";

        var contextInfo = "";
        if (db != null)
        {
            try
            {
                var conn = db.GetConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM symbols";
                var symbolCount = (long)(cmd.ExecuteScalar() ?? 0);
                var projectStore = new ProjectStore(conn);
                var projectCount = projectStore.GetAll().Count;
                contextInfo = $"\nAvailable context: The index covers {projectCount} projects with {symbolCount} symbols.";
            }
            catch
            {
                // Non-critical — skip context info
            }
        }

        return $"""
            You are a code research assistant with access to a semantic index of a .NET codebase.

            Your job is to answer the user's question by querying the index using the tools provided.
            Be thorough but efficient — use the minimum tool calls needed to answer confidently.

            Rules:
            - Always cite specific symbols by their fully qualified name (FQN)
            - Include file paths and line numbers when referencing code
            - If you find the answer early, stop and respond — don't exhaustively search
            - If the question is ambiguous, state your interpretation and answer that
            - Be concise: aim for a clear, direct answer
            - {scopeConstraint}

            {detailInstruction}
            {contextInfo}
            """;
    }

    internal static List<SourceReference> ExtractSources(string answer, IndexDatabase? db)
    {
        if (db == null)
            return [];

        var sources = new List<SourceReference>();
        var seen = new HashSet<string>();

        var fqnPattern = new Regex(@"global::[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)+(?:\([^)]*\))?", RegexOptions.Compiled);
        var matches = fqnPattern.Matches(answer);

        var conn = db.GetConnection();
        var symbolStore = new SymbolStore(conn);

        foreach (Match match in matches)
        {
            var fqn = match.Value;
            var lookupFqn = fqn.Contains('(') ? fqn[..fqn.IndexOf('(')] : fqn;

            if (!seen.Add(lookupFqn))
                continue;

            var symbol = symbolStore.GetByFqn(lookupFqn);
            if (symbol != null)
            {
                sources.Add(new SourceReference(
                    symbol.FullyQualifiedName,
                    symbol.FilePath,
                    symbol.LineStart
                ));
            }
        }

        return sources;
    }

    private static void TrackFreshness(string toolResultJson, ref long minFreshness)
    {
        try
        {
            using var doc = JsonDocument.Parse(toolResultJson);
            if (doc.RootElement.TryGetProperty("meta", out var meta) &&
                meta.TryGetProperty("index_freshness", out var freshness))
            {
                var val = freshness.GetInt64();
                if (val > 0 && val < minFreshness)
                    minFreshness = val;
            }
        }
        catch
        {
            // Non-critical
        }
    }
}

public record ResearchResult(
    string Answer,
    List<SourceReference> Sources,
    int ToolCallsUsed,
    string Model,
    long IndexFreshness,
    bool Truncated
);

public record SourceReference(string Fqn, string File, int Line);
