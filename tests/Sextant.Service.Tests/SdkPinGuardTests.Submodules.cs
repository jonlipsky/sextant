using System.Text.Json.Nodes;
using Sextant.Service.SdkPin;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #171 — every failing pin is verified ON ITS OWN, against the repository that owns it, so one
/// unverifiable pin never suppresses the override of another; and a pin inside a populated submodule is
/// overridden only when the submodule is checked out at exactly the gitlink its parent's verified commit pins.
/// The submodule here is synthetic (a <c>.git</c> file pointing into <c>.git/modules</c>), and git itself is
/// replaced by <see cref="RecordingVerifier"/>; GitCheckoutContentVerifierTests covers the real git side.
/// </summary>
public sealed partial class SdkPinGuardTests
{
    private const string SubmoduleHead = "3333333333333333333333333333333333333333";
    private const string SubmodulePin = """{ "sdk": { "version": "9.0.999", "rollForward": "disable" } }""";

    private string Submodule => Path.Combine(_checkout, "libs", "sub");

    private string SubmoduleGlobalJson => Path.Combine(Submodule, "global.json");

    // An absorbed submodule: libs/sub/.git is a "gitdir:" file into the superproject's .git/modules.
    private string AddSubmodule(string head = SubmoduleHead)
    {
        Directory.CreateDirectory(Submodule);
        File.WriteAllText(Path.Combine(Submodule, ".git"), "gitdir: ../../.git/modules/libs/sub\n");
        WriteSubmoduleHead(head);
        var solution = Path.Combine(Submodule, "Sub.slnx");
        File.WriteAllText(solution, "<Solution />");
        return solution;
    }

