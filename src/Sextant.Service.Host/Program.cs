using Microsoft.Build.Locator;
using Sextant.Service.Host;
using Sextant.Service.Restore;

// Issue #231: NuGet starts this assembly as the restore's credential provider plugin (`-Plugin`) when the service
// runs from it. It loads no MSBuild or Roslyn type, so it answers before MSBuildLocator runs.
if (FeedCredentialPlugin.IsPluginInvocation(args))
    return await FeedCredentialPlugin.RunFromEnvironmentAsync();

// MSBuildLocator MUST run before any Roslyn type loads (the local indexer worker pulls in Roslyn). This is
// the first executable statement in the process, exactly as the CLI does in its Program.cs.
if (!MSBuildLocator.IsRegistered)
    MSBuildLocator.RegisterDefaults();

return await ServiceHostRunner.RunAsync(args);

/// <summary>Exposed so hermetic host tests can reference the entry assembly.</summary>
public partial class Program;
