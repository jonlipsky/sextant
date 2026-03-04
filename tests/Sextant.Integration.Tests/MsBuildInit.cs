using System.Runtime.CompilerServices;
using Microsoft.Build.Locator;

namespace Sextant.Integration.Tests;

internal static class MsBuildInit
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (!MSBuildLocator.IsRegistered)
            MSBuildLocator.RegisterDefaults();
    }
}

[TestClass]
public static class AssemblySetup
{
    [AssemblyInitialize]
    public static async Task AssemblyInit(TestContext _)
    {
        await IntegrationFixture.InitializeAsync();
    }

    [AssemblyCleanup]
    public static async Task AssemblyCleanup()
    {
        await IntegrationFixture.DisposeAsync();
    }
}
