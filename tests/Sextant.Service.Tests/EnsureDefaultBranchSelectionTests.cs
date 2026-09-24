using Sextant.Core;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #104: a repository indexed via the service ensure path must leave a SELECTABLE default-branch
/// snapshot so direct HTTP MCP semantic queries (which resolve
/// <see cref="SnapshotStore.GetSelectedSnapshotIdForRepository"/>, requiring <c>is_default = 1</c>) return
/// results instead of failing closed. The coordinator NAMES the branch it advances (a non-null
/// <c>BranchName</c>) in the normal indexing case, which historically derived <c>isDefault = BranchName is
/// null = false</c> and never set a default. These tests drive <see cref="SnapshotService"/> end-to-end
/// over the no-worker reuse path and prove: the safety net makes a named-branch ensure selectable, the
/// explicit <c>default_branch</c> flag is honored, the single-default invariant holds across branches, an
/// explicit <c>false</c> never steals an existing default, and the local <c>BranchName is null</c> path is
/// unchanged.
/// </summary>
[TestClass]
public class EnsureDefaultBranchSelectionTests
{
    private const string Repo = "https://github.com/org/app";
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SnapshotService _service = null!;

    [TestCleanup]
    public void TestCleanup()
    {
        _service?.Dispose();
        SqliteTestDatabase.Delete(_dbPath, _db);
    }

    // (a) + (b): a NAMED-branch reuse ensure with NO default_branch flag still leaves the repo selectable
    // via the first/sole-consumer safety net — the exact #104 coordinator case that produced empty queries.
    [TestMethod]
    public async Task NamedBranchEnsure_NoFlag_SafetyNetMakesRepositorySelectable()
    {
        var snap = StartWithPublishedCommits("commit-A");

        Assert.IsNull(Selected(),
            "precondition: a published snapshot with no branch is unselectable (the #104 symptom)");

        await _service.EnsureSnapshotAsync(Ensure("commit-A", branch: "main", seq: 10));

        Assert.AreEqual(snap, Selected(),
            "a coordinator that NAMES the branch it advances (default_branch unset) still leaves a selectable snapshot (#104)");
        Assert.AreEqual("main", DefaultBranchName(), "the sole consumer branch was promoted to default");
    }

    // (c): explicit default_branch=true across two branches keeps EXACTLY ONE default (single-default
    // invariant), and the selector follows the most-recently-flagged branch.
    [TestMethod]
    public async Task MultiBranchExplicitDefault_PreservesSingleDefaultInvariant()
    {
        var (snapA, snapB) = StartWithPublishedCommits("commit-A", "commit-B");

        await _service.EnsureSnapshotAsync(Ensure("commit-A", branch: "main", seq: 10, isDefault: true));
        Assert.AreEqual(snapA, Selected(), "main becomes the selectable default");

        await _service.EnsureSnapshotAsync(Ensure("commit-B", branch: "feature", seq: 10, isDefault: true));

        Assert.AreEqual(1, DefaultCount(), "exactly one default branch remains after a second explicit default");
        Assert.AreEqual("feature", DefaultBranchName(), "the explicitly-flagged branch owns the default");
        Assert.AreEqual(snapB, Selected(), "the selector follows the current default branch");
    }

    // (d): an explicit default_branch=false on a SECONDARY branch must NOT steal the default from the
    // existing default branch (issue #62 preserved through the new decoupled resolution).
    [TestMethod]
    public async Task ExplicitFalseOnSecondaryBranch_DoesNotStealTheDefault()
    {
        var (snapA, snapB) = StartWithPublishedCommits("commit-A", "commit-B");

        await _service.EnsureSnapshotAsync(Ensure("commit-A", branch: "main", seq: 10, isDefault: true));
        await _service.EnsureSnapshotAsync(Ensure("commit-B", branch: "feature", seq: 10, isDefault: false));

        Assert.AreEqual("main", DefaultBranchName(), "an explicit false never demotes the existing default (#62)");
        Assert.AreEqual(1, DefaultCount(), "the single-default invariant is preserved");
        Assert.AreEqual(snapA, Selected(), "the selector still resolves the untouched default branch");
    }

