using System.Text.Json;
using Sextant.Core;
using Sextant.Mcp;
using Sextant.Mcp.Tools;
using Sextant.Store;

namespace Sextant.Mcp.Tests;

/// <summary>
/// Phase 17 — criterion 1. The real enforced <see cref="PolicyReadAuthorizer"/> that replaces the
/// permissive <see cref="AllowAllReadAuthorizer"/> on the service query plane. Covers the fail-closed
/// decision logic (own-repo allow, foreign-repo deny, unknown token deny, null/unknown repository deny,
/// wildcard admin, disabled-policy passthrough) and — through the real <c>get_index_status</c> tool — that
/// an unauthorized principal's response reveals NO repository name, project name, symbol/reference counts,
/// or existence signal, and that the denial reason is byte-identical whether the repository merely exists
/// but is forbidden or cannot be identified at all (no existence oracle).
/// </summary>
[TestClass]
public class PolicyReadAuthorizerTests
{
    private const string RepoA = "https://github.com/org/a";
    private const string RepoB = "https://github.com/org/b";

    private static ReadAuthorizationPolicy TwoTenant() => new()
    {
        Enabled = true,
        Principals =
        [
            new ReadPrincipal { Token = "tok-a", Repositories = new HashSet<string> { RepoA } },
            new ReadPrincipal { Token = "tok-b", Repositories = new HashSet<string> { RepoB } }
        ]
    };

    private static SnapshotRow Row(long repoId) => new()
    {
        Id = 1, RepositoryId = repoId, IdentityHash = "h", Status = "complete", CreatedAt = 1,
        SchemaVersion = IndexDatabase.LatestSchemaVersion, AnalyzerVersion = IndexConfigurationHash.AnalyzerVersion
    };

    // ==== decision logic (no database) =============================================================

    [TestMethod]
    public void DisabledPolicy_AllowsEverything()
    {
        var authz = new PolicyReadAuthorizer(ReadAuthorizationPolicy.Disabled, () => null, _ => null);
        Assert.IsTrue(authz.Authorize(Row(1)).Allowed, "no policy ⇒ zero-friction local default");
        Assert.IsTrue(authz.Authorize(null).Allowed);
        Assert.IsTrue(authz.AuthorizeRepository(1, RepoA).Allowed);
    }

    [TestMethod]
    public void EnabledPolicy_AuthorizedPrincipal_AllowsOwnRepository()
    {
        var authz = new PolicyReadAuthorizer(TwoTenant(), () => "tok-a", id => id == 1 ? RepoA : RepoB);
        Assert.IsTrue(authz.Authorize(Row(1)).Allowed);
        Assert.IsTrue(authz.AuthorizeRepository(1, RepoA).Allowed);
    }

    [TestMethod]
    public void EnabledPolicy_Principal_DeniedForeignRepository_WithGenericReason()
    {
        var authz = new PolicyReadAuthorizer(TwoTenant(), () => "tok-a", id => id == 2 ? RepoB : RepoA);
        var denied = authz.Authorize(Row(2));
        Assert.IsFalse(denied.Allowed, "a principal cannot read a repository it was not granted (criterion 1)");
        Assert.IsFalse(denied.Reason!.Contains(RepoB), "the denial reason never names the repository");
        Assert.IsFalse(authz.AuthorizeRepository(2, RepoB).Allowed, "cross-repo candidate is denied too");
    }

    [TestMethod]
    public void EnabledPolicy_UnknownToken_Denied()
    {
        var authz = new PolicyReadAuthorizer(TwoTenant(), () => "not-a-real-token", _ => RepoA);
        Assert.IsFalse(authz.Authorize(Row(1)).Allowed);
    }

    [TestMethod]
    public void EnabledPolicy_NullPrincipal_Denied()
    {
        var authz = new PolicyReadAuthorizer(TwoTenant(), () => null, _ => RepoA);
        Assert.IsFalse(authz.Authorize(Row(1)).Allowed, "an anonymous principal is denied under an enabled policy");
    }

    [TestMethod]
    public void EnabledPolicy_NullSelectedSnapshot_FailsClosed()
    {
        var authz = new PolicyReadAuthorizer(TwoTenant(), () => "tok-a", _ => RepoA);
        Assert.IsFalse(authz.Authorize(null).Allowed, "an unidentifiable repository is denied, never allowed");
    }

