using System.CommandLine;
using Microsoft.Build.Locator;
using Sextant.Cli.Commands;

// MSBuildLocator must be called before any Roslyn types are loaded
MSBuildLocator.RegisterDefaults();

Sextant.Core.SextantConfiguration.MigrateLegacyIfNeeded();

// Global options shared across commands
var dbOption = new Option<string?>("--db") { Description = "Path to the SQLite database", Recursive = true };
var profileOption = new Option<string?>("--profile", "-p") { Description = "Named index profile (default: 'default')", Recursive = true };

var rootCommand = new RootCommand("Sextant — Roslyn-based semantic code indexer for .NET")
{
    dbOption,
    profileOption,
    IndexCommand.Build(dbOption, profileOption),
    QueryCommand.Build(dbOption, profileOption),
    ServeCommand.Build(dbOption, profileOption),
    DaemonCommand.Build(dbOption, profileOption),
    InstallCommand.Build(dbOption, profileOption),
    UninstallCommand.Build(),
    ProfilesCommand.Build(profileOption),
    ConfigCommand.Build()
};

// Legacy: treat bare .sln path as "index <path>"
rootCommand.SetAction(async (parseResult) =>
{
    var cliArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
    if (cliArgs.Length > 0 && cliArgs[0].EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
    {
        return await Sextant.Cli.Handlers.IndexHandler.RunAsync(cliArgs[0], null, null);
    }

    // No command given — show help
    new System.CommandLine.Help.HelpAction().Invoke(parseResult);
    return 1;
});

var parseResult = rootCommand.Parse(args);
return await parseResult.InvokeAsync();
