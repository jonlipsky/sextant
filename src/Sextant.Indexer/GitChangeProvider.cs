using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Sextant.Indexer;

/// <summary>How a working-tree path differs from the committed base (issue #43, criterion 2).</summary>
public enum WorkingTreeChangeKind
{
    /// <summary>Tracked-and-added (staged new file) or otherwise present-vs-base as an addition.</summary>
    Added,

    /// <summary>Content modified relative to the base commit (staged and/or unstaged).</summary>
    Modified,

    /// <summary>Removed relative to the base commit (staged and/or unstaged deletion).</summary>
    Deleted,

    /// <summary>Renamed relative to the base commit (carries <see cref="WorkingTreeChange.OldRepoRelativePath"/>).</summary>
    Renamed,

    /// <summary>Present on disk but not tracked by git (a brand-new, un-added file).</summary>
    Untracked
}

/// <summary>
/// One working-tree difference from the committed base, in repo-relative terms. Paths are always
/// repo-relative (never absolute disk paths — the Phase-7 identity rule); <see cref="AbsolutePath"/>
/// resolves against the repo root for the (few) places that must touch disk.
/// </summary>
public sealed record WorkingTreeChange
{
    public required string RepoRelativePath { get; init; }
    public required WorkingTreeChangeKind Kind { get; init; }

    /// <summary>For a rename, the pre-rename repo-relative path; null otherwise.</summary>
    public string? OldRepoRelativePath { get; init; }

    /// <summary>The absolute on-disk path (repo root + repo-relative), for the rare disk-touching caller.</summary>
    public required string AbsolutePath { get; init; }
}

/// <summary>
/// The authoritative, rename-aware set of working-tree differences from the committed base, discovered
/// from a single machine-readable <c>git status --porcelain=v2 -z</c> pass. This is the AUTHORITATIVE
/// reconciliation input (criterion 3): the file watcher's events are only hints, and a restart
/// reconstructs the same set purely from git state — never from missed watcher events. When
/// <see cref="Changes"/> is empty the working tree is clean vs the base (criterion 1 → empty overlay).
/// </summary>
public sealed record WorkingTreeChangeSet
{
    public required IReadOnlyList<WorkingTreeChange> Changes { get; init; }

    /// <summary>True when the working tree exactly matches the committed base (no overlay needed).</summary>
    public bool IsClean => Changes.Count == 0;

    /// <summary>
    /// Every changed path that must be handed to the closure computation as a "touched" file, in
    /// absolute-disk form. Includes rename source and destination (both owning projects must rebuild)
    /// and deletions (whose owning projects must purge stale rows). Deduplicated, order-stable.
    /// </summary>
    public IReadOnlyList<string> TouchedAbsolutePaths()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var change in Changes)
        {
            AddUnique(result, seen, change.AbsolutePath);
            if (change.Kind == WorkingTreeChangeKind.Renamed && change.OldRepoRelativePath != null)
                AddUnique(result, seen, Path.Combine(RepoRoot, change.OldRepoRelativePath.Replace('/', Path.DirectorySeparatorChar)));
        }
        return result;
    }

    /// <summary>The repo root the change set was discovered under (absolute).</summary>
    public required string RepoRoot { get; init; }

    /// <summary>
    /// A stable digest over the sorted (repo-relative-path, kind, content-hash-or-marker) tuples of the
    /// working-tree changes (issue #43). Ties the overlay's identity to the ACTUAL dirty content, so an
    /// identical working-tree state always yields the identical digest (criterion 3 determinism) and a
    /// dirty tree is never mis-identified as the clean base commit's snapshot. Returns null for a clean
    /// tree (no delta → the base identity is used unchanged).
    /// </summary>
    public string? ComputeDeltaDigest()
    {
        if (Changes.Count == 0) return null;

        var lines = new List<string>(Changes.Count);
        foreach (var change in Changes)
        {
            var contentMarker = change.Kind == WorkingTreeChangeKind.Deleted
                ? "deleted"
                : SafeContentHash(change.AbsolutePath);
            var renamePart = change.OldRepoRelativePath is { } old ? $"<-{old}" : string.Empty;
            lines.Add($"{change.RepoRelativePath}{renamePart}|{change.Kind}|{contentMarker}");
        }
        lines.Sort(StringComparer.Ordinal);

        var preimage = string.Join("\n", lines);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(preimage));
        return Convert.ToHexStringLower(hash);
    }

    private static string SafeContentHash(string absolutePath)
    {
        try
        {
            if (!File.Exists(absolutePath)) return "missing";
            var bytes = File.ReadAllBytes(absolutePath);
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        catch
        {
            return "unreadable";
        }
    }

    private static void AddUnique(List<string> result, HashSet<string> seen, string path)
    {
        if (seen.Add(path)) result.Add(path);
    }
}

