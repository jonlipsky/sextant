using System.Runtime.CompilerServices;
using Microsoft.Build.Locator;

namespace Sextant.Benchmarks.Tests;

internal static class MsBuildInit
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (!MSBuildLocator.IsRegistered)
            MSBuildLocator.RegisterDefaults();
    }
}
