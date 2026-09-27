namespace Sextant.Indexer;

/// <summary>How a checkout's indexed solution set was chosen (recorded in provenance, issue #109).</summary>
public enum SolutionSelectionSource
{
    /// <summary>No solution could be selected (no config match and none discovered).</summary>
    None,

    /// <summary>An explicit per-repo <c>solutions</c> list (from <c>sextant.json</c>) selected the set.</summary>
    Configured,

    /// <summary>
    /// No explicit config; the deterministic UNION of every discovered solution was selected (issue #124).
    /// Replaces the historical single-solution <c>DefaultRoot</c> pick — coverage rows recorded before
    /// #124 may still carry the wire name <c>default_root</c>.
    /// </summary>
    DefaultUnion
}

/// <summary>
/// A configured solution that could not be selected, together with the reason. Surfacing these (rather
/// than silently dropping a mis-typed/missing entry) is what lets the service report PARTIAL coverage
/// instead of quietly indexing a subset (issue #109).
/// </summary>
/// <param name="RequestedPath">The raw entry as it appeared in the configuration.</param>
/// <param name="Reason">A concise human-readable reason the entry was skipped.</param>
public sealed record SkippedSolution(string RequestedPath, string Reason);

/// <summary>
/// The deterministic outcome of selecting which solution(s) to index for a checkout.
/// </summary>
/// <param name="SolutionPaths">The absolute solution paths to index, in a stable deterministic order.</param>
/// <param name="Source">How the set was chosen.</param>
/// <param name="SkippedSolutions">Configured entries that were skipped, with reasons.</param>
/// <param name="DiscoveredSolutions">
/// Every solution discovered under the checkout during a default (unconfigured) selection, in stable
/// ordinal-by-repo-relative-path order. Populated only for <see cref="SolutionSelectionSource.DefaultUnion"/>
/// (and <see cref="SolutionSelectionSource.None"/>); empty when an explicit config drove selection. Under
/// the default union every discovered solution is also selected, so "discovered but not selected" is empty
/// by construction; the worker still derives it defensively so a future narrowing selector could never
/// silently hide a gap (issue #119).
/// </param>
public sealed record SolutionSelection(
    IReadOnlyList<string> SolutionPaths,
    SolutionSelectionSource Source,
    IReadOnlyList<SkippedSolution> SkippedSolutions,
    IReadOnlyList<string> DiscoveredSolutions)
{
    /// <summary>True when at least one solution was selected to index.</summary>
    public bool HasSolutions => SolutionPaths.Count > 0;
}

/// <summary>
/// Chooses which solution(s) a checkout should index, EXPLICITLY and DETERMINISTICALLY (issue #109). This
/// replaces the historical nondeterministic "first-enumerated <c>.slnx</c>/<c>.sln</c>" pick that indexed
/// one arbitrary solution of a multi-solution monorepo and silently ignored the rest.
///
/// <list type="number">
///   <item>An explicit per-repo <c>solutions</c> list (from the checkout's own <c>sextant.json</c>)
///   selects that exact set, in listed order; a listed entry that is missing / not a solution / escapes
///   the checkout is recorded as a <see cref="SkippedSolution"/> rather than dropped.</item>
///   <item>With no config, all <c>.slnx</c>/<c>.sln</c> under the checkout are discovered and ALL of them
///   are selected — the deterministic UNION (issue #124), loaded as one workspace by
///   <see cref="MultiSolutionLoader"/> so a project shared by several solutions is evaluated once per
///   target framework. Platform-head solutions are included: their loadable projects are indexed and an
///   unloadable head is skipped-with-reason by the loader's per-project fault isolation, so coverage is
///   maximal AND honestly partial. The union is ordered by a total ranking (shallow first, then
///   explicitly-Linux, neutral, platform heads last; <c>.slnx</c> before <c>.sln</c>; a
///   separator-normalized ordinal path tiebreak) so it is stable across runs and operating systems
///   regardless of filesystem enumeration order, the first entry is the historical "best" default, and
///   fragile platform heads load last.</item>
/// </list>
///
/// The selection is a pure function of the checkout's file tree + its committed <c>sextant.json</c> — both
/// pinned by the commit — so it is stable across runs for a given commit (criterion 2).
/// </summary>
public static class SolutionSelector
{
    // Host-aware (issue #124 review): case-insensitive on Windows/macOS, ordinal on case-sensitive file systems,
    // so two distinct solution files differing only by case on Linux are both discovered/selected rather than
    // one silently hidden from the union and from coverage.
    private static StringComparer PathComparer => CheckoutInventory.PathComparer;

