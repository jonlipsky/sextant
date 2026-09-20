namespace Sextant.Core;

/// <summary>
/// Maps between the absolute source paths Roslyn reports at index time and the repository-relative
/// paths Phase 7 stores once in the <c>files</c> table. Storage keeps a project-root-relative path
/// (forward-slash normalized); a query reconstructs the absolute path from the project's
/// <c>disk_path</c> so no absolute path is ever persisted (acceptance criterion 1).
/// </summary>
/// <remarks>
/// Reconstruction derives the repository (or submodule) root by stripping a project's stored
/// repo-relative csproj path from its stored absolute <c>disk_path</c>. When no disk path is known —
/// synthetic rows in unit-test fixtures never set one — paths are treated as already-relative and
/// pass through byte-for-byte so round-tripping a fixture's stored path is lossless.
/// </remarks>
public static class SourcePaths
{
    /// <summary>
    /// Derives the repository/submodule root from a project's absolute disk path and its stored
    /// repo-relative path by removing the trailing path components the relative path contributes.
    /// Returns null when either input is missing (the synthetic no-root case).
    /// </summary>
    public static string? DeriveRepoRoot(string? diskPath, string? projectRepoRelativePath)
    {
        if (string.IsNullOrEmpty(diskPath) || string.IsNullOrEmpty(projectRepoRelativePath))
            return null;

        var segments = projectRepoRelativePath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return null;

        string? root;
        try { root = Path.GetFullPath(diskPath); }
        catch { return null; }

        for (var i = 0; i < segments.Length && root != null; i++)
            root = Path.GetDirectoryName(root);

        return string.IsNullOrEmpty(root) ? null : root;
    }

    /// <summary>
    /// Converts a source path (absolute as reported by Roslyn, or already relative) to the
    /// forward-slash repo-relative form stored in <c>files.repo_relative_path</c>. With no known root
    /// the path is stored verbatim so a synthetic fixture path round-trips unchanged.
    /// </summary>
    public static string ToRepoRelative(string? repoRoot, string path)
    {
        if (string.IsNullOrEmpty(repoRoot))
            return path;
        if (!Path.IsPathRooted(path))
            return path.Replace('\\', '/');
        try { return Path.GetRelativePath(repoRoot, path).Replace('\\', '/'); }
        catch { return path.Replace('\\', '/'); }
    }

    /// <summary>
    /// Reconstructs an absolute source path from a stored repo-relative path and the project's root.
    /// An already-rooted stored path (synthetic rows) or a null root returns the stored path
    /// unchanged; otherwise the result is the OS-native absolute path, byte-identical to the path
    /// Roslyn originally reported.
    /// </summary>
    public static string ToAbsolute(string? repoRoot, string storedPath)
    {
        if (string.IsNullOrEmpty(repoRoot) || Path.IsPathRooted(storedPath))
            return storedPath;
        try
        {
            return Path.GetFullPath(
                Path.Combine(repoRoot, storedPath.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch { return storedPath; }
    }
}
