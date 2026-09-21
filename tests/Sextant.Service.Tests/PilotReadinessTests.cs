using Sextant.Service.Observability;
using Sextant.Service.Rollout;

namespace Sextant.Service.Tests;

/// <summary>
/// Phase 17 slice 3 — criterion 7: the pilot exit criteria are EXERCISED in code, not just written in a
/// runbook. The decisive rule is the issue #76 hard precondition: an UNTRUSTED multi-tenant pilot is not
/// ready until out-of-process OS-hard worker isolation is available (the shipped sandbox is in-process
/// defense-in-depth only), while a TRUSTED single-tenant pilot may proceed on the in-process sandbox.
/// </summary>
[TestClass]
public class PilotReadinessTests
{
    [TestMethod]
    public void Untrusted_WithoutHardIsolation_IsBlockedBy76()
    {
        var report = PilotReadiness.Evaluate(FullyGreen() with
        {
            WorkloadClass = PilotWorkloadClass.UntrustedMultiTenant,
            HardOsIsolationAvailable = false
        });

        Assert.IsFalse(report.Ready, "an untrusted multi-tenant pilot is NOT ready without OS-hard isolation (#76)");
        Assert.IsTrue(report.Blockers.Any(c => c.Id == "hard_os_isolation"),
            "the #76 hard-isolation check is the blocker");
    }

    [TestMethod]
    public void Untrusted_WithHardIsolation_AndAllGreen_IsReady()
    {
        var report = PilotReadiness.Evaluate(FullyGreen() with
        {
            WorkloadClass = PilotWorkloadClass.UntrustedMultiTenant,
            HardOsIsolationAvailable = true
        });

        Assert.IsTrue(report.Ready, "with #76 shipped and everything else green, an untrusted pilot is ready");
        Assert.AreEqual(0, report.Blockers.Count);
    }

    [TestMethod]
    public void Trusted_WithoutHardIsolation_IsReady_76IsAdvisory()
    {
        var report = PilotReadiness.Evaluate(FullyGreen() with
        {
            WorkloadClass = PilotWorkloadClass.TrustedSingleTenant,
            HardOsIsolationAvailable = false
        });

        Assert.IsTrue(report.Ready,
            "a trusted single-tenant pilot may proceed on the in-process sandbox — #76 is advisory here");
        var isolation = report.Checks.Single(c => c.Id == "hard_os_isolation");
        Assert.IsFalse(isolation.Passed);
        Assert.IsFalse(isolation.Blocking, "the #76 check is non-blocking for a trusted workload");
    }

    [TestMethod]
    public void AuthorizationDisabled_BlocksEvenTrusted()
    {
        var report = PilotReadiness.Evaluate(FullyGreen() with
        {
            WorkloadClass = PilotWorkloadClass.TrustedSingleTenant,
            AuthorizationEnabled = false
        });

        Assert.IsFalse(report.Ready, "a pilot must enforce authorization (criterion 1)");
        Assert.IsTrue(report.Blockers.Any(c => c.Id == "authorization_enabled"));
    }

    [TestMethod]
    public void NoRestorableBackup_Blocks()
    {
        var report = PilotReadiness.Evaluate(FullyGreen() with { RecentBackupAvailable = false });
        Assert.IsFalse(report.Ready, "backup/restore must be proven before pilot (criterion 6)");
        Assert.IsTrue(report.Blockers.Any(c => c.Id == "restorable_backup"));
    }

    [TestMethod]
    public void ActiveCriticalAlert_Blocks()
    {
        var report = PilotReadiness.Evaluate(FullyGreen() with
        {
            Alerts = [new Alert { Id = "storage_pressure", Level = AlertLevel.Critical, Message = "disk full" }]
        });

        Assert.IsFalse(report.Ready, "an active critical alert blocks pilot readiness");
        Assert.IsTrue(report.Blockers.Any(c => c.Id == "no_critical_alerts"));
    }

    [TestMethod]
    public void WarningAlert_DoesNotBlock()
    {
        var report = PilotReadiness.Evaluate(FullyGreen() with
        {
            Alerts = [new Alert { Id = "queue_delay_high", Level = AlertLevel.Warning, Message = "slow" }]
        });
        Assert.IsTrue(report.Ready, "a warning alert is advisory, not a pilot blocker");
    }

    // A baseline input with every blocking precondition satisfied for a trusted pilot; individual tests
    // flip one field to prove that field's effect.
    private static PilotReadinessInput FullyGreen() => new()
    {
        WorkloadClass = PilotWorkloadClass.TrustedSingleTenant,
        AuthorizationEnabled = true,
        SandboxEnforced = true,
        HardOsIsolationAvailable = false,
        RecentBackupAvailable = true,
        CatalogRecovered = true,
        WorkerCapacityAvailable = true,
        Alerts = []
    };
}
