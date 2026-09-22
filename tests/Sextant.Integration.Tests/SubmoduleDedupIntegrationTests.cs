using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Daemon;
using Sextant.Indexer;
using Sextant.Store;

namespace Sextant.Integration.Tests;

/// <summary>
/// End-to-end Phase-12 coverage over REAL git repositories with a REAL git submodule shared by two
/// parents, driven through the real <see cref="LocalOverlayReconciler"/> / <see cref="IndexOrchestrator"/>.
/// A provider repo (a MixAndMatch-style shared library) is added as a submodule at the SAME commit into
/// two independent parent repos, both indexed into ONE database. Asserts the acceptance criteria that are
/// fundamentally orchestrator behaviors (the store-layer contracts are pinned separately in
/// <c>SnapshotDependencyStoreTests</c>):
/// <list type="bullet">
///   <item>Criterion 1 — the shared submodule commit is stored ONCE (one provider snapshot, one provider
///   project version, the provider symbol indexed once) even though two parents pin it.</item>
///   <item>Criterion 2 — each parent retains its own dependency edge and pin.</item>
///   <item>Criterion 3 — a producer-symbol usage query returns BOTH parents' authorized usages.</item>
///   <item>Criterion 6 — indexing the second parent does NOT mutate the first parent's snapshot nor
///   re-index the shared provider (immutability + dedup).</item>
/// </list>
/// The whole fixture is skipped (<see cref="Assert.Inconclusive"/>) when git or the SDK cannot build it in
/// this environment, so it never produces a false failure.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public class SubmoduleDedupIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public SubmoduleDedupIntegrationTests()
    {
        _ = IntegrationFixture.Instance; // MSBuildLocator registered before any Roslyn type loads
        _tempDir = Path.Combine(Path.GetTempPath(), $"sextant_submod_int_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Sextant.TestSupport.SqliteTestDatabase.DeleteDirectory(_tempDir);

    [TestMethod]
    public async Task SharedSubmodule_PinnedByTwoParents_IsDeduplicated_AndCrossRepoUsagesResolve()
    {
        var provider = CreateProviderRepo();
        var appA = CreateParentRepoWithSubmodule("appA", "ProgramA", provider);
        var appB = CreateParentRepoWithSubmodule("appB", "ProgramB", provider);

        var dbPath = Path.Combine(_tempDir, "index.db");
        using var db = new IndexDatabase(dbPath);
        db.RunMigrations();
        var conn = db.GetConnection();

        // Index parent A: builds A's parent snapshot + the deduplicated provider snapshot + A's edge.
        await ReconcileAsync(db, appA.SolutionPath);
        var providerSnapshotsAfterA = CountProviderSnapshots(conn);
        var providerCombineCountAfterA = CountProviderSymbol(conn, "Combine");
        Assert.AreEqual(1, providerSnapshotsAfterA, "indexing the first parent stages exactly one provider snapshot");
        Assert.AreEqual(1, providerCombineCountAfterA, "the provider's shared symbol is indexed once");
        var aParentSnapshotId = SingleParentSnapshotId(conn);
        var aParentFingerprint = SnapshotFingerprint(conn, aParentSnapshotId);
        var edgesAfterA = CountEdges(conn);
        Assert.AreEqual(1, edgesAfterA, "parent A recorded its own dependency edge");

        // Index parent B into the SAME db: the provider commit is already stored, so it must be REUSED,
        // never staged or extracted again (criterion 1) — and A's snapshot must not change (criterion 6).
        await ReconcileAsync(db, appB.SolutionPath);

        Assert.AreEqual(1, CountProviderSnapshots(conn),
            "the shared submodule commit is stored ONCE across both parents (criterion 1: dedup)");
        Assert.AreEqual(1, CountProviderSymbol(conn, "Combine"),
            "the provider symbol is still indexed once — the second parent reused it, never duplicated it (criterion 1)");
        Assert.AreEqual(aParentFingerprint, SnapshotFingerprint(conn, aParentSnapshotId),
            "indexing parent B never mutated parent A's snapshot rows (criterion 6: immutability)");

        // Criterion 2: two edges, one per parent, both pinning the SAME deduplicated provider project version.
        var edges = ReadEdges(conn);
        Assert.AreEqual(2, edges.Count, "each parent retains its own dependency edge (criterion 2)");
        Assert.AreEqual(1, edges.Select(e => e.providerProjectId).Distinct().Count(),
            "both edges pin the one deduplicated provider project version");

        // #7 regression: an INTRA-submodule project reference (Extensions -> Mix, both provider projects)
        // must NOT be recorded as a cross-repository edge. No dependency edge may attribute a provider
        // project as the CONSUMER — only genuine parent projects consume the submodule.
        Assert.AreEqual(0, ScalarInt(conn, """
            SELECT COUNT(*) FROM snapshot_dependencies d
            JOIN projects cp ON cp.id = d.consumer_project_id
            JOIN snapshots cs ON cs.id = cp.snapshot_id
            WHERE cs.is_provider = 1;
            """), "no dependency edge attributes a provider project as a consumer (intra-submodule ref excluded, #7)");

        // Criterion 3: a producer-symbol usage query returns BOTH parents' usages from their default heads.
        var providerUrl = ProviderRepoUrl(conn);
        var combineKey = ProviderSymbolKey(conn, "Combine");
        Assert.IsNotNull(combineKey, "the provider's Combine symbol has a stable key");
        var usages = new SnapshotDependencyStore(conn).FindCrossRepositoryUsages(
            providerUrl, combineKey!, CrossRepoUsageScope.DefaultHeads, authorizedConsumerRepositoryIds: null);
        Assert.AreEqual(2, usages.Count,
            "the producer-symbol usage query returns both parents' cross-repo usages (criterion 3)");
        CollectionAssert.AreEquivalent(
            new[] { "ProgramA.cs", "ProgramB.cs" },
            usages.Select(u => Path.GetFileName(u.FilePath)).ToList(),
            "each usage is located in the calling parent's source file");
    }

    // ==== assertions helpers ====================================================================

    private static int CountProviderSnapshots(SqliteConnection conn) =>
        ScalarInt(conn, "SELECT COUNT(*) FROM snapshots WHERE is_provider = 1;");

    private static int CountEdges(SqliteConnection conn) =>
        ScalarInt(conn, "SELECT COUNT(*) FROM snapshot_dependencies;");

    private static int CountProviderSymbol(SqliteConnection conn, string displayName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM symbols s
            JOIN projects p ON p.id = s.project_id
            JOIN snapshots sn ON sn.id = p.snapshot_id
            WHERE sn.is_provider = 1 AND s.display_name = @n;
            """;
        cmd.Parameters.AddWithValue("@n", displayName);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static string? ProviderSymbolKey(SqliteConnection conn, string displayName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.symbol_key FROM symbols s
            JOIN projects p ON p.id = s.project_id
            JOIN snapshots sn ON sn.id = p.snapshot_id
            WHERE sn.is_provider = 1 AND s.display_name = @n
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@n", displayName);
        return cmd.ExecuteScalar() as string;
    }

    private static string ProviderRepoUrl(SqliteConnection conn) =>
        (string)new SqliteCommand("SELECT remote_url FROM repositories WHERE is_provider = 1 LIMIT 1;", conn).ExecuteScalar()!;

    private static List<(long consumerProjectId, long providerProjectId)> ReadEdges(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT consumer_project_id, provider_project_id FROM snapshot_dependencies ORDER BY id;";
        var rows = new List<(long, long)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add((reader.GetInt64(0), reader.GetInt64(1)));
        return rows;
    }

    /// <summary>The id of the single NON-provider (parent) snapshot currently in the DB — used right
    /// after indexing the first parent, before the second parent adds its own.</summary>
    private static long SingleParentSnapshotId(SqliteConnection conn) =>
        Convert.ToInt64(ScalarInt(conn, "SELECT id FROM snapshots WHERE is_provider = 0 ORDER BY id LIMIT 1;"));

    /// <summary>A stable fingerprint of ONE snapshot's mapped symbols — recomputed for parent A's snapshot
    /// after parent B is indexed, it changes only if B's index mutated A's rows (criterion 6).</summary>
    private static string SnapshotFingerprint(SqliteConnection conn, long snapshotId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.symbol_key, s.display_name
            FROM symbols s
            JOIN snapshot_projects sp ON sp.project_id = s.project_id
            WHERE sp.snapshot_id = @sid
            ORDER BY s.symbol_key, s.display_name, s.id;
            """;
        cmd.Parameters.AddWithValue("@sid", snapshotId);
        var sb = new System.Text.StringBuilder();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            sb.Append(reader.GetString(0)).Append('|').Append(reader.GetString(1)).Append('\n');
        return sb.ToString();
    }

    private static int ScalarInt(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static async Task ReconcileAsync(IndexDatabase db, string solutionPath)
    {
        var solution = await SolutionLoader.LoadSolutionAsync(solutionPath);
        var reconciler = new LocalOverlayReconciler(
            db, log: null, useDocumentExtractor: true,
            parallelism: ExtractionParallelismOptions.Default,
            profile: IndexProfileDescriptor.For(IndexProfiles.Standard));
        await reconciler.ReconcileAsync(solution);
    }

    // ==== fixture builders ======================================================================

    private sealed record RepoFixture(string RepoRoot, string SolutionPath);

    /// <summary>A standalone git repo that serves as the shared submodule source (the "MixAndMatch" lib).
    /// It contains TWO projects — <c>Mix</c> and <c>Mix.Extensions</c> (which project-references Mix) — so
    /// the fixture also exercises an INTRA-submodule project reference: that internal edge must never be
    /// recorded as a parent -> provider cross-repository dependency (#7 regression).</summary>
    private RepoFixture CreateProviderRepo()
    {
        var repoRoot = Path.Combine(_tempDir, "provider");
        var projDir = Path.Combine(repoRoot, "Mix");
        Directory.CreateDirectory(projDir);
        File.WriteAllText(Path.Combine(repoRoot, ".gitignore"), "bin/\nobj/\n.sextant/\nsextant.json\n*.db\n*.db-wal\n*.db-shm\n");
        File.WriteAllText(Path.Combine(projDir, "Mix.csproj"), Csproj());
        File.WriteAllText(Path.Combine(projDir, "Combiner.cs"),
            "namespace Mix;\npublic class Combiner { public int Combine(int a, int b) => a + b; }\n");

        // A second provider project INSIDE the same submodule that references the first (Extensions -> Mix).
        var extDir = Path.Combine(repoRoot, "Extensions");
        Directory.CreateDirectory(extDir);
        File.WriteAllText(Path.Combine(extDir, "Extensions.csproj"), Csproj(projectReference: "..\\Mix\\Mix.csproj"));
        File.WriteAllText(Path.Combine(extDir, "Enhancer.cs"),
            "namespace Mix.Ext;\npublic class Enhancer { public int Enhance(int a) => new Mix.Combiner().Combine(a, 1); }\n");

        Git(repoRoot, "init -b main");
        ConfigureGit(repoRoot);
        Git(repoRoot, "add -A");
        Git(repoRoot, "commit -m provider-initial");
        return new RepoFixture(repoRoot, Path.Combine(repoRoot, "Mix", "Mix.csproj"));
    }

    /// <summary>A parent repo that consumes the provider as a git submodule at <c>libs/mix</c> and calls
    /// its shared symbol, so indexing it produces a cross-repository usage into the provider.</summary>
    private RepoFixture CreateParentRepoWithSubmodule(string name, string className, RepoFixture provider)
    {
        var repoRoot = Path.Combine(_tempDir, name);
        var projDir = Path.Combine(repoRoot, "App");
        Directory.CreateDirectory(projDir);
        File.WriteAllText(Path.Combine(repoRoot, ".gitignore"), "bin/\nobj/\n.sextant/\nsextant.json\n*.db\n*.db-wal\n*.db-shm\n");

        Git(repoRoot, "init -b main");
        ConfigureGit(repoRoot);

        // Each parent gets a DISTINCT origin remote so the two parents are indexed as SEPARATE
        // repositories (distinct snapshot identity + its own default-branch head). Without this they
        // would both fall back to the same local://<machine> identity, collapse into one repository, and
        // the shared "main" branch pointer would move from parent A's snapshot to parent B's — dropping
        // A's usages from the default-head cross-repo query. Real consumers always have distinct remotes;
        // this fixture must mirror that to exercise criterion 3 across two repositories.
        Git(repoRoot, $"remote add origin https://github.com/sextant-test/{name}.git");

        // Add the provider as a submodule at the SAME commit both parents will pin. A local file:// path
        // submodule needs protocol.file.allow=always on modern git.
        var providerUrl = "file:///" + provider.RepoRoot.Replace('\\', '/');
        Git(repoRoot, $"-c protocol.file.allow=always submodule add {providerUrl} libs/mix");

        // Parent project references the submodule project and calls into its shared symbol.
        File.WriteAllText(Path.Combine(projDir, "App.csproj"), Csproj(projectReference: "..\\libs\\mix\\Mix\\Mix.csproj"));
        File.WriteAllText(Path.Combine(projDir, $"{className}.cs"),
            $"namespace {name.ToUpperInvariant()};\npublic class {className} {{ public int Run() => new Mix.Combiner().Combine(1, 2); }}\n");

        var slnPath = Path.Combine(repoRoot, "App.slnx");
        File.WriteAllText(slnPath,
            "<Solution>\n  <Project Path=\"App/App.csproj\" />\n  <Project Path=\"libs/mix/Mix/Mix.csproj\" />\n  <Project Path=\"libs/mix/Extensions/Extensions.csproj\" />\n</Solution>\n");

        Git(repoRoot, "add -A");
        Git(repoRoot, "commit -m parent-initial");
        RestoreSolution(slnPath);
        return new RepoFixture(repoRoot, slnPath);
    }

    private static string Csproj(string? projectReference = null)
    {
        var reference = projectReference == null
            ? ""
            : $"\n  <ItemGroup>\n    <ProjectReference Include=\"{projectReference}\" />\n  </ItemGroup>";
        return $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
              </PropertyGroup>{reference}
            </Project>
            """;
    }

    private static void ConfigureGit(string repoRoot)
    {
        Git(repoRoot, "config user.email test@example.com");
        Git(repoRoot, "config user.name Test");
        Git(repoRoot, "config commit.gpgsign false");
        Git(repoRoot, "config protocol.file.allow always");
    }

    private static void Git(string repoRoot, string args)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        Process process;
        try { process = Process.Start(psi) ?? throw new InvalidOperationException("git not found"); }
        catch (Exception ex) { Assert.Inconclusive($"git is not available: {ex.Message}"); return; }
        var stderr = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            Assert.Inconclusive($"git {args} failed (exit {process.ExitCode}): {stderr}");
    }

    private static void RestoreSolution(string solutionPath)
    {
        var psi = new ProcessStartInfo("dotnet", $"restore \"{solutionPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            Assert.Inconclusive($"restore of the generated solution failed (exit {process.ExitCode}): {stderr}");
    }
}
