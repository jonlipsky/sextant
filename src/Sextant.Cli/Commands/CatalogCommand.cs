using System.CommandLine;

namespace Sextant.Cli.Commands;

internal static class CatalogCommand
{
    public static Command Build(Option<string?> dbOption)
    {
        var command = new Command("catalog", "Offline maintenance of a service catalog database");

        var compact = new Command("compact",
            "Write a compacted copy of a catalog with auto_vacuum=INCREMENTAL (offline; the service must be stopped; the input is never modified)");
        var outOption = new Option<string>("--out") { Description = "The new file to write; it must not exist", Required = true };
        var fullOption = new Option<bool>("--full-integrity-check")
        {
            Description = "Run PRAGMA integrity_check instead of the faster quick_check on the copy"
        };
        var countOption = new Option<bool>("--count-rows")
        {
            Description = "Also compare every table's row count between the input and the copy (reads both files fully)"
        };
        compact.Add(outOption);
        compact.Add(fullOption);
        compact.Add(countOption);
        compact.SetAction(parseResult => Handlers.CatalogCompactHandler.Run(
            parseResult.GetValue(dbOption) ?? throw new ArgumentException("--db is required"),
            parseResult.GetRequiredValue(outOption),
            parseResult.GetValue(fullOption),
            parseResult.GetValue(countOption)));
        command.Add(compact);
        return command;
    }
}
