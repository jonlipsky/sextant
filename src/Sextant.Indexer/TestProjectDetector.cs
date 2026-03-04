using Microsoft.CodeAnalysis;

namespace Sextant.Indexer;

public static class TestProjectDetector
{
    private static readonly HashSet<string> TestFrameworkPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.NET.Test.Sdk",
        "xunit",
        "xunit.core",
        "NUnit",
        "nunit",
        "MSTest.TestFramework"
    };

    public static bool IsTestProject(Project project)
    {
        if (project.MetadataReferences == null)
            return false;

        // Check metadata references for test framework assemblies
        foreach (var reference in project.MetadataReferences)
        {
            var display = reference.Display;
            if (display == null) continue;

            foreach (var framework in TestFrameworkPackages)
            {
                if (display.Contains(framework, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }
}
