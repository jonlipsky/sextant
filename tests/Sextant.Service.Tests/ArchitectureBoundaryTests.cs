using System.Reflection;

namespace Sextant.Service.Tests;

/// <summary>
/// Acceptance criterion 6 + the initiative's hard invariant: the core indexing libraries stay entirely
/// ProcessStack- and service-AGNOSTIC. All service/hosting code lives in NEW projects that depend on the
/// core libs — never the reverse — so a local-only Sextant installation keeps working with ZERO service
/// dependency. This asserts the dependency direction at the assembly level: no core assembly references
/// <c>Sextant.Service</c> or <c>Sextant.Service.Host</c>.
/// </summary>
[TestClass]
public class ArchitectureBoundaryTests
{
    private static readonly string[] ServiceAssemblies = ["Sextant.Service", "Sextant.Service.Host"];

    [DataTestMethod]
    [DataRow(typeof(Sextant.Core.SnapshotIdentity))]
    [DataRow(typeof(Sextant.Core.Platform.WorkerCapability))]
    [DataRow(typeof(Sextant.Core.Platform.CapabilityRouter))]
    [DataRow(typeof(Sextant.Store.IndexDatabase))]
    [DataRow(typeof(Sextant.Indexer.IndexOrchestrator))]
    [DataRow(typeof(Sextant.Daemon.DaemonHost))]
    [DataRow(typeof(Sextant.Mcp.DatabaseProvider))]
    public void CoreAssembly_DoesNotReferenceTheService(Type coreType)
    {
        var assembly = coreType.Assembly;
        var referenced = assembly.GetReferencedAssemblies().Select(a => a.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var service in ServiceAssemblies)
            Assert.IsFalse(referenced.Contains(service),
                $"core assembly '{assembly.GetName().Name}' must not reference '{service}' " +
                "— service/hosting code depends on core, never the reverse (criterion 6).");
    }

    [TestMethod]
    public void LocalQueryPath_WorksWithoutAnyServiceType()
    {
        // A local stdio consumer resolves symbols straight from the store with no SnapshotService involved,
        // proving the local path has zero service dependency (criterion 6).
        var dbPath = ServiceTestFixtures.NewDbPath();
        using var db = new Sextant.Store.IndexDatabase(dbPath);
        try
        {
            db.RunMigrations();
            var request = ServiceTestFixtures.Request();
            ServiceTestFixtures.PublishComplete(db, request, symbolCount: 2);

            var snapshots = new Sextant.Store.SnapshotStore(db.GetConnection());
            var snapshot = snapshots.GetByIdentityHash(request.ToIdentity().Hash);
            Assert.IsNotNull(snapshot);
            Assert.AreEqual(Sextant.Store.SnapshotStatus.Complete, snapshot!.Status);
            Assert.IsTrue(snapshots.GetSnapshotProjectIds(snapshot.Id).Count > 0,
                "the local store answers queries with no service dependency");
        }
        finally
        {
            SqliteTestDatabase.Delete(dbPath, db);
        }
    }
}
