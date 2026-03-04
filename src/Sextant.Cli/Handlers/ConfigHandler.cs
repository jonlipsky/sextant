namespace Sextant.Cli.Handlers;

internal static class ConfigHandler
{
    public static void ShowLlm()
    {
        var config = Mcp.LlmAssist.LlmConfiguration.Load();
        var hasKey = config.ResolveApiKey() != null;

        Console.WriteLine("LLM Assist Configuration:");
        Console.WriteLine($"  Provider:       {config.Provider}");
        Console.WriteLine($"  Model:          {config.Model}");
        Console.WriteLine($"  Base URL:       {config.BaseUrl ?? "(default)"}");
        Console.WriteLine($"  API Key:        {(hasKey ? "(configured)" : "(not set)")}");
        Console.WriteLine($"  API Key Env:    {config.ApiKeyEnv ?? "(not set)"}");
        Console.WriteLine($"  Max Tool Calls: {config.MaxToolCalls}");
        Console.WriteLine($"  Enabled:        {config.Enabled}");
    }

    public static int SetupLlm()
    {
        var config = Mcp.LlmAssist.LlmConfiguration.Load();

        Console.WriteLine("Sextant LLM Configuration");
        Console.WriteLine();

        // Provider
        var provider = Prompt(
            "Provider",
            ["anthropic", "openai-compatible"],
            config.Provider);

        // Model
        var defaultModel = provider == "anthropic" ? "claude-sonnet-4-20250514" : config.Model;
        var model = PromptText("Model", defaultModel);

        // Base URL (only relevant for openai-compatible)
        string? baseUrl = null;
        if (provider == "openai-compatible")
            baseUrl = PromptText("Base URL", config.BaseUrl ?? "https://api.openai.com/v1");

        // API key env var
        var defaultKeyEnv = provider == "anthropic" ? "ANTHROPIC_API_KEY" : (config.ApiKeyEnv ?? "");
        var apiKeyEnv = PromptText("API key environment variable", defaultKeyEnv);
        if (string.IsNullOrWhiteSpace(apiKeyEnv)) apiKeyEnv = null;

        // Verify the key is set
        if (apiKeyEnv != null)
        {
            var keyValue = Environment.GetEnvironmentVariable(apiKeyEnv);
            if (string.IsNullOrEmpty(keyValue))
                Console.WriteLine($"  Warning: ${apiKeyEnv} is not currently set in your environment.");
        }

        // Max tool calls
        var maxToolCallsStr = PromptText("Max tool calls per request", config.MaxToolCalls.ToString());
        if (!int.TryParse(maxToolCallsStr, out var maxToolCalls))
            maxToolCalls = config.MaxToolCalls;

        // Enabled
        var enabled = Prompt("Enable LLM assist?", ["yes", "no"], config.Enabled ? "yes" : "no") == "yes";

        Console.WriteLine();

        return SetLlm(provider, model, baseUrl, apiKeyEnv, maxToolCalls, enabled ? "true" : "false");
    }

    public static int SetLlm(string? provider, string? model, string? baseUrl, string? apiKeyEnv, int? maxToolCalls, string? enabled)
    {
        if (provider == null && model == null && baseUrl == null && apiKeyEnv == null && maxToolCalls == null && enabled == null)
        {
            Console.Error.WriteLine("No options provided. Use --provider, --model, --base-url, --api-key-env, --max-tool-calls, or --enabled.");
            return 1;
        }

        if (provider != null && provider != "anthropic" && provider != "openai-compatible")
        {
            Console.Error.WriteLine($"Invalid provider: '{provider}'. Use 'anthropic' or 'openai-compatible'.");
            return 1;
        }

        var configDir = Mcp.LlmAssist.LlmConfiguration.GlobalConfigDir;
        Directory.CreateDirectory(configDir);
        var configPath = Mcp.LlmAssist.LlmConfiguration.GlobalConfigPath;

        System.Text.Json.Nodes.JsonObject rootObj;
        if (File.Exists(configPath))
        {
            try
            {
                var text = File.ReadAllText(configPath);
                var parsed = System.Text.Json.Nodes.JsonNode.Parse(text, documentOptions: new System.Text.Json.JsonDocumentOptions
                {
                    CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });
                rootObj = parsed?.AsObject() ?? new System.Text.Json.Nodes.JsonObject();
            }
            catch
            {
                rootObj = new System.Text.Json.Nodes.JsonObject();
            }
        }
        else
        {
            rootObj = new System.Text.Json.Nodes.JsonObject();
        }

        if (!rootObj.ContainsKey("llm_assist"))
            rootObj["llm_assist"] = new System.Text.Json.Nodes.JsonObject();
        var llmSection = rootObj["llm_assist"]!.AsObject();

        if (provider != null) llmSection["provider"] = provider;
        if (model != null) llmSection["model"] = model;
        if (baseUrl != null) llmSection["base_url"] = baseUrl;
        if (apiKeyEnv != null) llmSection["api_key_env"] = apiKeyEnv;
        if (maxToolCalls != null) llmSection["max_tool_calls"] = maxToolCalls.Value;
        if (enabled != null) llmSection["enabled"] = enabled.ToLowerInvariant() is "true" or "1";

        var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(configPath, System.Text.Json.JsonSerializer.Serialize(rootObj, options));
        Console.WriteLine($"Saved to {configPath}");
        Console.WriteLine();

        ShowLlm();
        return 0;
    }

    private static string Prompt(string label, string[] choices, string defaultValue)
    {
        var choiceList = string.Join("/", choices.Select(c =>
            c.Equals(defaultValue, StringComparison.OrdinalIgnoreCase) ? c.ToUpperInvariant() : c));

        while (true)
        {
            Console.Write($"  {label} ({choiceList}): ");
            var input = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(input))
                return defaultValue;

            var match = choices.FirstOrDefault(c => c.Equals(input, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match;

            Console.WriteLine($"    Please enter one of: {string.Join(", ", choices)}");
        }
    }

    private static string PromptText(string label, string defaultValue)
    {
        var display = string.IsNullOrEmpty(defaultValue) ? "" : $" [{defaultValue}]";
        Console.Write($"  {label}{display}: ");
        var input = Console.ReadLine()?.Trim();
        return string.IsNullOrEmpty(input) ? defaultValue : input;
    }
}
