namespace Sextant.Core.Platform;

/// <summary>
/// The observed result of attempting to evaluate a project on the default (Linux) worker (Phase 15). The
/// routing rule keys off this DEMONSTRATED outcome, never the target-framework string alone: a project
/// whose Linux evaluation succeeded stays on Linux even if its TFM mentions <c>-windows</c>; a project is
/// escalated to a native worker only when Linux evaluation is demonstrably insufficient.
/// </summary>
public sealed record LinuxEvaluationOutcome
{
    /// <summary>Whether the default (Linux) worker faithfully evaluated the project.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>The capability requirement proven by the failure, when Linux was insufficient.</summary>
    public CapabilityRequirement? DemonstratedRequirement { get; init; }

    /// <summary>A human-readable reason (the workspace diagnostic) backing an insufficiency.</summary>
    public string? Reason { get; init; }

    /// <summary>Linux evaluated the project — no escalation needed.</summary>
    public static LinuxEvaluationOutcome Success { get; } = new() { Succeeded = true };

    /// <summary>Linux could not faithfully evaluate the project; carries the demonstrated requirement.</summary>
    public static LinuxEvaluationOutcome Insufficient(CapabilityRequirement requirement, string reason) =>
        new() { Succeeded = false, DemonstratedRequirement = requirement, Reason = reason };
}

/// <summary>Whether a project was placed on a worker or has no compatible worker.</summary>
public enum RoutingOutcome
{
    /// <summary>The project was placed on a worker that can faithfully evaluate it.</summary>
    Placed = 0,

    /// <summary>No available worker satisfies the project's requirement — the job fails closed.</summary>
    Unsupported = 1
}

/// <summary>The routing decision for a single project (Phase 15).</summary>
public sealed record RoutingDecision
{
    /// <summary>The project this decision is for.</summary>
    public required string ProjectId { get; init; }

    /// <summary>The project's repo-relative path, when known (for diagnostics).</summary>
    public string? ProjectPath { get; init; }

    /// <summary>Whether the project was placed or is unsupported.</summary>
    public required RoutingOutcome Outcome { get; init; }

    /// <summary>The capability fingerprint of the worker the project was placed on (Placed only).</summary>
    public string? WorkerCapabilityFingerprint { get; init; }

    /// <summary>The OS of the worker the project was placed on (Placed only).</summary>
    public PlatformOperatingSystem? WorkerOperatingSystem { get; init; }

    /// <summary>Whether the project stayed on the default (Linux) worker (Placed only).</summary>
    public bool IsDefaultWorker { get; init; }

    /// <summary>The requirement that could not be met (Unsupported only).</summary>
    public CapabilityRequirement? MissingCapability { get; init; }

    /// <summary>A human-readable reason (Unsupported only).</summary>
    public string? Reason { get; init; }

    /// <summary>True when the project was placed on a worker.</summary>
    public bool IsPlaced => Outcome == RoutingOutcome.Placed;
}

/// <summary>The aggregate routing decision for a project graph (Phase 15).</summary>
public sealed record RoutingPlan
{
    /// <summary>One decision per project, in input order.</summary>
    public required IReadOnlyList<RoutingDecision> Decisions { get; init; }

    /// <summary>Whether every project was placed on some worker.</summary>
    public bool AllPlaced => Decisions.All(d => d.IsPlaced);

    /// <summary>Whether any project has no compatible worker (the job cannot publish complete).</summary>
    public bool AnyUnsupported => Decisions.Any(d => !d.IsPlaced);

    /// <summary>Whether every project stayed on the default (Linux) worker (the ordinary, no-routing case).</summary>
    public bool PlacedOnDefaultOnly => Decisions.Count > 0 && Decisions.All(d => d.IsPlaced && d.IsDefaultWorker);

    /// <summary>The projects with no compatible worker.</summary>
    public IReadOnlyList<RoutingDecision> Unsupported => Decisions.Where(d => !d.IsPlaced).ToArray();

