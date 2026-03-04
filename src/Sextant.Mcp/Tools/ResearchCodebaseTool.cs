using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using Sextant.Mcp.LlmAssist;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class ResearchCodebaseTool
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    [McpServerTool(Name = "research_codebase"), Description(
        "Ask a natural language question about the indexed codebase. " +
        "An LLM agent will research the answer using the semantic index and return a synthesized response. " +
        "Use this for exploratory questions instead of chaining multiple tool calls.")]
    public static async Task<string> ResearchCodebase(
        DatabaseProvider dbProvider,
        [Description("The natural language question about the codebase")] string question,
        [Description("Optional project canonical ID to scope the research")] string? project_id = null,
        [Description("Scope filter: 'file:/path', 'project:canonical_id', 'solution:/path', or 'all'")] string? scope = null,
        [Description("Maximum number of tool calls the research agent can make (default: 15)")] int? max_tool_calls = null,
        [Description("Response detail level: 'brief' (default) or 'detailed'")] string? detail_level = null,
        CancellationToken cancellationToken = default)
    {
        var db = dbProvider.GetDatabase();
        if (db == null)
            return ResponseBuilder.BuildEmpty("No index database found.");

        LlmConfiguration config;
        try
        {
            config = LlmConfiguration.Load();
        }
        catch (Exception ex)
        {
            return SerializeError($"Failed to load LLM configuration: {ex.Message}");
        }

        if (!config.Enabled)
            return SerializeError("LLM assist is disabled in configuration.");

        Microsoft.Extensions.AI.IChatClient chatClient;
        try
        {
            chatClient = ChatClientFactory.Create(config);
        }
        catch (InvalidOperationException ex)
        {
            return SerializeError(ex.Message);
        }

        var toolRegistry = new ToolRegistry(dbProvider);
        var agent = new ResearchAgent(chatClient, toolRegistry, config);

        var level = detail_level?.ToLowerInvariant() ?? "brief";
        if (level != "brief" && level != "detailed")
            level = "brief";

        var result = await agent.RunAsync(
            question,
            project_id,
            scope,
            max_tool_calls,
            level,
            db,
            cancellationToken
        );

        var response = new
        {
            Meta = new
            {
                QueriedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                IndexFreshness = result.IndexFreshness,
                ToolCallsUsed = result.ToolCallsUsed,
                Model = result.Model,
                Truncated = result.Truncated ? true : (bool?)null
            },
            Answer = result.Answer,
            Sources = result.Sources.Select(s => new
            {
                Fqn = s.Fqn,
                File = s.File,
                Line = s.Line
            }).ToList()
        };

        return JsonSerializer.Serialize(response, JsonOptions);
    }

    private static string SerializeError(string message)
    {
        return JsonSerializer.Serialize(new { error = message }, JsonOptions);
    }
}
