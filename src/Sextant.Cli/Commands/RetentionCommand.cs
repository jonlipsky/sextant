using System.CommandLine;

namespace Sextant.Cli.Commands;

internal static class RetentionCommand
{
    public static Command Build(Option<string?> dbOption, Option<string?> profileOption)
    {
        var command = new Command("retention",
            "Report and apply retention for superseded generations, API history, and source blobs");

        var executeOption = new Option<bool>("--execute")
        {
            Description = "Apply retention. Without this flag the command is a dry-run."
        };
        command.Add(executeOption);

        command.SetAction((parseResult) =>
            Handlers.RetentionHandler.Run(
                parseResult.GetValue(dbOption),
                parseResult.GetValue(profileOption),
                parseResult.GetValue(executeOption)));

        return command;
    }
}
