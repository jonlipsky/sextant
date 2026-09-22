using Sextant.Core;

namespace Sextant.Store.Tests;

/// <summary>
/// Phase 8 (migration 012): every index run records its stable configuration hash, canonical profile
/// name, and feature bit set, and those round-trip through the <c>index_runs</c> ledger so the daemon
/// can compare the servable generation's configuration against the current one (criterion 1) and the
/// MCP capability gate can read the served generation's features (criterion 3).
/// </summary>
[TestClass]
public class IndexRunConfigurationTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_runcfg_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
    }

    [TestCleanup]
    public void TestCleanup() => SqliteTestDatabase.Delete(_dbPath, _db);

    [TestMethod]
    public void CompleteRun_RoundTripsConfigurationTriple()
    {
        var conn = _db.GetConnection();
        var runStore = new IndexRunStore(conn);
        var descriptor = IndexProfileDescriptor.For(IndexProfiles.Standard);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var id = runStore.BeginRun("full", now,
            descriptor.ConfigurationHash, descriptor.Profile, (long)descriptor.Features);
        runStore.MarkComplete(id, now, 3);

        var run = runStore.GetLastCompleteRun();
        Assert.IsNotNull(run);
        Assert.AreEqual(descriptor.ConfigurationHash, run!.ConfigHash);
        Assert.AreEqual(IndexProfiles.Standard, run.IndexingProfile);
        Assert.AreEqual((long)IndexFeature.Standard, run.Features);
    }

    [TestMethod]
    public void PrePhase8Run_HasNullConfiguration()
    {
        // BeginRun without the Phase-8 triple (a pre-Phase-8-style call) records nulls, which the
        // capability layer treats permissively (every feature available) for back-compat.
        var conn = _db.GetConnection();
        var runStore = new IndexRunStore(conn);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var id = runStore.BeginRun("full", now);
        runStore.MarkComplete(id, now, 1);

        var run = runStore.GetLastCompleteRun();
        Assert.IsNotNull(run);
        Assert.IsNull(run!.ConfigHash);
        Assert.IsNull(run.IndexingProfile);
        Assert.IsNull(run.Features);
    }
}
