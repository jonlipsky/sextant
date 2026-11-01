using System.Diagnostics;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Sextant.Core;
using Sextant.Indexer;
using Sextant.Store;

namespace Sextant.Indexer.Tests;

/// <summary>
/// Issue #233 (1): a submodule provider's own coverage row records binding health, measured like a parent's, and a
/// degraded provider project makes the PROVIDER partial. Real nested git repositories on disk, the layout of
/// <see cref="SubmoduleProjectOrderTests"/>, with a provider project whose code does not bind.
/// </summary>
[TestClass]
public class SubmoduleBindingHealthTests
{
    private const string SubmodulePath = "libs/mix";
    private const string ProviderRemote = "https://github.com/org/MixAndMatch";
    private const string ProviderCommit = "8796341f00000000000000000000000000000000";
    private const string MixProject = "Mix/Mix.csproj";
    private string _root = null!;
    private string _repo = null!;
    private string _dbPath = null!;
    private IndexDatabase _db = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sextant_subbind_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "index.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _repo = Layout();
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteTestDatabase.Delete(_dbPath, _db);
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [TestMethod]
    public async Task ProviderProjectThatFailsToBind_MakesTheProviderPartial_AndNamesIt()
    {
        await Index("c1");

        var provider = ProviderCoverage();
        Assert.AreEqual(SnapshotCoverageVerdict.Partial, provider.Verdict);
        Assert.IsNotNull(provider.Binding, "the provider records its own binding block");
        Assert.AreEqual(1, provider.Binding.ProjectsDegraded);
        var entry = provider.Binding.Projects.Single(p => p.Degraded);
        Assert.AreEqual(MixProject, entry.Project, "named relative to the provider, never the parent's layout");
        Assert.IsFalse(entry.Excluded);
        Assert.IsTrue(provider.Reasons.Any(r => r.Contains(MixProject, StringComparison.Ordinal)),
            "the reason names the degraded project");
    }

    [TestMethod]
    public async Task ParentBindingBlock_DoesNotCountTheProvidersProjects()
    {
        await Index("c1", loadIssues: new Dictionary<string, string> { [$"{SubmodulePath}/{MixProject}"] = "NU1101" });

        var parent = ParentCoverage("c1");
        Assert.AreEqual(SnapshotCoverageVerdict.Complete, parent.Verdict,
            "a degraded provider does not make the parent's own verdict partial");
        Assert.AreEqual(0, parent.Binding!.ProjectsDegraded);
        Assert.AreEqual(0, parent.Binding.UnboundNames, "the provider project's names are not in the parent's totals");
        Assert.IsFalse(parent.Binding.Projects.Any(p => p.Project.Contains("Mix", StringComparison.Ordinal)),
            "neither its bindings nor its load issue are listed again in the parent");
        var provider = ProviderCoverage().Binding!;
        Assert.IsTrue(provider.UnboundNames >= BindingHealthBuilder.DegradedMinUnboundNames);
        Assert.AreEqual("NU1101", provider.Projects.Single(p => p.Project == MixProject).LoadIssue,
            "the load issue is the provider's, keyed relative to it");
    }

    [TestMethod]
    public async Task ExcludedProviderProject_DoesNotMakeTheProviderPartial()
    {
        var exclusion = new ProjectExclusion(MixProject, null, ProjectExclusionReason.PlatformUnavailable, "ios; workload_missing");
        await Index("c1", providerCoverage: ProjectExclusionClassifier.Record(
            new SnapshotCoverage { Verdict = SnapshotCoverageVerdict.Complete }, [exclusion]));

        var provider = ProviderCoverage();
        Assert.AreEqual(SnapshotCoverageVerdict.Complete, provider.Verdict, "an exclusion is not a gap");
        Assert.IsNotNull(provider.Binding, "the block is still recorded");
        Assert.AreEqual(0, provider.Binding.ProjectsDegraded);
        var entry = provider.Binding.Projects.Single(p => p.Project == MixProject);
        Assert.IsTrue(entry.Degraded);
        Assert.IsTrue(entry.Excluded);
    }

    [TestMethod]
    public async Task ReusedProvider_KeepsItsStoredBlock()
    {
        await Index("c1");
        var first = ProviderCoverageRow();

        // A later parent commit pinning the same provider commit reuses the complete provider: nothing is re-measured.
        await Index("c2", providerCoverage: new SnapshotCoverage { Verdict = SnapshotCoverageVerdict.Complete });

        var second = ProviderCoverageRow();
        Assert.AreEqual(first.Json, second.Json, "the stored row, binding block included, is not rewritten");
        Assert.AreEqual(1, ProviderCoverage().Binding!.ProjectsDegraded);
        Assert.AreEqual(1, ScalarCount("SELECT COUNT(*) FROM snapshots WHERE is_provider = 1;"));
    }

