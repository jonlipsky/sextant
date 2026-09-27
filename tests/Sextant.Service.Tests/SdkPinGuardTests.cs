using System.Text;
using Sextant.Indexer;
using Sextant.Service.SdkPin;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #113 — <see cref="SdkPinGuard"/> neutralizes ONLY a <c>global.json</c> SDK pin hostfxr cannot
/// satisfy, only inside the checkout, only for the load, and always puts the committed bytes (and
/// last-write time) back — with a journal outside the working tree that repairs the checkout after a crash.
/// hostfxr is replaced by <see cref="FakeHostFxr"/>, which fails exactly like hostfxr for a
/// <c>rollForward: disable</c> pin naming an SDK that is not "installed".
/// </summary>
[TestClass]
public sealed class SdkPinGuardTests
{
    private const string UnsatisfiablePin = """
        {
          // pinned by the repository
          "sdk": { "version": "10.0.999", "rollForward": "disable", },
          "msbuild-sdks": { "Microsoft.Build.Traversal": "4.1.0" }
        }
        """;

    private string _root = null!;
    private string _checkout = null!;
    private string _solution = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sextant_sdkpin_{Guid.NewGuid():N}");
        _checkout = Path.Combine(_root, "checkouts", "repo");
        Directory.CreateDirectory(Path.Combine(_checkout, "src", "App"));
        _solution = Path.Combine(_checkout, "Repo.slnx");
        File.WriteAllText(_solution, "<Solution><Project Path=\"src/App/App.csproj\" /></Solution>");
        File.WriteAllText(Path.Combine(_checkout, "src", "App", "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string GlobalJson => Path.Combine(_checkout, "global.json");

    private string JournalDir => Path.Combine(_root, "checkouts", SdkPinOptions.JournalDirectoryName);

    private byte[] WritePin(string path, string content = UnsatisfiablePin, DateTime? mtime = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = Encoding.UTF8.GetBytes(content);
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, mtime ?? new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        return bytes;
    }

    [TestMethod]
    public void UnsatisfiablePin_IsNeutralizedForTheLoad_AndRestoredByteForByte()
    {
        var original = WritePin(GlobalJson);
        var mtime = File.GetLastWriteTimeUtc(GlobalJson);
        var guard = new SdkPinGuard(probe: new FakeHostFxr());

        var overlay = guard.Apply(_checkout, [_solution]);

        var finding = overlay.Findings.Single();
        Assert.IsTrue(finding.OverrideApplied);
        Assert.AreEqual("global.json", finding.GlobalJsonPath, "reported checkout-relative");
        Assert.IsTrue(finding.InsideCheckout);
        Assert.AreEqual("10.0.999", finding.RequestedVersion);
        Assert.AreEqual("disable", finding.RollForward);
        Assert.AreEqual("10.0.401", finding.ResolvedSdkVersion, "the SDK hostfxr resolves once the pin is gone");
        CollectionAssert.AreEqual(new[] { "10.0.401", "9.0.305" }, finding.InstalledSdks.ToArray());
        Assert.IsNull(finding.NotOverriddenReason);

        // During the load: the pin is gone, every other section survives, and the journal lives OUTSIDE the tree.
        var during = File.ReadAllText(GlobalJson);
        Assert.IsFalse(during.Contains("\"sdk\"", StringComparison.OrdinalIgnoreCase), during);
        StringAssert.Contains(during, "Microsoft.Build.Traversal");
        var journal = Directory.GetFiles(JournalDir).Single();
        Assert.IsFalse(journal.StartsWith(_checkout + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "the restore journal must never live inside a working tree");
        using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(journal)))
        {
            var entry = doc.RootElement.GetProperty("entries")[0];
            Assert.AreEqual(GlobalJson, entry.GetProperty("path").GetString());
            CollectionAssert.AreEqual(original, Convert.FromBase64String(entry.GetProperty("original_base64").GetString()!),
                "the journal holds the committed bytes before the file is touched");
        }

        overlay.Restore();

        CollectionAssert.AreEqual(original, File.ReadAllBytes(GlobalJson), "the committed bytes are restored exactly");
        Assert.AreEqual(mtime, File.GetLastWriteTimeUtc(GlobalJson), "the last-write time is restored too");
        Assert.IsNull(overlay.RestoreError);
        Assert.AreEqual(0, Directory.GetFiles(JournalDir).Length, "a clean restore deletes the journal");
        Assert.AreEqual(0, Directory.GetFiles(_checkout, "*.tmp", SearchOption.AllDirectories).Length, "no temp file is left behind");

        overlay.Restore(); // idempotent
        CollectionAssert.AreEqual(original, File.ReadAllBytes(GlobalJson));
    }

    [TestMethod]
    public void ResolvablePin_IsNeverTouched()
    {
        var original = WritePin(GlobalJson, """{ "sdk": { "version": "10.0.100", "rollForward": "latestFeature" } }""");
        var mtime = File.GetLastWriteTimeUtc(GlobalJson);
        var hostFxr = new FakeHostFxr();

        var overlay = new SdkPinGuard(probe: hostFxr).Apply(_checkout, [_solution]);
        overlay.Restore();

        Assert.AreEqual(0, overlay.Findings.Count, "a pin hostfxr satisfies is not a finding");
        CollectionAssert.AreEqual(original, File.ReadAllBytes(GlobalJson));
        Assert.AreEqual(mtime, File.GetLastWriteTimeUtc(GlobalJson));
        Assert.IsFalse(Directory.Exists(JournalDir), "no journal is written when nothing is overridden");
        Assert.AreEqual(0, hostFxr.ListCalls, "installed SDKs are only listed when a pin fails");
    }

    [TestMethod]
    public void NoGlobalJson_NoFindings()
    {
        var overlay = new SdkPinGuard(probe: new FakeHostFxr()).Apply(_checkout, [_solution]);
        Assert.AreEqual(0, overlay.Findings.Count);
        overlay.Restore();
    }

    [TestMethod]
    public void OverrideDisabled_ReportsTheFinding_WithoutModifyingTheFile()
    {
        var original = WritePin(GlobalJson);
        var guard = new SdkPinGuard(new SdkPinOptions { OverrideEnabled = false }, new FakeHostFxr());

        var overlay = guard.Apply(_checkout, [_solution]);

        var finding = overlay.Findings.Single();
        Assert.IsFalse(finding.OverrideApplied);
        StringAssert.Contains(finding.NotOverriddenReason, "SEXTANT_SERVICE_SDK_PIN_OVERRIDE");
        Assert.AreEqual("10.0.999", finding.RequestedVersion);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(GlobalJson));
        Assert.IsFalse(Directory.Exists(JournalDir));
        Assert.AreEqual(0, overlay.Overridden.Count());
    }

