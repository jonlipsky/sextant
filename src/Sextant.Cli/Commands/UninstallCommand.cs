using System.CommandLine;

namespace Sextant.Cli.Commands;

internal static class UninstallCommand
{
    public static Command Build()
    {
        var toolArg = new Argument<string>("tool") { Description = "Tool to remove MCP config for (claude-code, cursor, copilot, vscode, codex, opencode)" };

        var command = new Command("uninstall", "Remove MCP config for a tool") { toolArg };

        command.SetAction((parseResult) =>
        {
            try
            {
                Handlers.McpInstaller.Uninstall(parseResult.GetValue(toolArg)!);
                return 0;
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        });

        return command;
    }
}
