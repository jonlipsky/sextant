using Sextant.Core.Platform;
using Sextant.Service.Placement;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Phase 15 — the capability-routing worker (<see cref="CapabilityRoutingSnapshotWorker"/>) driven through
/// the REAL <see cref="SnapshotService"/> control plane. The native Windows/macOS placements are the
/// substitutable seam (<see cref="IWorkerPlacement"/>), so criteria 2 &amp; 3 — a project Linux cannot
/// evaluate routes to a Windows / macOS worker and produces correct semantics — are fully covered on a
/// Linux CI with fake native placements (criterion 6). Real native execution is Phase 14.
///
/// <list type="bullet">
///   <item><b>Criterion 1</b> — a portable graph stays on the default (Linux) placement, no routing;</item>
///   <item><b>Criterion 2</b> — a Windows-only project routes to the Windows placement and publishes a
///   complete snapshot;</item>
///   <item><b>Criterion 3</b> — an Apple project routes to the macOS placement and publishes complete;</item>
///   <item><b>Criterion 4</b> — no compatible worker ⇒ the job is <see cref="SnapshotJobStatus.Unsupported"/>
///   with a structured <c>no_compatible_worker</c> per-project diagnostic and NO complete snapshot;</item>
///   <item>routed success records a <c>routed_to_native_worker</c> provenance diagnostic (criterion 5).</item>
/// </list>
/// </summary>
[TestClass]
public class CapabilityRoutingWorkerTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SnapshotService _service = null!;

    private static readonly WorkerCapability LinuxNode =
        WorkerCapability.Create(PlatformOperatingSystem.Linux, "x64", targetPlatforms: ["linux"]);
    private static readonly WorkerCapability WindowsWorker =
        WorkerCapability.Create(PlatformOperatingSystem.Windows, "x64", targetPlatforms: ["windows"]);
    private static readonly WorkerCapability MacWorker =
        WorkerCapability.Create(PlatformOperatingSystem.MacOS, "arm64",
            targetPlatforms: ["ios", "maccatalyst", "macos"], installedWorkloads: ["ios"]);

    [TestCleanup]
    public void TestCleanup()
    {
        _service?.Dispose();
        SqliteTestDatabase.Delete(_dbPath, _db);
    }

    // ==== Criterion 1: a portable graph is not needlessly routed ==================================

    [TestMethod]
    public async Task Criterion1_PortableGraph_StaysOnDefaultLinuxPlacement()
    {
        var db = NewDb();
        var linux = PublishingPlacement(LinuxNode, isDefault: true, db);
        var windows = ThrowingPlacement(WindowsWorker, "the Windows worker must not run for a portable graph");

        // The default probe reports everything Linux-capable, so no escalation occurs.
        var worker = new CapabilityRoutingSnapshotWorker(linux, [windows]);
        var result = await StartWith(worker).EnsureSnapshotAsync(ServiceTestFixtures.Request());

        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status);
        Assert.AreEqual(1, linux.Calls, "the default placement produced the snapshot");
        Assert.AreEqual(0, windows.Calls, "a portable graph is never routed to the Windows worker (criterion 1)");
    }

    // ==== Criterion 2: a Windows-only project routes to the Windows worker =========================

    [TestMethod]
    public async Task Criterion2_WindowsOnlyProject_RoutesToWindowsWorker_AndPublishes()
    {
        var db = NewDb();
        var linux = ThrowingPlacement(LinuxNode, "the default worker must not run a project it cannot evaluate", isDefault: true);
        var windows = PublishingPlacement(WindowsWorker, isDefault: false, db);

        var probe = FakeProbe.LinuxInsufficient("src/App/App.csproj", targetPlatform: "windows",
            reason: "missing Windows targeting pack");
        var worker = new CapabilityRoutingSnapshotWorker(linux, [windows], probe);

        var result = await StartWith(worker).EnsureSnapshotAsync(ServiceTestFixtures.Request());

        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status, "the Windows worker produced correct semantics");
        Assert.AreEqual(1, windows.Calls, "the job was routed to the Windows worker (criterion 2)");
        Assert.AreEqual(0, linux.Calls, "the default worker was NOT used for the Windows-only project");

        var routed = _service.GetStatus(result.JobId)!.Diagnostics;
        Assert.IsTrue(routed.Any(d => d.Code == "routed_to_native_worker"),
            "a routed build records provenance that it ran off the default worker (criterion 5)");
    }

    // ==== Criterion 3: an Apple project routes to the macOS worker ================================

    [TestMethod]
    public async Task Criterion3_AppleProject_RoutesToMacWorker_AndPublishes()
    {
        var db = NewDb();
        var linux = ThrowingPlacement(LinuxNode, "the default worker cannot evaluate an Apple project", isDefault: true);
        var mac = PublishingPlacement(MacWorker, isDefault: false, db);
        var windows = ThrowingPlacement(WindowsWorker, "an Apple project must not route to Windows");

        var probe = FakeProbe.LinuxInsufficient("src/App/App.csproj", targetPlatform: "ios",
            reason: "missing iOS workload");
        var worker = new CapabilityRoutingSnapshotWorker(linux, [windows, mac], probe);

        var result = await StartWith(worker).EnsureSnapshotAsync(ServiceTestFixtures.Request());

        Assert.AreEqual(SnapshotJobStatus.Complete, result.Status, "the macOS worker produced correct semantics");
        Assert.AreEqual(1, mac.Calls, "the job was routed to the macOS worker (criterion 3)");
        Assert.AreEqual(0, windows.Calls, "an Apple project never routes to Windows");
    }

    // ==== Criterion 4: no compatible worker → fail closed, structured diagnostic, never complete ===

    [TestMethod]
    public async Task Criterion4_NoCompatibleWorker_FailsClosed_WithStructuredDiagnostic()
    {
        var db = NewDb();
        var linux = ThrowingPlacement(LinuxNode, "the default worker cannot evaluate the project", isDefault: true);
        var windows = ThrowingPlacement(WindowsWorker, "the Windows worker cannot evaluate an Apple project");

        // An Apple project, but only Linux + Windows workers exist → no compatible worker.
        var probe = FakeProbe.LinuxInsufficient("src/Mobile/Mobile.csproj", targetPlatform: "ios",
            reason: "missing iOS workload");
        var worker = new CapabilityRoutingSnapshotWorker(linux, [windows], probe);

        var result = await StartWith(worker).EnsureSnapshotAsync(ServiceTestFixtures.Request());

        Assert.AreEqual(SnapshotJobStatus.Unsupported, result.Status,
            "no compatible worker → the job fails closed, never a silent empty success (criterion 4)");
        Assert.IsNull(result.SnapshotId, "no snapshot is published complete");
        Assert.AreEqual(0, windows.Calls, "the incompatible Windows worker was never invoked");

        var diagnostics = _service.GetStatus(result.JobId)!.Diagnostics;
        var diag = diagnostics.Single(d => d.Code == "no_compatible_worker");
        Assert.AreEqual(JobDiagnosticSeverity.Error, diag.Severity, "the fail-closed diagnostic is an error");
        Assert.AreEqual("src/Mobile/Mobile.csproj", diag.ProjectPath, "the diagnostic names the affected project");

        // The fail-closed job never published a complete snapshot for the identity.
        Assert.IsNull(new SnapshotStore(_db.GetConnection())
            .GetByIdentityHash(ServiceTestFixtures.Request().ToIdentity().Hash),
            "no snapshot row exists for a fail-closed identity");
    }

    [TestMethod]
    public async Task LinuxOnlyPolicy_ProjectLinuxCannotEvaluate_FailsClosed()
    {
        var db = NewDb();
        var linux = ThrowingPlacement(LinuxNode, "the default worker cannot evaluate the project", isDefault: true);
        var windows = ThrowingPlacement(WindowsWorker, "linux_only policy must never escalate");

        var probe = FakeProbe.LinuxInsufficient("src/App/App.csproj", targetPlatform: "windows",
            reason: "missing Windows targeting pack");
        // Even though a compatible Windows worker exists, the linux_only policy forbids escalation.
        var worker = new CapabilityRoutingSnapshotWorker(linux, [windows], probe, PlatformRoutingPolicy.LinuxOnly);

        var result = await StartWith(worker).EnsureSnapshotAsync(ServiceTestFixtures.Request());

        Assert.AreEqual(SnapshotJobStatus.Unsupported, result.Status,
            "linux_only policy fails a non-Linux-evaluable project closed rather than escalating");
        Assert.AreEqual(0, windows.Calls, "policy withheld the compatible Windows worker");
    }

    // ==== helpers =================================================================================

    private IndexDatabase NewDb()
    {
        _dbPath = ServiceTestFixtures.NewDbPath();
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        return _db;
    }

    private SnapshotService StartWith(ISnapshotWorker worker)
    {
        // DefaultCapabilityFingerprint stays null (as on a plain single-node/local run) so the service's
        // expected identity matches the placement's published snapshot; the identity/capability fold is
        // covered by the Core/Store/MCP tests.
        _service = SnapshotService.Start(ServiceTestFixtures.NewOptions(_dbPath), worker, _db);
        return _service;
    }

    private FakePlacement PublishingPlacement(WorkerCapability capability, bool isDefault, IndexDatabase db) =>
        new(capability, isDefault, (request, _) =>
            SnapshotWorkResult.Complete(ServiceTestFixtures.PublishComplete(db, request)));

    private static FakePlacement ThrowingPlacement(WorkerCapability capability, string why, bool isDefault = false) =>
        new(capability, isDefault, (_, _) => throw new InvalidOperationException(why));

    private sealed class FakePlacement(
        WorkerCapability capability,
        bool isDefault,
        Func<EnsureSnapshotRequest, string, SnapshotWorkResult> produce) : IWorkerPlacement
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public WorkerCapability Capability { get; } = capability;
        public bool IsDefault { get; } = isDefault;

        public Task<SnapshotWorkResult> ProduceAsync(
            EnsureSnapshotRequest request, string identityHash, string scratchDir, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(produce(request, identityHash));
        }
    }

    private sealed class FakeProbe(ProbeResult result) : IPlatformEvaluationProbe
    {
        public Task<ProbeResult> ProbeAsync(
            EnsureSnapshotRequest request, string scratchDir, CancellationToken cancellationToken) =>
            Task.FromResult(result);

        /// <summary>A probe reporting ONE project as demonstrably insufficient on Linux for a platform.</summary>
        public static FakeProbe LinuxInsufficient(string projectPath, string targetPlatform, string reason) =>
            new(LinuxEvaluationAnalyzer.ToProbeResult([
                new ProjectEvaluationInfo
                {
                    ProjectId = projectPath,
                    ProjectPath = projectPath,
                    TargetPlatform = targetPlatform,
                    Loaded = true,
                    MissingCapabilityDiagnostics = [reason]
                }
            ]));
    }
}
