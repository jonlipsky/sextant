using System.Diagnostics;
using System.Text.RegularExpressions;
using Sextant.Core;

namespace Sextant.Indexer;

public static partial class SubmoduleDiscovery
{
    [GeneratedRegex(@"^[\s+-]([0-9a-f]+)\s+(\S+)")]
    private static partial Regex SubmoduleStatusPattern();

    /// <summary>
    /// Discovers all git submodules in a repository by running git submodule status --recursive.
    /// Returns submodule info with path, pinned commit SHA, and remote URL.
    /// </summary>
    public static async Task<List<SubmoduleInfo>> DiscoverAsync(string repoRoot)
    {
        var results = new List<SubmoduleInfo>();

        var statusOutput = await RunGitAsync(repoRoot, "submodule status --recursive");
        if (string.IsNullOrWhiteSpace(statusOutput))
            return results;

        foreach (var line in statusOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var match = SubmoduleStatusPattern().Match(line);
            if (!match.Success)
                continue;

            var commitSha = match.Groups[1].Value;
            var submodulePath = match.Groups[2].Value;
            var fullPath = Path.Combine(repoRoot, submodulePath);

            // Get the submodule's own remote URL
            var remoteUrl = await GetSubmoduleRemoteUrl(fullPath);
            if (string.IsNullOrWhiteSpace(remoteUrl))
                continue;

            var normalizedUrl = GitRemoteNormalizer.Normalize(remoteUrl);

            results.Add(new SubmoduleInfo
            {
                Path = submodulePath,
                CommitSha = commitSha,
                RemoteUrl = normalizedUrl
            });
        }

        return results;
    }

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
