using System.Text.Json;
using System.Text.Json.Serialization;
using Sextant.Core;

namespace Sextant.Mcp.LlmAssist;

public sealed class LlmConfiguration
{
    public string Provider { get; set; } = "openai-compatible";
    public string Model { get; set; } = "gpt-4o";
    public string? BaseUrl { get; set; }
    public string? ApiKey { get; set; }
    public string? ApiKeyEnv { get; set; }
    public int MaxToolCalls { get; set; } = 15;
    public bool Enabled { get; set; } = true;

    public string? ResolveApiKey()
    {
        // Direct env var override takes highest priority
        var directKey = Environment.GetEnvironmentVariable("SEXTANT_LLM_API_KEY");
        if (!string.IsNullOrEmpty(directKey))
            return directKey;

        // Direct value from config
        if (!string.IsNullOrEmpty(ApiKey))
            return ApiKey;

        // Indirection: read the key from the env var named by ApiKeyEnv
        var keyEnvName = Environment.GetEnvironmentVariable("SEXTANT_LLM_API_KEY_ENV") ?? ApiKeyEnv;
        if (!string.IsNullOrEmpty(keyEnvName))
        {
            var key = Environment.GetEnvironmentVariable(keyEnvName);
            if (!string.IsNullOrEmpty(key))
                return key;
        }

        return null;
    }

    public static string GlobalConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sextant");

    public static string GlobalConfigPath =>
        Path.Combine(GlobalConfigDir, "sextant.json");

    public static LlmConfiguration Load(string? repoRoot = null)
    {
        var config = new LlmConfiguration();

        // Load from ~/.sextant/sextant.json (global user config)
        var configPath = GlobalConfigPath;
        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });

                if (doc.RootElement.TryGetProperty("llm_assist", out var section))
                {
                    var fileConfig = JsonSerializer.Deserialize<LlmConfigFile>(
                        section.GetRawText(), JsonOptions);

                    if (fileConfig != null)
                    {
                        if (fileConfig.Provider != null) config.Provider = fileConfig.Provider;
                        if (fileConfig.Model != null) config.Model = fileConfig.Model;
                        if (fileConfig.BaseUrl != null) config.BaseUrl = fileConfig.BaseUrl;
                        if (fileConfig.ApiKey != null) config.ApiKey = fileConfig.ApiKey;
                        if (fileConfig.ApiKeyEnv != null) config.ApiKeyEnv = fileConfig.ApiKeyEnv;
                        if (fileConfig.MaxToolCalls.HasValue) config.MaxToolCalls = fileConfig.MaxToolCalls.Value;
                        if (fileConfig.Enabled.HasValue) config.Enabled = fileConfig.Enabled.Value;
                    }
                }
            }
            catch (JsonException)
            {
                // Invalid JSON — fall through to defaults + env vars
            }
        }

        // Environment variables override file settings
        ApplyEnvironmentVariables(config);

        return config;
    }

    private static void ApplyEnvironmentVariables(LlmConfiguration config)
    {
        var provider = Environment.GetEnvironmentVariable("SEXTANT_LLM_PROVIDER");
        if (!string.IsNullOrEmpty(provider))
            config.Provider = provider;

        var model = Environment.GetEnvironmentVariable("SEXTANT_LLM_MODEL");
        if (!string.IsNullOrEmpty(model))
            config.Model = model;

        var baseUrl = Environment.GetEnvironmentVariable("SEXTANT_LLM_BASE_URL");
        if (!string.IsNullOrEmpty(baseUrl))
            config.BaseUrl = baseUrl;

        var maxCalls = Environment.GetEnvironmentVariable("SEXTANT_LLM_MAX_CALLS");
        if (int.TryParse(maxCalls, out var calls))
            config.MaxToolCalls = calls;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private sealed class LlmConfigFile
    {
        [JsonPropertyName("provider")]
        public string? Provider { get; set; }

        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("base_url")]
        public string? BaseUrl { get; set; }

        [JsonPropertyName("api_key")]
        public string? ApiKey { get; set; }

        [JsonPropertyName("api_key_env")]
        public string? ApiKeyEnv { get; set; }

        [JsonPropertyName("max_tool_calls")]
        public int? MaxToolCalls { get; set; }

        [JsonPropertyName("enabled")]
        public bool? Enabled { get; set; }
    }
}