    [TestMethod]
    public async Task ProviderBuiltBeforeTheChange_HasNoBlock_AndIsNotBackfilled()
    {
        await Index("c1");
        var provider = ScalarCount("SELECT id FROM snapshots WHERE is_provider = 1;");
        var store = new SnapshotCoverageStore(_db.GetConnection());
        var old = store.Get(provider)!;
        store.Delete(provider);
        store.Record(provider, old with { Binding = null, Verdict = SnapshotCoverageVerdict.Complete, Reasons = [] }, 1);

        await Index("c2", providerCoverage: new SnapshotCoverage { Verdict = SnapshotCoverageVerdict.Complete });

        var after = store.Get(provider)!;
        Assert.IsNull(after.Binding, "unknown stays unknown: never healthy, never backfilled");
        Assert.AreEqual(SnapshotCoverageVerdict.Complete, after.Verdict, "and not reported as a gap");
    }

    [TestMethod]
    public void NestedSubmodules_LoadIssueGoesToTheInnermostProviderOnly()
    {
        var conn = _db.GetConnection();
        var outer = SeedPendingProvider(conn, "outer");
        var inner = SeedPendingProvider(conn, "inner");
        var ctx = ContextWith(new Dictionary<string, string> { ["lib/inner/A/A.csproj"] = "NU1101" });
        IReadOnlyCollection<string> all = ["lib", "lib/inner"];

        IndexOrchestrator.RecordProviderCoverage(conn, outer, ["lib"], ctx, 1, null, [], all);
        IndexOrchestrator.RecordProviderCoverage(conn, inner, ["lib/inner"], ctx, 1, null, [], all);

        var store = new SnapshotCoverageStore(conn);
        Assert.IsNull(store.Get(outer)!.Binding, "the outer provider owns no issue");
        Assert.AreEqual("NU1101", store.Get(inner)!.Binding!.Projects.Single(p => p.Project == "A/A.csproj").LoadIssue);
    }

    [TestMethod]
    public void GrownProvider_FoldsTheNewProjectsBindingIntoItsRow()
    {
        var conn = _db.GetConnection();
        var provider = SeedPendingProvider(conn, "grown");
        var ctx = ContextWith(null);
        IndexOrchestrator.RecordProviderCoverage(conn, provider, ["lib"], ctx, 1, null,
            [new ProjectBindingCounts("Good/Good.csproj", "net10.0", 500, 0, 0, 0)]);
        var store = new SnapshotCoverageStore(conn);
        Assert.AreEqual(SnapshotCoverageVerdict.Complete, store.Get(provider)!.Verdict);

        // Republished by a later parent with a project the first one did not pin, and that project does not bind.
        IndexOrchestrator.RecordProviderCoverage(conn, provider, ["lib"], ctx, 2, null,
            [new ProjectBindingCounts("Bad/Bad.csproj", "net10.0", 100, 40, 0, 0)]);

        var grown = store.Get(provider)!;
        Assert.AreEqual(SnapshotCoverageVerdict.Partial, grown.Verdict);
        Assert.AreEqual(1, grown.Binding!.ProjectsDegraded);
        Assert.AreEqual(600, grown.Binding.NamesExamined, "the first project's counts are kept");
        Assert.IsTrue(grown.Reasons.Any(r => r.Contains("Bad/Bad.csproj", StringComparison.Ordinal)));

        // A second growth replaces the binding reason instead of stacking another.
        IndexOrchestrator.RecordProviderCoverage(conn, provider, ["lib"], ctx, 3, null,
            [new ProjectBindingCounts("Worse/Worse.csproj", "net10.0", 100, 50, 0, 0)]);
        var again = store.Get(provider)!;
        Assert.AreEqual(2, again.Binding!.ProjectsDegraded);
        Assert.AreEqual(1, again.Reasons.Count(BindingHealthBuilder.IsBindingReason));
    }

    private static SnapshotContext ContextWith(IReadOnlyDictionary<string, string>? loadIssues) => new()
    {
        RepositoryRemoteUrl = "https://github.com/org/parent",
        CommitSha = "c1",
        BranchName = "main",
        ProviderCoverage = new Dictionary<string, SnapshotCoverage>
        {
            ["lib"] = new() { Verdict = SnapshotCoverageVerdict.Complete },
            ["lib/inner"] = new() { Verdict = SnapshotCoverageVerdict.Complete }
        },
        ProjectLoadIssues = loadIssues
    };

    private static long SeedPendingProvider(Microsoft.Data.Sqlite.SqliteConnection conn, string commit)
    {
        var snapshots = new SnapshotStore(conn);
        var remote = $"https://github.com/org/{commit}";
        var repo = snapshots.EnsureProviderRepository(remote, 1);
        var identity = new SnapshotIdentity
        {
            RepositoryRemoteUrl = remote, CommitSha = commit, SchemaVersion = IndexDatabase.SnapshotSchemaVersion,
            AnalyzerVersion = "test", ToolchainFingerprint = "test"
        };
        return snapshots.BeginPending(identity, repo, null, null, 1, isProvider: true).id;
    }

