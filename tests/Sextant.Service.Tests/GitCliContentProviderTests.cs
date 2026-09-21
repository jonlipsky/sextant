using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Sextant.Service.Contributions;

namespace Sextant.Service.Tests;

/// <summary>
/// Phase 17 / #68 — the REAL git-content provider. Builds a throwaway on-disk git repository, commits a
/// file, and asserts <see cref="GitCliContentProvider"/> verifies a declared blob against the repository's
/// actual content at the exact commit: a matching raw-SHA-256 fingerprint or git-OID fingerprint verifies
/// (Match), a tampered hash or a path absent at the commit is a Mismatch, and an unknown repo/commit is
/// Unavailable (so the policy — not the provider — decides whether that is fatal). Skipped when git is not
/// on PATH so the suite stays hermetic on machines without git.
/// </summary>
[TestClass]
public class GitCliContentProviderTests
{
    private string _repoDir = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        if (!GitAvailable())
            Assert.Inconclusive("git is not available on PATH; skipping the real git-content provider test.");
        _repoDir = Path.Combine(Path.GetTempPath(), $"sextant_git_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repoDir);
        Git("init", "-q");
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "Test");
        Git("config", "commit.gpgsign", "false");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (!string.IsNullOrEmpty(_repoDir) && Directory.Exists(_repoDir))
        {
            try { Directory.Delete(_repoDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [TestMethod]
    public void VerifyBlob_MatchingRawSha256Fingerprint_IsMatch()
    {
        var content = "public class Widget { }\n"u8.ToArray();
        var commit = CommitFile("src/Widget.cs", content);
        var provider = new GitCliContentProvider(_ => _repoDir);

        var rawSha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        Assert.AreEqual(GitContentCheck.Match,
            provider.VerifyBlob("https://example/repo", commit, "src/Widget.cs", rawSha256));
    }

    [TestMethod]
    public void VerifyBlob_MatchingGitBlobOid_IsMatch()
    {
        var commit = CommitFile("src/Widget.cs", "public class Widget { }\n"u8.ToArray());
        var oid = Git("rev-parse", $"{commit}:src/Widget.cs").Trim();
        var provider = new GitCliContentProvider(_ => _repoDir);

        Assert.AreEqual(GitContentCheck.Match,
            provider.VerifyBlob("https://example/repo", commit, "src/Widget.cs", oid));
    }

    [TestMethod]
    public void VerifyBlob_TamperedHash_IsMismatch()
    {
        var commit = CommitFile("src/Widget.cs", "public class Widget { }\n"u8.ToArray());
        var provider = new GitCliContentProvider(_ => _repoDir);

        var wrong = Convert.ToHexStringLower(SHA256.HashData("tampered"u8.ToArray()));
        Assert.AreEqual(GitContentCheck.Mismatch,
            provider.VerifyBlob("https://example/repo", commit, "src/Widget.cs", wrong));
    }

    [TestMethod]
    public void VerifyBlob_PathAbsentAtCommit_IsMismatch()
    {
        var commit = CommitFile("src/Widget.cs", "public class Widget { }\n"u8.ToArray());
        var provider = new GitCliContentProvider(_ => _repoDir);

        var anyHash = Convert.ToHexStringLower(SHA256.HashData("x"u8.ToArray()));
        Assert.AreEqual(GitContentCheck.Mismatch,
            provider.VerifyBlob("https://example/repo", commit, "src/Ghost.cs", anyHash));
    }

    [TestMethod]
    public void VerifyBlob_UnknownRepository_IsUnavailable()
    {
        var commit = CommitFile("src/Widget.cs", "public class Widget { }\n"u8.ToArray());
        var provider = new GitCliContentProvider(_ => null); // no checkout resolved

        var anyHash = Convert.ToHexStringLower(SHA256.HashData("x"u8.ToArray()));
        Assert.AreEqual(GitContentCheck.Unavailable,
            provider.VerifyBlob("https://example/repo", commit, "src/Widget.cs", anyHash));
    }

    [TestMethod]
    public void VerifyBlob_UnknownCommit_IsUnavailable()
    {
        CommitFile("src/Widget.cs", "public class Widget { }\n"u8.ToArray());
        var provider = new GitCliContentProvider(_ => _repoDir);

        var anyHash = Convert.ToHexStringLower(SHA256.HashData("x"u8.ToArray()));
        Assert.AreEqual(GitContentCheck.Unavailable,
            provider.VerifyBlob("https://example/repo", new string('0', 40), "src/Widget.cs", anyHash));
    }

    private string CommitFile(string repoRelativePath, byte[] content)
    {
        var full = Path.Combine(_repoDir, repoRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        Git("add", "-A");
        Git("commit", "-q", "-m", "add file");
        return Git("rev-parse", "HEAD").Trim();
    }

    private string Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _repoDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed with exit {process.ExitCode}.");
        return stdout;
    }

    private static bool GitAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process is null) return false;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
