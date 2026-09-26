using Sextant.Core;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #119 — the service never reports a snapshot COMPLETE when its durable coverage record says the
/// snapshot covers only part of the checkout: not when attaching to an already-published snapshot, not when
/// a worker claims Complete, and not on status reads. The coverage block travels with every answer.
/// </summary>
[TestClass]
public class SnapshotServiceCoverageTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SnapshotService? _service;

    [TestInitialize]
    public void Init()
    {
        _dbPath = ServiceTestFixtures.NewDbPath();
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _service?.Dispose();
        SqliteTestDatabase.Delete(_dbPath, _db);
    }

    private static SnapshotCoverage PartialCoverage() => new()
    {
        Verdict = SnapshotCoverageVerdict.Partial,
        Reasons = ["1 of 2 declared submodule(s) are not populated in the checkout (libs/shared); their projects are not indexed."],
        SelectionSource = "default_root",
        SubmodulesDeclared = 2,
        SubmodulesUnpopulated = 1
    };

    private long PublishWith(EnsureSnapshotRequest request, SnapshotCoverage? coverage)
    {
        var snapId = ServiceTestFixtures.PublishComplete(_db, request);
        if (coverage is not null)
            new SnapshotCoverageStore(_db.GetConnection()).Record(snapId, coverage, 1);
        return snapId;
    }

    private SnapshotService Start(FakeSnapshotWorker worker)
    {
        _service = SnapshotService.Start(ServiceTestFixtures.NewOptions(_dbPath), worker, _db);
        return _service;
    }

    [TestMethod]
    public async Task AttachToPublishedSnapshot_WithPartialCoverage_IsPartialWithReason()
    {
        var request = ServiceTestFixtures.Request();
        var snapId = PublishWith(request, PartialCoverage());
        var service = Start(new FakeSnapshotWorker(_db, FakeSnapshotWorker.Throws("worker must not run")));

        var result = await service.EnsureSnapshotAsync(request);

        Assert.AreEqual(SnapshotJobStatus.Partial, result.Status,
            "attaching to a published snapshot takes the verdict from its durable coverage, never blindly Complete");
        Assert.AreEqual(snapId, result.SnapshotId, "the partial snapshot is still served");
        StringAssert.Contains(result.Reason, "snapshot coverage is partial");
        StringAssert.Contains(result.Reason, "libs/shared");
        Assert.AreEqual(SnapshotCoverageVerdict.Partial, result.Coverage!.Verdict);

        var status = service.GetStatus(result.JobId)!;
        Assert.AreEqual(SnapshotJobStatus.Partial, status.Job.Status, "the durable job row records Partial");
        Assert.AreEqual(1, status.Coverage!.SubmodulesUnpopulated);

        var again = await service.EnsureSnapshotAsync(request);
        Assert.AreEqual(SnapshotJobStatus.Partial, again.Status, "the terminal fast path agrees");
        Assert.IsNotNull(again.Coverage);
    }

    [TestMethod]
    public async Task AttachToPublishedSnapshot_WithoutRecordedCoverage_StaysComplete()
    {
        var request = ServiceTestFixtures.Request();
        PublishWith(request, coverage: null);
        var service = Start(new FakeSnapshotWorker(_db, FakeSnapshotWorker.Throws("worker must not run")));

        var result = await service.EnsureSnapshotAsync(request);

        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status);
        Assert.IsNull(result.Reason);
        Assert.IsNull(result.Coverage, "coverage not recorded ⇒ absent, never invented");
    }

    [TestMethod]
    public async Task WorkerClaimsComplete_OverPartialDurableCoverage_IsDowngradedToPartial()
    {
        var request = ServiceTestFixtures.Request();
        var worker = new FakeSnapshotWorker(_db, (self, r) =>
        {
            var id = ServiceTestFixtures.PublishComplete(self.Database, r);
            new SnapshotCoverageStore(self.Database.GetConnection()).Record(id, PartialCoverage(), 1);
            return SnapshotWorkResult.Complete(id);
        });
        var service = Start(worker);

        var result = await service.EnsureSnapshotAsync(request);

        Assert.AreEqual(1, worker.Calls);
        Assert.AreEqual(SnapshotJobStatus.Partial, result.Status,
            "the job verdict never contradicts the snapshot's durable coverage record");
        StringAssert.Contains(service.GetStatus(result.JobId)!.Job.LastError, "snapshot coverage is partial");
        Assert.AreEqual(SnapshotCoverageVerdict.Partial, service.GetCoverage(result.SnapshotId!.Value)!.Verdict);
    }

    [TestMethod]
    public async Task WorkerReportsCompleteCoverage_IsCompleteWithCoverageBlock()
    {
        var request = ServiceTestFixtures.Request();
        var worker = new FakeSnapshotWorker(_db, (self, r) =>
        {
            var id = ServiceTestFixtures.PublishComplete(self.Database, r);
            new SnapshotCoverageStore(self.Database.GetConnection())
                .Record(id, new SnapshotCoverage { Verdict = SnapshotCoverageVerdict.Complete }, 1);
            return SnapshotWorkResult.Complete(id);
        });
        var service = Start(worker);

        var result = await service.EnsureSnapshotAsync(request);

        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status);
        Assert.IsNull(result.Reason);
        Assert.AreEqual(SnapshotCoverageVerdict.Complete, result.Coverage!.Verdict);
        Assert.AreEqual(SnapshotCoverageVerdict.Complete, service.GetStatusByIdentity(result.IdentityHash)!.Coverage!.Verdict);
    }
}
