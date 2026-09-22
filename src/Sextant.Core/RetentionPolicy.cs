namespace Sextant.Core;

/// <summary>
/// Retention limits for bounded, historical index data (Phase 8, criteria 4 &amp; 5). These are the
/// desired keep-counts; the retention service intersects them with a protected set (the currently
/// servable generation and any snapshots referenced by protected branches, open pull requests,
/// submodule pins, or active overlays) so a keep-count can only ever delete data that is both
/// superseded and unprotected. Loaded from the <c>retention</c> object in <c>sextant.json</c> with
/// environment overrides, mirroring the rest of <see cref="SextantConfiguration"/>.
/// </summary>
public sealed class RetentionPolicy
{
    /// <summary>Default value of <see cref="KeepCompleteGenerations"/>.</summary>
    public const int DefaultKeepCompleteGenerations = 3;

    /// <summary>Default value of <see cref="ApiSnapshotKeepCommits"/>.</summary>
    public const int DefaultApiSnapshotKeepCommits = 10;

    /// <summary>
    /// How many complete local generations (<c>index_runs</c> rows) to keep, newest first. The
    /// currently-servable last-complete generation is always protected regardless of this count.
    /// Default 3.
    /// </summary>
    public int KeepCompleteGenerations { get; set; } = DefaultKeepCompleteGenerations;

    /// <summary>
    /// How many distinct historical commits of API-surface snapshots to keep, newest first. Snapshots
    /// for a protected commit are never deleted even if they fall outside this window. Default 10.
    /// </summary>
    public int ApiSnapshotKeepCommits { get; set; } = DefaultApiSnapshotKeepCommits;

    /// <summary>
    /// Whether to prune source blobs (<c>file_versions</c>) that are no longer referenced by any live
    /// symbol/occurrence/comment and are not protected. Default true.
    /// </summary>
    public bool PruneSupersededSourceBlobs { get; set; } = true;

    /// <summary>
    /// Defends against a misconfigured negative keep-count. A negative value is treated as "unset"
    /// and falls back to the safe default rather than collapsing to <c>0</c> — which is itself a
    /// valid, deliberately-aggressive "keep only the servable generation" policy — so a stray
    /// <c>-1</c> can never silently become the most destructive setting.
    /// </summary>
    public RetentionPolicy Normalized() => new()
    {
        KeepCompleteGenerations =
            KeepCompleteGenerations < 0 ? DefaultKeepCompleteGenerations : KeepCompleteGenerations,
        ApiSnapshotKeepCommits =
            ApiSnapshotKeepCommits < 0 ? DefaultApiSnapshotKeepCommits : ApiSnapshotKeepCommits,
        PruneSupersededSourceBlobs = PruneSupersededSourceBlobs
    };
}
