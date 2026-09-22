using Sextant.Core;

namespace Sextant.Store.Tests;

/// <summary>
/// Phase 17 slice 3 — the durable operational + security audit log (migration 020). Verifies the append +
/// filtered read + per-repository cost rollup that backs criterion 5, and the two contract guarantees that
/// protect criterion 1: the actor is stored as a NON-REVERSIBLE hash (the raw principal never lands in the
/// DB), and cost attribution is scoped per repository (operator-only aggregation — the host gates every
/// read behind the control token, never the query token).
/// </summary>
[TestClass]
public class AuditLogStoreTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private AuditLogStore _store = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_audit_test_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _store = new AuditLogStore(_db.GetConnection());
    }

    [TestCleanup]
    public void TestCleanup() => SqliteTestDatabase.Delete(_dbPath, _db);

    [TestMethod]
    public void Append_And_Recent_RoundTripsWithFilters()
    {
        _store.Append(AuditAction.Ensure, AuditOutcome.Complete,
            actor: AuditLogStore.HashActor("op"), repositoryScope: "https://github.com/org/a", costIndexMs: 120);
        _store.Append(AuditAction.Retention, AuditOutcome.Complete, actor: AuditLogStore.HashActor("op"));
        _store.Append(AuditAction.Ensure, AuditOutcome.Failed,
            actor: AuditLogStore.HashActor("op"), repositoryScope: "https://github.com/org/b");

        Assert.AreEqual(3, _store.Count());

        var ensures = _store.Recent(action: AuditAction.Ensure);
        Assert.AreEqual(2, ensures.Count, "the action filter selects only ensure rows");
        Assert.IsTrue(ensures.All(e => e.Action == AuditAction.Ensure));

        var repoA = _store.Recent(repositoryScope: "https://github.com/org/a");
        Assert.AreEqual(1, repoA.Count, "the repository filter scopes to one repository");
        Assert.AreEqual(120, repoA[0].CostIndexMs);
    }

    [TestMethod]
    public void HashActor_IsNonReversible_AndStable()
    {
        var hash = AuditLogStore.HashActor("super-secret-control-token");
        Assert.IsNotNull(hash);
        Assert.AreNotEqual("super-secret-control-token", hash, "the raw principal is never stored");
        Assert.IsFalse(hash!.Contains("super-secret"), "the hash does not embed the secret");
        Assert.AreEqual(hash, AuditLogStore.HashActor("super-secret-control-token"), "hashing is stable for correlation");
        Assert.AreNotEqual(hash, AuditLogStore.HashActor("different"), "distinct principals hash differently");
    }

    [TestMethod]
    public void HashActor_NullOrBlank_IsNull_ForZeroPolicyLocalPath()
    {
        Assert.IsNull(AuditLogStore.HashActor(null));
        Assert.IsNull(AuditLogStore.HashActor("   "));
    }

    [TestMethod]
    public void CostByRepository_RollsUpPerScope_ExcludingUnscopedRows()
    {
        _store.Append(AuditAction.Ensure, AuditOutcome.Complete, repositoryScope: "repo-a", costIndexMs: 100);
        _store.Append(AuditAction.Ensure, AuditOutcome.Complete, repositoryScope: "repo-a", costIndexMs: 50);
        _store.Append(AuditAction.Ensure, AuditOutcome.Complete, repositoryScope: "repo-b", costIndexMs: 200);
        _store.Append(AuditAction.Retention, AuditOutcome.Complete); // unscoped — must be excluded

        var cost = _store.CostByRepository();
        Assert.AreEqual(2, cost.Count, "unscoped service-wide rows are excluded from per-repository attribution");

        var a = cost.Single(c => c.RepositoryScope == "repo-a");
        Assert.AreEqual(150, a.IndexMs, "index cost sums per repository");
        Assert.AreEqual(2, a.Events);

        Assert.AreEqual("repo-b", cost[0].RepositoryScope, "results are ordered by descending index cost");
    }
}
