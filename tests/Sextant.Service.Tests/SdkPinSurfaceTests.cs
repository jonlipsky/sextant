using System.Text.Json;
using Microsoft.CodeAnalysis;
using Sextant.Core;
using Sextant.Indexer;
using Sextant.Service.SdkPin;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #113 — how an unsatisfiable <c>global.json</c> SDK pin is SURFACED: typed job diagnostics
/// (<c>sdk_pin_overridden</c> / <c>sdk_resolution_failed</c>), a typed failure instead of a bare message,
/// coverage reasons + <c>sdk_pin_overrides</c> provenance, the audit flag, and the operator knob.
/// </summary>
[TestClass]
public sealed class SdkPinSurfaceTests
{
    private const string CheckoutDir = "/checkout/repo";

    // The coverage store's wire options (snake_case, nulls omitted).
    private static readonly JsonSerializerOptions CoverageJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static string HostFxrMessage(string globalJson, string version = "10.0.999") =>
        "An exception of type System.InvalidOperationException was thrown: Error while calling hostfxr function " +
        $"hostfxr_resolve_sdk2. Error code: -2147450725 Detailed error: A compatible .NET SDK was not found.\n\n" +
        $"Requested SDK version: {version}\nglobal.json file: {globalJson}\n\nInstalled SDKs:\n";

    private static SdkPinFinding Finding(bool applied, string? reason = null) => new()
    {
        GlobalJsonPath = "global.json",
        FullPath = $"{CheckoutDir}/global.json",
        InsideCheckout = true,
        RequestedVersion = "10.0.300",
        RollForward = "disable",
        InstalledSdks = ["10.0.401"],
        ResolvedSdkVersion = applied ? "10.0.401" : null,
        OverrideApplied = applied,
        NotOverriddenReason = reason
    };

    private static CheckoutResolution Resolution() => new()
    {
        CheckoutDir = CheckoutDir,
        SelectedSolutions = [$"{CheckoutDir}/App.slnx", $"{CheckoutDir}/tools/Tools.slnx"],
        Source = SolutionSelectionSource.Configured,
        SkippedSolutions = [],
        DiscoveredButNotSelected = []
    };

