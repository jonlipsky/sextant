using Microsoft.CodeAnalysis;
using Microsoft.Data.Sqlite;
using Sextant.Indexer;
using Sextant.Store;

namespace Sextant.Integration.Tests;

/// <summary>
/// Env-gated (SEXTANT_RUN_MULTISLN=1) end-to-end coverage for multi-solution monorepo indexing
/// (issue #109), driven through the REAL <see cref="SolutionSelector"/> + <see cref="MultiSolutionLoader"/>
/// over a REAL multi-solution fixture with a REAL MSBuild toolchain. Two tests:
/// <list type="bullet">
/// <item><see cref="TwoConfiguredSolutions_UnionDedup_Deterministic_SkippedWithReason"/> asserts the
/// loader-level acceptance criteria: deterministic explicit selection, UNION coverage with per-identity
/// dedup, skipped-with-reason driving PARTIAL (never complete), and stability across runs.</item>
/// <item><see cref="UnionIndexedIntoStore_DeduplicatesSharedProject_NoDuplicateOccurrences"/> drives the
/// union through the REAL <see cref="IndexOrchestrator"/> into an isolated SQLite index and asserts the
/// STORAGE-level dedup the coordinator called out: one stored logical project per identity (the shared
/// project stored ONCE, not once per head), the shared symbol indexed once, and both heads' cross-project
/// calls into it recorded exactly once each — no duplicate occurrences / no index blow-up.</item>
/// </list>
/// Both skip (Inconclusive) unless SEXTANT_RUN_MULTISLN=1 because they shell out to <c>dotnet restore</c>
/// and a real MSBuild BuildHost.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class MultiSolutionIndexingIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public MultiSolutionIndexingIntegrationTests()
    {
        _ = IntegrationFixture.Instance; // ensure MSBuildLocator is registered before any Roslyn type loads
        _tempDir = Path.Combine(Path.GetTempPath(), $"sextant_multisln_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Sextant.TestSupport.SqliteTestDatabase.DeleteDirectory(_tempDir);

    [TestMethod]
    public async Task TwoConfiguredSolutions_UnionDedup_Deterministic_SkippedWithReason()
    {
        if (Environment.GetEnvironmentVariable("SEXTANT_RUN_MULTISLN") != "1")
            Assert.Inconclusive("Set SEXTANT_RUN_MULTISLN=1 to run the real-MSBuild multi-solution index test.");

        var fixture = CreateMultiSolutionRepo();

        // --- Criterion 1: explicit, deterministic selection honors the configured list in order ---------
        var selection = SolutionSelector.Select(
            fixture.RepoRoot, new[] { "Core-A.slnx", "Core-B.slnx" });
        Assert.AreEqual(SolutionSelectionSource.Configured, selection.Source);
        Assert.AreEqual(2, selection.SolutionPaths.Count);
        Assert.IsTrue(selection.SolutionPaths[0].EndsWith("Core-A.slnx", StringComparison.Ordinal));
        Assert.IsTrue(selection.SolutionPaths[1].EndsWith("Core-B.slnx", StringComparison.Ordinal));
        Assert.AreEqual(0, selection.SkippedSolutions.Count);

        // --- Load the union and assert coverage --------------------------------------------------------
        var load = await MultiSolutionLoader.LoadAsync(selection.SolutionPaths, onDiagnostic: null);

        // Criterion 2: UNION of projects, de-duplicated by identity. Core is shared by BOTH heads yet
        // appears once; each head's own project is present.
        var loadedNames = LoadedProjectNames(load.Solution);
        CollectionAssert.Contains(loadedNames, "Core", "the shared project is indexed");
        CollectionAssert.Contains(loadedNames, "A", "head A's own project is indexed");
        CollectionAssert.Contains(loadedNames, "B", "head B's own project is indexed");
        Assert.AreEqual(1, loadedNames.Count(n => n == "Core"),
            "the project shared by both heads is de-duplicated (union by identity), not indexed twice");

        // Criterion 3: the unloadable project is recorded skipped-with-reason and drives PARTIAL — never
        // silently dropped, never present as an indexed project.
        Assert.IsTrue(load.IsPartial, "an unloadable project must make the load PARTIAL, not complete");
        Assert.IsTrue(load.SkippedProjects.Any(s =>
            Path.GetFileNameWithoutExtension(s.ProjectPath) == "Broken"),
            "the unloadable project is reported skipped-with-reason");
        var brokenSkip = load.SkippedProjects.First(s =>
            Path.GetFileNameWithoutExtension(s.ProjectPath) == "Broken");
        Assert.IsFalse(string.IsNullOrWhiteSpace(brokenSkip.Reason), "a skipped project carries a reason");
        CollectionAssert.DoesNotContain(loadedNames, "Broken",
            "the unloadable project is NOT counted as an indexed project");

        // Per-solution coverage attributes the skip to the head that declared it, and reports honest
        // declared/loaded counts (partial coverage surfaced per solution).
        var coreB = load.Solutions.Single(c => c.SolutionPath.EndsWith("Core-B.slnx", StringComparison.Ordinal));
        Assert.AreEqual(3, coreB.DeclaredProjectCount, "Core-B declares Core, B and Broken");
        Assert.AreEqual(2, coreB.LoadedProjectCount, "Core-B loaded Core and B");
        Assert.AreEqual(1, coreB.SkippedProjects.Count, "Core-B's skipped project is Broken");

        var coreA = load.Solutions.Single(c => c.SolutionPath.EndsWith("Core-A.slnx", StringComparison.Ordinal));
        Assert.AreEqual(2, coreA.DeclaredProjectCount, "Core-A declares Core and A");
        Assert.AreEqual(0, coreA.SkippedProjects.Count, "Core-A fully loaded");

        // --- Criterion 4: deterministic + stable across runs -------------------------------------------
        var reload = await MultiSolutionLoader.LoadAsync(selection.SolutionPaths, onDiagnostic: null);
        CollectionAssert.AreEqual(
            OrderedProjectFilePaths(load.Solution),
            OrderedProjectFilePaths(reload.Solution),
            "the union workspace's project set and order is identical across runs (determinism)");

        // Selection itself is stable across runs for the identical tree.
        var reselect = SolutionSelector.Select(fixture.RepoRoot, new[] { "Core-A.slnx", "Core-B.slnx" });
        CollectionAssert.AreEqual(
            selection.SolutionPaths.ToList(), reselect.SolutionPaths.ToList(),
            "selection is stable across runs");
    }

    [TestMethod]
    public async Task UnionIndexedIntoStore_DeduplicatesSharedProject_NoDuplicateOccurrences()
    {
        if (Environment.GetEnvironmentVariable("SEXTANT_RUN_MULTISLN") != "1")
            Assert.Inconclusive("Set SEXTANT_RUN_MULTISLN=1 to run the real-MSBuild multi-solution index test.");

        var fixture = CreateMultiSolutionRepo();
        var selection = SolutionSelector.Select(fixture.RepoRoot, new[] { "Core-A.slnx", "Core-B.slnx" });
        var load = await MultiSolutionLoader.LoadAsync(selection.SolutionPaths, onDiagnostic: null);

        // Index the deterministic UNION exactly ONCE into an isolated index (the temp fixture has no git,
        // so this is the legacy mutable-row path — precisely what exercises the store-level dedup).
        var dbPath = Path.Combine(_tempDir, "union-index.db");
        using var db = new IndexDatabase(dbPath);
        db.RunMigrations();
        await new IndexOrchestrator(db, useDocumentExtractor: true).IndexSolutionAsync(load.Solution);
        var conn = db.GetConnection();

        // Coordinator dedup ask: one stored logical project per identity. Core is shared by BOTH heads yet
        // stored ONCE; A and B are stored; the unloadable Broken contributes no project row at all.
        Assert.AreEqual(3, ScalarInt(conn, "SELECT COUNT(*) FROM projects;"),
            "exactly three projects are stored — Core (shared, deduped), A and B; Broken is skipped, not stored");
        Assert.AreEqual(1, ScalarInt(conn,
            "SELECT COUNT(*) FROM projects WHERE repo_relative_path LIKE '%Core.csproj';"),
            "the project shared by both heads is stored ONCE (union dedup by project identity, not once per head)");
        Assert.AreEqual(0, ScalarInt(conn,
            "SELECT COUNT(*) FROM projects WHERE repo_relative_path LIKE '%Broken.csproj';"),
            "the unloadable project is not stored");

        // The shared project's symbols are stored once — never doubled by appearing under two heads.
        Assert.AreEqual(1, ScalarInt(conn, "SELECT COUNT(*) FROM symbols WHERE display_name = 'CoreType';"),
            "the shared type is indexed exactly once");
        Assert.AreEqual(1, ScalarInt(conn, "SELECT COUNT(*) FROM symbols WHERE display_name = 'CoreCompute';"),
            "the shared method is indexed exactly once");

        // Both heads' cross-project CALLS into the shared method are recorded — one from A, one from B.
        // Exactly two (not four) proves union coverage of BOTH heads AND that the shared project was
        // processed once: a double-indexed Core would double the inbound call occurrences.
        var callsIntoCore = ScalarInt(conn, """
            SELECT COUNT(*) FROM occurrences o
            JOIN symbols t ON t.id = o.target_symbol_id
            WHERE t.display_name = 'CoreCompute' AND o.source_symbol_id IS NOT NULL;
            """);
        Assert.AreEqual(2, callsIntoCore,
            "both heads' cross-project calls into the shared method are recorded exactly once each " +
            "(union coverage of both heads, no duplicate occurrences)");
    }

    private static int ScalarInt(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static List<string> LoadedProjectNames(Solution solution) =>
        solution.Projects
            .Where(p => p.Documents.Any())
            .Select(p => Path.GetFileNameWithoutExtension(p.FilePath)!)
            .ToList();

    private static List<string> OrderedProjectFilePaths(Solution solution) =>
        solution.Projects
            .Where(p => p.Documents.Any())
            .Select(p => Path.GetFullPath(p.FilePath!))
            .ToList();

    private sealed record MultiSolutionRepo(string RepoRoot);

    /// <summary>
    /// A monorepo checkout with THREE loadable projects — <c>Core</c> (shared by both heads), <c>A</c> and
    /// <c>B</c> (each PROJECT-REFERENCING Core and calling its uniquely-named shared method) — plus one
    /// deliberately-unloadable project (<c>Broken</c>, an unresolvable SDK — a Linux stand-in for a
    /// platform head), spread across two solution heads:
    /// <c>Core-A.slnx</c> = {Core, A}; <c>Core-B.slnx</c> = {Core, B, Broken}. The A→Core and B→Core
    /// references let the storage test count each head's cross-project call into the shared method.
    /// </summary>
    private MultiSolutionRepo CreateMultiSolutionRepo()
    {
        var repoRoot = Path.Combine(_tempDir, "monorepo");
        Directory.CreateDirectory(repoRoot);

        WriteSdkProject(Path.Combine(repoRoot, "Core"), "Core", projectReference: null,
            source: "namespace App;\npublic class CoreType { public int CoreCompute() => 42; }\n");
        WriteSdkProject(Path.Combine(repoRoot, "A"), "A", projectReference: "..\\Core\\Core.csproj",
            source: "namespace App;\npublic class Alpha { public int Use() => new CoreType().CoreCompute(); }\n");
        WriteSdkProject(Path.Combine(repoRoot, "B"), "B", projectReference: "..\\Core\\Core.csproj",
            source: "namespace App;\npublic class Beta { public int Use() => new CoreType().CoreCompute(); }\n");

        // A project that cannot load on ANY worker: an SDK that does not resolve. Stands in for an
        // iOS/Android/Mac/WPF head that will not load on a Linux worker — recorded skipped-with-reason.
        var brokenDir = Path.Combine(repoRoot, "Broken");
        Directory.CreateDirectory(brokenDir);
        File.WriteAllText(Path.Combine(brokenDir, "Broken.csproj"),
            "<Project Sdk=\"Sextant.NonExistent.Sdk/9.9.9\">\n  <PropertyGroup>\n" +
            "    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        File.WriteAllText(Path.Combine(brokenDir, "Broken.cs"),
            "namespace App;\npublic class BrokenType { public int Value() => 1; }\n");

        File.WriteAllText(Path.Combine(repoRoot, "Core-A.slnx"),
            "<Solution>\n  <Project Path=\"Core/Core.csproj\" />\n  <Project Path=\"A/A.csproj\" />\n</Solution>\n");
        File.WriteAllText(Path.Combine(repoRoot, "Core-B.slnx"),
            "<Solution>\n  <Project Path=\"Core/Core.csproj\" />\n  <Project Path=\"B/B.csproj\" />\n" +
            "  <Project Path=\"Broken/Broken.csproj\" />\n</Solution>\n");

        // Restore only the loadable projects (Broken cannot restore — that is the point). Core-A pulls in
        // Core + A; B pulls in B + Core via its project reference. Broken is left unrestored/unresolvable.
        RestoreProject(Path.Combine(repoRoot, "Core-A.slnx"));
        RestoreProject(Path.Combine(repoRoot, "B", "B.csproj"));

        return new MultiSolutionRepo(repoRoot);
    }

    private static void WriteSdkProject(string projDir, string name, string? projectReference, string source)
    {
        Directory.CreateDirectory(projDir);
        var reference = projectReference == null
            ? ""
            : $"\n  <ItemGroup>\n    <ProjectReference Include=\"{projectReference}\" />\n  </ItemGroup>";
        File.WriteAllText(Path.Combine(projDir, $"{name}.csproj"), $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
              </PropertyGroup>{{reference}}
            </Project>
            """);
        File.WriteAllText(Path.Combine(projDir, $"{name}Source.cs"), source);
    }

    private static void RestoreProject(string path)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet", $"restore \"{path}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = System.Diagnostics.Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            Assert.Inconclusive($"restore of '{Path.GetFileName(path)}' failed (exit {process.ExitCode}): {stderr}");
    }
}
