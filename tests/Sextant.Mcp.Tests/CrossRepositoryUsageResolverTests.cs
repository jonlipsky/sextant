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
    // The name agents type for the provider method (no `global::`, no parameter list); stored member rows carry a
    // documentation-ID key and a bare display name, as the extractor writes them.
    private const string ProviderFqn = "Mix.Combiner.Combine";
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
    public void Resolve_EnforcingProviderDenied_ReportsUnresolved_NoProviderSymbolOracle()
    {
        SeedProviderAndTwoConsumers();

        // Under ENFORCEMENT, a caller with no access to the PROVIDER repo must not learn its symbol
        // resolved: the result collapses to the SAME "no stable identity" outcome as a nonexistent symbol,
        // so StableIdentityResolved is a provider-symbol existence oracle no longer (Phase 17, criterion 1).
        var result = CrossRepositoryUsageResolver.Resolve(
            _conn, ProviderUrl, ProviderFqn, CrossRepoUsageScope.DefaultHeads, new DenyProviderAuthorizer(ProviderUrl));

        Assert.IsFalse(result.StableIdentityResolved,
            "a denied provider repo reveals no stable identity (byte-identical to a nonexistent symbol)");
        Assert.AreEqual(0, result.ResolvedSymbolKeyCount);
        Assert.AreEqual(0, result.Usages.Count);
    }

    [TestMethod]
    public void ResolveConsumers_EnforcingProviderDenied_ReturnsEmpty_NoConsumerExistenceLeak()
    {
        SeedProviderAndTwoConsumers();

        var consumers = CrossRepositoryUsageResolver.ResolveConsumers(
            _conn, ProviderUrl, providerCommitSha: null, CrossRepoUsageScope.DefaultHeads,
            new DenyProviderAuthorizer(ProviderUrl));

        Assert.AreEqual(0, consumers.Count,
            "a caller denied the provider repo learns nothing about its consumers (fail closed)");
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
    public void Resolve_NameMatchingMultipleProviderSymbols_IsAmbiguous_AndReadsNoUsages()
    {
        // A name is NOT a unique identity: two DISTINCT provider stable keys (overloads) share it. Unioning their usages
        // would answer for a symbol the caller did not name (#163), so the resolver reports the ambiguity with both
        // symbols and reads no usages; the tool turns that into an ambiguous_symbol error listing them.
        var (providerSnap, providerProj, providerRepo, symbolId1) = SeedProvider();
        const string ProviderKey2 = ProviderKey + "(System.Int32)";
        var symbolId2 = InsertSymbol(providerProj, ProviderKey2, "Mix.Combiner.Combine(int)");

        SeedConsumer(AppAUrl, "appA_head", "logical_appA", "src/A/UseA.cs", 10,
            providerSnap, providerProj, providerRepo, symbolId1);
        SeedConsumer(AppBUrl, "appB_head", "logical_appB", "src/B/UseB.cs", 20,
            providerSnap, providerProj, providerRepo, symbolId2);

        var result = CrossRepositoryUsageResolver.Resolve(
            _conn, ProviderUrl, ProviderFqn, CrossRepoUsageScope.DefaultHeads, AllowAllReadAuthorizer.Instance);

        Assert.IsTrue(result.Ambiguous);
        Assert.IsFalse(result.StableIdentityResolved);
        Assert.AreEqual(2, result.AmbiguousMatchCount);
        CollectionAssert.AreEquivalent(
            new[] { ProviderKey, ProviderKey2 }, result.AmbiguousMatches.Select(s => s.SymbolKey).ToArray(),
            "both distinct provider symbols are reported so the caller can pick one");
        Assert.AreEqual(0, result.Usages.Count, "no usages are read for an ambiguous name");

        // Naming one overload (with its parameter list) resolves it, and only its usages are returned.
        var overload = CrossRepositoryUsageResolver.Resolve(
            _conn, ProviderUrl, "global::Mix.Combiner.Combine(int)", CrossRepoUsageScope.DefaultHeads,
            AllowAllReadAuthorizer.Instance);
        Assert.IsTrue(overload.StableIdentityResolved);
        CollectionAssert.AreEqual(new[] { ProviderKey2 }, overload.ResolvedSymbolKeys.ToArray());
        Assert.AreEqual(1, overload.Usages.Count);
        Assert.AreEqual(AppBUrl, overload.Usages[0].ConsumerRepositoryUrl);
    }

    [TestMethod]
    public void Resolve_ProviderProjectId_ReadsOnlyThatProjectVersionsUsages()
    {
        // The provider symbol is declared in two project versions (net10.0 and net8.0) that different consumers pin
        // and use (#224). Without a provider project the usages of both are unioned (today's answer); naming one
        // version reads only its own, and a version no consumer pins has no stable identity.
        var (providerSnap, net10Project, providerRepo, net10Symbol) = SeedProvider();
        var store = new SnapshotStore(_conn);
        var logicalId = store.EnsureLogicalProject(providerRepo, "logical_mix_net8", "lib/Mix.csproj", "net8.0", now: 1);
        var net8Project = new ProjectStore(_conn).UpsertSnapshotProject(
            new ProjectIdentity { CanonicalId = "logical_mix_net8", GitRemoteUrl = ProviderUrl, RepoRelativePath = "lib/Mix.csproj", TargetFramework = "net8.0" },
            providerSnap, logicalId, lastIndexedAt: 1);
        store.MapProject(providerSnap, net8Project);
        var net8Symbol = InsertSymbol(net8Project, ProviderKey, "Mix.Combiner.Combine()");
        SeedConsumer(AppAUrl, "appA_head", "logical_appA", "src/A/UseA.cs", 10,
            providerSnap, net10Project, providerRepo, net10Symbol);
        SeedConsumer(AppBUrl, "appB_head", "logical_appB", "src/B/UseB.cs", 20,
            providerSnap, net8Project, providerRepo, net8Symbol);

        var all = CrossRepositoryUsageResolver.Resolve(
            _conn, ProviderUrl, ProviderFqn, CrossRepoUsageScope.DefaultHeads, AllowAllReadAuthorizer.Instance);
        var net10 = CrossRepositoryUsageResolver.Resolve(
            _conn, ProviderUrl, ProviderFqn, new CrossRepoUsageScope { ProviderProjectCanonicalId = "logical_mix" },
            AllowAllReadAuthorizer.Instance);
        var net8 = CrossRepositoryUsageResolver.Resolve(
            _conn, ProviderUrl, ProviderFqn, new CrossRepoUsageScope { ProviderProjectCanonicalId = "logical_mix_net8" },
            AllowAllReadAuthorizer.Instance);
        var unpinned = CrossRepositoryUsageResolver.Resolve(
            _conn, ProviderUrl, ProviderFqn, new CrossRepoUsageScope { ProviderProjectCanonicalId = "no_such_project" },
            AllowAllReadAuthorizer.Instance);

        CollectionAssert.AreEquivalent(new[] { AppAUrl, AppBUrl }, all.Usages.Select(u => u.ConsumerRepositoryUrl).ToArray());
        CollectionAssert.AreEqual(new[] { AppAUrl }, net10.Usages.Select(u => u.ConsumerRepositoryUrl).ToArray());
        CollectionAssert.AreEqual(new[] { AppBUrl }, net8.Usages.Select(u => u.ConsumerRepositoryUrl).ToArray());
        Assert.IsFalse(unpinned.StableIdentityResolved);
        Assert.AreEqual(0, unpinned.Usages.Count);
    }

    [TestMethod]
    public void Tool_ProjectId_NamesAProviderProjectInAnotherRepository_AndAnUnknownIdIsNoOracle()
    {
        // Through the tool, with the provider in a different repository than the selected snapshot (#224): the
        // project_id is read among the versions consumers pin, never the selected snapshot's. An unknown id answers
        // exactly as an id of a provider the caller may not read.
        var (providerSnap, net10Project, providerRepo, net10Symbol) = SeedProvider();
        var store = new SnapshotStore(_conn);
        var logicalId = store.EnsureLogicalProject(providerRepo, "logical_mix_net8", "lib/Mix.csproj", "net8.0", now: 1);
        var net8Project = new ProjectStore(_conn).UpsertSnapshotProject(
            new ProjectIdentity { CanonicalId = "logical_mix_net8", GitRemoteUrl = ProviderUrl, RepoRelativePath = "lib/Mix.csproj", TargetFramework = "net8.0" },
            providerSnap, logicalId, lastIndexedAt: 1);
        store.MapProject(providerSnap, net8Project);
        var net8Symbol = InsertSymbol(net8Project, ProviderKey, "Mix.Combiner.Combine()");
        SeedConsumer(AppAUrl, "appA_head", "logical_appA", "src/A/UseA.cs", 10,
            providerSnap, net10Project, providerRepo, net10Symbol);
        SeedConsumer(AppBUrl, "appB_head", "logical_appB", "src/B/UseB.cs", 20,
            providerSnap, net8Project, providerRepo, net8Symbol);

        using var provider = new DatabaseProvider(_dbPath);
        string Call(string? projectId) =>
            Sextant.Mcp.Tools.FindCrossRepositoryUsagesTool.FindCrossRepositoryUsages(
                provider, ProviderUrl, ProviderFqn, project_id: projectId);

        var all = Call(null);
        var net8 = Call("logical_mix_net8");
        var net10 = Call("logical_mix");
        var unknown = Call("no_such_project");

        StringAssert.Contains(all, AppAUrl);
        StringAssert.Contains(all, AppBUrl);
        StringAssert.Contains(net8, AppBUrl);
        Assert.IsFalse(net8.Contains(AppAUrl), net8);
        StringAssert.Contains(net10, AppAUrl);
        Assert.IsFalse(net10.Contains(AppBUrl), net10);
        StringAssert.Contains(unknown, "stable identity");
        Assert.IsFalse(unknown.Contains("Unknown project_id"), unknown);
    }

    [TestMethod]
    public void Tool_ProjectId_WithOneConsumerRepository_ReadsTheProvidersPinnedVersion_NotTheSelectedSnapshots()
    {
        // With a single consumer repository its snapshot is THE selected one, and it holds no provider project: the
        // project_id still names the provider project version the consumer pins (#224), and an id of an unpinned
        // version answers like an unknown one.
        var (providerSnap, net10Project, providerRepo, _) = SeedProvider();
        var store = new SnapshotStore(_conn);
        var logicalId = store.EnsureLogicalProject(providerRepo, "logical_mix_net8", "lib/Mix.csproj", "net8.0", now: 1);
        var net8Project = new ProjectStore(_conn).UpsertSnapshotProject(
            new ProjectIdentity { CanonicalId = "logical_mix_net8", GitRemoteUrl = ProviderUrl, RepoRelativePath = "lib/Mix.csproj", TargetFramework = "net8.0" },
            providerSnap, logicalId, lastIndexedAt: 1);
        store.MapProject(providerSnap, net8Project);
        var net8Symbol = InsertSymbol(net8Project, ProviderKey, "Mix.Combiner.Combine()");
        SeedConsumer(AppBUrl, "appB_head", "logical_appB", "src/B/UseB.cs", 20,
            providerSnap, net8Project, providerRepo, net8Symbol);

        using var provider = new DatabaseProvider(_dbPath);
        string Call(string projectId) =>
            Sextant.Mcp.Tools.FindCrossRepositoryUsagesTool.FindCrossRepositoryUsages(
                provider, ProviderUrl, ProviderFqn, project_id: projectId);

        var net8 = Call("logical_mix_net8");
        var unpinned = Call("logical_mix");
        var unknown = Call("no_such_project");

        StringAssert.Contains(net8, AppBUrl);
        StringAssert.Contains(unpinned, "stable identity");
        Assert.AreEqual(
            System.Text.Json.JsonDocument.Parse(unpinned).RootElement.GetProperty("message").GetString(),
            System.Text.Json.JsonDocument.Parse(unknown).RootElement.GetProperty("message").GetString());
        Assert.AreNotEqual(0, net10Project);
    }

    [TestMethod]
    public void Resolve_SymbolOutsideTheProviderRepository_IsNotResolved()
    {
        // The name is resolved only among the provider's pinned projects: the same name in a consumer repository is
        // never taken for the provider's symbol (no FQN-only cross-repo match, #32).
        SeedProviderAndTwoConsumers();
        var appProject = new ProjectStore(_conn).GetByCanonicalId("logical_appA")!.Value.id;
        InsertSymbol(appProject, "M:App.Local.Combine", "App.Local.Combine()");

        var result = CrossRepositoryUsageResolver.Resolve(
            _conn, ProviderUrl, "App.Local.Combine", CrossRepoUsageScope.DefaultHeads, AllowAllReadAuthorizer.Instance);

        Assert.IsFalse(result.StableIdentityResolved);
        Assert.IsFalse(result.Ambiguous);
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
        // Non-enforcing: these tests assert the PER-REPOSITORY cross-repo gate (AuthorizeRepository) in
        // isolation, with the top-level provider-repository gate (Phase 17) deliberately NOT engaged, so
        // provider resolution still succeeds and only the denied CONSUMER is filtered.
        public bool IsEnforcing => false;
        public ReadAuthorization Authorize(SnapshotRow? selected) => ReadAuthorization.Allow;
        public ReadAuthorization AuthorizeRepository(long repositoryId, string remoteUrl) =>
            remoteUrl == deniedUrl ? ReadAuthorization.Deny("not authorized for " + remoteUrl) : ReadAuthorization.Allow;
    }

    private sealed class DenyAllRepositoriesAuthorizer : IReadAuthorizer
    {
        public bool IsEnforcing => false;
        public ReadAuthorization Authorize(SnapshotRow? selected) => ReadAuthorization.Allow;
        public ReadAuthorization AuthorizeRepository(long repositoryId, string remoteUrl) =>
            ReadAuthorization.Deny("no cross-repo access");
    }

    /// <summary>
    /// An ENFORCING authorizer that denies the provider repository itself. Under enforcement the resolver
    /// must collapse to "no stable identity" (never reveal the provider symbol resolved) so an unauthorized
    /// caller gets no provider-symbol existence oracle (Phase 17, criterion 1).
    /// </summary>
    private sealed class DenyProviderAuthorizer(string providerUrl) : IReadAuthorizer
    {
        public bool IsEnforcing => true;
        public ReadAuthorization Authorize(SnapshotRow? selected) => ReadAuthorization.Allow;
        public ReadAuthorization AuthorizeRepository(long repositoryId, string remoteUrl) =>
            remoteUrl == providerUrl ? ReadAuthorization.Deny("provider not authorized") : ReadAuthorization.Allow;
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
            SchemaVersion = IndexDatabase.SnapshotSchemaVersion,
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
        var symbolId = InsertSymbol(projectId, ProviderKey, "Mix.Combiner.Combine()");
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
            SchemaVersion = IndexDatabase.SnapshotSchemaVersion,
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

    private long InsertSymbol(long projectId, string symbolKey, string signature)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO symbols
                (project_id, symbol_key, fully_qualified_name, display_name, kind, accessibility, signature,
                 line_start, line_end, last_indexed_at)
            VALUES (@p, @k, 'Combine', 'Combine', @kind, 0, @sig, 1, 1, 1)
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("@p", projectId);
        cmd.Parameters.AddWithValue("@k", symbolKey);
        cmd.Parameters.AddWithValue("@kind", (int)SymbolKind.Method);
        cmd.Parameters.AddWithValue("@sig", signature);
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
