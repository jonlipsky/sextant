using System.CommandLine;

namespace Sextant.Cli.Commands;

internal static class ConfigCommand
{
    public static Command Build()
    {
        var providerOption = new Option<string?>("--provider") { Description = "LLM provider ('anthropic' or 'openai-compatible')" };
        var modelOption = new Option<string?>("--model") { Description = "Model name" };
        var baseUrlOption = new Option<string?>("--base-url") { Description = "API base URL" };
        var apiKeyEnvOption = new Option<string?>("--api-key-env") { Description = "Environment variable name containing API key" };
        var maxToolCallsOption = new Option<int?>("--max-tool-calls") { Description = "Max tool calls per research request" };
        var enabledOption = new Option<string?>("--enabled") { Description = "Enable or disable LLM assist (true/false)" };

        var setCommand = new Command("set", "Set LLM configuration values")
        {
            providerOption, modelOption, baseUrlOption,
            apiKeyEnvOption, maxToolCallsOption, enabledOption
        };

        setCommand.SetAction((parseResult) =>
        {
            return Handlers.ConfigHandler.SetLlm(
                parseResult.GetValue(providerOption),
                parseResult.GetValue(modelOption),
                parseResult.GetValue(baseUrlOption),
                parseResult.GetValue(apiKeyEnvOption),
                parseResult.GetValue(maxToolCallsOption),
                parseResult.GetValue(enabledOption));
        });

        var showOption = new Option<bool>("--show") { Description = "Show current configuration without interactive setup" };

        var llmCommand = new Command("llm", "Configure LLM assist provider") { setCommand, showOption };

        llmCommand.SetAction((parseResult) =>
        {
            if (parseResult.GetValue(showOption))
                Handlers.ConfigHandler.ShowLlm();
            else
                return Handlers.ConfigHandler.SetupLlm();
            return 0;
        });

        var command = new Command("config", "Configuration management") { llmCommand };

        return command;
    }
}
