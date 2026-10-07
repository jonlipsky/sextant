using Sextant.Core;
using Sextant.Store;

namespace Sextant.Service.Tests;

[TestClass]
public sealed class SnapshotRebuildTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task PublishedRestoreDegradation_OrdinaryEnsureAttaches_RebuildCreatesNewGeneration(bool partial)
    {
        var path = ServiceTestFixtures.NewDbPath();
        using var db = new IndexDatabase(path);
        db.RunMigrations();
        try
        {
            var request = ServiceTestFixtures.Request(branch: "main");
            var oldId = ServiceTestFixtures.PublishComplete(db, request, recordCommit: true);
            var coverage = new SnapshotCoverage
            {
                Verdict = partial ? SnapshotCoverageVerdict.Partial : SnapshotCoverageVerdict.Complete,
                Reasons = partial ? ["binding degraded after restore timeout"] : [],
                Notes = ["Package restore timed out."]
            };
            new SnapshotCoverageStore(db.GetConnection()).Record(oldId, coverage, 1);
            var worker = new FakeSnapshotWorker(db, (self, r) =>
            {
                var id = ServiceTestFixtures.PublishComplete(self.Database, r, recordCommit: true);
                new SnapshotCoverageStore(db.GetConnection()).Record(id, new SnapshotCoverage
                {
                    Verdict = SnapshotCoverageVerdict.Complete,
                    Rebuild = new SnapshotRebuild
                    {
                        Generation = r.RebuildGeneration!,
                        OriginalIdentityHash = (r with { RebuildGeneration = null }).ToIdentity().Hash
                    }
                }, 2);
                var snapshots = new SnapshotStore(db.GetConnection());
                var repoId = snapshots.GetRepositoryId(r.RepositoryRemoteUrl)!.Value;
                var branchId = snapshots.GetBranchId(repoId, r.BranchName!)!.Value;
                snapshots.AdvanceBranchPointerIfHeadMatches(branchId, id, r.ExpectedHeadCommit!, 2,
                    preservePrevious: true);
                return SnapshotWorkResult.Complete(id);
            });
            using var service = SnapshotService.Start(ServiceTestFixtures.NewOptions(path), worker, db);
            var ordinary = await service.EnsureSnapshotAsync(request);
            var again = await service.EnsureSnapshotAsync(request);
            Assert.AreEqual(oldId, again.SnapshotId);
            Assert.AreEqual(ordinary.JobId, again.JobId);
            Assert.AreEqual(0, worker.Calls, "restore improvement alone must not mutate a published identity");

            // A second branch must stay on the original snapshot.
            await service.EnsureSnapshotAsync(request with { BranchName = "release" });
            var rebuild = request with { RebuildGeneration = "restore-fixed-1", ExpectedHeadCommit = request.CommitSha };
            var rebuilt = await service.EnsureSnapshotAsync(rebuild);
            Assert.AreEqual(SnapshotJobStatus.Complete, rebuilt.Status);
            Assert.AreNotEqual(oldId, rebuilt.SnapshotId);
            Assert.AreNotEqual(ordinary.IdentityHash, rebuilt.IdentityHash);
            Assert.IsTrue(rebuilt.BranchAdvanced);
            Assert.AreEqual(rebuilt.SnapshotId, service.ResolveBranch(request.RepositoryRemoteUrl, "main")!.Id);
            Assert.AreEqual(oldId, service.ResolveBranch(request.RepositoryRemoteUrl, "release")!.Id);
            Assert.AreEqual(coverage.Verdict, service.GetCoverage(oldId)!.Verdict);
            CollectionAssert.AreEqual(coverage.Notes!.ToArray(), service.GetCoverage(oldId)!.Notes!.ToArray());
            Assert.AreEqual(SnapshotStatus.Complete, new SnapshotStore(db.GetConnection()).GetById(oldId)!.Status);

            var repeated = await service.EnsureSnapshotAsync(rebuild);
            Assert.AreEqual(rebuilt.JobId, repeated.JobId);
            Assert.AreEqual(rebuilt.SnapshotId, repeated.SnapshotId);
            Assert.AreEqual(1, worker.Calls);
            var staleOriginal = await service.EnsureSnapshotAsync(request with { ExpectedHeadCommit = request.CommitSha });
            Assert.IsFalse(staleOriginal.BranchAdvanced);
            Assert.AreEqual(rebuilt.SnapshotId, service.ResolveBranch(request.RepositoryRemoteUrl, "main")!.Id);
            await service.EnsureSnapshotAsync(request);
            Assert.AreEqual(rebuilt.SnapshotId, service.ResolveBranch(request.RepositoryRemoteUrl, "main")!.Id);
            var sequencedOriginal = await service.EnsureSnapshotAsync(request with { BranchHeadSequence = 20 });
            Assert.IsFalse(sequencedOriginal.BranchAdvanced);
            Assert.AreEqual(20L, service.ResolveBranchHead(request.RepositoryRemoteUrl, "main")!.HeadSequence);
            var otherCommit = request with { CommitSha = "newer-commit" };
            ServiceTestFixtures.PublishComplete(db, otherCommit, recordCommit: true);
            await service.EnsureSnapshotAsync(otherCommit with { BranchHeadSequence = 15 });
            Assert.AreEqual(rebuilt.SnapshotId, service.ResolveBranch(request.RepositoryRemoteUrl, "main")!.Id,
                "preserving the rebuild must still consume the newest sequence, so delayed events cannot regress history");
            Assert.AreEqual(rebuilt.IdentityHash,
                service.ResolveBranchHead(request.RepositoryRemoteUrl, "main")!.CurrentIdentityHash);
            Assert.IsTrue(service.RecentAudit(action: AuditAction.Ensure)
                .Any(a => a.Detail!.Contains(";rebuild=restore-fixed-1;identity=" + rebuilt.IdentityHash)));
        }
        finally
        {
            SqliteTestDatabase.Delete(path, db);
        }
    }

    [TestMethod]
    [DataRow("", "invalid_rebuild_generation")]
    [DataRow("bad;token", "invalid_rebuild_generation")]
    [DataRow("non-ascii-\u00e9", "invalid_rebuild_generation")]
    public void MalformedGeneration_IsRejected(string token, string reason)
    {
        var request = ServiceTestFixtures.Request(branch: "main") with
        {
            RebuildGeneration = token, ExpectedHeadCommit = "commit-aaaa"
        };
        Assert.AreEqual(reason, request.BranchGuardProblem());
    }

    [TestMethod]
    public void RebuildAdvance_RequiresSameCommitCasAndNamedBranch()
    {
        var request = ServiceTestFixtures.Request(branch: "main") with { RebuildGeneration = "g1" };
        Assert.AreEqual("rebuild_head_guard_required", request.BranchGuardProblem());
        Assert.AreEqual("rebuild_head_guard_required",
            (request with { ExpectedHeadCommit = "different-commit" }).BranchGuardProblem());
        Assert.AreEqual("rebuild_head_guard_required",
            (request with { ExpectedHeadCommit = request.CommitSha, BranchName = null }).BranchGuardProblem());
        Assert.IsNull((request with { BranchUpdate = "none" }).BranchGuardProblem());
        Assert.IsNull((request with { ExpectedHeadCommit = request.CommitSha }).BranchGuardProblem());
        Assert.AreEqual("invalid_rebuild_generation",
            (request with { RebuildGeneration = new string('g', 65), BranchUpdate = "none" }).BranchGuardProblem());
    }

    [TestMethod]
    public async Task StaleRebuildCas_PublishesWithoutMovingAChangedBranch()
    {
        var path = ServiceTestFixtures.NewDbPath();
        using var db = new IndexDatabase(path);
        db.RunMigrations();
        try
        {
            var request = ServiceTestFixtures.Request(branch: "main");
            var current = request with { CommitSha = "another-commit" };
            var currentId = ServiceTestFixtures.PublishComplete(db, current, recordCommit: true);
            var snapshots = new SnapshotStore(db.GetConnection());
            var repoId = snapshots.GetRepositoryId(request.RepositoryRemoteUrl)!.Value;
            var branch = snapshots.EnsureBranch(repoId, "main", true, 1);
            snapshots.SetBranchPointer(branch, currentId, 1);
            var worker = new FakeSnapshotWorker(db, (self, r) =>
            {
                var id = ServiceTestFixtures.PublishComplete(self.Database, r, recordCommit: true);
                Assert.IsFalse(snapshots.AdvanceBranchPointerIfHeadMatches(branch, id, r.ExpectedHeadCommit!, 2,
                    preservePrevious: true));
                return SnapshotWorkResult.Complete(id);
            });
            using var service = SnapshotService.Start(ServiceTestFixtures.NewOptions(path), worker, db);
            var result = await service.EnsureSnapshotAsync(request with
            {
                RebuildGeneration = "g1", ExpectedHeadCommit = request.CommitSha
            });
            Assert.AreEqual(SnapshotJobStatus.Complete, result.Status);
            Assert.IsFalse(result.BranchAdvanced);
            Assert.AreEqual(currentId, snapshots.GetBranchSnapshotId(branch));
            Assert.AreEqual(SnapshotStatus.Complete, snapshots.GetById(currentId)!.Status);
        }
        finally
        {
            SqliteTestDatabase.Delete(path, db);
        }
    }

    [TestMethod]
    public async Task FailedRebuild_DoesNotMoveTheBranchOrAlterThePublishedSnapshot()
    {
        var path = ServiceTestFixtures.NewDbPath();
        using var db = new IndexDatabase(path);
        db.RunMigrations();
        try
        {
            var request = ServiceTestFixtures.Request(branch: "main");
            var oldId = ServiceTestFixtures.PublishComplete(db, request, recordCommit: true);
            using var service = SnapshotService.Start(ServiceTestFixtures.NewOptions(path),
                new FakeSnapshotWorker(db, FakeSnapshotWorker.FailedWithDiagnostics("restore still unavailable")), db);
            await service.EnsureSnapshotAsync(request);
            var failed = await service.EnsureSnapshotAsync(request with
            {
                RebuildGeneration = "retry-1", ExpectedHeadCommit = request.CommitSha
            });
            Assert.AreEqual(SnapshotJobStatus.Failed, failed.Status);
            Assert.AreEqual(oldId, service.ResolveBranch(request.RepositoryRemoteUrl, "main")!.Id);
            var page = await new LocalBaseSnapshotSource(db.GetConnection()).FetchSymbolsAsync(
                new SnapshotPageRequest { IdentityHash = request.ToIdentity().Hash }, CancellationToken.None);
            Assert.IsTrue(page.IsPublished);
            Assert.AreEqual(3, page.Symbols.Count);
        }
        finally
        {
            SqliteTestDatabase.Delete(path, db);
        }
    }
}
