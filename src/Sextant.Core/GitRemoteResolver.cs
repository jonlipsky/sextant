using System.Diagnostics;

namespace Sextant.Core;

public static class GitRemoteResolver
{
    public static string? ResolveGitRoot(string startPath)
    {
        var dir = Directory.Exists(startPath)
            ? new DirectoryInfo(startPath)
            : new FileInfo(startPath).Directory;

        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                File.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        return null;
    }

    public static string? ReadOriginRemote(string gitRoot)
    {
        try
        {
            var psi = new ProcessStartInfo("git", "remote get-url origin")
            {
                WorkingDirectory = gitRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return null;

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();

            return process.ExitCode == 0 && !string.IsNullOrEmpty(output)
                ? output
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static ProjectIdentity Resolve(string csprojPath)
    {
        var fullPath = Path.GetFullPath(csprojPath);
        var gitRoot = ResolveGitRoot(fullPath);

        string gitRemoteUrl;
        string repoRelativePath;

        if (gitRoot != null)
        {
            var rawRemote = ReadOriginRemote(gitRoot);
            gitRemoteUrl = rawRemote != null
                ? GitRemoteNormalizer.Normalize(rawRemote)
                : $"local://{Environment.MachineName}";

            repoRelativePath = Path.GetRelativePath(gitRoot, fullPath)
                .Replace('\\', '/');
        }
        else
        {
            gitRemoteUrl = $"local://{Environment.MachineName}";
            repoRelativePath = Path.GetFileName(fullPath);
        }

        var canonicalId = CanonicalIdGenerator.Generate(gitRemoteUrl, repoRelativePath);

        return new ProjectIdentity
        {
            CanonicalId = canonicalId,
            GitRemoteUrl = gitRemoteUrl,
            RepoRelativePath = repoRelativePath,
            DiskPath = fullPath
        };
    }

    /// <summary>
    /// Resolves project identity for a project inside a submodule, using the submodule's own remote URL.
    /// The repo_relative_path is relative to the submodule root, not the parent repo root.
    /// </summary>
    public static ProjectIdentity ResolveForSubmodule(string csprojPath, string submoduleRemoteUrl, string submoduleRootPath)
    {
        var fullPath = Path.GetFullPath(csprojPath);
        var subRoot = Path.GetFullPath(submoduleRootPath);

        var repoRelativePath = Path.GetRelativePath(subRoot, fullPath)
            .Replace('\\', '/');

        var canonicalId = CanonicalIdGenerator.Generate(submoduleRemoteUrl, repoRelativePath);

        return new ProjectIdentity
        {
            CanonicalId = canonicalId,
            GitRemoteUrl = submoduleRemoteUrl,
            RepoRelativePath = repoRelativePath,
            DiskPath = fullPath
        };
    }
}
