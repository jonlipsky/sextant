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
        Sextant.TestSupport.TestTempRoot.Initialize();
        await IntegrationFixture.InitializeAsync();
    }

    [AssemblyCleanup]
    public static async Task AssemblyCleanup()
    {
        try
        {
            await IntegrationFixture.DisposeAsync();
        }
        finally
        {
            Sextant.TestSupport.TestTempRoot.Cleanup();
        }
    }
}
