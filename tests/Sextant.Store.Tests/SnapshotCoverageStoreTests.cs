using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Store.Tests;

/// <summary>
/// Issue #119 — durable per-snapshot coverage (migration 022) and how the snapshot symbol page surfaces it:
/// <c>complete</c> = published AND not coverage-partial, <c>published</c> = the source owns the snapshot,
/// and <c>coverage</c> carries the recorded summary.
/// </summary>
[TestClass]
public class SnapshotCoverageStoreTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SqliteConnection _conn = null!;
    private SnapshotStore _snapshots = null!;
    private long _now;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_cov_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _conn = _db.GetConnection();
        _snapshots = new SnapshotStore(_conn);
        _now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    [TestCleanup]
    public void TestCleanup() => SqliteTestDatabase.Delete(_dbPath, _db);

    private static SnapshotCoverage Partial() => new()
    {
        Verdict = SnapshotCoverageVerdict.Partial,
        Reasons = ["1 of 2 discovered solution(s) were not selected"],
        SelectionSource = "default_root",
        SolutionsDiscovered = 2,
        SolutionsSelected = 1,
        SolutionsNotSelected = 1,
        ProjectsDeclared = 5,
        ProjectsLoaded = 5,
        ProjectFilesOnDisk = 415,
        ProjectFilesUnreferenced = 410,
        SubmodulesDeclared = 1,
        SubmodulesUnpopulated = 1
    };

    [TestMethod]
    public void Record_RoundTripsTheSummary_AndIsImmutable()
    {
        var snap = Snapshot("c1", complete: true);
        var store = new SnapshotCoverageStore(_conn);

        Assert.IsTrue(store.Record(snap, Partial(), _now));
        Assert.IsFalse(store.Record(snap, new SnapshotCoverage { Verdict = SnapshotCoverageVerdict.Complete }, _now),
            "coverage is immutable once recorded, like the snapshot it describes");

        var read = store.Get(snap)!;
        Assert.AreEqual(SnapshotCoverageVerdict.Partial, read.Verdict);
        Assert.IsTrue(read.IsPartial);
        CollectionAssert.AreEqual(Partial().Reasons.ToArray(), read.Reasons.ToArray());
        Assert.AreEqual(410, read.ProjectFilesUnreferenced);
        Assert.AreEqual(1, read.SubmodulesUnpopulated);
        Assert.AreEqual("default_root", read.SelectionSource);
    }

    [TestMethod]
    public void Record_UnknownVerdict_Throws()
    {
        var snap = Snapshot("c1", complete: true);
        Assert.ThrowsExactly<ArgumentException>(() =>
            new SnapshotCoverageStore(_conn).Record(snap, new SnapshotCoverage { Verdict = "mostly" }, _now));
    }

    [TestMethod]
    public void Get_NoRow_IsNull_AndDeleteRemovesTheRow()
    {
        var snap = Snapshot("c1", complete: true);
        var store = new SnapshotCoverageStore(_conn);
        Assert.IsNull(store.Get(snap), "no row ⇒ coverage not recorded");

        store.Record(snap, Partial(), _now);
        store.Delete(snap);
        Assert.IsNull(store.Get(snap));
    }

    [TestMethod]
    public void Get_CorruptSummary_KeepsTheAuthoritativeVerdict()
    {
        var snap = Snapshot("c1", complete: true);
        new SnapshotCoverageStore(_conn).Record(snap, Partial(), _now);
        Exec($"UPDATE snapshot_coverage SET summary_json = '{{not json' WHERE snapshot_id = {snap};");

        var read = new SnapshotCoverageStore(_conn).Get(snap)!;

        Assert.AreEqual(SnapshotCoverageVerdict.Partial, read.Verdict,
            "a corrupt summary must never turn a recorded partial into 'not recorded' or complete");
    }

    [TestMethod]
    public void Get_CatalogWithoutTheTable_IsNull()
    {
        var snap = Snapshot("c1", complete: true);
        Exec("DROP TABLE snapshot_coverage;");

        Assert.IsNull(new SnapshotCoverageStore(_conn).Get(snap), "a pre-022 catalog has nothing recorded");
    }

    [TestMethod]
    public void DeletingTheSnapshot_CascadesItsCoverage()
    {
        var snap = Snapshot("c1", complete: true);
        new SnapshotCoverageStore(_conn).Record(snap, Partial(), _now);

        Exec("PRAGMA foreign_keys = ON;");
        Exec($"DELETE FROM snapshots WHERE id = {snap};");

        Assert.AreEqual(0L, Scalar($"SELECT COUNT(*) FROM snapshot_coverage WHERE snapshot_id = {snap};"));
    }

    // ---- LocalBaseSnapshotSource page semantics ------------------------------------------------------

    [TestMethod]
    public async Task Page_PublishedWithPartialCoverage_ServesRowsButIsNotComplete()
    {
        var snap = Snapshot("c1", complete: true, symbols: 2);
        new SnapshotCoverageStore(_conn).Record(snap, Partial(), _now);

        var page = await Fetch("c1");

        Assert.AreEqual(2, page.Symbols.Count, "a partial snapshot still serves the rows it has");
        Assert.IsFalse(page.Complete, "a partial snapshot is never presented as complete (#119)");
        Assert.IsTrue(page.Published);
        Assert.IsTrue(page.IsPublished);
        Assert.AreEqual(SnapshotCoverageVerdict.Partial, page.Coverage!.Verdict);
    }

    [TestMethod]
    public async Task Page_PublishedWithCompleteCoverage_IsComplete()
    {
        var snap = Snapshot("c1", complete: true, symbols: 1);
        new SnapshotCoverageStore(_conn).Record(snap, new SnapshotCoverage { Verdict = SnapshotCoverageVerdict.Complete }, _now);

        var page = await Fetch("c1");

        Assert.IsTrue(page.Complete);
        Assert.IsTrue(page.Published);
        Assert.AreEqual(SnapshotCoverageVerdict.Complete, page.Coverage!.Verdict);
    }

    [TestMethod]
    public async Task Page_PublishedWithoutRecordedCoverage_StaysComplete_CoverageAbsent()
    {
        Snapshot("c1", complete: true, symbols: 1);

        var page = await Fetch("c1");

        Assert.IsTrue(page.Complete, "no recorded coverage (local/pre-022) keeps the published meaning");
        Assert.IsTrue(page.Published);
        Assert.IsNull(page.Coverage);
    }

    [TestMethod]
    public async Task Page_PublishedWithZeroProjects_IsAnEmptyPublishedPage()
    {
        var snap = Snapshot("c1", complete: true, symbols: 0, withProject: false);
        new SnapshotCoverageStore(_conn).Record(snap, Partial(), _now);

        var page = await Fetch("c1");

        Assert.AreEqual(0, page.Symbols.Count);
        Assert.IsTrue(page.Published, "an empty published snapshot still owns its (empty) cursor space");
        Assert.IsFalse(page.Complete);
        Assert.IsNotNull(page.Coverage);
    }

    [TestMethod]
    public async Task Page_PendingOrUnknownSnapshot_IsUnpublished()
    {
        Snapshot("pending", complete: false, symbols: 2);

        var pending = await Fetch("pending");
        var unknown = await Fetch("never-indexed");

        foreach (var page in new[] { pending, unknown })
        {
            Assert.AreEqual(0, page.Symbols.Count);
            Assert.IsFalse(page.Complete);
            Assert.AreEqual(false, page.Published);
            Assert.IsFalse(page.IsPublished);
        }
    }

    [TestMethod]
    public void Page_LegacyPeerWithoutPublished_RoutesOnComplete_ButIsNeverProvenPublished()
    {
        var legacy = new SnapshotSymbolPage { IdentityHash = "h", Symbols = [], Complete = true };
        Assert.IsTrue(legacy.IsPublished, "composite routing keeps the pre-#119 `complete` cursor-ownership rule");
        Assert.IsFalse(legacy.IsProvenPublished,
            "a pre-#119 peer's `complete` was true for ANY known identity, so it never proves publication");
        Assert.IsFalse((legacy with { Complete = false }).IsPublished);
        Assert.IsTrue((legacy with { Complete = false, Published = true }).IsPublished);
        Assert.IsTrue((legacy with { Complete = false, Published = true }).IsProvenPublished);
        Assert.IsFalse((legacy with { Published = false }).IsProvenPublished);
    }

    [TestMethod]
    public void Page_WireFormat_IsSnakeCase_AndOmitsNullCoverage()
    {
        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        var page = new SnapshotSymbolPage
        {
            IdentityHash = "h", Symbols = [], Complete = false, Published = true,
            Coverage = new SnapshotCoverage { Verdict = SnapshotCoverageVerdict.Partial, SubmodulesUnpopulated = 1 }
        };

        var json = System.Text.Json.JsonSerializer.Serialize(page, options);
        StringAssert.Contains(json, "\"published\":true");
        StringAssert.Contains(json, "\"coverage\":{\"verdict\":\"partial\"");
        StringAssert.Contains(json, "\"submodules_unpopulated\":1");
        Assert.IsFalse(json.Contains("is_published") || json.Contains("is_partial"), "computed flags are not on the wire");

        var none = System.Text.Json.JsonSerializer.Serialize(page with { Coverage = null, Published = null }, options);
        Assert.IsFalse(none.Contains("coverage") || none.Contains("published"));
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private Task<SnapshotSymbolPage> Fetch(string commit) =>
        new LocalBaseSnapshotSource(_conn).FetchSymbolsAsync(
            new SnapshotPageRequest { IdentityHash = Identity(commit).Hash }, CancellationToken.None);

    private static SnapshotIdentity Identity(string commit) => new()
    {
        RepositoryRemoteUrl = "https://github.com/org/app",
        CommitSha = commit,
        SchemaVersion = IndexDatabase.LatestSchemaVersion,
        AnalyzerVersion = "test-analyzer",
        ToolchainFingerprint = "test-toolchain"
    };

    private long Snapshot(string commit, bool complete, int symbols = 0, bool withProject = true)
    {
        var repoId = _snapshots.EnsureRepository("https://github.com/org/app", _now);
        var runStore = new IndexRunStore(_conn);
        var runId = runStore.BeginRun("full", _now,
            IndexProfileDescriptor.Full.ConfigurationHash, IndexProfiles.Deep, (long)IndexFeature.Deep);
        runStore.MarkComplete(runId, _now, 1);
        var (snapId, _, _) = _snapshots.BeginPending(Identity(commit), repoId, null, runId, _now);

        if (withProject)
        {
            long projectId;
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO projects (canonical_id, git_remote_url, repo_relative_path, last_indexed_at, snapshot_id)
                    VALUES (@c, 'https://github.com/org/app', 'src/App/App.csproj', @now, @snap) RETURNING id;
                    """;
                cmd.Parameters.AddWithValue("@c", $"proj_{snapId}");
                cmd.Parameters.AddWithValue("@now", _now);
                cmd.Parameters.AddWithValue("@snap", snapId);
                projectId = (long)cmd.ExecuteScalar()!;
            }
            _snapshots.MapProject(snapId, projectId);

            for (var i = 0; i < symbols; i++)
            {
                using var s = _conn.CreateCommand();
                s.CommandText = """
                    INSERT INTO symbols
                        (project_id, symbol_key, fully_qualified_name, display_name, kind, accessibility,
                         line_start, line_end, last_indexed_at)
                    VALUES (@p, @key, @fqn, @name, 0, 0, 1, 10, @now);
                    """;
                s.Parameters.AddWithValue("@p", projectId);
                s.Parameters.AddWithValue("@key", $"T:App.T{i}:{snapId}");
                s.Parameters.AddWithValue("@fqn", $"global::App.T{i}");
                s.Parameters.AddWithValue("@name", $"T{i}");
                s.Parameters.AddWithValue("@now", _now);
                s.ExecuteNonQuery();
            }
        }

        if (complete)
            _snapshots.MarkComplete(snapId, _now);
        return snapId;
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private long Scalar(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
}
