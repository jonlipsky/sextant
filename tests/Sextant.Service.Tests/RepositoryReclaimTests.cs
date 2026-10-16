using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Store;
using Sextant.TestSupport;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #291: the maintenance loop's reclaim pass deletes the checkout, git object store and package folder of
/// repositories the service no longer indexes (no branch pointer, no pending work, no catalog activity for N days),
/// under the write gate, leaving every catalog row for retention. A later ensure simply re-clones cold.
/// </summary>
[TestClass]
public class RepositoryReclaimTests
{
    private const string Repo = "https://github.com/acme/stale";
    private const string OtherRepo = "https://github.com/acme/other";
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);

    private static MaintenancePolicy Policy() => new() { Interval = TimeSpan.Zero, RepositoryReclaimDays = 14 };

    [TestMethod]
    public async Task InactiveRepository_IsReclaimed_AndCatalogRowsStay()
    {
        await using var h = Harness.Create(Policy());
        var snapshot = h.PublishWithoutBranch(Repo, "c1");
        var dirs = h.Populate(Repo);
        h.Start();

        var report = await h.Service.ReclaimInactiveRepositoriesAsync();

        Assert.IsTrue(report.Enabled);
        Assert.AreEqual(1, report.Reclaimed);
        Assert.AreEqual(0, report.Failed);
        Assert.IsTrue(report.BytesFreed >= dirs.TotalBytes, $"freed {report.BytesFreed}, wrote {dirs.TotalBytes}");
        Assert.IsFalse(Directory.Exists(dirs.Checkout));
        Assert.IsFalse(Directory.Exists(dirs.ObjectStore));
        Assert.IsFalse(Directory.Exists(dirs.Packages));
        Assert.IsFalse(File.Exists(dirs.Packages + ".usage"));
        Assert.AreEqual(0, h.Leftovers().Count, "no renamed-aside directory is left behind");
        Assert.IsNotNull(new SnapshotStore(h.Db.GetConnection()).GetById(snapshot), "the snapshot row stays");
        Assert.IsNotNull(new SnapshotStore(h.Db.GetConnection()).GetRepositoryId(Repo), "the repository row stays");
    }

    [TestMethod]
    public async Task RepositoryWithABranchPointer_IsKept()
    {
        await using var h = Harness.Create(Policy());
        h.PublishWithBranch(Repo, "c1", "main");
        var dirs = h.Populate(Repo);
        h.Start();

        var report = await h.Service.ReclaimInactiveRepositoriesAsync();

        Assert.AreEqual(0, report.Inactive);
        Assert.AreEqual(0, report.Reclaimed);
        Assert.IsTrue(Directory.Exists(dirs.Checkout) && Directory.Exists(dirs.ObjectStore) && Directory.Exists(dirs.Packages));
    }

    [TestMethod]
    public async Task RepositoryWithAQueuedJob_AJobRunning_OrAPendingHandover_IsKept()
    {
        await using var h = Harness.Create(Policy());
        var urls = new[] { "https://github.com/acme/queued", "https://github.com/acme/running", "https://github.com/acme/handover" };
        var dirs = new List<Dirs>();
        foreach (var url in urls)
        {
            h.PublishWithoutBranch(url, "c1");
            dirs.Add(h.Populate(url));
        }
        h.Start();
        // Admitted by this run (after startup settled earlier runs' orphans), so they have a live turn.
        var jobs = new SnapshotJobStore(h.Db.GetConnection());
        jobs.EnsureJob("id-queued", urls[0] + ".git", "c2", null);
        jobs.EnsureJob("id-running", urls[1], "c2", null);
        h.Execute("UPDATE snapshot_jobs SET status = 'running' WHERE identity_hash = 'id-running';");
        jobs.RecordHandover(RemoteUrlIdentity.Normalize(urls[2]), "feature", "c-x", "c-h", "c-y");

        var report = await h.Service.ReclaimInactiveRepositoriesAsync();

        Assert.AreEqual(0, report.Reclaimed);
        foreach (var d in dirs)
            Assert.IsTrue(Directory.Exists(d.Checkout), "pending work keeps the repository");
    }

    [TestMethod]
    public async Task ActivityWithinTheThreshold_KeepsTheRepository_AndOlderActivityDoesNot()
    {
        await using var h = Harness.Create(Policy());
        h.PublishWithoutBranch(Repo, "c1");
        var dirs = h.Populate(Repo);
        h.Start();

        h.SetActivity(Repo, h.Clock.GetUtcNow().AddDays(-13));
        var kept = await h.Service.ReclaimInactiveRepositoriesAsync();
        Assert.AreEqual(0, kept.Reclaimed, "13 days of inactivity is below the 14 day threshold");
        Assert.IsTrue(Directory.Exists(dirs.Checkout));

        h.SetActivity(Repo, h.Clock.GetUtcNow().AddDays(-15));
        var reclaimed = await h.Service.ReclaimInactiveRepositoriesAsync();
        Assert.AreEqual(1, reclaimed.Reclaimed);
        Assert.IsFalse(Directory.Exists(dirs.Checkout));
    }

    [TestMethod]
    public async Task ARepositoryWhoseOnlyRowsWereSettledAtStartup_KeepsItsOriginalActivityTime()
    {
        await using var h = Harness.Create(Policy());
        h.PublishWithoutBranch(Repo, "c1");
        var dirs = h.Populate(Repo);
        h.SetActivity(Repo, h.Clock.GetUtcNow().AddDays(-60));
        new SnapshotJobStore(h.Db.GetConnection()).EnsureJob("id-orphan", Repo, "c2", null);
        var original = h.Clock.GetUtcNow().AddDays(-40).ToUnixTimeMilliseconds();
        h.Execute($"UPDATE snapshot_jobs SET created_at = {original}, updated_at = {original};");

        h.Start();

        Assert.AreEqual("cancelled", new SnapshotJobStore(h.Db.GetConnection()).GetJobByIdentity("id-orphan")!.Status);
        Assert.AreEqual(original, new RepositoryActivityStore(h.Db.GetConnection()).List().Single().LastActivityMs,
            "settling stamps no timestamp, so the repository is not freshly active");
        Assert.AreEqual(1, (await h.Service.ReclaimInactiveRepositoriesAsync()).Reclaimed,
            "reclaimable after its real inactivity");
        Assert.IsFalse(Directory.Exists(dirs.Checkout));
    }

    [TestMethod]
    public async Task ARecentJobOrRetiredHead_CountsAsActivity()
    {
        await using var h = Harness.Create(Policy());
        h.PublishWithoutBranch(Repo, "c1");
        var dirs = h.Populate(Repo);
        h.SetActivity(Repo, h.Clock.GetUtcNow().AddDays(-60));
        new SnapshotJobStore(h.Db.GetConnection()).EnsureJob("id-done", Repo, "c1", null);
        var recent = h.Clock.GetUtcNow().AddDays(-2).ToUnixTimeMilliseconds();
        h.Execute($"UPDATE snapshot_jobs SET status = 'complete', snapshot_id = (SELECT MIN(id) FROM snapshots), created_at = 1, updated_at = 1, completed_at = {recent};");
        h.Start();

        Assert.AreEqual(0, (await h.Service.ReclaimInactiveRepositoriesAsync()).Reclaimed);
        Assert.IsTrue(Directory.Exists(dirs.Checkout));

        h.Execute("UPDATE snapshot_jobs SET completed_at = 1;");
        var store = new SnapshotStore(h.Db.GetConnection());
        store.RecordRetiredHead(store.GetRepositoryId(Repo)!.Value, "feature", "abc", recent, SnapshotStore.RetiredHeadCauseIdle);
        Assert.AreEqual(0, (await h.Service.ReclaimInactiveRepositoriesAsync()).Reclaimed, "a just-retired branch waits the full period");
        Assert.IsTrue(Directory.Exists(dirs.Checkout));
    }

    [TestMethod]
    public async Task ZeroDays_DisablesTheRule()
    {
        await using var h = Harness.Create(Policy() with { RepositoryReclaimDays = 0 });
        h.PublishWithoutBranch(Repo, "c1");
        var dirs = h.Populate(Repo);
        h.Start();

        var report = await h.Service.ReclaimInactiveRepositoriesAsync();

        Assert.IsFalse(report.Enabled);
        Assert.IsTrue(Directory.Exists(dirs.Checkout));
    }

    [TestMethod]
    public async Task PerPassCap_ReclaimsTheLongestInactiveFirst_AndTheRestNextPass()
    {
        await using var h = Harness.Create(Policy() with { RepositoryReclaimMaxPerPass = 2 });
        var dirs = new List<Dirs>();
        for (var i = 1; i <= 4; i++)
        {
            var url = $"https://github.com/acme/r{i}";
            h.PublishWithoutBranch(url, "c1");
            dirs.Add(h.Populate(url));
            h.SetActivity(url, h.Clock.GetUtcNow().AddDays(-(20 + i)));
        }
        h.Start();

        var first = await h.Service.ReclaimInactiveRepositoriesAsync();

        Assert.AreEqual(4, first.Inactive);
        Assert.AreEqual(2, first.Reclaimed);
        Assert.IsFalse(Directory.Exists(dirs[3].Checkout));
        Assert.IsFalse(Directory.Exists(dirs[2].Checkout));
        Assert.IsTrue(Directory.Exists(dirs[1].Checkout));

        var second = await h.Service.ReclaimInactiveRepositoriesAsync();
        Assert.AreEqual(2, second.Reclaimed);
        Assert.IsFalse(Directory.Exists(dirs[0].Checkout));
    }

    [TestMethod]
    public async Task ARepositoryWithNothingOnDisk_IsNotCountedOrAudited()
    {
        await using var h = Harness.Create(Policy());
        h.PublishWithoutBranch(Repo, "c1");
        h.Start();

        var report = await h.Service.ReclaimInactiveRepositoriesAsync();

        Assert.AreEqual(1, report.Inactive);
        Assert.AreEqual(0, report.Reclaimed);
        Assert.AreEqual(0, h.Service.RecentAudit(action: AuditAction.Reclaim).Count);
    }

    [TestMethod]
    public async Task ASymlinkedDirectory_IsNeverFollowedOrDeleted()
    {
        await using var h = Harness.Create(Policy());
        h.PublishWithoutBranch(Repo, "c1");
        var dirs = h.Populate(Repo);
        var outside = Path.Combine(h.DataRoot, "outside-volume");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "precious.txt"), "keep");
        Directory.Delete(dirs.Checkout, recursive: true);
        try
        {
            Directory.CreateSymbolicLink(dirs.Checkout, outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Inconclusive("Symbolic links are unavailable here.");
        }
        h.Start();

        var report = await h.Service.ReclaimInactiveRepositoriesAsync();

        Assert.IsTrue(File.Exists(Path.Combine(outside, "precious.txt")), "a link target outside the volume is untouched");
        Assert.IsNotNull(new DirectoryInfo(dirs.Checkout).LinkTarget, "the link itself is left in place");
        Assert.IsFalse(Directory.Exists(dirs.ObjectStore), "the other, real directories are still reclaimed");
        Assert.AreEqual(1, report.Reclaimed);
    }

    [TestMethod]
    [DataRow("object store")]
    [DataRow("package folder")]
    public async Task ASymlinkedStoreOrPackageFolder_IsNeverFollowedOrDeleted(string kind)
    {
        await using var h = Harness.Create(Policy());
        h.PublishWithoutBranch(Repo, "c1");
        var dirs = h.Populate(Repo);
        var outside = Path.Combine(h.DataRoot, "outside-volume");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "precious.txt"), "keep");
        var linked = kind == "object store" ? dirs.ObjectStore : dirs.Packages;
        Directory.Delete(linked, recursive: true);
        try
        {
            Directory.CreateSymbolicLink(linked, outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Inconclusive("Symbolic links are unavailable here.");
        }
        h.Start();

        await h.Service.ReclaimInactiveRepositoriesAsync();

        Assert.IsTrue(File.Exists(Path.Combine(outside, "precious.txt")));
        Assert.IsNotNull(new DirectoryInfo(linked).LinkTarget, "the link itself is left in place");
        Assert.IsFalse(Directory.Exists(dirs.Checkout), "the real checkout is still reclaimed");
    }

    [TestMethod]
    public async Task ABranchOrPendingJobAppearingBeforeTheWriterTurn_KeepsTheRepository()
    {
        foreach (var how in new[] { "branch", "job" })
        {
            await using var h = Harness.Create(Policy());
            h.PublishWithoutBranch(Repo, "c1");
            var dirs = h.Populate(Repo);
            h.Start();
            h.Service.BeforeReclaimWriterTurn = () =>
            {
                if (how == "branch")
                {
                    var store = new SnapshotStore(h.Db.GetConnection());
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    store.EnsureBranch(store.EnsureRepository(Repo, now), "main", true, now);
                }
                else
                {
                    new SnapshotJobStore(h.Db.GetConnection()).EnsureJob("id-late", Repo, "c9", null);
                }
                return Task.CompletedTask;
            };

            var report = await h.Service.ReclaimInactiveRepositoriesAsync();

            Assert.AreEqual(1, report.Inactive, how);
            Assert.AreEqual(1, report.Skipped, how);
            Assert.AreEqual(0, report.Reclaimed, how);
            Assert.IsTrue(Directory.Exists(dirs.Checkout) && Directory.Exists(dirs.ObjectStore) && Directory.Exists(dirs.Packages), how);
        }
    }

    [TestMethod]
    public async Task AnInactiveRepository_SharingItsDirectoryWithAnActiveOne_IsUntouched()
    {
        await using var h = Harness.Create(Policy());
        var alias = Repo + ".git";
        Assert.AreEqual(ServicePaths.RepoDirectoryName(Repo), ServicePaths.RepoDirectoryName(alias));
        h.PublishWithoutBranch(Repo, "c1");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        h.Execute($"INSERT INTO repositories (remote_url, created_at) VALUES ('{alias}', {now});");
        h.SetActivity(Repo, h.Clock.GetUtcNow().AddDays(-90));
        h.Execute($"UPDATE repositories SET created_at = {h.Clock.GetUtcNow().ToUnixTimeMilliseconds()} WHERE remote_url = '{alias}';");
        var dirs = h.Populate(Repo);
        h.Start();

        var report = await h.Service.ReclaimInactiveRepositoriesAsync();

        Assert.AreEqual(0, report.Reclaimed);
        Assert.IsTrue(Directory.Exists(dirs.Checkout) && Directory.Exists(dirs.ObjectStore) && Directory.Exists(dirs.Packages));
    }

    [TestMethod]
    public async Task TheTick_RecordsNoReclaimMetrics_WhenDisabled_OrIndexingHoldsTheWriter()
    {
        await using (var h = Harness.Create(Policy() with { RepositoryReclaimDays = 0 }))
        {
            h.Start();
            await h.Service.RunMaintenanceTickAsync();
            Assert.AreEqual(0, h.Service.CollectMetrics().Maintenance.LastReclaimPassAtUnixMs);
        }
        await using (var h = Harness.Create(Policy(), gatedWorker: true))
        {
            h.Start();
            var blocker = h.Service.EnsureSnapshotAsync(new EnsureSnapshotRequest { RepositoryRemoteUrl = OtherRepo, CommitSha = "b-1" });
            await WaitUntilAsync(() => h.Worker.Calls == 1);
            var tick = await h.Service.RunMaintenanceTickAsync();
            Assert.IsTrue(tick.RepositoryReclaim!.Busy);
            Assert.AreEqual(0, h.Service.CollectMetrics().Maintenance.LastReclaimPassAtUnixMs);
            h.Worker.Gate.TrySetResult();
            await blocker;
        }
    }

    [TestMethod]
    public void ChildOf_AcceptsOnlyOnePlainSegmentUnderTheRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "volume-root");
        Assert.AreEqual(Path.Combine(root, "repo-abc123"), RepositoryReclaimer.ChildOf(root, "repo-abc123"));
        foreach (var bad in new[] { "", ".", "..", ".hidden", "a/b", "../outside", "/abs" })
            Assert.IsNull(RepositoryReclaimer.ChildOf(root, bad), $"'{bad}' is refused");
    }

    [TestMethod]
    public async Task AnEnsureAfterReclaim_ReclonesCold_AndCatalogStaysUsable()
    {
        await using var h = Harness.Create(Policy());
        h.PublishWithoutBranch(Repo, "c1");
        var dirs = h.Populate(Repo);
        var sawCheckout = new List<bool>();
        h.Start(behavior: (worker, request) =>
        {
            var checkout = Path.Combine(h.Service.Paths.CheckoutRoot, ServicePaths.RepoDirectoryName(request.RepositoryRemoteUrl));
            sawCheckout.Add(Directory.Exists(checkout));
            Directory.CreateDirectory(checkout);
            return SnapshotWorkResult.Complete(ServiceTestFixtures.PublishComplete(worker.Database, request));
        });

        Assert.AreEqual(1, (await h.Service.ReclaimInactiveRepositoriesAsync()).Reclaimed);
        var result = await h.Service.EnsureSnapshotAsync(
            new EnsureSnapshotRequest { RepositoryRemoteUrl = Repo, CommitSha = "c2", BranchName = "main" });

        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status);
        CollectionAssert.AreEqual(new[] { false }, sawCheckout, "the worker found no checkout and cloned cold");
        Assert.IsTrue(Directory.Exists(dirs.Checkout));
    }

    [TestMethod]
    public async Task ReclaimWaitsForNothing_WhenIndexingHoldsTheWriter()
    {
        await using var h = Harness.Create(Policy(), gatedWorker: true);
        h.PublishWithoutBranch(Repo, "c1");
        var dirs = h.Populate(Repo);
        h.Start();
        var blocker = h.Service.EnsureSnapshotAsync(new EnsureSnapshotRequest { RepositoryRemoteUrl = OtherRepo, CommitSha = "b-1" });
        await WaitUntilAsync(() => h.Worker.Calls == 1);

        var report = await h.Service.ReclaimInactiveRepositoriesAsync();

        Assert.IsTrue(report.Busy);
        Assert.IsTrue(Directory.Exists(dirs.Checkout));
        h.Worker.Gate.TrySetResult();
        await blocker;
    }

    [TestMethod]
    public async Task TheTick_RecordsMetrics_AndAuditsEachReclaimedRepository()
    {
        await using var h = Harness.Create(Policy());
        h.PublishWithoutBranch(Repo, "c1");
        var dirs = h.Populate(Repo);
        h.Start();

        var tick = await h.Service.RunMaintenanceTickAsync();

        Assert.AreEqual(1, tick.RepositoryReclaim!.Reclaimed);
        var metrics = h.Service.CollectMetrics().Maintenance;
        Assert.AreEqual(1, metrics.RepositoriesReclaimedTotal);
        Assert.AreEqual(1, metrics.LastRepositoriesReclaimed);
        Assert.AreEqual(tick.RepositoryReclaim.BytesFreed, metrics.RepositoryBytesReclaimedTotal);
        Assert.IsTrue(metrics.RepositoryBytesReclaimedTotal >= dirs.TotalBytes);
        StringAssert.Contains(
            Sextant.Service.Observability.PrometheusExposition.Render(h.Service.CollectMetrics()),
            "sextant_repositories_reclaimed_total 1");

        var audit = h.Service.RecentAudit(action: AuditAction.Reclaim);
        Assert.AreEqual(1, audit.Count);
        Assert.AreEqual(AuditOutcome.Complete, audit[0].Outcome);
        Assert.AreEqual(Repo, audit[0].RepositoryScope);
        Assert.AreEqual(AuditLogStore.HashActor("reclaim_policy"), audit[0].Actor);
        Assert.AreEqual(tick.RepositoryReclaim.BytesFreed, audit[0].CostBytes);
        StringAssert.StartsWith(audit[0].Detail, "reclaimed;checkout,git_object_store,package_folder;source=reclaim_policy");
    }

    [TestMethod]
    public async Task AFailedDelete_IsLogged_AndDoesNotStopTheOtherRepositories()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
            Assert.Inconclusive("Needs a read-only directory that the current user cannot delete.");
        await using var h = Harness.Create(Policy());
        h.PublishWithoutBranch(Repo, "c1");
        h.PublishWithoutBranch(OtherRepo, "c1");
        var stuck = h.Populate(Repo);
        var fine = h.Populate(OtherRepo);
        h.SetActivity(Repo, h.Clock.GetUtcNow().AddDays(-90));
        var locked = Path.Combine(stuck.Checkout, "locked");
        Directory.CreateDirectory(locked);
        File.WriteAllText(Path.Combine(locked, "f.txt"), "x");
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        h.Start();
        try
        {
            var report = await h.Service.ReclaimInactiveRepositoriesAsync();

            Assert.AreEqual(1, report.Failed);
            Assert.AreEqual(1, report.Reclaimed, "only the repository whose delete succeeded counts as reclaimed");
            Assert.IsTrue(report.BytesFreed >= fine.TotalBytes && report.BytesFreed < fine.TotalBytes + stuck.TotalBytes,
                "the stuck checkout's bytes are not counted as freed");
            var audit = h.Service.RecentAudit(action: AuditAction.Reclaim);
            Assert.AreEqual(1, audit.Count(a => a.Outcome == AuditOutcome.Failed), "the incomplete reclaim is audited as failed");
            Assert.IsFalse(Directory.Exists(fine.Checkout));
            Assert.IsFalse(Directory.Exists(stuck.Checkout));
        }
        finally
        {
            foreach (var left in h.Leftovers())
            {
                var lockedLeft = Path.Combine(left, "locked");
                if (Directory.Exists(lockedLeft))
                    File.SetUnixFileMode(lockedLeft, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        // The next pass sweeps the leftover once it can be deleted.
        await h.Service.ReclaimInactiveRepositoriesAsync();
        Assert.AreEqual(0, h.Leftovers().Count);
    }

    [TestMethod]
    public void ParseMaintenance_ReclaimSettings_DefaultClampAndDisable()
    {
        var defaults = ServiceOptions.ParseMaintenance(null, null, null, null, null);
        Assert.AreEqual(14, defaults.RepositoryReclaimDays);
        Assert.AreEqual(5, defaults.RepositoryReclaimMaxPerPass);
        Assert.AreEqual(0, ServiceOptions.ParseMaintenance(null, null, null, null, null, null, "0").RepositoryReclaimDays);
        Assert.AreEqual(14, ServiceOptions.ParseMaintenance(null, null, null, null, null, null, "-1", "0").RepositoryReclaimDays);
        Assert.AreEqual(5, ServiceOptions.ParseMaintenance(null, null, null, null, null, null, "x", "0").RepositoryReclaimMaxPerPass);
        Assert.AreEqual(MaintenancePolicy.MaxRepositoryReclaimDays,
            ServiceOptions.ParseMaintenance(null, null, null, null, null, null, "999999999").RepositoryReclaimDays);
        Assert.AreEqual(MaintenancePolicy.MaxRepositoryReclaimMaxPerPass,
            ServiceOptions.ParseMaintenance(null, null, null, null, null, null, null, "999999").RepositoryReclaimMaxPerPass);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Settle;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("The condition did not become true in time.");
            await Task.Delay(10);
        }
    }

    private sealed record Dirs(string Checkout, string ObjectStore, string Packages, long TotalBytes);

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _dbPath = ServiceTestFixtures.NewDbPath();
        private ServiceOptions _options = null!;
        private bool _gatedWorker;

        public string DataRoot { get; private set; } = null!;
        public IndexDatabase Db { get; private set; } = null!;
        public ManualClock Clock { get; private set; } = null!;
        public FakeSnapshotWorker Worker { get; private set; } = null!;
        public SnapshotService Service { get; private set; } = null!;

        public static Harness Create(MaintenancePolicy maintenance, bool gatedWorker = false)
        {
            var h = new Harness { _gatedWorker = gatedWorker };
            h.Db = new IndexDatabase(h._dbPath);
            h.Db.RunMigrations();
            // Rows the fixtures stamp with the real clock are then far older than any threshold the tests use.
            h.Clock = new ManualClock(DateTimeOffset.UtcNow.AddDays(120));
            h.DataRoot = ServiceTestFixtures.NewDataRoot();
            h._options = ServiceTestFixtures.NewOptions(h._dbPath, h.DataRoot) with { Maintenance = maintenance };
            return h;
        }

        public void Start(Func<FakeSnapshotWorker, EnsureSnapshotRequest, SnapshotWorkResult>? behavior = null)
        {
            Worker = new FakeSnapshotWorker(Db, behavior) { UseGate = _gatedWorker };
            Service = SnapshotService.Start(_options, Worker, Db, timeProvider: Clock);
        }

        public long PublishWithoutBranch(string url, string commit) => ServiceTestFixtures.PublishComplete(
            Db, new EnsureSnapshotRequest { RepositoryRemoteUrl = url, CommitSha = commit }, recordCommit: true);

        public void PublishWithBranch(string url, string commit, string branch)
        {
            var snapshot = PublishWithoutBranch(url, commit);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var store = new SnapshotStore(Db.GetConnection());
            var branchId = store.EnsureBranch(store.EnsureRepository(url, now), branch, true, now);
            store.SetBranchPointer(branchId, snapshot, now);
        }

        // Writes a checkout (with a submodule), an object store (with a submodule store) and a package folder + record.
        public Dirs Populate(string url)
        {
            var paths = new ServicePaths(_options.Volumes);
            var name = ServicePaths.RepoDirectoryName(url);
            var checkout = Path.Combine(paths.CheckoutRoot, name);
            var store = Path.Combine(paths.GitObjectStoreRoot, name);
            var packages = Path.Combine(paths.PackageCacheRoot, name);
            long total = 0;
            foreach (var file in new[]
            {
                Path.Combine(checkout, "src", "App.cs"), Path.Combine(checkout, "external", "lib", "Lib.cs"),
                Path.Combine(store, "repository.git", "objects", "pack", "p.pack"),
                Path.Combine(store, "submodules", "lib-abc.git", "HEAD"), Path.Combine(packages, "pkg", "1.0.0", "pkg.dll")
            })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, new string('x', 100));
                total += 100;
            }
            File.WriteAllText(packages + ".usage", "{}");
            return new Dirs(checkout, store, packages, total);
        }

        // Makes the repository's latest catalog activity exactly `at` (all its rows are stamped to it).
        public void SetActivity(string url, DateTimeOffset at)
        {
            var ms = at.ToUnixTimeMilliseconds();
            var key = url.Replace("'", "''");
            Execute($"""
                UPDATE repositories SET created_at = {ms} WHERE remote_url = '{key}';
                UPDATE snapshots SET created_at = {ms}, published_at = {ms}
                  WHERE repository_id IN (SELECT id FROM repositories WHERE remote_url = '{key}');
                """);
        }

        public bool BranchExists(string name)
        {
            using var conn = Open();
            var store = new SnapshotStore(conn);
            return store.GetRepositoryId(Repo) is long repoId && store.GetBranch(repoId, name) is not null;
        }

        public List<string> Leftovers()
        {
            var paths = new ServicePaths(_options.Volumes);
            var found = new List<string>();
            foreach (var root in new[] { paths.CheckoutRoot, paths.GitObjectStoreRoot, paths.PackageCacheRoot })
                if (Directory.Exists(root))
                    found.AddRange(Directory.EnumerateDirectories(root, RepositoryReclaimer.AsidePrefix + "*"));
            return found;
        }

        public void Execute(string sql)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        private SqliteConnection Open()
        {
            var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath, Pooling = false }.ToString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA busy_timeout = 5000;";
            cmd.ExecuteNonQuery();
            return conn;
        }

        public ValueTask DisposeAsync()
        {
            Worker?.Gate.TrySetResult();
            Service?.Dispose();
            SqliteTestDatabase.Delete(_dbPath, Db);
            try { Directory.Delete(DataRoot, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp */ }
            return ValueTask.CompletedTask;
        }
    }
}
