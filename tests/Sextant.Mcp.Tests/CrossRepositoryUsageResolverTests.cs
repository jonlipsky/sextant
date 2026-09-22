using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Mcp;
using Sextant.Store;

namespace Sextant.Mcp.Tests;

/// <summary>
/// Phase 12 — the cross-repository usage resolver's fail-closed authorization seam (criterion 4) and the
/// #32 / spec prohibition on FQN-only cross-repo matches. Drives the real
/// <see cref="CrossRepositoryUsageResolver"/> over a direct-seeded provider + two consumers with an
/// injected <see cref="IReadAuthorizer"/>, asserting that an inaccessible repository contributes NEITHER
/// a result NOR any existence/count metadata, and that an FQN with no stable provider identity is
/// reported as "unresolved", never as an empty (misleading) "no usages" result.
/// </summary>
[TestClass]
public class CrossRepositoryUsageResolverTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SqliteConnection _conn = null!;

    private const string ProviderUrl = "https://github.com/org/mixandmatch";
    private const string ProviderFqn = "global::Mix.Combiner";
    private const string ProviderKey = "M:Mix.Combiner.Combine";
    private const string ProviderCommit = "prov_commit_aaaa";
    private const string AppAUrl = "https://github.com/org/appA";
    private const string AppBUrl = "https://github.com/org/appB";

    [TestInitialize]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_xrepo_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _conn = _db.GetConnection();
    }

    [TestCleanup]
    public void Cleanup() => SqliteTestDatabase.Delete(_dbPath, _db);

    [TestMethod]
    public void Resolve_AllowAll_ReturnsBothConsumers()
    {
        SeedProviderAndTwoConsumers();

        var result = CrossRepositoryUsageResolver.Resolve(
            _conn, ProviderUrl, ProviderFqn, CrossRepoUsageScope.DefaultHeads, AllowAllReadAuthorizer.Instance);

        Assert.IsTrue(result.StableIdentityResolved, "the FQN resolved to a stable provider key");
        Assert.AreEqual(1, result.ResolvedSymbolKeyCount);
        Assert.AreEqual(2, result.Usages.Count, "both authorized consumers contribute a usage");
    }

    [TestMethod]
    public void Resolve_DeniedRepository_ContributesNoResultsNorMetadata()
    {
        SeedProviderAndTwoConsumers();

        // Deny appB: it must contribute NEITHER a usage row NOR any count/existence signal.
        var result = CrossRepositoryUsageResolver.Resolve(
            _conn, ProviderUrl, ProviderFqn, CrossRepoUsageScope.DefaultHeads, new DenyRepositoryAuthorizer(AppBUrl));

        Assert.IsTrue(result.StableIdentityResolved);
        Assert.AreEqual(1, result.Usages.Count, "only the authorized consumer contributes (fail closed)");
        Assert.AreEqual(AppAUrl, result.Usages[0].ConsumerRepositoryUrl);
        Assert.IsFalse(result.Usages.Any(u => u.ConsumerRepositoryUrl == AppBUrl),
            "the denied repository leaks no result and no existence/count metadata");
    }

    [TestMethod]
    public void Resolve_AllRepositoriesDenied_ReturnsResolvedButEmpty_NotUnresolved()
    {
        SeedProviderAndTwoConsumers();

        var result = CrossRepositoryUsageResolver.Resolve(
            _conn, ProviderUrl, ProviderFqn, CrossRepoUsageScope.DefaultHeads, new DenyAllRepositoriesAuthorizer());

        // The FQN DID resolve to a stable key; there are simply no authorized consumers. This is distinct
        // from "no stable identity" — the caller must not conflate an authorization denial with an
        // unresolvable symbol.
        Assert.IsTrue(result.StableIdentityResolved, "the symbol identity resolved even though every consumer is denied");
        Assert.AreEqual(0, result.Usages.Count, "no authorized consumer contributes");
    }

    [TestMethod]
    public void Resolve_FqnWithNoStableProviderIdentity_IsReportedUnresolved_NotEmptyUsages()
    {
        SeedProviderAndTwoConsumers();

        // A name that does not resolve to any provider stable key: an FQN-only cross-repo match is
        // PROHIBITED (#32 / spec), so the resolver reports it unresolved rather than "no usages".
        var result = CrossRepositoryUsageResolver.Resolve(
            _conn, ProviderUrl, "global::Mix.DoesNotExist", CrossRepoUsageScope.DefaultHeads, AllowAllReadAuthorizer.Instance);

        Assert.IsFalse(result.StableIdentityResolved, "an unresolved FQN is reported as such, never as an empty usage set");
        Assert.AreEqual(0, result.ResolvedSymbolKeyCount);
        Assert.AreEqual(0, result.Usages.Count);
    }

    [TestMethod]
    public void Resolve_FqnMatchingMultipleProviderKeys_UnionsUsages_AndReportsAllDistinctKeys()
    {
        // A display FQN is NOT a unique identity: two DISTINCT provider stable keys (e.g. overloads) can
        // share one FQN. The query unions both keys' usages, but must REPORT both keys so the caller can
        // tell the FQN was ambiguous and narrow by stable key (the tool surfaces this in meta.ambiguous).
        var (providerSnap, providerProj, providerRepo, symbolId1) = SeedProvider();
        const string ProviderKey2 = ProviderKey + "(System.Int32)";
        var symbolId2 = InsertSymbol(providerProj, ProviderKey2, ProviderFqn);

        SeedConsumer(AppAUrl, "appA_head", "logical_appA", "src/A/UseA.cs", 10,
            providerSnap, providerProj, providerRepo, symbolId1);
        SeedConsumer(AppBUrl, "appB_head", "logical_appB", "src/B/UseB.cs", 20,
            providerSnap, providerProj, providerRepo, symbolId2);

        var result = CrossRepositoryUsageResolver.Resolve(
            _conn, ProviderUrl, ProviderFqn, CrossRepoUsageScope.DefaultHeads, AllowAllReadAuthorizer.Instance);

        Assert.IsTrue(result.StableIdentityResolved);
        Assert.AreEqual(2, result.ResolvedSymbolKeyCount, "the ambiguous FQN resolves to BOTH distinct stable keys");
        CollectionAssert.AreEquivalent(
            new[] { ProviderKey, ProviderKey2 }, result.ResolvedSymbolKeys.ToArray(),
            "both distinct provider keys are reported so the caller can disambiguate, not silently merged away");
        Assert.AreEqual(ProviderKey, result.ResolvedSymbolKeys[0], "keys are returned in a deterministic (symbol_key) order");
        Assert.AreEqual(2, result.Usages.Count, "usages of both keys are unioned (one per consumer)");
    }

    [TestMethod]
    public void ResolveConsumers_DeniedRepository_IsExcluded()
    {
        SeedProviderAndTwoConsumers();

        var consumers = CrossRepositoryUsageResolver.ResolveConsumers(
            _conn, ProviderUrl, providerCommitSha: null, CrossRepoUsageScope.DefaultHeads, new DenyRepositoryAuthorizer(AppBUrl));

        Assert.AreEqual(1, consumers.Count, "the reverse-dependency listing also fails closed per repository");
        Assert.AreEqual(AppAUrl, consumers[0].ConsumerRepositoryUrl);
    }

    // ==== authorizers ==========================================================================

    private sealed class DenyRepositoryAuthorizer(string deniedUrl) : IReadAuthorizer
    {
        public ReadAuthorization Authorize(SnapshotRow? selected) => ReadAuthorization.Allow;
        public ReadAuthorization AuthorizeRepository(long repositoryId, string remoteUrl) =>
            remoteUrl == deniedUrl ? ReadAuthorization.Deny("not authorized for " + remoteUrl) : ReadAuthorization.Allow;
    }

    private sealed class DenyAllRepositoriesAuthorizer : IReadAuthorizer
    {
        public ReadAuthorization Authorize(SnapshotRow? selected) => ReadAuthorization.Allow;
        public ReadAuthorization AuthorizeRepository(long repositoryId, string remoteUrl) =>
            ReadAuthorization.Deny("no cross-repo access");
    }

    // ==== seeding (direct, no git/Roslyn) ======================================================

    private void SeedProviderAndTwoConsumers()
    {
        var (providerSnap, providerProj, providerRepo, symbolId) = SeedProvider();
        SeedConsumer(AppAUrl, "appA_head", "logical_appA", "src/A/UseA.cs", 10,
            providerSnap, providerProj, providerRepo, symbolId);
        SeedConsumer(AppBUrl, "appB_head", "logical_appB", "src/B/UseB.cs", 20,
            providerSnap, providerProj, providerRepo, symbolId);
    }

    private (long snap, long proj, long repo, long symbol) SeedProvider()
    {
        var store = new SnapshotStore(_conn);
        var repoId = store.EnsureProviderRepository(ProviderUrl, now: 1);
        var commitId = store.EnsureCommit(repoId, ProviderCommit, null, now: 1);
        var identity = new SnapshotIdentity
        {
            RepositoryRemoteUrl = ProviderUrl,
            CommitSha = ProviderCommit,
            SchemaVersion = IndexDatabase.LatestSchemaVersion,
            AnalyzerVersion = IndexConfigurationHash.AnalyzerVersion,
            ConfigHash = "cfg",
            ToolchainFingerprint = "tc"
        };
        var (snapId, _, _) = store.BeginPending(identity, repoId, commitId, null, now: 1, isProvider: true);
        var logicalId = store.EnsureLogicalProject(repoId, "logical_mix", "lib/Mix.csproj", "net10.0", now: 1);
        var projectId = new ProjectStore(_conn).UpsertSnapshotProject(
            new ProjectIdentity { CanonicalId = "logical_mix", GitRemoteUrl = ProviderUrl, RepoRelativePath = "lib/Mix.csproj", TargetFramework = "net10.0" },
            snapId, logicalId, lastIndexedAt: 1);
        store.MapProject(snapId, projectId);
        var symbolId = InsertSymbol(projectId, ProviderKey, ProviderFqn);
        store.MarkComplete(snapId, publishedAt: 1);
        return (snapId, projectId, repoId, symbolId);
    }

    private void SeedConsumer(
        string repoUrl, string commitSha, string canonical, string usageFile, int line,
        long providerSnap, long providerProj, long providerRepo, long providerSymbolId)
    {
        var store = new SnapshotStore(_conn);
        var repoId = store.EnsureRepository(repoUrl, now: 1);
        var commitId = store.EnsureCommit(repoId, commitSha, $"tree_{commitSha}", now: 1);
        var identity = new SnapshotIdentity
        {
            RepositoryRemoteUrl = repoUrl,
            CommitSha = commitSha,
            TreeSha = $"tree_{commitSha}",
            SchemaVersion = IndexDatabase.LatestSchemaVersion,
            AnalyzerVersion = IndexConfigurationHash.AnalyzerVersion,
            ConfigHash = "cfg",
            ToolchainFingerprint = "tc"
        };
        var (snapId, _, _) = store.BeginPending(identity, repoId, commitId, null, now: 1);
        var logicalId = store.EnsureLogicalProject(repoId, canonical, "src/App/App.csproj", "net10.0", now: 1);
        var projectId = new ProjectStore(_conn).UpsertSnapshotProject(
            new ProjectIdentity { CanonicalId = canonical, GitRemoteUrl = repoUrl, RepoRelativePath = "src/App/App.csproj", TargetFramework = "net10.0" },
            snapId, logicalId, lastIndexedAt: 1);
        store.MapProject(snapId, projectId);
        var fileVersionId = InsertFileVersion(projectId, usageFile);
        InsertOccurrence(projectId, providerSymbolId, fileVersionId, line);
        store.MarkComplete(snapId, publishedAt: 1);
        var branchId = store.EnsureBranch(repoId, "main", isDefault: true, now: 2);
        store.SetBranchPointer(branchId, snapId, now: 3);
        new SnapshotDependencyStore(_conn).Insert(new SnapshotDependencyEdge
        {
            ConsumerSnapshotId = snapId,
            ConsumerProjectId = projectId,
            ProviderSnapshotId = providerSnap,
            ProviderProjectId = providerProj,
            ProviderRepositoryId = providerRepo,
            ProviderCommitSha = ProviderCommit,
            ReferenceKind = "submodule_ref",
            SubmoduleDirty = false,
            CreatedAt = 1
        });
    }

    private long InsertSymbol(long projectId, string symbolKey, string fqn)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO symbols
                (project_id, symbol_key, fully_qualified_name, display_name, kind, accessibility,
                 line_start, line_end, last_indexed_at)
            VALUES (@p, @k, @fqn, @dn, 0, 0, 1, 1, 1)
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("@p", projectId);
        cmd.Parameters.AddWithValue("@k", symbolKey);
        cmd.Parameters.AddWithValue("@fqn", fqn);
        cmd.Parameters.AddWithValue("@dn", fqn.Split('.').Last());
        return (long)cmd.ExecuteScalar()!;
    }

    private long InsertFileVersion(long projectId, string repoRelativePath)
    {
        long fileId;
        using (var f = _conn.CreateCommand())
        {
            f.CommandText = "INSERT INTO files (project_id, repo_relative_path) VALUES (@p, @path) RETURNING id;";
            f.Parameters.AddWithValue("@p", projectId);
            f.Parameters.AddWithValue("@path", repoRelativePath);
            fileId = (long)f.ExecuteScalar()!;
        }
        using var fv = _conn.CreateCommand();
        fv.CommandText = "INSERT INTO file_versions (file_id, content_hash, last_indexed_at) VALUES (@f, @hash, 1) RETURNING id;";
        fv.Parameters.AddWithValue("@f", fileId);
        fv.Parameters.AddWithValue("@hash", System.Text.Encoding.UTF8.GetBytes($"hash_{repoRelativePath}"));
        return (long)fv.ExecuteScalar()!;
    }

    private void InsertOccurrence(long inProjectId, long targetSymbolId, long fileVersionId, int line)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO occurrences (in_project_id, target_symbol_id, source_symbol_id, file_version_id, line, col, kind, flags)
            VALUES (@p, @t, NULL, @fv, @line, 5, @kind, 0);
            """;
        cmd.Parameters.AddWithValue("@p", inProjectId);
        cmd.Parameters.AddWithValue("@t", targetSymbolId);
        cmd.Parameters.AddWithValue("@fv", fileVersionId);
        cmd.Parameters.AddWithValue("@line", line);
        cmd.Parameters.AddWithValue("@kind", (int)ReferenceKind.Invocation);
        cmd.ExecuteNonQuery();
    }
}
