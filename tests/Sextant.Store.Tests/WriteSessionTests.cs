using Microsoft.Data.Sqlite;
using Sextant.Core;

namespace Sextant.Store.Tests;

/// <summary>
/// Phase 3 — bounded, batched writes. Covers the unit-of-work session, the generation ledger and
/// scope, startup recovery, WAL-control pragmas, prepared-writer parity, and concurrent reads while
/// a new generation is being built.
/// </summary>
[TestClass]
public class WriteSessionTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private ProjectStore _projectStore = null!;
    private SymbolStore _symbolStore = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_ws_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        var conn = _db.GetConnection();
        _projectStore = new ProjectStore(conn);
        _symbolStore = new SymbolStore(conn);
    }

    [TestCleanup]
    public void TestCleanup()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        TryDelete(_dbPath);
        TryDelete(_dbPath + "-wal");
        TryDelete(_dbPath + "-shm");
    }

    // ---- Write session ----

    [TestMethod]
    public void CommitBatch_PersistsRows()
    {
        var projectId = InsertProject();
        using (var session = _db.BeginWriteSession())
        {
            session.Begin();
            _symbolStore.Insert(MakeSymbol(projectId, "A"));
            _symbolStore.Insert(MakeSymbol(projectId, "B"));
            session.CommitBatch();
            session.Complete();
        }

        Assert.AreEqual(2, CountSymbols());
    }

    [TestMethod]
    public void Rollback_DiscardsUncommittedRows()
    {
        var projectId = InsertProject();
        using (var session = _db.BeginWriteSession())
        {
            session.Begin();
            _symbolStore.Insert(MakeSymbol(projectId, "A"));
            session.CommitBatch(); // A is durable
            _symbolStore.Insert(MakeSymbol(projectId, "B")); // uncommitted
            session.Rollback();
        }

        Assert.AreEqual(1, CountSymbols(), "committed batch survives, uncommitted rows are discarded");
    }

    [TestMethod]
    public void DisposeWithoutComplete_RollsBackOpenBatch()
    {
        var projectId = InsertProject();
        using (var session = _db.BeginWriteSession())
        {
            session.Begin();
            _symbolStore.Insert(MakeSymbol(projectId, "A"));
            // No Complete()/CommitBatch(): dispose must roll the open batch back.
        }

        Assert.AreEqual(0, CountSymbols());
    }

    [TestMethod]
    public void RowThreshold_ForcesAutoCommit()
    {
        var projectId = InsertProject();
        var options = IndexWriteOptions.Default with { BatchRowThreshold = 3 };
        using var session = _db.BeginWriteSession(options);
        session.Begin();
        for (var i = 0; i < 7; i++)
        {
            _symbolStore.Insert(MakeSymbol(projectId, $"S{i}"));
            session.RowsWritten();
        }

        // 7 rows / threshold 3 => at least two safety-net commits fired mid-run.
        Assert.IsTrue(session.CommittedBatches >= 2, $"expected >= 2 auto-commits, got {session.CommittedBatches}");
        session.Complete();
        Assert.AreEqual(7, CountSymbols());
    }

    [TestMethod]
    public void PreparedInsert_MatchesSingleInsert()
    {
        var projectId = InsertProject();
        long idSingle;
        long idPrepared;
        using (var session = _db.BeginWriteSession())
        {
            session.Begin();
            idSingle = _symbolStore.Insert(MakeSymbol(projectId, "Single"));
            using (var cmd = _symbolStore.CreateInsertCommand())
                idPrepared = _symbolStore.Insert(cmd, MakeSymbol(projectId, "Prepared"));
            session.Complete();
        }

        var single = _symbolStore.GetById(idSingle);
        var prepared = _symbolStore.GetById(idPrepared);
        Assert.IsNotNull(single);
        Assert.IsNotNull(prepared);
        Assert.AreEqual("Single", single!.DisplayName);
        Assert.AreEqual("Prepared", prepared!.DisplayName);
    }

    [TestMethod]
    public void ReusedPreparedCommand_InsertsManyRows()
    {
        var projectId = InsertProject();
        using (var session = _db.BeginWriteSession())
        {
            session.Begin();
            using (var cmd = _symbolStore.CreateInsertCommand())
            {
                for (var i = 0; i < 50; i++)
                    _symbolStore.Insert(cmd, MakeSymbol(projectId, $"R{i}"));
            }
            session.Complete();
        }

        Assert.AreEqual(50, CountSymbols());
    }

    // ---- Generation ledger / scope ----

    [TestMethod]
    public void RunScope_Complete_MarksLastCompleteRun()
    {
        var runStore = new IndexRunStore(_db.GetConnection());
        using (var scope = runStore.BeginScope("full", Now()))
            scope.Complete(Now(), projects: 3);

        var last = runStore.GetLastCompleteRun();
        Assert.IsNotNull(last);
        Assert.AreEqual(IndexRunState.Complete, last!.Status);
        Assert.AreEqual(3, last.Projects);
    }

    [TestMethod]
    public void RunScope_DisposeWithoutComplete_AbandonsRun()
    {
        var runStore = new IndexRunStore(_db.GetConnection());
        long runId;
        using (var scope = runStore.BeginScope("full", Now()))
            runId = scope.RunId; // dispose without Complete()

        Assert.AreEqual(IndexRunState.Abandoned, runStore.GetById(runId)!.Status);
        Assert.IsNull(runStore.GetLastCompleteRun());
    }

    [TestMethod]
    public void StagingRun_DoesNotReplaceLastCompleteRun()
    {
        var runStore = new IndexRunStore(_db.GetConnection());
        using (var scope = runStore.BeginScope("full", Now()))
            scope.Complete(Now(), projects: 1);
        var firstComplete = runStore.GetLastCompleteRun()!.Id;

        // A new run is staging (in progress) and must not become the last-complete pointer.
        var stagingId = runStore.BeginRun("full", Now());
        var last = runStore.GetLastCompleteRun();
        Assert.AreEqual(firstComplete, last!.Id, "readers keep seeing the previous complete generation while a new one stages");
        Assert.AreEqual(IndexRunState.Staging, runStore.GetById(stagingId)!.Status);
    }

    [TestMethod]
    public void AbandonStaleRuns_MarksAllStagingAbandoned()
    {
        var runStore = new IndexRunStore(_db.GetConnection());
        runStore.BeginRun("full", Now());
        runStore.BeginRun("incremental", Now());

        var abandoned = runStore.AbandonStaleRuns(Now());
        Assert.AreEqual(2, abandoned);
    }

    [TestMethod]
    public void MarkComplete_OnAbandonedRun_DoesNotResurrect()
    {
        var runStore = new IndexRunStore(_db.GetConnection());
        var runId = runStore.BeginRun("full", Now());
        runStore.AbandonRun(runId, Now()); // e.g. recovery abandoned it under a concurrent opener

        var affected = runStore.MarkComplete(runId, Now(), projects: 1);

        Assert.AreEqual(0, affected, "a guarded publish of a non-staging run must update no rows");
        Assert.AreEqual(IndexRunState.Abandoned, runStore.GetById(runId)!.Status,
            "a run abandoned by recovery must never be resurrected to complete");
        Assert.IsNull(runStore.GetLastCompleteRun());
    }

    [TestMethod]
    public void ScopeComplete_OnAbandonedRun_ThrowsAndLeavesAbandoned()
    {
        var runStore = new IndexRunStore(_db.GetConnection());
        var scope = runStore.BeginScope("full", Now());
        runStore.AbandonRun(scope.RunId, Now());

        Assert.ThrowsExactly<InvalidOperationException>(() => scope.Complete(Now(), projects: 1));
        Assert.AreEqual(IndexRunState.Abandoned, runStore.GetById(scope.RunId)!.Status);
        Assert.IsNull(runStore.GetLastCompleteRun());
    }

    [TestMethod]
    public void PublishInTransaction_CommitsPointerAtomicallyWithData()
    {
        var projectId = InsertProject();
        var runStore = new IndexRunStore(_db.GetConnection());
        var runScope = runStore.BeginScope("full", Now());
        using (var session = _db.BeginWriteSession())
        {
            session.Begin();
            _symbolStore.Insert(MakeSymbol(projectId, "Published"));
            // Flip the pointer inside the same open transaction as the data, then commit both.
            runStore.MarkComplete(runScope.RunId, Now(), projects: 1);
            session.Complete();
            runScope.Detach();
        }

        Assert.AreEqual(1, CountSymbols());
        Assert.AreEqual(runScope.RunId, runStore.GetLastCompleteRun()!.Id);
    }

    [TestMethod]
    public void RecordFootprint_PersistsProvenanceWithoutChangingStatus()
    {
        var runStore = new IndexRunStore(_db.GetConnection());
        using var scope = runStore.BeginScope("full", Now());
        scope.Complete(Now(), projects: 1);

        runStore.RecordFootprint(scope.RunId, finalDbBytes: 4096, finalWalBytes: 128, finalShmBytes: 32);

        var run = runStore.GetById(scope.RunId)!;
        Assert.AreEqual(IndexRunState.Complete, run.Status);
        Assert.AreEqual(4096, run.FinalDbBytes);
        Assert.AreEqual(128, run.FinalWalBytes);
        Assert.AreEqual(32, run.FinalShmBytes);
    }

    // ---- Startup recovery ----

    [TestMethod]
    public void Recover_AbandonsStaleStagingRuns()
    {
        var runStore = new IndexRunStore(_db.GetConnection());
        var stagingId = runStore.BeginRun("full", Now()); // simulate a run left staging by a dead process

        _db.Recover();

        Assert.AreEqual(IndexRunState.Abandoned, runStore.GetById(stagingId)!.Status);
    }

    [TestMethod]
    public void Reopen_RecoversAndAbandonsStagingRun()
    {
        long stagingId;
        var runStore = new IndexRunStore(_db.GetConnection());
        stagingId = runStore.BeginRun("full", Now());
        _db.Checkpoint();
        _db.Dispose();

        // A fresh open runs migrations, which invoke Recover() and sweep the abandoned generation.
        using var reopened = new IndexDatabase(_dbPath);
        reopened.RunMigrations();
        var reopenedStore = new IndexRunStore(reopened.GetConnection());
        Assert.AreEqual(IndexRunState.Abandoned, reopenedStore.GetById(stagingId)!.Status);
    }

    // ---- WAL controls ----

    [TestMethod]
    public void WalControlPragmas_ReflectWriteOptions()
    {
        var conn = _db.GetConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA wal_autocheckpoint;";
        Assert.AreEqual((long)IndexWriteOptions.Default.WalAutocheckpointPages, (long)cmd.ExecuteScalar()!);

        cmd.CommandText = "PRAGMA journal_size_limit;";
        Assert.AreEqual(IndexWriteOptions.Default.JournalSizeLimitBytes, (long)cmd.ExecuteScalar()!);
    }

    // ---- Concurrent reads during a staging generation ----

    [TestMethod]
    public void ConcurrentRead_DuringOpenBatch_SeesCommittedBaseline()
    {
        var projectId = InsertProject();
        using var session = _db.BeginWriteSession();
        session.Begin();
        _symbolStore.Insert(MakeSymbol(projectId, "Committed"));
        session.CommitBatch();

        // Start a new, uncommitted batch (a generation being built).
        _symbolStore.Insert(MakeSymbol(projectId, "Uncommitted"));

        // The real reader (the MCP server) is a separate process, so it never joins the writer's
        // in-process shared cache; it coordinates through the WAL at the file level and gets a
        // consistent snapshot. A private-cache connection here reproduces that cross-process reader
        // faithfully; a shared-cache reader in the same process would instead take a table lock.
        using var reader = OpenPrivateCacheReader();
        using var cmd = reader.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM symbols;";
        var visible = (long)cmd.ExecuteScalar()!;
        Assert.AreEqual(1, visible, "a concurrent reader sees the last committed generation, not the in-flight batch");

        session.Rollback();
    }

    private SqliteConnection OpenPrivateCacheReader()
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private
        }.ToString());
        conn.Open();
        return conn;
    }

    // ---- Helpers ----

    private long InsertProject() => _projectStore.Insert(new ProjectIdentity
    {
        CanonicalId = $"test{Guid.NewGuid():N}"[..16],
        GitRemoteUrl = "https://github.com/test/repo",
        RepoRelativePath = "src/Test/Test.csproj"
    }, Now());

    private static SymbolInfo MakeSymbol(long projectId, string name) => new()
    {
        ProjectId = projectId,
        SymbolKey = $"global::Test.{name}",
        FullyQualifiedName = $"global::Test.{name}",
        DisplayName = name,
        Kind = SymbolKind.Class,
        Accessibility = Accessibility.Public,
        FilePath = $"src/{name}.cs",
        LineStart = 1,
        LineEnd = 10,
        LastIndexedAt = Now()
    };

    private long CountSymbols()
    {
        using var cmd = _db.GetConnection().CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM symbols;";
        return (long)cmd.ExecuteScalar()!;
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* best-effort cleanup */ }
    }
}
