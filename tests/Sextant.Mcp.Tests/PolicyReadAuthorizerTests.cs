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
            Assert.AreEqual("authorization_denied", meta.GetProperty("error").GetProperty("code").GetString(),
                "an unauthorized status read fails closed as a structured error, never an empty success");
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
