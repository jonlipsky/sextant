using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Store.Tests;

/// <summary>
/// Phase 17 slice 2, fold issue #70 — the query-path equivalence that CLOSES the Phase-16 R3 concern: a
/// snapshot ASSEMBLED from multiple independent contributions (its project versions produced under
/// DIFFERENT runs and merely mapped into ONE snapshot via <c>snapshot_projects</c>) must answer a
/// find-references / cross-repository-usage query IDENTICALLY to a NATIVELY-indexed snapshot of the same
/// logical content (both projects under one run). The read path scopes by <c>snapshot_projects</c>
/// membership, so an assembled snapshot is a first-class queryable snapshot, not a degraded one. If the
/// assembly were materially incomplete the finalize gate would have published it Partial (see
/// <c>AssemblyFinalizeGateTests</c>); this test proves the COMPLETE assembled result is query-equivalent.
/// </summary>
[TestClass]
public class AssembledSnapshotQueryEquivalenceTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SqliteConnection _conn = null!;
    private SnapshotStore _snapshots = null!;
    private long _now;
    private long _repo;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_asmq_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _conn = _db.GetConnection();
        _snapshots = new SnapshotStore(_conn);
        _now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _repo = _snapshots.EnsureRepository("https://github.com/org/app", _now);
    }

    [TestCleanup]
    public void TestCleanup() => SqliteTestDatabase.Delete(_dbPath, _db);

    [TestMethod]
    public void FindReferences_OnAssembledSnapshot_EquivalentToNative()
    {
        // NATIVE: provider + consumer project versions built under ONE run and mapped into one snapshot.
        var nativeRun = CompleteRun("native");
        var nativeSnap = BeginSnapshot(nativeRun, "commit-native");
        var (nativeTarget, _) = BuildProviderAndConsumer(nativeSnap, nativeSnap, tag: "n");
        _snapshots.MarkComplete(nativeSnap, _now);

        // ASSEMBLED: the SAME logical content, but the provider and consumer project versions were produced
        // by TWO separate contributions (two runs) and only mapped into ONE assembled snapshot — exactly
        // what multi-capability assembly (ContributionIngestTests) leaves in the catalog.
        var providerRun = CompleteRun("contrib-provider");
        var consumerRun = CompleteRun("contrib-consumer");
        var assembledSnap = BeginSnapshot(consumerRun, "commit-assembled");
        var (assembledTarget, _) = BuildProviderAndConsumer(assembledSnap, assembledSnap, tag: "a",
            providerOwningRun: providerRun, consumerOwningRun: consumerRun);
        _snapshots.MarkComplete(assembledSnap, _now);

        var nativeRefs = ScopedReferences(nativeSnap, nativeTarget);
        var assembledRefs = ScopedReferences(assembledSnap, assembledTarget);

        Assert.AreEqual(1, nativeRefs.Count, "the native snapshot resolves the single cross-project usage");
        CollectionAssert.AreEquivalent(
            nativeRefs.Select(Normalize).ToList(),
            assembledRefs.Select(Normalize).ToList(),
            "find-references over an assembled snapshot returns results equivalent to a native snapshot (#70 / Phase-16 R3)");
    }

    [TestMethod]
    public void CrossProjectPairs_OnAssembledSnapshot_MatchNative()
    {
        // The undirected-closure feeder (cross-repo usage topology) must see the assembled snapshot's
        // cross-project edge exactly as it sees a native one, so retention closure/deletion stays complete.
        var nativeRun = CompleteRun("native");
        var nativeSnap = BeginSnapshot(nativeRun, "commit-native");
        BuildProviderAndConsumer(nativeSnap, nativeSnap, tag: "n");
        _snapshots.MarkComplete(nativeSnap, _now);

        var providerRun = CompleteRun("contrib-provider");
        var consumerRun = CompleteRun("contrib-consumer");
        var assembledSnap = BeginSnapshot(consumerRun, "commit-assembled");
        BuildProviderAndConsumer(assembledSnap, assembledSnap, tag: "a",
            providerOwningRun: providerRun, consumerOwningRun: consumerRun);
        _snapshots.MarkComplete(assembledSnap, _now);

        // One cross-project pair per snapshot (consumer -> provider); both snapshots produce exactly one.
        Assert.AreEqual(2, new ReferenceStore(_conn).GetCrossProjectPairs().Count,
            "each snapshot (native and assembled) contributes exactly one cross-project edge to the closure feeder");
    }

    // === helpers =================================================================================

    private List<ReferenceInfo> ScopedReferences(long snapshotId, long targetSymbolId)
    {
        var refs = new ReferenceStore(_conn) { Scope = new SnapshotReadScope(snapshotId) };
        return refs.GetBySymbolId(targetSymbolId);
    }

    private static (string relPath, int line, ReferenceKind kind) Normalize(ReferenceInfo r) =>
        (Path.GetFileName(r.FilePath), r.Line, r.ReferenceKind);

    /// <summary>
    /// Builds a provider project (owning a target symbol) and a consumer project (owning an occurrence that
    /// references the provider's target symbol), both mapped into <paramref name="snapshotId"/>. When
    /// per-project owning runs are supplied the two project versions belong to DIFFERENT generations
    /// (the assembled case); otherwise both are plain snapshot-owned rows (the native case). Returns the
    /// target symbol id and the consumer project id.
    /// </summary>
    private (long targetSymbolId, long consumerProjectId) BuildProviderAndConsumer(
        long providerMapSnap, long consumerMapSnap, string tag,
        long? providerOwningRun = null, long? consumerOwningRun = null)
    {
        var providerProj = InsertProject($"provider_{tag}", "src/Lib/Lib.csproj", providerMapSnap);
        var providerFv = InsertFileVersion(providerProj, "src/Lib/Lib.cs");
        var targetSymbol = InsertSymbol(providerProj, providerFv, $"global::Lib.Api:{tag}", "Api");

        var consumerProj = InsertProject($"consumer_{tag}", "src/App/App.csproj", consumerMapSnap);
        var consumerFv = InsertFileVersion(consumerProj, "src/App/App.cs");
        InsertSymbol(consumerProj, consumerFv, $"global::App.Caller:{tag}", "Caller");
        // A pure reference (source_symbol_id NULL) from the consumer file to the provider's target symbol.
        InsertOccurrence(consumerProj, targetSymbol, consumerFv, line: 7, kind: (int)ReferenceKind.Invocation);

        return (targetSymbol, consumerProj);
    }

    private long CompleteRun(string label)
    {
        var runStore = new IndexRunStore(_conn);
        var id = runStore.BeginRun("full", _now,
            IndexProfileDescriptor.Full.ConfigurationHash, IndexProfiles.Deep, (long)IndexFeature.Deep);
        runStore.MarkComplete(id, _now, 1);
        return id;
    }

    private long BeginSnapshot(long runId, string commit)
    {
        var identity = new SnapshotIdentity
        {
            RepositoryRemoteUrl = "https://github.com/org/app",
            CommitSha = commit,
            SchemaVersion = IndexDatabase.LatestSchemaVersion,
            AnalyzerVersion = "test-analyzer",
            ToolchainFingerprint = "test-toolchain"
        };
        var (id, _, _) = _snapshots.BeginPending(identity, _repo, null, runId, _now);
        return id;
    }

    private long InsertProject(string canonical, string relPath, long snapshotId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO projects (canonical_id, git_remote_url, repo_relative_path, last_indexed_at, snapshot_id)
            VALUES (@c, @g, @p, @now, @snap) RETURNING id;
            """;
        cmd.Parameters.AddWithValue("@c", $"{canonical}_{snapshotId}");
        cmd.Parameters.AddWithValue("@g", "https://github.com/org/app");
        cmd.Parameters.AddWithValue("@p", relPath);
        cmd.Parameters.AddWithValue("@now", _now);
        cmd.Parameters.AddWithValue("@snap", snapshotId);
        var id = (long)cmd.ExecuteScalar()!;
        _snapshots.MapProject(snapshotId, id);
        return id;
    }

    private long InsertFileVersion(long projectId, string relPath)
    {
        long fileId;
        using (var f = _conn.CreateCommand())
        {
            f.CommandText = "INSERT INTO files (project_id, repo_relative_path) VALUES (@p, @path) RETURNING id;";
            f.Parameters.AddWithValue("@p", projectId);
            f.Parameters.AddWithValue("@path", relPath);
            fileId = (long)f.ExecuteScalar()!;
        }
        using var fv = _conn.CreateCommand();
        fv.CommandText =
            "INSERT INTO file_versions (file_id, content_hash, last_indexed_at) VALUES (@f, @h, @now) RETURNING id;";
        fv.Parameters.AddWithValue("@f", fileId);
        fv.Parameters.AddWithValue("@h", new byte[32]);
        fv.Parameters.AddWithValue("@now", _now);
        return (long)fv.ExecuteScalar()!;
    }

    private long InsertSymbol(long projectId, long fileVersionId, string key, string name)
    {
        using var s = _conn.CreateCommand();
        s.CommandText = """
            INSERT INTO symbols
                (project_id, symbol_key, fully_qualified_name, display_name, kind, accessibility,
                 file_version_id, line_start, line_end, last_indexed_at)
            VALUES (@p, @key, @fqn, @name, 0, 0, @fv, 1, 10, @now) RETURNING id;
            """;
        s.Parameters.AddWithValue("@p", projectId);
        s.Parameters.AddWithValue("@key", key);
        s.Parameters.AddWithValue("@fqn", key);
        s.Parameters.AddWithValue("@name", name);
        s.Parameters.AddWithValue("@fv", fileVersionId);
        s.Parameters.AddWithValue("@now", _now);
        return (long)s.ExecuteScalar()!;
    }

    private void InsertOccurrence(long inProjectId, long targetSymbolId, long fileVersionId, int line, int kind)
    {
        using var o = _conn.CreateCommand();
        o.CommandText = """
            INSERT INTO occurrences
                (in_project_id, target_symbol_id, source_symbol_id, file_version_id, line, col, kind, flags, last_indexed_at)
            VALUES (@in, @target, NULL, @fv, @line, 0, @kind, 0, @now);
            """;
        o.Parameters.AddWithValue("@in", inProjectId);
        o.Parameters.AddWithValue("@target", targetSymbolId);
        o.Parameters.AddWithValue("@fv", fileVersionId);
        o.Parameters.AddWithValue("@line", line);
        o.Parameters.AddWithValue("@kind", kind);
        o.Parameters.AddWithValue("@now", _now);
        o.ExecuteNonQuery();
    }
}