    [TestMethod]
    public void OverriddenPin_IsAWarningNamingRequestedAndSubstitutedSdk()
    {
        var diagnostic = LocalIndexerSnapshotWorker.SdkPinDiagnostics([Finding(applied: true)], published: true).Single();

        Assert.AreEqual(LocalIndexerSnapshotWorker.SdkPinOverriddenCode, diagnostic.Code);
        Assert.AreEqual(JobDiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.AreEqual("global.json", diagnostic.ProjectPath);
        StringAssert.Contains(diagnostic.Message, "pins .NET SDK 10.0.300 (rollForward: disable)");
        StringAssert.Contains(diagnostic.Message, "installed SDK(s): 10.0.401");
        StringAssert.Contains(diagnostic.Message, "Override applied");
        StringAssert.Contains(diagnostic.Message, "substituted SDK");
    }

    [TestMethod]
    public void PinNotOverridden_IsTypedWithTheReason_ErrorOnlyWhenNothingPublished()
    {
        var pin = Finding(applied: false, reason: "the service's SDK-pin override is disabled");

        var published = LocalIndexerSnapshotWorker.SdkPinDiagnostics([pin], published: true).Single();
        var failed = LocalIndexerSnapshotWorker.SdkPinDiagnostics([pin], published: false).Single();

        Assert.AreEqual(LocalIndexerSnapshotWorker.SdkResolutionFailedCode, published.Code);
        Assert.AreEqual(JobDiagnosticSeverity.Warning, published.Severity);
        Assert.AreEqual(JobDiagnosticSeverity.Error, failed.Severity);
        StringAssert.Contains(failed.Message, "Override not applied: the service's SDK-pin override is disabled");
    }

    [TestMethod]
    public void WholeLoadSdkFailure_IsATypedFailure_NotABareMessage()
    {
        Assert.IsTrue(HostFxrSdkResolutionError.TryParse(HostFxrMessage($"{CheckoutDir}/global.json"), out var error));

        var result = LocalIndexerSnapshotWorker.SdkResolutionFailed(CheckoutDir, error, [], ["10.0.401", "9.0.305"]);

        Assert.AreEqual(SnapshotJobStatus.Failed, result.Status);
        StringAssert.Contains(result.Error, "'global.json' requests SDK 10.0.999");
        StringAssert.Contains(result.Error, "installed SDK(s): 10.0.401, 9.0.305");
        Assert.IsFalse(result.Error!.Contains(CheckoutDir, StringComparison.Ordinal), "the worker's volume layout is redacted");
        var diagnostic = result.Projects.Single();
        Assert.AreEqual(LocalIndexerSnapshotWorker.SdkResolutionFailedCode, diagnostic.Code);
        Assert.AreEqual(JobDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.AreEqual("global.json", diagnostic.ProjectPath);
    }

    [TestMethod]
    public void WholeLoadSdkFailure_ForAKnownPin_SaysWhyItWasNotOverridden_WithoutDuplicating()
    {
        Assert.IsTrue(HostFxrSdkResolutionError.TryParse(HostFxrMessage($"{CheckoutDir}/global.json", "10.0.300"), out var error));
        var pin = Finding(applied: false, reason: "the global.json is a symbolic link, so the service does not modify it");

        var result = LocalIndexerSnapshotWorker.SdkResolutionFailed(CheckoutDir, error, [pin], ["10.0.401"]);

        StringAssert.Contains(result.Error, "(rollForward: disable)");
        StringAssert.Contains(result.Error, "The pin was not overridden: the global.json is a symbolic link");
        Assert.AreEqual(1, result.Projects.Count, "the pin's own diagnostic already names it");
        Assert.AreEqual(JobDiagnosticSeverity.Error, result.Projects[0].Severity);
    }

    [TestMethod]
    public void WholeLoadSdkFailure_WithNoPin_NeverBlamesAGlobalJson()
    {
        // hostfxr also fails resolution on a worker with no usable SDK at all; there is no pin to relax then.
        Assert.IsTrue(HostFxrSdkResolutionError.TryParse(
            "Error while calling hostfxr function hostfxr_resolve_sdk2. Error code: -2147450725 Detailed error: " +
            "No .NET SDKs were found.", out var error));
        Assert.IsFalse(error.IsGlobalJsonPin);

        var result = LocalIndexerSnapshotWorker.SdkResolutionFailed(CheckoutDir, error, [], []);

        Assert.AreEqual(SnapshotJobStatus.Failed, result.Status);
        StringAssert.Contains(result.Error, "no compatible .NET SDK could be resolved on this worker");
        StringAssert.Contains(result.Error, "installed SDK(s): unknown");
        Assert.IsFalse(result.Error!.Contains("rollForward", StringComparison.Ordinal));
        Assert.IsFalse(result.Error.Contains("pins", StringComparison.Ordinal));
        var diagnostic = result.Projects.Single();
        Assert.AreEqual(LocalIndexerSnapshotWorker.SdkResolutionFailedCode, diagnostic.Code);
        Assert.AreEqual(JobDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.IsNull(diagnostic.ProjectPath);
        StringAssert.Contains(diagnostic.Message, "Install a .NET SDK on the worker");
    }

    [TestMethod]
    public void ProjectSkippedWithNoPin_IsTypedWithoutBlamingAGlobalJson()
    {
        var reason = "Error while calling hostfxr function hostfxr_resolve_sdk2. Error code: -2147450725 Detailed " +
                     "error: No .NET SDKs were found.";
        var skipped = new[] { new SkippedProject($"{CheckoutDir}/tools/Tool/Tool.csproj", reason) };
        var load = new MultiSolutionLoadResult(new AdhocWorkspace().CurrentSolution, skipped,
        [
            new SolutionCoverage($"{CheckoutDir}/App.slnx", 1, 1, []),
            new SolutionCoverage($"{CheckoutDir}/tools/Tools.slnx", 1, 0, skipped)
        ]);
        var resolution = Resolution();
        var coverage = SnapshotCoverageBuilder.Build(CheckoutDir, resolution, load, new SnapshotCoverageBuilder.Inventory([], []));

        var result = LocalIndexerSnapshotWorker.BuildResult(9, CheckoutDir, resolution, load, coverage, [], ["10.0.401"]);

        Assert.AreEqual(SnapshotJobStatus.Partial, result.Status);
        StringAssert.Contains(result.Error,
            "1 declared project(s) could not be loaded because no compatible .NET SDK could be resolved on this worker.");
        Assert.IsFalse(result.Error!.Contains("global.json pins", StringComparison.Ordinal));
        var diagnostic = result.Projects.Single(p => p.Code == LocalIndexerSnapshotWorker.SdkResolutionFailedCode);
        StringAssert.Contains(diagnostic.Message, "no compatible .NET SDK could be resolved (hostfxr reported no global.json pin)");
    }

    [TestMethod]
    public async Task UnreplayableLeftoverJournal_RequeuesTypedBeforeAnyLoad_AndKeepsTheJournal()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sextant_sdkpin_gate_{Guid.NewGuid():N}");
        var checkout = Path.Combine(root, "checkouts", "repo");
        var journalRoot = Path.Combine(root, "checkouts", SdkPinOptions.JournalDirectoryName);
        Directory.CreateDirectory(checkout);
        var dbPath = ServiceTestFixtures.NewDbPath();
        var db = new IndexDatabase(dbPath);
        db.RunMigrations();
        try
        {
            var guard = new SdkPinGuard(new SdkPinOptions { JournalRoot = journalRoot });
            var journal = guard.JournalPathFor(checkout);
            Directory.CreateDirectory(journalRoot);
            File.WriteAllText(journal, "{ not a journal");
            var resolution = new CheckoutResolution
            {
                CheckoutDir = checkout,
                SelectedSolutions = [Path.Combine(checkout, "App.slnx")],
                Source = SolutionSelectionSource.DefaultUnion
            };
            var worker = new LocalIndexerSnapshotWorker(
                db, new SextantConfiguration(), new FixedCheckoutProvider(resolution), sdkPinGuard: guard);

            var ex = await Assert.ThrowsExactlyAsync<TransientProvisioningException>(
                () => worker.ProduceAsync(ServiceTestFixtures.Request(), "identity", root, CancellationToken.None));

            Assert.AreEqual(LocalIndexerSnapshotWorker.SdkPinRestoreFailedCode, ex.DiagnosticCode);
            StringAssert.Contains(ex.Message, "could not restore");
            StringAssert.Contains(ex.Message, "restore journal could not be replayed");
            Assert.IsFalse(ex.Message.Contains(root, StringComparison.OrdinalIgnoreCase), "no worker volume paths");
            Assert.IsTrue(File.Exists(journal), "the journal is kept for inspection");
        }
        finally
        {
            SqliteTestDatabase.Delete(dbPath, db);
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

    private sealed class FixedCheckoutProvider(CheckoutResolution resolution) : ICheckoutProvider
    {
        public bool TryResolve(EnsureSnapshotRequest request, out CheckoutResolution resolved)
        {
            resolved = resolution;
            return true;
        }
    }

    [TestMethod]
    public void ProjectSkippedForAnSdkPin_IsTypedAndNamedInTheCoverageReason()
    {
        // Multi-solution isolation (#90 parity): only tools/ pins a missing band, so its project is skipped
        // with the hostfxr failure and the rest of the checkout still publishes — as PARTIAL, with the cause.
        var skipped = new[]
        {
            new SkippedProject($"{CheckoutDir}/tools/Tool/Tool.csproj", HostFxrMessage($"{CheckoutDir}/tools/global.json"))
        };
        var load = new MultiSolutionLoadResult(new AdhocWorkspace().CurrentSolution, skipped,
        [
            new SolutionCoverage($"{CheckoutDir}/App.slnx", 1, 1, []),
            new SolutionCoverage($"{CheckoutDir}/tools/Tools.slnx", 1, 0, skipped)
        ]);
        var resolution = Resolution();
        var coverage = SnapshotCoverageBuilder.Build(CheckoutDir, resolution, load, new SnapshotCoverageBuilder.Inventory([], []));

        var result = LocalIndexerSnapshotWorker.BuildResult(9, CheckoutDir, resolution, load, coverage, [], ["10.0.401"]);

        Assert.AreEqual(SnapshotJobStatus.Partial, result.Status);
        StringAssert.Contains(result.Error,
            "1 declared project(s) could not be loaded because the .NET SDK their global.json pins is not installed " +
            "on this worker ('tools/global.json' requests SDK 10.0.999)");
        var diagnostic = result.Projects.Single(p => p.ProjectPath?.Replace('\\', '/') == "tools/Tool/Tool.csproj");
        Assert.AreEqual(LocalIndexerSnapshotWorker.SdkResolutionFailedCode, diagnostic.Code, "typed, not a generic project_skipped");
        Assert.AreEqual(JobDiagnosticSeverity.Warning, diagnostic.Severity);
        StringAssert.Contains(diagnostic.Message, "installed SDK(s): 10.0.401");
        Assert.IsFalse(result.Projects.Any(p => p.Code == "project_skipped"));
    }

    [TestMethod]
    public void NonSdkSkip_KeepsTheExistingCoverageReason()
    {
        var skipped = new[] { new SkippedProject($"{CheckoutDir}/src/Ios/Ios.csproj", "iOS workload not available") };
        var load = new MultiSolutionLoadResult(new AdhocWorkspace().CurrentSolution, skipped,
            [new SolutionCoverage($"{CheckoutDir}/App.slnx", 2, 1, skipped)]);

        var coverage = SnapshotCoverageBuilder.Build(CheckoutDir, Resolution(), load, new SnapshotCoverageBuilder.Inventory([], []));

        CollectionAssert.Contains(coverage.Coverage.Reasons.ToList(),
            "1 declared project(s) could not be loaded on this worker (src/Ios/Ios.csproj (TFM unknown)); see the `project_skipped` diagnostics for each reason.");
        Assert.AreEqual(SnapshotCoverageVerdict.Partial, coverage.Coverage.Verdict);
        var gap = coverage.Coverage.EvaluationGaps!.Single();
        Assert.AreEqual("src/Ios/Ios.csproj", gap.Project);
        Assert.IsNull(gap.TargetFramework, "a workload failure does not establish a loaded TFM");
        Assert.IsFalse(gap.HasDocuments);
        Assert.IsFalse(coverage.Coverage.Reasons.Any(r => r.Contains("SDK", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void CoverageProvenance_RecordsTheOverride_AndIsByteIdenticalWithoutOne()
    {
        var load = new MultiSolutionLoadResult(new AdhocWorkspace().CurrentSolution, [],
            [new SolutionCoverage($"{CheckoutDir}/App.slnx", 1, 1, [])]);
        var inventory = new SnapshotCoverageBuilder.Inventory([], []);

        var plain = SnapshotCoverageBuilder.Build(CheckoutDir, Resolution(), load, inventory).Coverage;
        var overridden = SnapshotCoverageBuilder.Build(
            CheckoutDir, Resolution(), load, inventory, [Finding(applied: true).ToCoverageOverride()]).Coverage;

        Assert.IsNull(plain.SdkPinOverrides);
        Assert.IsFalse(JsonSerializer.Serialize(plain, CoverageJson).Contains("sdk_pin", StringComparison.Ordinal),
            "a snapshot built without an override serializes exactly as before #113");
        Assert.AreEqual(SnapshotCoverageVerdict.Complete, overridden.Verdict,
            "the override restores full coverage; it is provenance, not a coverage gap");
        var json = JsonSerializer.Serialize(overridden, CoverageJson);
        StringAssert.Contains(json, "\"sdk_pin_overrides\":[{\"global_json_path\":\"global.json\",\"requested_version\":\"10.0.300\"");

        var dbPath = ServiceTestFixtures.NewDbPath();
        var db = new IndexDatabase(dbPath);
        try
        {
            db.RunMigrations();
            var snapId = ServiceTestFixtures.PublishComplete(db, ServiceTestFixtures.Request());
            var store = new SnapshotCoverageStore(db.GetConnection());
            Assert.IsTrue(store.Record(snapId, overridden, 1));

            var roundTripped = store.Get(snapId)!.SdkPinOverrides!.Single();
            Assert.AreEqual("global.json", roundTripped.GlobalJsonPath);
            Assert.AreEqual("10.0.300", roundTripped.RequestedVersion);
            Assert.AreEqual("disable", roundTripped.RollForward);
            Assert.AreEqual("10.0.401", roundTripped.ResolvedSdkVersion);
            CollectionAssert.AreEqual(new[] { "10.0.401" }, roundTripped.InstalledSdks.ToArray());
        }
        finally
        {
            SqliteTestDatabase.Delete(dbPath, db);
        }
    }

    [TestMethod]
    public void AuditSuffix_FlagsOnlySdkPinOutcomes()
    {
        static SnapshotJobDiagnostic D(string code) => new() { JobId = 1, Severity = "warning", Code = code, Message = "m" };

        Assert.AreEqual(string.Empty, SnapshotService.SdkPinAuditSuffix([]));
        Assert.AreEqual(string.Empty, SnapshotService.SdkPinAuditSuffix([D("project_skipped"), D("solution_indexed")]));
        Assert.AreEqual(";sdk_pin_overridden", SnapshotService.SdkPinAuditSuffix([D("sdk_pin_overridden"), D("sdk_pin_overridden")]));
        Assert.AreEqual(";sdk_pin_overridden;sdk_resolution_failed",
            SnapshotService.SdkPinAuditSuffix([D("sdk_resolution_failed"), D("sdk_pin_overridden")]));
    }

    [TestMethod]
    public async Task OverriddenJob_IsVisibleInControlStatusAndTheAuditTrail()
    {
        var dbPath = ServiceTestFixtures.NewDbPath();
        var db = new IndexDatabase(dbPath);
        db.RunMigrations();
        var worker = new FakeSnapshotWorker(db, (self, request) =>
        {
            var snapId = ServiceTestFixtures.PublishComplete(self.Database, request);
            return SnapshotWorkResult.Complete(snapId,
                LocalIndexerSnapshotWorker.SdkPinDiagnostics([Finding(applied: true)], published: true));
        });
        var service = SnapshotService.Start(ServiceTestFixtures.NewOptions(dbPath), worker, db);
        try
        {
            var result = await service.EnsureSnapshotAsync(ServiceTestFixtures.Request());

            Assert.AreEqual(SnapshotJobStatus.Complete, result.Status, "an overridden pin still yields a snapshot");
            var status = service.GetStatus(result.JobId)!;
            var diagnostic = status.Diagnostics.Single(d => d.Code == LocalIndexerSnapshotWorker.SdkPinOverriddenCode);
            StringAssert.Contains(diagnostic.Message, "10.0.300");
            Assert.AreEqual("global.json", diagnostic.ProjectPath);

            var audit = new AuditLogStore(db.GetConnection()).Recent(action: AuditAction.Ensure).Single();
            Assert.AreEqual($"job_{result.JobId};sdk_pin_overridden", audit.Detail);
        }
        finally
        {
            service.Dispose();
            SqliteTestDatabase.Delete(dbPath, db);
        }
    }

    [TestMethod]
    public async Task RestoreFailure_IsRequeuedWithATypedDiagnostic_NotCachedAsATerminalFailure()
    {
        // A checkout that could not be put back is a checkout-state problem an operator repair resolves — it must
        // not poison the commit's identity. The service requeues it (bounded), typed as sdk_pin_restore_failed.
        var dbPath = ServiceTestFixtures.NewDbPath();
        var db = new IndexDatabase(dbPath);
        db.RunMigrations();
        var worker = new FakeSnapshotWorker(db, (self, request) =>
        {
            if (self.Calls == 1)
                throw new TransientProvisioningException(
                    "could not restore the committed file", diagnosticCode: LocalIndexerSnapshotWorker.SdkPinRestoreFailedCode);
            return SnapshotWorkResult.Complete(ServiceTestFixtures.PublishComplete(self.Database, request));
        });
        var service = SnapshotService.Start(ServiceTestFixtures.NewOptions(dbPath), worker, db);
        try
        {
            var request = ServiceTestFixtures.Request();
            var first = await service.EnsureSnapshotAsync(request);

            Assert.AreEqual(SnapshotJobStatus.Queued, first.Status, "requeued, not a cached terminal failure");
            var diagnostic = service.GetStatus(first.JobId)!.Diagnostics.Single();
            Assert.AreEqual(LocalIndexerSnapshotWorker.SdkPinRestoreFailedCode, diagnostic.Code);
            StringAssert.Contains(diagnostic.Message, "could not restore the committed file");
            var audit = new AuditLogStore(db.GetConnection()).Recent(action: AuditAction.Ensure).Single();
            Assert.AreEqual($"job_{first.JobId};sdk_pin_restore_failed", audit.Detail);

            // Once the checkout is repaired, the next ensure re-runs the worker and publishes.
            var retry = await service.EnsureSnapshotAsync(request);
            Assert.AreEqual(SnapshotJobStatus.Complete, retry.Status);
            Assert.AreEqual(2, worker.Calls);
        }
        finally
        {
            service.Dispose();
            SqliteTestDatabase.Delete(dbPath, db);
        }
    }

    [TestMethod]
    public async Task RestoreFailure_PastTheAttemptBound_SettlesFailed_AndKeepsTheTypedCode()
    {
        var dbPath = ServiceTestFixtures.NewDbPath();
        var db = new IndexDatabase(dbPath);
        db.RunMigrations();
        var worker = new FakeSnapshotWorker(db, (_, _) => throw new TransientProvisioningException(
            "could not restore the committed file", diagnosticCode: LocalIndexerSnapshotWorker.SdkPinRestoreFailedCode));
        var service = SnapshotService.Start(
            ServiceTestFixtures.NewOptions(dbPath) with { MaxProvisioningAttempts = 1 }, worker, db);
        try
        {
            var result = await service.EnsureSnapshotAsync(ServiceTestFixtures.Request());

            Assert.AreEqual(SnapshotJobStatus.Failed, result.Status);
            var codes = service.GetStatus(result.JobId)!.Diagnostics.Select(d => d.Code).ToArray();
            CollectionAssert.AreEquivalent(
                new[] { LocalIndexerSnapshotWorker.SdkPinRestoreFailedCode, "provisioning_attempts_exhausted" }, codes);
        }
        finally
        {
            service.Dispose();
            SqliteTestDatabase.Delete(dbPath, db);
        }
    }

    [TestMethod]
    public void SdkPinOverride_DefaultsOn_AndCanBeDisabled()
    {
        const string name = "SEXTANT_SERVICE_SDK_PIN_OVERRIDE";
        var config = new SextantConfiguration { DbPath = ServiceTestFixtures.NewDbPath() };
        Environment.SetEnvironmentVariable(name, null);
        Assert.IsTrue(ServiceOptions.FromEnvironment(config).SdkPinOverride);
        try
        {
            Environment.SetEnvironmentVariable(name, "false");
            Assert.IsFalse(ServiceOptions.FromEnvironment(config).SdkPinOverride);
            Environment.SetEnvironmentVariable(name, "nope");
            Assert.ThrowsExactly<InvalidOperationException>(() => ServiceOptions.FromEnvironment(config),
                "a malformed toggle fails closed like every other service boolean");
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [TestMethod]
    public void SdkPinIdentityComponent_IsNullByDefault_AndStrictWhenTheOverrideIsOff()
    {
        var options = ServiceTestFixtures.NewOptions(ServiceTestFixtures.NewDbPath());

        Assert.IsNull(options.SdkPinIdentityComponent, "the default policy keeps identities byte-identical");
        Assert.AreEqual(SdkPinOptions.StrictIdentityComponent, (options with { SdkPinOverride = false }).SdkPinIdentityComponent);

        // The worker publishes under the value its guard derives from the SAME toggle, so the two always agree.
        Assert.IsNull(new SdkPinGuard(new SdkPinOptions { OverrideEnabled = true }).IdentityComponent);
        Assert.AreEqual(SdkPinOptions.StrictIdentityComponent,
            new SdkPinGuard(new SdkPinOptions { OverrideEnabled = false }).IdentityComponent);

        var request = ServiceTestFixtures.Request();
        var context = LocalIndexerSnapshotWorker.CreateSnapshotContext(request, capability: null, SdkPinOptions.StrictIdentityComponent);
        Assert.AreEqual(SdkPinOptions.StrictIdentityComponent, context.SdkPinPolicy, "the orchestrator publishes under it");
        Assert.IsNull(LocalIndexerSnapshotWorker.CreateSnapshotContext(request, capability: null).SdkPinPolicy);

        Assert.AreEqual(request.ToIdentity("cfg", "cap").Hash, request.ToIdentity("cfg", "cap", sdkPinPolicy: null).Hash);
        Assert.AreNotEqual(request.ToIdentity("cfg", "cap").Hash,
            request.ToIdentity("cfg", "cap", SdkPinOptions.StrictIdentityComponent).Hash);
    }

    [TestMethod]
    public async Task FlippingTheOverride_EnsuresTheSameCommitUnderANewIdentity_AndRebuildsIt()
    {
        // A snapshot that is already published is never rebuilt, and a failed job is reused as recorded. So the
        // override policy must be part of the identity: otherwise a commit ensured with the override off (partial
        // or failed) would be silently reused after the operator turned it on, and the other way round.
        var dbPath = ServiceTestFixtures.NewDbPath();
        var db = new IndexDatabase(dbPath);
        db.RunMigrations();
        var hashes = new List<string>();
        SnapshotService? service = null;
        try
        {
            async Task<EnsureSnapshotResult> EnsureAsync(bool overrideEnabled, FakeSnapshotWorker worker)
            {
                service?.Dispose();
                service = SnapshotService.Start(
                    ServiceTestFixtures.NewOptions(dbPath) with { SdkPinOverride = overrideEnabled }, worker, db);
                var result = await service.EnsureSnapshotAsync(ServiceTestFixtures.Request());
                hashes.Add(result.IdentityHash);
                return result;
            }

            // Off: this node's worker fails the pinned commit (a whole-load SDK failure is a terminal failed job).
            var strictWorker = new FakeSnapshotWorker(db, FakeSnapshotWorker.FailedWithDiagnostics("SDK 10.0.300 is not installed"));
            var off = await EnsureAsync(overrideEnabled: false, strictWorker);
            Assert.AreEqual(SnapshotJobStatus.Failed, off.Status);

            // On: a NEW identity, so the failed job is not reused; the worker runs and the commit publishes.
            var overridingWorker = new FakeSnapshotWorker(db);
            var on = await EnsureAsync(overrideEnabled: true, overridingWorker);
            Assert.AreNotEqual(off.IdentityHash, on.IdentityHash);
            Assert.IsFalse(on.Attached, "a job produced under the other policy is never attached to");
            Assert.AreEqual(SnapshotJobStatus.Complete, on.Status, on.Reason);
            Assert.AreEqual(1, overridingWorker.Calls);
            Assert.AreEqual(ServiceTestFixtures.Request().ToIdentity().Hash, on.IdentityHash,
                "the default policy's identity is byte-identical to the pre-#113 identity");

            // Each policy is deterministic: re-ensuring under either attaches to that policy's own job.
            var onAgain = await EnsureAsync(overrideEnabled: true, overridingWorker);
            Assert.IsTrue(onAgain.Attached);
            Assert.AreEqual(on.SnapshotId, onAgain.SnapshotId);
            Assert.AreEqual(1, overridingWorker.Calls, "no rebuild under an unchanged policy");
            var offAgain = await EnsureAsync(overrideEnabled: false, strictWorker);
            Assert.AreEqual(off.IdentityHash, offAgain.IdentityHash);
            Assert.AreEqual(off.JobId, offAgain.JobId);
            Assert.AreEqual(1, strictWorker.Calls);
        }
        finally
        {
            service?.Dispose();
            SqliteTestDatabase.Delete(dbPath, db);
        }
    }

    [TestMethod]
    public async Task OverrideOffNode_PublishesUnderTheStrictIdentity_AndItValidates()
    {
        // The worker on an override-disabled node publishes under the strict component; the service's request
        // identity carries the same component, so its published-identity validation accepts the result.
        var dbPath = ServiceTestFixtures.NewDbPath();
        var db = new IndexDatabase(dbPath);
        db.RunMigrations();
        var worker = new FakeSnapshotWorker(db, (self, request) => SnapshotWorkResult.Complete(
            ServiceTestFixtures.PublishComplete(self.Database, request, sdkPinPolicy: SdkPinOptions.StrictIdentityComponent)));
        var service = SnapshotService.Start(
            ServiceTestFixtures.NewOptions(dbPath) with { SdkPinOverride = false }, worker, db);
        try
        {
            var result = await service.EnsureSnapshotAsync(ServiceTestFixtures.Request());

            Assert.AreEqual(SnapshotJobStatus.Complete, result.Status, result.Reason);
            Assert.AreEqual(
                ServiceTestFixtures.Request().ToIdentity(sdkPinPolicy: SdkPinOptions.StrictIdentityComponent).Hash,
                result.IdentityHash);
        }
        finally
        {
            service.Dispose();
            SqliteTestDatabase.Delete(dbPath, db);
        }
    }
}
