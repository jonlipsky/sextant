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
            CorpusRestore.Restore(Path.Combine(goodDir, "Good.csproj"));

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

    [TestMethod]
    public async Task ALoadThatReplaysARestoreWarning_DoesNotDegradeTheProject()
    {
        // The live case: an authenticated feed's first 401 (before the credential provider answered) was recorded
        // as a Warning in project.assets.json. The design-time load replays it, MSBuildWorkspace reports it as a
        // Failure, and a project whose packages restored and whose code loaded was reported degraded.
        var root = NewTempDir("replayed-warning");
        try
        {
            var slnx = await StrictFixtureAsync(root, "Warning");

            // Control: a plain workspace does report the replayed warning as a Failure, so the fixture is not vacuous.
            Assert.IsTrue((await PlainLoadFailuresAsync(slnx)).Count > 0, "MSBuildWorkspace reports the warning as a failure");

            var diagnostics = new List<string>();
            var result = await SolutionLoader.LoadSolutionResilientlyAsync(slnx, diagnostics.Add);

            var loaded = result.Solution.Projects.SingleOrDefault(p =>
                string.Equals(Path.GetFileName(p.FilePath), "Strict.csproj", StringComparison.OrdinalIgnoreCase));
            Assert.IsNotNull(loaded, string.Join("\n", diagnostics));
            Assert.IsFalse(result.IsPartial, string.Join("\n", diagnostics));
            Assert.AreEqual(0, result.DegradedProjects.Count);
            Assert.AreEqual(0, result.UnattributedFailureCount);
            Assert.IsTrue(diagnostics.Any(d => d.Contains("not a load failure", StringComparison.Ordinal)),
                "the replayed warning is still reported, as a warning");
            var compilation = await loaded.GetCompilationAsync();
            Assert.IsNotNull(compilation!.GetTypeByMetadataName("Strict.Strict1"));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task ALoadThatReplaysARestoreError_StillDegradesTheProject()
    {
        var root = NewTempDir("replayed-error");
        try
        {
            var slnx = await StrictFixtureAsync(root, "Error");

            var diagnostics = new List<string>();
            var result = await SolutionLoader.LoadSolutionResilientlyAsync(slnx, diagnostics.Add);

            Assert.IsTrue(result.IsPartial, string.Join("\n", diagnostics));
            Assert.IsTrue(result.DegradedProjects.Any(d => Path.GetFileName(d.ProjectPath) == "Strict.csproj")
                          || result.SkippedProjects.Any(s => s.ProjectName == "Strict.csproj"),
                string.Join("\n", diagnostics));
        }
        finally
        {
            TryDelete(root);
        }
    }

    // A restored one-project solution whose project.assets.json records one restore log entry at `level`.
    private static Task<string> StrictFixtureAsync(string root, string level)
    {
        var dir = Path.Combine(root, "Strict");
        Directory.CreateDirectory(dir);
        var project = Path.Combine(dir, "Strict.csproj");
        File.WriteAllText(project,
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
            "  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>\n" +
            "</Project>\n");
        File.WriteAllText(Path.Combine(dir, "Strict1.cs"),
            "namespace Strict;\npublic class Strict1 { public int Answer() => 42; }\n");
        var slnx = Path.Combine(root, "Strict.slnx");
        File.WriteAllText(slnx, "<Solution>\n  <Project Path=\"Strict/Strict.csproj\" />\n</Solution>\n");
        CorpusRestore.Restore(project);

        var assetsPath = Path.Combine(dir, "obj", "project.assets.json");
        var assets = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(assetsPath))!.AsObject();
        assets["logs"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
        {
            ["code"] = "NU1801",
            ["level"] = level,
            ["warningLevel"] = 1,
            ["message"] = "Your request could not be authenticated by the package source."
        });
        File.WriteAllText(assetsPath, assets.ToJsonString());
        return Task.FromResult(slnx);
    }

    private static async Task<List<string>> PlainLoadFailuresAsync(string slnx)
    {
        using var plain = Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace.Create();
        var failures = new List<string>();
        plain.RegisterWorkspaceFailedHandler(e =>
        {
            if (e.Diagnostic.Kind == Microsoft.CodeAnalysis.WorkspaceDiagnosticKind.Failure)
                failures.Add(e.Diagnostic.Message);
        });
        await plain.OpenSolutionAsync(slnx);
        return failures;
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