    // (e): the local/legacy BranchName is null path is byte-identical — ResolveIsDefaultBranch falls back to
    // (BranchName is null) ⇒ default, advancing the default "main" exactly as before #104.
    [TestMethod]
    public async Task NullBranchName_LocalPath_AdvancesDefaultBranch_ByteIdentical()
    {
        var snap = StartWithPublishedCommits("commit-A");

        await _service.EnsureSnapshotAsync(Ensure("commit-A", branch: null, seq: 10));

        Assert.AreEqual(snap, Selected(), "the null-branch path advances the default branch, unchanged from pre-#104");
        Assert.AreEqual("main", DefaultBranchName(), "a null branch name resolves to the default 'main' branch");
    }

    // ---- helpers --------------------------------------------------------------------------------

    private long StartWithPublishedCommits(string commit)
    {
        var snaps = StartWithPublishedCommits(new[] { commit });
        return snaps[0];
    }

    private (long, long) StartWithPublishedCommits(string commitA, string commitB)
    {
        var snaps = StartWithPublishedCommits(new[] { commitA, commitB });
        return (snaps[0], snaps[1]);
    }

    private long[] StartWithPublishedCommits(string[] commits)
    {
        _dbPath = ServiceTestFixtures.NewDbPath();
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        var snaps = commits
            .Select(c => ServiceTestFixtures.PublishComplete(_db, new EnsureSnapshotRequest { RepositoryRemoteUrl = Repo, CommitSha = c }))
            .ToArray();
        // A worker that MUST NOT run — every ensure hits the already-published reuse path, so the branch
        // decision is made entirely by the service ensure logic under test.
        var worker = new FakeSnapshotWorker(_db, FakeSnapshotWorker.Throws("worker must not run on the reuse path"));
        _service = SnapshotService.Start(ServiceTestFixtures.NewOptions(_dbPath), worker, _db);
        return snaps;
    }

    private static EnsureSnapshotRequest Ensure(string commit, string? branch, long? seq, bool? isDefault = null) => new()
    {
        RepositoryRemoteUrl = Repo,
        CommitSha = commit,
        BranchName = branch,
        BranchHeadSequence = seq,
        IsDefaultBranch = isDefault
    };

    private long? Selected() => new SnapshotStore(_db.GetConnection()).GetSelectedSnapshotIdForRepository(Repo);

    private int DefaultCount()
    {
        using var cmd = _db.GetConnection().CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM branches b JOIN repositories r ON r.id = b.repository_id
            WHERE r.remote_url = @url AND b.is_default = 1;
            """;
        cmd.Parameters.AddWithValue("@url", Repo);
        return (int)(long)cmd.ExecuteScalar()!;
    }

    private string? DefaultBranchName()
    {
        using var cmd = _db.GetConnection().CreateCommand();
        cmd.CommandText = """
            SELECT b.name FROM branches b JOIN repositories r ON r.id = b.repository_id
            WHERE r.remote_url = @url AND b.is_default = 1 ORDER BY b.id LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@url", Repo);
        return cmd.ExecuteScalar() as string;
    }
}

/// <summary>
/// Issue #104 contract-level checks for the new <c>default_branch</c> field on
/// <see cref="EnsureSnapshotRequest"/>: it decouples default-ness from the "no branch name" heuristic,
/// binds over the snake_case wire as <c>default_branch</c>, preserves the legacy fallback when omitted, and
/// is NOT folded into the content-addressed snapshot identity.
/// </summary>
[TestClass]
public class EnsureDefaultBranchContractTests
{
    [TestMethod]
    public void ResolveIsDefaultBranch_ExplicitTrue_Honored()
        => Assert.IsTrue((ServiceTestFixtures.Request(branch: "main") with { IsDefaultBranch = true }).ResolveIsDefaultBranch(),
            "an explicit default_branch=true marks the NAMED branch default (the coordinator's authoritative signal)");

