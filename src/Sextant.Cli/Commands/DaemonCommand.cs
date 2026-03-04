using System.CommandLine;

namespace Sextant.Cli.Commands;

internal static class DaemonCommand
{
    public static Command Build(Option<string?> dbOption, Option<string?> profileOption)
    {
        var repoRootOption = new Option<string?>("--repo-root") { Description = "Repository root path" };

        var startCommand = new Command("start", "Start the file-watching daemon") { repoRootOption };
        startCommand.SetAction(async (parseResult) =>
        {
            return await Handlers.DaemonHandler.StartAsync(
                parseResult.GetValue(dbOption),
                parseResult.GetValue(profileOption),
                parseResult.GetValue(repoRootOption));
        });

        var statusCommand = new Command("status", "Check if daemon is running");
        statusCommand.SetAction(async (_) => await Handlers.DaemonHandler.StatusAsync());

        var stopCommand = new Command("stop", "Stop the running daemon");
        stopCommand.SetAction(async (_) => await Handlers.DaemonHandler.StopAsync());

        var command = new Command("daemon", "Manage the file-watching daemon")
        {
            startCommand,
            statusCommand,
            stopCommand,
            repoRootOption
        };

        // "sextant daemon" with no subcommand defaults to start
        command.SetAction(async (parseResult) =>
        {
            return await Handlers.DaemonHandler.StartAsync(
                parseResult.GetValue(dbOption),
                parseResult.GetValue(profileOption),
                parseResult.GetValue(repoRootOption));
        });

        return command;
    }
}