    /// <summary>The distinct non-default worker fingerprints projects were routed to.</summary>
    public IReadOnlyList<string> EscalatedWorkerFingerprints => Decisions
        .Where(d => d.IsPlaced && !d.IsDefaultWorker && d.WorkerCapabilityFingerprint is not null)
        .Select(d => d.WorkerCapabilityFingerprint!)
        .Distinct(StringComparer.Ordinal)
        .ToArray();
}

/// <summary>
/// The job-granular routing result (Phase 15): the per-project <see cref="Plan"/> PLUS the single worker
/// the whole job graph must execute on. Phase 15 runs each job on ONE placement (per-project mixed
/// production is a later phase), so a job is runnable only when a single available worker satisfies the
/// COMBINED requirements of every project that Linux could not evaluate. When no such worker exists — or
/// any project has no compatible worker at all — <see cref="CanRun"/> is false and the job must fail
/// closed with the per-project diagnostics from <see cref="Plan"/> (acceptance criterion 4).
/// </summary>
public sealed record JobRoutingResult
{
    /// <summary>The per-project decisions (used for diagnostics + provenance).</summary>
    public required RoutingPlan Plan { get; init; }

    /// <summary>Whether the job can run as a single-placement unit.</summary>
    public required bool CanRun { get; init; }

    /// <summary>The worker capability the whole job executes on (null when <see cref="CanRun"/> is false).</summary>
    public WorkerCapability? ExecutionWorker { get; init; }

    /// <summary>Whether the execution worker is the default (Linux) worker — the no-routing case.</summary>
    public bool ExecutionIsDefault { get; init; }

    /// <summary>Why the job cannot run (null when <see cref="CanRun"/> is true).</summary>
    public string? FailureReason { get; init; }
}

/// <summary>
/// The pure, ProcessStack- and service-AGNOSTIC routing DECISION engine (Phase 15). Given each project's
/// discovered requirement, its DEMONSTRATED Linux evaluation outcome, the available worker capabilities,
/// and the per-repo/profile policy, it decides for each project whether to keep it on the default (Linux)
/// worker, escalate it to the least-specialized compatible native worker, or fail it closed as
/// unsupported (acceptance criteria 1–4). It never executes anything — the native-worker placement that
/// acts on a decision is wired behind a service-side seam (Phase 14, deferred).
/// </summary>
public static class CapabilityRouter
{
    /// <summary>
    /// Routes every project in <paramref name="requirements"/>. A project whose Linux evaluation
    /// <see cref="LinuxEvaluationOutcome.Succeeded"/> (or that has no recorded outcome — the conservative
    /// "Linux loaded it fine" default) stays on <paramref name="defaultWorker"/>. A project Linux could
    /// not evaluate escalates to the least-specialized worker in <paramref name="nativeWorkers"/> that
    /// <see cref="WorkerCapability.Satisfies"/> its demonstrated requirement and whose OS the
    /// <paramref name="policy"/> permits; if none exists (or policy forbids escalation, or the failure
    /// cannot be attributed to any concrete capability constraint) it is unsupported and the job fails
    /// closed.
    /// </summary>
    public static RoutingPlan Route(
        IReadOnlyList<ProjectCapabilityRequirement> requirements,
        IReadOnlyDictionary<string, LinuxEvaluationOutcome> linuxOutcomes,
        WorkerCapability defaultWorker,
        IReadOnlyList<WorkerCapability> nativeWorkers,
        PlatformRoutingPolicy policy)
    {
        var defaultFingerprint = defaultWorker.Fingerprint;
        var decisions = new List<RoutingDecision>(requirements.Count);

        foreach (var project in requirements)
        {
            var outcome = linuxOutcomes.TryGetValue(project.ProjectId, out var o) ? o : LinuxEvaluationOutcome.Success;

            // Demonstrated success (or no probe result) → the default worker, regardless of the TFM string.
            if (outcome.Succeeded)
            {
                decisions.Add(new RoutingDecision
                {
                    ProjectId = project.ProjectId,
                    ProjectPath = project.ProjectPath,
                    Outcome = RoutingOutcome.Placed,
                    WorkerCapabilityFingerprint = defaultFingerprint,
                    WorkerOperatingSystem = defaultWorker.OperatingSystem,
                    IsDefaultWorker = true
                });
                continue;
            }

            // Linux is demonstrably insufficient. The proven requirement wins over the declared hint.
            var requirement = outcome.DemonstratedRequirement ?? project.Requirement;

            if (!policy.AllowsNativeRouting)
            {
                decisions.Add(Unsupported(project, requirement,
                    $"Linux evaluation was insufficient and native routing is disabled by policy ({policy.Mode}). " +
                    (outcome.Reason ?? requirement.Reason ?? "no compatible worker.")));
                continue;
            }

            // A demonstrated Linux failure that reduces to a PORTABLE requirement is unclassifiable: Linux
            // could not evaluate the project, yet we cannot attribute the failure to any concrete OS /
            // platform / workload constraint, so no worker can be PROVEN compatible. Fail closed rather
            // than routing to an arbitrary native worker (a Windows worker must never absorb an
            // unattributed failure of, say, an Apple-target project). Escalation requires a concrete,
            // demonstrated capability constraint (acceptance criterion 4).
            if (requirement.IsPortable)
            {
                decisions.Add(Unsupported(project, requirement,
                    ("Linux evaluation was insufficient but the failure could not be attributed to a concrete " +
                     "platform capability, so no worker can be proven compatible. " +
                     (outcome.Reason ?? requirement.Reason ?? string.Empty)).TrimEnd()));
                continue;
            }

            var chosen = SelectLeastSpecialized(nativeWorkers, requirement, policy);
            if (chosen is null)
            {
                decisions.Add(Unsupported(project, requirement,
                    $"no available worker satisfies the required capability " +
                    $"({Describe(requirement)}). {outcome.Reason ?? requirement.Reason ?? string.Empty}".TrimEnd()));
                continue;
            }

            decisions.Add(new RoutingDecision
            {
                ProjectId = project.ProjectId,
                ProjectPath = project.ProjectPath,
                Outcome = RoutingOutcome.Placed,
                WorkerCapabilityFingerprint = chosen.Fingerprint,
                WorkerOperatingSystem = chosen.OperatingSystem,
                IsDefaultWorker = string.Equals(chosen.Fingerprint, defaultFingerprint, StringComparison.Ordinal)
            });
        }

        return new RoutingPlan { Decisions = decisions };
    }

