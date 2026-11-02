using Sextant.Core;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>Issue #387: a persistent git failure ends the job failed, with git's own error text, and is never requeued forever.</summary>
[TestClass]
public sealed class GitDiagnosticsSurfaceTests
{
    [TestMethod]
    [DataRow("submodule_discovery_failed", "submodule discovery returned no result for 1 populated submodule(s) the checkout declares (libs/mix) after 3 attempts (git submodule status --recursive: exit code 128: bad object HEAD)")]
    public async Task APersistentGitFailure_AtTheFinalAttempt_SettlesFailed_WithTheGitText_AndIsNotRequeued(string code, string message)
    {
        var dbPath = ServiceTestFixtures.NewDbPath();
        var db = new IndexDatabase(dbPath);
        db.RunMigrations();
        var runs = 0;
        var finals = new List<bool>();
        var worker = new FakeSnapshotWorker(db, (_, request) =>
        {
            runs++;
            finals.Add(request.IsFinalProvisioningAttempt);
            throw new TransientProvisioningException(message, diagnosticCode: code);
        });
        var service = SnapshotService.Start(
            ServiceTestFixtures.NewOptions(dbPath) with { MaxProvisioningAttempts = 2 }, worker, db);
        try
        {
            var request = ServiceTestFixtures.Request();
            Assert.AreEqual(SnapshotJobStatus.Queued, (await service.EnsureSnapshotAsync(request)).Status);

            var last = await service.EnsureSnapshotAsync(request);

            Assert.AreEqual(SnapshotJobStatus.Failed, last.Status);
            CollectionAssert.AreEqual(new[] { false, true }, finals);
            var diagnostics = service.GetStatus(last.JobId)!.Diagnostics;
            CollectionAssert.AreEquivalent(new[] { code, "provisioning_attempts_exhausted" }, diagnostics.Select(d => d.Code).ToArray());
            Assert.IsTrue(diagnostics.All(d => d.Message.Contains("exit code 128: bad object HEAD", StringComparison.Ordinal)),
                "every diagnostic carries git's exit code and error text");

            var again = await service.EnsureSnapshotAsync(request);
            Assert.AreEqual(SnapshotJobStatus.Failed, again.Status);
            Assert.AreEqual(2, runs, "the failed job is a cached terminal result, never re-run");
        }
        finally
        {
            service.Dispose();
            SqliteTestDatabase.Delete(dbPath, db);
        }
    }
}
