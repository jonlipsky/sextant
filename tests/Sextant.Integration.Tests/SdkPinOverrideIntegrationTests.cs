using System.Diagnostics;
using Sextant.Core;
using Sextant.Indexer;
using Sextant.Service;
using Sextant.Service.Sandbox;
using Sextant.Service.SdkPin;
using Sextant.Store;
using Sextant.TestSupport;

namespace Sextant.Integration.Tests;

/// <summary>
/// Issue #113 end-to-end over the REAL Roslyn/MSBuild load: a checkout whose <c>global.json</c> pins an SDK
/// band this machine does not have with <c>"rollForward": "disable"</c> makes the BuildHost die in
/// <c>hostfxr_resolve_sdk2</c>. The service worker must still publish a snapshot with the installed SDK
/// (override on), record a typed <c>sdk_pin_overridden</c> diagnostic + coverage provenance, and leave the
/// persistent checkout byte-identical to its commit (content, mtime, <c>git status</c>, and the project's
/// <see cref="EvaluationFingerprint"/>). With the override off the job fails with a TYPED
/// <c>sdk_resolution_failed</c> diagnostic, and a pin that resolves is never touched.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public class SdkPinOverrideIntegrationTests
{
    // No real SDK band will ever be 10.0.999, so hostfxr cannot satisfy this pin on any machine.
    private const string UnsatisfiablePin =
        "{\n  \"sdk\": {\n    \"version\": \"10.0.999\",\n    \"rollForward\": \"disable\"\n  }\n}\n";

    // latestMajor rolls forward to whatever is installed, so this pin always resolves.
    private const string ResolvablePin =
        "{\n  \"sdk\": {\n    \"version\": \"6.0.100\",\n    \"rollForward\": \"latestMajor\"\n  }\n}\n";

    private const string RemoteUrl = "https://example.invalid/elevenworks/sdkpin.git";

    private string _root = null!;
    private string _checkout = null!;
    private string _journalRoot = null!;
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private readonly List<string> _log = [];

    [TestInitialize]
    public void Init()
    {
        _ = IntegrationFixture.Instance;
        _root = Path.Combine(Path.GetTempPath(), $"sextant_sdkpin_it_{Guid.NewGuid():N}");
        _checkout = Path.Combine(_root, "checkouts", "sdkpin");
        _journalRoot = Path.Combine(_root, "checkouts", SdkPinOptions.JournalDirectoryName);
        Directory.CreateDirectory(_checkout);
        _dbPath = Path.Combine(_root, "catalog.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteTestDatabase.Delete(_dbPath, _db);
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

    [TestMethod]
    [DataRow(false, DisplayName = "direct")]
    [DataRow(true, DisplayName = "under the enforced evaluation sandbox")]
    public async Task UnsatisfiablePin_OverrideOn_PublishesWithTheInstalledSdk_AndLeavesTheCheckoutPristine(bool sandboxed)
    {
        var commit = CreateRepo(("global.json", UnsatisfiablePin));
        var globalJson = Path.Combine(_checkout, "global.json");
        var appProject = Path.Combine(_checkout, "src", "App", "App.csproj");
        var bytesBefore = File.ReadAllBytes(globalJson);
        var mtimeBefore = File.GetLastWriteTimeUtc(globalJson);
        var fingerprintBefore = EvaluationFingerprint.Compute(appProject, _checkout);

        var (result, _) = await ProduceAsync(
            commit, [Path.Combine(_checkout, "App.slnx")], overrideEnabled: true, sandboxed: sandboxed);

        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status, $"{result.Error}\n{string.Join('\n', _log)}");
        var snapshotId = result.SnapshotId!.Value;
        var diagnostic = result.Projects.Single(p => p.Code == LocalIndexerSnapshotWorker.SdkPinOverriddenCode);
        Assert.AreEqual("global.json", diagnostic.ProjectPath);
        StringAssert.Contains(diagnostic.Message, "pins .NET SDK 10.0.999 (rollForward: disable)");
        Assert.IsFalse(result.Projects.Any(p => p.Code == LocalIndexerSnapshotWorker.SdkResolutionFailedCode));

        // The override is durable snapshot provenance, and the snapshot is complete (the override restored coverage).
        var coverage = new SnapshotCoverageStore(_db.GetConnection()).Get(snapshotId)!;
        Assert.AreEqual(SnapshotCoverageVerdict.Complete, coverage.Verdict);
        var recorded = coverage.SdkPinOverrides!.Single();
        Assert.AreEqual("global.json", recorded.GlobalJsonPath);
        Assert.AreEqual("10.0.999", recorded.RequestedVersion);
        Assert.AreEqual("disable", recorded.RollForward);
        Assert.IsFalse(string.IsNullOrEmpty(recorded.ResolvedSdkVersion), "the substituted SDK is recorded");
        Assert.IsTrue(Scalar("SELECT COUNT(*) FROM symbols WHERE fully_qualified_name LIKE '%Greeter%';") > 0,
            "the checkout was actually indexed");

        // The persistent checkout is byte-identical to its commit afterwards.
        CollectionAssert.AreEqual(bytesBefore, File.ReadAllBytes(globalJson));
        Assert.AreEqual(mtimeBefore, File.GetLastWriteTimeUtc(globalJson));
        Assert.AreEqual(string.Empty, Git(_checkout, "status", "--porcelain").Trim(), "the working tree matches the commit");
        Assert.AreEqual(0, Directory.Exists(_journalRoot) ? Directory.GetFiles(_journalRoot).Length : 0, "no restore journal is left");

        // EvaluationFingerprint hashes global.json at index time — after the restore — so it equals the pristine value.
        Assert.IsNotNull(fingerprintBefore);
        Assert.AreEqual(fingerprintBefore, EvaluationFingerprint.Compute(appProject, _checkout));
        Assert.AreEqual(fingerprintBefore, ScalarString(
            "SELECT evaluation_fingerprint FROM projects WHERE repo_relative_path LIKE '%App.csproj' LIMIT 1;"),
            "the stored evaluation fingerprint is the committed checkout's, never the neutralized one's");
    }

    [TestMethod]
    public async Task UnsatisfiablePin_OverrideOff_FailsWithATypedDiagnostic()
    {
        var commit = CreateRepo(("global.json", UnsatisfiablePin));
        var globalJson = Path.Combine(_checkout, "global.json");
        var bytesBefore = File.ReadAllBytes(globalJson);

        var (result, _) = await ProduceAsync(commit, [Path.Combine(_checkout, "App.slnx")], overrideEnabled: false);

        Assert.AreEqual(SnapshotJobStatus.Failed, result.Status);
        StringAssert.Contains(result.Error, "'global.json' requests SDK 10.0.999");
        StringAssert.Contains(result.Error, "(rollForward: disable)");
        StringAssert.Contains(result.Error, "installed SDK(s):");
        StringAssert.Contains(result.Error, "The pin was not overridden");
        Assert.IsFalse(result.Error!.Contains(_checkout, StringComparison.OrdinalIgnoreCase), "no worker volume paths");
        var diagnostic = result.Projects.Single(p => p.Code == LocalIndexerSnapshotWorker.SdkResolutionFailedCode);
        Assert.AreEqual(JobDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.AreEqual("global.json", diagnostic.ProjectPath);
        Assert.AreEqual(0, Scalar("SELECT COUNT(*) FROM snapshots WHERE status = 'complete';"), "nothing was published");
        CollectionAssert.AreEqual(bytesBefore, File.ReadAllBytes(globalJson));
    }

    [TestMethod]
    public async Task ResolvablePin_IsNeverOverridden()
    {
        var commit = CreateRepo(("global.json", ResolvablePin));
        var globalJson = Path.Combine(_checkout, "global.json");
        var bytesBefore = File.ReadAllBytes(globalJson);
        var mtimeBefore = File.GetLastWriteTimeUtc(globalJson);

        var (result, _) = await ProduceAsync(commit, [Path.Combine(_checkout, "App.slnx")], overrideEnabled: true);

        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status, $"{result.Error}\n{string.Join('\n', _log)}");
        Assert.IsFalse(result.Projects.Any(p => p.Code is LocalIndexerSnapshotWorker.SdkPinOverriddenCode
            or LocalIndexerSnapshotWorker.SdkResolutionFailedCode), "a resolvable pin produces no SDK-pin diagnostic");
        Assert.IsNull(new SnapshotCoverageStore(_db.GetConnection()).Get(result.SnapshotId!.Value)!.SdkPinOverrides);
        CollectionAssert.AreEqual(bytesBefore, File.ReadAllBytes(globalJson));
        Assert.AreEqual(mtimeBefore, File.GetLastWriteTimeUtc(globalJson));
        Assert.IsFalse(Directory.Exists(_journalRoot), "no journal is ever written for a resolvable pin");
    }

    [TestMethod]
    public async Task MalformedSdkVersionPin_IsNeverOverridden_AndFailsTyped()
    {
        // Real hostfxr words a malformed version ("1.2.0" has no feature band) exactly like an absent band, so
        // only the guard's version validation keeps it from "fixing" a pin that does not name a real SDK band.
        const string malformed = "{\n  \"sdk\": {\n    \"version\": \"1.2.0\",\n    \"rollForward\": \"disable\"\n  }\n}\n";
        var commit = CreateRepo(("global.json", malformed));
        var globalJson = Path.Combine(_checkout, "global.json");
        var bytesBefore = File.ReadAllBytes(globalJson);

        var (result, _) = await ProduceAsync(commit, [Path.Combine(_checkout, "App.slnx")], overrideEnabled: true);

        Assert.AreEqual(SnapshotJobStatus.Failed, result.Status, string.Join('\n', _log));
        StringAssert.Contains(result.Error, "not a well-formed .NET SDK version");
        Assert.AreEqual(1, result.Projects.Count(p => p.Code == LocalIndexerSnapshotWorker.SdkResolutionFailedCode));
        Assert.IsFalse(result.Projects.Any(p => p.Code == LocalIndexerSnapshotWorker.SdkPinOverriddenCode));
        CollectionAssert.AreEqual(bytesBefore, File.ReadAllBytes(globalJson));
        Assert.IsFalse(Directory.Exists(_journalRoot), "a refused pin never writes a journal");
    }

    [TestMethod]
    public async Task AnUncommittedPin_IsNeverOverridden_AndFailsTyped()
    {
        // A locate-mode checkout is indexed as found. A local edit that ADDS an unsatisfiable pin is not the
        // commit's content, so the guard refuses it: the restore journal must only ever hold committed bytes.
        var commit = CreateRepo(("global.json", ResolvablePin));
        var globalJson = Path.Combine(_checkout, "global.json");
        File.WriteAllText(globalJson, UnsatisfiablePin);
        var dirty = File.ReadAllBytes(globalJson);

        var (result, _) = await ProduceAsync(commit, [Path.Combine(_checkout, "App.slnx")], overrideEnabled: true);

        Assert.AreEqual(SnapshotJobStatus.Failed, result.Status, string.Join('\n', _log));
        StringAssert.Contains(result.Error, "not verifiably the checkout's committed content");
        StringAssert.Contains(result.Error, "'global.json' differs from its committed content");
        Assert.AreEqual(1, result.Projects.Count(p => p.Code == LocalIndexerSnapshotWorker.SdkResolutionFailedCode));
        Assert.IsFalse(result.Projects.Any(p => p.Code == LocalIndexerSnapshotWorker.SdkPinOverriddenCode));
        CollectionAssert.AreEqual(dirty, File.ReadAllBytes(globalJson), "the local edit is left exactly as found");
        Assert.IsFalse(Directory.Exists(_journalRoot), "a refused pin never writes a journal");
    }

    [TestMethod]
    public async Task LeftoverNeutralizedPin_FromAnInterruptedJob_IsRepairedBeforeTheCheckoutIsReused()
    {
        var commit = CreateRepo(("global.json", UnsatisfiablePin));
        var globalJson = Path.Combine(_checkout, "global.json");
        var bytesBefore = File.ReadAllBytes(globalJson);

        // Simulate a worker that crashed mid-load: the pin is neutralized and its journal is left behind.
        var crashed = new SdkPinGuard(new SdkPinOptions { JournalRoot = _journalRoot })
            .Apply(_checkout, [Path.Combine(_checkout, "App.slnx")]);
        Assert.IsTrue(crashed.Overridden.Any());
        Assert.AreNotEqual(Convert.ToBase64String(bytesBefore), Convert.ToBase64String(File.ReadAllBytes(globalJson)));

        var (result, _) = await ProduceAsync(commit, [Path.Combine(_checkout, "App.slnx")], overrideEnabled: true);

        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status, $"{result.Error}\n{string.Join('\n', _log)}");
        CollectionAssert.AreEqual(bytesBefore, File.ReadAllBytes(globalJson));
        Assert.AreEqual(0, Directory.GetFiles(_journalRoot).Length);
        Assert.AreEqual(string.Empty, Git(_checkout, "status", "--porcelain").Trim());
    }

    [TestMethod]
    public async Task RestoreFailureAfterASuccessfulLoad_IsTypedAndRequeued_WithoutIndexingTheDivergedCheckout()
    {
        const string foreign = "{\n  \"comment\": \"rewritten by something else\"\n}\n";
        var commit = CreateRepo(("global.json", UnsatisfiablePin));
        var globalJson = Path.Combine(_checkout, "global.json");

        var ex = await Assert.ThrowsExactlyAsync<TransientProvisioningException>(() => ProduceAsync(
            commit, [Path.Combine(_checkout, "App.slnx")], overrideEnabled: true, probe: new RewritingProbe(foreign)));

        // The load succeeded (the rewritten file has no pin), but the committed bytes could not be put back:
        // the job must not index/publish a checkout that diverges from its commit. It is a typed, requeued
        // checkout-state failure (never a terminal result that would poison the commit's identity).
        Assert.AreEqual(LocalIndexerSnapshotWorker.SdkPinRestoreFailedCode, ex.DiagnosticCode, string.Join('\n', _log));
        StringAssert.Contains(ex.Message, "could not restore the committed file");
        StringAssert.Contains(ex.Message, "./global.json");
        Assert.IsFalse(ex.Message.Contains(_checkout, StringComparison.OrdinalIgnoreCase), "no worker volume paths");
        Assert.AreEqual(0, Scalar("SELECT COUNT(*) FROM snapshots WHERE status = 'complete';"), "nothing was published");
        Assert.AreEqual(0, Scalar("SELECT COUNT(*) FROM symbols;"), "nothing was indexed");
        Assert.AreEqual(foreign, File.ReadAllText(globalJson), "content something else wrote is never clobbered");
        Assert.AreEqual(1, Directory.GetFiles(_journalRoot).Length, "the journal is kept for the next job");
    }

    [TestMethod]
    public async Task RestoreFailure_OutranksALoadFailure()
    {
        // The rewrite re-pins an unsatisfiable band, so the load ALSO dies in hostfxr — but the checkout no
        // longer matches its commit, and that is what the job must report (not a generic sdk_resolution_failed).
        const string foreign = "{\n  \"sdk\": { \"version\": \"10.0.998\", \"rollForward\": \"disable\" }\n}\n";
        var commit = CreateRepo(("global.json", UnsatisfiablePin));

        var ex = await Assert.ThrowsExactlyAsync<TransientProvisioningException>(() => ProduceAsync(
            commit, [Path.Combine(_checkout, "App.slnx")], overrideEnabled: true, probe: new RewritingProbe(foreign)));

        Assert.AreEqual(LocalIndexerSnapshotWorker.SdkPinRestoreFailedCode, ex.DiagnosticCode);
        StringAssert.Contains(ex.Message, "could not restore the committed file");
        Assert.IsFalse(ex.Message.Contains("could not be resolved on this worker", StringComparison.Ordinal),
            "the restore failure is reported, not the SDK-resolution failure it caused");
        Assert.AreEqual(0, Scalar("SELECT COUNT(*) FROM snapshots WHERE status = 'complete';"));
    }

    [TestMethod]
    public async Task RestoreFailure_OutranksCancellation()
    {
        // The job is cancelled while the pin is neutralized AND the restore then finds foreign content: the job
        // must report the diverged checkout (typed sdk_pin_restore_failed), not a plain cancellation.
        const string foreign = "{\n  \"comment\": \"rewritten by something else\"\n}\n";
        var commit = CreateRepo(("global.json", UnsatisfiablePin));
        using var cts = new CancellationTokenSource();

        var ex = await Assert.ThrowsExactlyAsync<TransientProvisioningException>(() => ProduceAsync(
            commit, [Path.Combine(_checkout, "App.slnx")], overrideEnabled: true,
            probe: new RewritingProbe(foreign, cts), cancellationToken: cts.Token));

        Assert.AreEqual(LocalIndexerSnapshotWorker.SdkPinRestoreFailedCode, ex.DiagnosticCode, string.Join('\n', _log));
        Assert.AreEqual(1, Directory.GetFiles(_journalRoot).Length, "the journal is kept for the next job");
    }

    [TestMethod]
    public async Task UnclassifiedLoadFailure_WithAnUnresolvedPin_IsStillTyped()
    {
        // The loader's own "every declared project failed" error names no hostfxr function. When the guard knows
        // a pin the load depends on does NOT resolve (here: hostfxr failed for a non-missing-SDK reason, so the
        // pin was refused), the job must still fail TYPED — naming the pin and the load error — not bare.
        var commit = CreateRepo(("global.json", ResolvablePin), ("src/App/App.csproj", "<Project"));

        var (result, _) = await ProduceAsync(commit, [Path.Combine(_checkout, "App.slnx")], overrideEnabled: true,
            probe: new NonMissingSdkFailureProbe(_checkout));

        Assert.AreEqual(SnapshotJobStatus.Failed, result.Status, string.Join('\n', _log));
        StringAssert.Contains(result.Error, "the checkout could not be loaded (");
        StringAssert.Contains(result.Error, "'global.json' requests SDK");
        StringAssert.Contains(result.Error, "The pin was not overridden");
        Assert.IsFalse(result.Error!.Contains(_checkout, StringComparison.OrdinalIgnoreCase), "no worker volume paths");
        Assert.IsTrue(result.Projects.Any(p => p.Code == LocalIndexerSnapshotWorker.SdkResolutionFailedCode));
        Assert.IsFalse(Directory.Exists(_journalRoot), "a refused pin is never touched");
    }

    [TestMethod]
    public async Task MultiSolution_OnlyOneSolutionPinsAMissingBand_IsPartialOff_AndCompleteOn()
    {
        var commit = CreateRepo(
            ("tools/Tools.slnx", "<Solution>\n  <Project Path=\"Tool/Tool.csproj\" />\n</Solution>\n"),
            ("tools/Tool/Tool.csproj", ProjectXml),
            ("tools/Tool/Hammer.cs", "namespace Fixture.Tools;\n\npublic sealed class Hammer\n{\n}\n"),
            ("tools/global.json", UnsatisfiablePin));
        string[] solutions = [Path.Combine(_checkout, "App.slnx"), Path.Combine(_checkout, "tools", "Tools.slnx")];

        var (off, _) = await ProduceAsync(commit, solutions, overrideEnabled: false);

        // #90-style isolation: the root solution still indexes; the pinned one is skipped WITH the SDK reason.
        Assert.AreEqual(SnapshotJobStatus.Partial, off.Status, $"{off.Error}\n{string.Join('\n', _log)}");
        StringAssert.Contains(off.Error, "because the .NET SDK their global.json pins is not installed on this worker");
        StringAssert.Contains(off.Error, "'tools/global.json' requests SDK 10.0.999");
        Assert.IsTrue(off.Projects.Any(p => p.Code == LocalIndexerSnapshotWorker.SdkResolutionFailedCode
                                            && p.Severity == JobDiagnosticSeverity.Warning));

        // Same checkout, fresh catalog, override on: the whole checkout indexes with the installed SDK.
        ResetCatalog();
        var (on, _) = await ProduceAsync(commit, solutions, overrideEnabled: true);

        Assert.AreEqual(SnapshotJobStatus.Complete, on.Status, $"{on.Error}\n{string.Join('\n', _log)}");
        Assert.AreEqual("tools/global.json",
            on.Projects.Single(p => p.Code == LocalIndexerSnapshotWorker.SdkPinOverriddenCode).ProjectPath);
        Assert.IsTrue(Scalar("SELECT COUNT(*) FROM symbols WHERE fully_qualified_name LIKE '%Hammer%';") > 0);
        Assert.AreEqual(string.Empty, Git(_checkout, "status", "--porcelain").Trim());
    }

    [TestMethod]
    public async Task DefaultUnion_APinInANestedProjectDirectory_IsIsolatedOff_AndOverriddenOn()
    {
        // #124: with no sextant.json the service selects the UNION of every discovered solution and loads it
        // through MultiSolutionLoader's per-project path. The pin here sits in a declared PROJECT's directory,
        // below its solution's directory, so it governs only that project's evaluation — the guard must find it.
        var commit = CreateRepo(
            ("tools/Tools.slnx", "<Solution>\n  <Project Path=\"Legacy/Legacy.csproj\" />\n</Solution>\n"),
            ("tools/Legacy/Legacy.csproj", ProjectXml),
            ("tools/Legacy/Anvil.cs", "namespace Fixture.Legacy;\n\npublic sealed class Anvil\n{\n}\n"),
            ("tools/Legacy/global.json", UnsatisfiablePin));
        var selection = SolutionSelector.Select(_checkout, configuredSolutions: null);
        Assert.AreEqual(SolutionSelectionSource.DefaultUnion, selection.Source);
        Assert.AreEqual(2, selection.SolutionPaths.Count, string.Join(", ", selection.SolutionPaths));

        var (off, _) = await ProduceAsync(
            commit, selection.SolutionPaths, overrideEnabled: false, source: selection.Source);

        Assert.AreEqual(SnapshotJobStatus.Partial, off.Status, $"{off.Error}\n{string.Join('\n', _log)}");
        StringAssert.Contains(off.Error, "'tools/Legacy/global.json' requests SDK 10.0.999");
        Assert.IsTrue(off.Projects.Any(p => p.Code == LocalIndexerSnapshotWorker.SdkResolutionFailedCode
                                            && p.Severity == JobDiagnosticSeverity.Warning));
        Assert.IsTrue(Scalar("SELECT COUNT(*) FROM symbols WHERE fully_qualified_name LIKE '%Greeter%';") > 0,
            "the unpinned solution still indexes");
        Assert.AreEqual(0, Scalar("SELECT COUNT(*) FROM symbols WHERE fully_qualified_name LIKE '%Anvil%';"));

        ResetCatalog();
        var (on, _) = await ProduceAsync(
            commit, selection.SolutionPaths, overrideEnabled: true, source: selection.Source);

        Assert.AreEqual(SnapshotJobStatus.Complete, on.Status, $"{on.Error}\n{string.Join('\n', _log)}");
        Assert.AreEqual("tools/Legacy/global.json",
            on.Projects.Single(p => p.Code == LocalIndexerSnapshotWorker.SdkPinOverriddenCode).ProjectPath);
        Assert.IsTrue(Scalar("SELECT COUNT(*) FROM symbols WHERE fully_qualified_name LIKE '%Anvil%';") > 0);
        Assert.AreEqual(string.Empty, Git(_checkout, "status", "--porcelain").Trim());
    }

    [TestMethod]
    [DataRow(false, DisplayName = "the root pin fails the whole load (failed job)")]
    [DataRow(true, DisplayName = "one solution's pin is isolated (partial snapshot)")]
    public async Task FlippingTheOverride_OffThenOn_RebuildsTheCommitUnderANewIdentity_InsteadOfReusingIt(bool multiSolution)
    {
        // An already-published snapshot is never rebuilt and a failed job is reused as recorded. So the policy is
        // part of the identity: after the operator turns the override on, the SAME commit (in the SAME catalog)
        // is re-indexed with complete coverage instead of reusing what the override-off node produced.
        string commit;
        string[] solutions;
        if (multiSolution)
        {
            commit = CreateRepo(
                ("tools/Tools.slnx", "<Solution>\n  <Project Path=\"Tool/Tool.csproj\" />\n</Solution>\n"),
                ("tools/Tool/Tool.csproj", ProjectXml),
                ("tools/Tool/Hammer.cs", "namespace Fixture.Tools;\n\npublic sealed class Hammer\n{\n}\n"),
                ("tools/global.json", UnsatisfiablePin));
            solutions = [Path.Combine(_checkout, "App.slnx"), Path.Combine(_checkout, "tools", "Tools.slnx")];
        }
        else
        {
            commit = CreateRepo(("global.json", UnsatisfiablePin));
            solutions = [Path.Combine(_checkout, "App.slnx")];
        }
        var coverageStore = new SnapshotCoverageStore(_db.GetConnection());

        var off = await EnsureThroughServiceAsync(commit, solutions, overrideEnabled: false);

        if (multiSolution)
        {
            Assert.AreEqual(SnapshotJobStatus.Partial, off.Status, $"{off.Reason}\n{string.Join('\n', _log)}");
            Assert.AreEqual(SnapshotCoverageVerdict.Partial, coverageStore.Get(off.SnapshotId!.Value)!.Verdict);
        }
        else
        {
            Assert.AreEqual(SnapshotJobStatus.Failed, off.Status, $"{off.Reason}\n{string.Join('\n', _log)}");
            Assert.IsNull(off.SnapshotId);
        }

        var on = await EnsureThroughServiceAsync(commit, solutions, overrideEnabled: true);

        Assert.AreNotEqual(off.IdentityHash, on.IdentityHash, "the override policy is part of the snapshot identity");
        Assert.IsFalse(on.Attached, "the override-off result is not reused");
        Assert.AreEqual(SnapshotJobStatus.Complete, on.Status, $"{on.Reason}\n{string.Join('\n', _log)}");
        Assert.AreNotEqual(off.SnapshotId, on.SnapshotId, "a fresh snapshot was built");
        var coverage = coverageStore.Get(on.SnapshotId!.Value)!;
        Assert.AreEqual(SnapshotCoverageVerdict.Complete, coverage.Verdict);
        Assert.AreEqual(multiSolution ? "tools/global.json" : "global.json", coverage.SdkPinOverrides!.Single().GlobalJsonPath);
        Assert.IsTrue(Scalar(
            $"SELECT COUNT(*) FROM symbols WHERE fully_qualified_name LIKE '%{(multiSolution ? "Hammer" : "Greeter")}%';") > 0);
        if (multiSolution)
            Assert.AreEqual(SnapshotCoverageVerdict.Partial, coverageStore.Get(off.SnapshotId!.Value)!.Verdict,
                "the override-off snapshot is immutable and keeps its own partial verdict");

        // Under an unchanged policy the published snapshot is reused as usual, with no rebuild.
        var again = await EnsureThroughServiceAsync(commit, solutions, overrideEnabled: true);
        Assert.IsTrue(again.Attached);
        Assert.AreEqual(on.SnapshotId, again.SnapshotId);
        Assert.AreEqual(string.Empty, Git(_checkout, "status", "--porcelain").Trim());
    }

    [TestMethod]
    public async Task DefaultUnion_ARootPinAndAPinInsideAPopulatedSubmodule_AreBothOverridden()
    {
        // Issue #171: the submodule's global.json belongs to the submodule's repository (from the superproject it
        // is untracked). It is verified there, at the gitlink, and overridden like the root pin — before #171 its
        // refusal also refused the root pin, and the whole default-union load failed.
        var commit = CreateRepoWithSubmodule();
        var sub = Path.Combine(_checkout, "libs", "sub");
        var rootPin = Path.Combine(_checkout, "global.json");
        var subPin = Path.Combine(sub, "global.json");
        var rootBytes = File.ReadAllBytes(rootPin);
        var subBytes = File.ReadAllBytes(subPin);
        var subMtime = File.GetLastWriteTimeUtc(subPin);
        var selection = SolutionSelector.Select(_checkout, configuredSolutions: null);
        Assert.AreEqual(2, selection.SolutionPaths.Count, string.Join(", ", selection.SolutionPaths));

        var (result, _) = await ProduceAsync(
            commit, selection.SolutionPaths, overrideEnabled: true, source: selection.Source);

        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status, $"{result.Error}\n{string.Join('\n', _log)}");
        CollectionAssert.AreEquivalent(
            new[] { "global.json", "libs/sub/global.json" },
            result.Projects.Where(p => p.Code == LocalIndexerSnapshotWorker.SdkPinOverriddenCode).Select(p => p.ProjectPath).ToArray());
        Assert.IsFalse(result.Projects.Any(p => p.Code == LocalIndexerSnapshotWorker.SdkResolutionFailedCode));
        var coverage = new SnapshotCoverageStore(_db.GetConnection()).Get(result.SnapshotId!.Value)!;
        Assert.AreEqual(SnapshotCoverageVerdict.Complete, coverage.Verdict, string.Join("; ", coverage.Reasons));
        CollectionAssert.AreEquivalent(
            new[] { "global.json", "libs/sub/global.json" }, coverage.SdkPinOverrides!.Select(p => p.GlobalJsonPath).ToArray());
        Assert.IsTrue(Scalar("SELECT COUNT(*) FROM symbols WHERE fully_qualified_name LIKE '%Greeter%';") > 0);
        Assert.IsTrue(Scalar("SELECT COUNT(*) FROM symbols WHERE fully_qualified_name LIKE '%Anvil%';") > 0,
            "the submodule's projects were indexed with the installed SDK");

        CollectionAssert.AreEqual(rootBytes, File.ReadAllBytes(rootPin));
        CollectionAssert.AreEqual(subBytes, File.ReadAllBytes(subPin));
        Assert.AreEqual(subMtime, File.GetLastWriteTimeUtc(subPin));
        Assert.AreEqual(string.Empty, Git(_checkout, "status", "--porcelain", "--ignore-submodules=none").Trim(),
            "the superproject matches its commit, and the submodule its gitlink");
        Assert.AreEqual(string.Empty, Git(sub, "status", "--porcelain").Trim());
        Assert.AreEqual(0, Directory.Exists(_journalRoot) ? Directory.GetFiles(_journalRoot).Length : 0, "no restore journal is left");
    }

    [TestMethod]
    public async Task DefaultUnion_ASubmoduleAwayFromItsGitlink_IsIsolatedAsPartial_WhileTheRootPinIsStillOverridden()
    {
        // A submodule not at the commit the superproject pins holds content the snapshot's commit never vouched
        // for, so its pin is refused — with its OWN reason — and only its projects are lost (#90 isolation).
        var commit = CreateRepoWithSubmodule();
        var sub = Path.Combine(_checkout, "libs", "sub");
        Git(sub, "commit", "--quiet", "--allow-empty", "-m", "drift");
        var subPin = Path.Combine(sub, "global.json");
        var subBytes = File.ReadAllBytes(subPin);
        var subMtime = File.GetLastWriteTimeUtc(subPin);
        var selection = SolutionSelector.Select(_checkout, configuredSolutions: null);

        var (result, _) = await ProduceAsync(
            commit, selection.SolutionPaths, overrideEnabled: true, source: selection.Source);

        Assert.AreEqual(SnapshotJobStatus.Partial, result.Status, $"{result.Error}\n{string.Join('\n', _log)}");
        StringAssert.Contains(result.Error, "'libs/sub/global.json' requests SDK 9.0.999");
        Assert.AreEqual("global.json",
            result.Projects.Single(p => p.Code == LocalIndexerSnapshotWorker.SdkPinOverriddenCode).ProjectPath,
            "the root pin is overridden although the submodule's is refused");
        var refused = result.Projects.Single(p => p.Code == LocalIndexerSnapshotWorker.SdkResolutionFailedCode
                                                  && p.ProjectPath == "libs/sub/global.json");
        Assert.AreEqual(JobDiagnosticSeverity.Warning, refused.Severity);
        StringAssert.Contains(refused.Message, "the submodule 'libs/sub' is checked out at");
        Assert.IsFalse(result.Projects.Any(p => p.Code == LocalIndexerSnapshotWorker.SdkResolutionFailedCode
                                                && p.ProjectPath == "global.json"));
        Assert.IsTrue(Scalar("SELECT COUNT(*) FROM symbols WHERE fully_qualified_name LIKE '%Greeter%';") > 0,
            "the root solution indexes with the installed SDK");
        Assert.AreEqual(0, Scalar("SELECT COUNT(*) FROM symbols WHERE fully_qualified_name LIKE '%Anvil%';"));

        CollectionAssert.AreEqual(subBytes, File.ReadAllBytes(subPin), "the refused pin is never touched");
        Assert.AreEqual(subMtime, File.GetLastWriteTimeUtc(subPin));
        Assert.AreEqual(string.Empty, Git(sub, "status", "--porcelain").Trim());
        Assert.AreEqual(string.Empty, Git(_checkout, "status", "--porcelain", "--ignore-submodules=all").Trim());
        Assert.AreEqual(0, Directory.Exists(_journalRoot) ? Directory.GetFiles(_journalRoot).Length : 0, "no restore journal is left");
    }

    /// <summary>
    /// One ensure through a real <see cref="SnapshotService"/> over the shared catalog, as a node configured with
    /// <c>SEXTANT_SERVICE_SDK_PIN_OVERRIDE=<paramref name="overrideEnabled"/></c> would run it. The host wires the
    /// guard and the service options from that one toggle; each call is a fresh service start, as after an
    /// operator changes the setting and restarts.
    /// </summary>
    private async Task<EnsureSnapshotResult> EnsureThroughServiceAsync(
        string commit, IReadOnlyList<string> solutions, bool overrideEnabled)
    {
        var config = new SextantConfiguration();
        var resolution = new CheckoutResolution
        {
            CheckoutDir = _checkout,
            SelectedSolutions = solutions,
            Source = solutions.Count > 1 ? SolutionSelectionSource.Configured : SolutionSelectionSource.DefaultUnion
        };
        var guard = new SdkPinGuard(
            new SdkPinOptions { OverrideEnabled = overrideEnabled, JournalRoot = _journalRoot }, log: _log.Add);
        var worker = new LocalIndexerSnapshotWorker(
            _db, config, new FixedCheckoutProvider(resolution), _log.Add, sdkPinGuard: guard);
        var options = new ServiceOptions
        {
            CatalogDbPath = _dbPath,
            Volumes = ServiceVolumes.Rooted(_root),
            DefaultConfigHash = IndexProfileDescriptor.FromConfiguration(config).ConfigurationHash,
            SdkPinOverride = overrideEnabled
        };
        using var service = SnapshotService.Start(options, worker, _db);
        return await service.EnsureSnapshotAsync(
            new EnsureSnapshotRequest { RepositoryRemoteUrl = RemoteUrl, CommitSha = commit, BranchName = "main" });
    }

    // ---- fixture ------------------------------------------------------------------------------------

    /// <summary>
    /// The real hostfxr probe, except that the FIRST time it is asked to re-probe a directory whose
    /// <c>global.json</c> is currently neutralized (no <c>"sdk"</c> section) it simulates something else rewriting
    /// that file mid-job — writing <paramref name="foreign"/> — and reports success, so the guard proceeds with
    /// the override and the post-load restore then finds content it must not clobber.
    /// </summary>
    private sealed class RewritingProbe(string foreign, CancellationTokenSource? cancelOnRewrite = null) : ISdkResolutionProbe
    {
        private bool _rewritten;

        public SdkResolutionProbeResult Probe(string workingDirectory)
        {
            var globalJson = Path.Combine(workingDirectory, "global.json");
            if (!_rewritten && File.Exists(globalJson)
                && !File.ReadAllText(globalJson).Contains("\"sdk\"", StringComparison.Ordinal))
            {
                _rewritten = true;
                File.WriteAllText(globalJson, foreign);
                cancelOnRewrite?.Cancel();
                return new SdkResolutionProbeResult { ResolvedSdkVersion = "10.0.0-rewritten" };
            }
            return HostFxrSdkResolutionProbe.Instance.Probe(workingDirectory);
        }

        public IReadOnlyList<string> ListInstalledSdks() => HostFxrSdkResolutionProbe.Instance.ListInstalledSdks();
    }

    /// <summary>
    /// Reports a hostfxr SDK-resolution failure that is NOT the missing-SDK outcome for <paramref name="checkout"/>
    /// (so the guard refuses to override it); every other directory resolves normally.
    /// </summary>
    private sealed class NonMissingSdkFailureProbe(string checkout) : ISdkResolutionProbe
    {
        public SdkResolutionProbeResult Probe(string workingDirectory) =>
            string.Equals(Path.TrimEndingDirectorySeparator(workingDirectory), checkout, StringComparison.OrdinalIgnoreCase)
                ? new SdkResolutionProbeResult
                {
                    Error = new HostFxrSdkResolutionError
                    {
                        RequestedVersion = "10.0.100", GlobalJsonPath = Path.Combine(checkout, "global.json"), IsMissingSdk = false
                    }
                }
                : HostFxrSdkResolutionProbe.Instance.Probe(workingDirectory);

        public IReadOnlyList<string> ListInstalledSdks() => HostFxrSdkResolutionProbe.Instance.ListInstalledSdks();
    }

    private const string ProjectXml =
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n" +
        "  </PropertyGroup>\n</Project>\n";

    private async Task<(SnapshotWorkResult Result, string IdentityHash)> ProduceAsync(
        string commit, IReadOnlyList<string> solutions, bool overrideEnabled, bool sandboxed = false,
        ISdkResolutionProbe? probe = null, SolutionSelectionSource? source = null,
        CancellationToken cancellationToken = default)
    {
        var config = new SextantConfiguration();
        var resolution = new CheckoutResolution
        {
            CheckoutDir = _checkout,
            SelectedSolutions = solutions,
            Source = source
                     ?? (solutions.Count > 1 ? SolutionSelectionSource.Configured : SolutionSelectionSource.DefaultUnion)
        };
        var guard = new SdkPinGuard(
            new SdkPinOptions { OverrideEnabled = overrideEnabled, JournalRoot = _journalRoot }, probe, _log.Add);

        // The production host enforces the evaluation sandbox by default: the guard's probe + rewrite then run
        // inside its scrubbed environment scope, with per-job scratch under the service scratch root.
        var paths = new ServicePaths(ServiceVolumes.Rooted(_root));
        var sandbox = sandboxed
            ? new EvaluationSandbox(
                SandboxPolicy.Enforced with { TimeBudget = TimeSpan.FromMinutes(10), MemoryBudgetBytes = 0 }, paths, _log.Add)
            : null;
        var scratch = sandboxed ? paths.AllocateScratch("sdkpin") : Path.Combine(_root, "scratch-direct");
        Directory.CreateDirectory(scratch);

        var worker = new LocalIndexerSnapshotWorker(
            _db, config, new FixedCheckoutProvider(resolution), _log.Add, sandbox: sandbox, sdkPinGuard: guard);
        var request = new EnsureSnapshotRequest { RepositoryRemoteUrl = RemoteUrl, CommitSha = commit, BranchName = "main" };
        // As the service does: the requested identity carries the node's SDK-pin policy (issue #113).
        var identity = request.ToIdentity(
            IndexProfileDescriptor.FromConfiguration(config).ConfigurationHash, sdkPinPolicy: guard.IdentityComponent).Hash;
        var result = await worker.ProduceAsync(request, identity, scratch, cancellationToken);
        return (result, identity);
    }

    private void ResetCatalog()
    {
        SqliteTestDatabase.Delete(_dbPath, _db);
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
    }

    // App.slnx → src/App/App.csproj (+ a source file), a .gitignore for build output, plus the given files.
    private string CreateRepo(params (string Path, string Content)[] extra)
    {
        Write("App.slnx", "<Solution>\n  <Project Path=\"src/App/App.csproj\" />\n</Solution>\n");
        Write("src/App/App.csproj", ProjectXml);
        Write("src/App/Greeter.cs", "namespace Fixture;\n\npublic sealed class Greeter\n{\n    public string Hello() => \"hi\";\n}\n");
        Write(".gitignore", "bin/\nobj/\n");
        foreach (var (path, content) in extra)
            Write(path, content);

        try
        {
            Git(_checkout, "init", "--quiet", "--initial-branch", "main");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Assert.Inconclusive($"git is not available: {ex.Message}");
        }
        Git(_checkout, "config", "user.email", "test@example.com");
        Git(_checkout, "config", "user.name", "Sextant Test");
        Git(_checkout, "config", "commit.gpgsign", "false");
        Git(_checkout, "config", "core.autocrlf", "false");
        Git(_checkout, "add", "-A");
        Git(_checkout, "commit", "--quiet", "-m", "fixture");
        return Git(_checkout, "rev-parse", "HEAD").Trim();
    }

    private void Write(string relative, string content)
    {
        var full = Path.Combine(_checkout, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    // The CreateRepo superproject with a root pin, plus an absorbed submodule at libs/sub (its git dir under
    // .git/modules, as #125's provisioning leaves it) whose own commit holds Sub.slnx → Anvil and its own
    // unsatisfiable pin. Returns the superproject's commit, which records the submodule's gitlink.
    private string CreateRepoWithSubmodule()
    {
        var source = Path.Combine(_root, "sub-source");
        void WriteSource(string relative, string content)
        {
            var full = Path.Combine(source, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        WriteSource("Sub.slnx", "<Solution>\n  <Project Path=\"Anvil/Anvil.csproj\" />\n</Solution>\n");
        WriteSource("Anvil/Anvil.csproj", ProjectXml);
        WriteSource("Anvil/Anvil.cs", "namespace Fixture.Sub;\n\npublic sealed class Anvil\n{\n}\n");
        WriteSource("global.json", UnsatisfiablePin.Replace("10.0.999", "9.0.999", StringComparison.Ordinal));
        WriteSource(".gitignore", "bin/\nobj/\n");

        _ = CreateRepo(("global.json", UnsatisfiablePin));
        ConfigureRepo(source, init: true);
        Git(source, "add", "-A");
        Git(source, "commit", "--quiet", "-m", "submodule fixture");

        Git(_checkout, "-c", "protocol.file.allow=always", "-c", "core.autocrlf=false",
            "submodule", "add", "--quiet", source, "libs/sub");
        var sub = Path.Combine(_checkout, "libs", "sub");
        ConfigureRepo(sub, init: false);
        Git(_checkout, "commit", "--quiet", "-m", "add submodule");
        Assert.IsTrue(File.Exists(Path.Combine(sub, ".git")), "the submodule is absorbed (.git is a gitdir file)");
        return Git(_checkout, "rev-parse", "HEAD").Trim();
    }

    private static void ConfigureRepo(string dir, bool init)
    {
        if (init)
            Git(dir, "init", "--quiet", "--initial-branch", "main");
        Git(dir, "config", "user.email", "test@example.com");
        Git(dir, "config", "user.name", "Sextant Test");
        Git(dir, "config", "commit.gpgsign", "false");
        Git(dir, "config", "core.autocrlf", "false");
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
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("git could not be started");
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({p.ExitCode}): {stderr}");
        return stdout;
    }

    private long Scalar(string sql)
    {
        using var cmd = _db.GetConnection().CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private string? ScalarString(string sql)
    {
        using var cmd = _db.GetConnection().CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar() as string;
    }

    private sealed class FixedCheckoutProvider(CheckoutResolution resolution) : ICheckoutProvider
    {
        public bool TryResolve(EnsureSnapshotRequest request, out CheckoutResolution resolved)
        {
            resolved = resolution;
            return true;
        }
    }
}