    private Task Index(string parentCommit, SnapshotCoverage? providerCoverage = null,
        IReadOnlyDictionary<string, string>? loadIssues = null)
    {
        var orchestrator = new IndexOrchestrator(_db, useDocumentExtractor: true)
        {
            SubmoduleDiscoverer = root => Task.FromResult(
                Path.GetFullPath(root) == Path.GetFullPath(_repo)
                    ? new List<SubmoduleInfo> { new() { Path = SubmodulePath, CommitSha = ProviderCommit, RemoteUrl = ProviderRemote } }
                    : new List<SubmoduleInfo>())
        };
        var context = new SnapshotContext
        {
            RepositoryRemoteUrl = "https://github.com/org/parent",
            CommitSha = parentCommit,
            BranchName = "main",
            CheckoutDir = _repo,
            Coverage = new SnapshotCoverage { Verdict = SnapshotCoverageVerdict.Complete },
            ProviderCoverage = new Dictionary<string, SnapshotCoverage>
            {
                [SubmodulePath] = providerCoverage ?? new SnapshotCoverage { Verdict = SnapshotCoverageVerdict.Complete }
            },
            ProjectLoadIssues = loadIssues
        };
        return orchestrator.IndexSolutionAsync(BuildSolution(), snapshotContext: context);
    }

    private string Layout()
    {
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(repo);
        Git(repo, "init", "--quiet", "-b", "main");
        File.WriteAllText(Path.Combine(repo, "README.md"), "x");
        var sub = Path.Combine(repo, "libs", "mix");
        Directory.CreateDirectory(sub);
        Git(sub, "init", "--quiet", "-b", "main");
        File.WriteAllText(Path.Combine(sub, "README.md"), "x");
        return repo;
    }

    private Solution BuildSolution()
    {
        var solution = new AdhocWorkspace().CurrentSolution;
        solution = AddProject(solution, Path.Combine("src", "App"), "App",
            "namespace App { public class Widget { public int Size() => 1; } }");
        // Thirty fields of a type that does not exist: far past the degraded threshold.
        var unbound = new StringBuilder("namespace Mix { public class Combiner {");
        for (var i = 0; i < 30; i++)
            unbound.Append($" public Missing{i} Field{i};");
        unbound.Append(" } }");
        return AddProject(solution, Path.Combine("libs", "mix", "Mix"), "Mix", unbound.ToString());
    }

    private Solution AddProject(Solution solution, string relativeDir, string name, string source)
    {
        var projectDir = Path.Combine(_repo, relativeDir);
        Directory.CreateDirectory(projectDir);
        var projectPath = Path.Combine(projectDir, $"{name}.csproj");
        var sourcePath = Path.Combine(projectDir, $"{name}.cs");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(sourcePath, source);
        var projectId = ProjectId.CreateNewId();
        return solution
            .AddProject(ProjectInfo.Create(projectId, VersionStamp.Default, name, name, LanguageNames.CSharp,
                filePath: projectPath,
                metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]))
            .AddDocument(DocumentId.CreateNewId(projectId), $"{name}.cs", SourceText.From(source), filePath: sourcePath);
    }

    private SnapshotCoverage ProviderCoverage()
        => new SnapshotCoverageStore(_db.GetConnection()).Get(ScalarCount("SELECT id FROM snapshots WHERE is_provider = 1;"))!;

    private (string Json, long RecordedAt) ProviderCoverageRow()
    {
        using var cmd = _db.GetConnection().CreateCommand();
        cmd.CommandText = """
            SELECT c.summary_json, c.recorded_at FROM snapshot_coverage c
            JOIN snapshots s ON s.id = c.snapshot_id WHERE s.is_provider = 1;
            """;
        using var reader = cmd.ExecuteReader();
        Assert.IsTrue(reader.Read(), "the provider has a coverage row");
        return (reader.GetString(0), reader.GetInt64(1));
    }

    private SnapshotCoverage ParentCoverage(string commit)
    {
        var id = ScalarCount($"""
            SELECT s.id FROM snapshots s JOIN commits c ON c.id = s.commit_id
            WHERE s.is_provider = 0 AND c.commit_sha = '{commit}';
            """);
        return new SnapshotCoverageStore(_db.GetConnection()).Get(id)!;
    }

    private long ScalarCount(string sql)
    {
        using var cmd = _db.GetConnection().CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    private static void Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.AreEqual(0, p.ExitCode, $"git {string.Join(' ', args)} failed");
    }
}
