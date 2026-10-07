using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Sextant.Core;
using Sextant.Indexer;
using Sextant.Store;

namespace Sextant.Indexer.Tests;

/// <summary>
/// Issue #162 (b): a Phase-12 PROVIDER snapshot records the worker-computed coverage of its submodule subtree
/// (<see cref="SnapshotContext.ProviderCoverage"/>) INSIDE the transaction that publishes it, so a direct
/// ensure that later reuses the provider reports an honest verdict instead of an absent (implicitly
/// complete) one. Drives the real <see cref="IndexOrchestrator"/> over an in-memory Roslyn solution with a
/// fixed submodule list, so no git checkout or MSBuild is needed.
/// </summary>
[TestClass]
public class OrchestratorProviderCoverageTests
{
    private const string SubmodulePath = "libs/mix";
    private const string ProviderRemote = "https://github.com/org/MixAndMatch";
    private const string ProviderCommit = "8796341f00000000000000000000000000000000";
    private string _root = null!;
    private string _dbPath = null!;
    private IndexDatabase _db = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sextant_provcov_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        _dbPath = Path.Combine(_root, "index.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteTestDatabase.Delete(_dbPath, _db);
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static readonly SnapshotCoverage ProviderPartial = new()
    {
        Verdict = SnapshotCoverageVerdict.Partial,
        Reasons = ["1 of 2 project file(s) on disk were not reached by the indexing checkout's solution selection and were not indexed (Tools/Tools.csproj)."],
        SelectionSource = "parent_selection",
        ProjectsLoaded = 1,
        ProjectFilesOnDisk = 2,
        ProjectFilesUnreferenced = 1
    };

    private static readonly SnapshotCoverage ProviderComplete = new()
    {
        Verdict = SnapshotCoverageVerdict.Complete,
        SelectionSource = "default_union",
        SolutionsDiscovered = 1,
        SolutionsSelected = 1,
        ProjectsLoaded = 1,
        ProjectFilesOnDisk = 1
    };

    [TestMethod]
    public async Task ProviderPublish_RecordsItsComputedCompleteCoverage()
    {
        await Index("c1", providerCoverage: new Dictionary<string, SnapshotCoverage> { [SubmodulePath] = ProviderComplete });

        var recorded = ProviderCoverageRow();
        Assert.IsNotNull(recorded, "the provider publish records its coverage in the same transaction (#162)");
        Assert.AreEqual(SnapshotCoverageVerdict.Complete, recorded.Verdict);
        Assert.AreEqual("default_union", recorded.SelectionSource);
        Assert.AreEqual(SnapshotCoverageVerdict.Partial, new SnapshotCoverageStore(_db.GetConnection())
            .Get(ParentSnapshotId("c1"))!.Verdict, "the parent keeps its OWN coverage row");
    }

    [TestMethod]
    public async Task ProviderPublish_RecordsPartialCoverage()
    {
        await Index("c1", providerCoverage: new Dictionary<string, SnapshotCoverage> { [SubmodulePath] = ProviderPartial });

        var recorded = ProviderCoverageRow();
        Assert.IsNotNull(recorded);
        Assert.AreEqual(SnapshotCoverageVerdict.Partial, recorded.Verdict,
            "a provider built only from parent-reachable projects is never recorded complete");
        Assert.AreEqual("parent_selection", recorded.SelectionSource);
        Assert.AreEqual(1, recorded.ProjectFilesUnreferenced);
        StringAssert.Contains(recorded.Reasons.Single(), "Tools/Tools.csproj");
    }

    [TestMethod]
    public async Task ProviderPublish_WithoutAComputedEntryForItsPath_RecordsPartial()
    {
        await Index("c1", providerCoverage: new Dictionary<string, SnapshotCoverage>());

        var recorded = ProviderCoverageRow();
        Assert.IsNotNull(recorded);
        Assert.AreEqual(SnapshotCoverageVerdict.Partial, recorded.Verdict,
            "a provider whose coverage was not computed cannot be proven complete");
        StringAssert.Contains(recorded.Reasons.Single(), "not computed");
    }

    [TestMethod]
    public async Task ProviderPublish_WithoutProviderCoverage_RecordsNothing()
    {
        await Index("c1", providerCoverage: null);

        Assert.IsNotNull(ProviderSnapshotId(), "the provider is still published");
        Assert.IsNull(ProviderCoverageRow(), "a local (CLI/daemon) index computes no coverage, so none is recorded");
    }

    [TestMethod]
    public async Task ExplicitGeneration_RebuildsProviderWithoutMutatingTheOldParentOrProvider()
    {
        await Index("c1", new Dictionary<string, SnapshotCoverage> { [SubmodulePath] = ProviderPartial });
        var conn = _db.GetConnection();
        var snapshots = new SnapshotStore(conn);
        var coverage = new SnapshotCoverageStore(conn);
        var oldParentId = ParentSnapshotId("c1");
        var oldProviderId = ProviderSnapshotId()!.Value;
        var oldProvider = snapshots.GetById(oldProviderId)!;
        var oldProjects = snapshots.GetSnapshotProjectIds(oldProviderId).ToArray();
        var newIdentity = new SnapshotIdentity
        {
            RepositoryRemoteUrl = ProviderRemote, CommitSha = ProviderCommit,
            SchemaVersion = oldProvider.SchemaVersion, AnalyzerVersion = oldProvider.AnalyzerVersion,
            ConfigHash = oldProvider.ConfigHash, ToolchainFingerprint = oldProvider.ToolchainFingerprint!,
            RebuildGeneration = "restore-fixed"
        };

        await Index("c1", new Dictionary<string, SnapshotCoverage> { [SubmodulePath] = ProviderComplete },
            rebuildGeneration: "restore-fixed");

        var rebuiltProvider = snapshots.GetByIdentityHash(newIdentity.Hash)!;
        Assert.IsNotNull(rebuiltProvider);
        Assert.AreNotEqual(oldProviderId, rebuiltProvider.Id);
        Assert.AreEqual(SnapshotStatus.Complete, rebuiltProvider.Status);
        Assert.AreEqual(SnapshotCoverageVerdict.Complete, coverage.Get(rebuiltProvider.Id)!.Verdict);
        Assert.AreEqual(oldProvider.IdentityHash, coverage.Get(rebuiltProvider.Id)!.Rebuild!.OriginalIdentityHash);
        Assert.AreEqual("restore-fixed", coverage.Get(rebuiltProvider.Id)!.Rebuild!.Generation);
        Assert.AreEqual(oldProvider, snapshots.GetById(oldProviderId), "the old provider catalog row is untouched");
        CollectionAssert.AreEqual(oldProjects, snapshots.GetSnapshotProjectIds(oldProviderId).ToArray());
        Assert.AreEqual(SnapshotCoverageVerdict.Partial, coverage.Get(oldProviderId)!.Verdict);
        Assert.IsNull(coverage.Get(oldProviderId)!.Rebuild);
        Assert.AreEqual(SnapshotStatus.Complete, snapshots.GetById(oldParentId)!.Status);
        Assert.IsNull(coverage.Get(oldParentId)!.Rebuild);
        var newParent = snapshots.GetSelectedSnapshotIdForRepositoryBranch("https://github.com/org/parent", "main")!.Value;
        Assert.AreNotEqual(oldParentId, newParent);
        Assert.AreEqual(snapshots.GetById(oldParentId)!.IdentityHash, coverage.Get(newParent)!.Rebuild!.OriginalIdentityHash);

        await Index("c1", new Dictionary<string, SnapshotCoverage> { [SubmodulePath] = ProviderPartial },
            rebuildGeneration: "restore-fixed");
        Assert.AreEqual(SnapshotCoverageVerdict.Complete, coverage.Get(rebuiltProvider.Id)!.Verdict,
            "repeating the token never backfills or rewrites the newly published provider");
    }

    [TestMethod]
    public async Task ReusedCompleteProvider_KeepsItsRow_NoBackfillNoRewrite()
    {
        await Index("c1", providerCoverage: new Dictionary<string, SnapshotCoverage> { [SubmodulePath] = ProviderPartial });
        var provider = ProviderSnapshotId();

        // A second parent commit pinning the SAME provider commit reuses the complete provider (dedup).
        await Index("c2", providerCoverage: new Dictionary<string, SnapshotCoverage> { [SubmodulePath] = ProviderComplete });

        Assert.AreEqual(provider, ProviderSnapshotId(), "the provider snapshot is deduplicated, not rebuilt");
        Assert.AreEqual(SnapshotCoverageVerdict.Partial, ProviderCoverageRow()!.Verdict,
            "a reused complete provider keeps its first-publish verdict (#119: no backfill, no rewrite)");
    }

    [TestMethod]
    public async Task RowlessCompleteProvider_IsNotBackfilledOnReuse()
    {
        await Index("c1", providerCoverage: null);

        await Index("c2", providerCoverage: new Dictionary<string, SnapshotCoverage> { [SubmodulePath] = ProviderComplete });

        Assert.IsNull(ProviderCoverageRow(), "a provider published without coverage stays 'not recorded' when reused");
    }

    [TestMethod]
    public async Task RebuiltProvider_RecordsAFreshRow_WithoutThrowing()
    {
        await Index("c1", providerCoverage: new Dictionary<string, SnapshotCoverage> { [SubmodulePath] = ProviderPartial });
        var provider = ProviderSnapshotId()!.Value;
        // Model a provider generation that must be rebuilt (e.g. abandoned) while a row from its earlier
        // publish is still on disk: re-staging it must drop the stale row instead of tripping the
        // record-once invariant.
        new SnapshotStore(_db.GetConnection()).MarkStatus(provider, SnapshotStatus.Failed);

        await Index("c2", providerCoverage: new Dictionary<string, SnapshotCoverage> { [SubmodulePath] = ProviderComplete });

        Assert.AreEqual(provider, ProviderSnapshotId(), "the same identity is rebuilt in place");
        Assert.AreEqual(SnapshotStatus.Complete, new SnapshotStore(_db.GetConnection()).GetById(provider)!.Status);
        Assert.AreEqual(SnapshotCoverageVerdict.Complete, ProviderCoverageRow()!.Verdict,
            "the rebuild records ITS verdict, replacing the stale row");
    }

    [TestMethod]
    public async Task GrownProvider_KeepsItsFirstPublishVerdict()
    {
        await Index("c1", providerCoverage: new Dictionary<string, SnapshotCoverage> { [SubmodulePath] = ProviderPartial });
        var provider = ProviderSnapshotId();

        // A later parent reaches a provider project no earlier parent did: #53 republishes the SAME provider
        // snapshot id. Coverage is immutable, so its first-publish row stands (and the republish must not throw).
        await Index("c2", providerCoverage: new Dictionary<string, SnapshotCoverage> { [SubmodulePath] = ProviderComplete },
            extraProviderProject: true);

        Assert.AreEqual(provider, ProviderSnapshotId());
        Assert.AreEqual(2, ProviderProjectCount(provider!.Value), "the provider grew in place (#53)");
        Assert.AreEqual(SnapshotCoverageVerdict.Partial, ProviderCoverageRow()!.Verdict);
    }

    [TestMethod]
    public void RecordProviderCoverage_MergesSeveralPathsWorstOf()
    {
        var conn = _db.GetConnection();
        var provider = SeedPendingProvider(conn);
        var ctx = new SnapshotContext
        {
            RepositoryRemoteUrl = "https://github.com/org/parent",
            CommitSha = "c1",
            BranchName = "main",
            ProviderCoverage = new Dictionary<string, SnapshotCoverage>
            {
                ["libs/a"] = ProviderComplete,
                ["libs/b"] = ProviderPartial
            }
        };

        IndexOrchestrator.RecordProviderCoverage(conn, provider, ["libs/a", "libs/b", "libs/c"], ctx, 1);

        var recorded = new SnapshotCoverageStore(conn).Get(provider)!;
        Assert.AreEqual(SnapshotCoverageVerdict.Partial, recorded.Verdict,
            "one submodule path pinning the same commit with a gap makes the shared provider partial");
        Assert.AreEqual(2, recorded.Reasons.Count, "every distinct partial reason is kept (the gap and the uncomputed path)");
    }

    // ---- helpers --------------------------------------------------------------------------------

    private Task Index(string parentCommit, IReadOnlyDictionary<string, SnapshotCoverage>? providerCoverage,
        bool extraProviderProject = false, string? rebuildGeneration = null)
    {
        var orchestrator = new IndexOrchestrator(_db, useDocumentExtractor: true)
        {
            SubmoduleDiscoverer = _ => Task.FromResult(new List<SubmoduleInfo>
            {
                new() { Path = SubmodulePath, CommitSha = ProviderCommit, RemoteUrl = ProviderRemote }
            })
        };
        var context = new SnapshotContext
        {
            RepositoryRemoteUrl = "https://github.com/org/parent",
            CommitSha = parentCommit,
            BranchName = "main",
            RebuildGeneration = rebuildGeneration,
            ExpectedHeadCommit = rebuildGeneration is null ? null : parentCommit,
            Coverage = new SnapshotCoverage { Verdict = SnapshotCoverageVerdict.Partial, Reasons = ["parent gap"] },
            ProviderCoverage = providerCoverage
        };
        return orchestrator.IndexSolutionAsync(BuildSolution(extraProviderProject), snapshotContext: context);
    }

    private Solution BuildSolution(bool extraProviderProject)
    {
        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution;
        solution = AddProject(solution, Path.Combine("src", "App"), "App",
            "namespace App { public class Widget { public int Size() => 1; } }");
        solution = AddProject(solution, Path.Combine("libs", "mix", "Mix"), "Mix",
            "namespace Mix { public class Combiner { public int Combine() => 2; } }");
        if (extraProviderProject)
            solution = AddProject(solution, Path.Combine("libs", "mix", "Extra"), "Extra",
                "namespace Extra { public class Helper { public int Help() => 3; } }");
        return solution;
    }

    private Solution AddProject(Solution solution, string relativeDir, string name, string source)
    {
        var projectDir = Path.Combine(_root, relativeDir);
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

    private long? ProviderSnapshotId()
    {
        using var cmd = _db.GetConnection().CreateCommand();
        cmd.CommandText = "SELECT id FROM snapshots WHERE is_provider = 1;";
        using var reader = cmd.ExecuteReader();
        long? id = null;
        while (reader.Read())
        {
            Assert.IsNull(id, "exactly one provider snapshot is expected");
            id = reader.GetInt64(0);
        }
        return id;
    }

    private long ParentSnapshotId(string commit)
    {
        using var cmd = _db.GetConnection().CreateCommand();
        cmd.CommandText = """
            SELECT s.id FROM snapshots s JOIN commits c ON c.id = s.commit_id
            WHERE s.is_provider = 0 AND c.commit_sha = @sha;
            """;
        cmd.Parameters.AddWithValue("@sha", commit);
        return (long)cmd.ExecuteScalar()!;
    }

    private SnapshotCoverage? ProviderCoverageRow() =>
        ProviderSnapshotId() is long id ? new SnapshotCoverageStore(_db.GetConnection()).Get(id) : null;

    private long ProviderProjectCount(long providerSnapshotId)
    {
        using var cmd = _db.GetConnection().CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM snapshot_projects WHERE snapshot_id = @s;";
        cmd.Parameters.AddWithValue("@s", providerSnapshotId);
        return (long)cmd.ExecuteScalar()!;
    }

    private static long SeedPendingProvider(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        var snapshots = new SnapshotStore(conn);
        var repo = snapshots.EnsureProviderRepository(ProviderRemote, 1);
        var identity = new SnapshotIdentity
        {
            RepositoryRemoteUrl = ProviderRemote,
            CommitSha = ProviderCommit,
            SchemaVersion = IndexDatabase.SnapshotSchemaVersion,
            AnalyzerVersion = "test",
            ToolchainFingerprint = "test"
        };
        return snapshots.BeginPending(identity, repo, null, null, 1, isProvider: true).id;
    }
}