    [TestMethod]
    public void EnabledPolicy_UnknownRepositoryId_FailsClosed()
    {
        var authz = new PolicyReadAuthorizer(TwoTenant(), () => "tok-a", _ => null);
        Assert.IsFalse(authz.Authorize(Row(99)).Allowed, "a repository id that resolves to no URL is denied");
    }

    [TestMethod]
    public void EnabledPolicy_WildcardPrincipal_AllowsEveryRepository()
    {
        var policy = new ReadAuthorizationPolicy
        {
            Enabled = true,
            Principals = [new ReadPrincipal { Token = "admin", Repositories = new HashSet<string> { "*" } }]
        };
        var authz = new PolicyReadAuthorizer(policy, () => "admin", _ => RepoB);
        Assert.IsTrue(authz.Authorize(Row(5)).Allowed);
        Assert.IsTrue(authz.AuthorizeRepository(9, RepoB).Allowed);
    }

    [TestMethod]
    public void DenialReason_ForbiddenVersusUnidentifiable_IsIdentical_NoExistenceOracle()
    {
        // "exists but forbidden" and "cannot be identified" must return a byte-identical reason so a caller
        // cannot use it to prove a repository exists (criterion 1: no timing-sensitive existence signal).
        var forbidden = new PolicyReadAuthorizer(TwoTenant(), () => "tok-a", _ => RepoB).Authorize(Row(2));
        var unidentifiable = new PolicyReadAuthorizer(TwoTenant(), () => "tok-a", _ => null).Authorize(Row(2));
        Assert.IsFalse(forbidden.Allowed);
        Assert.IsFalse(unidentifiable.Allowed);
        Assert.AreEqual(forbidden.Reason, unidentifiable.Reason, "the denial reason reveals nothing about existence");
    }

    // ==== criterion 1 through the real get_index_status tool =======================================

    [TestMethod]
    public void GetIndexStatus_UnauthorizedPrincipal_RevealsNoDataCountsNamesOrExistence()
    {
        var (dbPath, db, repoId) = SeedSelectedRepo(RepoA);
        try
        {
            var policy = TwoTenant();
            Func<long, string?> resolver = id => id == repoId ? RepoA : null;

            // Principal tok-b is authorized ONLY for repo B, but the selected snapshot is repo A.
            using var denied = new DatabaseProvider(dbPath, new PolicyReadAuthorizer(policy, () => "tok-b", resolver));
            var deniedJson = GetIndexStatusTool.GetIndexStatus(denied);

            var meta = JsonDocument.Parse(deniedJson).RootElement.GetProperty("meta");
            // Phase 17, criterion 1 (revised from the Phase-11 `authorization_denied` code): an
            // unauthorized status read now returns the UNIFORM not-found — no error block — so it is
            // byte-indistinguishable from an unprovisioned/nonexistent index. A distinct code would itself
            // be an existence/authz oracle.
            Assert.IsFalse(meta.TryGetProperty("error", out _),
                "an unauthorized status read must NOT carry a distinct error code (authz/existence oracle)");
            Assert.AreEqual(0, meta.GetProperty("result_count").GetInt32(), "it reveals zero results");
            Assert.IsFalse(deniedJson.Contains(RepoA), "the repository remote URL does not leak");
            Assert.IsFalse(deniedJson.Contains("logical_A"), "the project canonical id does not leak");
            Assert.IsFalse(deniedJson.Contains("symbol_count"), "no symbol/reference counts leak");
            Assert.IsFalse(deniedJson.Contains("storage"), "no storage/existence signal leaks");

            // The authorized principal sees exactly the same index it is entitled to.
            using var allowed = new DatabaseProvider(dbPath, new PolicyReadAuthorizer(policy, () => "tok-a", resolver));
            var allowedJson = GetIndexStatusTool.GetIndexStatus(allowed);
            StringAssert.Contains(allowedJson, RepoA, "the authorized principal reads its own repository");
        }
        finally
        {
            SqliteTestDatabase.Delete(dbPath, db);
        }
    }