    /// <summary>
    /// Job-granular routing (Phase 15): routes every project, then selects the SINGLE worker the whole job
    /// executes on. The job runs on the default (Linux) worker when no project needed escalation; otherwise
    /// it runs on the least-specialized native worker that satisfies the COMBINED demonstrated requirements
    /// of every escalated project. The job cannot run (fails closed) when any project has no compatible
    /// worker, or when no single worker covers the union of escalated requirements.
    /// </summary>
    public static JobRoutingResult RouteJob(
        IReadOnlyList<ProjectCapabilityRequirement> requirements,
        IReadOnlyDictionary<string, LinuxEvaluationOutcome> linuxOutcomes,
        WorkerCapability defaultWorker,
        IReadOnlyList<WorkerCapability> nativeWorkers,
        PlatformRoutingPolicy policy)
    {
        var plan = Route(requirements, linuxOutcomes, defaultWorker, nativeWorkers, policy);

        if (plan.AnyUnsupported)
            return new JobRoutingResult
            {
                Plan = plan,
                CanRun = false,
                FailureReason = string.Join("; ", plan.Unsupported.Select(u => $"{u.ProjectId}: {u.Reason}"))
            };

        // Projects Linux could not evaluate. A job with none of these runs on the default worker — this
        // also covers an empty/portable graph (no probe requirements) so it never routes to a native.
        var escalatedRequirements = requirements
            .Where(r => linuxOutcomes.TryGetValue(r.ProjectId, out var o) && !o.Succeeded)
            .Select(r => linuxOutcomes[r.ProjectId].DemonstratedRequirement ?? r.Requirement)
            .ToArray();

        if (escalatedRequirements.Length == 0)
            return new JobRoutingResult
            {
                Plan = plan,
                CanRun = true,
                ExecutionWorker = defaultWorker,
                ExecutionIsDefault = true
            };

        // Some projects escalated. The whole job must run on one worker that satisfies EVERY escalated
        // project's demonstrated requirement (projects Linux handled are portable → satisfied by any).
        var worker = nativeWorkers
            .Where(w => policy.AllowsEscalationTo(w.OperatingSystem) && escalatedRequirements.All(w.Satisfies))
            .OrderBy(Specificity)
            .ThenBy(w => w.OperatingSystem)
            .ThenBy(w => w.Fingerprint, StringComparer.Ordinal)
            .FirstOrDefault();

        if (worker is null)
            return new JobRoutingResult
            {
                Plan = plan,
                CanRun = false,
                FailureReason =
                    "no single available worker satisfies the combined platform requirements of this job " +
                    "graph (Phase-15 routing is job-granular; per-project mixed production is a later phase)."
            };

        return new JobRoutingResult
        {
            Plan = plan,
            CanRun = true,
            ExecutionWorker = worker,
            ExecutionIsDefault = string.Equals(worker.Fingerprint, defaultWorker.Fingerprint, StringComparison.Ordinal)
        };
    }

