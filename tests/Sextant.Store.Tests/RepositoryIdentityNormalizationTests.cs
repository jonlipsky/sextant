using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Store.Tests;

/// <summary>
/// Issue #92 — the catalog dedups REPOSITORY identity on the NORMALIZED remote url, so trivially-
/// equivalent spellings of one repository (a trailing <c>.git</c>, a trailing slash, or a different
/// case) collapse to ONE <c>repositories</c> row instead of several. The row keeps the FIRST raw
/// spelling (readback/snapshot identity stay byte-for-byte), while <c>GetRepositoryId</c> matches by
/// the shared <see cref="RemoteUrlIdentity.Normalize"/> key. Before the fix the raw url string was the
/// identity key while the on-disk checkout directory already normalized, so the same repo indexed under
/// two spellings produced multiple rows and defeated cross-repo/submodule dedup.
/// </summary>
[TestClass]
public class RepositoryIdentityNormalizationTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SqliteConnection _conn = null!;

    // The three real spellings from the bug report (standalone ensure with .git, two submodule
    // discoveries without .git differing by case) plus a trailing-slash variant.
    private const string WithGit = "https://github.com/elevenworks/MixAndMatch.git";
    private const string NoGit = "https://github.com/elevenworks/MixAndMatch";
    private const string Lowercase = "https://github.com/elevenworks/mixandmatch";
    private const string TrailingSlash = "https://github.com/elevenworks/MixAndMatch/";
    private const string Canonical = "https://github.com/elevenworks/mixandmatch";

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_repoident_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _conn = _db.GetConnection();
    }

    [TestCleanup]
    public void TestCleanup() => SqliteTestDatabase.Delete(_dbPath, _db);

    [TestMethod]
    public void EnsureRepository_EquivalentSpellings_CollapseToOneRow()
    {
        var store = new SnapshotStore(_conn);

        var id1 = store.EnsureRepository(WithGit, now: 1);
        var id2 = store.EnsureRepository(NoGit, now: 2);
        var id3 = store.EnsureRepository(Lowercase, now: 3);
        var id4 = store.EnsureRepository(TrailingSlash, now: 4);
        var id5 = store.EnsureRepository("HTTPS://GITHUB.COM:443/ElevenWorks/MIXANDMATCH.GIT/", now: 5);

        Assert.AreEqual(id1, id2, ".git is identity-neutral");
        Assert.AreEqual(id1, id3, "case is identity-neutral");
        Assert.AreEqual(id1, id4, "a trailing slash is identity-neutral");
        Assert.AreEqual(id1, id5, "scheme and host case and the default HTTPS port are identity-neutral");
        Assert.AreEqual(1, CountRepositories(), "all equivalent spellings map to ONE repository row");
        Assert.AreEqual(WithGit, StoredRemoteUrl(id1), "the row preserves the FIRST raw spelling; dedup is by normalized key");
    }

    [TestMethod]
    public void EnsureRepository_DifferentRepository_GetsDistinctRow()
    {
        var store = new SnapshotStore(_conn);

        var mixAndMatch = store.EnsureRepository(WithGit, now: 1);
        var otherRepo = store.EnsureRepository("https://github.com/elevenworks/OtherRepo.git", now: 2);
        var otherOwner = store.EnsureRepository("https://github.com/someoneelse/MixAndMatch.git", now: 3);

        Assert.AreNotEqual(mixAndMatch, otherRepo, "a genuinely different repo gets its own row");
        Assert.AreNotEqual(mixAndMatch, otherOwner, "a different owner (same basename) gets its own row");
        Assert.AreEqual(3, CountRepositories(), "three distinct repositories, three rows");
    }

    [TestMethod]
    public void GetRepositoryId_ResolvesAnyEquivalentSpelling()
    {
        var store = new SnapshotStore(_conn);
        var id = store.EnsureRepository(WithGit, now: 1);

        Assert.AreEqual(id, store.GetRepositoryId(NoGit), "lookup by an equivalent spelling finds the canonical row");
        Assert.AreEqual(id, store.GetRepositoryId(Lowercase));
        Assert.AreEqual(id, store.GetRepositoryId(TrailingSlash));
        Assert.IsNull(store.GetRepositoryId("https://github.com/elevenworks/Unknown"), "an unknown repo resolves to null");
    }

    [TestMethod]
    public void ProviderAndConsumerSpellings_ShareOneRow_AndConsumerWins()
    {
        // The real scenario: the repo is ensured standalone as a CONSUMER (is_provider = 0) under one
        // spelling, then discovered as a submodule PROVIDER from two parents' .gitmodules under two other
        // spellings. All three must resolve to ONE row, and a repository indexed in its own right stays a
        // consumer (EnsureRepository clears is_provider on an existing row; the provider ensure never sets
        // it back when the row already exists).
        var store = new SnapshotStore(_conn);

        var consumerId = store.EnsureRepository(WithGit, now: 1);
        var providerFromAppPilot = store.EnsureProviderRepository(NoGit, now: 2);
        var providerFromRecordStack = store.EnsureProviderRepository(Lowercase, now: 3);

        Assert.AreEqual(consumerId, providerFromAppPilot, "submodule provider (no .git) resolves to the same row");
        Assert.AreEqual(consumerId, providerFromRecordStack, "submodule provider (lowercase) resolves to the same row");
        Assert.AreEqual(1, CountRepositories(), "one logical repo, one row");
        Assert.AreEqual(0L, IsProvider(consumerId), "a repo indexed in its own right stays a consumer");
    }

    [TestMethod]
    public void ProviderFirst_ThenConsumer_FlipsToConsumer()
    {
        // Order-independence of the is_provider ON CONFLICT semantics: even when a provider spelling is
        // seen first, ensuring the same repo as a primary consumer flips is_provider back to 0.
        var store = new SnapshotStore(_conn);

        var providerId = store.EnsureProviderRepository(Lowercase, now: 1);
        Assert.AreEqual(1L, IsProvider(providerId), "provider-only discovery is flagged is_provider = 1");

        var consumerId = store.EnsureRepository(WithGit, now: 2);
        Assert.AreEqual(providerId, consumerId, "the later consumer ensure resolves to the same row");
        Assert.AreEqual(0L, IsProvider(consumerId), "ensuring it as a primary repo clears the provider flag");
        Assert.AreEqual(1, CountRepositories());
    }

    [TestMethod]
    public void ContentAddressing_DifferentCommits_StillYieldDistinctSnapshots()
    {
        // Scope guard: normalizing SPELLING must NOT collapse genuinely different commits. The same repo
        // at two commits still produces two commit rows and two distinct snapshots.
        var store = new SnapshotStore(_conn);
        var repoId = store.EnsureRepository(WithGit, now: 1);

        var commitA = store.EnsureCommit(repoId, "commit_aaaaaaaa", "tree_aaaa", now: 1);
        var commitB = store.EnsureCommit(repoId, "commit_bbbbbbbb", "tree_bbbb", now: 2);
        Assert.AreNotEqual(commitA, commitB, "distinct commits are distinct rows under the one repo");

        var (snapA, _, _) = store.BeginPending(Identity(Canonical, "commit_aaaaaaaa", "tree_aaaa"), repoId, commitA, runId: null, now: 1);
        var (snapB, _, _) = store.BeginPending(Identity(Canonical, "commit_bbbbbbbb", "tree_bbbb"), repoId, commitB, runId: null, now: 2);
        Assert.AreNotEqual(snapA, snapB, "different commits yield distinct snapshots (content-addressing preserved)");
    }

    [TestMethod]
    public void Upgrade_MergesLegacyRowsWithoutLosingSnapshots_AndKeepsTheNewestMainPointer()
    {
        const string unrelated = "https://github.com/acme/other";
        var store = new SnapshotStore(_conn);
        var root = store.EnsureRepository(NoGit, 1);
        var duplicate = store.EnsureRepository(unrelated, 2);
        var oldCommit = store.EnsureCommit(root, "head", null, 1);
        var newCommit = store.EnsureCommit(duplicate, "head", "tree", 2);
        var (oldSnapshot, _, _) = store.BeginPending(Identity(NoGit, "head", "tree"), root, oldCommit, null, 1);
        var newIdentity = Identity(WithGit, "head", "tree");
        var (newSnapshot, _, _) = store.BeginPending(newIdentity, duplicate, newCommit, null, 2);
        store.MarkComplete(oldSnapshot, 1);
        store.MarkComplete(newSnapshot, 2);
        store.SetBranchPointer(store.EnsureBranch(root, "main", true, 1), oldSnapshot, 1);
        store.SetBranchPointer(store.EnsureBranch(duplicate, "main", true, 2), newSnapshot, 2);
        store.SetBranchPointer(store.EnsureBranch(duplicate, "feature", false, 2), newSnapshot, 2);
        store.RecordRetiredHead(duplicate, "idle", "retired", 2, SnapshotStore.RetiredHeadCauseIdle);
        using (var seed = _conn.CreateCommand())
        {
            seed.CommandText = """
                DROP INDEX ix_repositories_key;
                DROP INDEX ix_snapshot_jobs_snapshot_branch;
                ALTER TABLE repositories DROP COLUMN repository_key;
                DELETE FROM schema_version WHERE version = 34;
                UPDATE repositories SET remote_url = @url WHERE id = @id;
                INSERT INTO pull_request_snapshots (repository_id, pr_number, snapshot_id, updated_at)
                VALUES (@id, 1, @snapshot, 2);
                """;
            seed.Parameters.AddWithValue("@url", WithGit);
            seed.Parameters.AddWithValue("@id", duplicate);
            seed.Parameters.AddWithValue("@snapshot", newSnapshot);
            seed.ExecuteNonQuery();
        }

        _db.RunMigrations();

        Assert.AreEqual(1, CountRepositories());
        foreach (var spelling in new[] { NoGit, WithGit, Lowercase, TrailingSlash })
            Assert.AreEqual(root, store.GetRepositoryId(spelling));
        Assert.AreEqual(newSnapshot, store.GetBranch(root, "main")!.SnapshotId);
        Assert.AreEqual(newSnapshot, store.GetBranch(root, "feature")!.SnapshotId);
        Assert.AreEqual(root, store.GetById(oldSnapshot)!.RepositoryId);
        Assert.AreEqual(root, store.GetByIdentityHash(newIdentity.Hash)!.RepositoryId);
        Assert.AreEqual(oldCommit, store.GetById(newSnapshot)!.CommitId);
        Assert.AreEqual("retired", store.GetRetiredHead(root, "idle"));
        using var check = _conn.CreateCommand();
        check.CommandText = "SELECT repository_id FROM pull_request_snapshots WHERE pr_number = 1;";
        Assert.AreEqual(root, check.ExecuteScalar());
        check.CommandText = "PRAGMA foreign_key_check;";
        Assert.IsNull(check.ExecuteScalar());
    }

    private static SnapshotIdentity Identity(string repo, string commit, string tree) => new()
    {
        RepositoryRemoteUrl = repo,
        CommitSha = commit,
        TreeSha = tree,
        SchemaVersion = IndexDatabase.SnapshotSchemaVersion,
        AnalyzerVersion = IndexConfigurationHash.AnalyzerVersion,
        ConfigHash = "cfg",
        ToolchainFingerprint = "tc"
    };

    private int CountRepositories()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM repositories;";
        return (int)(long)cmd.ExecuteScalar()!;
    }

    private string StoredRemoteUrl(long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT remote_url FROM repositories WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        return (string)cmd.ExecuteScalar()!;
    }

    private long IsProvider(long id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT is_provider FROM repositories WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        return (long)cmd.ExecuteScalar()!;
    }

}
