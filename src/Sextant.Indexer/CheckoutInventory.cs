namespace Sextant.Indexer;

/// <summary>A submodule declared by a <c>.gitmodules</c> file and whether the checkout actually contains it.</summary>
/// <param name="Path">The submodule path relative to the checkout root, using <c>/</c> separators.</param>
/// <param name="Populated">True when the submodule is checked out: its directory holds a git worktree (<c>.git</c>).</param>
public sealed record DeclaredSubmodule(string Path, bool Populated);

/// <summary>
/// Pure file-system inventory of a checkout for coverage accounting (issue #119): the project files that
/// exist on disk and the submodules the repository declares. Never runs git or MSBuild, so it is safe on a
/// worker that cannot evaluate the checkout and deterministic for a given tree. Every part of the tree the
/// scan could NOT inspect is reported through an optional error sink, so a caller can refuse to call an
/// incomplete scan complete.
/// </summary>
public static class CheckoutInventory
{
    private static readonly HashSet<string> ExcludedSegments =
        new(StringComparer.OrdinalIgnoreCase) { "obj", "bin", ".git" };

    private static readonly HashSet<string> ProjectExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".csproj", ".vbproj", ".fsproj" };

    private const int MaxSubmoduleDepth = 8;

    /// <summary>
    /// Path equality matching the host file system's case semantics: case-insensitive on Windows/macOS,
    /// ordinal on Linux and other case-sensitive platforms. Case-folding on a case-sensitive file system would
    /// collapse two distinct project files (<c>A.csproj</c>/<c>a.csproj</c>) and hide one from coverage.
    /// </summary>
    public static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Depth-first walk yielding every file under <paramref name="root"/> accepted by
    /// <paramref name="include"/>, never descending build-output/VCS directories (obj/bin/.git) or reparse
    /// points (symlinks/junctions), so it can neither cycle nor escape the checkout. A per-directory I/O
    /// error skips that subtree instead of faulting the walk, and is reported to <paramref name="errors"/>
    /// (when supplied) so the skip is never silent.
    /// </summary>
    public static IEnumerable<string> EnumerateFilesPruned(
        string root, Func<string, bool> include, ICollection<string>? errors = null)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch (Exception ex) { files = []; errors?.Add($"{dir}: cannot list files ({ex.GetType().Name})"); }
            foreach (var file in files)
                if (include(file))
                    yield return file;

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch (Exception ex) { subdirs = []; errors?.Add($"{dir}: cannot list directories ({ex.GetType().Name})"); }
            foreach (var subdir in subdirs)
            {
                if (ExcludedSegments.Contains(Path.GetFileName(subdir)))
                    continue;
                try
                {
                    if ((File.GetAttributes(subdir) & FileAttributes.ReparsePoint) != 0)
                        continue;
                }
                catch (Exception ex)
                {
                    errors?.Add($"{subdir}: cannot read attributes ({ex.GetType().Name})");
                    continue;
                }
                stack.Push(subdir);
            }
        }
    }

    /// <summary>
    /// Every recognized project file (<c>.csproj</c>/<c>.vbproj</c>/<c>.fsproj</c>) under
    /// <paramref name="checkoutDir"/>, as full paths in ordinal order. Unreadable subtrees are reported to
    /// <paramref name="errors"/>.
    /// </summary>
    public static IReadOnlyList<string> FindProjectFiles(string checkoutDir, ICollection<string>? errors = null)
    {
        var root = Path.GetFullPath(checkoutDir);
        var found = EnumerateFilesPruned(root, f => ProjectExtensions.Contains(Path.GetExtension(f)), errors)
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .ToList();
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    /// <summary>
    /// The submodules declared by <paramref name="checkoutDir"/>'s <c>.gitmodules</c>, recursing into each
    /// POPULATED submodule's own <c>.gitmodules</c> (bounded depth). An unpopulated submodule cannot reveal
    /// its nested submodules, so only its own entry is reported. Paths are checkout-relative with
    /// <c>/</c> separators, in ordinal order. An unreadable <c>.gitmodules</c>, an entry escaping the
    /// checkout, or a submodule path that traverses a reparse point (symlink/junction) in ANY component is
    /// reported to <paramref name="errors"/>.
    /// </summary>
    public static IReadOnlyList<DeclaredSubmodule> FindDeclaredSubmodules(string checkoutDir, ICollection<string>? errors = null)
    {
        var root = Path.GetFullPath(checkoutDir);
        var result = new List<DeclaredSubmodule>();
        var seen = new HashSet<string>(PathComparer);
        Collect(root, root, depth: 0, result, seen, errors);
        result.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return result;
    }

    private static void Collect(
        string root, string repoDir, int depth, List<DeclaredSubmodule> result, HashSet<string> seen,
        ICollection<string>? errors)
    {
        if (depth > MaxSubmoduleDepth)
        {
            errors?.Add($"{repoDir}: submodule nesting deeper than {MaxSubmoduleDepth} levels was not inspected");
            return;
        }

        foreach (var declared in ReadGitmodulesPaths(repoDir, errors))
        {
            string full;
            try { full = Path.GetFullPath(Path.Combine(repoDir, declared)); }
            catch (Exception ex)
            {
                errors?.Add($"{repoDir}/.gitmodules: invalid submodule path '{declared}' ({ex.GetType().Name})");
                continue;
            }

            var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
            if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            {
                errors?.Add($"{repoDir}/.gitmodules: submodule path '{declared}' escapes the checkout");
                continue;
            }
            if (!seen.Add(relative))
                continue;

            var populated = IsPopulated(root, full, relative, errors);
            result.Add(new DeclaredSubmodule(relative, populated));
            if (populated)
                Collect(root, full, depth + 1, result, seen, errors);
        }
    }

    /// <summary>
    /// The <c>path</c> values of every <c>[submodule "…"]</c> section of <paramref name="repoDir"/>'s
    /// <c>.gitmodules</c>, following git-config syntax: case-insensitive section/key names, quoted values
    /// with <c>\"</c>/<c>\\</c> escapes, and <c>;</c>/<c>#</c> comments outside quotes.
    /// </summary>
    internal static IEnumerable<string> ReadGitmodulesPaths(string repoDir, ICollection<string>? errors = null)
    {
        var gitmodules = Path.Combine(repoDir, ".gitmodules");
        string[] lines;
        try
        {
            if (!File.Exists(gitmodules))
                return [];
            lines = File.ReadAllLines(gitmodules);
        }
        catch (Exception ex)
        {
            errors?.Add($"{gitmodules}: cannot read ({ex.GetType().Name})");
            return [];
        }

        return ParseGitmodulesPaths(lines);
    }

    internal static List<string> ParseGitmodulesPaths(IEnumerable<string> lines)
    {
        var paths = new List<string>();
        var inSubmodule = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#')
                continue;

            if (line[0] == '[')
            {
                var close = line.IndexOf(']');
                var header = close > 0 ? line[1..close].Trim() : string.Empty;
                inSubmodule = header.StartsWith("submodule", StringComparison.OrdinalIgnoreCase)
                    && (header.Length == "submodule".Length || char.IsWhiteSpace(header["submodule".Length]));
                continue;
            }

            if (!inSubmodule)
                continue;

            var eq = line.IndexOf('=');
            if (eq <= 0 || !string.Equals(line[..eq].Trim(), "path", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = ParseConfigValue(line[(eq + 1)..]);
            if (value.Length > 0)
                paths.Add(value);
        }
        return paths;
    }

    // A git-config value: quoted segments may contain spaces/comment characters; outside quotes a ';' or
    // '#' starts a comment; '\' escapes the next character; surrounding whitespace is trimmed.
    private static string ParseConfigValue(string raw)
    {
        var sb = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c == '\\' && i + 1 < raw.Length)
            {
                var next = raw[++i];
                sb.Append(next switch { 'n' => '\n', 't' => '\t', 'b' => '\b', _ => next });
            }
            else if (c == '"')
                quoted = !quoted;
            else if (!quoted && c is ';' or '#')
                break;
            else
                sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    // Populated = a git worktree exists at the declared path: a `.git` file (gitdir link, the normal form for
    // a checked-out submodule) or directory. A merely non-empty directory is not a checked-out submodule.
    // Lexical containment is not physical containment: EVERY component between the checkout root and the
    // submodule (not just the last) must be a real directory, or `link/sub` with `link` a junction outside
    // the checkout would count external content as a populated submodule the project walk never visits.
    private static bool IsPopulated(string root, string dir, string relative, ICollection<string>? errors)
    {
        try
        {
            var current = root;
            foreach (var segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (!Directory.Exists(current))
                    return false;
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    errors?.Add($"{relative}: submodule path traverses a symbolic link or junction and was not inspected");
                    return false;
                }
            }
            var dotGit = Path.Combine(dir, ".git");
            return File.Exists(dotGit) || Directory.Exists(dotGit);
        }
        catch (Exception ex)
        {
            errors?.Add($"{relative}: cannot inspect submodule directory ({ex.GetType().Name})");
            return false;
        }
    }
}
