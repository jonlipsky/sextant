using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #85: a sequence-bearing ensure of a commit whose already-built snapshot was SUPERSEDED by a later
/// branch advance (a reset / force-push A@10 → B@20 → A@30) must re-select that snapshot on the no-worker
/// reuse path — mirroring the orchestrator's <c>SelectExistingSnapshot</c>: restore it to Complete and apply
/// the #84 forward-only advance (re-point on a higher sequence, decline on a lower/equal one) — while never
/// resurrecting a data-less/unservable snapshot (it is demoted and rebuilt instead) and leaving the
/// null-sequence (local/legacy) path byte-for-byte unchanged.
/// </summary>
[TestClass]
public class EnsureSupersededReselectTests
{
    private const string Repo = "https://github.com/org/app";
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SqliteConnection _conn = null!;
    private SnapshotService _service = null!;
    private FakeSnapshotWorker _worker = null!;
    private long _snapA;
    private long _snapB;

    [TestCleanup]
    public void TestCleanup()
    {
        _service?.Dispose();
        SqliteTestDatabase.Delete(_dbPath, _db);
    }

    [TestMethod]
    public async Task ResetToOlderCommit_WithHigherSequence_RepointsToSupersededSnapshot_WithoutWorker()
    {
        await StartAtBWithASuperseded();

        var result = await _service.EnsureSnapshotAsync(Ensure("commit-A", seq: 30));

        Assert.AreEqual(0, _worker.Calls, "an intact superseded snapshot is re-selected without running the worker");
        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status);
        Assert.AreEqual(_snapA, result.SnapshotId, "the ensure attaches to the existing immutable snapshot A");
        Assert.IsTrue(result.Attached, "the ensure attached to A's existing durable job");
        Assert.AreEqual(_snapA, PointerOfMain(), "the higher sequence re-points the branch back to A (A→B→A)");
        Assert.AreEqual(_snapA, _service.ResolveBranch(Repo, null)?.Id, "the default branch resolves to A again");
        Assert.AreEqual(30, HeadSequenceOfMain(), "the advanced sequence is persisted");
        Assert.AreEqual(SnapshotStatus.Complete, StatusOf(_snapA), "A is un-superseded (Complete again)");
        Assert.AreEqual(SnapshotStatus.Superseded, StatusOf(_snapB), "the previous head B is superseded");
        Assert.AreEqual(1, JobCount(result.IdentityHash), "still exactly one durable job per identity");
    }

    [TestMethod]
    public async Task AfterRepoint_StaleSequence_IsDeclined_AndNeverRegressesTheHead()
    {
        await StartAtBWithASuperseded();
        await _service.EnsureSnapshotAsync(Ensure("commit-A", seq: 30));

        // A delayed/out-of-order delivery of B@20 now carries a LOWER sequence than the stored 30.
        var stale = await _service.EnsureSnapshotAsync(Ensure("commit-B", seq: 20));

        Assert.AreEqual(0, _worker.Calls);
        Assert.AreEqual(SnapshotJobStatus.Complete, stale.Status, "the stale ensure still attaches B's immutable snapshot");
        Assert.AreEqual(_snapB, stale.SnapshotId);
        Assert.AreEqual(_snapA, PointerOfMain(), "a lower sequence never moves the pointer back to B");
        Assert.AreEqual(30, HeadSequenceOfMain(), "the stored sequence is not regressed");
        Assert.AreEqual(SnapshotStatus.Complete, StatusOf(_snapA), "the head A is not superseded by a declined advance");
        // Parity with SelectExistingSnapshot (#84 criterion 3): a declined re-select still restores the
        // older snapshot to Complete so it stays resolvable by commit — it is simply not the branch head.
        Assert.AreEqual(SnapshotStatus.Complete, StatusOf(_snapB),
            "a declined re-select leaves B Complete (attached, resolvable by commit) but not the head");
        Assert.AreEqual(_snapA, _service.ResolveBranch(Repo, null)?.Id, "the branch still resolves to A");
    }

    [TestMethod]
    public async Task EqualSequence_OnSupersededSnapshot_IsDeclined()
    {
        await StartAtBWithASuperseded();

        var result = await _service.EnsureSnapshotAsync(Ensure("commit-A", seq: 20));

        Assert.AreEqual(0, _worker.Calls);
        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status);
        Assert.AreEqual(_snapA, result.SnapshotId);
        Assert.AreEqual(_snapB, PointerOfMain(), "an equal sequence does not advance (forward-only, strict >)");
        Assert.AreEqual(20, HeadSequenceOfMain());
        Assert.AreEqual(SnapshotStatus.Complete, StatusOf(_snapB), "the head B is untouched");
        Assert.AreEqual(SnapshotStatus.Complete, StatusOf(_snapA), "A is restored to Complete (SelectExistingSnapshot parity)");
    }

    [TestMethod]
    public async Task Repoint_ReportsRecordedPartialCoverage_WithoutRewritingIt()
    {
        await StartAtBWithASuperseded(coverageForA: PartialCoverage());
        var (rowsBefore, recordedAtBefore) = CoverageRow(_snapA);

        var result = await _service.EnsureSnapshotAsync(Ensure("commit-A", seq: 30));

        Assert.AreEqual(0, _worker.Calls);
        Assert.AreEqual(_snapA, PointerOfMain(), "a partial-coverage snapshot is still published and re-pointed");
        Assert.AreEqual(SnapshotJobStatus.Partial, result.Status,
            "the verdict comes from A's durable coverage record — never blindly Complete (issue #119)");
        StringAssert.Contains(result.Reason, "snapshot coverage is partial");
        Assert.AreEqual(SnapshotCoverageVerdict.Partial, result.Coverage!.Verdict);
        Assert.AreEqual(SnapshotJobStatus.Partial, _service.GetStatus(result.JobId)!.Job.Status,
            "the durable job row records Partial");
        var (rowsAfter, recordedAtAfter) = CoverageRow(_snapA);
        Assert.AreEqual(1, rowsBefore);
        Assert.AreEqual(1, rowsAfter, "re-select never duplicates the coverage row");
        Assert.AreEqual(recordedAtBefore, recordedAtAfter, "re-select never rewrites/backfills the immutable coverage row");
    }

    [TestMethod]
    public async Task SupersededSnapshotReclaimedByRetention_IsRebuilt_NotResurrected()
    {
        // The default worker genuinely publishes, so a rebuild is observable.
        await StartAtBWithASuperseded(publishingWorker: true, retention: new RetentionPolicy { KeepCompleteGenerations = 1 });
        _service.RunRetention(execute: true);
        Assert.IsNull(new SnapshotStore(_conn).GetById(_snapA),
            "precondition: retention reclaimed the superseded, un-pointed snapshot A (row + data)");

        var result = await _service.EnsureSnapshotAsync(Ensure("commit-A", seq: 30));

        Assert.AreEqual(1, _worker.Calls, "a reclaimed snapshot is regenerated by the worker");
        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status);
        Assert.IsNotNull(result.SnapshotId);
        Assert.AreNotEqual(_snapA, result.SnapshotId, "the rebuild publishes a fresh snapshot, not a phantom of the old id");
        Assert.AreEqual(SnapshotStatus.Complete, StatusOf(result.SnapshotId!.Value));
        Assert.AreEqual(1, JobCount(result.IdentityHash), "the rebuild reuses the ONE durable job for the identity");
    }

    [TestMethod]
    public async Task DataLessSupersededSnapshot_IsNotResurrected_WhenTheRebuildFails()
    {
        await StartAtBWithASuperseded();
        DeleteSnapshotData(_snapA);

        var result = await _service.EnsureSnapshotAsync(Ensure("commit-A", seq: 30));

        Assert.AreEqual(1, _worker.Calls, "a data-less superseded snapshot falls back to the worker (rebuild)");
        Assert.AreEqual(SnapshotJobStatus.Failed, result.Status, "the failed rebuild is reported, never a phantom complete");
        Assert.AreNotEqual(SnapshotStatus.Complete, StatusOf(_snapA), "a data-less snapshot is never resurrected as Complete");
        Assert.AreEqual(SnapshotStatus.Failed, StatusOf(_snapA), "it is demoted so the orchestrator genuinely rebuilds it");
        Assert.AreEqual(_snapB, PointerOfMain(), "the branch head is not moved to a snapshot with no data");
        Assert.AreEqual(20, HeadSequenceOfMain(), "the sequence does not advance without a servable snapshot");
    }

    [TestMethod]
    public async Task DataLessSupersededSnapshot_IsRebuiltIntoTheSameIdentity()
    {
        // A worker that mirrors the orchestrator's retry of a Failed identity: reset to Pending, rebuild into
        // the same snapshot id, publish, and forward-only advance the requested branch.
        await StartAtBWithASuperseded(behavior: OrchestratorLikeRebuild);
        DeleteSnapshotData(_snapA);

        var result = await _service.EnsureSnapshotAsync(Ensure("commit-A", seq: 30));

        Assert.AreEqual(1, _worker.Calls);
        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status);
        Assert.AreEqual(_snapA, result.SnapshotId, "the demoted identity is rebuilt into its existing snapshot id");
        Assert.AreEqual(SnapshotStatus.Complete, StatusOf(_snapA));
        Assert.AreEqual(_snapA, PointerOfMain(), "the rebuilt snapshot becomes the head via the worker's own advance");
        Assert.AreEqual(30, HeadSequenceOfMain());
    }

    [TestMethod]
    public async Task NullSequence_OnSupersededSnapshot_KeepsTheWorkerPath_Unchanged()
    {
        await StartAtBWithASuperseded();

        // No sequence (the local/legacy path): the service must NOT re-select on the reuse path — it still
        // hands the identity to the worker exactly as before #85 (#84 criterion 2).
        await _service.EnsureSnapshotAsync(Ensure("commit-A", seq: null));

        Assert.AreEqual(1, _worker.Calls, "the null-sequence path still runs the worker (byte-for-byte unchanged)");
        Assert.AreEqual(SnapshotStatus.Superseded, StatusOf(_snapA), "the service does not un-supersede on the null path");
        Assert.AreEqual(_snapB, PointerOfMain());
        Assert.AreEqual(20, HeadSequenceOfMain());
    }

    [TestMethod]
    public async Task ConcurrentRepointEnsures_AttachToOneJob_AndRunNoWorker()
    {
        await StartAtBWithASuperseded(useGate: true);

        var results = await RunWhileWriteGateContended(
            Ensure("commit-A", seq: 30), Ensure("commit-A", seq: 30));

        Assert.AreEqual(1, _worker.Calls, "only the gate-holding blocker ran the worker; neither re-point ensure did");
        Assert.AreEqual(results[0].JobId, results[1].JobId, "both attach to the ONE durable job (identity_hash UNIQUE)");
        Assert.AreEqual(1, JobCount(results[0].IdentityHash));
        foreach (var r in results)
        {
            Assert.AreEqual(SnapshotJobStatus.Complete, r.Status);
            Assert.AreEqual(_snapA, r.SnapshotId);
        }
        Assert.AreEqual(_snapA, PointerOfMain());
        Assert.AreEqual(30, HeadSequenceOfMain());
        Assert.AreEqual(SnapshotStatus.Complete, StatusOf(_snapA));
        Assert.AreEqual(SnapshotStatus.Superseded, StatusOf(_snapB));
    }

    [DataTestMethod]
    [DataRow(true, DisplayName = "A@30 then B@40")]
    [DataRow(false, DisplayName = "B@40 then A@30")]
    public async Task InterleavedRepointAndAdvance_ConvergeOnTheHighestSequence(bool repointFirst)
    {
        await StartAtBWithASuperseded();

        if (repointFirst)
        {
            await _service.EnsureSnapshotAsync(Ensure("commit-A", seq: 30));
            await _service.EnsureSnapshotAsync(Ensure("commit-B", seq: 40));
        }
        else
        {
            await _service.EnsureSnapshotAsync(Ensure("commit-B", seq: 40));
            await _service.EnsureSnapshotAsync(Ensure("commit-A", seq: 30));
        }

        Assert.AreEqual(0, _worker.Calls);
        Assert.AreEqual(_snapB, PointerOfMain(), "whatever the delivery order, the highest sequence (B@40) wins");
        Assert.AreEqual(40, HeadSequenceOfMain());
        Assert.AreEqual(SnapshotStatus.Complete, StatusOf(_snapB));
        Assert.AreEqual(_snapB, _service.ResolveBranch(Repo, null)?.Id);
    }

    [DataTestMethod]
    [DataRow(true, DisplayName = "A@30 launched first")]
    [DataRow(false, DisplayName = "B@40 launched first")]
    public async Task ConcurrentRepointAndAdvance_ConvergeOnTheHighestSequence(bool repointFirst)
    {
        await StartAtBWithASuperseded(useGate: true);
        var repoint = Ensure("commit-A", seq: 30);
        var advance = Ensure("commit-B", seq: 40);

        await (repointFirst ? RunWhileWriteGateContended(repoint, advance) : RunWhileWriteGateContended(advance, repoint));

        Assert.AreEqual(1, _worker.Calls, "only the gate-holding blocker ran the worker");
        Assert.AreEqual(_snapB, PointerOfMain());
        Assert.AreEqual(40, HeadSequenceOfMain());
        Assert.AreEqual(SnapshotStatus.Complete, StatusOf(_snapB));
        Assert.AreEqual(_snapB, _service.ResolveBranch(Repo, null)?.Id, "the head always resolves (never a Superseded head)");
    }

    // The terminal-attach fast path must validate a snapshot and advance the branch to it in ONE write-gate
    // hold. Starting from head A@10 with B's terminal job already recorded (a declined B@5), racing A@30
    // against B@20 must always leave the head on A — and never on a Superseded snapshot, which is what a
    // release of the gate between A's validation and A's advance would allow (B@20 supersedes A in between,
    // then A@30 re-points to the now-Superseded A without restoring it).
    [DataTestMethod]
    [DataRow(true, DisplayName = "A@30 launched first")]
    [DataRow(false, DisplayName = "B@20 launched first")]
    public async Task RacingTerminalAttachAdvances_NeverLeaveTheHeadOnASupersededSnapshot(bool higherFirst)
    {
        StartService(useGate: true);
        await _service.EnsureSnapshotAsync(Ensure("commit-A", seq: 10));
        await _service.EnsureSnapshotAsync(Ensure("commit-B", seq: 5));
        Assert.AreEqual(_snapA, PointerOfMain(), "precondition: head A@10 (B@5 was declined)");
        Assert.AreEqual(SnapshotStatus.Complete, StatusOf(_snapB), "precondition: B has a usable terminal job");
        Assert.AreEqual(0, _worker.Calls);
        var higher = Ensure("commit-A", seq: 30);
        var lower = Ensure("commit-B", seq: 20);

        await (higherFirst ? RunWhileWriteGateContended(higher, lower) : RunWhileWriteGateContended(lower, higher));

        Assert.AreEqual(1, _worker.Calls, "only the gate-holding blocker ran the worker");
        Assert.AreEqual(_snapA, PointerOfMain(), "the highest sequence (A@30) wins in either order");
        Assert.AreEqual(30, HeadSequenceOfMain());
        Assert.AreEqual(SnapshotStatus.Complete, StatusOf(_snapA), "the head is never a Superseded snapshot");
        Assert.AreEqual(_snapA, _service.ResolveBranch(Repo, null)?.Id, "the head resolves");
    }

    // ---- helpers --------------------------------------------------------------------------------

    // Publishes A and B and starts the service over them. useGate makes every worker call block on the fake
    // worker's gate — used by RunWhileWriteGateContended to hold the service's write gate.
    private void StartService(
        SnapshotCoverage? coverageForA = null,
        Func<FakeSnapshotWorker, EnsureSnapshotRequest, SnapshotWorkResult>? behavior = null,
        bool publishingWorker = false,
        RetentionPolicy? retention = null,
        bool useGate = false)
    {
        _dbPath = ServiceTestFixtures.NewDbPath();
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _conn = _db.GetConnection();
        _snapA = ServiceTestFixtures.PublishComplete(_db, new EnsureSnapshotRequest { RepositoryRemoteUrl = Repo, CommitSha = "commit-A" });
        _snapB = ServiceTestFixtures.PublishComplete(_db, new EnsureSnapshotRequest { RepositoryRemoteUrl = Repo, CommitSha = "commit-B" });
        if (coverageForA is not null)
            new SnapshotCoverageStore(_conn).Record(_snapA, coverageForA, 1);

        // Default: a worker that throws, so any unexpected worker run surfaces as a Failed job (and Calls > 0).
        // publishingWorker: the fixture's default behavior, which genuinely publishes the requested identity.
        if (behavior is null && !publishingWorker)
            behavior = FakeSnapshotWorker.Throws("worker must not run on the re-select path");
        _worker = new FakeSnapshotWorker(_db, behavior) { UseGate = useGate };
        _service = SnapshotService.Start(ServiceTestFixtures.NewOptions(_dbPath, retention: retention), _worker, _db);
    }

    // Drives A@10 → B@20 through the reuse path so the default branch sits on B at sequence 20 with A
    // Superseded — the precondition for a reset back to A.
    private async Task StartAtBWithASuperseded(
        SnapshotCoverage? coverageForA = null,
        Func<FakeSnapshotWorker, EnsureSnapshotRequest, SnapshotWorkResult>? behavior = null,
        bool publishingWorker = false,
        RetentionPolicy? retention = null,
        bool useGate = false)
    {
        StartService(coverageForA, behavior, publishingWorker, retention, useGate);

        await _service.EnsureSnapshotAsync(Ensure("commit-A", seq: 10));
        await _service.EnsureSnapshotAsync(Ensure("commit-B", seq: 20));
        Assert.AreEqual(_snapB, PointerOfMain(), "precondition: the branch advanced to B");
        Assert.AreEqual(SnapshotStatus.Superseded, StatusOf(_snapA), "precondition: A was superseded by the advance");
        Assert.AreEqual(0, _worker.Calls, "precondition: A@10 → B@20 ran entirely on the reuse path");
    }

    // Forces genuine overlap: an unrelated, never-published identity runs the (gated) worker, which holds the
    // service's single write gate while it waits; the contenders are then launched — each parking on the held
    // gate — and only then is the gate released, so they all contend for it at once instead of completing
    // sequentially on an uncontended semaphore. Launch order is best-effort (staggered, but a loaded host may
    // park them out of order), so every caller's assertions must hold for ANY acquisition order.
    private async Task<EnsureSnapshotResult[]> RunWhileWriteGateContended(params EnsureSnapshotRequest[] contenders)
    {
        var timeout = TimeSpan.FromSeconds(30);
        var blocker = _service.EnsureSnapshotAsync(new EnsureSnapshotRequest { RepositoryRemoteUrl = Repo, CommitSha = "commit-blocker" });
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (_worker.Calls == 0)
        {
            Assert.IsTrue(waited.Elapsed < timeout, "the blocker never reached the worker");
            await Task.Delay(10);
        }

        var tasks = new List<Task<EnsureSnapshotResult>>();
        foreach (var request in contenders)
        {
            tasks.Add(Task.Run(() => _service.EnsureSnapshotAsync(request)));
            await Task.Delay(50);
        }
        Assert.IsTrue(tasks.TrueForAll(t => !t.IsCompleted), "the contenders are parked behind the held write gate");

        _worker.Gate.SetResult();
        await blocker.WaitAsync(timeout);
        return await Task.WhenAll(tasks).WaitAsync(timeout);
    }

    private static SnapshotWorkResult OrchestratorLikeRebuild(FakeSnapshotWorker self, EnsureSnapshotRequest request)
    {
        var conn = self.Database.GetConnection();
        var snapshots = new SnapshotStore(conn);
        var identityHash = request.ToIdentity().Hash;
        if (snapshots.GetByIdentityHash(identityHash) is { Status: SnapshotStatus.Failed } failed)
            snapshots.MarkStatus(failed.Id, SnapshotStatus.Pending);
        var snapId = ServiceTestFixtures.PublishComplete(self.Database, request);
        var repoId = snapshots.GetRepositoryId(request.RepositoryRemoteUrl)!.Value;
        var branchId = snapshots.GetBranchId(repoId, request.BranchName ?? "main")!.Value;
        snapshots.AdvanceBranchPointerForwardOnly(
            branchId, snapId, request.BranchHeadSequence, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return SnapshotWorkResult.Complete(snapId);
    }

    private static SnapshotCoverage PartialCoverage() => new()
    {
        Verdict = SnapshotCoverageVerdict.Partial,
        Reasons = ["1 of 2 declared submodule(s) are not populated in the checkout (libs/shared); their projects are not indexed."],
        SelectionSource = "default_root",
        SubmodulesDeclared = 2,
        SubmodulesUnpopulated = 1
    };

    private static EnsureSnapshotRequest Ensure(string commit, long? seq) => new()
    {
        RepositoryRemoteUrl = Repo,
        CommitSha = commit,
        BranchHeadSequence = seq
    };

    private long? MainBranchId()
    {
        var snapshots = new SnapshotStore(_conn);
        return snapshots.GetRepositoryId(Repo) is long repo ? snapshots.GetBranchId(repo, "main") : null;
    }

    private long? PointerOfMain() =>
        MainBranchId() is long id ? new SnapshotStore(_conn).GetBranchSnapshotId(id) : null;

    private long? HeadSequenceOfMain() =>
        MainBranchId() is long id ? new SnapshotStore(_conn).GetBranchHeadSequence(id) : null;

    private string? StatusOf(long snapshotId) => new SnapshotStore(_conn).GetById(snapshotId)?.Status;

    private long JobCount(string identityHash)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM snapshot_jobs WHERE identity_hash = @h;";
        cmd.Parameters.AddWithValue("@h", identityHash);
        return (long)cmd.ExecuteScalar()!;
    }

    private (long rows, long? recordedAt) CoverageRow(long snapshotId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*), MAX(recorded_at) FROM snapshot_coverage WHERE snapshot_id = @id;";
        cmd.Parameters.AddWithValue("@id", snapshotId);
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return (reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt64(1));
    }

    // Removes a snapshot's semantic data while leaving its catalog row — an anomaly the intact predicate must
    // catch (retention itself deletes the row together with its data).
    private void DeleteSnapshotData(long snapshotId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM projects WHERE snapshot_id = @id;";
        cmd.Parameters.AddWithValue("@id", snapshotId);
        cmd.ExecuteNonQuery();
    }
}
