using System.Text.Json.Serialization;

namespace Sextant.Core;

/// <summary>The coverage verdicts a <see cref="SnapshotCoverage"/> can carry (migration 022 <c>snapshot_coverage.verdict</c>).</summary>
public static class SnapshotCoverageVerdict
{
    /// <summary>Every discovered solution, declared project, and declared submodule of the checkout was indexed.</summary>
    public const string Complete = "complete";

    /// <summary>At least one coverage gap exists; <see cref="SnapshotCoverage.Reasons"/> says which.</summary>
    public const string Partial = "partial";
}

/// <summary>
/// A machine-readable summary of how much of a checkout a published snapshot actually covers (issue #119).
/// A snapshot row's <c>complete</c> status only means "published and servable"; this record says whether
/// the published data covers the whole repository or only part of it, and why. It is computed by the
/// service worker from the solution selection and the multi-solution load BEFORE indexing, persisted in the
/// SAME transaction that publishes the snapshot (migration 022), and surfaced on ensure/status/resolve, on
/// the snapshot symbol page, and in MCP <c>meta.snapshot</c>. Serialized snake_case.
/// </summary>
public sealed record SnapshotCoverage
{
    /// <summary><see cref="SnapshotCoverageVerdict.Complete"/> or <see cref="SnapshotCoverageVerdict.Partial"/>.</summary>
    public required string Verdict { get; init; }

    /// <summary>One human-readable reason per coverage gap; empty when <see cref="Verdict"/> is complete.</summary>
    public IReadOnlyList<string> Reasons { get; init; } = [];

    /// <summary>
    /// How the solution set was chosen: <c>configured</c>, <c>default_union</c> (no config: every discovered
    /// solution, issue #124), or <c>none</c>. Rows recorded before #124 may carry the retired single-solution
    /// value <c>default_root</c>.
    /// </summary>
    public string? SelectionSource { get; init; }

    /// <summary>Solutions found on disk (default selection) or listed in <c>sextant.json</c> (configured selection).</summary>
    public int SolutionsDiscovered { get; init; }

    /// <summary>Solutions actually indexed.</summary>
    public int SolutionsSelected { get; init; }

    /// <summary>
    /// Solutions discovered on disk that the selection left out. Always 0 under <c>default_union</c> (every
    /// discovered solution is selected) and <c>configured</c> (no discovery); non-zero only on pre-#124
    /// <c>default_root</c> rows.
    /// </summary>
    public int SolutionsNotSelected { get; init; }

    /// <summary>Configured solutions that could not be used (missing, invalid, outside the checkout).</summary>
    public int SolutionsSkipped { get; init; }

    /// <summary>Distinct project files declared across the selected solutions.</summary>
    public int ProjectsDeclared { get; init; }

    /// <summary>
    /// Distinct project files in the loaded workspace — declared projects that loaded plus any the load
    /// pulled in through <c>ProjectReference</c> (so it can exceed <see cref="ProjectsDeclared"/>).
    /// </summary>
    public int ProjectsLoaded { get; init; }

    /// <summary>Declared project files that failed to load on this worker.</summary>
    public int ProjectsSkipped { get; init; }

    /// <summary>Project files (<c>.csproj</c>/<c>.fsproj</c>/<c>.vbproj</c>) found on disk, excluding build output.</summary>
    public int ProjectFilesOnDisk { get; init; }

    /// <summary>On-disk project files that no selected solution declared and the load did not pull in.</summary>
    public int ProjectFilesUnreferenced { get; init; }

    /// <summary>Submodules declared by <c>.gitmodules</c> (recursively, through populated submodules).</summary>
    public int SubmodulesDeclared { get; init; }

    /// <summary>Declared submodules that are not checked out (no git worktree at the declared path).</summary>
    public int SubmodulesUnpopulated { get; init; }

    /// <summary>
    /// Parts of the checkout the coverage scan could not inspect (unreadable directories or
    /// <c>.gitmodules</c>, entries escaping the checkout). Any error makes the verdict partial, because the
    /// scan cannot prove nothing was missed.
    /// </summary>
    public int ScanErrors { get; init; }

    /// <summary>
    /// The unsatisfiable <c>global.json</c> SDK pins the worker temporarily neutralized so the checkout could
    /// be evaluated with an installed SDK (issue #113). Null when no pin was overridden (omitted from the
    /// JSON, so records written before #113 read back unchanged). An override does NOT make the verdict
    /// partial — every project still loaded — but it is provenance an operator must be able to see: the
    /// snapshot was built with a substituted SDK.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<SdkPinOverride>? SdkPinOverrides { get; init; }

    /// <summary>True when <see cref="Verdict"/> is <see cref="SnapshotCoverageVerdict.Partial"/>.</summary>
    [JsonIgnore]
    public bool IsPartial => string.Equals(Verdict, SnapshotCoverageVerdict.Partial, StringComparison.Ordinal);
}

/// <summary>
/// One <c>global.json</c> SDK pin the service worker overrode because hostfxr could not satisfy it on this
/// worker (issue #113) — e.g. <c>"version": "10.0.300", "rollForward": "disable"</c> on a node that only
/// has 10.0.401. Serialized snake_case inside <see cref="SnapshotCoverage.SdkPinOverrides"/>.
/// </summary>
public sealed record SdkPinOverride
{
    /// <summary>The overridden <c>global.json</c>, relative to the checkout root (forward slashes).</summary>
    public required string GlobalJsonPath { get; init; }

    /// <summary>The SDK version the pin requested (<c>sdk.version</c>), when present.</summary>
    public string? RequestedVersion { get; init; }

    /// <summary>The pin's <c>sdk.rollForward</c> policy, when present.</summary>
    public string? RollForward { get; init; }

    /// <summary>The installed SDK hostfxr resolved once the pin was neutralized (the SDK actually used).</summary>
    public string? ResolvedSdkVersion { get; init; }

    /// <summary>The SDK versions installed on the worker, newest first.</summary>
    public IReadOnlyList<string> InstalledSdks { get; init; } = [];
}
