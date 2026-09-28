using Microsoft.Data.Sqlite;

namespace Sextant.Store.Tests;

/// <summary>
/// SVC-4 repository grants (migration 024): the store behind per-caller visibility. A grant is keyed by
/// (tenant, principal, repository key, branch); visibility is the tenant-wide <c>'*'</c> grants plus, for a user,
/// its exact subject's own; the reconcile targets carry counts and sources, never a principal.
/// </summary>
[TestClass]
public class RepositoryGrantStoreTests
{
    private const string Widgets = "https://github.com/acme/widgets";
    private const string Gadgets = "https://github.com/acme/gadgets";

    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private RepositoryGrantStore _store = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_grants_test_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _store = new RepositoryGrantStore(_db.GetConnection());
    }

    [TestCleanup]
    public void TestCleanup() => SqliteTestDatabase.Delete(_dbPath, _db);

    [TestMethod]
    public void Migration024_IsApplied_AndIsTheLatest()
    {
        Assert.IsTrue(IndexDatabase.LatestSchemaVersion >= 24, "migration 024 advances the schema version");
        using var cmd = _db.GetConnection().CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type IN ('table','index') AND name IN ('repository_grants','ix_grants_tenant_repo');";
        Assert.AreEqual(2L, cmd.ExecuteScalar());
    }

    [TestMethod]
    public void Upsert_CreatesOnce_ThenRefreshes_KeepingSpellingAndSource()
    {
        var (first, created) = _store.Upsert("t1", "user-1", Widgets, Widgets, "", RepositoryGrantSource.Self, now: 10);
        var (second, createdAgain) = _store.Upsert("t1", "user-1", Widgets, Widgets + ".git", "", RepositoryGrantSource.Import, now: 20);

        Assert.IsTrue(created);
        Assert.IsFalse(createdAgain, "the same (tenant, principal, key, branch) is one grant");
        Assert.AreEqual(first.Id, second.Id);
        Assert.AreEqual(Widgets, second.RemoteUrl, "the first-submitted spelling is kept");
        Assert.AreEqual(RepositoryGrantSource.Self, second.Source, "and so is the first source");
        Assert.AreEqual(10, second.CreatedAt);
        Assert.AreEqual(20, second.UpdatedAt);
        Assert.AreEqual(20, _store.Get("t1", "user-1", Widgets, "")!.UpdatedAt);
        Assert.AreEqual(1, _store.CountForTenant("t1"));
    }

    [TestMethod]
    public void UniqueKey_IsEnforcedByTheSchema()
    {
        _store.Upsert("t1", "user-1", Widgets, Widgets, "main", RepositoryGrantSource.Self, now: 1);
        using var cmd = _db.GetConnection().CreateCommand();
        cmd.CommandText = """
            INSERT INTO repository_grants (tenant_id, principal, repository_key, remote_url, branch, source, created_at, updated_at)
            VALUES ('t1', 'user-1', @key, @key, 'main', 'self', 2, 2);
            """;
        cmd.Parameters.AddWithValue("@key", Widgets);
        Assert.ThrowsExactly<SqliteException>(() => cmd.ExecuteNonQuery());
    }

    [TestMethod]
    public void Delete_OneBranch_OrEveryBranch_OfThePrincipalOnly()
    {
        _store.Upsert("t1", "user-1", Widgets, Widgets, "", RepositoryGrantSource.Self, now: 1);
        _store.Upsert("t1", "user-1", Widgets, Widgets, "dev", RepositoryGrantSource.Self, now: 1);
        _store.Upsert("t1", "user-1", Widgets, Widgets, "rel", RepositoryGrantSource.Self, now: 1);
        _store.Upsert("t1", "user-2", Widgets, Widgets, "dev", RepositoryGrantSource.Self, now: 1);
        _store.Upsert("t1", RepositoryGrantStore.TenantWide, Widgets, Widgets, "dev", RepositoryGrantSource.Tenant, now: 1);

        Assert.AreEqual(1, _store.Delete("t1", "user-1", Widgets, "dev"));
        Assert.AreEqual(0, _store.Delete("t1", "user-1", Widgets, "dev"), "deleting an absent grant deletes nothing");
        Assert.AreEqual(2, _store.Delete("t1", "user-1", Widgets, RepositoryGrantStore.AllBranches));

        Assert.AreEqual(0, _store.CountForPrincipal("t1", "user-1"));
        Assert.AreEqual(1, _store.CountForPrincipal("t1", "user-2"), "another subject's grant is untouched");
        Assert.AreEqual(1, _store.CountForPrincipal("t1", RepositoryGrantStore.TenantWide), "and so is the tenant's");
    }

    [TestMethod]
    public void VisibleKeys_AreTheTenantWideGrants_PlusTheExactSubjects()
    {
        _store.Upsert("t1", "user-1", Widgets, Widgets, "", RepositoryGrantSource.Self, now: 1);
        _store.Upsert("t1", RepositoryGrantStore.TenantWide, Gadgets, Gadgets, "", RepositoryGrantSource.Tenant, now: 1);
        _store.Upsert("t2", "user-1", "https://github.com/other/repo", "https://github.com/other/repo", "", RepositoryGrantSource.Self, now: 1);

        CollectionAssert.AreEquivalent(new[] { Widgets, Gadgets }, _store.VisibleKeys("t1", "user-1").ToList());
        CollectionAssert.AreEquivalent(new[] { Gadgets }, _store.VisibleKeys("t1", "user-2").ToList(),
            "another subject sees only the tenant-wide grants");
        CollectionAssert.AreEquivalent(new[] { Gadgets }, _store.VisibleKeys("t1", null).ToList(),
            "an application (no subject) sees only the tenant-wide grants");
        CollectionAssert.AreEquivalent(new[] { "https://github.com/other/repo" }, _store.VisibleKeys("t2", "user-1").ToList(),
            "the same subject under another tenant sees only that tenant's grants");
        Assert.AreEqual(0, _store.VisibleKeys("t3", "user-1").Count);
        Assert.AreEqual(0, _store.VisibleKeys("t1", "user-").Count(k => k == Widgets), "the subject is matched exactly");
    }

    [TestMethod]
    public void ListVisible_OrdersTenantWideFirst()
    {
        _store.Upsert("t1", "user-1", Widgets, Widgets, "", RepositoryGrantSource.Self, now: 1);
        _store.Upsert("t1", RepositoryGrantStore.TenantWide, Widgets, Widgets + ".git", "", RepositoryGrantSource.Tenant, now: 2);

        var visible = _store.ListVisible("t1", "user-1");

        Assert.AreEqual(2, visible.Count);
        Assert.AreEqual(RepositoryGrantStore.TenantWide, visible[0].Principal);
        Assert.AreEqual("user-1", visible[1].Principal);
        Assert.AreEqual(1, _store.ListVisible("t1", null).Count);
        Assert.AreEqual(1, _store.ListForPrincipal("t1", "user-1").Count);
    }

    [TestMethod]
    public void TenantTargets_AreDistinctTargets_WithCountsAndSources_AndNoPrincipal()
    {
        _store.Upsert("t1", "user-1", Widgets, Widgets + ".git", "", RepositoryGrantSource.Self, now: 1);
        _store.Upsert("t1", "user-2", Widgets, Widgets, "", RepositoryGrantSource.Self, now: 2);
        _store.Upsert("t1", RepositoryGrantStore.TenantWide, Widgets, Widgets, "", RepositoryGrantSource.Tenant, now: 3);
        _store.Upsert("t1", "user-1", Widgets, Widgets, "dev", RepositoryGrantSource.Self, now: 4);
        _store.Upsert("t1", "user-3", Gadgets, Gadgets, "", RepositoryGrantSource.Import, now: 5);
        _store.Upsert("t2", "user-1", Gadgets, Gadgets, "", RepositoryGrantSource.Self, now: 6);

        var targets = _store.TenantTargets("t1");

        Assert.AreEqual(3, targets.Count);
        Assert.AreEqual(Gadgets, targets[0].RepositoryKey);
        Assert.AreEqual(1, targets[0].Watchers, "another tenant's grant is not counted");
        CollectionAssert.AreEqual(new[] { RepositoryGrantSource.Import }, targets[0].Sources.ToList());

        Assert.AreEqual((Widgets, ""), (targets[1].RepositoryKey, targets[1].Branch));
        Assert.AreEqual(3, targets[1].Watchers);
        CollectionAssert.AreEqual(new[] { RepositoryGrantSource.Self, RepositoryGrantSource.Tenant }, targets[1].Sources.ToList());
        Assert.AreEqual(Widgets, targets[1].RemoteUrl, "the tenant-wide spelling is preferred");

        Assert.AreEqual((Widgets, "dev", 1), (targets[2].RepositoryKey, targets[2].Branch, targets[2].Watchers));

        var properties = typeof(RepositoryGrantTarget).GetProperties().Select(p => p.Name).ToList();
        CollectionAssert.DoesNotContain(properties, "Principal", "a reconcile target never names a principal");
        CollectionAssert.DoesNotContain(properties, "TenantId");
    }
}
