using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Sextant.Indexer;

namespace Sextant.Service.Placement;

/// <summary>
/// The real (best-effort) Linux-evaluation probe: it locates the provisioned checkout, loads the solution
/// on the default (Linux) worker, and derives each project's <see cref="LinuxEvaluationOutcome"/> from what
/// the workspace actually reported — a project that failed to load, or loaded with a workspace-failure
/// diagnostic, is demonstrably insufficient; a project that loaded cleanly is Linux-succeeded regardless of
/// its TFM (Phase 15). The DECISION lives in the pure, unit-tested <see cref="LinuxEvaluationAnalyzer"/>;
/// this class only feeds it MSBuild-derived facts, so no test needs a real native toolchain.
///
/// Precise per-project attribution of a solution-level MSBuild failure is inherently fuzzy, so this probe
/// is intentionally conservative (a failure it cannot attribute leaves projects Linux-succeeded rather
/// than routing needlessly); richer capability detection is refined when real native workers land
/// (Phase 14). A deployment with no native workers can leave the default <see cref="AssumeLinuxCapableProbe"/>
/// in place.
/// </summary>
public sealed class SolutionEvaluationProbe(ICheckoutProvider checkoutProvider) : IPlatformEvaluationProbe
{
    public async Task<ProbeResult> ProbeAsync(
        EnsureSnapshotRequest request, string scratchDir, CancellationToken cancellationToken)
    {
        if (!checkoutProvider.TryResolve(request, out var resolution))
            return ProbeResult.Unavailable(
                $"No provisioned checkout with a solution found for '{request.RepositoryRemoteUrl}'.");

        // Config-error state (issue #109): the checkout resolved but its own sextant.json expressed a
        // scoping intent that could not be honored, so no solution was selected. Nothing to probe — surface
        // the reason as unavailable rather than loading an empty union.
        if (!resolution.HasSelectedSolutions)
            return ProbeResult.Unavailable(
                resolution.ConfigurationError
                ?? $"No indexable solution could be selected for '{request.RepositoryRemoteUrl}'.");

        // WorkspaceFailed can be raised from multiple MSBuild worker threads during the load, so the
        // diagnostic sink must be thread-safe.
        var diagnostics = new ConcurrentQueue<string>();
        MultiSolutionLoadResult load;
        try
        {
            // Probe the UNION of the deterministically-selected solutions (#109) so platform-aware routing
            // sees every project across every selected solution, not one arbitrary solution's slice.
            load = await MultiSolutionLoader.LoadAsync(
                resolution.SelectedSolutions, onDiagnostic: d => diagnostics.Enqueue(d), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A hard load failure of the whole solution is treated as unavailable rather than a false
            // "everything is fine" — the routing worker fails the job closed with this reason.
            return ProbeResult.Unavailable(
                $"failed to load solution '{resolution.PrimarySolution}': {ex.Message}");
        }

        var solution = load.Solution;

        // Snapshot the concurrent sink once the load has completed and no more diagnostics can arrive.
        var loadDiagnostics = diagnostics.ToArray();
        var infos = new List<ProjectEvaluationInfo>();
        foreach (var project in solution.Projects)
        {
            var path = project.FilePath;
            var fileName = path is null ? null : Path.GetFileName(path);

            infos.Add(new ProjectEvaluationInfo
            {
                ProjectId = project.Name,
                ProjectPath = path,
                TargetPlatform = ParseTargetPlatform(project.Name),
                Loaded = true,
                MissingCapabilityDiagnostics = AttributeDiagnostics(fileName, loadDiagnostics)
            });
        }

        // A project DECLARED in a selected solution but skipped-with-reason (e.g. a platform head that will
        // not load on Linux) never appears in solution.Projects — surface it as an explicitly not-loaded
        // project so the analyzer can route it rather than silently omitting it from the probe (#109/#89).
        foreach (var skipped in load.SkippedProjects)
        {
            infos.Add(new ProjectEvaluationInfo
            {
                ProjectId = Path.GetFileNameWithoutExtension(skipped.ProjectPath),
                ProjectPath = skipped.ProjectPath,
                TargetPlatform = ParseTargetPlatform(Path.GetFileNameWithoutExtension(skipped.ProjectPath)),
                Loaded = false,
                MissingCapabilityDiagnostics = [skipped.Reason]
            });
        }

        return LinuxEvaluationAnalyzer.ToProbeResult(infos);
    }

    // Selects the load diagnostics that mention a project's file name, attributing a solution-level
    // failure to the project it names. Pure so it can be unit-tested without an MSBuild load.
    internal static string[] AttributeDiagnostics(string? projectFileName, IEnumerable<string> diagnostics) =>
        projectFileName is null
            ? []
            : diagnostics.Where(d => d.Contains(projectFileName, StringComparison.OrdinalIgnoreCase)).ToArray();

    // Extracts a target-platform token (e.g. "windows", "ios") from a project name via the ONE canonical
    // TFM parser (<see cref="Sextant.Core.Platform.TargetFrameworkFacts"/>, issue #65) — "Foo
    // (net8.0-windows)" → "windows", "Foo (net8.0-windows10.0.19041)" → "windows". Returns null when the
    // name carries no parenthesized platform-specific TFM (a single-TFM/plain name like "Acme-Cli" must NOT
    // be mis-parsed to a bogus platform "cli", and a portable "Foo (net8.0)" is platform-agnostic). Sharing
    // the parser with the contribution manifest builder keeps route-time and contribution-time capability
    // detection consistent. This is a display-only hint that only NAMES the owning OS — it never by itself
    // triggers routing; a demonstrated load failure does.
    internal static string? ParseTargetPlatform(string projectName) =>
        Sextant.Core.Platform.TargetFrameworkFacts.Parse(projectName).Platform;
}