    // ==== criterion 1: multi-tenant is queryable-and-scoped, NOT deny-all, and forbidden ≡ nonexistent ==

    [TestMethod]
    public void GetIndexStatus_MultiTenant_AuthorizedPrincipal_ReadsOwnRepoScoped_NotDenyAll()
    {
        var (dbPath, db, repoAId, repoBId) = SeedTwoSelectedRepos();
        try
        {
            var policy = TwoTenant();
            Func<long, string?> resolver = id => id == repoAId ? RepoA : id == repoBId ? RepoB : null;

            // tok-b names its authorized repository B via the request selector: the read must resolve to
            // B's snapshot (queryable), NOT collapse to deny-all just because the catalog is multi-tenant.
            using var provider =
                new DatabaseProvider(dbPath, new PolicyReadAuthorizer(policy, () => "tok-b", resolver))
                { RequestedRepository = () => RepoB };
            var json = GetIndexStatusTool.GetIndexStatus(provider);

            var meta = JsonDocument.Parse(json).RootElement.GetProperty("meta");
            Assert.IsFalse(meta.TryGetProperty("error", out _), "an authorized multi-tenant read is not deny-all");
            Assert.IsTrue(meta.GetProperty("result_count").GetInt32() > 0, "the caller's own repository is queryable");
            StringAssert.Contains(json, RepoB, "the authorized principal reads its own repository");
            Assert.IsFalse(json.Contains(RepoA), "the OTHER tenant's repository is scoped OUT (no cross-tenant leak)");
            Assert.IsFalse(json.Contains("logical_A"), "the other tenant's project canonical id does not leak");
        }
        finally
        {
            SqliteTestDatabase.Delete(dbPath, db);
        }
    }

    [TestMethod]
    public void GetIndexStatus_ForbiddenExistingRepo_ByteIndistinguishableFromNonexistent()
    {
        var (dbPath, db, repoAId, repoBId) = SeedTwoSelectedRepos();
        try
        {
            var policy = TwoTenant();
            Func<long, string?> resolver = id => id == repoAId ? RepoA : id == repoBId ? RepoB : null;

            // (a) tok-b names repo A — it EXISTS in the catalog but the principal is not authorized for it.
            using var forbidden =
                new DatabaseProvider(dbPath, new PolicyReadAuthorizer(policy, () => "tok-b", resolver))
                { RequestedRepository = () => RepoA };
            var forbiddenJson = GetIndexStatusTool.GetIndexStatus(forbidden);

            // (b) tok-b names a repository that does NOT exist in the catalog at all.
            using var nonexistent =
                new DatabaseProvider(dbPath, new PolicyReadAuthorizer(policy, () => "tok-b", resolver))
                { RequestedRepository = () => "https://github.com/org/does-not-exist" };
            var nonexistentJson = GetIndexStatusTool.GetIndexStatus(nonexistent);

            // Criterion 1: the two responses are byte-identical (modulo the always-varying queried_at), so an
            // unauthorized caller CANNOT tell a forbidden existing repository from a nonexistent one — no
            // existence, name, count, or timing-sensitive signal leaks.
            Assert.AreEqual(StripQueriedAt(forbiddenJson), StripQueriedAt(nonexistentJson),
                "a forbidden existing repo must be byte-indistinguishable from a nonexistent one");
            var meta = JsonDocument.Parse(forbiddenJson).RootElement.GetProperty("meta");
            Assert.IsFalse(meta.TryGetProperty("error", out _), "neither carries an authz/existence oracle");
            Assert.IsFalse(forbiddenJson.Contains(RepoA), "the forbidden repository name does not leak");
        }
        finally
        {
            SqliteTestDatabase.Delete(dbPath, db);
        }
    }

    // ==== criterion 1/2: raw host-source reads are suppressed under enforcement ======================

