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
        AddSubmodule();
        var inner = Path.Combine(Submodule, "deps", "inner");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, ".git"), "gitdir: ../../../../.git/modules/libs/sub/modules/deps/inner\n");
        var innerGitDir = Path.Combine(_checkout, ".git", "modules", "libs", "sub", "modules", "deps", "inner");
        Directory.CreateDirectory(innerGitDir);
        const string innerHead = "5555555555555555555555555555555555555555";
        File.WriteAllText(Path.Combine(innerGitDir, "HEAD"), innerHead + "\n");
        var innerSolution = Path.Combine(inner, "Inner.slnx");
        File.WriteAllText(innerSolution, "<Solution />");
        var innerPin = Path.Combine(inner, "global.json");
        WritePin(innerPin, SubmodulePin);
        var verifier = new RecordingVerifier();
        var guard = NewGuard(probe: new FakeHostFxr(), verifier: verifier);

        var overlay = guard.Apply(_checkout, [innerSolution]);

        Assert.IsTrue(overlay.Findings.Single().OverrideApplied);
        CollectionAssert.AreEqual(
            new[]
            {
                (Path.GetFullPath(_checkout), DefaultHead, Submodule, SubmoduleHead),
                (Submodule, SubmoduleHead, inner, innerHead)
            },
            verifier.GitlinkCalls.ToArray(), "outermost first, each against its parent's verified commit");
        var call = verifier.Calls.Single();
        Assert.AreEqual((inner, innerHead), (call.Checkout, call.Head), "verified in the innermost repository");
        var entry = JsonNode.Parse(File.ReadAllText(guard.JournalPathFor(_checkout)))!["entries"]![0]!.AsObject();
        Assert.AreEqual(inner, (string)entry["work_tree"]!);
        Assert.AreEqual(innerHead, (string)entry["work_tree_head"]!);
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
    [DataRow("v1-with-submodule")]
    [DataRow("work-tree-outside")]
    [DataRow("work-tree-not-containing")]
    [DataRow("work-tree-in-git-metadata")]
    [DataRow("work-tree-relative")]
    [DataRow("head-missing")]
    [DataRow("head-malformed")]
    [DataRow("work-tree-missing")]
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
            case "work-tree-in-git-metadata": entry["work_tree"] = Path.Combine(_checkout, ".git", "modules", "libs", "sub"); break;
            case "work-tree-relative": entry["work_tree"] = "libs/sub"; break;
            case "head-missing": entry.Remove("work_tree_head"); break;
            case "head-malformed": entry["work_tree_head"] = "HEAD"; break;
            case "work-tree-missing": entry.Remove("work_tree"); break;
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
