using System.CommandLine;

namespace Sextant.Cli.Commands;

internal static class InstallCommand
{
    public static Command Build(Option<string?> dbOption, Option<string?> profileOption)
    {
        var toolArg = new Argument<string>("tool") { Description = "Tool to install MCP config for (claude-code, cursor, copilot, vscode, codex, opencode)" };

        var command = new Command("install", "Install MCP config for a tool") { toolArg };

        command.SetAction((parseResult) =>
        {
            var db = parseResult.GetValue(dbOption);
            var profile = parseResult.GetValue(profileOption);

            if (db != null && profile != null)
            {
                Console.Error.WriteLine("Error: --db and --profile are mutually exclusive.");
                return 1;
            }

            try
            {
                Handlers.McpInstaller.Install(parseResult.GetValue(toolArg)!, db, profile);
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
