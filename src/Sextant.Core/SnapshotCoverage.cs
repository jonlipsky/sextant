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

    /// <summary>How the solution set was chosen: <c>configured</c>, <c>default_root</c>, or <c>none</c>.</summary>
    public string? SelectionSource { get; init; }

    /// <summary>Solutions found on disk (default selection) or listed in <c>sextant.json</c> (configured selection).</summary>
    public int SolutionsDiscovered { get; init; }

    /// <summary>Solutions actually indexed.</summary>
    public int SolutionsSelected { get; init; }

    /// <summary>Solutions discovered on disk that the selection left out.</summary>
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

    /// <summary>True when <see cref="Verdict"/> is <see cref="SnapshotCoverageVerdict.Partial"/>.</summary>
    [JsonIgnore]
    public bool IsPartial => string.Equals(Verdict, SnapshotCoverageVerdict.Partial, StringComparison.Ordinal);
}
