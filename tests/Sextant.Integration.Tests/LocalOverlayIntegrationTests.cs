using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Daemon;
using Sextant.Indexer;
using Sextant.Store;

namespace Sextant.Integration.Tests;

/// <summary>
/// End-to-end Phase-10 coverage over a REAL git repository and a REAL SDK project driven through the
/// real <see cref="LocalOverlayReconciler"/> / <see cref="IndexOrchestrator"/> / <see cref="DaemonHost"/>.
/// Maps the six acceptance criteria and the folded-in issues to concrete assertions:
/// <list type="bullet">
/// <item>Criterion 1 — clean checkout over an exact base ⇒ empty overlay (base re-selected, no rebuild).</item>
/// <item>Criterion 2 — a modified file ⇒ a working-tree-delta overlay is staged and selected.</item>
/// <item>Criterion 3 — an identical dirty tree reconciled again (a "restart") ⇒ the SAME overlay,
/// reconstructed purely from git without any watcher events.</item>
/// <item>Criterion 5 — a dirty tree with no compatible base ⇒ a full local index with an EXPLICIT reason.</item>
/// <item>#43 — a dirty tree is never mis-identified as the clean base commit's snapshot (distinct id +
/// non-null working-tree delta).</item>
/// <item>#44 — the overlay never mutates the published base snapshot's rows (base stays Complete and
/// byte-identical).</item>
/// <item>#28 / #39 — the live daemon re-reads <c>sextant.json</c> on its authoritative pass, and the
/// <c>document_extractor</c> toggle participates in the config hash so flipping it forces a rebuild.</item>
/// </list>
/// </summary>
[TestClass]
[TestCategory("Integration")]
public class LocalOverlayIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public LocalOverlayIntegrationTests()
    {
        _ = IntegrationFixture.Instance; // ensure MSBuildLocator is registered before any Roslyn type loads
        _tempDir = Path.Combine(Path.GetTempPath(), $"sextant_overlay_int_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Sextant.TestSupport.SqliteTestDatabase.DeleteDirectory(_tempDir);

    [TestMethod]
    public async Task CleanBase_ThenOverlay_ThenIdempotentRestart()
    {
        var repo = CreateGitProject("Widget");
        var dbPath = Path.Combine(_tempDir, "index.db");
        using var db = new IndexDatabase(dbPath);
        db.RunMigrations();
        var conn = db.GetConnection();
        var store = new SnapshotStore(conn);

        // First pass over a CLEAN checkout builds the committed base snapshot.
        var first = await ReconcileAsync(db, repo.SolutionPath);
        Assert.AreEqual(OverlayReconcileKind.FullInitial, first.Kind, "the first clean index builds the base");
        var baseId = store.GetSelectedSnapshotId();
        Assert.IsNotNull(baseId, "a base snapshot is selected after the first index");
        var baseFingerprint = SnapshotFingerprint(conn, baseId.Value);
        Assert.IsTrue(SnapshotHasSymbol(conn, baseId.Value, "Widget"), "the base contains the committed type");

        // Criterion 1: a second CLEAN reconcile is an EMPTY overlay — the exact base is re-selected with
        // no new snapshot published.
        var clean2 = await ReconcileAsync(db, repo.SolutionPath);
        Assert.AreEqual(OverlayReconcileKind.CleanBase, clean2.Kind, "a clean tree over an exact base is an empty overlay (criterion 1)");
        Assert.AreEqual(0, clean2.ChangeCount, "a clean tree reports zero changes");
        Assert.AreEqual(baseId, store.GetSelectedSnapshotId(), "the base stays selected on a clean reconcile");
        Assert.AreEqual(1, CountComplete(conn), "no new snapshot is published for a clean tree (criterion 1)");

        // Criterion 2: modify the tracked source file → an overlay is staged and selected.
        File.WriteAllText(repo.SourceFile,
            "namespace App;\npublic class Widget { public int Value() => 1; public int Added() => 2; }\n");
        var dirty = await ReconcileAsync(db, repo.SolutionPath);
        Assert.AreEqual(OverlayReconcileKind.Overlay, dirty.Kind, "a dirty tree over a base stages an overlay (criterion 2)");
        Assert.AreEqual(1, dirty.ChangeCount, "exactly one working-tree change was discovered");

        var overlayId = store.GetSelectedSnapshotId();
        Assert.IsNotNull(overlayId, "the overlay is selected after staging");
        Assert.AreNotEqual(baseId, overlayId, "the overlay is a DISTINCT snapshot from the base (#43)");
        var overlayRow = store.GetById(overlayId.Value)!;
        Assert.IsTrue(overlayRow.IsOverlay, "the selected snapshot is flagged as an overlay");
        Assert.AreEqual(baseId, overlayRow.BaseSnapshotId, "the overlay layers on the exact base (#44)");
        Assert.IsNotNull(overlayRow.WorkingTreeDelta, "the overlay identity folds in the working-tree delta so it is never confused with the clean base (#43)");
        Assert.IsTrue(SnapshotHasSymbol(conn, overlayId.Value, "Added"), "the overlay reflects the edit's new symbol");
        Assert.AreEqual(2, CountComplete(conn), "base + one overlay are both complete");

        // #44: the base snapshot is UNTOUCHED — still Complete, byte-identical, and never gained the edit.
        Assert.AreEqual(SnapshotStatus.Complete, store.GetById(baseId.Value)!.Status, "the base stays Complete after an overlay (#44)");
        Assert.AreEqual(baseFingerprint, SnapshotFingerprint(conn, baseId.Value), "the overlay run never mutated the base snapshot's rows (#44)");
        Assert.IsFalse(SnapshotHasSymbol(conn, baseId.Value, "Added"), "the base never gained the overlay-only symbol (#44)");

        // Criterion 3: reconcile AGAIN from the identical dirty tree (a restart) → the SAME overlay,
        // reconstructed purely from git state, with no new snapshot and no watcher events.
        var restart = await ReconcileAsync(db, repo.SolutionPath);
        Assert.AreEqual(OverlayReconcileKind.Overlay, restart.Kind, "the identical dirty tree still resolves to an overlay (criterion 3)");
        Assert.AreEqual(overlayId, store.GetSelectedSnapshotId(), "an identical dirty tree re-selects the SAME overlay (criterion 3, idempotent)");
        Assert.AreEqual(2, CountComplete(conn), "the restart publishes no duplicate overlay (criterion 3)");
    }

    [TestMethod]
    public async Task DirtyTree_NoBase_FallsBackWithExplicitReason()
    {
        var repo = CreateGitProject("Widget");
        var dbPath = Path.Combine(_tempDir, "index.db");
        using var db = new IndexDatabase(dbPath);
        db.RunMigrations();

        // Make the tree dirty BEFORE any base is indexed: there is no compatible committed base.
        File.WriteAllText(repo.SourceFile, "namespace App;\npublic class Widget { public int Value() => 42; }\n");

        var result = await ReconcileAsync(db, repo.SolutionPath);
        Assert.AreEqual(OverlayReconcileKind.FullFallback, result.Kind, "a dirty tree with no compatible base falls back to a full local index (criterion 5)");
        Assert.IsFalse(string.IsNullOrEmpty(result.FallbackReason), "the fallback records an EXPLICIT reason (criterion 5)");

        var conn = db.GetConnection();
        var store = new SnapshotStore(conn);
        var selected = store.GetSelectedSnapshotId();
        Assert.IsNotNull(selected, "the full local index is selected");
        var row = store.GetById(selected.Value)!;
        Assert.IsFalse(row.IsOverlay, "the fallback is a full local index, not an overlay");
        Assert.IsFalse(string.IsNullOrEmpty(row.FallbackReason), "the published snapshot carries the fallback reason (criterion 5)");
        Assert.IsNotNull(row.WorkingTreeDelta, "the fallback carries the delta so it never claims identity with the clean base commit (#43)");
        Assert.IsTrue(SnapshotHasSymbol(conn, selected.Value, "Widget"), "the fallback fully indexes the dirty working tree");
    }

    [TestMethod]
    public async Task Daemon_LiveConfigEdit_ExtractorToggleForcesRebuild()
    {
        // #28 (daemon re-resolves config on the authoritative pass, not just watcher events) + #39 (the
        // document_extractor toggle participates in the config hash, so flipping it forces a rebuild).
        var repo = CreateGitProject("Widget");
        var configPath = Path.Combine(repo.RepoRoot, "sextant.json");
        WriteConfig(configPath, documentExtractor: true, reconcileIntervalSeconds: 1);

        var dbPath = Path.Combine(_tempDir, "daemon.db");
        using var daemon = new DaemonHost(repo.RepoRoot, dbPath, [repo.SolutionPath], _ => { });
        using var cts = new CancellationTokenSource();
        await daemon.StartAsync(cts.Token);
        try
        {
            var hashOn = await PollAsync(() => ReadSelectedConfigHash(dbPath), h => h != null, TimeSpan.FromSeconds(20));
            Assert.IsNotNull(hashOn, "the daemon publishes a base snapshot with the initial (document_extractor=on) config hash");

            // Flip the LIVE config while the daemon runs. sextant.json is gitignored, so the working tree
            // stays clean from git's view — the rebuild is driven purely by the config-hash change.
            WriteConfig(configPath, documentExtractor: false, reconcileIntervalSeconds: 1);

            var hashOff = await PollAsync(
                () => ReadSelectedConfigHash(dbPath),
                h => h != null && h != hashOn,
                TimeSpan.FromSeconds(40));
            Assert.IsNotNull(hashOff, "the periodic authoritative pass re-read sextant.json and rebuilt (#28)");
            Assert.AreNotEqual(hashOn, hashOff, "flipping document_extractor changes the config hash and forces a rebuild (#39)");
        }
        finally
        {
            await daemon.StopAsync();
        }
    }

    [TestMethod]
    public async Task OverlayReselect_AfterRevertToClean_LeavesBaseComplete()
    {
        // Regression for the overlay re-select path (#44): building a delta, reverting to clean (so the
        // branch rolls back to the base), then RE-APPLYING the identical delta must re-select the existing
        // overlay via overlay-aware supersede semantics — it must NOT supersede the committed base the
        // branch currently points at, which would break every later base lookup and force full fallbacks.
        var repo = CreateGitProject("Widget");
        var dbPath = Path.Combine(_tempDir, "index.db");
        using var db = new IndexDatabase(dbPath);
        db.RunMigrations();
        var conn = db.GetConnection();
        var store = new SnapshotStore(conn);

        const string cleanText = "namespace App;\npublic class Widget { public int Value() => 1; }\n";
        const string dirtyText = "namespace App;\npublic class Widget { public int Value() => 1; public int Added() => 2; }\n";

        // (1) clean → base
        await ReconcileAsync(db, repo.SolutionPath);
        var baseId = store.GetSelectedSnapshotId()!.Value;
        var baseFingerprint = SnapshotFingerprint(conn, baseId);

        // (2) edit X → overlay-X built & selected (branch → overlay-X, base stays Complete)
        File.WriteAllText(repo.SourceFile, dirtyText);
        await ReconcileAsync(db, repo.SolutionPath);
        var overlayId = store.GetSelectedSnapshotId()!.Value;
        Assert.AreNotEqual(baseId, overlayId, "the overlay is distinct from the base");
        Assert.IsTrue(store.GetById(overlayId)!.IsOverlay, "the selected snapshot is an overlay");

        // (3) revert to clean → clean reconcile re-selects the base (branch → base), superseding overlay-X
        File.WriteAllText(repo.SourceFile, cleanText);
        var reverted = await ReconcileAsync(db, repo.SolutionPath);
        Assert.AreEqual(OverlayReconcileKind.CleanBase, reverted.Kind, "reverting to clean re-selects the base");
        Assert.AreEqual(baseId, store.GetSelectedSnapshotId(), "the branch points back at the base after revert");

        // (4) re-apply the IDENTICAL edit → the existing (now Superseded) overlay-X is re-selected. The
        // branch currently points at the BASE, so the fix must NOT supersede it.
        File.WriteAllText(repo.SourceFile, dirtyText);
        var reapplied = await ReconcileAsync(db, repo.SolutionPath);
        Assert.AreEqual(OverlayReconcileKind.Overlay, reapplied.Kind, "re-applying the identical delta resolves to an overlay");
        Assert.AreEqual(overlayId, store.GetSelectedSnapshotId(), "the SAME overlay is re-selected without rebuilding");

        // The committed base must survive the overlay re-select — Complete, byte-identical, unsuperseded.
        Assert.AreEqual(SnapshotStatus.Complete, store.GetById(baseId)!.Status, "the base stays Complete after an overlay re-select (#44)");
        Assert.AreEqual(baseFingerprint, SnapshotFingerprint(conn, baseId), "the base snapshot's rows are never mutated (#44)");

        // Downstream proof: a DIFFERENT dirty edit still resolves to an overlay (the base remains a
        // compatible base), rather than falling back to a full local index.
        File.WriteAllText(repo.SourceFile,
            "namespace App;\npublic class Widget { public int Value() => 1; public int Other() => 3; }\n");
        var different = await ReconcileAsync(db, repo.SolutionPath);
        Assert.AreEqual(OverlayReconcileKind.Overlay, different.Kind, "the base is still resolvable as a compatible base after the re-select (#44)");
    }

    [TestMethod]
    public async Task MultiProject_SuccessiveOverlays_ShareUnchangedAndPreserveEarlierDelta()
    {
        // Two INDEPENDENT projects (no project reference ⇒ separate closures). Exercises two overlay
        // correctness invariants that only surface with more than one project:
        //   • An overlay SHARES the base's unchanged project rows (whose physical snapshot_id stays the
        //     BASE) via snapshot_projects. Project-scoped reads (ProjectStore.GetByCanonicalId/GetAll,
        //     which back the whole MCP surface via BuildCanonicalIdCache) must resolve membership through
        //     snapshot_projects, else a shared project vanishes under an overlay.
        //   • Successive overlays touching DIFFERENT projects must each represent the full working-tree
        //     delta versus the base. A project dirty-versus-base but unchanged since the previous overlay
        //     must be re-extracted (its owning file is still git-dirty), never re-shared as the base's
        //     clean version — otherwise the earlier overlay's edit is silently dropped (issue #44).
        var repo = CreateMultiProjectGitRepo();
        var dbPath = Path.Combine(_tempDir, "index.db");
        using var db = new IndexDatabase(dbPath);
        db.RunMigrations();
        var conn = db.GetConnection();
        var store = new SnapshotStore(conn);

        // Clean base over both projects.
        await ReconcileAsync(db, repo.SolutionPath);
        var baseId = store.GetSelectedSnapshotId()!.Value;
        var baseFingerprint = SnapshotFingerprint(conn, baseId);
        Assert.IsTrue(SnapshotHasSymbol(conn, baseId, "Alpha"), "the base contains project A's type");
        Assert.IsTrue(SnapshotHasSymbol(conn, baseId, "Beta"), "the base contains project B's type");
        Assert.AreEqual(2, ProjectCanonicalIds(conn).Count, "the base exposes both projects");
        var betaCanonicalId = FindCanonicalId(conn, "/B/");

        // Overlay 1: edit ONLY project A.
        File.WriteAllText(repo.SourceA,
            "namespace App;\npublic class Alpha { public int Value() => 1; public int AlphaAdded() => 2; }\n");
        await ReconcileAsync(db, repo.SolutionPath);
        var overlay1 = store.GetSelectedSnapshotId()!.Value;
        Assert.AreNotEqual(baseId, overlay1, "editing A stages a distinct overlay");
        Assert.IsTrue(SnapshotHasSymbol(conn, overlay1, "AlphaAdded"), "overlay 1 reflects A's edit");

        // Finding 3: project B is UNCHANGED and SHARED from the base (its row keeps the base's snapshot_id),
        // but must still be visible through the project-scoped read path under the selected overlay.
        Assert.IsTrue(SnapshotHasSymbol(conn, overlay1, "Beta"), "the shared, unchanged project B is still in the overlay");
        Assert.AreEqual(2, ProjectCanonicalIds(conn).Count,
            "ProjectStore.GetAll enumerates BOTH the re-extracted and the shared project under an overlay (finding 3)");
        Assert.IsTrue(ProjectResolvableByCanonicalId(conn, betaCanonicalId),
            "the shared project B is resolvable by canonical id under an overlay (finding 3)");

        // The base is untouched by overlay 1 (#44).
        Assert.AreEqual(baseFingerprint, SnapshotFingerprint(conn, baseId), "overlay 1 never mutated the base (#44)");

        // Overlay 2: edit project B while A is STILL dirty (uncommitted).
        File.WriteAllText(repo.SourceB,
            "namespace App;\npublic class Beta { public int Value() => 1; public int BetaAdded() => 2; }\n");
        await ReconcileAsync(db, repo.SolutionPath);
        var overlay2 = store.GetSelectedSnapshotId()!.Value;
        Assert.AreNotEqual(overlay1, overlay2, "the new combined delta stages a fresh overlay");

        // Finding 1: overlay 2 must carry BOTH deltas. B's new edit AND A's earlier (still-dirty) edit —
        // A must NOT have been re-shared as the base's clean version just because it was unchanged since
        // overlay 1.
        Assert.IsTrue(SnapshotHasSymbol(conn, overlay2, "BetaAdded"), "overlay 2 reflects B's edit");
        Assert.IsTrue(SnapshotHasSymbol(conn, overlay2, "AlphaAdded"),
            "overlay 2 STILL carries A's earlier dirty edit — a project dirty-versus-base is re-extracted, never re-shared as clean (finding 1)");
        Assert.IsFalse(SnapshotHasSymbol(conn, overlay2, "Alpha") && !SnapshotHasSymbol(conn, overlay2, "AlphaAdded"),
            "A is not silently reverted to its clean base version");
        Assert.AreEqual(2, ProjectCanonicalIds(conn).Count, "both projects remain visible under overlay 2 (finding 3)");

        // The base is STILL untouched after two overlays (#44).
        Assert.AreEqual(SnapshotStatus.Complete, store.GetById(baseId)!.Status, "the base stays Complete after two overlays (#44)");
        Assert.AreEqual(baseFingerprint, SnapshotFingerprint(conn, baseId), "two overlays never mutated the base (#44)");
    }

    // ---- issue #49: pin the reconcile pass to one git state --------------------------------------

    /// <summary>
    /// Criterion 1: a HEAD/working-tree move BETWEEN the start-of-pass pin capture and the pre-publish
    /// re-verification is detected and the pass aborts WITHOUT publishing a mixed-state snapshot. Injects
    /// the move via a scripted probe whose second capture (the orchestrator's re-verify) reports a moved
    /// HEAD.
    /// </summary>
    [TestMethod]
    public async Task MidPassGitMove_AbortsWithoutPublishing()
    {
        var repo = CreateGitProject("Widget");
        var dbPath = Path.Combine(_tempDir, "index.db");
        using var db = new IndexDatabase(dbPath);
        db.RunMigrations();
        var conn = db.GetConnection();
        var store = new SnapshotStore(conn);

        // Build the committed base over the clean checkout with the real probe.
        await ReconcileAsync(db, repo.SolutionPath);
        var baseId = store.GetSelectedSnapshotId();
        Assert.IsNotNull(baseId, "a base snapshot is selected after the first index");
        var completeBefore = CountComplete(conn);

        // Dirty the tree so the pass takes the overlay path, then reconcile with a probe that reports a
        // move on the pre-publish re-verify (call 2): baseline (call 1) is the real state.
        File.WriteAllText(repo.SourceFile,
            "namespace App;\npublic class Widget { public int Value() => 1; public int Added() => 2; }\n");
        var probe = new ScriptedGitStateProbe(moveOnCall: 2);
        var result = await ReconcileWithProbeAsync(db, repo.SolutionPath, probe);

        Assert.AreEqual(OverlayReconcileKind.Aborted, result.Kind, "a mid-pass move aborts the pass (criterion 1)");
        Assert.IsTrue(probe.Calls >= 2, "the guard captured a baseline and re-verified before publish");
        Assert.AreEqual(baseId, store.GetSelectedSnapshotId(), "the aborted pass published nothing — the base stays selected");
        Assert.AreEqual(completeBefore, CountComplete(conn), "no overlay/partial snapshot was published on abort (criterion 1)");
        Assert.IsFalse(SnapshotHasSymbol(conn, baseId.Value, "Added"), "the base never gained the un-published edit");
    }

    /// <summary>
    /// Criterion 2: after an aborted pass leaves nothing published, a subsequent pass over the now-stable
    /// tree publishes the correct single-state overlay (self-heal / bounded retry).
    /// </summary>
    [TestMethod]
    public async Task AbortedPass_ThenStablePass_PublishesCorrectSnapshot()
    {
        var repo = CreateGitProject("Widget");
        var dbPath = Path.Combine(_tempDir, "index.db");
        using var db = new IndexDatabase(dbPath);
        db.RunMigrations();
        var conn = db.GetConnection();
        var store = new SnapshotStore(conn);

        await ReconcileAsync(db, repo.SolutionPath);
        var baseId = store.GetSelectedSnapshotId()!.Value;
        var baseFingerprint = SnapshotFingerprint(conn, baseId);

        File.WriteAllText(repo.SourceFile,
            "namespace App;\npublic class Widget { public int Value() => 1; public int Added() => 2; }\n");

        // First pass aborts mid-flight — nothing published, base untouched.
        var aborted = await ReconcileWithProbeAsync(db, repo.SolutionPath, new ScriptedGitStateProbe(moveOnCall: 2));
        Assert.AreEqual(OverlayReconcileKind.Aborted, aborted.Kind, "the first pass aborts");
        Assert.AreEqual(1, CountComplete(conn), "the aborted pass left no partial/overlay generation behind (criterion 2)");
        Assert.AreEqual(baseFingerprint, SnapshotFingerprint(conn, baseId), "the aborted pass never mutated the base");

        // Self-heal: a stable pass (real probe) now publishes the correct overlay.
        var healed = await ReconcileAsync(db, repo.SolutionPath);
        Assert.AreEqual(OverlayReconcileKind.Overlay, healed.Kind, "the stable pass publishes the overlay (criterion 2)");
        var overlayId = store.GetSelectedSnapshotId()!.Value;
        Assert.AreNotEqual(baseId, overlayId, "the overlay is a distinct published snapshot");
        Assert.AreEqual(2, CountComplete(conn), "exactly base + one overlay after self-heal — no orphan generations");
        Assert.IsTrue(SnapshotHasSymbol(conn, overlayId, "Added"), "the healed overlay reflects the edit");
        Assert.AreEqual(baseFingerprint, SnapshotFingerprint(conn, baseId), "the base stays byte-identical through abort + heal");
    }

    /// <summary>
    /// Criterion 3: on a stable tree the guard is INERT — a probe that never reports a move publishes the
    /// overlay exactly as the real path (same distinct snapshot, folded delta, and edit), so a stable pass
    /// is byte-identical to pre-fix behavior.
    /// </summary>
    [TestMethod]
    public async Task StableTree_GuardIsInert_PublishesNormally()
    {
        var repo = CreateGitProject("Widget");
        var dbPath = Path.Combine(_tempDir, "index.db");
        using var db = new IndexDatabase(dbPath);
        db.RunMigrations();
        var conn = db.GetConnection();
        var store = new SnapshotStore(conn);

        await ReconcileAsync(db, repo.SolutionPath);
        var baseId = store.GetSelectedSnapshotId()!.Value;

        File.WriteAllText(repo.SourceFile,
            "namespace App;\npublic class Widget { public int Value() => 1; public int Added() => 2; }\n");

        var probe = new ScriptedGitStateProbe(moveOnCall: 0); // never moves
        var result = await ReconcileWithProbeAsync(db, repo.SolutionPath, probe);

        Assert.AreEqual(OverlayReconcileKind.Overlay, result.Kind, "a stable tree still publishes an overlay (guard inert)");
        Assert.IsTrue(probe.Calls >= 2, "the guard ran (captured baseline + re-verified) yet did not interfere");
        var overlayId = store.GetSelectedSnapshotId()!.Value;
        Assert.AreNotEqual(baseId, overlayId, "the overlay is a distinct snapshot from the base");
        var overlayRow = store.GetById(overlayId)!;
        Assert.IsTrue(overlayRow.IsOverlay, "the published snapshot is an overlay");
        Assert.IsNotNull(overlayRow.WorkingTreeDelta, "the overlay identity folds in the working-tree delta (no new guard component)");
        Assert.AreEqual(baseId, overlayRow.BaseSnapshotId, "the overlay layers on the exact base");
        Assert.IsTrue(SnapshotHasSymbol(conn, overlayId, "Added"), "the overlay reflects the edit");
        Assert.AreEqual(2, CountComplete(conn), "base + one overlay, exactly as the real path");
    }

    /// <summary>
    /// A test <see cref="IGitStateProbe"/> that returns the REAL git pin except that, starting with its
    /// <c>moveOnCall</c>-th capture (1-based), it reports a different HEAD to simulate a mid-pass move.
    /// <c>moveOnCall == 0</c> never moves (delegates fully to the real probe).
    /// </summary>
    private sealed class ScriptedGitStateProbe(int moveOnCall) : IGitStateProbe
    {
        private int _calls;

        public int Calls => _calls;

        public GitStatePin? Capture(string repoRoot)
        {
            _calls++;
            var real = GitStateProbe.Default.Capture(repoRoot);
            if (real == null || moveOnCall == 0 || _calls < moveOnCall)
                return real;
            return real with { Head = real.Head + "-moved" };
        }
    }

    private static async Task<OverlayReconcileResult> ReconcileWithProbeAsync(
        IndexDatabase db, string solutionPath, IGitStateProbe probe)
    {
        var solution = await SolutionLoader.LoadSolutionAsync(solutionPath);
        var reconciler = new LocalOverlayReconciler(
            db, log: null, useDocumentExtractor: true,
            parallelism: ExtractionParallelismOptions.Default,
            profile: IndexProfileDescriptor.For(IndexProfiles.Standard),
            gitStateProbe: probe);
        return await reconciler.ReconcileAsync(solution);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private sealed record GitProject(string RepoRoot, string SolutionPath, string SourceFile);

    private GitProject CreateGitProject(string className)
    {
        var repoRoot = Path.Combine(_tempDir, "repo");
        var projDir = Path.Combine(repoRoot, "App");
        Directory.CreateDirectory(projDir);

        // Keep the working tree clean after `dotnet restore`: ignore build output, the DB, and the
        // gitignored runtime config used by the daemon test.
        File.WriteAllText(Path.Combine(repoRoot, ".gitignore"), "bin/\nobj/\n.sextant/\nsextant.json\n*.db\n*.db-wal\n*.db-shm\n");
        File.WriteAllText(Path.Combine(projDir, "App.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
              </PropertyGroup>
            </Project>
            """);
        var sourceFile = Path.Combine(projDir, "Class1.cs");
        File.WriteAllText(sourceFile, $"namespace App;\npublic class {className} {{ public int Value() => 1; }}\n");
        var slnPath = Path.Combine(repoRoot, "App.slnx");
        File.WriteAllText(slnPath, "<Solution>\n  <Project Path=\"App/App.csproj\" />\n</Solution>\n");

        Git(repoRoot, "init -b main");
        Git(repoRoot, "config user.email test@example.com");
        Git(repoRoot, "config user.name Test");
        Git(repoRoot, "config commit.gpgsign false");
        Git(repoRoot, "add -A");
        Git(repoRoot, "commit -m initial");
        RestoreSolution(slnPath);

        return new GitProject(repoRoot, slnPath, sourceFile);
    }

    private sealed record MultiGitProject(string RepoRoot, string SolutionPath, string SourceA, string SourceB);

    /// <summary>Two INDEPENDENT SDK projects (A and B, no project reference) committed to one git repo.</summary>
    private MultiGitProject CreateMultiProjectGitRepo()
    {
        var repoRoot = Path.Combine(_tempDir, "multi");
        Directory.CreateDirectory(repoRoot);
        File.WriteAllText(Path.Combine(repoRoot, ".gitignore"), "bin/\nobj/\n.sextant/\nsextant.json\n*.db\n*.db-wal\n*.db-shm\n");

        static void WriteProject(string projDir, string name, string className)
        {
            Directory.CreateDirectory(projDir);
            File.WriteAllText(Path.Combine(projDir, $"{name}.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(projDir, $"{className}.cs"),
                $"namespace App;\npublic class {className} {{ public int Value() => 1; }}\n");
        }

        var aDir = Path.Combine(repoRoot, "A");
        var bDir = Path.Combine(repoRoot, "B");
        WriteProject(aDir, "A", "Alpha");
        WriteProject(bDir, "B", "Beta");
        var slnPath = Path.Combine(repoRoot, "App.slnx");
        File.WriteAllText(slnPath,
            "<Solution>\n  <Project Path=\"A/A.csproj\" />\n  <Project Path=\"B/B.csproj\" />\n</Solution>\n");

        Git(repoRoot, "init -b main");
        Git(repoRoot, "config user.email test@example.com");
        Git(repoRoot, "config user.name Test");
        Git(repoRoot, "config commit.gpgsign false");
        Git(repoRoot, "add -A");
        Git(repoRoot, "commit -m initial");
        RestoreSolution(slnPath);

        return new MultiGitProject(repoRoot, slnPath, Path.Combine(aDir, "Alpha.cs"), Path.Combine(bDir, "Beta.cs"));
    }

    /// <summary>Canonical ids visible through the project-scoped read path under the CURRENTLY selected snapshot.</summary>
    private static List<string> ProjectCanonicalIds(SqliteConnection conn) =>
        new ProjectStore(conn).GetAll().Select(p => p.project.CanonicalId).ToList();

    private static bool ProjectResolvableByCanonicalId(SqliteConnection conn, string canonicalId) =>
        new ProjectStore(conn).GetByCanonicalId(canonicalId) != null;

    /// <summary>The canonical id of the enumerable project whose repo-relative path contains <paramref name="fragment"/>.</summary>
    private static string FindCanonicalId(SqliteConnection conn, string fragment) =>
        new ProjectStore(conn).GetAll()
            .Select(p => p.project)
            .First(p => ("/" + p.RepoRelativePath.Replace('\\', '/')).Contains(fragment, StringComparison.Ordinal))
            .CanonicalId;

    private async Task<OverlayReconcileResult> ReconcileAsync(IndexDatabase db, string solutionPath)
    {
        // Load a FRESH solution each pass (a Roslyn Solution is an immutable snapshot; a restart / periodic
        // pass in production re-loads too). Reloading also guarantees the #35 analyzed-hash reflects the
        // current on-disk bytes.
        var solution = await SolutionLoader.LoadSolutionAsync(solutionPath);
        var reconciler = new LocalOverlayReconciler(
            db, log: null, useDocumentExtractor: true,
            parallelism: ExtractionParallelismOptions.Default,
            profile: IndexProfileDescriptor.For(IndexProfiles.Standard));
        return await reconciler.ReconcileAsync(solution);
    }

    private static void WriteConfig(string path, bool documentExtractor, int reconcileIntervalSeconds) =>
        File.WriteAllText(path,
            $$"""
            {
              "document_extractor": {{(documentExtractor ? "true" : "false")}},
              "reconcile_interval_seconds": {{reconcileIntervalSeconds}}
            }
            """);

    private static string? ReadSelectedConfigHash(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.config_hash
            FROM branches b
            JOIN snapshots s ON s.id = b.snapshot_id
            WHERE b.is_default = 1 AND s.status = 'complete'
              AND (SELECT COUNT(*) FROM repositories) = 1
            ORDER BY b.updated_at DESC, b.id DESC
            LIMIT 1;
            """;
        return cmd.ExecuteScalar() as string;
    }

    private static async Task<T?> PollAsync<T>(Func<T?> read, Func<T?, bool> done, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            T? value = default;
            try { value = read(); } catch { /* DB may be mid-write; retry */ }
            if (done(value)) return value;
            await Task.Delay(500);
        }
        return default;
    }

    private static int CountComplete(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM snapshots WHERE status = 'complete';";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static bool SnapshotHasSymbol(SqliteConnection conn, long snapshotId, string displayName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM symbols s
            JOIN snapshot_projects sp ON sp.project_id = s.project_id
            WHERE sp.snapshot_id = @sid AND s.display_name = @n;
            """;
        cmd.Parameters.AddWithValue("@sid", snapshotId);
        cmd.Parameters.AddWithValue("@n", displayName);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>
    /// A stable fingerprint of a snapshot's semantic rows (its mapped project ids + every symbol's
    /// display name). If an overlay run wrongly mutated the base snapshot's rows this would change (#44).
    /// </summary>
    private static string SnapshotFingerprint(SqliteConnection conn, long snapshotId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.project_id, s.display_name
            FROM symbols s
            JOIN snapshot_projects sp ON sp.project_id = s.project_id
            WHERE sp.snapshot_id = @sid
            ORDER BY s.project_id, s.display_name, s.id;
            """;
        cmd.Parameters.AddWithValue("@sid", snapshotId);
        var sb = new System.Text.StringBuilder();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            sb.Append(reader.GetInt64(0)).Append('|').Append(reader.GetString(1)).Append('\n');
        return sb.ToString();
    }

    private static void Git(string repoRoot, string args)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("git not found");
        var stderr = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            Assert.Inconclusive($"git {args} failed (exit {process.ExitCode}): {stderr}");
    }

    private static void RestoreSolution(string solutionPath)
    {
        var psi = new ProcessStartInfo("dotnet", $"restore \"{solutionPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            Assert.Inconclusive($"restore of the generated solution failed (exit {process.ExitCode}): {stderr}");
    }
}
