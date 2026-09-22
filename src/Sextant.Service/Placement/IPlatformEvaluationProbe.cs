using Sextant.Core.Platform;

namespace Sextant.Service.Placement;

/// <summary>
/// Discovers, for one ensure-snapshot request, each project's capability requirement AND its DEMONSTRATED
/// outcome of being evaluated on the default (Linux) worker (Phase 15). Routing keys off this observed
/// outcome, never the TFM string alone: a probe reports a project INSUFFICIENT only when Linux genuinely
/// could not evaluate it (a missing SDK/workload/reference pack surfaced as a load failure), so an
/// ordinary <c>net8.0-windows</c> class library that Linux evaluates fine is never needlessly routed.
///
/// The default (<see cref="AssumeLinuxCapableProbe"/>) reports success for everything — the correct
/// behaviour on the Linux service for the common/portable case — while <see cref="SolutionEvaluationProbe"/>
/// derives real per-project outcomes from a loaded workspace's diagnostics. Tests substitute a fake probe
/// to exercise the escalation + fail-closed paths on a Linux CI (acceptance criterion 6).
/// </summary>
public interface IPlatformEvaluationProbe
{
    Task<ProbeResult> ProbeAsync(EnsureSnapshotRequest request, string scratchDir, CancellationToken cancellationToken);
}

/// <summary>The discovered requirements + demonstrated Linux outcomes for a request's project graph.</summary>
public sealed record ProbeResult
{
    /// <summary>Per-project capability requirements (portable unless a non-portable target was discovered).</summary>
    public required IReadOnlyList<ProjectCapabilityRequirement> Requirements { get; init; }

    /// <summary>Per-project (by project id) demonstrated Linux evaluation outcome.</summary>
    public required IReadOnlyDictionary<string, LinuxEvaluationOutcome> LinuxOutcomes { get; init; }

    /// <summary>False when no checkout/solution could be located to probe (→ the job is unsupported).</summary>
    public bool CheckoutAvailable { get; init; } = true;

    /// <summary>Why the checkout was unavailable, when <see cref="CheckoutAvailable"/> is false.</summary>
    public string? UnavailableReason { get; init; }

    /// <summary>An empty portable graph — the trivial "nothing to route" result.</summary>
    public static ProbeResult Empty { get; } = new()
    {
        Requirements = [],
        LinuxOutcomes = new Dictionary<string, LinuxEvaluationOutcome>()
    };

    /// <summary>A probe result for a missing checkout — the job cannot proceed.</summary>
    public static ProbeResult Unavailable(string reason) => new()
    {
        Requirements = [],
        LinuxOutcomes = new Dictionary<string, LinuxEvaluationOutcome>(),
        CheckoutAvailable = false,
        UnavailableReason = reason
    };
}

/// <summary>
/// A minimal, MSBuild-free description of one evaluated project, so the Linux-outcome derivation can be
/// unit-tested as a PURE function without a real Roslyn/MSBuild load (<see cref="LinuxEvaluationAnalyzer"/>).
/// A real probe builds these from a loaded workspace; tests construct them directly.
/// </summary>
public sealed record ProjectEvaluationInfo
{
    /// <summary>A stable project identifier (canonical id or repo-relative path).</summary>
    public required string ProjectId { get; init; }

    /// <summary>The project's repo-relative path, when known.</summary>
    public string? ProjectPath { get; init; }

    /// <summary>The declared target platform (the <c>-windows</c>/<c>-ios</c>/… TFM suffix), or null.</summary>
    public string? TargetPlatform { get; init; }

    /// <summary>Whether the project LOADED into the workspace at all (a hard failure when false).</summary>
    public bool Loaded { get; init; } = true;

    /// <summary>
    /// Diagnostic messages from the load that indicate a platform/SDK/workload the Linux worker could not
    /// satisfy (e.g. a missing workload import). Empty for a clean Linux evaluation. Their PRESENCE — not
    /// the TFM string — is what demonstrates insufficiency.
    /// </summary>
    public IReadOnlyList<string> MissingCapabilityDiagnostics { get; init; } = [];
}

/// <summary>
/// The PURE derivation of a project's <see cref="LinuxEvaluationOutcome"/> from its
/// <see cref="ProjectEvaluationInfo"/> (Phase 15). Kept free of MSBuild/Roslyn so the "route on
/// demonstrated success, not TFM name" rule is exhaustively unit-testable: a project that loaded cleanly
/// is Linux-SUCCEEDED even with a <c>-windows</c> TFM; a project that failed to load, or loaded with a
/// missing-capability diagnostic, is Linux-INSUFFICIENT and carries the requirement demonstrated by the
/// failure (its target platform's owning OS + any named workloads).
/// </summary>
public static class LinuxEvaluationAnalyzer
{
    public static LinuxEvaluationOutcome Analyze(ProjectEvaluationInfo project)
    {
        // A project that never loaded is a hard Linux failure; attribute it to its declared platform.
        if (!project.Loaded)
        {
            var requirement = CapabilityRequirement.ForTargetPlatform(
                project.TargetPlatform,
                RequirementSource.DemonstratedFailure,
                reason: $"project failed to load on the Linux worker");
            return LinuxEvaluationOutcome.Insufficient(requirement, "project failed to load on the Linux worker");
        }

        // A project that loaded but surfaced a missing-capability diagnostic is demonstrably insufficient,
        // regardless of TFM. The diagnostics are the DEMONSTRATION; the TFM only names the owning OS.
        if (project.MissingCapabilityDiagnostics.Count > 0)
        {
            var reason = string.Join("; ", project.MissingCapabilityDiagnostics);
            var requirement = CapabilityRequirement.ForTargetPlatform(
                project.TargetPlatform, RequirementSource.DemonstratedFailure, reason: reason);
            return LinuxEvaluationOutcome.Insufficient(requirement, reason);
        }

        // Loaded cleanly → Linux evaluated it, even if the TFM mentions -windows/-ios. No escalation.
        return LinuxEvaluationOutcome.Success;
    }

    /// <summary>Builds a full <see cref="ProbeResult"/> from a set of evaluated projects.</summary>
    public static ProbeResult ToProbeResult(IEnumerable<ProjectEvaluationInfo> projects)
    {
        var requirements = new List<ProjectCapabilityRequirement>();
        var outcomes = new Dictionary<string, LinuxEvaluationOutcome>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            var outcome = Analyze(project);
            outcomes[project.ProjectId] = outcome;
            requirements.Add(new ProjectCapabilityRequirement
            {
                ProjectId = project.ProjectId,
                ProjectPath = project.ProjectPath,
                Requirement = outcome.DemonstratedRequirement
                    ?? CapabilityRequirement.ForTargetPlatform(project.TargetPlatform)
            });
        }
        return new ProbeResult { Requirements = requirements, LinuxOutcomes = outcomes };
    }
}

/// <summary>
/// The DEFAULT probe: it reports every project as Linux-SUCCEEDED without loading anything. This is the
/// correct, zero-cost behaviour for the common case on the Linux service (a portable/ordinary graph the
/// default worker evaluates fine) and guarantees no needless native routing (acceptance criterion 1). A
/// deployment that provisions native workers registers <see cref="SolutionEvaluationProbe"/> (or a
/// ProcessStack-provided probe in Phase 14) to detect genuine Linux insufficiency.
/// </summary>
public sealed class AssumeLinuxCapableProbe : IPlatformEvaluationProbe
{
    public Task<ProbeResult> ProbeAsync(
        EnsureSnapshotRequest request, string scratchDir, CancellationToken cancellationToken) =>
        Task.FromResult(ProbeResult.Empty);
}
