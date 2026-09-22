using Sextant.Service.Observability;

namespace Sextant.Service.Rollout;

/// <summary>The trust posture of the repositories a pilot deployment will host.</summary>
public enum PilotWorkloadClass
{
    /// <summary>Only first-party repositories the operator already trusts to evaluate on a shared host.</summary>
    TrustedSingleTenant,

    /// <summary>Third-party / multi-tenant repositories whose MSBuild evaluation is untrusted code.</summary>
    UntrustedMultiTenant
}

/// <summary>The observed state a <see cref="PilotReadiness"/> evaluation is computed from.</summary>
public sealed record PilotReadinessInput
{
    /// <summary>The trust posture the operator intends to pilot (drives the #76 hard precondition).</summary>
    public required PilotWorkloadClass WorkloadClass { get; init; }

    /// <summary>Read-authorization policy is ENABLED (slice 1, criterion 1).</summary>
    public required bool AuthorizationEnabled { get; init; }

    /// <summary>
    /// The operator control plane is SECURED with a control token (not the dev-only anonymous-open mode).
    /// A null/empty control token leaves the control plane — including the observability + audit surfaces
    /// that aggregate cross-tenant scopes, counts, and cost — reachable anonymously, which is acceptable
    /// only for single-node dev, never for a pilot (criterion-1 leakage guard).
    /// </summary>
    public required bool ControlPlaneSecured { get; init; }

    /// <summary>The evaluation sandbox is ENFORCED (slice 1, criterion 2).</summary>
    public required bool SandboxEnforced { get; init; }

    /// <summary>
    /// OS-hard, out-of-process worker isolation (issue #76) is available. The slice-1 sandbox is an
    /// IN-PROCESS defense-in-depth boundary only; #76 (Job Object / cgroup+rlimits+netns / sandbox-exec)
    /// is the HARD isolation boundary required before hosting untrusted multi-tenant repositories in
    /// production. Defaults false because #76 is still open.
    /// </summary>
    public bool HardOsIsolationAvailable { get; init; }

    /// <summary>A recent, restorable backup exists (criterion 6 exercised — DR path proven).</summary>
    public required bool RecentBackupAvailable { get; init; }

    /// <summary>Startup recovery + reconciliation completed cleanly (slice 2, criterion 3).</summary>
    public required bool CatalogRecovered { get; init; }

    /// <summary>This node has worker capacity to produce snapshots end-to-end.</summary>
    public required bool WorkerCapacityAvailable { get; init; }

    /// <summary>The current alerts (a critical alert blocks pilot readiness).</summary>
    public IReadOnlyList<Alert> Alerts { get; init; } = [];
}

/// <summary>One evaluated pilot exit-criterion check.</summary>
public sealed record PilotCheck
{
    public required string Id { get; init; }
    public required bool Passed { get; init; }

    /// <summary>When true, a failed check BLOCKS pilot readiness; when false it is advisory only.</summary>
    public required bool Blocking { get; init; }
    public required string Message { get; init; }
}

/// <summary>
/// The result of evaluating pilot exit criteria + rollback readiness (criterion 7). <see cref="Ready"/>
/// is true only when every BLOCKING check passes for the requested workload class.
/// </summary>
public sealed record PilotReadinessReport
{
    public required PilotWorkloadClass WorkloadClass { get; init; }
    public required bool Ready { get; init; }
    public required IReadOnlyList<PilotCheck> Checks { get; init; }

    /// <summary>The blocking checks that failed (the gate's reasons for not being pilot-ready).</summary>
    public IReadOnlyList<PilotCheck> Blockers => Checks.Where(c => c.Blocking && !c.Passed).ToArray();
}

