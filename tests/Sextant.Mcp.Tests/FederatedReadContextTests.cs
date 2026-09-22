using System.Text.Json;
using Sextant.Core;
using Sextant.Mcp;
using Sextant.Mcp.Tools;
using Sextant.Store;

namespace Sextant.Mcp.Tests;

/// <summary>
/// Phase 11 — the federated read planner (<see cref="FederatedReadContext"/> / <see cref="ReadContextGate"/>)
/// and the provenance/compatibility/authorization contracts it stamps into every MCP response's
/// <c>meta</c>. Covers:
/// <list type="bullet">
///   <item><b>Criterion 4</b> — a tool response's <c>meta.snapshot</c> carries base snapshot, overlay
///   generation, completeness (+ the existing result count);</item>
///   <item><b>Criterion 3 / issue #41</b> — read-time compatibility of the committed base against the
///   running binary/config is annotated, not silently mixed, and results are still returned;</item>
///   <item><b>Criterion 6</b> — an authorization denial fails CLOSED as a structured <c>meta.error</c>,
///   never an empty successful result;</item>
///   <item><b>Issue #42</b> — the (base snapshot, overlay generation) pair is pinned ONCE per request and
///   survives a concurrent publish, so sub-queries never splice two states;</item>
///   <item><b>Legacy parity</b> — a database with no selected snapshot omits <c>meta.snapshot</c> so its
///   response stays byte-identical to pre-Phase-11.</item>
/// </list>
/// </summary>
[TestClass]
public class FederatedReadContextTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private DatabaseProvider _dbProvider = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_fedctx_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _dbProvider = new DatabaseProvider(_dbPath);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _dbProvider?.Dispose();
        SqliteTestDatabase.Delete(_dbPath, _db);
    }

    // ==== Criterion 4: provenance meta on a real tool response =====================================

    [TestMethod]
    public void FindReferences_OnOverlay_StampsBaseOverlayCompletenessProvenance()
    {
        var s = SeedBaseAndOverlay();

        var result = FindReferencesTool.FindReferences(_dbProvider, "global::A.Widget");
        var meta = JsonDocument.Parse(result).RootElement.GetProperty("meta");

        Assert.IsTrue(meta.GetProperty("result_count").GetInt32() >= 1, "the overlay reference is returned");

        var snap = meta.GetProperty("snapshot");
        Assert.AreEqual(s.BaseId, snap.GetProperty("base_snapshot_id").GetInt64(), "base snapshot is stamped");
        Assert.AreEqual(s.OverlayId, snap.GetProperty("overlay_generation").GetInt64(), "overlay generation is stamped");
        Assert.IsTrue(snap.GetProperty("is_overlay").GetBoolean(), "the selected generation is an overlay");
        Assert.AreEqual("complete", snap.GetProperty("completeness").GetString(), "completeness is stamped");
        Assert.AreEqual("federated", snap.GetProperty("scope").GetString(), "the default read is federated");
        Assert.IsTrue(snap.GetProperty("compatible").GetBoolean(), "a same-binary base is compatible");
        Assert.IsTrue(snap.GetProperty("dirty").GetBoolean(), "an overlay carries a working-tree delta");
    }

    [TestMethod]
    public void FindReferences_BaseOnlyFederationMode_IsReflectedInProvenanceScope()
    {
        SeedBaseAndOverlay();

        var result = FindReferencesTool.FindReferences(_dbProvider, "global::A.Widget", federation: "base_only");
        var snap = JsonDocument.Parse(result).RootElement.GetProperty("meta").GetProperty("snapshot");

        Assert.AreEqual("base_only", snap.GetProperty("scope").GetString(),
            "an explicit diagnostic partition is surfaced in provenance");
    }

    // ==== Legacy parity: no snapshot ⇒ no snapshot meta block ======================================

    [TestMethod]
    public void FindReferences_LegacyDbWithoutSnapshots_OmitsSnapshotMeta()
    {
        // A plain project with no snapshot generation (a pre-Phase-9 / direct-seed database).
        var conn = _db.GetConnection();
        var projectId = new ProjectStore(conn).Insert(new ProjectIdentity
        {
            CanonicalId = "legacy0123456789",
            GitRemoteUrl = "https://github.com/test/legacy",
            RepoRelativePath = "src/L/L.csproj"
        }, 1);
        var sym = new SymbolStore(conn).Insert(new SymbolInfo
        {
            ProjectId = projectId,
            SymbolKey = "global::L.Thing", FullyQualifiedName = "global::L.Thing",
            DisplayName = "Thing", Kind = SymbolKind.Class, Accessibility = Accessibility.Public,
            FilePath = "src/L/Thing.cs", LineStart = 1, LineEnd = 2, LastIndexedAt = 1
        });
        new ReferenceStore(conn).Insert(new ReferenceInfo
        {
            SymbolId = sym, InProjectId = projectId, FilePath = "src/L/Caller.cs", Line = 3,
            ReferenceKind = ReferenceKind.Invocation
        });

        var result = FindReferencesTool.FindReferences(_dbProvider, "global::L.Thing");
        var meta = JsonDocument.Parse(result).RootElement.GetProperty("meta");

        Assert.IsFalse(meta.TryGetProperty("snapshot", out _),
            "a database with no selected snapshot omits meta.snapshot (byte-identical legacy parity)");
    }

    // ==== Criterion 3 / issue #41: read-time compatibility is annotated, not hidden =================

    [TestMethod]
    public void Resolve_DriftedBaseFingerprint_MarksIncompatibleButStillResolves()
    {
        SeedBaseAndOverlay();

        // Simulate the running binary/config having drifted from the base snapshot's stored fingerprint.
        var drift = new CompatibilityInputs(
            SchemaVersion: IndexDatabase.LatestSchemaVersion,
            AnalyzerVersion: "999",
            ToolchainFingerprint: "some-other-toolchain");

        var ctx = FederatedReadContext.Resolve(_db, compatibility: drift);

        Assert.IsNotNull(ctx.Provenance);
        Assert.IsFalse(ctx.Provenance!.Compatible, "a drifted base is flagged incompatible at READ time (#41)");
        Assert.IsNotNull(ctx.Provenance.Incompatibilities);
        CollectionAssert.AreEquivalent(
            new[] { "analyzer", "toolchain" },
            ctx.Provenance.Incompatibilities!.Select(i => i.Dimension).ToList(),
            "the mismatched dimensions are named");
        Assert.IsTrue(ctx.Scope.IsScoped, "results are still served — incompatibility annotates, it does not hide");
    }

    [TestMethod]
    public void Resolve_MatchingFingerprint_IsCompatible()
    {
        SeedBaseAndOverlay();
        var ctx = FederatedReadContext.Resolve(_db); // default = current running inputs, matching the seed
        Assert.IsNotNull(ctx.Provenance);
        Assert.IsTrue(ctx.Provenance!.Compatible, "a base built by this binary/config is compatible");
        Assert.IsNull(ctx.Provenance.Incompatibilities);
    }

    // ==== Phase 15 / criterion 5: worker-capability drift gates cache reuse ========================

    [TestMethod]
    public void Capability_DriftBetweenSnapshotAndRunningNode_IsFlaggedIncompatible()
    {
        // A snapshot built under a macOS/Apple worker capability must NOT be silently reused under a
        // Linux running node — the capability dimension flags the drift so the read annotates it.
        var running = CompatibilityInputs.Current with { CapabilityFingerprint = "cap-linux" };
        var appleBuilt = CompatRow(capability: "cap-macos-apple");

        var issues = ReadCompatibility.Evaluate(appleBuilt, running);

        var capability = issues.Single(i => i.Dimension == "capability");
        Assert.AreEqual("cap-linux", capability.Expected, "the running node's capability is named");
        Assert.AreEqual("cap-macos-apple", capability.Actual, "the snapshot's producing capability is named");
    }

    [TestMethod]
    public void Capability_NullOnEitherSide_IsUnknownNotMismatch()
    {
        // A local snapshot leaves capability null; that must read as "unknown", never a false mismatch,
        // so pre-Phase-15 and local generations keep serving (CRITICAL 2 / no crying wolf).
        var running = CompatibilityInputs.Current with { CapabilityFingerprint = "cap-linux" };

        Assert.IsFalse(ReadCompatibility.Evaluate(CompatRow(capability: null), running).Any(i => i.Dimension == "capability"),
            "a null snapshot capability is unknown, not a mismatch");
        Assert.IsFalse(ReadCompatibility.Evaluate(CompatRow(capability: "cap-linux"),
            CompatibilityInputs.Current with { CapabilityFingerprint = null }).Any(i => i.Dimension == "capability"),
            "a null running capability is unknown, not a mismatch");
    }

    [TestMethod]
    public void Capability_SameFingerprint_IsCompatible()
    {
        var running = CompatibilityInputs.Current with { CapabilityFingerprint = "cap-linux" };
        Assert.IsFalse(ReadCompatibility.Evaluate(CompatRow(capability: "cap-linux"), running).Any(i => i.Dimension == "capability"),
            "identical capability fingerprints are compatible");
    }

    private static SnapshotRow CompatRow(string? capability) => new()
    {
        Id = 1, RepositoryId = 1, IdentityHash = "h", Status = "complete", CreatedAt = 1,
        SchemaVersion = IndexDatabase.LatestSchemaVersion,
        AnalyzerVersion = IndexConfigurationHash.AnalyzerVersion,
        ToolchainFingerprint = Sextant.Core.ToolchainFingerprint.Current,
        CapabilityFingerprint = capability
    };

    // ==== Criterion 6: authorization fails CLOSED, never an empty success ===========================

    [TestMethod]
    public void ReadContextGate_UnauthorizedRead_ReturnsStructuredErrorNotEmptySuccess()
    {
        SeedBaseAndOverlay();

        var denied = ReadContextGate.TryResolve(
            _db, out _, out var errorResponse, authorizer: new DenyingAuthorizer());

        Assert.IsFalse(denied, "an unauthorized read does not resolve");
        var meta = JsonDocument.Parse(errorResponse).RootElement.GetProperty("meta");
        Assert.IsTrue(meta.TryGetProperty("error", out var error), "the denial is a structured error, not empty success");
        Assert.AreEqual("authorization_denied", error.GetProperty("code").GetString());
        Assert.AreEqual(0, meta.GetProperty("result_count").GetInt32());
    }

    [TestMethod]
    public void ReadContextGate_AllowedRead_Resolves()
    {
        SeedBaseAndOverlay();
        var ok = ReadContextGate.TryResolve(_db, out var ctx, out var err);
        Assert.IsTrue(ok);
        Assert.AreEqual(string.Empty, err);
        Assert.IsTrue(ctx.Authorization.Allowed);
    }

    [TestMethod]
    public void Resolve_DeniedRead_ScopeReadsEmptyNotUnscoped()
    {
        SeedBaseAndOverlay(); // real data exists that an unscoped read would leak

        var ctx = FederatedReadContext.Resolve(_db, authorizer: new DenyingAuthorizer());

        Assert.IsFalse(ctx.Authorization.Allowed);
        Assert.IsTrue(ctx.Scope.IsScoped, "a denied scope restricts rows (it is not the wide-open Unscoped)");

        // Belt-and-suspenders (criterion 6): even a caller that reads Scope WITHOUT consulting the
        // authorization verdict must leak nothing — the deny-all scope matches no rows by construction.
        var symbols = new SymbolStore(_db.GetConnection()) { Scope = ctx.Scope };
        Assert.AreEqual(0, symbols.ResolveByFqn("global::A.Widget").Count,
            "a denied read returns no rows even though the symbol exists");
    }

    // ==== Issue #42: the resolved scope is pinned ONCE and survives a concurrent publish ============

    [TestMethod]
    public void Resolve_PinsScopeOnce_AndSurvivesConcurrentPublish()
    {
        var s = SeedBaseAndOverlay();

        // Resolve the request context — this pins the (base, overlay) pair once.
        var ctx = FederatedReadContext.Resolve(_db);
        Assert.AreEqual(s.OverlayId, ctx.Scope.SnapshotId, "the read is pinned to the selected overlay generation");

        // A concurrent publisher advances the default branch to a brand-new generation mid-request.
        var store = new SnapshotStore(_db.GetConnection());
        var newGen = store.BeginPending(
            OverlayIdentity("delta_edit2"), s.RepoId, s.CommitId, runId: null, now: 9, baseSnapshotId: s.BaseId).id;
        store.MarkComplete(newGen, publishedAt: 9);
        store.SetBranchPointer(s.BranchId, newGen, now: 9);

        // The already-resolved context must NOT re-observe the new generation — it stays on its pin.
        Assert.AreEqual(s.OverlayId, ctx.Scope.SnapshotId,
            "a mid-request publish never splices a second state into an already-pinned request (#42)");

        // A fresh request, however, sees the newly published generation.
        var next = FederatedReadContext.Resolve(_db);
        Assert.AreEqual(newGen, next.Scope.SnapshotId, "a subsequent request resolves the new generation");
    }

    [TestMethod]
    public void ProjectStore_PinnedToRequestScope_ResistsConcurrentPublish()
    {
        // The read-side analogue of #42 at ProjectStore granularity: a ProjectStore that carries the
        // request's pinned scope must keep resolving the pinned generation's project-version even after a
        // publisher moves the branch pointer, while an UNPINNED store follows the moved pointer. This is
        // exactly the splice the tools' `new ProjectStore(conn) { Scope = readContext.Scope }` prevents.
        var s = SeedBaseAndOverlay();
        var conn = _db.GetConnection();

        var ctx = FederatedReadContext.Resolve(_db);
        Assert.AreEqual(s.OverlayId, ctx.Scope.SnapshotId, "the request pins the selected overlay generation");

        var pinnedRowId = new ProjectStore(conn) { Scope = ctx.Scope }.GetByCanonicalId("logical_A")!.Value.id;

        // A concurrent publisher lands a brand-new generation carrying its OWN project-version for logical_A.
        var store = new SnapshotStore(conn);
        var logicalA = store.EnsureLogicalProject(s.RepoId, "logical_A", "src/A/A.csproj", "net10.0", now: 9);
        var newGen = store.BeginPending(
            OverlayIdentity("delta_edit2"), s.RepoId, s.CommitId, runId: null, now: 9, baseSnapshotId: s.BaseId).id;
        var pANew = new ProjectStore(conn).UpsertSnapshotProject(ProjA, newGen, logicalA, 9);
        store.MapProject(newGen, pANew);
        SeedSymbolWithReference(conn, pANew, "sig_v3");
        store.MarkComplete(newGen, publishedAt: 9);
        store.SetBranchPointer(s.BranchId, newGen, now: 9);

        // An unpinned store re-reads the moved pointer and resolves the NEW generation's project-version...
        Assert.AreEqual(pANew, new ProjectStore(conn).GetByCanonicalId("logical_A")!.Value.id,
            "an unpinned ProjectStore follows the moved branch pointer");

        // ...but a store pinned to the request scope stays on the pinned generation's row (#42).
        Assert.AreEqual(pinnedRowId, new ProjectStore(conn) { Scope = ctx.Scope }.GetByCanonicalId("logical_A")!.Value.id,
            "a ProjectStore pinned to the request scope never splices a mid-request publish (#42)");
        Assert.AreNotEqual(pANew, pinnedRowId, "the pin and the new generation are genuinely different rows");
    }

    // ==== helpers =================================================================================

    private const string RepoUrl = "https://github.com/test/fedctx-repo";

    private sealed record Scenario(long RepoId, long CommitId, long BaseId, long OverlayId, long BranchId);

    private Scenario SeedBaseAndOverlay()
    {
        var conn = _db.GetConnection();
        var store = new SnapshotStore(conn);
        var repoId = store.EnsureRepository(RepoUrl, now: 1);
        var commitId = store.EnsureCommit(repoId, "commit_base", "tree_base", now: 1);
        var logicalA = store.EnsureLogicalProject(repoId, "logical_A", "src/A/A.csproj", "net10.0", now: 1);

        var baseId = store.BeginPending(BaseIdentity(), repoId, commitId, runId: null, now: 1).id;
        var pABase = new ProjectStore(conn).UpsertSnapshotProject(ProjA, baseId, logicalA, 1);
        store.MapProject(baseId, pABase);
        SeedSymbolWithReference(conn, pABase, "sig_v1");
        store.MarkComplete(baseId, publishedAt: 1);

        var overlayId = store.BeginPending(
            OverlayIdentity("delta_edit"), repoId, commitId, runId: null, now: 2, baseSnapshotId: baseId).id;
        var pAOverlay = new ProjectStore(conn).UpsertSnapshotProject(ProjA, overlayId, logicalA, 2);
        store.MapProject(overlayId, pAOverlay);
        SeedSymbolWithReference(conn, pAOverlay, "sig_v2");
        store.MarkComplete(overlayId, publishedAt: 2);

        var branchId = store.EnsureBranch(repoId, "main", isDefault: true, now: 2);
        store.SetBranchPointer(branchId, overlayId, now: 2);

        return new Scenario(repoId, commitId, baseId, overlayId, branchId);
    }

    private static void SeedSymbolWithReference(Microsoft.Data.Sqlite.SqliteConnection conn, long projectId, string sig)
    {
        var symId = new SymbolStore(conn).Insert(new SymbolInfo
        {
            ProjectId = projectId,
            SymbolKey = "K:A.Widget", FullyQualifiedName = "global::A.Widget",
            DisplayName = "Widget", Kind = SymbolKind.Class, Accessibility = Accessibility.Public,
            Signature = sig, FilePath = "src/A/Widget.cs", LineStart = 1, LineEnd = 2, LastIndexedAt = 1
        });
        new ReferenceStore(conn).Insert(new ReferenceInfo
        {
            SymbolId = symId, InProjectId = projectId, FilePath = "src/A/Caller.cs", Line = 5,
            ReferenceKind = ReferenceKind.Invocation
        });
    }

    private static ProjectIdentity ProjA => new()
    {
        CanonicalId = "logical_A",
        GitRemoteUrl = RepoUrl,
        RepoRelativePath = "src/A/A.csproj",
        TargetFramework = "net10.0"
    };

    private static SnapshotIdentity BaseIdentity() => IdentityWith(delta: null, isOverlay: false);
    private static SnapshotIdentity OverlayIdentity(string delta) => IdentityWith(delta, isOverlay: true);

    private static SnapshotIdentity IdentityWith(string? delta, bool isOverlay) => new()
    {
        RepositoryRemoteUrl = RepoUrl,
        CommitSha = "commit_base",
        TreeSha = "tree_base",
        SchemaVersion = IndexDatabase.LatestSchemaVersion,
        AnalyzerVersion = IndexConfigurationHash.AnalyzerVersion,
        ConfigHash = "cfg",
        ToolchainFingerprint = Sextant.Core.ToolchainFingerprint.Current,
        WorkingTreeDelta = delta,
        IsOverlay = isOverlay
    };

    private sealed class DenyingAuthorizer : IReadAuthorizer
    {
        public ReadAuthorization Authorize(SnapshotRow? selected) =>
            ReadAuthorization.Deny("test principal is not authorized");
        public ReadAuthorization AuthorizeRepository(long repositoryId, string remoteUrl) =>
            ReadAuthorization.Deny("test principal is not authorized");
    }
}
