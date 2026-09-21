using Microsoft.Build.Locator;
using Sextant.Service.Host;

// MSBuildLocator MUST run before any Roslyn type loads (the local indexer worker pulls in Roslyn). This is
// the first executable statement in the process, exactly as the CLI does in its Program.cs.
if (!MSBuildLocator.IsRegistered)
    MSBuildLocator.RegisterDefaults();

return await ServiceHostRunner.RunAsync(args);

/// <summary>Exposed so hermetic host tests can reference the entry assembly.</summary>
public partial class Program;