/// <summary>
/// Evaluates whether the service meets its documented pilot exit criteria and rollback preconditions
/// (criterion 7), so the go/no-go decision is EXERCISED in code rather than only written in a runbook.
///
/// The hard rule this gate encodes — the reason it is code, not prose — is the issue #76 precondition:
/// the current worker sandbox is IN-PROCESS defense-in-depth, NOT a hard isolation boundary, so a pilot
/// that will host UNTRUSTED, MULTI-TENANT repositories is NOT ready until out-of-process OS-hard
/// isolation (#76) is available. A trusted single-tenant pilot may proceed on the in-process sandbox
/// (that check is advisory for it), but is never marked ready with authz disabled, the sandbox off, no
/// restorable backup, an unrecovered catalog, no worker capacity, or an active critical alert. Pure and
/// deterministic.
/// </summary>
public static class PilotReadiness
{
    public static PilotReadinessReport Evaluate(PilotReadinessInput input)
    {
        var untrusted = input.WorkloadClass == PilotWorkloadClass.UntrustedMultiTenant;
        var hasCritical = input.Alerts.Any(a => a.Level == AlertLevel.Critical);

        var checks = new List<PilotCheck>
        {
            new()
            {
                Id = "authorization_enabled",
                Passed = input.AuthorizationEnabled,
                Blocking = true,
                Message = input.AuthorizationEnabled
                    ? "Read-authorization policy is enforced."
                    : "Read-authorization policy is DISABLED — a pilot must enforce per-repository authorization (criterion 1)."
            },
            new()
            {
                Id = "control_plane_secured",
                Passed = input.ControlPlaneSecured,
                Blocking = true,
                Message = input.ControlPlaneSecured
                    ? "The operator control plane requires a control token."
                    : "The control plane has NO control token — its observability/audit surfaces (cross-tenant scopes, "
                      + "counts, cost) are anonymously reachable. Set a control token before pilot (criterion 1)."
            },
            new()
            {
                Id = "sandbox_enforced",
                Passed = input.SandboxEnforced,
                Blocking = true,
                Message = input.SandboxEnforced
                    ? "Evaluation sandbox is enforced."
                    : "Evaluation sandbox is DISABLED — untrusted MSBuild evaluation must be sandboxed (criterion 2)."
            },
            new()
            {
                // #76: HARD precondition for untrusted multi-tenant; advisory for trusted single-tenant.
                Id = "hard_os_isolation",
                Passed = input.HardOsIsolationAvailable,
                Blocking = untrusted,
                Message = input.HardOsIsolationAvailable
                    ? "Out-of-process OS-hard worker isolation (#76) is available."
                    : untrusted
                        ? "OS-hard out-of-process worker isolation (#76) is NOT available. The current in-process " +
                          "sandbox is defense-in-depth, not a hard boundary; it is a HARD PRECONDITION before hosting " +
                          "untrusted multi-tenant repositories (see #76 and #19). Pilot BLOCKED for this workload class."
                        : "OS-hard isolation (#76) is not available; acceptable for a TRUSTED single-tenant pilot on the " +
                          "in-process sandbox, but required before untrusted multi-tenant rollout."
            },
            new()
            {
                Id = "restorable_backup",
                Passed = input.RecentBackupAvailable,
                Blocking = true,
                Message = input.RecentBackupAvailable
                    ? "A restorable backup exists (DR path exercised)."
                    : "No restorable backup exists — backup/restore must be proven before pilot (criterion 6)."
            },
            new()
            {
                Id = "catalog_recovered",
                Passed = input.CatalogRecovered,
                Blocking = true,
                Message = input.CatalogRecovered
                    ? "Startup recovery + reconciliation completed cleanly."
                    : "Catalog recovery/reconciliation did not complete — resolve before pilot (criterion 3)."
            },
            new()
            {
                Id = "worker_capacity",
                Passed = input.WorkerCapacityAvailable,
                Blocking = true,
                Message = input.WorkerCapacityAvailable
                    ? "Worker capacity is available to produce snapshots end-to-end."
                    : "No worker capacity — a pilot node must be able to produce snapshots."
            },
            new()
            {
                Id = "no_critical_alerts",
                Passed = !hasCritical,
                Blocking = true,
                Message = hasCritical
                    ? "One or more CRITICAL alerts are active — clear them before pilot."
                    : "No critical alerts active."
            }
        };

        var ready = checks.Where(c => c.Blocking).All(c => c.Passed);
        return new PilotReadinessReport { WorkloadClass = input.WorkloadClass, Ready = ready, Checks = checks };
    }
}
