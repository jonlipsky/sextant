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

        Assert.AreEqual(id1, id2, ".git is identity-neutral");
        Assert.AreEqual(id1, id3, "case is identity-neutral");
        Assert.AreEqual(id1, id4, "a trailing slash is identity-neutral");
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
    public void GetRepositoryId_LegacyDuplicateRows_ExactSpellingStaysReachable_EquivalentResolvesDeterministically()
    {
        // A database indexed BEFORE this fix can already hold several raw-spelling rows that normalize
        // equal (UNIQUE is on the raw remote_url, so the #92 spellings coexisted). Two invariants must hold
        // on such a legacy catalog:
        //   (1) each EXACT stored spelling keeps resolving to ITS OWN row, so snapshots/branches/deps and
        //       contribution-validation binds attached to a higher-id duplicate are never hidden; and
        //   (2) an EQUIVALENT spelling that is NOT stored verbatim resolves DETERMINISTICALLY to the
        //       lowest-id equivalent row (the write-time dedup chokepoint), so new writes converge.
        var lowId = InsertRawRepository(WithGit, now: 1);   // stored ".../MixAndMatch.git"
        var midId = InsertRawRepository(NoGit, now: 2);     // stored ".../MixAndMatch" (normalizes equal)
        Assert.IsTrue(lowId < midId, "sanity: rows inserted in ascending id order");

        var store = new SnapshotStore(_conn);
        // (1) exact spellings keep reaching their own rows — no legacy data hidden behind another id.
        Assert.AreEqual(lowId, store.GetRepositoryId(WithGit), "the exact stored spelling resolves to its own row");
        Assert.AreEqual(midId, store.GetRepositoryId(NoGit), "the other exact stored spelling resolves to its own row");
        // (2) an equivalent spelling with no verbatim row resolves to the lowest-id equivalent, deterministically.
        Assert.AreEqual(lowId, store.GetRepositoryId(Lowercase), "a lowercase (unstored) spelling resolves to the lowest-id equivalent row");
        Assert.AreEqual(lowId, store.GetRepositoryId(TrailingSlash), "a trailing-slash (unstored) spelling resolves to the lowest-id equivalent row");
    }

    private static SnapshotIdentity Identity(string repo, string commit, string tree) => new()
    {
        RepositoryRemoteUrl = repo,
        CommitSha = commit,
        TreeSha = tree,
        SchemaVersion = IndexDatabase.LatestSchemaVersion,
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

    // Inserts a repository row with the RAW spelling directly, bypassing SnapshotStore's dedup, to
    // reproduce a pre-fix database that holds several equivalent-spelling rows for one logical repo.
    private long InsertRawRepository(string rawUrl, long now)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO repositories (remote_url, created_at) VALUES (@url, @now) RETURNING id;";
        cmd.Parameters.AddWithValue("@url", rawUrl);
        cmd.Parameters.AddWithValue("@now", now);
        return (long)cmd.ExecuteScalar()!;
    }
}
