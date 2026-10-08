using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Indexer;
using Sextant.Service;
using Sextant.Store;

namespace Sextant.Integration.Tests;

/// <summary>
/// Issue #124 end-to-end: with NO <c>sextant.json</c> the service worker indexes the deterministic UNION of
/// every discovered solution. Driven through the REAL <see cref="PersistentVolumeCheckoutProvider"/> (real
/// <see cref="SolutionSelector"/>), the REAL <see cref="LocalIndexerSnapshotWorker"/> (real
/// <see cref="MultiSolutionLoader"/> over a real MSBuild toolchain, real coverage, real Phase-9 publish) over
/// a no-config checkout whose three solutions OVERLAP on a multi-targeted shared project:
/// <list type="bullet">
/// <item>the shared project is evaluated and stored ONCE per target framework (not once per solution);</item>
/// <item>a platform-head solution with an unloadable head does NOT fail the snapshot — the loadable union is
/// published, the head is skipped-with-reason, and coverage is honestly <c>partial</c> naming it;</item>
/// <item>an all-loadable multi-solution checkout reports coverage <c>complete</c>.</item>
/// </list>
/// Real <c>dotnet restore</c> runs first; a restore failure (e.g. an offline machine without the
/// netstandard2.0 reference pack cached) makes the test Inconclusive rather than failed.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class DefaultUnionSnapshotIntegrationTests : IDisposable
{
    private const string RemoteUrl = "https://example.invalid/org/union-monorepo.git";

    private readonly string _dataRoot;

    public DefaultUnionSnapshotIntegrationTests()
    {
        _ = IntegrationFixture.Instance; // ensure MSBuildLocator is registered before any Roslyn type loads
        _dataRoot = Path.Combine(Path.GetTempPath(), $"sextant_union_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataRoot);
    }

    public void Dispose() => Sextant.TestSupport.SqliteTestDatabase.DeleteDirectory(_dataRoot);

    [TestMethod]
    public async Task NoConfig_OverlappingSolutionsWithUnloadableHead_PublishesUnionOncePerTfm_PartialWithReason()
    {
        var paths = new ServicePaths(ServiceVolumes.Rooted(_dataRoot));
        var checkout = CreateCheckout(paths, includeUnloadableHead: true);
        var provider = new PersistentVolumeCheckoutProvider(paths);
        var request = Request();

        // --- Selection: the union of all three solutions, deterministic order, nothing left unselected ----
        Assert.IsTrue(provider.TryResolve(request, out var resolution));
        Assert.AreEqual(SolutionSelectionSource.DefaultUnion, resolution.Source);
        CollectionAssert.AreEqual(
            new[] { "App.slnx", "Build.Linux/Tools-server.slnx", "Build.Mac/Mobile-ios.slnx" },
            Relative(checkout, resolution.SelectedSolutions),
            "shallow first, then the Linux-marker head, the platform head last");
        Assert.AreEqual(0, resolution.DiscoveredButNotSelected.Count);

        // --- Produce through the real worker -----------------------------------------------------------
        var (db, config) = OpenCatalog();
        using (db)
        {
            var identityHash = IdentityHash(request, config);
            var result = await new LocalIndexerSnapshotWorker(db, config, provider)
                .ProduceAsync(request, identityHash, paths.AllocateScratch("job-1"), CancellationToken.None);

            // The unloadable head does not fail the snapshot: the loadable union is published, PARTIAL.
            Assert.AreEqual(SnapshotJobStatus.Partial, result.Status, result.Error);
            Assert.IsNotNull(result.SnapshotId, "the loadable union is still published and served");
            StringAssert.Contains(result.Error, "Mobile.iOS/Mobile.iOS.csproj",
                "the coverage reason names the unloadable platform head");
            var skip = result.Projects.Single(p => p.Code == "project_skipped");
            Assert.AreEqual("Mobile.iOS/Mobile.iOS.csproj", skip.ProjectPath?.Replace('\\', '/'));
            Assert.IsFalse(string.IsNullOrWhiteSpace(skip.Message));
            Assert.IsFalse(result.Projects.Any(p => p.Code == "solution_not_selected"));
            Assert.AreEqual(3, result.Projects.Count(p => p.Code == "solution_indexed"));

            // Issue #267: the real worker reports where the job's time went, in execution order.
            var timings = result.Timings;
            Assert.IsNotNull(timings);
            var phaseNames = timings.Phases.Select(p => p.Name).ToList();
            foreach (var (earlier, later) in new[] { ("checkout", "restore"), ("restore", "load"), ("load", "coverage_scan"), ("coverage_scan", "extracting_symbols") })
            {
                Assert.IsGreaterThanOrEqualTo(0, phaseNames.IndexOf(earlier), $"{earlier} recorded: {string.Join(", ", phaseNames)}");
                Assert.IsLessThan(phaseNames.IndexOf(later), phaseNames.IndexOf(earlier), $"{earlier} before {later}: {string.Join(", ", phaseNames)}");
            }
            // Issue #268: the three solutions' union loads in ONE open of a generated solution, even with an unloadable
            // head among them: no project is opened individually, and one BuildHost evaluates them all.
            Assert.AreEqual(SolutionLoadModes.Union, timings.Load!.Mode, "three selected solutions load as a union");
            Assert.AreEqual(0, timings.Load.ProjectsOpened, "the union is not opened project by project");
            Assert.IsGreaterThan(0, timings.Load.ProjectsLoaded);
            if (OperatingSystem.IsLinux())
            {
                Assert.IsGreaterThan(0, timings.Load.BuildHosts!.Launches, "the union load starts BuildHost processes");
                Assert.IsLessThanOrEqualTo(5, timings.Load.BuildHosts.Launches, "one pass, not one BuildHost per project");
            }
            Assert.IsTrue(timings.SlowestProjects.Any(p => p.Phase == "extracting_symbols"));
            Assert.IsGreaterThanOrEqualTo(timings.Phases.Single(p => p.Name == "load").WallMs, timings.TotalMs);

            var coverage = new SnapshotCoverageStore(db.GetConnection()).Get(result.SnapshotId!.Value);
            Assert.IsNotNull(coverage, "coverage is recorded durably with the published snapshot");
            Assert.AreEqual(SnapshotCoverageVerdict.Partial, coverage.Verdict);
            Assert.AreEqual("default_union", coverage.SelectionSource);
            Assert.AreEqual(3, coverage.SolutionsDiscovered);
            Assert.AreEqual(3, coverage.SolutionsSelected);
            Assert.AreEqual(0, coverage.SolutionsNotSelected);
            Assert.AreEqual(4, coverage.ProjectsDeclared, "Core is declared by all three solutions yet counts once");
            Assert.AreEqual(1, coverage.ProjectsSkipped);
            Assert.AreEqual(4, coverage.ProjectFilesOnDisk);
            Assert.AreEqual(0, coverage.ProjectFilesUnreferenced);
            Assert.IsTrue(coverage.Reasons.Any(reason =>
                reason.Contains("Mobile.iOS/Mobile.iOS.csproj", StringComparison.Ordinal)),
                "the unloadable head remains visible as a coverage gap");
            Assert.IsTrue(coverage.Reasons.Any(reason =>
                reason.Contains("package restore was incomplete", StringComparison.Ordinal)),
                "an incomplete best-effort restore is an additional explicit coverage gap");

            AssertSharedProjectStoredOncePerTfm(db.GetConnection(), result.SnapshotId.Value);
            // Solution scope (#124 review): the union workspace has no solution file of its own, yet each
            // selected solution keeps its OWN solution → project mapping; the shared project maps to every
            // solution that declares it (once per TFM), and no solution picks up another's projects.
            var app = SolutionMembers(db.GetConnection(), Path.Combine(checkout, "App.slnx"));
            CollectionAssert.AreEqual(
                new[] { "App.csproj", "Core.csproj", "Core.csproj" }, app, string.Join(", ", app));
            var tools = SolutionMembers(db.GetConnection(), Path.Combine(checkout, "Build.Linux", "Tools-server.slnx"));
            CollectionAssert.AreEqual(
                new[] { "Core.csproj", "Core.csproj", "Tool.csproj" }, tools, string.Join(", ", tools));
            var mobile = SolutionMembers(db.GetConnection(), Path.Combine(checkout, "Build.Mac", "Mobile-ios.slnx"));
            Assert.AreEqual(2, mobile.Count(p => p == "Core.csproj"), string.Join(", ", mobile));
            Assert.IsTrue(mobile.All(p => p is "Core.csproj" or "Mobile.iOS.csproj"),
                "the head solution maps only its own declared projects: " + string.Join(", ", mobile));
            // MSBuild keeps an empty, document-less stub for a project it could not evaluate (#90), so the
            // head may still get a project row; what matters is that it contributes no indexed content.
            Assert.AreEqual(0, ScalarInt(db.GetConnection(), $"""
                SELECT COUNT(*) FROM symbols s
                JOIN snapshot_projects sp ON sp.project_id = s.project_id
                JOIN projects p ON p.id = s.project_id
                WHERE sp.snapshot_id = {result.SnapshotId.Value} AND p.repo_relative_path LIKE '%Mobile.iOS.csproj';
                """), "the unloadable head contributes no symbols to the snapshot");
        }
    }

    [TestMethod]
    public async Task NoConfig_OverlappingSolutions_AllLoadable_IsComplete()
    {
        var paths = new ServicePaths(ServiceVolumes.Rooted(_dataRoot));
        var checkout = CreateCheckout(paths, includeUnloadableHead: false);
        var provider = new PersistentVolumeCheckoutProvider(paths);
        var request = Request();

        Assert.IsTrue(provider.TryResolve(request, out var resolution));
        Assert.AreEqual(SolutionSelectionSource.DefaultUnion, resolution.Source);
        CollectionAssert.AreEqual(
            new[] { "App.slnx", "Build.Linux/Tools-server.slnx" }, Relative(checkout, resolution.SelectedSolutions));

        var (db, config) = OpenCatalog();
        using (db)
        {
            var result = await new LocalIndexerSnapshotWorker(db, config, provider)
                .ProduceAsync(request, IdentityHash(request, config), paths.AllocateScratch("job-1"), CancellationToken.None);

            Assert.AreEqual(SnapshotJobStatus.Complete, result.Status, result.Error);
            Assert.IsNotNull(result.SnapshotId);
            var coverage = new SnapshotCoverageStore(db.GetConnection()).Get(result.SnapshotId!.Value);
            Assert.IsNotNull(coverage);
            Assert.AreEqual(SnapshotCoverageVerdict.Complete, coverage.Verdict,
                "a multi-solution checkout whose projects all load is complete: " + string.Join(" ", coverage.Reasons));
            Assert.AreEqual("default_union", coverage.SelectionSource);
            Assert.AreEqual(2, coverage.SolutionsSelected);
            Assert.AreEqual(0, coverage.SolutionsNotSelected);
            Assert.AreEqual(3, coverage.ProjectsDeclared);
            Assert.AreEqual(0, coverage.ProjectsSkipped);

            AssertSharedProjectStoredOncePerTfm(db.GetConnection(), result.SnapshotId.Value);
        }
    }

    // Core is declared by EVERY solution and multi-targets net10.0;netstandard2.0. Evaluated once per TFM
    // means exactly two project versions (one per TFM) and its type stored once per TFM; App's and Tool's
    // calls into it recorded exactly once each. A per-solution re-index would multiply all of these.
    private static void AssertSharedProjectStoredOncePerTfm(SqliteConnection conn, long snapshotId)
    {
        Assert.AreEqual(2, ScalarInt(conn, SnapshotProjectCount(snapshotId, "%Core.csproj")),
            "the shared multi-targeted project is stored once per target framework, not once per solution");
        Assert.AreEqual(2, ScalarInt(conn, $"""
            SELECT COUNT(DISTINCT p.target_framework) FROM projects p
            JOIN snapshot_projects sp ON sp.project_id = p.id
            WHERE sp.snapshot_id = {snapshotId} AND p.repo_relative_path LIKE '%Core.csproj';
            """), "one version per distinct TFM (net10.0, netstandard2.0)");
        Assert.AreEqual(1, ScalarInt(conn, SnapshotProjectCount(snapshotId, "%App.csproj")));
        Assert.AreEqual(1, ScalarInt(conn, SnapshotProjectCount(snapshotId, "%Tool.csproj")));
        Assert.AreEqual(2, ScalarInt(conn, $"""
            SELECT COUNT(*) FROM symbols s
            JOIN snapshot_projects sp ON sp.project_id = s.project_id
            WHERE sp.snapshot_id = {snapshotId} AND s.display_name = 'CoreType';
            """), "the shared type is indexed once per TFM");
        Assert.AreEqual(2, ScalarInt(conn, $"""
            SELECT COUNT(*) FROM occurrences o
            JOIN symbols t ON t.id = o.target_symbol_id
            JOIN snapshot_projects sp ON sp.project_id = o.in_project_id
            WHERE sp.snapshot_id = {snapshotId} AND t.display_name = 'CoreCompute' AND o.source_symbol_id IS NOT NULL;
            """), "App's and Tool's calls into the shared method are each recorded exactly once");
    }

    private static string SnapshotProjectCount(long snapshotId, string likePattern) => $"""
        SELECT COUNT(*) FROM projects p
        JOIN snapshot_projects sp ON sp.project_id = p.id
        WHERE sp.snapshot_id = {snapshotId} AND p.repo_relative_path LIKE '{likePattern}';
        """;

    // The file names of the projects mapped to one solution (one entry per per-TFM project row). File names,
    // not repo-relative paths: this fixture checkout is not a git repo, so no repo root anchors the path.
    private static string[] SolutionMembers(SqliteConnection conn, string solutionPath)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT p.repo_relative_path FROM solution_projects sp
            JOIN solutions s ON s.id = sp.solution_id
            JOIN projects p ON p.id = sp.project_id
            WHERE s.file_path = @path;
            """;
        cmd.Parameters.AddWithValue("@path", solutionPath);
        var members = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            members.Add(Path.GetFileName(reader.GetString(0).Replace('\\', '/')));
        return members.Order(StringComparer.Ordinal).ToArray();
    }

    private static EnsureSnapshotRequest Request() => new()
    {
        RepositoryRemoteUrl = RemoteUrl,
        CommitSha = new string('c', 40),
        TreeSha = new string('d', 40)
    };

    // The identity the worker's orchestrator publishes under (no capability on a plain local worker).
    private static string IdentityHash(EnsureSnapshotRequest request, SextantConfiguration config) =>
        request.ToIdentity(IndexProfileDescriptor.FromConfiguration(config).ConfigurationHash, null).Hash;

    private (IndexDatabase Db, SextantConfiguration Config) OpenCatalog()
    {
        var db = new IndexDatabase(Path.Combine(_dataRoot, "catalog.db"), IndexWriteOptions.Default);
        db.RunMigrations();
        return (db, new SextantConfiguration());
    }

    private static string[] Relative(string root, IEnumerable<string> paths) =>
        paths.Select(p => Path.GetRelativePath(root, p).Replace('\\', '/')).ToArray();

    private static int ScalarInt(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// A no-config monorepo checkout (no root "umbrella" solution declares everything):
    /// <c>Core</c> (multi-targeted <c>net10.0;netstandard2.0</c>, shared by every solution), <c>App</c> and
    /// <c>Tools/Tool</c> (each project-referencing Core and calling its shared method), and — optionally — an
    /// unloadable <c>Mobile.iOS</c> head whose project XML MSBuild cannot evaluate (an offline, deterministic
    /// stand-in for an iOS head on a Linux worker). Solutions:
    /// <c>App.slnx</c> = {Core, App}; <c>Build.Linux/Tools-server.slnx</c> = {Core, Tool};
    /// <c>Build.Mac/Mobile-ios.slnx</c> = {Core, Mobile.iOS}.
    /// </summary>
    private string CreateCheckout(ServicePaths paths, bool includeUnloadableHead)
    {
        var root = Path.Combine(paths.CheckoutRoot, ServicePaths.RepoDirectoryName(RemoteUrl));
        Directory.CreateDirectory(root);

        WriteProject(root, "Core/Core.csproj", "<TargetFrameworks>net10.0;netstandard2.0</TargetFrameworks>", reference: null,
            "Core/CoreSource.cs", "namespace Union;\npublic class CoreType { public int CoreCompute() => 42; }\n");
        WriteProject(root, "App/App.csproj", "<TargetFramework>net10.0</TargetFramework>", "..\\Core\\Core.csproj",
            "App/AppSource.cs", "namespace Union;\npublic class Alpha { public int Use() => new CoreType().CoreCompute(); }\n");
        WriteProject(root, "Tools/Tool.csproj", "<TargetFramework>net10.0</TargetFramework>", "..\\Core\\Core.csproj",
            "Tools/ToolSource.cs", "namespace Union;\npublic class Tooling { public int Use() => new CoreType().CoreCompute(); }\n");

        Write(root, "App.slnx",
            "<Solution>\n  <Project Path=\"Core/Core.csproj\" />\n  <Project Path=\"App/App.csproj\" />\n</Solution>\n");
        Write(root, "Build.Linux/Tools-server.slnx",
            "<Solution>\n  <Project Path=\"../Core/Core.csproj\" />\n  <Project Path=\"../Tools/Tool.csproj\" />\n</Solution>\n");

        if (includeUnloadableHead)
        {
            // Unevaluable project XML (unterminated) — the same offline stand-in #90's resilient-load test uses.
            // A missing <Import> is NOT a usable stand-in: MSBuildWorkspace still loaded such a project with
            // its documents (observed while writing this test), so it would be indexed, not skipped.
            Write(root, "Mobile.iOS/Mobile.iOS.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0-ios\n");
            Write(root, "Mobile.iOS/MobileSource.cs", "namespace Union;\npublic class MobileType { }\n");
            Write(root, "Build.Mac/Mobile-ios.slnx",
                "<Solution>\n  <Project Path=\"../Core/Core.csproj\" />\n  <Project Path=\"../Mobile.iOS/Mobile.iOS.csproj\" />\n</Solution>\n");
        }

        // Restore only the loadable projects (the head cannot evaluate — that is the point). App and Tool
        // pull in Core (both TFMs) through their project references.
        Restore(Path.Combine(root, "App", "App.csproj"));
        Restore(Path.Combine(root, "Tools", "Tool.csproj"));
        return root;
    }

    private static void WriteProject(
        string root, string projectPath, string tfmElement, string? reference, string sourcePath, string source)
    {
        var referenceItem = reference is null
            ? string.Empty
            : $"\n  <ItemGroup>\n    <ProjectReference Include=\"{reference}\" />\n  </ItemGroup>";
        Write(root, projectPath, $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                {{tfmElement}}
                <LangVersion>latest</LangVersion>
                <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
              </PropertyGroup>{{referenceItem}}
            </Project>
            """);
        Write(root, sourcePath, source);
    }

    private static void Write(string root, string relative, string content)
    {
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static void Restore(string projectPath)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet", $"restore \"{projectPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = System.Diagnostics.Process.Start(psi)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        // Bounded (review): a hung restore must not stall the whole Integration run. Kill only THIS process
        // (and its children) — never dotnet/MSBuild by name, other sessions share the machine.
        if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already exited */ }
            Assert.Inconclusive($"restore of '{Path.GetFileName(projectPath)}' did not finish within 5 minutes");
        }
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        // Inconclusive (not failed) on a restore failure, matching every other generated-corpus integration
        // test in this repo (e.g. MultiSolutionIndexingIntegrationTests): an offline machine without the
        // netstandard2.0 reference pack cached cannot restore, which is not a product regression.
        if (process.ExitCode != 0)
            Assert.Inconclusive(
                $"restore of '{Path.GetFileName(projectPath)}' failed (exit {process.ExitCode}): {stderr}{stdout}");
    }
}
