namespace Sextant.Indexer;

/// <summary>How a checkout's indexed solution set was chosen (recorded in provenance, issue #109).</summary>
public enum SolutionSelectionSource
{
    /// <summary>No solution could be selected (no config match and none discovered).</summary>
    None,

    /// <summary>An explicit per-repo <c>solutions</c> list (from <c>sextant.json</c>) selected the set.</summary>
    Configured,

    /// <summary>No explicit config; a single deterministic, preferably Linux-loadable root solution was chosen.</summary>
    DefaultRoot
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
/// order. Populated only for <see cref="SolutionSelectionSource.DefaultRoot"/> (and
/// <see cref="SolutionSelectionSource.None"/>); empty when an explicit config drove selection. Lets the
/// worker RECORD which discovered solutions were NOT selected instead of silently ignoring them.
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
///   <item>With no config, all <c>.slnx</c>/<c>.sln</c> under the checkout are discovered and ONE
///   deterministic default is chosen — preferring a root-level, Linux-loadable solution (e.g. a
///   <c>*-no-macos.slnx</c>-style root) so a Linux worker indexes the broadest loadable slice. A total
///   ordering (with a path-ordinal final tiebreak) makes the choice stable across runs regardless of
///   filesystem enumeration order.</item>
/// </list>
///
/// The selection is a pure function of the checkout's file tree + its committed <c>sextant.json</c> — both
/// pinned by the commit — so it is stable across runs for a given commit (criterion 2).
/// </summary>
public static class SolutionSelector
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    // Directory segments whose contents are never a repo's real solution set (build output / VCS).
    private static readonly HashSet<string> ExcludedSegments =
        new(StringComparer.OrdinalIgnoreCase) { "obj", "bin", ".git" };

    // Name fragments that mark a solution head as EXPLICITLY Linux-loadable — preferred as the default.
    private static readonly string[] PreferLinuxMarkers = ["no-macos", "no-mac", "linux", "server"];

    // Name fragments that mark a cross-platform head that will NOT load on a Linux worker (iOS/Android/
    // Mac/Windows/Unity heads) — deprioritized as the default. Routing them to a native worker is #89.
    private static readonly string[] PlatformHeadMarkers =
        ["maccatalyst", "macos", "ios", "tvos", "android", "windows", "winui", "wpf", "unity", "tizen", "mac"];

    /// <summary>
    /// Selects the solution set for <paramref name="checkoutDir"/>. When
    /// <paramref name="configuredSolutions"/> is non-empty it is authoritative (each entry resolved
    /// relative to the checkout unless already an absolute path inside it); otherwise a single
    /// deterministic default root solution is chosen from what is discovered on disk.
    /// </summary>
    public static SolutionSelection Select(string checkoutDir, IReadOnlyList<string>? configuredSolutions)
    {
        var root = Path.GetFullPath(checkoutDir);

        if (configuredSolutions is { Count: > 0 })
            return SelectConfigured(root, configuredSolutions);

        var discovered = DiscoverSolutions(root);
        if (discovered.Count == 0)
            return new SolutionSelection([], SolutionSelectionSource.None, [], []);

        // A total ordering with a path-ordinal final tiebreak → a single, stable default.
        var best = discovered.OrderBy(p => RankKey(root, p), RankComparer.Instance).First();
        return new SolutionSelection([best], SolutionSelectionSource.DefaultRoot, [], discovered);
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
    /// directories excluded), de-duplicated and returned in a stable ordinal-by-repo-relative-path order.
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
        foreach (var match in EnumerateSolutionFilesPruned(root))
        {
            var full = Path.GetFullPath(match);
            if (IsExcluded(root, full) || !seen.Add(full))
                continue;
            found.Add(full);
        }

        found.Sort((a, b) => string.CompareOrdinal(RepoRelative(root, a), RepoRelative(root, b)));
        return found;
    }

    /// <summary>
    /// Depth-first walk that yields the recognized solution files under <paramref name="root"/> while never
    /// descending an excluded (obj/bin/.git) or reparse-point (symlink/junction) directory — so the walk can
    /// neither cycle nor escape the checkout. A per-directory I/O error (e.g. permission denied) skips that
    /// one subtree instead of faulting discovery, mirroring the old <c>IgnoreInaccessible</c> behavior.
    /// </summary>
    private static IEnumerable<string> EnumerateSolutionFilesPruned(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { files = []; }
            foreach (var file in files)
                if (IsRecognizedSolution(file))
                    yield return file;

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch { subdirs = []; }
            foreach (var subdir in subdirs)
            {
                if (ExcludedSegments.Contains(Path.GetFileName(subdir)))
                    continue;
                try
                {
                    if ((File.GetAttributes(subdir) & FileAttributes.ReparsePoint) != 0)
                        continue;
                }
                catch
                {
                    continue; // cannot stat the directory — do not descend into it
                }
                stack.Push(subdir);
            }
        }
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
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // The last segment is the file name; only directory segments gate exclusion.
        for (var i = 0; i < segments.Length - 1; i++)
            if (ExcludedSegments.Contains(segments[i]))
                return true;
        return false;
    }

    // The deterministic ranking key for the default choice: prefer shallow (root) solutions, then
    // explicitly-Linux over neutral over platform-head names, then .slnx over .sln, and finally an
    // ordinal path tiebreak so the choice is total and stable regardless of enumeration order.
    private static (int Depth, int PlatformRank, int ExtRank, string Path) RankKey(string root, string solutionPath)
    {
        var relative = RepoRelative(root, solutionPath);
        var depth = relative.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar);
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

    private static string RepoRelative(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath);
        return relative;
    }

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
