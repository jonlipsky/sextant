using System.Diagnostics;
using System.Text.RegularExpressions;
using Sextant.Core;

namespace Sextant.Indexer;

public static partial class SubmoduleDiscovery
{
    [GeneratedRegex(@"^([+\-U ]?)([0-9a-f]{7,})\s+(\S+)")]
    private static partial Regex SubmoduleStatusPattern();

    /// <summary>
    /// Discovers all git submodules in a repository by running git submodule status --recursive.
    /// Returns submodule info with path, pinned commit SHA, and remote URL.
    /// </summary>
    public static async Task<List<SubmoduleInfo>> DiscoverAsync(string repoRoot)
    {
        var results = new List<SubmoduleInfo>();

        // Fast path: submodules are defined in a top-level .gitmodules file. When it is absent the repo
        // has no submodules and `git submodule status --recursive` would return nothing anyway, so skip
        // the subprocess entirely — on git-for-windows that recursive submodule call is ~2s of helper
        // bootstrap, and it otherwise runs on every incremental/overlay reindex of a submodule-free repo.
        if (!File.Exists(Path.Combine(repoRoot, ".gitmodules")))
            return results;

        var statusOutput = await RunGitAsync(repoRoot, "submodule status --recursive");
        if (string.IsNullOrWhiteSpace(statusOutput))
            return results;

        foreach (var line in statusOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parsed = ParseStatusLine(line);
            if (!parsed.Matched)
                continue;

            var commitSha = parsed.CommitSha;
            var submodulePath = parsed.Path;
            var fullPath = Path.Combine(repoRoot, submodulePath);
            var isDirty = parsed.IsDirty;

            // Get the submodule's own remote URL
            var remoteUrl = await GetSubmoduleRemoteUrl(fullPath);
            if (string.IsNullOrWhiteSpace(remoteUrl))
                continue;

            var normalizedUrl = GitRemoteNormalizer.Normalize(remoteUrl);

            results.Add(new SubmoduleInfo
            {
                Path = submodulePath,
                CommitSha = commitSha,
                RemoteUrl = normalizedUrl,
                IsDirty = isDirty
            });
        }

        return results;
    }

    /// <summary>
    /// Parses a single <c>git submodule status --recursive</c> line into its pinned commit, path, and
    /// dirty state. The leading prefix column encodes clean vs dirty: <c>' '</c> = checked out exactly at
    /// the recorded pin (clean); <c>'+'</c> = a DIFFERENT commit is checked out than the pin (or a dirty
    /// worktree); <c>'-'</c> = uninitialized; <c>'U'</c> = merge conflicts. Anything but a space means the
    /// parent's working tree does not match the clean pinned commit (issue #48), so it must not be treated
    /// as the clean pin. The prefix is matched OPTIONALLY because the raw git output is trimmed before it
    /// reaches here, and trimming strips a clean line's leading SPACE (a dirty marker is non-whitespace and
    /// always survives) — so an ABSENT prefix is the trimmed clean case and is classified clean, never
    /// dropped. Pure and git-free so the classification is unit-testable without a real submodule.
    /// </summary>
    public static SubmoduleStatusLine ParseStatusLine(string line)
    {
        var match = SubmoduleStatusPattern().Match(line);
        if (!match.Success)
            return new SubmoduleStatusLine(false, string.Empty, string.Empty, false);

        var statusPrefix = match.Groups[1].Value;
        return new SubmoduleStatusLine(
            Matched: true,
            CommitSha: match.Groups[2].Value,
            Path: match.Groups[3].Value,
            // Only the three explicit dirty markers are dirty; a space OR an absent (trimmed-away) prefix
            // is the clean pinned commit. Treating an empty prefix as dirty would misclassify every clean
            // single-submodule line, whose leading space is removed by the upstream output trim.
            IsDirty: statusPrefix is "+" or "-" or "U");
    }

    /// <summary>The parsed shape of one <c>git submodule status</c> line (see <see cref="ParseStatusLine"/>).</summary>
    public readonly record struct SubmoduleStatusLine(bool Matched, string CommitSha, string Path, bool IsDirty);

    /// <summary>
    /// Discovers .csproj files within a submodule directory.
    /// Returns paths relative to the submodule root.
    /// </summary>
    public static List<(string absolutePath, string relativeToSubmodule)> DiscoverProjects(string submoduleFullPath)
    {
        var results = new List<(string, string)>();
        if (!Directory.Exists(submoduleFullPath))
            return results;

        foreach (var csproj in Directory.EnumerateFiles(submoduleFullPath, "*.csproj", SearchOption.AllDirectories))
        {
            // Skip obj/bin directories
            var relativePath = Path.GetRelativePath(submoduleFullPath, csproj);
            if (relativePath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                relativePath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                relativePath.StartsWith($"obj{Path.DirectorySeparatorChar}") ||
                relativePath.StartsWith($"bin{Path.DirectorySeparatorChar}"))
                continue;

            results.Add((csproj, relativePath));
        }

        return results;
    }

    /// <summary>
    /// Checks whether a given path is inside a submodule directory.
    /// </summary>
    public static bool IsPathInSubmodule(string filePath, IReadOnlyList<SubmoduleInfo> submodules, string repoRoot)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        foreach (var sub in submodules)
        {
            var subFullPath = Path.GetFullPath(Path.Combine(repoRoot, sub.Path));
            if (normalizedPath.StartsWith(subFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Finds the submodule that contains a given path, if any.
    /// </summary>
    public static SubmoduleInfo? FindContainingSubmodule(string filePath, IReadOnlyList<SubmoduleInfo> submodules, string repoRoot)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        foreach (var sub in submodules)
        {
            var subFullPath = Path.GetFullPath(Path.Combine(repoRoot, sub.Path));
            if (normalizedPath.StartsWith(subFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return sub;
        }
        return null;
    }

    private static async Task<string> GetSubmoduleRemoteUrl(string submodulePath)
    {
        return await RunGitAsync(submodulePath, "remote get-url origin");
    }

    private static async Task<string> RunGitAsync(string workingDirectory, string arguments)
    {
        if (!Directory.Exists(workingDirectory))
            return string.Empty;

        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var process = Process.Start(psi);
            if (process == null)
                return string.Empty;

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            return process.ExitCode == 0 ? output.Trim() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