    // Directory segments whose contents are never a repo's real solution set (build output / VCS).
    private static readonly HashSet<string> ExcludedSegments =
        new(StringComparer.OrdinalIgnoreCase) { "obj", "bin", ".git" };

    // Name fragments that mark a solution head as EXPLICITLY Linux-loadable — ordered first in the union.
    private static readonly string[] PreferLinuxMarkers = ["no-macos", "no-mac", "linux", "server"];

    // Name fragments that mark a cross-platform head that will NOT load on a Linux worker (iOS/Android/
    // Mac/Windows/Unity heads) — still selected, but ordered last in the union so the broadly-loadable
    // solutions are loaded first. Routing them to a native worker is #89.
    private static readonly string[] PlatformHeadMarkers =
        ["maccatalyst", "macos", "ios", "tvos", "android", "windows", "winui", "wpf", "unity", "tizen", "mac"];

    /// <summary>
    /// Selects the solution set for <paramref name="checkoutDir"/>. When
    /// <paramref name="configuredSolutions"/> is non-empty it is authoritative (each entry resolved
    /// relative to the checkout unless already an absolute path inside it); otherwise EVERY solution
    /// discovered on disk is selected, in the deterministic union order of <see cref="OrderForUnion"/>.
    /// </summary>
    public static SolutionSelection Select(string checkoutDir, IReadOnlyList<string>? configuredSolutions)
    {
        var root = Path.GetFullPath(checkoutDir);

        if (configuredSolutions is { Count: > 0 })
            return SelectConfigured(root, configuredSolutions);

        var discovered = DiscoverSolutions(root);
        if (discovered.Count == 0)
            return new SolutionSelection([], SolutionSelectionSource.None, [], []);

        return new SolutionSelection(OrderForUnion(root, discovered), SolutionSelectionSource.DefaultUnion, [], discovered);
    }

    /// <summary>
    /// Orders discovered solutions for the default union: a TOTAL ordering (depth, platform rank,
    /// extension, then a separator-normalized ordinal repo-relative path) so the result depends only on
    /// the SET of paths — never on the input/enumeration order or the host's directory separator. The
    /// union's first-appearance project order (and hence the persisted project order) follows from it.
    /// </summary>
    internal static IReadOnlyList<string> OrderForUnion(string checkoutDir, IEnumerable<string> solutionPaths)
    {
        var root = Path.GetFullPath(checkoutDir);
        return solutionPaths
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .OrderBy(p => RankKey(root, p), RankComparer.Instance)
            .ToList();
    }

    private static SolutionSelection SelectConfigured(string root, IReadOnlyList<string> configured)
    {
        var selected = new List<string>();
        var seen = new HashSet<string>(PathComparer);
        var skipped = new List<SkippedSolution>();

        foreach (var entry in configured)
        {
            var resolved = ResolveConfigured(root, entry);
            if (resolved is null)
            {
                skipped.Add(new SkippedSolution(entry, "configured solution path is invalid"));
                continue;
            }
            if (!IsContainedIn(root, resolved))
            {
                skipped.Add(new SkippedSolution(entry, "configured solution resolves outside the checkout"));
                continue;
            }
            if (!IsRecognizedSolution(resolved))
            {
                skipped.Add(new SkippedSolution(entry, "configured entry is not a .sln/.slnx solution file"));
                continue;
            }
            if (!File.Exists(resolved))
            {
                skipped.Add(new SkippedSolution(entry, "configured solution not found in checkout"));
                continue;
            }
            if (seen.Add(resolved))
                selected.Add(resolved);
        }

        return new SolutionSelection(selected, SolutionSelectionSource.Configured, skipped, []);
    }

    /// <summary>
    /// Discovers every recognized solution under <paramref name="checkoutDir"/> (build-output and VCS
    /// directories excluded), de-duplicated and returned in a stable ordinal order of the '/'-normalized
    /// repo-relative path (identical on every OS).
    /// </summary>
    public static IReadOnlyList<string> DiscoverSolutions(string checkoutDir)
    {
        var root = Path.GetFullPath(checkoutDir);
        var found = new List<string>();
        var seen = new HashSet<string>(PathComparer);

        // Walk the tree ourselves, PRUNING build-output/VCS subtrees (obj/bin/.git) before descending them,
        // rather than filtering them out after a full recursive enumeration. A monorepo's .git object store
        // alone can be enormous, so descending it on every ensure is wasted I/O; EnumerationOptions exposes
        // no per-directory predicate, so a manual stack walk is the only way to skip a subtree ENTIRELY.
        // Recognized solutions are de-duplicated and returned in a stable ordinal-by-repo-relative-path order
        // so discovery is deterministic regardless of the walk order.
        foreach (var match in CheckoutInventory.EnumerateFilesPruned(root, IsRecognizedSolution))
        {
            var full = Path.GetFullPath(match);
            if (IsExcluded(root, full) || !seen.Add(full))
                continue;
            found.Add(full);
        }

        found.Sort((a, b) => string.CompareOrdinal(RepoRelative(root, a), RepoRelative(root, b)));
        return found;
    }

