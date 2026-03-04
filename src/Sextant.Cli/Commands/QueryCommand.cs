using System.CommandLine;

namespace Sextant.Cli.Commands;

internal static class QueryCommand
{
    public static Command Build(Option<string?> dbOption, Option<string?> profileOption)
    {
        var toolArg = new Argument<string>("tool") { Description = "The query tool to run" };
        var toolArgsArg = new Argument<string[]>("tool-args") { Description = "Arguments for the query tool", Arity = ArgumentArity.ZeroOrMore };

        var command = new Command("query", "Query the index") { toolArg, toolArgsArg };

        command.SetAction((parseResult) =>
        {
            return Handlers.QueryHandler.Run(
                parseResult.GetValue(toolArg)!,
                parseResult.GetValue(toolArgsArg) ?? [],
                parseResult.GetValue(dbOption),
                parseResult.GetValue(profileOption));
        });

        return command;
    }
}
