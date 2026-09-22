using Sextant.Core.Platform;

namespace Sextant.Service.Contributions;

/// <summary>
/// The Phase-16 assembly FINALIZE completeness/topology gate (issue #70). Before slice 2, a finalize
/// published the assembled snapshot Complete with no topological-completeness check, so a materially
/// incomplete assembly — a contributor that declared a project version partial/unsupported, or an
/// assembled graph with a dangling reference to a SAME-REPOSITORY project version that was never
/// contributed — was silently published Complete. This gate decides whether a finalize may publish
/// Complete or must publish Partial: a materially-incomplete assembly is ALWAYS Partial, NEVER silently
/// Complete (Phase 17 criterion 3 — reconciliation/publication never records a not-provably-complete
/// snapshot as Complete).
///
/// Cross-repository (provider) references are intentionally NOT treated as incompleteness here: those are
/// resolved through Phase-12 <c>snapshot_dependencies</c> and their edge-stitching is tracked separately
/// (#72, deferred). Only references to a project version KNOWN to belong to THIS repository but absent from
/// the assembled snapshot count as material incompleteness, so a legitimate cross-repo assembly still
/// publishes Complete.
/// </summary>
public static class AssemblyFinalizeGate
{
    /// <summary>The outcome of the finalize gate: whether to publish Complete, and why not when Partial.</summary>
    public readonly record struct Result(bool IsComplete, IReadOnlyList<string> Reasons)
    {
        public static Result Complete { get; } = new(true, []);
    }

    /// <summary>
    /// Evaluates the finalize manifest against what was actually assembled.
    /// <paramref name="anyContributionIncomplete"/> is true when any contribution that fed the snapshot
    /// declared itself non-complete (durable per-contribution completeness — issue #70).
    /// <paramref name="assembledCanonicalIds"/> is the set of logical project canonical ids present in the
    /// assembled snapshot. <paramref name="knownRepositoryCanonicalIds"/> is every logical project canonical
    /// id the catalog has ever seen for THIS repository (used to classify a referenced key as intra-repo vs
    /// a cross-repo provider reference). Returns whether the snapshot may be published Complete.
    /// </summary>
    public static Result Evaluate(
        ContributionManifest manifest,
        bool anyContributionIncomplete,
        IReadOnlySet<string> assembledCanonicalIds,
        IReadOnlySet<string> knownRepositoryCanonicalIds)
    {
        var reasons = new List<string>();

        if (anyContributionIncomplete)
            reasons.Add("at least one contribution that fed this snapshot declared itself non-complete (partial/unsupported).");

        // Any project THIS finalize manifest itself declares non-complete is also material incompleteness
        // even when its own contribution row has not yet been recorded at the moment of the gate call.
        foreach (var project in manifest.Projects)
            if (!string.Equals(project.Completeness, "complete", StringComparison.OrdinalIgnoreCase))
                reasons.Add($"project '{project.CanonicalId}' declares completeness '{project.Completeness}'.");

        // Topology: a referenced project-version key that belongs to THIS repository but is not present in
        // the assembled snapshot is a dangling intra-repo edge — the assembly is missing a project the repo
        // has, so find-references / cross-project queries would be materially incomplete. Cross-repo keys
        // (not known to this repo) are provider references (#72) and are NOT counted.
        var dangling = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in manifest.Projects)
            foreach (var referenced in project.ReferencedProjectVersionKeys)
                if (!assembledCanonicalIds.Contains(referenced)
                    && knownRepositoryCanonicalIds.Contains(referenced))
                    dangling.Add(referenced);

        foreach (var key in dangling)
            reasons.Add($"assembled snapshot is missing intra-repository project version '{key}' referenced by the project graph.");

        return reasons.Count == 0 ? Result.Complete : new Result(false, reasons);
    }
}
