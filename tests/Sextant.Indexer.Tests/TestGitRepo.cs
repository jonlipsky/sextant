using System.Diagnostics;

namespace Sextant.Indexer.Tests;

/// <summary>
/// A throwaway on-disk git repository for tests that exercise real <c>git</c> discovery. Runs the real
/// git binary in a temp directory so <see cref="GitChangeProvider"/> is tested against actual porcelain
/// output rather than a mock. Disposal deletes the tree.
/// </summary>
internal sealed class TestGitRepo : IDisposable
{
    public string Root { get; }

    private TestGitRepo(string root) => Root = root;

    /// <summary>Creates a git repo with an initial commit of the given files, or null if git is unavailable.</summary>
    public static TestGitRepo? TryCreate(IReadOnlyDictionary<string, string> initialFiles)
    {
        var root = Path.Combine(Path.GetTempPath(), $"sextant_gittest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var repo = new TestGitRepo(root);
        try
        {
            if (repo.Run("init -b main") == null) { repo.Dispose(); return null; }
            repo.Run("config user.email test@example.com");
            repo.Run("config user.name Test");
            repo.Run("config commit.gpgsign false");
            foreach (var (relPath, content) in initialFiles)
                repo.Write(relPath, content);
            repo.Run("add -A");
            if (repo.Run("commit -m initial") == null) { repo.Dispose(); return null; }
            return repo;
        }
        catch
        {
            repo.Dispose();
            return null;
        }
    }

    public void Write(string relPath, string content)
    {
        var full = Path.Combine(Root, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public void Delete(string relPath) =>
        File.Delete(Path.Combine(Root, relPath.Replace('/', Path.DirectorySeparatorChar)));

    public string? Run(string args)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi);
        if (process == null) return null;
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output : null;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }
}
