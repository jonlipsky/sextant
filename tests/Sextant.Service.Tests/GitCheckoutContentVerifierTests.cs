using System.Diagnostics;
using Sextant.Service.SdkPin;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #113 — <see cref="GitCheckoutContentVerifier"/> vouches for a <c>global.json</c> only when git confirms
/// it is exactly its committed content at the checkout's HEAD, so the SDK-pin journal can only ever hold the
/// commit's bytes. Runs the real git over throwaway repositories.
/// </summary>
[TestClass]
public sealed class GitCheckoutContentVerifierTests
{
    private const string Pin = "{ \"sdk\": { \"version\": \"10.0.999\", \"rollForward\": \"disable\" } }\n";

    private string _root = null!;
    private string _checkout = null!;
    private string _head = null!;

    private string GlobalJson => Path.Combine(_checkout, "global.json");

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sextant_gitverify_{Guid.NewGuid():N}");
        _checkout = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_checkout);
        File.WriteAllText(GlobalJson, Pin);
        File.WriteAllText(Path.Combine(_checkout, ".gitignore"), "ignored/\n");
        _head = InitRepo(_checkout);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal); // git object files are read-only on Windows
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best-effort temp cleanup
        }
    }

    private static GitCheckoutContentVerifier Verifier => GitCheckoutContentVerifier.Instance;

    [TestMethod]
    public void ACommittedUnmodifiedFile_IsVouchedFor()
    {
        Assert.IsNull(Verifier.Problem(_checkout, _head, [GlobalJson]));
        Assert.IsNull(Verifier.Problem(_checkout, _head.ToUpperInvariant(), [GlobalJson]), "object ids compare case-insensitively");
    }

    [TestMethod]
    public void ANestedCommittedFile_IsVouchedFor()
    {
        var nested = Path.Combine(_checkout, "tools", "global.json");
        Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
        File.WriteAllText(nested, Pin);
        Git(_checkout, "add", "-A");
        Git(_checkout, "commit", "--quiet", "-m", "nested");
        var head = Git(_checkout, "rev-parse", "HEAD").Trim();

        Assert.IsNull(Verifier.Problem(_checkout, head, [GlobalJson, nested]));
    }

    [TestMethod]
    public void AnUnstagedEdit_IsRefused()
    {
        File.WriteAllText(GlobalJson, Pin.Replace("10.0.999", "10.0.998", StringComparison.Ordinal));

        StringAssert.Contains(Verifier.Problem(_checkout, _head, [GlobalJson]), "'global.json' differs from its committed content");
    }

    [TestMethod]
    public void AStagedEdit_IsRefused()
    {
        File.WriteAllText(GlobalJson, Pin.Replace("10.0.999", "10.0.998", StringComparison.Ordinal));
        Git(_checkout, "add", "global.json");

        StringAssert.Contains(Verifier.Problem(_checkout, _head, [GlobalJson]), "differs from its committed content");
    }

    [TestMethod]
    public void AStagedButUncommittedNewFile_IsRefused()
    {
        var added = Path.Combine(_checkout, "tools", "global.json");
        Directory.CreateDirectory(Path.GetDirectoryName(added)!);
        File.WriteAllText(added, Pin);
        Git(_checkout, "add", "tools/global.json");

        StringAssert.Contains(Verifier.Problem(_checkout, _head, [added]), "'tools/global.json' differs from its committed content");
    }

    [TestMethod]
    [DataRow("tools", DisplayName = "untracked")]
    [DataRow("ignored", DisplayName = "untracked and git-ignored")]
    public void AnUntrackedFile_IsRefused(string directory)
    {
        var untracked = Path.Combine(_checkout, directory, "global.json");
        Directory.CreateDirectory(Path.GetDirectoryName(untracked)!);
        File.WriteAllText(untracked, Pin);

        StringAssert.Contains(Verifier.Problem(_checkout, _head, [GlobalJson, untracked]), $"'{directory}/global.json' is not tracked by git");
    }

    [TestMethod]
    [DataRow("--assume-unchanged", "h")]
    [DataRow("--skip-worktree", "S")]
    public void AnIndexFlagThatHidesEdits_IsRefused(string flag, string tag)
    {
        Git(_checkout, "update-index", flag, "global.json");
        File.WriteAllText(GlobalJson, Pin.Replace("10.0.999", "10.0.998", StringComparison.Ordinal));

        var problem = Verifier.Problem(_checkout, _head, [GlobalJson]);

        StringAssert.Contains(problem, $"'global.json' is flagged in the git index (ls-files tag '{tag}')");
    }

    [TestMethod]
    public void AHeadOtherThanTheExpectedCommit_IsRefused()
    {
        var other = new string('a', 40);

        StringAssert.Contains(Verifier.Problem(_checkout, other, [GlobalJson]), $"not the commit {other}");
    }

    [TestMethod]
    public void AnUnbornHead_IsRefused()
    {
        var fresh = Path.Combine(_root, "fresh");
        Directory.CreateDirectory(fresh);
        File.WriteAllText(Path.Combine(fresh, "global.json"), Pin);
        Git(fresh, "init", "--quiet", "--initial-branch", "main");

        Assert.IsNotNull(Verifier.Problem(fresh, _head, [Path.Combine(fresh, "global.json")]));
    }

    [TestMethod]
    public void ADirectoryInsideAnotherRepository_IsNeverVouchedForByTheOuterRepository()
    {
        // Discovery must not climb out of the checkout: the outer repository tracks this exact file at _head,
        // but the "checkout" below it is not a repository of its own.
        var inner = Path.Combine(_checkout, "sub");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, "global.json"), Pin);
        Git(_checkout, "add", "-A");
        Git(_checkout, "commit", "--quiet", "-m", "sub");
        var head = Git(_checkout, "rev-parse", "HEAD").Trim();

        Assert.IsNotNull(Verifier.Problem(inner, head, [Path.Combine(inner, "global.json")]));
    }

    [TestMethod]
    public void AFileOutsideTheCheckout_IsRefused()
    {
        var outside = Path.Combine(_root, "global.json");
        File.WriteAllText(outside, Pin);

        StringAssert.Contains(Verifier.Problem(_checkout, _head, [outside]), "is not inside the checkout");
    }

    [TestMethod]
    public void AMissingGitExecutable_IsAProblem_NeverAnException()
    {
        var verifier = new GitCheckoutContentVerifier(Path.Combine(_root, "no-such-git"));

        StringAssert.Contains(verifier.Problem(_checkout, _head, [GlobalJson]), "git could not verify it");
    }

    [TestMethod]
    public void Verification_NeverWritesTheIndex()
    {
        // A stat-only change makes a normal `git status` refresh (rewrite) .git/index; GIT_OPTIONAL_LOCKS=0 must not.
        var index = Path.Combine(_checkout, ".git", "index");
        var before = File.ReadAllBytes(index);
        var indexTime = File.GetLastWriteTimeUtc(index);
        File.SetLastWriteTimeUtc(GlobalJson, DateTime.UtcNow.AddMinutes(-5));

        Assert.IsNull(Verifier.Problem(_checkout, _head, [GlobalJson]));

        CollectionAssert.AreEqual(before, File.ReadAllBytes(index));
        Assert.AreEqual(indexTime, File.GetLastWriteTimeUtc(index));
        Assert.IsFalse(File.Exists(index + ".lock"));
    }

    private static string InitRepo(string dir)
    {
        try
        {
            Git(dir, "init", "--quiet", "--initial-branch", "main");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Assert.Inconclusive($"git is not available: {ex.Message}");
        }
        Git(dir, "add", "-A");
        Git(dir, "commit", "--quiet", "-m", "fixture");
        return Git(dir, "rev-parse", "HEAD").Trim();
    }

    private static string Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var setting in new[] { "user.email=test@example.com", "user.name=Sextant Test", "commit.gpgsign=false", "core.autocrlf=false" })
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(setting);
        }
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("git could not be started");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({p.ExitCode}): {stderr}");
        return stdout.GetAwaiter().GetResult();
    }
}
