using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Store.Tests;

/// <summary>
/// Phase 11 — federated overlay/base read shadowing, store-level guarantees (criteria 1, 2, 5). These
/// deterministic tests seed a Phase-9 committed base snapshot and a Phase-10 overlay generation that
/// re-extracts one project (A) while SHARING an unchanged project (B), then assert the read scopes the
/// federated planner (<see cref="FederatedReadContext"/>, in the MCP layer) produces — expressed here as
/// the underlying <see cref="SnapshotReadScope"/> — never resurrect a base row for a file the overlay
/// touched, deleted, or renamed away from:
/// <list type="bullet">
///   <item><b>Criterion 1</b> — no base occurrence from a touched/deleted/renamed file appears under the
///   federated (overlay) scope, including the rename = delete+add case;</item>
///   <item><b>Criterion 2</b> — a changed definition in the overlay shadows the base definition
///   deterministically;</item>
///   <item><b>Criterion 5</b> — the federated result for each logical project equals exactly one
///   partition (overlay-only for a touched project, base-only for a shared project), so paging/grouping
///   is independent of the local/base partition.</item>
/// </list>
/// The full end-to-end federation (through the MCP tools + provenance meta) is covered by the MCP-layer
/// tests; these are the fast, git-free, Roslyn-free backstops for the shadowing invariant itself.
/// </summary>
[TestClass]
public class FederatedShadowingTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SqliteConnection _conn = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_fed_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _conn = _db.GetConnection();
    }

    [TestCleanup]
    public void TestCleanup() => SqliteTestDatabase.Delete(_dbPath, _db);

    // ==== Criterion 1: no base row from a touched / deleted / renamed file survives federation =====

    [TestMethod]
    public void FederatedScope_ExcludesBaseOccurrences_FromTouchedDeletedAndRenamedFiles()
    {
        var s = SeedBaseAndOverlay();

        // The federated view is the selected overlay generation's full membership (fresh A + shared B).
        var federated = Occurrences(new SnapshotReadScope(s.OverlayId));

        // Not a single occurrence may belong to the base's re-extracted project version A: every one of
        // its files (the touched Widget.cs, the deleted Gone.cs, the renamed-away OldName.cs) is shadowed.
        Assert.IsFalse(federated.Any(o => o.InProjectId == s.BaseProjectA),
            "criterion 1: no base occurrence from a touched/deleted/renamed file may appear federated");

        var federatedFilesA = federated.Where(o => o.InProjectId == s.OverlayProjectA)
            .Select(o => o.FilePath).OrderBy(p => p).ToList();
        CollectionAssert.AreEqual(
            new[] { "src/A/NewName.cs", "src/A/Widget.cs" }, federatedFilesA,
            "the overlay re-extraction of A exposes the touched file and the rename ADD, never the deleted " +
            "file or the rename's OLD path");

        Assert.IsFalse(federatedFilesA.Contains("src/A/Gone.cs"), "a deleted file's base row is never resurrected");
        Assert.IsFalse(federatedFilesA.Contains("src/A/OldName.cs"), "a rename's old path is delete-shadowed");

        // The unchanged project B is SHARED from the base, so its occurrence is federated in unchanged.
        Assert.IsTrue(federated.Any(o => o.InProjectId == s.SharedProjectB && o.FilePath == "src/B/Helper.cs"),
            "an unchanged shared project's base occurrences remain visible through federation");
    }

    [TestMethod]
    public void BaseOnlyScope_StillContainsTheProvablyShadowedRows()
    {
        var s = SeedBaseAndOverlay();

        // Differential proof: the base snapshot itself is byte-unchanged — a base-only read STILL returns
        // the touched/deleted/renamed rows. They are shadowed by federation, not deleted from the base.
        var baseOnly = Occurrences(SnapshotReadScope.ForBaseOf(s.BaseId))
            .Where(o => o.InProjectId == s.BaseProjectA)
            .Select(o => o.FilePath).OrderBy(p => p).ToList();

        CollectionAssert.AreEqual(
            new[] { "src/A/Gone.cs", "src/A/OldName.cs", "src/A/Widget.cs" }, baseOnly,
            "the committed base is never mutated by an overlay (criterion 1 differential; Phase-10 #44)");
    }

    // ==== Criterion 2: a changed definition shadows the base definition deterministically ==========

    [TestMethod]
    public void FederatedScope_ChangedDefinition_ShadowsBaseDefinition()
    {
        var s = SeedBaseAndOverlay();

        var federated = new SymbolStore(_conn) { Scope = new SnapshotReadScope(s.OverlayId) }
            .ResolveByFqn("global::A.Widget");
        Assert.AreEqual(1, federated.Count, "the changed definition resolves to exactly one row (no base shadow leak)");
        Assert.AreEqual(s.OverlayProjectA, federated[0].ProjectId, "federation resolves the overlay's definition");
        Assert.AreEqual("sig_v2", federated[0].Signature, "the overlay's CHANGED signature shadows the base's");

        var baseOnly = new SymbolStore(_conn) { Scope = SnapshotReadScope.ForBaseOf(s.BaseId) }
            .ResolveByFqn("global::A.Widget");
        Assert.AreEqual(1, baseOnly.Count);
        Assert.AreEqual("sig_v1", baseOnly[0].Signature,
            "the base still holds its own definition — the overlay shadows, it does not overwrite");
    }

    // ==== Criterion 5: the federated result for a logical project equals exactly ONE partition ======

    [TestMethod]
    public void Partitions_AreDisjointAndReconstructTheFederatedResult()
    {
        var s = SeedBaseAndOverlay();

        var federated = Occurrences(new SnapshotReadScope(s.OverlayId));
        var overlayOnly = Occurrences(SnapshotReadScope.ForOverlayLocalOnly(s.OverlayId));
        var baseOnly = Occurrences(SnapshotReadScope.ForBaseOf(s.BaseId));

        // Overlay-only is exactly the freshly re-extracted project A (never the shared B).
        Assert.IsTrue(overlayOnly.All(o => o.InProjectId == s.OverlayProjectA),
            "overlay-only is precisely the working-tree delta (fresh project versions only)");
        Assert.IsTrue(overlayOnly.Count > 0);

        // For a TOUCHED project, the federated rows come from the overlay partition, byte-for-byte.
        var fedA = Signature(federated.Where(o => o.InProjectId == s.OverlayProjectA));
        Assert.AreEqual(Signature(overlayOnly), fedA,
            "criterion 5: a touched project's federated rows equal its overlay-only partition, independent of partition");

        // For a SHARED project, the federated rows come from the base partition, byte-for-byte.
        var fedB = Signature(federated.Where(o => o.InProjectId == s.SharedProjectB));
        var baseB = Signature(baseOnly.Where(o => o.InProjectId == s.SharedProjectB));
        Assert.AreEqual(baseB, fedB,
            "criterion 5: a shared project's federated rows equal its base-only partition");

        // The two partitions that feed the federated result are disjoint by project version.
        Assert.IsFalse(overlayOnly.Select(o => o.InProjectId)
                .Intersect(baseOnly.Select(o => o.InProjectId)).Any(),
            "overlay-only and base-only partition the federated result with no overlap");
    }

    // ==== helpers =================================================================================

    private const string RepoUrl = "https://github.com/org/fed-repo";

    private sealed record Scenario(
        long BaseId, long OverlayId, long BaseProjectA, long OverlayProjectA, long SharedProjectB);

    /// <summary>
    /// Seeds a committed base (projects A + B, A has occurrences in a touched, a to-be-deleted, and a
    /// to-be-renamed file) and an overlay that re-extracts A (touched file kept, deleted file dropped,
    /// rename = old path dropped + new path added) while SHARING B. Points the default branch at the
    /// overlay so it is the selected generation.
    /// </summary>
    private Scenario SeedBaseAndOverlay()
    {
        var store = new SnapshotStore(_conn);
        var repoId = store.EnsureRepository(RepoUrl, now: 1);
        var commitId = store.EnsureCommit(repoId, "commit_base", "tree_base", now: 1);
        var logicalA = store.EnsureLogicalProject(repoId, "logical_A", "src/A/A.csproj", "net10.0", now: 1);
        var logicalB = store.EnsureLogicalProject(repoId, "logical_B", "src/B/B.csproj", "net10.0", now: 1);

        // --- base snapshot ---
        var baseId = store.BeginPending(Identity(delta: null), repoId, commitId, runId: null, now: 1).id;
        var pA_base = new ProjectStore(_conn).UpsertSnapshotProject(ProjA, baseId, logicalA, 1);
        var pB = new ProjectStore(_conn).UpsertSnapshotProject(ProjB, baseId, logicalB, 1);
        store.MapProject(baseId, pA_base);
        store.MapProject(baseId, pB);
        var baseASym = InsertSymbol(pA_base, "K:A.Widget", "global::A.Widget", "sig_v1");
        var bSym = InsertSymbol(pB, "K:B.Helper", "global::B.Helper", "sig_b");
        InsertOccurrence(pA_base, baseASym, "src/A/Widget.cs", 10);   // touched file
        InsertOccurrence(pA_base, baseASym, "src/A/Gone.cs", 20);     // deleted file
        InsertOccurrence(pA_base, baseASym, "src/A/OldName.cs", 30);  // rename: old path
        InsertOccurrence(pB, bSym, "src/B/Helper.cs", 40);
        store.MarkComplete(baseId, publishedAt: 1);

        // --- overlay snapshot (re-extract A, share B) ---
        var overlayId = store.BeginPending(
            Identity(delta: "delta_edit"), repoId, commitId, runId: null, now: 2, baseSnapshotId: baseId).id;
        var pA_overlay = new ProjectStore(_conn).UpsertSnapshotProject(ProjA, overlayId, logicalA, 2);
        store.MapProject(overlayId, pA_overlay);
        store.MapProject(overlayId, pB); // SHARE the unchanged base project version B
        var overlayASym = InsertSymbol(pA_overlay, "K:A.Widget", "global::A.Widget", "sig_v2"); // changed def
        InsertOccurrence(pA_overlay, overlayASym, "src/A/Widget.cs", 11); // touched file re-extracted
        InsertOccurrence(pA_overlay, overlayASym, "src/A/NewName.cs", 12); // rename: new path (add)
        store.MarkComplete(overlayId, publishedAt: 2);

        var branchId = store.EnsureBranch(repoId, "main", isDefault: true, now: 2);
        store.SetBranchPointer(branchId, overlayId, now: 2);

        return new Scenario(baseId, overlayId, pA_base, pA_overlay, pB);
    }

    private static ProjectIdentity ProjA => new()
    {
        CanonicalId = "logical_A",
        GitRemoteUrl = RepoUrl,
        RepoRelativePath = "src/A/A.csproj",
        TargetFramework = "net10.0"
    };

    private static ProjectIdentity ProjB => new()
    {
        CanonicalId = "logical_B",
        GitRemoteUrl = RepoUrl,
        RepoRelativePath = "src/B/B.csproj",
        TargetFramework = "net10.0"
    };

    private static SnapshotIdentity Identity(string? delta) => new()
    {
        RepositoryRemoteUrl = RepoUrl,
        CommitSha = "commit_base",
        TreeSha = "tree_base",
        SchemaVersion = IndexDatabase.LatestSchemaVersion,
        AnalyzerVersion = IndexConfigurationHash.AnalyzerVersion,
        ConfigHash = "cfg",
        ToolchainFingerprint = "tc",
        WorkingTreeDelta = delta,
        IsOverlay = delta != null
    };

    private List<ReferenceInfo> Occurrences(SnapshotReadScope scope)
    {
        var store = new ReferenceStore(_conn) { Scope = scope };
        // Union the occurrences of every project version this seed touches; the scope decides which survive.
        var all = new List<ReferenceInfo>();
        foreach (var pid in AllProjectIds())
            all.AddRange(store.GetByProject(pid));
        return all;
    }

    private List<long> AllProjectIds()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM projects ORDER BY id;";
        var ids = new List<long>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        return ids;
    }

    private static string Signature(IEnumerable<ReferenceInfo> refs) =>
        string.Join("|", refs.Select(r => $"{r.InProjectId}:{r.FilePath}:{r.Line}").OrderBy(x => x));

    private long InsertSymbol(long projectId, string symbolKey, string fqn, string signature)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO symbols
                (project_id, symbol_key, fully_qualified_name, display_name, kind, accessibility,
                 signature, line_start, line_end, last_indexed_at)
            VALUES (@p, @k, @fqn, @dn, 0, 0, @sig, 1, 1, 1)
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("@p", projectId);
        cmd.Parameters.AddWithValue("@k", symbolKey);
        cmd.Parameters.AddWithValue("@fqn", fqn);
        cmd.Parameters.AddWithValue("@dn", fqn.Split('.').Last());
        cmd.Parameters.AddWithValue("@sig", signature);
        return (long)cmd.ExecuteScalar()!;
    }

    private void InsertOccurrence(long projectId, long targetSymbolId, string repoRelativePath, int line)
    {
        var store = new ReferenceStore(_conn);
        store.Insert(new ReferenceInfo
        {
            SymbolId = targetSymbolId,
            InProjectId = projectId,
            FilePath = repoRelativePath,
            Line = line,
            ReferenceKind = ReferenceKind.Invocation,
            LastIndexedAt = 1
        });
    }
}
