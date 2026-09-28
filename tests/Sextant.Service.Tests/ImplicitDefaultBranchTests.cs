using System.Net;
using System.Text.Json;
using Sextant.Core;
using Sextant.Store;
using static Sextant.Service.Tests.CallerAssertionHttpTests;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #199: a caller that may not pick a repository's default branch (a verified <c>act=user</c> caller, marked
/// <see cref="EnsureSnapshotRequest.RestrictsImplicitDefault"/>) never makes its branch the default just by being the
/// repository's first: the #104 first-branch safety net applies to it only for the branch the REMOTE names as its
/// default, which the service looks up itself (<see cref="IRemoteDefaultBranchResolver"/>), and it fails closed when
/// that lookup is unavailable. Covered on the worker path (the orchestrator's branch advance) and on the reuse path
/// (a pre-built identity), which must converge; application and assertion-less callers are unchanged.
/// </summary>
[TestClass]
public class ImplicitDefaultBranchTests
{
    private const string Repo = "https://github.com/acme/widgets";
    private readonly List<string> _cleanup = [];

    [TestCleanup]
    public void TestCleanup()
    {
        foreach (var path in _cleanup)
            try { Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }

    // ==== the four #199 cases, on the worker path and the reuse path ===============================

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UserFirstEnsure_OfANonDefaultBranch_DoesNotTakeTheDefault(bool prebuild)
    {
        var remote = new FakeRemoteDefaults("main");

        var (state, _) = await Run(prebuild, remote, User("feature-x", "commit-A"));

        CollectionAssert.AreEqual(new[] { "branch feature-x -> commit-A default=False seq=" }, state, string.Join(" | ", state));
        Assert.AreEqual(1, remote.Calls, "the remote default is looked up once, while the repository has no default");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UserFirstEnsure_OfTheRemotesDefaultBranch_TakesTheDefault(bool prebuild)
    {
        var (state, _) = await Run(prebuild, new FakeRemoteDefaults("main"), User("main", "commit-A"));

        CollectionAssert.AreEqual(new[] { "branch main -> commit-A default=True seq=" }, state, string.Join(" | ", state));
    }

    [TestMethod]
    [DataRow(false, "")]
    [DataRow(false, null)]
    [DataRow(true, "")]
    [DataRow(true, null)]
    public async Task ApplicationFirstEnsure_OfAnyBranch_BehavesAsToday(bool prebuild, string? expected)
    {
        var remote = new FakeRemoteDefaults("main");

        var (state, _) = await Run(prebuild, remote, App("feature-x", "commit-A", expected));

        CollectionAssert.AreEqual(new[] { "branch feature-x -> commit-A default=True seq=" }, state, string.Join(" | ", state));
        Assert.AreEqual(0, remote.Calls, "an unrestricted ensure never consults the remote");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ApplicationEnsure_WithDefaultBranchTrue_StillMovesTheDefault(bool prebuild)
    {
        var (state, _) = await Run(prebuild, new FakeRemoteDefaults("main"),
            User("main", "commit-A"),
            App("release", "commit-B", "", isDefault: true));

        CollectionAssert.AreEqual(
            new[] { "branch release -> commit-B default=True seq=", "branch main -> commit-A default=False seq=" },
            state, string.Join(" | ", state));
    }

    // ==== fail closed, and the lookup's scope ======================================================

    [TestMethod]
    [DataRow(false, "unknown")]
    [DataRow(true, "unknown")]
    [DataRow(false, "throws")]
    [DataRow(true, "throws")]
    [DataRow(false, "unwired")]
    [DataRow(true, "unwired")]
    public async Task UserEnsure_WhenTheRemoteDefaultCannotBeVerified_DoesNotTakeTheDefault(bool prebuild, string mode)
    {
        var remote = mode switch
        {
            "unknown" => new FakeRemoteDefaults((string?)null),
            "throws" => new FakeRemoteDefaults(_ => throw new InvalidOperationException("remote unreachable")),
            _ => null
        };

        var (state, _) = await Run(prebuild, remote, User("main", "commit-A"));

        CollectionAssert.AreEqual(new[] { "branch main -> commit-A default=False seq=" }, state, string.Join(" | ", state));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UserEnsure_OnceTheRepositoryHasADefault_DoesNotConsultTheRemote(bool prebuild)
    {
        var remote = new FakeRemoteDefaults("feature-x");

        var (state, _) = await Run(prebuild, remote,
            App("main", "commit-A", ""),
            User("feature-x", "commit-B"),
            User("main", "commit-B", "commit-A"));

        CollectionAssert.AreEqual(
            new[] { "branch main -> commit-B default=True seq=", "branch feature-x -> commit-B default=False seq=" },
            state, string.Join(" | ", state));
        Assert.AreEqual(0, remote.Calls, "the safety net cannot apply once a default exists, so nothing is looked up");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UserEnsure_ADirectCallersVerifiedBranch_IsDiscarded(bool prebuild)
    {
        var forged = User("feature-x", "commit-A") with { VerifiedRemoteDefaultBranch = "feature-x" };

        var (state, _) = await Run(prebuild, new FakeRemoteDefaults("main"), forged);

        CollectionAssert.AreEqual(new[] { "branch feature-x -> commit-A default=False seq=" }, state, string.Join(" | ", state));
    }

    [TestMethod]
    public async Task UserEnsure_WithBranchUpdateNone_DoesNotConsultTheRemote()
    {
        var remote = new FakeRemoteDefaults("main");
        var none = User("main", "commit-A") with { ExpectedHeadCommit = null, BranchUpdate = BranchUpdateMode.None };

        var (state, _) = await Run(prebuild: false, remote, none);

        Assert.IsEmpty(state, "no branch row, so the answer could never be used");
        Assert.AreEqual(0, remote.Calls);
    }

    // ==== the host marks user ensures, and only them ===============================================

    [TestMethod]
    public async Task Http_UserEnsures_OfAFreshRepository_TakeTheDefaultOnlyForTheRemotesDefault()
    {
        var remote = new FakeRemoteDefaults("main");
        await using var host = await Harness.StartAsync(seed: SeedFresh, remoteDefaults: remote);
        await GrantAsync(host, Fresh);

        await EnsureOkAsync(host, host.UserAssertion(), """{"branch_name":"feature-x","expected_head_commit":""}""");
        Assert.IsFalse(host.Service.ResolveBranchHead(Fresh, "feature-x")!.IsDefault);
        Assert.IsNull(host.Service.ResolveBranchHead(Fresh, null), "the repository still has no default");

        await EnsureOkAsync(host, host.UserAssertion(), """{"branch_name":"main","expected_head_commit":""}""");
        Assert.AreEqual("main", host.Service.ResolveBranchHead(Fresh, null)!.Branch);

        await EnsureOkAsync(host, host.UserAssertion(), """{"branch_name":"other","expected_head_commit":""}""");
        Assert.AreEqual(1, remote.Calls, "the answer is remembered, and nothing is looked up once a default exists");
    }

    [TestMethod]
    public async Task Http_ApplicationEnsure_OfAFreshRepository_IsUnchanged()
    {
        var remote = new FakeRemoteDefaults("main");
        await using var host = await Harness.StartAsync(seed: SeedFresh, remoteDefaults: remote);
        var application = host.Sign(CallerAssertionSigner.ApplicationClaims(DateTimeOffset.UtcNow));

        await EnsureOkAsync(host, application, """{"branch_name":"feature-x","expected_head_commit":""}""");

        Assert.AreEqual("feature-x", host.Service.ResolveBranchHead(Fresh, null)!.Branch, "the #104 safety net as today");
        Assert.AreEqual(0, remote.Calls);
    }

    // ==== contract =================================================================================

    [TestMethod]
    public void TheRestriction_IsNeverBoundFromTheWire_AndIsNotPartOfTheIdentity()
    {
        var bound = JsonSerializer.Deserialize<EnsureSnapshotRequest>(
            """{"repository_remote_url":"https://github.com/acme/widgets","commit_sha":"c1","restricts_implicit_default":true,"verified_remote_default_branch":"x"}""",
            ServiceJson.Options)!;
        var restricted = bound with { RestrictsImplicitDefault = true, VerifiedRemoteDefaultBranch = "main" };

        Assert.IsFalse(bound.RestrictsImplicitDefault);
        Assert.IsNull(bound.VerifiedRemoteDefaultBranch);
        Assert.AreEqual(bound.ToIdentity().Hash, restricted.ToIdentity().Hash);
        StringAssert.DoesNotMatch(JsonSerializer.Serialize(restricted, ServiceJson.Options), new System.Text.RegularExpressions.Regex("implicit|verified"));
    }

    [TestMethod]
    [DataRow(false, null, "main", null)]
    [DataRow(true, null, "main", false)]
    [DataRow(true, "main", "main", true)]
    [DataRow(true, "main", "feature", false)]
    [DataRow(true, "main", null, true)]
    public void Worker_MapsTheRestriction_ToTheSnapshotContext(bool restricted, string? verified, string? branch, bool? allowed)
    {
        var request = ServiceTestFixtures.Request(branch: branch) with
        {
            RestrictsImplicitDefault = restricted,
            VerifiedRemoteDefaultBranch = verified
        };

        Assert.AreEqual(allowed, LocalIndexerSnapshotWorker.CreateSnapshotContext(request, capability: null).AllowImplicitDefault);
    }

    [TestMethod]
    public void Store_WithoutTheImplicitAllowance_OwnsTheDefaultOnlyExplicitlyOrWhenItAlreadyIsIt()
    {
        var dbPath = ServiceTestFixtures.NewDbPath();
        var db = new IndexDatabase(dbPath);
        try
        {
            db.RunMigrations();
            var snapshots = new SnapshotStore(db.GetConnection());
            var repoId = snapshots.EnsureRepository(Repo, 1);

            Assert.IsTrue(snapshots.ShouldOwnDefault(repoId, "feature", false), "the #104 safety net");
            Assert.IsFalse(snapshots.ShouldOwnDefault(repoId, "feature", false, allowImplicitDefault: false));
            Assert.IsTrue(snapshots.ShouldOwnDefault(repoId, "feature", true, allowImplicitDefault: false), "explicit");

            snapshots.EnsureBranch(repoId, "main", true, 1);
            Assert.IsTrue(snapshots.ShouldOwnDefault(repoId, "main", false, allowImplicitDefault: false), "already the default");
            Assert.IsFalse(snapshots.ShouldOwnDefault(repoId, "feature", false, allowImplicitDefault: false));
        }
        finally
        {
            SqliteTestDatabase.Delete(dbPath, db);
        }
    }

    [TestMethod]
    [DataRow("ref: refs/heads/main\tHEAD\n0123abcd\tHEAD\n", "main")]
    [DataRow("ref: refs/heads/release/1.x\tHEAD\r\n0123abcd\tHEAD\r\n", "release/1.x")]
    [DataRow("0123abcd\tHEAD\n", null)]
    [DataRow("", null)]
    [DataRow("ref: refs/tags/v1\tHEAD\n", null)]
    [DataRow("ref: refs/heads/\tHEAD\n", null)]
    [DataRow("ref: refs/heads/a b\tHEAD\n", null)]
    [DataRow("ref: refs/heads/main\tHEAD\nref: refs/heads/dev\tHEAD\n", null)]
    [DataRow("ref: refs/heads/main\trefs/remotes/origin/HEAD\n", null)]
    public void ParseSymref_ReadsOnlyAnUnambiguousBranchHead(string output, string? expected) =>
        Assert.AreEqual(expected, RemoteDefaultBranch.ParseSymref(output));

    // ==== helpers ==================================================================================

    private const string Fresh = "https://github.com/acme/fresh";

    private static EnsureSnapshotRequest User(string branch, string commit, string expected = "") => new()
    {
        RepositoryRemoteUrl = Repo,
        CommitSha = commit,
        BranchName = branch,
        ExpectedHeadCommit = expected,
        RestrictsImplicitDefault = true
    };

    private static EnsureSnapshotRequest App(string branch, string commit, string? expected, bool? isDefault = null) => new()
    {
        RepositoryRemoteUrl = Repo,
        CommitSha = commit,
        BranchName = branch,
        ExpectedHeadCommit = expected,
        IsDefaultBranch = isDefault
    };

    // Runs the ensures through a real service whose worker runs the orchestrator over an in-memory solution. With
    // prebuild, every identity is first published by a `branch_update: none` ensure (no branch row), so the ensures
    // under test take the reuse path; without it, each new identity takes the worker path.
    private async Task<(List<string> Branches, int WorkerCalls)> Run(
        bool prebuild, IRemoteDefaultBranchResolver? remote, params EnsureSnapshotRequest[] ensures)
    {
        var root = Path.Combine(Path.GetTempPath(), $"sextant_implicit_default_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _cleanup.Add(root);
        var dbPath = Path.Combine(root, "catalog.db");
        var db = new IndexDatabase(dbPath);
        db.RunMigrations();
        var options = ServiceTestFixtures.NewOptions(dbPath, dataRoot: Path.Combine(root, "data")) with
        {
            DefaultConfigHash = IndexProfileDescriptor.Full.ConfigurationHash
        };
        var worker = new FakeSnapshotWorker(db,
            (self, request) => EnsureBranchCasConvergenceTests.IndexWithOrchestrator(self.Database, root, request));
        try
        {
            using var service = SnapshotService.Start(options, worker, db, remoteDefaults: remote);
            if (prebuild)
            {
                foreach (var commit in ensures.Select(e => e.CommitSha).Distinct())
                    await service.EnsureSnapshotAsync(new EnsureSnapshotRequest
                    {
                        RepositoryRemoteUrl = Repo, CommitSha = commit, BranchUpdate = BranchUpdateMode.None
                    });
            }
            var callsBefore = worker.Calls;
            foreach (var ensure in ensures)
            {
                var result = await service.EnsureSnapshotAsync(ensure);
                Assert.AreEqual(SnapshotJobStatus.Complete, result.Status, $"{ensure.CommitSha}: {result.Reason}");
            }
            var calls = worker.Calls - callsBefore;
            Assert.AreEqual(prebuild ? 0 : ensures.Select(e => e.CommitSha).Distinct().Count(), calls,
                prebuild ? "every ensure over a pre-built identity is a reuse" : "each new identity ran the orchestrator");
            return ([.. EnsureBranchCasConvergenceTests.State(db).Where(s => s.StartsWith("branch ", StringComparison.Ordinal))], calls);
        }
        finally
        {
            SqliteTestDatabase.Delete(dbPath, db);
        }
    }

    private static void SeedFresh(IndexDatabase db) =>
        ServiceTestFixtures.PublishComplete(db, ServiceTestFixtures.Request(repo: Fresh, commit: "commit-f1"), symbolCount: 1, recordCommit: true);

    private static async Task GrantAsync(Harness host, string repository)
    {
        using var grant = await host.ControlAsync(HttpMethod.Put, "/control/grants/self", ControlToken, host.UserAssertion(),
            JsonSerializer.Serialize(new { repository }));
        Assert.AreEqual(HttpStatusCode.OK, grant.StatusCode, await grant.Content.ReadAsStringAsync());
    }

    private static async Task EnsureOkAsync(Harness host, string assertion, string fields)
    {
        var body = JsonSerializer.SerializeToNode(
            new EnsureSnapshotRequest { RepositoryRemoteUrl = Fresh, CommitSha = "commit-f1" }, ServiceJson.Options)!.AsObject();
        foreach (var (key, value) in System.Text.Json.Nodes.JsonNode.Parse(fields)!.AsObject())
            body[key] = value?.DeepClone();
        using var response = await host.ControlAsync(HttpMethod.Post, "/control/ensure", ControlToken, assertion, body.ToJsonString());
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private sealed class FakeRemoteDefaults(Func<string, string?> answer) : IRemoteDefaultBranchResolver
    {
        private int _calls;

        public FakeRemoteDefaults(string? branch) : this(_ => branch) { }

        public int Calls => Volatile.Read(ref _calls);

        public string? ResolveDefaultBranch(string repositoryRemoteUrl)
        {
            Interlocked.Increment(ref _calls);
            return answer(repositoryRemoteUrl);
        }
    }
}
