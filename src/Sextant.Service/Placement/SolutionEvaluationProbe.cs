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
        if (!checkoutProvider.TryResolve(request, out _, out var solutionPath))
            return ProbeResult.Unavailable(
                $"No provisioned checkout with a solution found for '{request.RepositoryRemoteUrl}'.");

        // WorkspaceFailed can be raised from multiple MSBuild worker threads during the load, so the
        // diagnostic sink must be thread-safe.
        var diagnostics = new ConcurrentQueue<string>();
        Solution solution;
        try
        {
            solution = await SolutionLoader.LoadSolutionAsync(
                solutionPath, onDiagnostic: d => diagnostics.Enqueue(d), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A hard load failure of the whole solution is treated as unavailable rather than a false
            // "everything is fine" — the routing worker fails the job closed with this reason.
            return ProbeResult.Unavailable($"failed to load solution '{solutionPath}': {ex.Message}");
        }

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

        return LinuxEvaluationAnalyzer.ToProbeResult(infos);
    }

    // Selects the load diagnostics that mention a project's file name, attributing a solution-level
    // failure to the project it names. Pure so it can be unit-tested without an MSBuild load.
    internal static string[] AttributeDiagnostics(string? projectFileName, IEnumerable<string> diagnostics) =>
        projectFileName is null
            ? []
            : diagnostics.Where(d => d.Contains(projectFileName, StringComparison.OrdinalIgnoreCase)).ToArray();

    // Extracts a target-platform token (e.g. "windows", "ios") from the multi-TFM display suffix of a
    // project name — "Foo (net8.0-windows)" → "windows". Returns null when the name carries no
    // parenthesized TFM (a single-TFM/plain name like "Acme-Cli" must NOT be mis-parsed to a bogus
    // platform "cli"). This is a display-only hint that only NAMES the owning OS — it never by itself
    // triggers routing; a demonstrated load failure does.
    internal static string? ParseTargetPlatform(string projectName)
    {
        var open = projectName.LastIndexOf('(');
        var close = projectName.LastIndexOf(')');
        if (open < 0 || close <= open)
            return null;
        var tfm = projectName[(open + 1)..close];
        var dash = tfm.IndexOf('-');
        if (dash < 0 || dash + 1 >= tfm.Length)
            return null;
        var platform = tfm[(dash + 1)..].Trim();
        // Strip a trailing platform version (net8.0-windows10.0.19041 → windows).
        var end = 0;
        while (end < platform.Length && !char.IsDigit(platform[end])) end++;
        var name = platform[..end].Trim().ToLowerInvariant();
        return name.Length > 0 ? name : null;
    }
}