    [TestMethod]
    public void FindSymbol_IncludeSource_ServedLocally_ButSuppressedUnderEnforcement()
    {
        var sourceFile = Path.Combine(Path.GetTempPath(), $"sextant_src_{Guid.NewGuid():N}.cs");
        File.WriteAllLines(sourceFile, ["public class Secret", "{", "    // sensitive host file", "}"]);
        var (dbPath, db, repoId) = SeedRepoWithSourceFile(RepoA, sourceFile);
        try
        {
            var policy = TwoTenant();
            Func<long, string?> resolver = id => id == repoId ? RepoA : null;

            // Zero-policy local path (AllowAll, IsEnforcing=false): the raw declaration IS read off disk,
            // byte-identical to pre-Phase-17 behavior.
            using var local = new DatabaseProvider(dbPath, AllowAllReadAuthorizer.Instance);
            var localJson = FindSymbolTool.FindSymbol(local, "global::Secret", include_source: true).GetAwaiter().GetResult();
            StringAssert.Contains(localJson, "source_context", "the local path serves source declarations");
            StringAssert.Contains(localJson, "public class Secret", "the on-disk declaration is returned locally");

            // Enforced multi-tenant path (authorized principal): the raw, hash-UNverified host-file read is
            // suppressed so a reconstructed absolute path can never expose service-host file contents. The
            // authorized caller still gets the symbol metadata — just locations, no snippet (criteria 1 & 2).
            using var enforced = new DatabaseProvider(dbPath, new PolicyReadAuthorizer(policy, () => "tok-a", resolver));
            var enforcedJson = FindSymbolTool.FindSymbol(enforced, "global::Secret", include_source: true).GetAwaiter().GetResult();
            Assert.IsFalse(enforcedJson.Contains("source_context"), "source snippet is suppressed under enforcement");
            Assert.IsFalse(enforcedJson.Contains("sensitive host file"), "no host-file contents leak under enforcement");
            StringAssert.Contains(enforcedJson, "Secret", "the authorized caller still receives the symbol metadata");
        }
        finally
        {
            SqliteTestDatabase.Delete(dbPath, db);
            File.Delete(sourceFile);
        }
    }

