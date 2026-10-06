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

    [TestMethod]
    public void ControlPlaneUnsecured_BlocksEvenTrusted()
    {
        var report = PilotReadiness.Evaluate(FullyGreen() with
        {
            WorkloadClass = PilotWorkloadClass.TrustedSingleTenant,
            ControlPlaneSecured = false
        });

        Assert.IsFalse(report.Ready,
            "an anonymous (tokenless) control plane exposes cross-tenant observability — blocked for pilot (criterion 1)");
        Assert.IsTrue(report.Blockers.Any(c => c.Id == "control_plane_secured"));
    }

    [TestMethod]
    [DataRow(PilotWorkloadClass.TrustedSingleTenant, false)]
    [DataRow(PilotWorkloadClass.UntrustedMultiTenant, true)]
    public void LowCompleteness_IsAdvisoryOrBlocking(PilotWorkloadClass workload, bool blocking)
    {
        var report = PilotReadiness.Evaluate(FullyGreen() with
        {
            WorkloadClass = workload,
            HardOsIsolationAvailable = true,
            RecentJobs = Recent(complete: 8, partial: 2),
            Alerts = [new Alert { Id = "low_completeness_rate", Level = AlertLevel.Warning, Message = "partial" }]
        });
        var check = report.Checks.Single(c => c.Id == "snapshot_completeness");
        Assert.IsFalse(check.Passed);
        Assert.AreEqual(blocking, check.Blocking);
        Assert.AreEqual(!blocking, report.Ready);
        StringAssert.Contains(check.Message, "coverage.reasons");
        StringAssert.Contains(check.Message, "solutions");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(4)]
    public void InsufficientCompletenessSamples_AreNotHealthy(int complete)
    {
        var report = PilotReadiness.Evaluate(FullyGreen() with
        {
            WorkloadClass = PilotWorkloadClass.UntrustedMultiTenant,
            HardOsIsolationAvailable = true,
            RecentJobs = Recent(complete: complete)
        });
        var check = report.Checks.Single(c => c.Id == "snapshot_completeness");
        Assert.IsFalse(check.Passed);
        Assert.IsFalse(report.Ready);
        StringAssert.Contains(check.Message, "not assessed");
    }

    [TestMethod]
    public void MissingCompletenessMetrics_AreNotHealthy()
    {
        var report = PilotReadiness.Evaluate(FullyGreen() with { RecentJobs = null });
        var check = report.Checks.Single(c => c.Id == "snapshot_completeness");
        Assert.IsFalse(check.Passed);
        Assert.IsFalse(check.Blocking);
        Assert.IsTrue(report.Ready);
        StringAssert.Contains(check.Message, "not assessed");
    }

    [TestMethod]
    [DataRow(9, 1, 0.9, true)]
    [DataRow(8, 2, 0.9, false)]
    [DataRow(8, 2, 0.8, true)]
    public void Completeness_UsesAlertWarningThreshold(int complete, int partial, double threshold, bool passed)
    {
        var report = PilotReadiness.Evaluate(FullyGreen() with
        {
            RecentJobs = Recent(complete, partial)
        }, new AlertThresholds { CompletenessRateWarn = threshold });
        Assert.AreEqual(passed, report.Checks.Single(c => c.Id == "snapshot_completeness").Passed);
    }

    [TestMethod]
    [DataRow(5, 5, true)]
    [DataRow(5, 6, false)]
    [DataRow(0, 0, false)]
    public void Completeness_UsesSharedSampleMinimum(int complete, int minimum, bool passed)
    {
        var report = PilotReadiness.Evaluate(FullyGreen() with
        {
            RecentJobs = Recent(complete)
        }, new AlertThresholds { SuccessRateMinSamples = minimum });
        Assert.AreEqual(passed, report.Checks.Single(c => c.Id == "snapshot_completeness").Passed);
    }

    [TestMethod]
    public void AllPartialJobs_FailCompleteness_AndCriticalAlertStillBlocksTrusted()
    {
        var jobs = Recent(partial: 5);
        var report = PilotReadiness.Evaluate(FullyGreen() with
        {
            RecentJobs = jobs,
            Alerts = [new Alert { Id = "low_completeness_rate", Level = AlertLevel.Critical, Message = "partial" }]
        });
        Assert.AreEqual(1.0, jobs.SuccessRate, "partial outcomes are successful publication");
        Assert.IsFalse(report.Checks.Single(c => c.Id == "snapshot_completeness").Passed);
        Assert.IsFalse(report.Ready);
        Assert.IsTrue(report.Blockers.Any(c => c.Id == "no_critical_alerts"));
    }

    // A baseline input with every blocking precondition satisfied for a trusted pilot; individual tests
    // flip one field to prove that field's effect.
    private static PilotReadinessInput FullyGreen() => new()
    {
        WorkloadClass = PilotWorkloadClass.TrustedSingleTenant,
        AuthorizationEnabled = true,
        ControlPlaneSecured = true,
        SandboxEnforced = true,
        HardOsIsolationAvailable = false,
        RecentBackupAvailable = true,
        CatalogRecovered = true,
        WorkerCapacityAvailable = true,
        RecentJobs = Recent(complete: 5),
        Alerts = []
    };

    private static RecentJobMetrics Recent(long complete = 0, long partial = 0) => new()
    {
        WindowStartUnixMs = 100,
        WindowEndUnixMs = 200,
        MinimumSamples = 5,
        Complete = complete,
        Partial = partial
    };
}