    [TestMethod]
    public void PinOutsideTheCheckout_IsNeverModified()
    {
        var outside = Path.Combine(_root, "global.json");
        var original = WritePin(outside);

        var overlay = new SdkPinGuard(probe: new FakeHostFxr()).Apply(_checkout, [_solution]);

        var finding = overlay.Findings.Single();
        Assert.IsFalse(finding.InsideCheckout);
        Assert.IsFalse(finding.OverrideApplied);
        Assert.AreEqual(outside, finding.GlobalJsonPath, "a pin outside the checkout is reported by absolute path");
        StringAssert.Contains(finding.NotOverriddenReason, "outside the checkout");
        CollectionAssert.AreEqual(original, File.ReadAllBytes(outside));
    }

    [TestMethod]
    public void SymlinkedGlobalJson_IsNeverModified()
    {
        var target = Path.Combine(_root, "shared-global.json");
        var original = WritePin(target);
        try
        {
            File.CreateSymbolicLink(GlobalJson, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Inconclusive($"symbolic links cannot be created here: {ex.Message}");
        }

        var overlay = new SdkPinGuard(probe: new FakeHostFxr()).Apply(_checkout, [_solution]);

        var finding = overlay.Findings.Single();
        Assert.IsFalse(finding.OverrideApplied);
        StringAssert.Contains(finding.NotOverriddenReason, "symbolic link");
        CollectionAssert.AreEqual(original, File.ReadAllBytes(target), "the link target is untouched");
    }

    [TestMethod]
    public void UnparseableGlobalJson_IsReported_NotModified()
    {
        var original = WritePin(GlobalJson, "{ \"sdk\": { \"version\": \"10.0.999\", \"rollForward\": \"disable\" ");
        var hostFxr = new FakeHostFxr { AlwaysFails = { _checkout } };

        var overlay = new SdkPinGuard(probe: hostFxr).Apply(_checkout, [_solution]);

        var finding = overlay.Findings.Single();
        Assert.IsFalse(finding.OverrideApplied);
        StringAssert.Contains(finding.NotOverriddenReason, "could not be parsed");
        Assert.AreEqual("10.0.999", finding.RequestedVersion, "falls back to hostfxr's reported request");
        CollectionAssert.AreEqual(original, File.ReadAllBytes(GlobalJson));
    }

    [TestMethod]
    public void NeutralizingThatDoesNotHelp_IsUndoneImmediately_AndNotReportedAsAnOverride()
    {
        var original = WritePin(GlobalJson);
        var mtime = File.GetLastWriteTimeUtc(GlobalJson);
        var hostFxr = new FakeHostFxr { AlwaysFails = { _checkout } };

        var overlay = new SdkPinGuard(probe: hostFxr).Apply(_checkout, [_solution]);

        var finding = overlay.Findings.Single();
        Assert.IsFalse(finding.OverrideApplied);
        StringAssert.Contains(finding.NotOverriddenReason, "did not make an installed SDK resolvable");
        CollectionAssert.AreEqual(original, File.ReadAllBytes(GlobalJson), "restored before Apply returns");
        Assert.AreEqual(mtime, File.GetLastWriteTimeUtc(GlobalJson));
        Assert.AreEqual(0, Directory.GetFiles(JournalDir).Length, "nothing left to recover");
        overlay.Restore();
        Assert.IsNull(overlay.RestoreError);
    }

    [TestMethod]
    public void PinGoverningADeclaredProjectDirectory_IsDetected()
    {
        // The solution directory resolves, but a project directory has its own unsatisfiable pin — the
        // per-project (multi-solution) load path would launch the BuildHost from there.
        var projectPin = Path.Combine(_checkout, "src", "App", "global.json");
        var original = WritePin(projectPin);

        var overlay = new SdkPinGuard(probe: new FakeHostFxr()).Apply(_checkout, [_solution]);

        var finding = overlay.Findings.Single();
        Assert.AreEqual("src/App/global.json", finding.GlobalJsonPath);
        Assert.IsTrue(finding.OverrideApplied);
        overlay.Restore();
        CollectionAssert.AreEqual(original, File.ReadAllBytes(projectPin));
    }

    [TestMethod]
    public void ForeignContentDuringTheLoad_IsNeverClobbered_AndFailsTheRestore()
    {
        WritePin(GlobalJson);
        var guard = new SdkPinGuard(probe: new FakeHostFxr());
        var overlay = guard.Apply(_checkout, [_solution]);

        const string foreign = "{ \"changed\": \"by someone else\" }";
        File.WriteAllText(GlobalJson, foreign);
        overlay.Restore();

        Assert.IsNotNull(overlay.RestoreError);
        StringAssert.Contains(overlay.RestoreError, "changed by something else");
        Assert.AreEqual(foreign, File.ReadAllText(GlobalJson), "foreign content is left as-is");
        Assert.AreEqual(1, Directory.GetFiles(JournalDir).Length, "the journal is kept for inspection/recovery");

        // Recovery FAILS CLOSED: the checkout is still at the same commit, so it must not be reused/indexed.
        Assert.IsFalse(guard.Recover(_checkout));
        Assert.AreEqual(foreign, File.ReadAllText(GlobalJson));
        Assert.AreEqual(1, Directory.GetFiles(JournalDir).Length, "the journal keeps blocking the checkout");
    }

    [TestMethod]
    public void Recovery_AfterTheCheckoutMovedToAnotherCommit_RetiresTheJournal()
    {
        WriteHead("1111111111111111111111111111111111111111");
        WritePin(GlobalJson);
        var projectPin = Path.Combine(_checkout, "src", "App", "global.json");
        WritePin(projectPin);
        _ = new SdkPinGuard(probe: new FakeHostFxr()).Apply(_checkout, [_solution]); // "crash"

        // The checkout was then re-checked-out at another commit, whose global.json differs / does not exist.
        WriteHead("2222222222222222222222222222222222222222");
        const string nextCommit = "{ \"sdk\": { \"version\": \"10.0.401\" } }";
        File.WriteAllText(GlobalJson, nextCommit);
        File.Delete(projectPin);

        Assert.IsTrue(new SdkPinGuard(probe: new FakeHostFxr()).Recover(_checkout));
        Assert.AreEqual(nextCommit, File.ReadAllText(GlobalJson), "the other commit's content is left alone");
        Assert.IsFalse(File.Exists(projectPin), "recovery never recreates a file that is gone");
        Assert.AreEqual(0, Directory.GetFiles(JournalDir).Length);
    }

    [TestMethod]
    public void Recovery_OfAMissingFileAtTheSameCommit_FailsClosed()
    {
        WriteHead("1111111111111111111111111111111111111111");
        WritePin(GlobalJson);
        _ = new SdkPinGuard(probe: new FakeHostFxr()).Apply(_checkout, [_solution]); // "crash"
        File.Delete(GlobalJson);

        Assert.IsFalse(new SdkPinGuard(probe: new FakeHostFxr()).Recover(_checkout));
        Assert.IsFalse(File.Exists(GlobalJson), "recovery never recreates a file that is gone");
        Assert.AreEqual(1, Directory.GetFiles(JournalDir).Length);
    }

    [TestMethod]
    public void Recovery_OfADeletedCheckout_RetiresTheJournal()
    {
        var options = new SdkPinOptions { JournalRoot = JournalDir };
        WritePin(GlobalJson);
        _ = new SdkPinGuard(options, new FakeHostFxr()).Apply(_checkout, [_solution]); // "crash"
        Directory.Delete(_checkout, recursive: true); // an operator removed it; the next clone is fresh

        Assert.AreEqual(1, new SdkPinGuard(options, new FakeHostFxr()).RecoverAll());
        Assert.AreEqual(0, Directory.GetFiles(JournalDir).Length);
    }

    [TestMethod]
    public void CrashBetweenNeutralizeAndRestore_IsRepairedByTheNextRun()
    {
        var original = WritePin(GlobalJson);
        var mtime = File.GetLastWriteTimeUtc(GlobalJson);
        _ = new SdkPinGuard(probe: new FakeHostFxr()).Apply(_checkout, [_solution]); // never restored: "crash"
        Assert.IsFalse(File.ReadAllText(GlobalJson).Contains("\"sdk\"", StringComparison.Ordinal));

        var nextRun = new SdkPinGuard(probe: new FakeHostFxr());
        Assert.IsTrue(nextRun.Recover(_checkout));

        CollectionAssert.AreEqual(original, File.ReadAllBytes(GlobalJson));
        Assert.AreEqual(mtime, File.GetLastWriteTimeUtc(GlobalJson));
        Assert.AreEqual(0, Directory.GetFiles(JournalDir).Length);
        Assert.IsTrue(nextRun.Recover(_checkout), "no journal ⇒ nothing to do");
    }

    [TestMethod]
    public void RecoverAll_ReplaysEveryJournalUnderTheConfiguredRoot()
    {
        var second = Path.Combine(_root, "checkouts", "other");
        Directory.CreateDirectory(second);
        File.Copy(_solution, Path.Combine(second, "Repo.slnx"));
        Directory.CreateDirectory(Path.Combine(second, "src", "App"));
        var originalA = WritePin(GlobalJson);
        var originalB = WritePin(Path.Combine(second, "global.json"));
        var options = new SdkPinOptions { JournalRoot = Path.Combine(_root, "journal") };

        var guard = new SdkPinGuard(options, new FakeHostFxr());
        _ = guard.Apply(_checkout, [_solution]);
        _ = guard.Apply(second, [Path.Combine(second, "Repo.slnx")]);
        Assert.AreEqual(2, Directory.GetFiles(options.JournalRoot).Length);

        Assert.AreEqual(2, new SdkPinGuard(options, new FakeHostFxr()).RecoverAll());

        CollectionAssert.AreEqual(originalA, File.ReadAllBytes(GlobalJson));
        CollectionAssert.AreEqual(originalB, File.ReadAllBytes(Path.Combine(second, "global.json")));
        Assert.AreEqual(0, Directory.GetFiles(options.JournalRoot).Length);
    }

    [TestMethod]
    public void RecoverAll_WithoutAConfiguredRoot_IsANoOp() =>
        Assert.AreEqual(0, new SdkPinGuard(probe: new FakeHostFxr()).RecoverAll());

    [TestMethod]
    public void Recovery_LeavesOriginalContentAlone()
    {
        var original = WritePin(GlobalJson);
        _ = new SdkPinGuard(probe: new FakeHostFxr()).Apply(_checkout, [_solution]); // "crash"

        File.WriteAllBytes(GlobalJson, original); // e.g. an operator already restored it

        Assert.IsTrue(new SdkPinGuard(probe: new FakeHostFxr()).Recover(_checkout));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(GlobalJson));
        Assert.AreEqual(0, Directory.GetFiles(JournalDir).Length);
    }

