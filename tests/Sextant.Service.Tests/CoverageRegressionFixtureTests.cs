using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Sextant.Core;
using Sextant.Indexer;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #119 regression fixture: the elevenworks/monorepo shape that was indexed 5 of 415 projects yet
/// reported COMPLETE. A real git "remote" (file:// — no network) with TWO solutions, a project file only the
/// second solution declares, and a declared-but-uninitialized submodule is cloned through the real
/// <see cref="CloningCheckoutProvider"/>; the real selection + real checkout inventory then feed the
/// coverage verdict. The MSBuild load is synthetic (the selected solution's one project loaded) so the test
/// runs on every <c>dotnet test</c>; the real-MSBuild union load is covered by the env-gated
/// <c>MultiSolutionIndexingIntegrationTests</c>.
/// </summary>
[TestClass]
public class CoverageRegressionFixtureTests
{
    private readonly List<string> _tempDirs = [];

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal); // git object files are read-only on Windows
                Directory.Delete(dir, recursive: true);
            }
            catch { /* best-effort temp cleanup */ }
        }
    }

    [TestMethod]
    public void TwoSolutionsAndAnUninitializedSubmodule_ClonedCheckout_IsPartialWithEveryReason()
    {
        var (remoteUrl, commit) = NewMonorepoRemote();
        var dataRoot = Temp("sextant_covfix_data");
        var paths = new ServicePaths(ServiceVolumes.Rooted(dataRoot));
        var provider = new CloningCheckoutProvider(new PersistentVolumeCheckoutProvider(paths), paths);

        Assert.IsTrue(provider.TryResolve(ServiceTestFixtures.Request(remoteUrl, commit), out var resolution));
        Assert.AreEqual(SolutionSelectionSource.DefaultRoot, resolution.Source);
        Assert.AreEqual(1, resolution.SelectedSolutions.Count, "no sextant.json ⇒ the default picks one solution");
        Assert.AreEqual(1, resolution.DiscoveredButNotSelected.Count, "the other solution is discovered, not selected");

        var checkout = resolution.CheckoutDir;
        var selected = resolution.SelectedSolutions[0];
        var declaredProject = Path.GetFileName(selected) == "App.slnx"
            ? Path.Combine(checkout, "src", "App", "App.csproj")
            : Path.Combine(checkout, "tools", "Tool", "Tool.csproj");
        var load = new MultiSolutionLoadResult(
            new AdhocWorkspace().CurrentSolution, [], [new SolutionCoverage(selected, 1, 1, [])])
        {
            DeclaredProjects = [declaredProject]
        };

        var inventory = SnapshotCoverageBuilder.Inventory.Scan(checkout);
        var coverage = SnapshotCoverageBuilder.Build(checkout, resolution, load, inventory);
        var result = LocalIndexerSnapshotWorker.BuildResult(99, checkout, resolution, load, coverage);

        Assert.AreEqual(SnapshotJobStatus.Partial, result.Status, "the #119 shape must never be reported complete");
        Assert.AreEqual(99, result.SnapshotId, "the partial snapshot is still published and served");

        var c = result.Coverage!;
        Assert.AreEqual(SnapshotCoverageVerdict.Partial, c.Verdict);
        Assert.AreEqual(2, c.SolutionsDiscovered);
        Assert.AreEqual(1, c.SolutionsNotSelected);
        Assert.AreEqual(2, c.ProjectFilesOnDisk);
        Assert.AreEqual(1, c.ProjectFilesUnreferenced, "the other solution's project is on disk but not indexed");
        Assert.AreEqual(1, c.SubmodulesDeclared);
        Assert.AreEqual(1, c.SubmodulesUnpopulated, "a plain clone never initializes submodules");
        Assert.AreEqual(0, c.ScanErrors);
        Assert.AreEqual(3, c.Reasons.Count, "one reason per gap: unselected solution, unpopulated submodule, orphan project");

        Assert.IsTrue(result.Projects.Any(p => p.Code == "solution_not_selected"));
        Assert.IsTrue(result.Projects.Any(p => p.Code == "submodule_unpopulated" && p.ProjectPath == "libs/shared"));
        Assert.IsTrue(result.Projects.Any(p => p.Code == "project_file_unreferenced"));
        StringAssert.Contains(result.Error, "snapshot coverage is partial");
    }

    // A monorepo-shaped remote: App.slnx → src/App/App.csproj, Tools.slnx → tools/Tool/Tool.csproj, and a
    // gitlink at libs/shared declared in .gitmodules (never initialized, exactly like a fresh clone).
    private (string url, string commit) NewMonorepoRemote()
    {
        var repo = Temp("sextant_covfix_remote");
        Write(repo, "App.slnx", "<Solution>\n  <Project Path=\"src/App/App.csproj\" />\n</Solution>\n");
        Write(repo, "Tools.slnx", "<Solution>\n  <Project Path=\"tools/Tool/Tool.csproj\" />\n</Solution>\n");
        Write(repo, "src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        Write(repo, "tools/Tool/Tool.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        Write(repo, ".gitmodules",
            "[submodule \"libs/shared\"]\n\tpath = libs/shared\n\turl = https://example.invalid/shared.git\n");

        Git(repo, "init", "--quiet", "--initial-branch", "main");
        Git(repo, "config", "user.email", "test@example.com");
        Git(repo, "config", "user.name", "Sextant Test");
        Git(repo, "config", "commit.gpgsign", "false");
        Git(repo, "config", "uploadpack.allowReachableSHA1InWant", "true");
        Git(repo, "config", "uploadpack.allowAnySHA1InWant", "true");
        Git(repo, "add", "-A");
        Git(repo, "update-index", "--add", "--cacheinfo", $"160000,{new string('1', 40)},libs/shared");
        Git(repo, "commit", "--quiet", "-m", "monorepo");

        return (new Uri(repo).AbsoluteUri, GitOut(repo, "rev-parse", "HEAD").Trim());
    }

    private string Temp(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static void Write(string root, string relative, string content)
    {
        var full = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static void Git(string dir, params string[] args) => GitOut(dir, args);

    private static string GitOut(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.AreEqual(0, p.ExitCode, $"git {string.Join(' ', args)} failed: {stderr}");
        return stdout;
    }
}
