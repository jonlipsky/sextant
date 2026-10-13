using Sextant.Store;

namespace Sextant.Service.Tests;

[TestClass]
public class ScheduledRetentionContinuationTests
{
    [TestMethod]
    public void PassWithWorkLeftAndProgress_StaysDueForTheNextTick()
    {
        var report = new RetentionReport { DryRun = false, MoreRemaining = true, SnapshotProjectVersionsDeleted = 3 };
        Assert.IsTrue(SnapshotService.ScheduledRetentionContinues(report));
    }

    [TestMethod]
    public void FinishedPass_IsDoneForTheDay()
    {
        var report = new RetentionReport { DryRun = false, MoreRemaining = false, SnapshotsDeleted = 5 };
        Assert.IsFalse(SnapshotService.ScheduledRetentionContinues(report));
    }

    [TestMethod]
    public void PassThatDeletedOnlyTailRows_Continues()
    {
        Assert.IsTrue(SnapshotService.ScheduledRetentionContinues(
            new RetentionReport { DryRun = false, MoreRemaining = true, ApiSnapshotsDeleted = 256 }));
        Assert.IsTrue(SnapshotService.ScheduledRetentionContinues(
            new RetentionReport { DryRun = false, MoreRemaining = true, FileVersionsDeleted = 256 }));
        Assert.IsTrue(SnapshotService.ScheduledRetentionContinues(
            new RetentionReport { DryRun = false, MoreRemaining = true, SourceTextsDeleted = 10 }));
        Assert.IsTrue(SnapshotService.ScheduledRetentionContinues(new RetentionReport
        {
            DryRun = false, MoreRemaining = true, Deleted = [new RetentionGeneration { Id = 1, Status = "complete", Reason = "test" }]
        }));
    }

    [TestMethod]
    public void PassWithWorkLeftButNoProgress_IsNotRetriedEveryTick()
    {
        var report = new RetentionReport { DryRun = false, MoreRemaining = true, StopReason = "time_budget" };
        Assert.IsFalse(SnapshotService.ScheduledRetentionContinues(report));
    }
}
