using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
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

    private static CheckoutFileContent[] OnDisk(params string[] paths) =>
        paths.Select(p => new CheckoutFileContent(p, File.ReadAllBytes(p))).ToArray();

    private static string SameSizeEdit => Pin.Replace("10.0.999", "10.0.998", StringComparison.Ordinal);

    [TestMethod]
    public void ACommittedUnmodifiedFile_IsVouchedFor()
    {
        Assert.IsNull(Verifier.Problem(_checkout, _head, OnDisk(GlobalJson)));
        Assert.IsNull(Verifier.Problem(_checkout, _head.ToUpperInvariant(), OnDisk(GlobalJson)), "object ids compare case-insensitively");
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

        Assert.IsNull(Verifier.Problem(_checkout, head, OnDisk(GlobalJson, nested)));
    }

    [TestMethod]
    public void BytesOtherThanTheCommittedContent_AreRefused_EvenWhenTheWorkingTreeIsClean()
    {
        var problem = Verifier.Problem(_checkout, _head, [new CheckoutFileContent(GlobalJson, Encoding.UTF8.GetBytes(SameSizeEdit))]);

        StringAssert.Contains(problem, "the bytes read from 'global.json' are not a checkout of its committed content");
    }

    [TestMethod]
    public void AnEditGitStatusCannotSeeFromStatData_IsStillRefused()
    {
        // With minimal stat checking, a same-size edit that keeps the recorded mtime looks clean to `git status`.
        Git(_checkout, "config", "core.checkStat", "minimal");
        Git(_checkout, "config", "core.trustctime", "false");
        var mtime = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(GlobalJson, mtime);
        Git(_checkout, "update-index", "--refresh");
        File.WriteAllText(GlobalJson, SameSizeEdit);
        File.SetLastWriteTimeUtc(GlobalJson, mtime);
        Assert.AreEqual(string.Empty, Git(_checkout, "status", "--porcelain", "--untracked-files=no"),
            "precondition: git status cannot see the edit");

        var problem = Verifier.Problem(_checkout, _head, OnDisk(GlobalJson));

        StringAssert.Contains(problem, "the bytes read from 'global.json' are not a checkout of its committed content");
    }

    [TestMethod]
    public void AWorkingCopyInTheCheckoutsLineEndings_IsItsCommittedContent()
    {
        // Stored with LF, checked out by git with CRLF: the comparison is with git's checkout rendering.
        File.WriteAllText(Path.Combine(_checkout, ".gitattributes"), "*.json text eol=crlf\n");
        Git(_checkout, "add", ".gitattributes");
        Git(_checkout, "commit", "--quiet", "-m", "eol");
        var head = Git(_checkout, "rev-parse", "HEAD").Trim();
        File.Delete(GlobalJson);
        Git(_checkout, "checkout", "--", "global.json");
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(Pin.Replace("\n", "\r\n", StringComparison.Ordinal)), File.ReadAllBytes(GlobalJson),
            "precondition: git checked the file out with CRLF");

        Assert.IsNull(Verifier.Problem(_checkout, head, OnDisk(GlobalJson)));
    }

    [TestMethod]
    public void ACheckoutMadeBeforeAnEolSettingChanged_IsItsStoredContent()
    {
        // The LF file on disk is the blob as stored, although a fresh checkout would now render it with CRLF.
        Git(_checkout, "config", "core.autocrlf", "true");

        Assert.IsNull(Verifier.Problem(_checkout, _head, OnDisk(GlobalJson)));
    }

    [TestMethod]
    public void AnEditTheCleanFilterNormalizesAway_IsRefused()
    {
        // `ident` collapses "$Id: <anything> $" back to "$Id$" on the way in, so the edit below is invisible to
        // `git status` and to hashing through the clean filter; only the checkout rendering exposes it.
        File.WriteAllText(Path.Combine(_checkout, ".gitattributes"), "global.json ident\n");
        File.WriteAllText(GlobalJson, "{ \"id\": \"$Id$\", \"sdk\": { \"version\": \"10.0.999\", \"rollForward\": \"disable\" } }\n");
        Git(_checkout, "add", "-A");
        Git(_checkout, "commit", "--quiet", "-m", "ident");
        var head = Git(_checkout, "rev-parse", "HEAD").Trim();
        File.Delete(GlobalJson);
        Git(_checkout, "checkout", "--", "global.json");
        var expanded = File.ReadAllText(GlobalJson);
        var id = Regex.Match(expanded, @"\$Id: ([0-9a-f]+) \$").Groups[1].Value;
        Assert.AreNotEqual(string.Empty, id, "precondition: git expanded the ident keyword");
        Assert.IsNull(Verifier.Problem(_checkout, head, OnDisk(GlobalJson)), "the expanded checkout is the committed content");

        File.WriteAllText(GlobalJson, expanded.Replace(id, new string('0', id.Length), StringComparison.Ordinal));
        Assert.AreEqual(string.Empty, Git(_checkout, "status", "--porcelain", "--untracked-files=no"),
            "precondition: git status cannot see the edit");

        StringAssert.Contains(Verifier.Problem(_checkout, head, OnDisk(GlobalJson)),
            "the bytes read from 'global.json' are not a checkout of its committed content");
    }

    [TestMethod]
    [DataRow("swap", "global.json filter=swap\n", DisplayName = "a named driver")]
    [DataRow("unspecified", "global.json filter=unspecified\n", DisplayName = "a driver named like an unspecified attribute")]
    [DataRow("unset", "global.json filter=unset\n", DisplayName = "a driver named like an unset attribute")]
    [DataRow("swap", "[attr]swapped filter=swap\nglobal.json swapped\n", DisplayName = "a driver set through a macro")]
    public void ACheckoutRenderedByAFilterDriver_IsNeverVouchedFor(string driver, string attributes)
    {
        // The driver's clean hides the edit from `git status`, and its smudge renders exactly the bytes on disk,
        // but that output is the program's, not the blob's.
        Git(_checkout, "config", $"filter.{driver}.clean", @"sed -e s/10\.0\.998/10.0.999/");
        Git(_checkout, "config", $"filter.{driver}.smudge", @"sed -e s/10\.0\.999/10.0.998/");
        File.WriteAllText(Path.Combine(_checkout, ".gitattributes"), attributes);
        Git(_checkout, "add", ".gitattributes");
        Git(_checkout, "commit", "--quiet", "-m", "filter");
        var head = Git(_checkout, "rev-parse", "HEAD").Trim();
        File.Delete(GlobalJson);
        Git(_checkout, "checkout", "--", "global.json");
        Assert.AreEqual(SameSizeEdit, File.ReadAllText(GlobalJson), "precondition: the smudge filter rendered the file");
        Assert.AreEqual(string.Empty, Git(_checkout, "status", "--porcelain", "--untracked-files=no"),
            "precondition: git status sees no change");

        StringAssert.Contains(Verifier.Problem(_checkout, head, OnDisk(GlobalJson)), $"through the '{driver}' filter driver");
    }

    [TestMethod]
    public void AGitThatDoesNotAnswerInTime_IsAProblem()
    {
        var verifier = new GitCheckoutContentVerifier(timeout: TimeSpan.FromMilliseconds(1));

        StringAssert.Contains(verifier.Problem(_checkout, _head, OnDisk(GlobalJson)), "timed out");
    }

    [TestMethod]
    public void AReplaceRefForTheHeadCommit_IsIgnored()
    {
        // HEAD stays at _head, but a local refs/replace entry swaps in a commit whose tree matches the edited
        // index and worktree. git must still judge the file against _head's own tree.
        File.WriteAllText(GlobalJson, SameSizeEdit);
        Git(_checkout, "add", "global.json");
        Git(_checkout, "commit", "--quiet", "-m", "edited");
        var edited = Git(_checkout, "rev-parse", "HEAD").Trim();
        Git(_checkout, "reset", "--soft", _head);
        Git(_checkout, "replace", _head, edited);
        Assert.AreEqual(string.Empty, Git(_checkout, "status", "--porcelain", "--untracked-files=no"),
            "precondition: with the replace ref honored, git status sees no change");

        Assert.IsNotNull(Verifier.Problem(_checkout, _head, OnDisk(GlobalJson)));
    }

    [TestMethod]
    public void AnUnstagedEdit_IsRefused()
    {
        File.WriteAllText(GlobalJson, Pin.Replace("10.0.999", "10.0.998", StringComparison.Ordinal));

        StringAssert.Contains(Verifier.Problem(_checkout, _head, OnDisk(GlobalJson)), "'global.json' differs from its committed content");
    }

    [TestMethod]
    public void AStagedEdit_IsRefused()
    {
        File.WriteAllText(GlobalJson, Pin.Replace("10.0.999", "10.0.998", StringComparison.Ordinal));
        Git(_checkout, "add", "global.json");

        StringAssert.Contains(Verifier.Problem(_checkout, _head, OnDisk(GlobalJson)), "differs from its committed content");
    }

    [TestMethod]
    public void AStagedButUncommittedNewFile_IsRefused()
    {
        var added = Path.Combine(_checkout, "tools", "global.json");
        Directory.CreateDirectory(Path.GetDirectoryName(added)!);
        File.WriteAllText(added, Pin);
        Git(_checkout, "add", "tools/global.json");

        StringAssert.Contains(Verifier.Problem(_checkout, _head, OnDisk(added)), "'tools/global.json' differs from its committed content");
    }

    [TestMethod]
    [DataRow("tools", DisplayName = "untracked")]
    [DataRow("ignored", DisplayName = "untracked and git-ignored")]
    public void AnUntrackedFile_IsRefused(string directory)
    {
        var untracked = Path.Combine(_checkout, directory, "global.json");
        Directory.CreateDirectory(Path.GetDirectoryName(untracked)!);
        File.WriteAllText(untracked, Pin);

        StringAssert.Contains(Verifier.Problem(_checkout, _head, OnDisk(GlobalJson, untracked)), $"'{directory}/global.json' is not tracked by git");
    }

    [TestMethod]
    [DataRow("--assume-unchanged", "h")]
    [DataRow("--skip-worktree", "S")]
    public void AnIndexFlagThatHidesEdits_IsRefused(string flag, string tag)
    {
        Git(_checkout, "update-index", flag, "global.json");
        File.WriteAllText(GlobalJson, Pin.Replace("10.0.999", "10.0.998", StringComparison.Ordinal));

        var problem = Verifier.Problem(_checkout, _head, OnDisk(GlobalJson));

        StringAssert.Contains(problem, $"'global.json' is flagged in the git index (ls-files tag '{tag}')");
    }

    [TestMethod]
    public void AHeadOtherThanTheExpectedCommit_IsRefused()
    {
        var other = new string('a', 40);

        StringAssert.Contains(Verifier.Problem(_checkout, other, OnDisk(GlobalJson)), $"not the commit {other}");
    }

    [TestMethod]
    public void AnUnbornHead_IsRefused()
    {
        var fresh = Path.Combine(_root, "fresh");
        Directory.CreateDirectory(fresh);
        File.WriteAllText(Path.Combine(fresh, "global.json"), Pin);
        Git(fresh, "init", "--quiet", "--initial-branch", "main");

        Assert.IsNotNull(Verifier.Problem(fresh, _head, OnDisk(Path.Combine(fresh, "global.json"))));
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

        Assert.IsNotNull(Verifier.Problem(inner, head, OnDisk(Path.Combine(inner, "global.json"))));
    }

    [TestMethod]
    public void AFileOutsideTheCheckout_IsRefused()
    {
        var outside = Path.Combine(_root, "global.json");
        File.WriteAllText(outside, Pin);

        StringAssert.Contains(Verifier.Problem(_checkout, _head, OnDisk(outside)), "is not inside the checkout");
    }

    [TestMethod]
    public void AMissingGitExecutable_IsAProblem_NeverAnException()
    {
        var verifier = new GitCheckoutContentVerifier(Path.Combine(_root, "no-such-git"));

        StringAssert.Contains(verifier.Problem(_checkout, _head, OnDisk(GlobalJson)), "git could not verify it");
    }

    [TestMethod]
    public void Verification_NeverWritesTheIndex()
    {
        // A stat-only change makes a normal `git status` refresh (rewrite) .git/index; GIT_OPTIONAL_LOCKS=0 must not.
        var index = Path.Combine(_checkout, ".git", "index");
        var before = File.ReadAllBytes(index);
        var indexTime = File.GetLastWriteTimeUtc(index);
        File.SetLastWriteTimeUtc(GlobalJson, DateTime.UtcNow.AddMinutes(-5));

        Assert.IsNull(Verifier.Problem(_checkout, _head, OnDisk(GlobalJson)));

        CollectionAssert.AreEqual(before, File.ReadAllBytes(index));
        Assert.AreEqual(indexTime, File.GetLastWriteTimeUtc(index));
        Assert.IsFalse(File.Exists(index + ".lock"));
    }

    [TestMethod]
    public void ASubmoduleAtItsGitlink_IsVouchedFor_AndItsFileIsVerifiedInItsOwnRepository()
    {
        var (sub, subHead, head) = AddSubmodule();
        var subPin = Path.Combine(sub, "global.json");

        Assert.IsNull(Verifier.GitlinkProblem(_checkout, head, sub, subHead));
        Assert.IsNull(Verifier.GitlinkProblem(_checkout, head.ToUpperInvariant(), sub, subHead.ToUpperInvariant()));
        Assert.IsNull(Verifier.Problem(sub, subHead, OnDisk(subPin)), "the submodule's own repository vouches for it");
        // Issue #171's root cause: from the superproject, the submodule's file is simply not tracked.
        StringAssert.Contains(Verifier.Problem(_checkout, head, OnDisk(subPin)), "is not tracked by git");
    }

    [TestMethod]
    public void ASubmoduleCheckedOutAwayFromItsGitlink_IsRefused()
    {
        var (sub, subHead, head) = AddSubmodule();
        File.WriteAllText(Path.Combine(sub, "extra.txt"), "drift");
        Git(sub, "add", "-A");
        Git(sub, "commit", "--quiet", "-m", "drift");
        var drifted = Git(sub, "rev-parse", "HEAD").Trim();

        var problem = Verifier.GitlinkProblem(_checkout, head, sub, drifted);

        StringAssert.Contains(problem, $"the submodule 'libs/sub' is checked out at {drifted}, not the commit {subHead}");
    }

    [TestMethod]
    public void ADirectoryThatIsNotAGitlink_IsNotASubmodule()
    {
        var tools = Path.Combine(_checkout, "tools");
        Directory.CreateDirectory(tools);
        File.WriteAllText(Path.Combine(tools, "global.json"), Pin);
        Git(_checkout, "add", "-A");
        Git(_checkout, "commit", "--quiet", "-m", "tools");
        var head = Git(_checkout, "rev-parse", "HEAD").Trim();

        StringAssert.Contains(Verifier.GitlinkProblem(_checkout, head, tools, _head), "is not a submodule in the commit");
        StringAssert.Contains(Verifier.GitlinkProblem(_checkout, head, tools, _head), "(mode 040000 tree)");
        StringAssert.Contains(Verifier.GitlinkProblem(_checkout, head, Path.Combine(_checkout, "absent"), _head), "'absent' is not a submodule in the commit");
        StringAssert.Contains(Verifier.GitlinkProblem(_checkout, head, _root, _head), "is not inside the repository");
    }

    [TestMethod]
    public void AGitlinkCheck_AgainstAnotherParentCommit_IsRefused()
    {
        var (sub, subHead, _) = AddSubmodule();

        StringAssert.Contains(Verifier.GitlinkProblem(_checkout, _head, sub, subHead), "git resolves HEAD to");
    }

    [TestMethod]
    public void TheGuard_OverridesARootAndASubmodulePin_AndLeavesGitMetadataAndBothTreesClean()
    {
        // Issue #171 end to end with real git: a root pin and a pin inside an absorbed submodule both fail, both
        // are verified (the submodule's at its gitlink) and overridden, and nothing git owns is ever written.
        var (sub, _, _) = AddSubmodule();
        var subPin = Path.Combine(sub, "global.json");
        var rootBytes = File.ReadAllBytes(GlobalJson);
        var subBytes = File.ReadAllBytes(subPin);
        var gitDigest = Digest(Path.Combine(_checkout, ".git"));
        var journalRoot = Path.Combine(_root, "journal");
        var guard = new SdkPinGuard(new SdkPinOptions { JournalRoot = journalRoot }, new SdkPinGuardTests.FakeHostFxr(), verifier: Verifier);

        var overlay = guard.Apply(_checkout, [Path.Combine(_checkout, "Repo.slnx"), Path.Combine(sub, "Sub.slnx")]);

        Assert.AreEqual(2, overlay.Findings.Count);
        Assert.IsTrue(overlay.Findings.All(f => f.OverrideApplied), string.Join("; ", overlay.Findings.Select(f => f.NotOverriddenReason)));
        CollectionAssert.AreEquivalent(new[] { "global.json", "libs/sub/global.json" }, overlay.Findings.Select(f => f.GlobalJsonPath).ToArray());
        overlay.Restore();
        Assert.IsNull(overlay.RestoreError);

        CollectionAssert.AreEqual(rootBytes, File.ReadAllBytes(GlobalJson));
        CollectionAssert.AreEqual(subBytes, File.ReadAllBytes(subPin));
        Assert.AreEqual(gitDigest, Digest(Path.Combine(_checkout, ".git")), ".git and .git/modules are never written");
        Assert.AreEqual(string.Empty, Git(_checkout, "status", "--porcelain", "--ignore-submodules=none"));
        Assert.AreEqual(string.Empty, Git(sub, "status", "--porcelain"));
        Assert.AreEqual(0, Directory.GetFiles(journalRoot).Length);
    }

    [TestMethod]
    public void TheGuard_RefusesOnlyADriftedSubmodulesPin_AndStillOverridesTheRoot()
    {
        var (sub, _, _) = AddSubmodule();
        File.WriteAllText(Path.Combine(sub, "extra.txt"), "drift");
        Git(sub, "add", "-A");
        Git(sub, "commit", "--quiet", "-m", "drift");
        var subPin = Path.Combine(sub, "global.json");
        var subBytes = File.ReadAllBytes(subPin);
        var guard = new SdkPinGuard(new SdkPinOptions { JournalRoot = Path.Combine(_root, "journal") }, new SdkPinGuardTests.FakeHostFxr(), verifier: Verifier);

        var overlay = guard.Apply(_checkout, [Path.Combine(_checkout, "Repo.slnx"), Path.Combine(sub, "Sub.slnx")]);

        Assert.IsTrue(overlay.Findings.Single(f => f.GlobalJsonPath == "global.json").OverrideApplied);
        var refused = overlay.Findings.Single(f => f.GlobalJsonPath == "libs/sub/global.json");
        Assert.IsFalse(refused.OverrideApplied);
        StringAssert.Contains(refused.NotOverriddenReason, "the submodule 'libs/sub' is checked out at");
        CollectionAssert.AreEqual(subBytes, File.ReadAllBytes(subPin));
        overlay.Restore();
        Assert.IsNull(overlay.RestoreError);
        Assert.AreEqual(string.Empty, Git(sub, "status", "--porcelain"));
    }

    // A committed superproject (root global.json + Repo.slnx) with an absorbed submodule at libs/sub whose own
    // commit holds a failing pin and Sub.slnx. Returns the submodule directory, its commit, and the superproject's.
    private (string Sub, string SubHead, string Head) AddSubmodule()
    {
        File.WriteAllText(Path.Combine(_checkout, "Repo.slnx"), "<Solution />");
        var source = Path.Combine(_root, "sub-source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "global.json"), Pin.Replace("10.0.999", "9.0.999", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(source, "Sub.slnx"), "<Solution />");
        var subHead = InitRepo(source);

        Git(_checkout, "-c", "protocol.file.allow=always", "submodule", "add", "--quiet", source, "libs/sub");
        var sub = Path.Combine(_checkout, "libs", "sub");
        Git(sub, "config", "core.autocrlf", "false");
        Git(_checkout, "add", "-A");
        Git(_checkout, "commit", "--quiet", "-m", "submodule");
        Assert.IsTrue(File.Exists(Path.Combine(sub, ".git")), "the submodule is absorbed (.git is a gitdir file)");
        Assert.IsTrue(Directory.Exists(Path.Combine(_checkout, ".git", "modules", "libs", "sub")));
        return (sub, subHead, Git(_checkout, "rev-parse", "HEAD").Trim());
    }

    private static string Digest(string dir)
    {
        var builder = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            builder.Append(Path.GetRelativePath(dir, file)).Append(':')
                .Append(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))))
                .Append(':').Append(File.GetLastWriteTimeUtc(file).Ticks).Append('\n');
        return builder.ToString();
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
        // The verifier sees the repository's configuration, so pin it rather than inherit the machine's.
        Git(dir, "config", "core.autocrlf", "false");
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