    [TestMethod]
    public void RepositoryFilesNamedLikeATempFile_AreNeverTouched()
    {
        // A repository may legitimately contain any file name — including the guard's own temp-file shapes.
        var legacyName = Path.Combine(_checkout, ".global.json.sextant-sdk-pin.tmp");
        var lookalike = Path.Combine(_checkout, ".global.json.sextant-sdk-pin-0123456789abcdef0123456789abcdef.tmp");
        File.WriteAllText(legacyName, "tracked");
        File.WriteAllText(lookalike, "tracked too");
        var original = WritePin(GlobalJson);

        var overlay = new SdkPinGuard(probe: new FakeHostFxr()).Apply(_checkout, [_solution]);
        Assert.IsTrue(overlay.Findings.Single().OverrideApplied);
        overlay.Restore();

        CollectionAssert.AreEqual(original, File.ReadAllBytes(GlobalJson));
        Assert.AreEqual("tracked", File.ReadAllText(legacyName));
        Assert.AreEqual("tracked too", File.ReadAllText(lookalike));
        Assert.AreEqual(2, Directory.GetFiles(_checkout, "*.tmp").Length, "only the repository's own files remain");
    }

    [TestMethod]
    public void NonMissingSdkFailure_IsReported_NotOverridden()
    {
        // hostfxr failed for some other reason (e.g. a malformed sdk section) — removing the section would
        // "fix" something that is not an absent SDK band, so the guard refuses.
        var original = WritePin(GlobalJson);
        var overlay = new SdkPinGuard(probe: new FakeHostFxr { MissingSdk = false }).Apply(_checkout, [_solution]);

        var finding = overlay.Findings.Single();
        Assert.IsFalse(finding.OverrideApplied);
        StringAssert.Contains(finding.NotOverriddenReason, "other than a missing SDK version");
        CollectionAssert.AreEqual(original, File.ReadAllBytes(GlobalJson));
        Assert.IsFalse(Directory.Exists(JournalDir));
    }

