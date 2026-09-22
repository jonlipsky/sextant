using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sextant.Mcp.Tools;
using Sextant.Store;

namespace Sextant.Integration.Tests;

/// <summary>
/// Phase 9 end-to-end: the shared fixture runs the REAL <see cref="Sextant.Indexer.IndexOrchestrator"/>
/// over this git repository, so a full index resolves git coordinates and publishes an immutable
/// snapshot. These read-only assertions prove the whole pipeline: exactly one complete, selected
/// generation exists (criterion 7 / whole-generation isolation as observed by a scope-less reader), a
/// scope-less MCP query transparently defaults to it (criterion 6), and historical API surface is
/// self-contained so it survives reindexing (criterion 5).
/// </summary>
[TestClass]
[TestCategory("Integration")]
public class SnapshotIntegrationTests
{
    private IntegrationFixture _fixture = null!;
    private SqliteConnection _conn = null!;

    [TestInitialize]
    public void Setup()
    {
        _fixture = IntegrationFixture.Instance;
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _fixture.DbPath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        _conn.Open();
    }

    [TestCleanup]
    public void Cleanup() => _conn.Dispose();

    [TestMethod]
    public void RealFullIndex_PublishesExactlyOneCompleteSelectedSnapshot()
    {
        // The indexed working tree is a single git repository.
        Assert.AreEqual(1, Scalar("SELECT COUNT(*) FROM repositories;"), "one repository was indexed");

        // Exactly one complete snapshot, and no half-built (pending/partial/failed) generation lingers
        // as observable state — a scope-less reader can only ever see one complete generation.
        Assert.AreEqual(1, Scalar("SELECT COUNT(*) FROM snapshots WHERE status = 'complete';"),
            "the full index published exactly one complete snapshot");
        Assert.AreEqual(0, Scalar("SELECT COUNT(*) FROM snapshots WHERE status IN ('pending','partial','failed');"),
            "no half-built generation is left observable");

        // A default branch points at that complete snapshot, and the scope-less resolver selects it.
        var selected = new SnapshotStore(_conn).GetSelectedSnapshotId();
        Assert.IsNotNull(selected, "the default branch selects the published snapshot");
        Assert.AreEqual(selected, Scalar(
            "SELECT b.snapshot_id FROM branches b WHERE b.is_default = 1 ORDER BY b.id LIMIT 1;"),
            "GetSelectedSnapshotId resolves the default branch pointer");
        Assert.AreEqual("complete", ScalarString($"SELECT status FROM snapshots WHERE id = {selected};"));
    }

    [TestMethod]
    public void RealFullIndex_AllRowsBelongToTheSelectedGeneration_NoTornMix()
    {
        var selected = new SnapshotStore(_conn).GetSelectedSnapshotId();
        Assert.IsNotNull(selected);

        // Every project row is snapshot-tagged (no pre-Phase-9 mutable NULL rows survive a fresh index)
        // and mapped into the selected snapshot — so a scope-less reader sees one coherent generation.
        Assert.AreEqual(0, Scalar("SELECT COUNT(*) FROM projects WHERE snapshot_id IS NULL;"),
            "no legacy (untagged) project rows remain after a fresh snapshot index");
        Assert.AreEqual(0, Scalar($"""
            SELECT COUNT(*) FROM projects p
            WHERE p.snapshot_id IS NOT NULL
              AND p.id NOT IN (SELECT project_id FROM snapshot_projects WHERE snapshot_id = {selected});
            """), "every snapshot-tagged project is mapped into the selected snapshot");

        // No semantic row escapes the selected generation: every symbol (and occurrence) is owned by a
        // project that belongs to the selected snapshot — the whole-generation reader-isolation property.
        Assert.AreEqual(0, Scalar($"""
            SELECT COUNT(*) FROM symbols s
            WHERE s.project_id NOT IN (SELECT project_id FROM snapshot_projects WHERE snapshot_id = {selected});
            """), "no symbol leaks from outside the selected generation");
        Assert.AreEqual(0, Scalar($"""
            SELECT COUNT(*) FROM occurrences o
            WHERE o.in_project_id NOT IN (SELECT project_id FROM snapshot_projects WHERE snapshot_id = {selected});
            """), "no occurrence leaks from outside the selected generation");
    }

    [TestMethod]
    public void ScopelessMcpQuery_DefaultsToSelectedSnapshot()
    {
        // Backward-compatible default (criterion 6): existing MCP tools take no scope yet resolve the
        // current selected snapshot transparently and return results.
        var result = FindSymbolTool.FindSymbol(_fixture.DbProvider, "global::Sextant.Store.IndexDatabase");
        var meta = JsonDocument.Parse(result).RootElement.GetProperty("meta");
        Assert.IsTrue(meta.GetProperty("result_count").GetInt32() >= 1,
            "a scope-less query returns the selected snapshot's rows");
    }

    [TestMethod]
    public void HistoricalApiSurface_IsSelfContained_ForReindexSurvival()
    {
        // Criterion 5: whatever API surface the real index captured must be self-contained (its own
        // symbol_key / fqn / accessibility), which is exactly what lets a comparison survive a later
        // local/remote reindex that replaces the live symbol rows. (The survival mechanism itself is
        // proven deterministically by the Phase-9 store test C5_ApiHistory_Survives...).
        var total = Scalar("SELECT COUNT(*) FROM api_surface_snapshots;");
        if (total == 0)
            return; // no cross-project public API captured; the store-level C5 test covers the guarantee.

        Assert.AreEqual(0, Scalar("""
            SELECT COUNT(*) FROM api_surface_snapshots
            WHERE symbol_key IS NULL OR fully_qualified_name IS NULL OR accessibility IS NULL OR git_commit IS NULL;
            """), "every captured API-surface row carries its own self-contained identity");
    }

    private long Scalar(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private string ScalarString(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return (string)cmd.ExecuteScalar()!;
    }
}