    private void WriteSubmoduleHead(string head)
    {
        var gitDir = Path.Combine(_checkout, ".git", "modules", "libs", "sub");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), head + "\n");
    }

    private string SubmoduleGitDir => Path.Combine(_checkout, ".git", "modules", "libs", "sub");

    private const string InnerHead = "5555555555555555555555555555555555555555";

    // A submodule nested in libs/sub (absorbed into the superproject's .git/modules/libs/sub/modules/…).
    private (string Inner, string InnerGitDir, string Solution, string Pin) AddNestedSubmodule()
    {
        AddSubmodule();
        var inner = Path.Combine(Submodule, "deps", "inner");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, ".git"), "gitdir: ../../../../.git/modules/libs/sub/modules/deps/inner\n");
        var innerGitDir = Path.Combine(SubmoduleGitDir, "modules", "deps", "inner");
        Directory.CreateDirectory(innerGitDir);
        File.WriteAllText(Path.Combine(innerGitDir, "HEAD"), InnerHead + "\n");
        var solution = Path.Combine(inner, "Inner.slnx");
        File.WriteAllText(solution, "<Solution />");
        var pin = Path.Combine(inner, "global.json");
        WritePin(pin, SubmodulePin);
        return (inner, innerGitDir, solution, pin);
    }

    [TestMethod]
    public void AnUnverifiablePin_NeverSuppressesTheOverrideOfAnotherPin()
    {
        // The core #171 regression: one pin git cannot vouch for used to refuse every candidate, root included,
        // with a reason naming the OTHER file.
        var rootBytes = WritePin(GlobalJson);
        var nested = Path.Combine(_checkout, "tools", "global.json");
        var nestedBytes = WritePin(nested, SubmodulePin);
        var toolsSolution = Path.Combine(_checkout, "tools", "Tools.slnx");
        File.WriteAllText(toolsSolution, "<Solution />");
        var verifier = new RecordingVerifier { FileProblems = { [nested] = "'tools/global.json' is not tracked by git" } };

        var overlay = NewGuard(probe: new FakeHostFxr(), verifier: verifier).Apply(_checkout, [_solution, toolsSolution]);

        var root = overlay.Findings.Single(f => f.GlobalJsonPath == "global.json");
        Assert.IsTrue(root.OverrideApplied, "the verifiable root pin is still overridden");
        Assert.IsNull(root.NotOverriddenReason);
        var refused = overlay.Findings.Single(f => f.GlobalJsonPath == "tools/global.json");
        Assert.IsFalse(refused.OverrideApplied);
        StringAssert.Contains(refused.NotOverriddenReason, "'tools/global.json' is not tracked by git",
            "the refusal names its own file");
        CollectionAssert.AreEqual(nestedBytes, File.ReadAllBytes(nested), "the refused pin is never modified");

        overlay.Restore();
        Assert.IsNull(overlay.RestoreError);
        CollectionAssert.AreEqual(rootBytes, File.ReadAllBytes(GlobalJson));
    }

    [TestMethod]
    public void ASubmodulePin_IsVerifiedAgainstTheSubmodulesOwnRepository_AtItsGitlink()
    {
        var subSolution = AddSubmodule();
        WritePin(GlobalJson);
        var subBytes = WritePin(SubmoduleGlobalJson, SubmodulePin);
        var verifier = new RecordingVerifier();
        var guard = NewGuard(probe: new FakeHostFxr(), verifier: verifier);

        var overlay = guard.Apply(_checkout, [_solution, subSolution]);

        Assert.AreEqual(2, overlay.Findings.Count);
        Assert.IsTrue(overlay.Findings.All(f => f.OverrideApplied), string.Join("; ", overlay.Findings.Select(f => f.NotOverriddenReason)));
        var gitlink = verifier.GitlinkCalls.Single();
        Assert.AreEqual((Path.GetFullPath(_checkout), DefaultHead, Submodule, SubmoduleHead), gitlink);
        var subCall = verifier.Calls.Single(c => c.Files.Single().Path == SubmoduleGlobalJson);
        Assert.AreEqual(Submodule, subCall.Checkout, "the submodule's file is verified in the submodule's own repository");
        Assert.AreEqual(SubmoduleHead, subCall.Head);
        CollectionAssert.AreEqual(subBytes, subCall.Files.Single().Content.ToArray());
        var rootCall = verifier.Calls.Single(c => c.Files.Single().Path == GlobalJson);
        Assert.AreEqual(Path.GetFullPath(_checkout), rootCall.Checkout);

        // The journal names the submodule and its commit, and is version 2 so an older binary fails closed on it.
        var journal = JsonNode.Parse(File.ReadAllText(guard.JournalPathFor(_checkout)))!.AsObject();
        Assert.AreEqual(SdkPinJournal.SubmoduleVersion, (int)journal["version"]!);
        var entries = journal["entries"]!.AsArray().Select(e => e!.AsObject()).ToList();
        var subEntry = entries.Single(e => (string)e["path"]! == SubmoduleGlobalJson);
        Assert.AreEqual(Submodule, (string)subEntry["work_tree"]!);
        Assert.AreEqual(SubmoduleHead, (string)subEntry["work_tree_head"]!);
        Assert.AreEqual(SubmoduleGitDir, (string)subEntry["work_tree_git_dir"]!);
        var rootEntry = entries.Single(e => (string)e["path"]! == GlobalJson);
        Assert.IsFalse(rootEntry.ContainsKey("work_tree"), "a checkout-owned entry carries no submodule fields");

        overlay.Restore();
        Assert.IsNull(overlay.RestoreError);
        CollectionAssert.AreEqual(subBytes, File.ReadAllBytes(SubmoduleGlobalJson));
        Assert.IsFalse(File.Exists(guard.JournalPathFor(_checkout)));
    }

    [TestMethod]
    public void ARootOnlyOverride_StillWritesAVersion1Journal()
    {
        WritePin(GlobalJson);
        var guard = NewGuard(probe: new FakeHostFxr());

        _ = guard.Apply(_checkout, [_solution]); // "crash"

        var journal = JsonNode.Parse(File.ReadAllText(guard.JournalPathFor(_checkout)))!.AsObject();
        Assert.AreEqual(SdkPinJournal.CurrentVersion, (int)journal["version"]!);
        Assert.IsFalse(journal["entries"]![0]!.AsObject().ContainsKey("work_tree"));
        Assert.IsTrue(NewGuard(probe: new FakeHostFxr()).Recover(_checkout));
    }

    [TestMethod]
    [DataRow("gitlink", DisplayName = "the submodule is not checked out at its gitlink")]
    [DataRow("gitlink-throws", DisplayName = "the gitlink check throws")]
    [DataRow("unreadable-head", DisplayName = "the submodule's HEAD cannot be read")]
    [DataRow("content", DisplayName = "the file is not the submodule commit's content")]
    public void AnUnverifiableSubmodulePin_RefusesOnlyThatPin(string failure)
    {
        var subSolution = AddSubmodule();
        var rootBytes = WritePin(GlobalJson);
        var subBytes = WritePin(SubmoduleGlobalJson, SubmodulePin);
        var subMtime = File.GetLastWriteTimeUtc(SubmoduleGlobalJson);
        var verifier = new RecordingVerifier { GitlinkFailure = failure == "gitlink-throws" ? new IOException("boom") : null };
        string expected;
        switch (failure)
        {
            case "gitlink":
                expected = "is checked out at";
                verifier.GitlinkProblems[Submodule] = $"the submodule 'libs/sub' is checked out at {SubmoduleHead}, not the commit 4444 that {DefaultHead} pins for it";
                break;
            case "gitlink-throws":
                expected = "verification failed (IOException: boom)";
                break;
            case "unreadable-head":
                expected = "the git HEAD of the submodule 'libs/sub' that contains it cannot be read";
                File.WriteAllText(Path.Combine(_checkout, ".git", "modules", "libs", "sub", "HEAD"), "garbage\n");
                break;
            default:
                expected = "differs from its committed content";
                verifier.FileProblems[SubmoduleGlobalJson] = "'global.json' differs from its committed content (git status ' M')";
                break;
        }

        var overlay = NewGuard(new SdkPinOptions { JournalRoot = JournalDir }, new FakeHostFxr(), verifier: verifier)
            .Apply(_checkout, [_solution, subSolution]);

        Assert.IsTrue(overlay.Findings.Single(f => f.GlobalJsonPath == "global.json").OverrideApplied);
        var refused = overlay.Findings.Single(f => f.GlobalJsonPath == "libs/sub/global.json");
        Assert.IsFalse(refused.OverrideApplied);
        StringAssert.Contains(refused.NotOverriddenReason, expected);
        StringAssert.Contains(refused.NotOverriddenReason, "not verifiably the checkout's committed content");
        CollectionAssert.AreEqual(subBytes, File.ReadAllBytes(SubmoduleGlobalJson), "the refused pin is never modified");
        Assert.AreEqual(subMtime, File.GetLastWriteTimeUtc(SubmoduleGlobalJson));

        var journal = JsonNode.Parse(File.ReadAllText(Directory.GetFiles(JournalDir).Single()))!.AsObject();
        Assert.AreEqual(SdkPinJournal.CurrentVersion, (int)journal["version"]!, "only the root entry is journaled");
        Assert.AreEqual(1, journal["entries"]!.AsArray().Count);

        overlay.Restore();
        Assert.IsNull(overlay.RestoreError);
        CollectionAssert.AreEqual(rootBytes, File.ReadAllBytes(GlobalJson));
    }

    [TestMethod]
    public void ANestedSubmodulePin_IsVerifiedThroughEveryGitlink()
    {
        var (inner, innerGitDir, innerSolution, _) = AddNestedSubmodule();
        var verifier = new RecordingVerifier();
        var guard = NewGuard(probe: new FakeHostFxr(), verifier: verifier);

        var overlay = guard.Apply(_checkout, [innerSolution]);

        Assert.IsTrue(overlay.Findings.Single().OverrideApplied);
        CollectionAssert.AreEqual(
            new[]
            {
                (Path.GetFullPath(_checkout), DefaultHead, Submodule, SubmoduleHead),
                (Submodule, SubmoduleHead, inner, InnerHead)
            },
            verifier.GitlinkCalls.ToArray(), "outermost first, each against its parent's verified commit");
        var call = verifier.Calls.Single();
        Assert.AreEqual((inner, InnerHead), (call.Checkout, call.Head), "verified in the innermost repository");
        var entry = JsonNode.Parse(File.ReadAllText(guard.JournalPathFor(_checkout)))!["entries"]![0]!.AsObject();
        Assert.AreEqual(inner, (string)entry["work_tree"]!);
        Assert.AreEqual(InnerHead, (string)entry["work_tree_head"]!);
        Assert.AreEqual(innerGitDir, (string)entry["work_tree_git_dir"]!);
        overlay.Restore();
        Assert.IsNull(overlay.RestoreError);
    }

    [TestMethod]
    [DataRow("outside-absolute", DisplayName = "an absolute gitdir outside the checkout")]
    [DataRow("outside-relative", DisplayName = "a relative gitdir that escapes the checkout")]
    [DataRow("not-git-metadata", DisplayName = "a gitdir inside the checkout but not under a .git directory")]
    public void ASubmoduleWhoseGitMetadataIsNotTheCheckoutsOwn_IsRefused(string pointer)
    {
        // Its HEAD would describe some other repository (e.g. another checkout's), not this work tree.
        var subSolution = AddSubmodule();
        WritePin(GlobalJson);
        var subBytes = WritePin(SubmoduleGlobalJson, SubmodulePin);
        var elsewhere = pointer == "not-git-metadata"
            ? Path.Combine(_checkout, "src", "modules", "sub")
            : Path.Combine(_root, "other-checkout", ".git", "modules", "libs", "sub");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "HEAD"), SubmoduleHead + "\n");
        var target = pointer == "outside-absolute" ? elsewhere : Path.GetRelativePath(Submodule, elsewhere);
        File.WriteAllText(Path.Combine(Submodule, ".git"), $"gitdir: {target}\n");
        var verifier = new RecordingVerifier();

        var overlay = NewGuard(probe: new FakeHostFxr(), verifier: verifier).Apply(_checkout, [_solution, subSolution]);

        var refused = overlay.Findings.Single(f => f.GlobalJsonPath == "libs/sub/global.json");
        Assert.IsFalse(refused.OverrideApplied);
        StringAssert.Contains(refused.NotOverriddenReason, "keeps its git metadata outside the checkout's own git directories");
        Assert.IsTrue(overlay.Findings.Single(f => f.GlobalJsonPath == "global.json").OverrideApplied);
        Assert.AreEqual(0, verifier.GitlinkCalls.Count);
        Assert.IsFalse(verifier.Calls.Any(c => c.Files.Any(f => f.Path == SubmoduleGlobalJson)));
        CollectionAssert.AreEqual(subBytes, File.ReadAllBytes(SubmoduleGlobalJson));
        overlay.Restore();
        Assert.IsNull(overlay.RestoreError);
    }

    [TestMethod]
    public void ASubmoduleWhoseDotGitIsASymbolicLink_IsRefused()
    {
        var subSolution = AddSubmodule();
        WritePin(GlobalJson);
        var subBytes = WritePin(SubmoduleGlobalJson, SubmodulePin);
        var dotGit = Path.Combine(Submodule, ".git");
        File.Delete(dotGit);
        try
        {
            Directory.CreateSymbolicLink(dotGit, SubmoduleGitDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Inconclusive($"symbolic links cannot be created here: {ex.Message}");
        }

        var overlay = NewGuard(probe: new FakeHostFxr()).Apply(_checkout, [_solution, subSolution]);

        StringAssert.Contains(overlay.Findings.Single(f => f.GlobalJsonPath == "libs/sub/global.json").NotOverriddenReason,
            "is a symbolic link");
        Assert.IsTrue(overlay.Findings.Single(f => f.GlobalJsonPath == "global.json").OverrideApplied);
        CollectionAssert.AreEqual(subBytes, File.ReadAllBytes(SubmoduleGlobalJson));
        overlay.Restore();
        Assert.IsNull(overlay.RestoreError);
    }

    [TestMethod]
    public void APinInsideGitMetadata_IsNeverOverridden()
    {
        // e.g. a solution inside an absorbed submodule's git directory; the service never writes under .git.
        var metadata = Path.Combine(_checkout, ".git", "modules", "x");
        Directory.CreateDirectory(metadata);
        var solution = Path.Combine(metadata, "X.slnx");
        File.WriteAllText(solution, "<Solution />");
        var pin = Path.Combine(metadata, "global.json");
        var bytes = WritePin(pin);
        var verifier = new RecordingVerifier();

        var overlay = NewGuard(probe: new FakeHostFxr(), verifier: verifier).Apply(_checkout, [solution]);

        var finding = overlay.Findings.Single();
        Assert.IsFalse(finding.OverrideApplied);
        StringAssert.Contains(finding.NotOverriddenReason, "inside git metadata (.git)");
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(pin));
        Assert.AreEqual(0, verifier.Calls.Count + verifier.GitlinkCalls.Count);
    }

    [TestMethod]
    public void Recovery_OfASubmoduleEntry_AtTheSameSubmoduleCommit_RestoresIt()
    {
        var subSolution = AddSubmodule();
        var rootBytes = WritePin(GlobalJson);
        var subBytes = WritePin(SubmoduleGlobalJson, SubmodulePin);
        var mtime = File.GetLastWriteTimeUtc(SubmoduleGlobalJson);
        _ = NewGuard(probe: new FakeHostFxr()).Apply(_checkout, [_solution, subSolution]); // "crash"
        CollectionAssert.AreNotEqual(subBytes, File.ReadAllBytes(SubmoduleGlobalJson));

        Assert.IsTrue(NewGuard(probe: new FakeHostFxr()).Recover(_checkout));

        CollectionAssert.AreEqual(rootBytes, File.ReadAllBytes(GlobalJson));
        CollectionAssert.AreEqual(subBytes, File.ReadAllBytes(SubmoduleGlobalJson));
        Assert.AreEqual(mtime, File.GetLastWriteTimeUtc(SubmoduleGlobalJson));
        Assert.AreEqual(0, Directory.GetFiles(JournalDir).Length);
    }

    [TestMethod]
    public void Recovery_AfterTheSubmoduleMoved_LeavesItAlone_ButStillRestoresTheRoot()
    {
        var subSolution = AddSubmodule();
        var rootBytes = WritePin(GlobalJson);
        WritePin(SubmoduleGlobalJson, SubmodulePin);
        _ = NewGuard(probe: new FakeHostFxr()).Apply(_checkout, [_solution, subSolution]); // "crash"

        // The submodule was re-checked-out at another commit, whose global.json holds (by chance) the
        // neutralized bytes: it belongs to THAT commit now and must never be written.
        WriteSubmoduleHead("4444444444444444444444444444444444444444");
        var moved = File.ReadAllBytes(SubmoduleGlobalJson);
        var logs = new List<string>();

        Assert.IsTrue(NewGuard(probe: new FakeHostFxr(), log: logs.Add).Recover(_checkout));

        CollectionAssert.AreEqual(rootBytes, File.ReadAllBytes(GlobalJson), "the checkout's own entry is restored");
        CollectionAssert.AreEqual(moved, File.ReadAllBytes(SubmoduleGlobalJson), "the moved submodule is untouched");
        Assert.IsTrue(logs.Any(l => l.Contains("moved from", StringComparison.Ordinal)));
        Assert.AreEqual(0, Directory.GetFiles(JournalDir).Length);
    }

    [TestMethod]
    public void Recovery_OfAnUnpopulatedSubmodule_RetiresItsEntry()
    {
        var subSolution = AddSubmodule();
        WritePin(GlobalJson);
        WritePin(SubmoduleGlobalJson, SubmodulePin);
        _ = NewGuard(probe: new FakeHostFxr()).Apply(_checkout, [_solution, subSolution]); // "crash"
        Directory.Delete(Submodule, recursive: true);
        Directory.CreateDirectory(Submodule); // `git submodule deinit` leaves the empty directory

        Assert.IsTrue(NewGuard(probe: new FakeHostFxr()).Recover(_checkout));
        Assert.AreEqual(0, Directory.GetFileSystemEntries(Submodule).Length, "nothing is recreated");
        Assert.AreEqual(0, Directory.GetFiles(JournalDir).Length);
    }

    [TestMethod]
    [DataRow("unreadable-head", DisplayName = "the submodule's HEAD is unreadable")]
    [DataRow("no-longer-a-work-tree", DisplayName = "the submodule's .git is gone but its global.json remains")]
    public void Recovery_WhenTheSubmodulesCommitCannotBeConfirmed_FailsClosed(string state)
    {
        var subSolution = AddSubmodule();
        var rootBytes = WritePin(GlobalJson);
        WritePin(SubmoduleGlobalJson, SubmodulePin);
        _ = NewGuard(probe: new FakeHostFxr()).Apply(_checkout, [_solution, subSolution]); // "crash"
        var neutralizedRoot = File.ReadAllBytes(GlobalJson);
        var neutralizedSub = File.ReadAllBytes(SubmoduleGlobalJson);
        if (state == "unreadable-head")
            File.WriteAllText(Path.Combine(_checkout, ".git", "modules", "libs", "sub", "HEAD"), "garbage\n");
        else
            File.Delete(Path.Combine(Submodule, ".git"));

        Assert.IsFalse(NewGuard(probe: new FakeHostFxr()).Recover(_checkout), "the checkout must not be indexed");

        CollectionAssert.AreEqual(neutralizedSub, File.ReadAllBytes(SubmoduleGlobalJson), "nothing is written");
        CollectionAssert.AreEqual(neutralizedRoot, File.ReadAllBytes(GlobalJson),
            "no entry is restored while any is unconfirmed (all-or-nothing, like a link refusal)");
        CollectionAssert.AreNotEqual(rootBytes, neutralizedRoot);
        Assert.AreEqual(1, Directory.GetFiles(JournalDir).Length, "the journal keeps blocking the checkout");
    }

    [TestMethod]
    [DataRow("other-git-dir", DisplayName = "repointed at another git directory of the checkout, at another commit")]
    [DataRow("outside-checkout", DisplayName = "repointed at a git directory outside the checkout")]
    public void Recovery_AfterTheSubmodulesGitMetadataWasRepointed_FailsClosed(string pointer)
    {
        // Its HEAD no longer describes the journaled work tree, so a different commit there proves nothing — the
        // entry must not be retired as "moved" (which would leave the pin neutralized), nor restored.
        var subSolution = AddSubmodule();
        WritePin(GlobalJson);
        WritePin(SubmoduleGlobalJson, SubmodulePin);
        _ = NewGuard(probe: new FakeHostFxr()).Apply(_checkout, [_solution, subSolution]); // "crash"
        var neutralized = File.ReadAllBytes(SubmoduleGlobalJson);
        var other = pointer == "other-git-dir"
            ? Path.Combine(_checkout, ".git", "modules", "other")
            : Path.Combine(_root, "other-checkout", ".git", "modules", "libs", "sub");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "HEAD"), "4444444444444444444444444444444444444444\n");
        File.WriteAllText(Path.Combine(Submodule, ".git"), $"gitdir: {other}\n");
        var logs = new List<string>();

        Assert.IsFalse(NewGuard(probe: new FakeHostFxr(), log: logs.Add).Recover(_checkout), "the checkout must not be indexed");

        CollectionAssert.AreEqual(neutralized, File.ReadAllBytes(SubmoduleGlobalJson), "nothing is written");
        Assert.AreEqual(1, Directory.GetFiles(JournalDir).Length, "the journal keeps blocking the checkout");
        Assert.IsFalse(logs.Any(l => l.Contains("moved from", StringComparison.Ordinal)), string.Join(Environment.NewLine, logs));
        StringAssert.Contains(string.Join(Environment.NewLine, logs),
            pointer == "other-git-dir" ? "now keeps its git metadata in" : "keeps its git metadata outside the checkout's own git directories");
    }

    [TestMethod]
    public void Recovery_OfACheckoutEntryThatLiesInsideAPopulatedSubmodule_FailsClosed()
    {
        // A version-1 (checkout-owned) entry is replayed after only the checkout's HEAD check; a global.json inside a
        // populated submodule belongs to the submodule's commit, which such an entry never recorded.
        var subSolution = AddSubmodule();
        WritePin(GlobalJson);
        WritePin(SubmoduleGlobalJson, SubmodulePin);
        var guard = NewGuard(probe: new FakeHostFxr());
        _ = guard.Apply(_checkout, [_solution, subSolution]); // "crash"
        var neutralized = File.ReadAllBytes(SubmoduleGlobalJson);
        var journalPath = guard.JournalPathFor(_checkout);
        var journal = JsonNode.Parse(File.ReadAllText(journalPath))!.AsObject();
        journal["version"] = SdkPinJournal.CurrentVersion;
        var entry = journal["entries"]!.AsArray().Select(e => e!.AsObject()).Single(e => e.ContainsKey("work_tree"));
        entry.Remove("work_tree");
        entry.Remove("work_tree_head");
        entry.Remove("work_tree_git_dir");
        File.WriteAllText(journalPath, journal.ToJsonString());
        var logs = new List<string>();

        Assert.IsFalse(NewGuard(probe: new FakeHostFxr(), log: logs.Add).Recover(_checkout));

        CollectionAssert.AreEqual(neutralized, File.ReadAllBytes(SubmoduleGlobalJson), "nothing is written");
        Assert.IsTrue(File.Exists(journalPath));
        Assert.IsTrue(logs.Any(l => l.Contains("records no submodule commit", StringComparison.Ordinal)), string.Join(Environment.NewLine, logs));
    }

    [TestMethod]
    public void Recovery_OfAnEntryNamingAnOuterSubmodule_FailsClosed()
    {
        // The file lies in a nested submodule; an entry recording only the OUTER one would replay after checking the
        // wrong repository's commit.
        var (_, _, innerSolution, innerPin) = AddNestedSubmodule();
        var guard = NewGuard(probe: new FakeHostFxr());
        _ = guard.Apply(_checkout, [innerSolution]); // "crash"
        var neutralized = File.ReadAllBytes(innerPin);
        var journalPath = guard.JournalPathFor(_checkout);
        var journal = JsonNode.Parse(File.ReadAllText(journalPath))!.AsObject();
        var entry = journal["entries"]![0]!.AsObject();
        entry["work_tree"] = Submodule;
        entry["work_tree_head"] = SubmoduleHead;
        entry["work_tree_git_dir"] = SubmoduleGitDir;
        File.WriteAllText(journalPath, journal.ToJsonString());
        var logs = new List<string>();

        Assert.IsFalse(NewGuard(probe: new FakeHostFxr(), log: logs.Add).Recover(_checkout));

        CollectionAssert.AreEqual(neutralized, File.ReadAllBytes(innerPin), "nothing is written");
        Assert.IsTrue(File.Exists(journalPath));
        Assert.IsTrue(logs.Any(l => l.Contains("the innermost submodule containing it is not", StringComparison.Ordinal)), string.Join(Environment.NewLine, logs));
    }

    [TestMethod]
    [DataRow("v1-with-submodule")]
    [DataRow("work-tree-outside")]
    [DataRow("work-tree-not-containing")]
    [DataRow("work-tree-relative")]
    [DataRow("head-missing")]
    [DataRow("head-malformed")]
    [DataRow("work-tree-missing")]
    [DataRow("git-dir-missing")]
    [DataRow("git-dir-relative")]
    [DataRow("git-dir-not-git-metadata")]
    [DataRow("v2-without-submodule-entry")]
    [DataRow("entry-in-git-metadata")]
    public void MalformedSubmoduleJournal_IsKept_AndNeverReplayed(string mutation)
    {
        var subSolution = AddSubmodule();
        WritePin(GlobalJson);
        WritePin(SubmoduleGlobalJson, SubmodulePin);
        var guard = NewGuard(probe: new FakeHostFxr());
        _ = guard.Apply(_checkout, [_solution, subSolution]); // "crash"
        var neutralized = File.ReadAllBytes(SubmoduleGlobalJson);
        var journalPath = guard.JournalPathFor(_checkout);
        var journal = JsonNode.Parse(File.ReadAllText(journalPath))!.AsObject();
        var entry = journal["entries"]!.AsArray().Select(e => e!.AsObject()).Single(e => e.ContainsKey("work_tree"));
        switch (mutation)
        {
            case "v1-with-submodule": journal["version"] = SdkPinJournal.CurrentVersion; break;
            case "work-tree-outside": entry["work_tree"] = _root; break;
            case "work-tree-not-containing": entry["work_tree"] = Path.Combine(_checkout, "src"); break;
            case "work-tree-relative": entry["work_tree"] = "libs/sub"; break;
            case "head-missing": entry.Remove("work_tree_head"); break;
            case "head-malformed": entry["work_tree_head"] = "HEAD"; break;
            case "work-tree-missing": entry.Remove("work_tree"); break;
            case "git-dir-missing": entry.Remove("work_tree_git_dir"); break;
            case "git-dir-relative": entry["work_tree_git_dir"] = ".git/modules/libs/sub"; break;
            case "git-dir-not-git-metadata": entry["work_tree_git_dir"] = Path.Combine(_checkout, "src"); break;
            case "v2-without-submodule-entry":
                // Only the checkout-owned entry is left, under the version Apply writes only with a submodule entry.
                journal["entries"]!.AsArray().Remove(entry);
                break;
            case "entry-in-git-metadata":
                entry["path"] = Path.Combine(_checkout, ".git", "modules", "libs", "sub", "global.json");
                entry["temp_path"] = Path.Combine(_checkout, ".git", "modules", "libs", "sub", Path.GetFileName((string)entry["temp_path"]!));
                entry["work_tree"] = _checkout + Path.DirectorySeparatorChar + ".git";
                break;
            default: Assert.Fail(mutation); break;
        }
        File.WriteAllText(journalPath, journal.ToJsonString());

        Assert.IsFalse(guard.Recover(_checkout), $"{mutation}: an invalid journal must keep blocking the checkout");
        Assert.IsTrue(File.Exists(journalPath), $"{mutation}: kept for an operator to inspect");
        CollectionAssert.AreEqual(neutralized, File.ReadAllBytes(SubmoduleGlobalJson), $"{mutation}: nothing was written");
    }

    [TestMethod]
    public void ARootJournalEntryRedirectedIntoGitMetadata_IsKept_AndNeverReplayed()
    {
        // A root-owned (version 1) entry records no work tree, so the entry's own path is its only guard against a
        // tampered journal directing a restore into .git.
        WritePin(GlobalJson);
        var logs = new List<string>();
        var guard = NewGuard(probe: new FakeHostFxr(), log: logs.Add);
        _ = guard.Apply(_checkout, [_solution]); // "crash"
        var journalPath = guard.JournalPathFor(_checkout);
        var journal = JsonNode.Parse(File.ReadAllText(journalPath))!.AsObject();
        Assert.AreEqual(SdkPinJournal.CurrentVersion, (int)journal["version"]!);
        var entry = journal["entries"]!.AsArray().Single()!.AsObject();
        Assert.IsFalse(entry.ContainsKey("work_tree"));
        var target = Path.Combine(_checkout, ".git", "modules", "libs", "sub", "global.json");
        entry["path"] = target;
        entry["temp_path"] = Path.Combine(Path.GetDirectoryName(target)!, Path.GetFileName((string)entry["temp_path"]!));
        File.WriteAllText(journalPath, journal.ToJsonString());

        Assert.IsFalse(guard.Recover(_checkout), "an invalid journal must keep blocking the checkout");
        Assert.IsTrue(File.Exists(journalPath), "kept for an operator to inspect");
        Assert.IsFalse(File.Exists(target), "nothing is written under .git");
        Assert.IsTrue(logs.Any(l => l.Contains("an entry lies inside git metadata", StringComparison.Ordinal)),
            string.Join(Environment.NewLine, logs));
    }

    [TestMethod]
    public void AnEnclosingDirectoryWithGitMetadata_ThatIsNotASubmodule_IsRefused()
    {
        // A nested repository that the superproject's commit does not record as a gitlink (e.g. a stray clone):
        // its global.json is not the superproject commit's content, so it is not overridden.
        var subSolution = AddSubmodule();
        WritePin(GlobalJson);
        var subBytes = WritePin(SubmoduleGlobalJson, SubmodulePin);
        var verifier = new RecordingVerifier { GitlinkProblems = { [Submodule] = $"'libs/sub' is not a submodule in the commit {DefaultHead}" } };

        var overlay = NewGuard(probe: new FakeHostFxr(), verifier: verifier).Apply(_checkout, [_solution, subSolution]);

        StringAssert.Contains(overlay.Findings.Single(f => f.GlobalJsonPath == "libs/sub/global.json").NotOverriddenReason, "is not a submodule in the commit");
        Assert.IsTrue(overlay.Findings.Single(f => f.GlobalJsonPath == "global.json").OverrideApplied);
        Assert.IsFalse(verifier.Calls.Any(c => c.Files.Any(f => f.Path == SubmoduleGlobalJson)), "its content is never checked against the wrong repository");
        CollectionAssert.AreEqual(subBytes, File.ReadAllBytes(SubmoduleGlobalJson));
        overlay.Restore();
        Assert.IsNull(overlay.RestoreError);
    }
}
