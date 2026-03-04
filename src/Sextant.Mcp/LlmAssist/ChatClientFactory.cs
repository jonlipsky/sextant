using System.ClientModel;
using Anthropic;
using Anthropic.Core;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Sextant.Mcp.LlmAssist;

public static class ChatClientFactory
{
    public static IChatClient Create(LlmConfiguration config)
    {
        var apiKey = config.ResolveApiKey()
            ?? throw new InvalidOperationException(
                "LLM assist not configured. Set SEXTANT_LLM_API_KEY or run 'sextant config llm setup' to configure ~/.sextant/sextant.json.");

        return config.Provider.ToLowerInvariant() switch
        {
            "anthropic" => CreateAnthropicClient(apiKey, config.Model),
            "openai-compatible" => CreateOpenAiClient(apiKey, config.Model, config.BaseUrl),
            _ => throw new InvalidOperationException(
                $"Unknown LLM provider: '{config.Provider}'. Use 'anthropic' or 'openai-compatible'.")
        };
    }

    private static IChatClient CreateAnthropicClient(string apiKey, string model)
    {
        var client = new AnthropicClient(new ClientOptions())
            .WithOptions(opts => { opts.ApiKey = apiKey; return opts; });
        return client.AsIChatClient(model);
    }

    private static IChatClient CreateOpenAiClient(string apiKey, string model, string? baseUrl)
    {
        var credential = new ApiKeyCredential(apiKey);

        OpenAIClient openAiClient;
        if (baseUrl != null)
        {
            var options = new OpenAIClientOptions { Endpoint = new Uri(baseUrl) };
            openAiClient = new OpenAIClient(credential, options);
        }
        else
        {
            openAiClient = new OpenAIClient(credential);
        }

        return openAiClient.GetChatClient(model).AsIChatClient();
    }
}
