using Microsoft.Data.Sqlite;
using Sextant.Store;

namespace Sextant.Store.Tests;

/// <summary>
/// Issue #320: a crashed writer must not block its successor for the whole lease TTL, but a live holder, a holder on
/// another host and a holder that cannot be proven dead must still block; a crash is visible as an unclean shutdown.
/// </summary>
[TestClass]
public class WriterLeaseCrashTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_lease_crash_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
    }

    [TestCleanup]
    public void TestCleanup() => SqliteTestDatabase.Delete(_dbPath, _db);

    private sealed class FakeInspector(ProcessIdentity current) : IProcessInspector
    {
        public Dictionary<int, ProcessProbe> Processes { get; } = [];
        public ProcessIdentity Current { get; } = current;
        public ProcessProbe Probe(int pid) => Processes.TryGetValue(pid, out var p) ? p : ProcessProbe.Absent;
    }

    private static ProcessIdentity Identity(
        string instance, int pid, long ticks, string host = "host-a", string boot = "boot-1",
        string? ns = "pid:[100]", string? container = "c1", string? machine = "machine-1", long? initTicks = 1000) => new()
    {
        InstanceId = instance, HostId = host, BootId = boot, PidNamespace = ns, ContainerId = container,
        Pid = pid, StartTicks = ticks, MachineId = machine, InitStartTicks = initTicks
    };

    // A holder that "crashes": it neither releases nor heartbeats, and its row stays live for the full TTL.
    // Abandon() stops the heartbeat and keeps the row; the recorded reason is then cleared, as a dead process never writes one.
    private WriterLease Crashed(ProcessIdentity identity)
    {
        var lease = WriterLease.TryAcquire(
            _dbPath, "dead-holder", TimeSpan.FromMinutes(5), autoHeartbeat: false, inspector: new FakeInspector(identity));
        Assert.IsNotNull(lease);
        lease.Abandon();
        lease.Dispose();
        Exec("UPDATE writer_run_state SET end_reason = NULL;");
        return lease;
    }

    private void Exec(string sql)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private WriterLease? Successor(FakeInspector inspector) =>
        WriterLease.TryAcquire(_dbPath, "successor", TimeSpan.FromMinutes(5), autoHeartbeat: false, inspector: inspector);

    private WriterRunStateInfo? RunState()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return WriterLease.GetRunState(conn);
    }

    [TestMethod]
    public void DeadSameHostHolder_PidGone_IsTakenOverImmediately()
    {
        Crashed(Identity("old", pid: 100, ticks: 500));

        var inspector = new FakeInspector(Identity("new", pid: 200, ticks: 900)); // pid 100 absent
        using var lease = Successor(inspector);

        Assert.IsNotNull(lease, "a provably dead same-host holder must not block its successor");
        StringAssert.Contains(lease.Acquisition.TakenOverReason, "no longer exists");
        Assert.AreEqual("dead-holder", lease.Acquisition.TakenOverHolder);
        Assert.AreEqual(1, RunState()!.StaleLeaseTakeoversTotal);
    }

    [TestMethod]
    public void LiveSameHostHolder_StillBlocks()
    {
        Crashed(Identity("old", pid: 100, ticks: 500));

        var inspector = new FakeInspector(Identity("new", pid: 200, ticks: 900));
        inspector.Processes[100] = ProcessProbe.Present(500);

        Assert.IsNull(Successor(inspector), "the holder's pid still runs with the recorded start time");
        Assert.AreEqual(0, RunState()!.StaleLeaseTakeoversTotal);
    }

    [TestMethod]
    public void ReusedPid_WithAnotherStartTime_IsTakenOver_ButTheSameStartTimeIsNot()
    {
        Crashed(Identity("old", pid: 100, ticks: 500));

        var inspector = new FakeInspector(Identity("new", pid: 100, ticks: 900));
        inspector.Processes[100] = ProcessProbe.Present(900); // the successor itself now owns pid 100
        using var lease = Successor(inspector);

        Assert.IsNotNull(lease, "same pid, different start time: the pid was reused");
        StringAssert.Contains(lease.Acquisition.TakenOverReason, "reused");
    }

    [TestMethod]
    public void HolderOnAnotherHost_WaitsForExpiry()
    {
        Crashed(Identity("old", pid: 100, ticks: 500, host: "host-b"));

        var inspector = new FakeInspector(Identity("new", pid: 200, ticks: 900)); // pid 100 absent here
        Assert.IsNull(Successor(inspector), "a holder on another host cannot be probed");
    }

    [TestMethod]
    public void UnprovableHolder_StillBlocks()
    {
        Crashed(Identity("old", pid: 100, ticks: 500));
        var unknown = new FakeInspector(Identity("new", pid: 200, ticks: 900));
        unknown.Processes[100] = ProcessProbe.Unknown;
        Assert.IsNull(Successor(unknown), "an unreadable /proc proves nothing");

        // No boot id on the successor's side: nothing to compare, so nothing is proven.
        var noBoot = new FakeInspector(Identity("new", pid: 200, ticks: 900, boot: ""));
        Assert.IsNull(Successor(noBoot));
    }

    [TestMethod]
    public void ContainerRestart_NewPidNamespace_SameContainer_NewInit_IsTakenOver_AndPid1IsNotProofItself()
    {
        // Both processes are PID 1 in their own namespace.
        Crashed(Identity("old", pid: 1, ticks: 500, ns: "pid:[100]", initTicks: 500));

        var inspector = new FakeInspector(Identity("new", pid: 1, ticks: 700, ns: "pid:[200]", initTicks: 700));
        inspector.Processes[1] = ProcessProbe.Present(700); // pid 1 IS alive: the successor
        using var lease = Successor(inspector);

        Assert.IsNotNull(lease);
        StringAssert.Contains(lease.Acquisition.TakenOverReason, "container restarted");
    }

    [TestMethod]
    public void NestedPidNamespace_SameContainer_SameInitStart_Blocks()
    {
        Crashed(Identity("old", pid: 7, ticks: 500, ns: "pid:[100]", initTicks: 1000));

        Assert.IsNull(Successor(new FakeInspector(Identity("new", pid: 9, ticks: 700, ns: "pid:[200]", initTicks: 1000))),
            "the container run is the same: a different namespace inside it proves nothing");
        Assert.IsNull(Successor(new FakeInspector(Identity("new", pid: 9, ticks: 700, ns: "pid:[200]", initTicks: null))),
            "an unknown init start never proves a restart");
    }

    [TestMethod]
    public void ContainerRestart_RequiresBothSidesToBePid1()
    {
        // Holder was PID 1, successor is a nested process (pid 9) in a fresh namespace with a newer init start.
        Crashed(Identity("old", pid: 1, ticks: 500, ns: "pid:[100]", initTicks: 500));
        Assert.IsNull(Successor(new FakeInspector(Identity("new", pid: 9, ticks: 700, ns: "pid:[200]", initTicks: 700))),
            "a successor that is not the init of its namespace proves no restart");
    }

    [TestMethod]
    public void ContainerRestart_HolderThatWasNotPid1_Blocks()
    {
        Crashed(Identity("old", pid: 7, ticks: 500, ns: "pid:[100]", initTicks: 500));
        Assert.IsNull(Successor(new FakeInspector(Identity("new", pid: 1, ticks: 700, ns: "pid:[200]", initTicks: 700))),
            "a holder that was not the init may have lived on in a nested namespace");
    }

    [TestMethod]
    public void UnknownOrHiddenProbe_Blocks_EvenWhenTheHolderIsInTheSameNamespace()
    {
        Crashed(Identity("old", pid: 100, ticks: 500));
        var hidden = new FakeInspector(Identity("new", pid: 200, ticks: 900));
        hidden.Processes[100] = ProcessProbe.Unknown; // /proc/<pid> hidden (hidepid) or stat unreadable
        Assert.IsNull(Successor(hidden));
    }

    [TestMethod]
    public void Classify_UsesKillAsTheAuthority()
    {
        Assert.IsFalse(ProcessInspector.Classify(-1, 3, () => 5).Exists, "ESRCH: absent");
        Assert.IsTrue(ProcessInspector.Classify(-1, 3, () => 5).Known);
        Assert.IsTrue(ProcessInspector.Classify(-1, 1, () => 5).Exists, "EPERM: present, owned by another user");
        Assert.IsTrue(ProcessInspector.Classify(0, 0, () => 5).Exists);
        Assert.AreEqual(5, ProcessInspector.Classify(0, 0, () => 5).StartTicks);
        Assert.IsFalse(ProcessInspector.Classify(0, 0, () => null).Known, "present but stat unreadable: unknown, not reused");
        Assert.IsFalse(ProcessInspector.Classify(-1, 22, () => 5).Known, "any other errno proves nothing");
    }

    [TestMethod]
    public void OtherPidNamespace_WithoutTheSameContainerId_StillBlocks()
    {
        // Same hostname (e.g. host networking), different containers: the holder may be alive in its own namespace.
        Crashed(Identity("old", pid: 1, ticks: 500, ns: "pid:[100]", container: "c1"));

        Assert.IsNull(Successor(new FakeInspector(Identity("new", pid: 1, ticks: 700, ns: "pid:[200]", container: "c2", initTicks: 700))));
        Assert.IsNull(Successor(new FakeInspector(Identity("new", pid: 1, ticks: 700, ns: "pid:[200]", container: null, initTicks: 700))));
    }

    [TestMethod]
    public void RebootedHost_IsTakenOver()
    {
        Crashed(Identity("old", pid: 100, ticks: 500, boot: "boot-1"));

        var inspector = new FakeInspector(Identity("new", pid: 200, ticks: 900, boot: "boot-2"));
        inspector.Processes[100] = ProcessProbe.Present(500); // a different process of the new boot
        using var lease = Successor(inspector);

        Assert.IsNotNull(lease);
        StringAssert.Contains(lease.Acquisition.TakenOverReason, "rebooted");
    }

    [TestMethod]
    public void UnknownOrDifferentMachineId_DropsTheRebootShortcut()
    {
        Crashed(Identity("old", pid: 100, ticks: 500, boot: "boot-1"));

        Assert.IsNull(Successor(new FakeInspector(Identity("new", pid: 200, ticks: 900, boot: "boot-2", machine: null))),
            "no machine identity here: another machine with the same hostname would look like a reboot");
        Assert.IsNull(Successor(new FakeInspector(Identity("new", pid: 200, ticks: 900, boot: "boot-2", machine: "machine-2"))));

        Exec("UPDATE writer_run_state SET machine_id = NULL;");
        Assert.IsNull(Successor(new FakeInspector(Identity("new", pid: 200, ticks: 900, boot: "boot-2"))),
            "the holder recorded no machine identity");
    }

    [TestMethod]
    public void ConcurrentTakeover_OnlyOneAcquirerWins()
    {
        Crashed(Identity("old", pid: 100, ticks: 500));

        var winners = 0;
        var leases = new List<WriterLease>();
        using var start = new Barrier(2);
        var tasks = Enumerable.Range(0, 2).Select(i => Task.Run(() =>
        {
            var inspector = new FakeInspector(Identity($"new-{i}", pid: 200 + i, ticks: 900 + i));
            // Each acquirer's pid is alive from every viewpoint, so only the lease row decides.
            inspector.Processes[200] = ProcessProbe.Present(900);
            inspector.Processes[201] = ProcessProbe.Present(901);
            start.SignalAndWait();
            var lease = Successor(inspector);
            if (lease is null) return;
            lock (leases) leases.Add(lease);
            Interlocked.Increment(ref winners);
        })).ToArray();
        Task.WaitAll(tasks);

        Assert.AreEqual(1, winners, "two writers must never both hold the lease");
        foreach (var l in leases) l.Dispose();
    }

    [TestMethod]
    public void AbandonedRun_IsReportedAsAbandoned_NotAsACrash()
    {
        var abandoned = WriterLease.TryAcquire(
            _dbPath, "straggler", TimeSpan.FromMinutes(5), autoHeartbeat: false,
            inspector: new FakeInspector(Identity("old", pid: 100, ticks: 500)), kind: WriterKind.Service);
        Assert.IsNotNull(abandoned);
        abandoned.Abandon();
        Assert.AreEqual(WriterEndReason.Abandoned, abandoned.EndReason);
        abandoned.Dispose();

        using var next = Successor(new FakeInspector(Identity("new", pid: 200, ticks: 900))); // pid 100 gone
        Assert.IsNotNull(next);
        Assert.IsTrue(next.Acquisition.PreviousRunUnclean);
        Assert.AreEqual(WriterEndReason.Abandoned, next.Acquisition.PreviousRunEndReason);
        Assert.AreEqual(WriterKind.Service, next.Acquisition.PreviousRunKind);
        var state = RunState()!;
        Assert.AreEqual(0, state.UncleanShutdownsTotal, "an abandon is not a crash");
        Assert.AreEqual(1, state.AbandonedRunsTotal);
        Assert.AreEqual(WriterEndReason.Abandoned, state.PreviousRunEndReason);
    }

    [TestMethod]
    public void CrashedRun_IsReportedAsUnclean_WithItsKind()
    {
        Crashed(Identity("old", pid: 100, ticks: 500));
        using var next = Successor(new FakeInspector(Identity("new", pid: 200, ticks: 900)));
        Assert.IsNotNull(next);
        Assert.AreEqual(WriterEndReason.Unclean, next.Acquisition.PreviousRunEndReason);
        Assert.AreEqual(WriterKind.Cli, next.Acquisition.PreviousRunKind);
        Assert.AreEqual(1, RunState()!.UncleanShutdownsTotal);
    }

    [TestMethod]
    public void LeaseWithoutRecordedIdentity_IsNotTakenOver()
    {
        Crashed(Identity("old", pid: 100, ticks: 500));
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM writer_run_state;";
            cmd.ExecuteNonQuery();
        }

        Assert.IsNull(Successor(new FakeInspector(Identity("new", pid: 200, ticks: 900))));
    }

    [TestMethod]
    public void UncleanShutdown_IsDetectedAndCounted_AndACleanShutdownClearsIt()
    {
        using (var first = Successor(new FakeInspector(Identity("a", pid: 100, ticks: 500))))
        {
            Assert.IsNotNull(first);
            Assert.IsFalse(first.Acquisition.PreviousRunUnclean, "the first run has no predecessor");
        }
        Assert.IsTrue(RunState()!.CleanShutdown);

        Crashed(Identity("b", pid: 100, ticks: 600)); // acquires cleanly after "a", then dies
        Assert.IsFalse(RunState()!.CleanShutdown);

        using (var third = Successor(new FakeInspector(Identity("c", pid: 300, ticks: 900))))
        {
            Assert.IsNotNull(third);
            Assert.IsTrue(third.Acquisition.PreviousRunUnclean);
            Assert.IsNotNull(third.Acquisition.PreviousRunHeartbeatAt);
            var state = RunState()!;
            Assert.IsTrue(state.PreviousRunUnclean);
            Assert.AreEqual(1, state.UncleanShutdownsTotal);
        }

        using var fourth = Successor(new FakeInspector(Identity("d", pid: 400, ticks: 950)));
        Assert.IsNotNull(fourth);
        Assert.IsFalse(fourth.Acquisition.PreviousRunUnclean, "the third run released the lease cleanly");
        var after = RunState()!;
        Assert.IsFalse(after.PreviousRunUnclean);
        Assert.AreEqual(1, after.UncleanShutdownsTotal, "the counter is durable and monotonic");
    }

    [TestMethod]
    public void RealInspector_DescribesThisProcess_AndNeverProvesItselfDead()
    {
        var inspector = ProcessInspector.Instance;
        Assert.AreEqual(Environment.ProcessId, inspector.Current.Pid);
        Assert.IsNull(HolderLiveness.ProveDead(inspector.Current, inspector));

        var twin = inspector.Current with { InstanceId = "another-instance" };
        if (OperatingSystem.IsLinux() && twin.BootId is not null && twin.StartTicks is not null)
            Assert.IsNull(HolderLiveness.ProveDead(twin, inspector), "this very process is alive with that start time");
    }
}
