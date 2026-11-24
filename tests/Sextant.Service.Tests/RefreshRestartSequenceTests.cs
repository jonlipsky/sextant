using Sextant.Store;

namespace Sextant.Service.Tests;

[TestClass]
public sealed class RefreshRestartSequenceTests
{
    [TestMethod]
    public void Restart_ReleasesClaimAndKeepsBranchIntentOrder()
    {
        var dbPath = ServiceTestFixtures.NewDbPath();
        long repositoryId;
        long refreshId;
        long retireId;
        try
        {
            using (var db = new IndexDatabase(dbPath))
            {
                db.RunMigrations();
                var connection = db.GetConnection();
                repositoryId = new SnapshotStore(connection)
                    .EnsureRepository("https://github.com/acme/refresh-sequence", 10);
                var refreshIdText = "refresh-before-retire";
                new PendingRefreshStore(connection).Admit(repositoryId, "feature", refreshIdText, 10);
                var intents = new BranchIntentStore(connection);
                refreshId = intents.Admit(
                    repositoryId, "feature", "refresh", null, refreshIdText, null, 10).Id;
                Assert.IsTrue(intents.Claim(refreshId));
                retireId = intents.Admit(
                    repositoryId, "feature", "retire", null, null, "{}", 11).Id;
                Assert.AreEqual(refreshId, intents.Head(repositoryId, "feature")?.Id);
            }

            using (var restarted = new IndexDatabase(dbPath))
            {
                restarted.RunMigrations();
                var connection = restarted.GetConnection();
                var intents = new BranchIntentStore(connection);
                Assert.AreEqual(1, intents.ReleaseClaims());
                Assert.AreEqual(refreshId, intents.Head(repositoryId, "feature")?.Id);
                Assert.IsTrue(intents.Claim(refreshId));
                Assert.IsTrue(intents.Settle(refreshId, "retired", 20));
                Assert.AreEqual(retireId, intents.Head(repositoryId, "feature")?.Id);
            }

            using (var restarted = new IndexDatabase(dbPath))
            {
                restarted.RunMigrations();
                var intents = new BranchIntentStore(restarted.GetConnection());
                Assert.AreEqual(retireId, intents.Head(repositoryId, "feature")?.Id);
                Assert.IsTrue(intents.Settle(retireId, "retired", 21));
                var laterRefresh = intents.Admit(
                    repositoryId, "feature", "refresh", null, "refresh-after-retire", null, 22);
                Assert.AreEqual(laterRefresh.Id, intents.Head(repositoryId, "feature")?.Id);
            }
        }
        finally
        {
            Sextant.TestSupport.SqliteTestDatabase.Delete(dbPath);
        }
    }
}
