using Sextant.Core.Platform;
using Sextant.Store;

namespace Sextant.Service.Placement;

/// <summary>
/// The Phase-15 capability-routing worker: the service's <see cref="ISnapshotWorker"/> that decides WHICH
/// worker capability should evaluate each ensure-snapshot request, then runs it there. It composes three
/// substitutable seams — a set of <see cref="IWorkerPlacement"/>s (a default Linux placement + zero or more
/// native ones), an <see cref="IPlatformEvaluationProbe"/> that observes DEMONSTRATED Linux evaluation, and
/// the pure <see cref="CapabilityRouter"/> decision — and:
///
/// <list type="number">
///   <item>probes the project graph's per-project Linux outcome + requirements;</item>
///   <item>routes the job (job-granular) to the least-specialized worker that can evaluate it, defaulting
///   to Linux and escalating ONLY when Linux is demonstrably insufficient (criteria 1–3);</item>
///   <item>fails the job CLOSED — <see cref="SnapshotJobStatus.Unsupported"/> with structured per-project
///   diagnostics, never a silent empty success — when no compatible worker exists (criterion 4);</item>
///   <item>executes on the selected placement, whose published snapshot records the node's capability
///   fingerprint for cache-compat (criterion 5).</item>
/// </list>
///
/// With only the default placement registered (the single-node/local case) it never escalates and behaves
/// exactly like the Phase-13 in-process worker — routing infrastructure is entirely opt-in (CRITICAL 2).
/// The native placements are substitutable, so the whole routing DECISION + fail-closed + fingerprint path
/// is covered on a Linux CI with fake native placements (criterion 6); real native execution is Phase 14.
/// </summary>
public sealed class CapabilityRoutingSnapshotWorker : ISnapshotWorker
{
    private readonly IWorkerPlacement _defaultPlacement;
    private readonly IReadOnlyList<IWorkerPlacement> _nativePlacements;
    private readonly IPlatformEvaluationProbe _probe;
    private readonly PlatformRoutingPolicy _policy;
    private readonly Action<string>? _log;

    public CapabilityRoutingSnapshotWorker(
        IWorkerPlacement defaultPlacement,
        IReadOnlyList<IWorkerPlacement>? nativePlacements = null,
        IPlatformEvaluationProbe? probe = null,
        PlatformRoutingPolicy? policy = null,
        Action<string>? log = null)
    {
        _defaultPlacement = defaultPlacement;
        _nativePlacements = nativePlacements ?? [];
        _probe = probe ?? new AssumeLinuxCapableProbe();
        _policy = policy ?? PlatformRoutingPolicy.Default;
        _log = log;
    }

    public async Task<SnapshotWorkResult> ProduceAsync(
        EnsureSnapshotRequest request, string identityHash, string scratchDir, CancellationToken cancellationToken)
    {
        var probe = await _probe.ProbeAsync(request, scratchDir, cancellationToken).ConfigureAwait(false);
        if (!probe.CheckoutAvailable)
            return SnapshotWorkResult.Unsupported(
                probe.UnavailableReason ?? "no checkout available to evaluate this request.");

        var nativeCapabilities = _nativePlacements.Select(p => p.Capability).ToArray();
        var routing = CapabilityRouter.RouteJob(
            probe.Requirements, probe.LinuxOutcomes, _defaultPlacement.Capability, nativeCapabilities, _policy);

        // Fail CLOSED: no compatible worker for at least one project (or no single worker covers the union).
        // Record one structured per-project diagnostic so the status API explains WHICH projects and WHY —
        // and the snapshot is never published complete (criteria 4/5). Reuses snapshot_job_diagnostics.
        if (!routing.CanRun)
        {
            _log?.Invoke($"Routing failed closed for {request.RepositoryRemoteUrl}@{request.CommitSha}: {routing.FailureReason}");
            return SnapshotWorkResult.Unsupported(
                routing.FailureReason ?? "no compatible worker capability exists for this project graph.",
                BuildUnsupportedDiagnostics(routing));
        }

        var placement = SelectPlacement(routing.ExecutionWorker!);
        if (placement is null)
        {
            // The router chose a capability with no backing placement — treat as fail-closed rather than
            // silently running on the wrong worker. Should not happen (capabilities come FROM placements).
            return SnapshotWorkResult.Unsupported(
                "the routing decision selected a worker capability with no registered placement.");
        }

        if (!placement.IsDefault)
            _log?.Invoke(
                $"Routed {request.RepositoryRemoteUrl}@{request.CommitSha} to a " +
                $"{routing.ExecutionWorker!.OperatingSystem.ToName()} worker (Linux evaluation was insufficient).");

        var result = await placement.ProduceAsync(request, identityHash, scratchDir, cancellationToken).ConfigureAwait(false);

        // Annotate a successful ROUTED build with an info diagnostic naming the worker OS, so provenance
        // records that this snapshot was produced off the default worker (criterion 5 provenance).
        if (result.Status == SnapshotJobStatus.Complete && !placement.IsDefault)
            result = result with { Projects = [.. result.Projects, RoutedInfo(routing.ExecutionWorker!)] };

        return result;
    }

    private IWorkerPlacement? SelectPlacement(WorkerCapability capability)
    {
        if (string.Equals(_defaultPlacement.Capability.Fingerprint, capability.Fingerprint, StringComparison.Ordinal))
            return _defaultPlacement;
        return _nativePlacements.FirstOrDefault(p =>
            string.Equals(p.Capability.Fingerprint, capability.Fingerprint, StringComparison.Ordinal));
    }

    private static IReadOnlyList<ProjectOutcome> BuildUnsupportedDiagnostics(JobRoutingResult routing)
    {
        var outcomes = new List<ProjectOutcome>();
        foreach (var decision in routing.Plan.Decisions.Where(d => !d.IsPlaced))
        {
            outcomes.Add(new ProjectOutcome
            {
                ProjectCanonicalId = decision.ProjectId,
                ProjectPath = decision.ProjectPath,
                Severity = JobDiagnosticSeverity.Error,
                Code = "no_compatible_worker",
                Message = decision.Reason
                    ?? "no worker capability can evaluate this project's required platform."
            });
        }

        // If the per-project plan placed everything but the job still cannot run (no single worker covers
        // the union of escalated requirements), record one job-level diagnostic so the failure is explained.
        if (outcomes.Count == 0)
            outcomes.Add(new ProjectOutcome
            {
                Severity = JobDiagnosticSeverity.Error,
                Code = "no_compatible_worker",
                Message = routing.FailureReason ?? "no compatible worker capability exists for this project graph."
            });

        return outcomes;
    }

    private static ProjectOutcome RoutedInfo(WorkerCapability worker) => new()
    {
        Severity = JobDiagnosticSeverity.Info,
        Code = "routed_to_native_worker",
        Message = $"evaluated on a {worker.OperatingSystem.ToName()} worker because Linux evaluation was insufficient."
    };
}