/// <summary>
/// Discovers working-tree differences from git in a machine-readable, rename-aware way. Startup and
/// periodic reconciliation call this to reconstruct the authoritative change set independent of any
/// file-watcher events (criterion 3). Uses <c>git status --porcelain=v2 -z</c> for a single pass that
/// reports staged, unstaged, renamed, deleted, and untracked entries with exact rename detection.
/// </summary>
public static class GitChangeProvider
{
    /// <summary>
    /// Reads the working-tree change set for <paramref name="repoRoot"/>, or null when git is
    /// unavailable / the directory is not a work tree (the caller then treats the tree as
    /// non-reconcilable and falls back). A clean tree returns an empty (not null) change set.
    /// </summary>
    public static WorkingTreeChangeSet? TryGetChangeSet(string repoRoot)
    {
        var output = RunGitRaw(repoRoot, "status --porcelain=v2 -z --untracked-files=all --renames");
        if (output == null) return null;

        var changes = ParsePorcelainV2(output, repoRoot);
        return new WorkingTreeChangeSet { Changes = changes, RepoRoot = repoRoot };
    }

    internal static IReadOnlyList<WorkingTreeChange> ParsePorcelainV2(string output, string repoRoot)
    {
        var changes = new List<WorkingTreeChange>();
        // -z NUL-separates records; a type-2 (rename/copy) record is followed by a second NUL-separated
        // field carrying the original path, so we walk the token list with a manual cursor.
        var tokens = output.Split('\0');
        for (var i = 0; i < tokens.Length; i++)
        {
            var record = tokens[i];
            if (record.Length == 0) continue;

            var marker = record[0];
            switch (marker)
            {
                case '1':
                {
                    // "1 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <path>"
                    var (xy, path) = SplitOrdinary(record);
                    if (path == null) break;
                    changes.Add(Make(repoRelative: path, kind: ClassifyXy(xy), oldPath: null, repoRoot));
                    break;
                }
                case '2':
                {
                    // "2 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <X><score> <path>" then NUL <origPath>
                    var (xy, path) = SplitRename(record);
                    string? origPath = null;
                    if (i + 1 < tokens.Length)
                    {
                        origPath = tokens[i + 1];
                        i++; // consume the original-path field
                    }
                    if (path == null) break;
                    var kind = xy.Contains('R') ? WorkingTreeChangeKind.Renamed : ClassifyXy(xy);
                    changes.Add(Make(repoRelative: path, kind, oldPath: origPath, repoRoot));
                    break;
                }
                case 'u':
                {
                    // Unmerged (conflict): treat as modified so the owning project is rebuilt.
                    var (_, path) = SplitOrdinary(record);
                    if (path == null) break;
                    changes.Add(Make(repoRelative: path, WorkingTreeChangeKind.Modified, oldPath: null, repoRoot));
                    break;
                }
                case '?':
                {
                    // "? <path>" — untracked
                    var path = record.Length > 2 ? record[2..] : null;
                    if (string.IsNullOrEmpty(path)) break;
                    changes.Add(Make(repoRelative: path, WorkingTreeChangeKind.Untracked, oldPath: null, repoRoot));
                    break;
                }
                // '!' ignored files are intentionally skipped.
            }
        }
        return changes;
    }

    private static WorkingTreeChange Make(string repoRelative, WorkingTreeChangeKind kind, string? oldPath, string repoRoot)
    {
        var normalized = repoRelative.Replace('\\', '/');
        var absolute = Path.Combine(repoRoot, normalized.Replace('/', Path.DirectorySeparatorChar));
        return new WorkingTreeChange
        {
            RepoRelativePath = normalized,
            Kind = kind,
            OldRepoRelativePath = oldPath?.Replace('\\', '/'),
            AbsolutePath = absolute
        };
    }

    // The XY field is the two status chars after the leading marker+space. For type 1 the path is the
    // 9th space-separated field; splitting on space with a cap preserves paths that contain spaces.
    private static (string xy, string? path) SplitOrdinary(string record)
    {
        // record = "1 XY sub mH mI mW hH hI path..."
        var parts = record.Split(' ', 9);
        if (parts.Length < 9) return (parts.Length > 1 ? parts[1] : "..", null);
        return (parts[1], parts[8]);
    }

    private static (string xy, string? path) SplitRename(string record)
    {
        // record = "2 XY sub mH mI mW hH hI Xscore path..."
        var parts = record.Split(' ', 10);
        if (parts.Length < 10) return (parts.Length > 1 ? parts[1] : "..", null);
        return (parts[1], parts[9]);
    }

    private static WorkingTreeChangeKind ClassifyXy(string xy)
    {
        // XY: X = index (staged) status, Y = worktree (unstaged) status. Prefer a deletion/addition
        // signal from either column; otherwise it is a modification.
        if (xy.Contains('D')) return WorkingTreeChangeKind.Deleted;
        if (xy.Contains('A')) return WorkingTreeChangeKind.Added;
        if (xy.Contains('R')) return WorkingTreeChangeKind.Renamed;
        return WorkingTreeChangeKind.Modified;
    }

    private static string? RunGitRaw(string repoRoot, string args)
    {
        try
        {
            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using var process = Process.Start(psi);
            if (process == null) return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
