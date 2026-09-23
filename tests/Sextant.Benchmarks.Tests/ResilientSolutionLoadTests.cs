using System.Diagnostics;
using Sextant.Indexer;

namespace Sextant.Benchmarks.Tests;

/// <summary>
/// End-to-end proof of issue #90 against a REAL MSBuildWorkspace load: a solution that mixes a loadable
/// project with an unloadable one must yield a NON-empty solution for the loadable project plus a
/// diagnostic naming the skipped project — never abort into an empty index. Uses malformed project XML
/// to reproduce a project MSBuild cannot evaluate (a lightweight stand-in for the legacy/non-SDK
/// BuildHost crash observed on TouchDraw2.Dev.sln), so the test needs no legacy toolchain.
/// </summary>
[TestClass]
public sealed class ResilientSolutionLoadTests
{
    [TestMethod]
    public async Task PartialSolution_LoadsGoodProject_AndReportsBrokenProject()
    {
        var root = NewTempDir("partial-load");
        try
        {
            // A minimal, loadable SDK project.
            var goodDir = Path.Combine(root, "Good");
            Directory.CreateDirectory(goodDir);
            File.WriteAllText(Path.Combine(goodDir, "Good.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
                "  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>\n" +
                "</Project>\n");
            File.WriteAllText(Path.Combine(goodDir, "Class1.cs"),
                "namespace Good;\npublic class Class1 { public int Answer() => 42; }\n");

            // A project MSBuild cannot evaluate (malformed, unterminated XML).
            var badDir = Path.Combine(root, "Bad");
            Directory.CreateDirectory(badDir);
            File.WriteAllText(Path.Combine(badDir, "Bad.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0\n");

            var slnx = Path.Combine(root, "Mixed.slnx");
            File.WriteAllText(slnx,
                "<Solution>\n" +
                "  <Project Path=\"Good/Good.csproj\" />\n" +
                "  <Project Path=\"Bad/Bad.csproj\" />\n" +
                "</Solution>\n");

            // Restore only the loadable project so it evaluates; restoring the whole (broken) solution
            // would itself fail.
            Restore(Path.Combine(goodDir, "Good.csproj"));

            var diagnostics = new List<string>();
            var result = await SolutionLoader.LoadSolutionResilientlyAsync(slnx, diagnostics.Add);

            // The loadable project produced a real, non-empty solution — not a zero-project abort.
            Assert.IsTrue(
                result.Solution.Projects.Any(p =>
                    string.Equals(Path.GetFileName(p.FilePath), "Good.csproj", StringComparison.OrdinalIgnoreCase)),
                "the loadable project must be present in the solution");

            // The unloadable project is reported as skipped, so the caller knows the index is PARTIAL.
            Assert.IsTrue(result.IsPartial, "the load must be reported as partial");
            Assert.IsTrue(
                result.SkippedProjects.Any(s =>
                    string.Equals(s.ProjectName, "Bad.csproj", StringComparison.OrdinalIgnoreCase)),
                "the broken project must be named in the skipped list");
            Assert.IsTrue(diagnostics.Any(d => d.Contains("Bad.csproj", StringComparison.OrdinalIgnoreCase)),
                "a diagnostic naming the skipped project must be emitted");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void Restore(string projectPath)
    {
        var psi = new ProcessStartInfo("dotnet", $"restore \"{projectPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi)!;
        process.WaitForExit();
    }

    private static string NewTempDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sextant-resilient-{tag}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