    private static string? ResolveConfigured(string root, string entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
            return null;
        try
        {
            var combined = Path.IsPathRooted(entry) ? entry : Path.Combine(root, entry);
            return Path.GetFullPath(combined);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsRecognizedSolution(string path)
    {
        var ext = Path.GetExtension(path);
        return string.Equals(ext, ".slnx", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".sln", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExcluded(string root, string solutionPath)
    {
        var relative = RepoRelative(root, solutionPath);
        var segments = relative.Split('/');
        // The last segment is the file name; only directory segments gate exclusion.
        for (var i = 0; i < segments.Length - 1; i++)
            if (ExcludedSegments.Contains(segments[i]))
                return true;
        return false;
    }

    // The deterministic ranking key for the union order: shallow (root) solutions first, then
    // explicitly-Linux over neutral over platform-head names, then .slnx over .sln, and finally an ordinal
    // tiebreak on the '/'-normalized repo-relative path so the order is total and identical on every OS.
    private static (int Depth, int PlatformRank, int ExtRank, string Path) RankKey(string root, string solutionPath)
    {
        var relative = RepoRelative(root, solutionPath);
        var depth = relative.Count(c => c == '/');
        var extRank = string.Equals(Path.GetExtension(solutionPath), ".slnx", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        return (depth, PlatformRank(solutionPath), extRank, relative);
    }

    // 0 = explicitly Linux-loadable, 1 = neutral (no platform hint), 2 = a platform head unlikely to load
    // on Linux. Explicit-Linux markers are checked first so a "*-no-macos" root beats the "mac"/"macos"
    // platform-head substring it also contains.
    private static int PlatformRank(string solutionPath)
    {
        var name = Path.GetFileNameWithoutExtension(solutionPath).ToLowerInvariant();
        foreach (var marker in PreferLinuxMarkers)
            if (name.Contains(marker, StringComparison.Ordinal))
                return 0;
        foreach (var marker in PlatformHeadMarkers)
            if (name.Contains(marker, StringComparison.Ordinal))
                return 2;
        return 1;
    }

    // The repo-relative path with the host directory separator normalized to '/', so ordering keys (and
    // the ordinal discovery sort) are identical on Windows and Linux: with '\' a sibling "a0" dir would
    // sort BEFORE "a\..." on Windows ('0' < '\') but AFTER "a/..." on Linux ('/' < '0'). Only the HOST
    // separator is rewritten — on Linux a '\' is a legal file-name character and must not be
    // reinterpreted as a directory boundary.
    private static string RepoRelative(string root, string fullPath) =>
        ToSlashSeparated(Path.GetRelativePath(root, fullPath), Path.DirectorySeparatorChar);

    // Pure so the Windows branch is testable on any OS (the CI gate runs on Linux, where it is a no-op).
    internal static string ToSlashSeparated(string relative, char hostSeparator) =>
        hostSeparator == '/' ? relative : relative.Replace(hostSeparator, '/');

    // True when <paramref name="candidate"/> is the root itself or a descendant of it, computed on the
    // normalized full paths so "..", traversal, and separator differences cannot escape the checkout.
    private static bool IsContainedIn(string root, string candidate)
    {
        var rel = Path.GetRelativePath(root, candidate);
        return rel != ".."
            && !rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !rel.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(rel);
    }

    private sealed class RankComparer : IComparer<(int Depth, int PlatformRank, int ExtRank, string Path)>
    {
        public static readonly RankComparer Instance = new();

        public int Compare(
            (int Depth, int PlatformRank, int ExtRank, string Path) x,
            (int Depth, int PlatformRank, int ExtRank, string Path) y)
        {
            var c = x.Depth.CompareTo(y.Depth);
            if (c != 0) return c;
            c = x.PlatformRank.CompareTo(y.PlatformRank);
            if (c != 0) return c;
            c = x.ExtRank.CompareTo(y.ExtRank);
            if (c != 0) return c;
            return string.CompareOrdinal(x.Path, y.Path);
        }
    }
}