    private static (string dbPath, IndexDatabase db, long repoId) SeedRepoWithSourceFile(string repoUrl, string absoluteFilePath)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"sextant_polauthz_{Guid.NewGuid():N}.db");
        var db = new IndexDatabase(dbPath);
        db.RunMigrations();
        var conn = db.GetConnection();
        var store = new SnapshotStore(conn);

        var repoId = store.EnsureRepository(repoUrl, now: 1);
        var commitId = store.EnsureCommit(repoId, "c1", "t1", now: 1);
        var logical = store.EnsureLogicalProject(repoId, "logical_A", "src/A/A.csproj", "net10.0", now: 1);
        var snapId = store.BeginPending(Identity(repoUrl), repoId, commitId, runId: null, now: 1).id;
        var projId = new ProjectStore(conn).UpsertSnapshotProject(ProjOf(repoUrl), snapId, logical, 1);
        store.MapProject(snapId, projId);
        new SymbolStore(conn).Insert(new SymbolInfo
        {
            ProjectId = projId,
            SymbolKey = "K:Secret", FullyQualifiedName = "global::Secret",
            DisplayName = "Secret", Kind = SymbolKind.Class, Accessibility = Accessibility.Public,
            FilePath = absoluteFilePath, LineStart = 1, LineEnd = 4, LastIndexedAt = 1
        });
        store.MarkComplete(snapId, publishedAt: 1);
        var branch = store.EnsureBranch(repoId, "main", isDefault: true, now: 1);
        store.SetBranchPointer(branch, snapId, now: 1);
        return (dbPath, db, repoId);
    }

    private static string StripQueriedAt(string json) =>
        System.Text.RegularExpressions.Regex.Replace(json, "\"queried_at\":\\s*\\d+", "\"queried_at\":0");

    private static (string dbPath, IndexDatabase db, long repoAId, long repoBId) SeedTwoSelectedRepos()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"sextant_polauthz_{Guid.NewGuid():N}.db");
        var db = new IndexDatabase(dbPath);
        db.RunMigrations();
        var conn = db.GetConnection();
        var store = new SnapshotStore(conn);
        var repoAId = SeedOneSelectedRepo(conn, store, RepoA, "logical_A", "src/A/A.csproj", "K:A.T", "global::A.T");
        var repoBId = SeedOneSelectedRepo(conn, store, RepoB, "logical_B", "src/B/B.csproj", "K:B.T", "global::B.T");
        return (dbPath, db, repoAId, repoBId);
    }

    private static long SeedOneSelectedRepo(
        Microsoft.Data.Sqlite.SqliteConnection conn, SnapshotStore store,
        string url, string canonical, string projPath, string symbolKey, string fqn)
    {
        var repoId = store.EnsureRepository(url, now: 1);
        var commitId = store.EnsureCommit(repoId, "c1", "t1", now: 1);
        var logical = store.EnsureLogicalProject(repoId, canonical, projPath, "net10.0", now: 1);

        var identity = new SnapshotIdentity
        {
            RepositoryRemoteUrl = url, CommitSha = "c1", TreeSha = "t1",
            SchemaVersion = IndexDatabase.LatestSchemaVersion,
            AnalyzerVersion = IndexConfigurationHash.AnalyzerVersion,
            ConfigHash = "cfg", ToolchainFingerprint = ToolchainFingerprint.Current
        };
        var snapId = store.BeginPending(identity, repoId, commitId, runId: null, now: 1).id;
        var proj = new ProjectIdentity
        {
            CanonicalId = canonical, GitRemoteUrl = url, RepoRelativePath = projPath, TargetFramework = "net10.0"
        };
        var projId = new ProjectStore(conn).UpsertSnapshotProject(proj, snapId, logical, 1);
        store.MapProject(snapId, projId);
        new SymbolStore(conn).Insert(new SymbolInfo
        {
            ProjectId = projId,
            SymbolKey = symbolKey, FullyQualifiedName = fqn,
            DisplayName = "T", Kind = SymbolKind.Class, Accessibility = Accessibility.Public,
            FilePath = projPath, LineStart = 1, LineEnd = 2, LastIndexedAt = 1
        });
        store.MarkComplete(snapId, publishedAt: 1);
        var branch = store.EnsureBranch(repoId, "main", isDefault: true, now: 1);
        store.SetBranchPointer(branch, snapId, now: 1);
        return repoId;
    }

    private static (string dbPath, IndexDatabase db, long repoId) SeedSelectedRepo(string repoUrl)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"sextant_polauthz_{Guid.NewGuid():N}.db");
        var db = new IndexDatabase(dbPath);
        db.RunMigrations();
        var conn = db.GetConnection();
        var store = new SnapshotStore(conn);

        var repoId = store.EnsureRepository(repoUrl, now: 1);
        var commitId = store.EnsureCommit(repoId, "c1", "t1", now: 1);
        var logical = store.EnsureLogicalProject(repoId, "logical_A", "src/A/A.csproj", "net10.0", now: 1);

        var snapId = store.BeginPending(Identity(repoUrl), repoId, commitId, runId: null, now: 1).id;
        var projId = new ProjectStore(conn).UpsertSnapshotProject(ProjOf(repoUrl), snapId, logical, 1);
        store.MapProject(snapId, projId);
        new SymbolStore(conn).Insert(new SymbolInfo
        {
            ProjectId = projId,
            SymbolKey = "K:A.T", FullyQualifiedName = "global::A.T",
            DisplayName = "T", Kind = SymbolKind.Class, Accessibility = Accessibility.Public,
            FilePath = "src/A/T.cs", LineStart = 1, LineEnd = 2, LastIndexedAt = 1
        });
        store.MarkComplete(snapId, publishedAt: 1);

        var branch = store.EnsureBranch(repoId, "main", isDefault: true, now: 1);
        store.SetBranchPointer(branch, snapId, now: 1);
        return (dbPath, db, repoId);
    }

    private static SnapshotIdentity Identity(string repoUrl) => new()
    {
        RepositoryRemoteUrl = repoUrl,
        CommitSha = "c1",
        TreeSha = "t1",
        SchemaVersion = IndexDatabase.LatestSchemaVersion,
        AnalyzerVersion = IndexConfigurationHash.AnalyzerVersion,
        ConfigHash = "cfg",
        ToolchainFingerprint = ToolchainFingerprint.Current
    };

    private static ProjectIdentity ProjOf(string repoUrl) => new()
    {
        CanonicalId = "logical_A",
        GitRemoteUrl = repoUrl,
        RepoRelativePath = "src/A/A.csproj",
        TargetFramework = "net10.0"
    };
}