    [TestMethod]
    [DataRow("""{ "sdk": "10.0.999" }""")]
    [DataRow("""{ "sdk": { "rollForward": "disable" } }""")]
    public void SdkSectionWithoutAVersion_IsReported_NotOverridden(string json)
    {
        var original = WritePin(GlobalJson, json);
        var overlay = new SdkPinGuard(probe: new FakeHostFxr { AlwaysFails = { _checkout } }).Apply(_checkout, [_solution]);

        var finding = overlay.Findings.Single();
        Assert.IsFalse(finding.OverrideApplied);
        StringAssert.Contains(finding.NotOverriddenReason, "does not pin a version");
        CollectionAssert.AreEqual(original, File.ReadAllBytes(GlobalJson));
    }

    [TestMethod]
    public void TamperedJournal_IsNeverReplayed()
    {
        var guard = new SdkPinGuard(probe: new FakeHostFxr());
        var victim = Path.Combine(_root, "outside.txt");
        File.WriteAllText(victim, "keep me");
        var repoFile = Path.Combine(_checkout, "README.md");
        File.WriteAllText(repoFile, "tracked");
        var content = Convert.ToBase64String(Encoding.UTF8.GetBytes("pwned"));
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("pwned")));
        var victimSha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(victim)));

        void WriteJournal(string checkoutDir, string entryPath, string tempPath)
        {
            var journal = guard.JournalPathFor(_checkout);
            Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
            File.WriteAllText(journal, System.Text.Json.JsonSerializer.Serialize(new
            {
                version = 1,
                checkout_dir = checkoutDir,
                entries = new[]
                {
                    new
                    {
                        path = entryPath, temp_path = tempPath, original_base64 = content, original_sha256 = sha,
                        neutralized_sha256 = victimSha
                    }
                }
            }));
        }

        var ownTemp = Path.Combine(_checkout, ".global.json.sextant-sdk-pin-0123456789abcdef0123456789abcdef.tmp");
        WriteJournal(_checkout, victim, ownTemp); // an entry outside the checkout
        Assert.IsFalse(guard.Recover(_checkout));
        WriteJournal(_checkout, Path.Combine(_checkout, "global.json"), repoFile); // a temp path that is a repo file
        Assert.IsFalse(guard.Recover(_checkout));
        WriteJournal(_root, Path.Combine(_root, "global.json"), Path.Combine(_root, Path.GetFileName(ownTemp))); // wrong checkout
        Assert.IsFalse(guard.Recover(_checkout));

        Assert.AreEqual("keep me", File.ReadAllText(victim), "nothing outside the checkout is ever written");
        Assert.AreEqual("tracked", File.ReadAllText(repoFile), "a journaled temp path never deletes a repository file");
        Assert.IsFalse(File.Exists(Path.Combine(_root, "global.json")));
    }

    [TestMethod]
    [DataRow("empty-entries")]
    [DataRow("null-entries")]
    [DataRow("null-entry")]
    [DataRow("corrupt-head")]
    [DataRow("null-content")]
    [DataRow("bad-checksum-shape")]
    [DataRow("ticks-too-large")]
    [DataRow("negative-ticks")]
    [DataRow("mode-out-of-range")]
    [DataRow("duplicate-entry")]
    [DataRow("nul-in-path")]
    public void MalformedJournal_IsKept_BlocksTheCheckout_AndNeverThrows(string mutation)
    {
        WritePin(GlobalJson);
        var guard = new SdkPinGuard(probe: new FakeHostFxr());
        _ = guard.Apply(_checkout, [_solution]); // "crash": a well-formed journal, then tamper with its shape
        var neutralized = File.ReadAllBytes(GlobalJson);
        var journalPath = guard.JournalPathFor(_checkout);
        var journal = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(journalPath))!.AsObject();
        var entries = journal["entries"]!.AsArray();
        var entry = entries[0]!.AsObject();
        switch (mutation)
        {
            case "empty-entries": entries.Clear(); break;
            case "null-entries": journal["entries"] = null; break;
            case "null-entry": entries[0] = null; break;
            case "corrupt-head": journal["head"] = "corrupt"; break;
            case "null-content": entry["original_base64"] = null; break;
            case "bad-checksum-shape": entry["neutralized_sha256"] = "XYZ"; break;
            case "ticks-too-large": entry["last_write_time_utc_ticks"] = long.MaxValue; break;
            case "negative-ticks": entry["last_write_time_utc_ticks"] = -1; break;
            case "mode-out-of-range": entry["unix_mode"] = 99999; break;
            case "duplicate-entry": entries.Add(entry.DeepClone()); break;
            case "nul-in-path": entry["path"] = Path.Combine(_checkout, "a\0b", "global.json"); break;
            default: Assert.Fail(mutation); break;
        }
        File.WriteAllText(journalPath, journal.ToJsonString());

        Assert.IsFalse(guard.Recover(_checkout), $"{mutation}: an invalid journal must keep blocking the checkout");
        Assert.IsTrue(File.Exists(journalPath), $"{mutation}: kept for an operator to inspect");
        CollectionAssert.AreEqual(neutralized, File.ReadAllBytes(GlobalJson), $"{mutation}: nothing was written");
    }

    [TestMethod]
    public void RecoverAll_OneBadJournal_NeverStopsTheOthers()
    {
        var options = new SdkPinOptions { JournalRoot = JournalDir };
        var original = WritePin(GlobalJson);
        _ = new SdkPinGuard(options, new FakeHostFxr()).Apply(_checkout, [_solution]); // "crash"
        File.WriteAllText(Path.Combine(JournalDir, "garbage-0123456789ab.json"), "{ \"version\": 1, \"checkout_dir\": null, \"entries\": null }");

        Assert.AreEqual(1, new SdkPinGuard(options, new FakeHostFxr()).RecoverAll());
        CollectionAssert.AreEqual(original, File.ReadAllBytes(GlobalJson), "the valid journal was still replayed");
        Assert.IsTrue(File.Exists(Path.Combine(JournalDir, "garbage-0123456789ab.json")), "the bad one is kept");
    }

    [TestMethod]
    public void Recovery_AfterTheCheckoutMoved_NeverWrites_EvenWhenTheNewCommitHoldsTheNeutralizedBytes()
    {
        WriteHead("1111111111111111111111111111111111111111");
        WritePin(GlobalJson);
        var guard = new SdkPinGuard(probe: new FakeHostFxr());
        _ = guard.Apply(_checkout, [_solution]); // "crash"
        var neutralized = File.ReadAllBytes(GlobalJson);
        string tempPath;
        using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(guard.JournalPathFor(_checkout))))
            tempPath = doc.RootElement.GetProperty("entries")[0].GetProperty("temp_path").GetString()!;

        // Re-provisioned at a commit whose committed global.json is byte-identical to the neutralized form, and
        // which even holds a file at the journaled temp-file path: both belong to the new commit now.
        WriteHead("2222222222222222222222222222222222222222");
        File.WriteAllText(tempPath, "the new commit's file");

        Assert.IsTrue(new SdkPinGuard(probe: new FakeHostFxr()).Recover(_checkout));
        CollectionAssert.AreEqual(neutralized, File.ReadAllBytes(GlobalJson), "the other commit's content is never overwritten");
        Assert.AreEqual("the new commit's file", File.ReadAllText(tempPath), "nothing in the moved checkout is deleted either");
        Assert.AreEqual(0, Directory.GetFiles(JournalDir).Length);
    }

    [TestMethod]
    public void Restore_ThroughADirectorySwappedForALinkDuringTheLoad_IsRefused()
    {
        var projectDir = Path.Combine(_checkout, "src", "App");
        var projectPin = Path.Combine(projectDir, "global.json");
        WritePin(projectPin);
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);

        var overlay = new SdkPinGuard(probe: new FakeHostFxr()).Apply(_checkout, [_solution]);
        Assert.IsTrue(overlay.Findings.Single().OverrideApplied);

        // Something run by the load replaces the pin's directory with a link that points outside the checkout.
        Directory.Move(projectDir, projectDir + ".moved");
        try
        {
            Directory.CreateSymbolicLink(projectDir, outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Inconclusive($"symbolic links cannot be created here: {ex.Message}");
            return;
        }

        overlay.Restore();

        StringAssert.Contains(overlay.RestoreError, "symbolic link");
        Assert.AreEqual(0, Directory.GetFileSystemEntries(outside).Length, "nothing is written through the link");
        Assert.AreEqual(1, Directory.GetFiles(JournalDir).Length, "the journal is kept, so the checkout stays blocked");
        Assert.IsFalse(new SdkPinGuard(probe: new FakeHostFxr()).Recover(_checkout), "recovery refuses the link too");
        Assert.AreEqual(0, Directory.GetFileSystemEntries(outside).Length);
    }

    [TestMethod]
    public void RelativeJournalRoot_IsAnchored_SoACrashIsStillRecoverable()
    {
        var original = WritePin(GlobalJson);
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, Path.Combine(_root, "journal"));
        if (Path.IsPathRooted(relative))
        {
            Assert.Inconclusive("the temp directory is on another volume than the working directory");
            return;
        }
        var options = new SdkPinOptions { JournalRoot = relative };
        _ = new SdkPinGuard(options, new FakeHostFxr()).Apply(_checkout, [_solution]); // "crash"

        Assert.AreEqual(1, new SdkPinGuard(options, new FakeHostFxr()).RecoverAll());
        CollectionAssert.AreEqual(original, File.ReadAllBytes(GlobalJson));
    }

    [TestMethod]
    public void JournalRootInsideTheCheckout_IsRefused_WithoutModifyingTheFile()
    {
        var original = WritePin(GlobalJson);
        var options = new SdkPinOptions { JournalRoot = Path.Combine(_checkout, "journal") };

        var overlay = new SdkPinGuard(options, new FakeHostFxr()).Apply(_checkout, [_solution]);

        var finding = overlay.Findings.Single();
        Assert.IsFalse(finding.OverrideApplied);
        StringAssert.Contains(finding.NotOverriddenReason, "journal would be inside the checkout");
        CollectionAssert.AreEqual(original, File.ReadAllBytes(GlobalJson));
        Assert.IsFalse(Directory.Exists(options.JournalRoot));
    }

    [TestMethod]
    public void Recovery_WhenTheJournaledHeadCanNoLongerBeRead_FailsClosed()
    {
        WriteHead("1111111111111111111111111111111111111111");
        WritePin(GlobalJson);
        _ = new SdkPinGuard(probe: new FakeHostFxr()).Apply(_checkout, [_solution]); // "crash"
        var neutralized = File.ReadAllBytes(GlobalJson);
        Directory.Delete(Path.Combine(_checkout, ".git"), recursive: true);

        Assert.IsFalse(new SdkPinGuard(probe: new FakeHostFxr()).Recover(_checkout), "it cannot tell whether the commit moved");
        CollectionAssert.AreEqual(neutralized, File.ReadAllBytes(GlobalJson));
        Assert.AreEqual(1, Directory.GetFiles(JournalDir).Length);
    }

    [TestMethod]
    public void InaccessibleJournalDirectory_IsNeverMistakenForNoJournal()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("POSIX permission bits do not apply on Windows");
            return;
        }

        WritePin(GlobalJson);
        _ = new SdkPinGuard(probe: new FakeHostFxr()).Apply(_checkout, [_solution]); // "crash"
        var mode = File.GetUnixFileMode(JournalDir);
        File.SetUnixFileMode(JournalDir, UnixFileMode.None);
        try
        {
            if (TryListFiles(JournalDir))
            {
                Assert.Inconclusive("running with privileges that bypass directory permissions");
                return;
            }
            Assert.IsFalse(new SdkPinGuard(probe: new FakeHostFxr()).Recover(_checkout),
                "an unreadable journal directory must block the checkout, not read as 'no journal'");
        }
        finally
        {
            File.SetUnixFileMode(JournalDir, mode);
        }
        Assert.AreEqual(1, Directory.GetFiles(JournalDir).Length);
    }

    private static bool TryListFiles(string directory)
    {
        try
        {
            _ = Directory.GetFiles(directory);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    [TestMethod]
    public void SymlinkedCheckoutDirectory_IsNeverModified()
    {
        var real = Path.Combine(_root, "real-checkout");
        Directory.CreateDirectory(real);
        var target = Path.Combine(real, "global.json");
        var original = WritePin(target);
        File.Copy(_solution, Path.Combine(real, "Repo.slnx"));
        var linked = Path.Combine(_root, "checkouts", "linked");
        try
        {
            Directory.CreateSymbolicLink(linked, real);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Inconclusive($"symbolic links cannot be created here: {ex.Message}");
            return;
        }

        var overlay = new SdkPinGuard(probe: new FakeHostFxr()).Apply(linked, [Path.Combine(linked, "Repo.slnx")]);

        var finding = overlay.Findings.Single();
        Assert.IsFalse(finding.OverrideApplied);
        StringAssert.Contains(finding.NotOverriddenReason, "checkout directory is a symbolic link");
        CollectionAssert.AreEqual(original, File.ReadAllBytes(target), "the link target is untouched");
    }

    [TestMethod]
    [DataRow("1.2.0")]
    [DataRow("10.0.3")]
    [DataRow("10.0.099")]
    [DataRow("latest")]
    public void MalformedSdkVersion_IsReported_NotOverridden(string version)
    {
        var original = WritePin(GlobalJson, $$"""{ "sdk": { "version": "{{version}}", "rollForward": "disable" } }""");

        var overlay = new SdkPinGuard(probe: new FakeHostFxr()).Apply(_checkout, [_solution]);

        var finding = overlay.Findings.Single();
        Assert.IsFalse(finding.OverrideApplied);
        StringAssert.Contains(finding.NotOverriddenReason, "not a well-formed .NET SDK version");
        CollectionAssert.AreEqual(original, File.ReadAllBytes(GlobalJson));
        Assert.IsFalse(Directory.Exists(JournalDir));
    }

    [TestMethod]
    [DataRow("10.0.300", true)]
    [DataRow("9.0.999", true)]
    [DataRow("10.0.100-preview.7.25380.108", true)]
    [DataRow("10.0.300+build.1", true)]
    [DataRow("10.0.300-rc.0", true)]
    [DataRow("10.0.300-0a", true)]
    [DataRow("10.0.300-preview.01", false)]
    [DataRow("\uFF11\uFF10.0.300", false)]
    [DataRow("10.0.\u0663\u0660\u0660", false)]
    [DataRow("1.2.0", false)]
    [DataRow("10.0.3", false)]
    [DataRow("10.0.099", false)]
    [DataRow("010.0.300", false)]
    [DataRow("10.0", false)]
    [DataRow("10.0.300.1", false)]
    [DataRow("latest", false)]
    [DataRow("", false)]
    [DataRow(null, false)]
    public void IsSdkVersion_AcceptsOnlyAFeatureBandVersion(string? version, bool expected) =>
        Assert.AreEqual(expected, GlobalJsonSdkPin.IsSdkVersion(version), version);

    [TestMethod]
    public void CheckoutHead_ReadsDetachedLooseAndPackedRefs()
    {
        const string a = "1111111111111111111111111111111111111111";
        const string b = "2222222222222222222222222222222222222222";
        Assert.IsNull(CheckoutHead.TryRead(_checkout), "not a git checkout");

        WriteHead(a.ToUpperInvariant());
        Assert.AreEqual(a, CheckoutHead.TryRead(_checkout), "a detached HEAD, normalized to lowercase");

        var gitDir = Path.Combine(_checkout, ".git");
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/main\n");
        Assert.IsNull(CheckoutHead.TryRead(_checkout), "an unresolvable ref");
        File.WriteAllText(Path.Combine(gitDir, "packed-refs"), $"# pack-refs with: peeled\n{b} refs/heads/main\n");
        Assert.AreEqual(b, CheckoutHead.TryRead(_checkout), "a packed ref");
        Directory.CreateDirectory(Path.Combine(gitDir, "refs", "heads"));
        File.WriteAllText(Path.Combine(gitDir, "refs", "heads", "main"), a + "\n");
        Assert.AreEqual(a, CheckoutHead.TryRead(_checkout), "a loose ref wins over the packed one");

        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/../../escape\n");
        Assert.IsNull(CheckoutHead.TryRead(_checkout), "a ref that escapes .git is refused");

        // A worktree-style ".git" file pointing at the real git directory.
        var worktree = Path.Combine(_root, "wt");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), b + "\n");
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {gitDir}\n");
        Assert.AreEqual(b, CheckoutHead.TryRead(worktree));
    }

    private void WriteHead(string sha)
    {
        var gitDir = Path.Combine(_checkout, ".git");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), sha + "\n");
    }

    [TestMethod]
    public void UnreadableJournal_BlocksTheCheckout_AndIsKept()
    {
        var guard = new SdkPinGuard(probe: new FakeHostFxr());
        var journal = guard.JournalPathFor(_checkout);
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        File.WriteAllText(journal, "{ not json");

        Assert.IsFalse(guard.Recover(_checkout), "a journal that cannot be replayed must block reuse of the checkout");
        Assert.IsTrue(File.Exists(journal), "kept for an operator to inspect");
    }

    [TestMethod]
    public void JournalPath_IsOutsideTheCheckout_AndDistinctPerCheckout()
    {
        var guard = new SdkPinGuard(probe: new FakeHostFxr());
        var a = guard.JournalPathFor(_checkout);
        var b = guard.JournalPathFor(Path.Combine(_root, "checkouts", "repo2"));

        Assert.AreEqual(JournalDir, Path.GetDirectoryName(a));
        Assert.AreNotEqual(a, b);
        Assert.AreEqual(a, guard.JournalPathFor(_checkout + Path.DirectorySeparatorChar), "trailing separator is irrelevant");
    }

    [TestMethod]
    public void UnixMode_IsPreserved()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("unix file modes do not apply on Windows");
            return;
        }

        var original = WritePin(GlobalJson);
        const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        File.SetUnixFileMode(GlobalJson, mode);

        var overlay = new SdkPinGuard(probe: new FakeHostFxr()).Apply(_checkout, [_solution]);
        Assert.AreEqual(mode, File.GetUnixFileMode(GlobalJson), "the neutralized file keeps the mode");
        overlay.Restore();

        Assert.AreEqual(mode, File.GetUnixFileMode(GlobalJson));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(GlobalJson));
    }

    [TestMethod]
    public void ThrowingProbe_NeverBlocksIndexing()
    {
        var original = WritePin(GlobalJson);
        var overlay = new SdkPinGuard(probe: new FakeHostFxr { Throws = true }).Apply(_checkout, [_solution]);

        Assert.AreEqual(0, overlay.Findings.Count, "an unavailable probe behaves exactly as before #113");
        CollectionAssert.AreEqual(original, File.ReadAllBytes(GlobalJson));
    }

    // ---- GlobalJsonSdkPin ---------------------------------------------------------------------------

    [TestMethod]
    public void Neutralize_RemovesOnlyTheSdkSection_ToleratingCommentsTrailingCommasAndBom()
    {
        var content = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(UnsatisfiablePin)).ToArray();

        Assert.IsTrue(GlobalJsonSdkPin.TryRead(content, out var pin, out _));
        Assert.AreEqual("10.0.999", pin.Version);
        Assert.AreEqual("disable", pin.RollForward);

        Assert.IsTrue(GlobalJsonSdkPin.TryNeutralize(content, out var neutralized, out var error), error);
        var text = Encoding.UTF8.GetString(neutralized);
        Assert.IsFalse(text.Contains("\"sdk\"", StringComparison.OrdinalIgnoreCase), text);
        StringAssert.Contains(text, "\"msbuild-sdks\"");
        StringAssert.Contains(text, "\"Microsoft.Build.Traversal\": \"4.1.0\"");
    }

    [TestMethod]
    public void Neutralize_IsCaseInsensitiveOnTheSdkKey()
    {
        var content = Encoding.UTF8.GetBytes("""{ "SDK": { "version": "9.0.999", "rollForward": "disable" }, "test": { "runner": "x" } }""");

        Assert.IsTrue(GlobalJsonSdkPin.TryRead(content, out var pin, out _));
        Assert.AreEqual("9.0.999", pin.Version);
        Assert.IsTrue(GlobalJsonSdkPin.TryNeutralize(content, out var neutralized, out _));
        var text = Encoding.UTF8.GetString(neutralized);
        Assert.IsFalse(text.Contains("SDK", StringComparison.Ordinal), text);
        StringAssert.Contains(text, "\"runner\"");
    }

    [TestMethod]
    [DataRow("{ \"msbuild-sdks\": { \"A\": \"1.0.0\" } }", "no \"sdk\" section")]
    [DataRow("[1, 2]", "not a JSON object")]
    [DataRow("{ \"sdk\": ", "could not be parsed")]
    [DataRow("{ \"sdk\": { \"version\": \"1.0.0\" }, \"sdk\": { \"version\": \"2.0.0\" } }", "could not be parsed")]
    public void Neutralize_RefusesContentItCannotSafelyRewrite(string json, string expected)
    {
        Assert.IsFalse(GlobalJsonSdkPin.TryNeutralize(Encoding.UTF8.GetBytes(json), out _, out var error));
        StringAssert.Contains(error, expected);
    }

    /// <summary>
    /// Stands in for hostfxr: resolution of a directory fails when its nearest <c>global.json</c> pins an SDK
    /// that is not installed with <c>rollForward: disable</c> (or the directory is in <see cref="AlwaysFails"/>).
    /// </summary>
    internal sealed class FakeHostFxr : ISdkResolutionProbe
    {
        private static readonly string[] Installed = ["10.0.401", "9.0.305"];

        public HashSet<string> AlwaysFails { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Throws { get; init; }
        public bool MissingSdk { get; init; } = true;
        public int ListCalls { get; private set; }

        public SdkResolutionProbeResult Probe(string workingDirectory)
        {
            if (Throws)
                throw new InvalidOperationException("hostfxr is unavailable");

            var pinPath = GlobalJsonLocator.FindNearest(workingDirectory);
            GlobalJsonSdkPin.Pin pin = default;
            var readable = pinPath is not null && GlobalJsonSdkPin.TryRead(File.ReadAllBytes(pinPath), out pin, out _);
            var fails = AlwaysFails.Contains(Path.TrimEndingDirectorySeparator(workingDirectory))
                || (readable && pin.Version is { } v && !Installed.Contains(v)
                    && string.Equals(pin.RollForward, "disable", StringComparison.OrdinalIgnoreCase));

            return fails
                ? new SdkResolutionProbeResult
                {
                    Error = new HostFxrSdkResolutionError
                    {
                        RequestedVersion = pin.Version ?? "10.0.999", GlobalJsonPath = pinPath, IsMissingSdk = MissingSdk
                    }
                }
                : new SdkResolutionProbeResult { ResolvedSdkVersion = Installed[0] };
        }

        public IReadOnlyList<string> ListInstalledSdks()
        {
            ListCalls++;
            return Installed;
        }
    }
}