    [TestMethod]
    public void ResolveIsDefaultBranch_ExplicitFalse_Honored()
        => Assert.IsFalse((ServiceTestFixtures.Request(branch: null) with { IsDefaultBranch = false }).ResolveIsDefaultBranch(),
            "an explicit default_branch=false wins even for a null branch name (flag is authoritative)");

    [TestMethod]
    public void ResolveIsDefaultBranch_Omitted_FallsBackToNullBranchHeuristic()
    {
        Assert.IsTrue(ServiceTestFixtures.Request(branch: null).ResolveIsDefaultBranch(),
            "omitted flag + null branch ⇒ default (byte-identical to the pre-#104 local path)");
        Assert.IsFalse(ServiceTestFixtures.Request(branch: "main").ResolveIsDefaultBranch(),
            "omitted flag + named branch ⇒ NOT default via the legacy heuristic (unchanged derivation)");
    }

    [TestMethod]
    public void DefaultBranch_TrueRoundTripsOverSnakeCaseWire()
    {
        var request = ServiceTestFixtures.Request(branch: "main") with { IsDefaultBranch = true };

        var json = System.Text.Json.JsonSerializer.Serialize(request, ServiceJson.Options);
        StringAssert.Contains(json, "\"default_branch\":true",
            "the wire field is snake_case default_branch (matching the contribute path's query parameter)");

        var round = System.Text.Json.JsonSerializer.Deserialize<EnsureSnapshotRequest>(json, ServiceJson.Options)!;
        Assert.AreEqual(true, round.IsDefaultBranch);
    }

    [TestMethod]
    public void DefaultBranch_ExplicitFalseBindsFromWire()
    {
        const string json = """
            {"repository_remote_url":"https://github.com/org/app","commit_sha":"commit-aaaa","branch_name":"main","default_branch":false}
            """;

        var request = System.Text.Json.JsonSerializer.Deserialize<EnsureSnapshotRequest>(json, ServiceJson.Options)!;

        Assert.AreEqual(false, request.IsDefaultBranch);
        Assert.IsFalse(request.ResolveIsDefaultBranch());
    }

    [TestMethod]
    public void DefaultBranch_OmittedFromWire_BindsAsNull()
    {
        const string json = """
            {"repository_remote_url":"https://github.com/org/app","commit_sha":"commit-aaaa","branch_name":"main"}
            """;

        var request = System.Text.Json.JsonSerializer.Deserialize<EnsureSnapshotRequest>(json, ServiceJson.Options)!;

        Assert.IsNull(request.IsDefaultBranch,
            "a coordinator that omits the field binds to null → the legacy derivation, preserving old behavior");
    }

    [TestMethod]
    public void DefaultBranch_DoesNotChangeSnapshotIdentity()
    {
        var baseRequest = ServiceTestFixtures.Request(branch: "main");
        var asDefault = baseRequest with { IsDefaultBranch = true };
        var notDefault = baseRequest with { IsDefaultBranch = false };

        Assert.AreEqual(baseRequest.ToIdentity().Hash, asDefault.ToIdentity().Hash,
            "default designation is a branch attribute, not part of the content-addressed snapshot identity");
        Assert.AreEqual(asDefault.ToIdentity().Hash, notDefault.ToIdentity().Hash,
            "the same committed state is the SAME immutable snapshot regardless of default_branch");
    }

    [TestMethod]
    public void Worker_CreateSnapshotContext_UsesResolvedDefault()
    {
        var flagged = ServiceTestFixtures.Request(branch: "main") with { IsDefaultBranch = true };
        var context = LocalIndexerSnapshotWorker.CreateSnapshotContext(flagged, capability: null);
        Assert.IsTrue(context.IsDefaultBranch,
            "an explicit default_branch=true carries into the orchestrator's SnapshotContext even for a named branch (#104)");

        var localish = ServiceTestFixtures.Request(branch: null);
        Assert.IsTrue(LocalIndexerSnapshotWorker.CreateSnapshotContext(localish, capability: null).IsDefaultBranch,
            "a null branch with no flag still resolves default — byte-identical to the pre-#104 worker context");
    }
}
