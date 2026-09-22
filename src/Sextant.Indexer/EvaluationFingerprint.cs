using System.Security.Cryptography;
using System.Text;

namespace Sextant.Indexer;

/// <summary>
/// Computes a per-project fingerprint over the evaluation inputs that shape a compilation but are not
/// themselves .cs files: the project file, the Directory.Build.props/targets and analyzer/editor config
/// chains, global.json, and the restored package assets. Startup catch-up compares this against the
/// stored fingerprint so a change to any of them escalates the project to a rebuild even when every
/// source byte is unchanged (Phase 4, acceptance criterion 6).
/// </summary>
public static class EvaluationFingerprint
{
    private static readonly string[] WalkUpFileNames =
    [
        "Directory.Build.props",
        "Directory.Build.targets",
        "global.json",
        ".editorconfig",
        ".globalconfig",
    ];

    /// <summary>
    /// Returns a stable hex fingerprint of the project's evaluation inputs, or null when the project
    /// file is absent on disk (e.g. an in-memory ad-hoc workspace) and no fingerprint can be taken.
    /// </summary>
    public static string? Compute(string? projectFilePath, string? repoRoot)
    {
        if (string.IsNullOrEmpty(projectFilePath) || !File.Exists(projectFilePath))
            return null;

        var projectFull = Path.GetFullPath(projectFilePath);
        var projectDir = Path.GetDirectoryName(projectFull);
        var stop = string.IsNullOrEmpty(repoRoot) ? null : Path.GetFullPath(repoRoot);

        // Collect input files keyed by absolute path so duplicates (a config file seen once while
        // walking up) collapse to a single entry.
        var inputs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { projectFull };

        var current = projectDir;
        var guard = 0;
        while (current != null && guard++ < 64)
        {
            foreach (var name in WalkUpFileNames)
            {
                var candidate = Path.Combine(current, name);
                if (File.Exists(candidate))
                    inputs.Add(Path.GetFullPath(candidate));
            }

            if (stop != null && PathEquals(current, stop))
                break;

            var parent = Path.GetDirectoryName(current);
            if (parent == null || PathEquals(parent, current))
                break;
            current = parent;
        }

        if (projectDir != null)
        {
            var assets = Path.Combine(projectDir, "obj", "project.assets.json");
            if (File.Exists(assets)) inputs.Add(Path.GetFullPath(assets));
            var lockFile = Path.Combine(projectDir, "packages.lock.json");
            if (File.Exists(lockFile)) inputs.Add(Path.GetFullPath(lockFile));
        }

        var sb = new StringBuilder();
        foreach (var path in inputs)
        {
            var key = stop != null && path.StartsWith(stop, StringComparison.OrdinalIgnoreCase)
                ? path[stop.Length..].Replace('\\', '/')
                : Path.GetFileName(path);
            sb.Append(key).Append('=').Append(HashFile(path)).Append('\n');
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(digest);
    }

    private static string HashFile(string path)
    {
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        }
        catch (IOException)
        {
            return "unreadable";
        }
        catch (UnauthorizedAccessException)
        {
            return "unreadable";
        }
    }

    private static bool PathEquals(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