    /// <summary>
    /// The cache-reuse gate (acceptance criterion 5): whether a snapshot produced under
    /// <paramref name="producedCapability"/> may be reused to satisfy <paramref name="requirement"/>.
    /// Reuse is blocked when the producing capability does not satisfy the requirement — a snapshot built
    /// without, say, the Apple workloads must not be reused as a complete Apple-target result.
    /// </summary>
    public static bool CanReuse(WorkerCapability producedCapability, CapabilityRequirement requirement) =>
        producedCapability.Satisfies(requirement);

    // Among the workers whose OS the policy allows escalating to and which satisfy the requirement, pick
    // the LEAST specialized (fewest advertised extra capabilities), tie-broken deterministically by OS
    // then fingerprint, so routing prefers the smallest sufficient worker and is reproducible.
    private static WorkerCapability? SelectLeastSpecialized(
        IReadOnlyList<WorkerCapability> workers, CapabilityRequirement requirement, PlatformRoutingPolicy policy) =>
        workers
            .Where(w => policy.AllowsEscalationTo(w.OperatingSystem) && w.Satisfies(requirement))
            .OrderBy(Specificity)
            .ThenBy(w => w.OperatingSystem)
            .ThenBy(w => w.Fingerprint, StringComparer.Ordinal)
            .FirstOrDefault();

    private static int Specificity(WorkerCapability w) =>
        w.SdkFeatureBands.Count + w.InstalledWorkloads.Count + w.ReferencePacks.Count
        + w.TargetPlatforms.Count + w.CustomToolLabels.Count;

    private static RoutingDecision Unsupported(
        ProjectCapabilityRequirement project, CapabilityRequirement requirement, string reason) => new()
    {
        ProjectId = project.ProjectId,
        ProjectPath = project.ProjectPath,
        Outcome = RoutingOutcome.Unsupported,
        MissingCapability = requirement,
        Reason = reason
    };

    private static string Describe(CapabilityRequirement r)
    {
        var parts = new List<string>();
        if (r.RequiredOperatingSystem is { } os) parts.Add($"os={os.ToName()}");
        if (!string.IsNullOrEmpty(r.RequiredTargetPlatform)) parts.Add($"platform={r.RequiredTargetPlatform}");
        if (r.RequiredWorkloads.Count > 0) parts.Add($"workloads=[{string.Join(",", r.RequiredWorkloads)}]");
        if (r.RequiredReferencePacks.Count > 0) parts.Add($"refpacks=[{string.Join(",", r.RequiredReferencePacks)}]");
        if (r.RequiredCustomTools.Count > 0) parts.Add($"tools=[{string.Join(",", r.RequiredCustomTools)}]");
        return parts.Count > 0 ? string.Join(", ", parts) : "portable";
    }
}
