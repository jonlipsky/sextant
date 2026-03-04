using System.CommandLine;

namespace Sextant.Cli.Commands;

internal static class IndexCommand
{
    public static Command Build(Option<string?> dbOption, Option<string?> profileOption)
    {
        var solutionArg = new Argument<string>("solution-path") { Description = "Path to the .sln file to index" };
        var command = new Command("index", "Index a .sln file") { solutionArg };

        command.SetAction(async (parseResult) =>
        {
            return await Handlers.IndexHandler.RunAsync(
                parseResult.GetValue(solutionArg)!,
                parseResult.GetValue(dbOption),
                parseResult.GetValue(profileOption));
        });

        return command;
    }
}
