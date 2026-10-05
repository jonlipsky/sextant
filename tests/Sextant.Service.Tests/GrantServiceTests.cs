using Sextant.Core;
using Sextant.Mcp;
using Sextant.Service.CallerIdentity;
using Sextant.Service.Grants;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// SVC-4 grant logic below the HTTP layer: the principal each scope writes for, the limits, per-caller visibility,
/// implicit selection, the grant status and <c>list_repositories</c> model, the audit attribution, and the grant
/// authorizer (including the cross-repository consumer filter).
/// </summary>
[TestClass]
public class GrantServiceTests
{
    private const string Widgets = "https://github.com/acme/widgets";
    private const string Gadgets = "https://github.com/acme/gadgets";
    private const string Gizmos = "https://github.com/acme/gizmos";

    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SnapshotService? _service;
    private FakeSnapshotWorker _worker = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = ServiceTestFixtures.NewDbPath();
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
    }

    [TestCleanup]
    public void TestCleanup()
    {
        _service?.Dispose();
        SqliteTestDatabase.Delete(_dbPath, _db);
    }

    // ==== principals and scopes =====================================================================

    [TestMethod]
    public async Task Put_Self_WritesTheCallersExactSubject_Tenant_WritesTheTenantWidePrincipal()
    {
        var service = Start();

        await service.PutGrantAsync(User("tenant-a", "slack:conn-1:U42"), GrantScope.Self, Widgets, Key(Widgets), "");
        await service.PutGrantAsync(App("tenant-a"), GrantScope.Tenant, Gadgets, Key(Gadgets), "");

        var rows = Grants();
        Assert.AreEqual(2, rows.Count);
        Assert.IsTrue(rows.Any(r => r is { TenantId: "tenant-a", Principal: "slack:conn-1:U42", Source: RepositoryGrantSource.Self }));
        Assert.IsTrue(rows.Any(r => r is { TenantId: "tenant-a", Principal: RepositoryGrantStore.TenantWide, Source: RepositoryGrantSource.Tenant }));
    }

    [TestMethod]
    public async Task Put_WrongActorForScope_OrReservedSubject_Throws_AndWritesNothing()
    {
        var service = Start();

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => service.PutGrantAsync(App("tenant-a"), GrantScope.Self, Widgets, Key(Widgets), ""));
        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => service.PutGrantAsync(User("tenant-a", "user-1"), GrantScope.Tenant, Widgets, Key(Widgets), ""));
        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => service.PutGrantAsync(User("tenant-a", RepositoryGrantStore.TenantWide), GrantScope.Self, Widgets, Key(Widgets), ""));

        Assert.IsFalse(SnapshotService.CanWriteGrants(User("tenant-a", RepositoryGrantStore.TenantWide), GrantScope.Self));
        Assert.IsTrue(SnapshotService.CanWriteGrants(User("tenant-a", "user-1"), GrantScope.Self));
        Assert.IsTrue(SnapshotService.CanWriteGrants(App("tenant-a"), GrantScope.Tenant));
        Assert.AreEqual(0, Grants().Count);
    }

    [TestMethod]
    public async Task Delete_RemovesOnlyTheCallersOwnGrants()
    {
        var service = Start();
        await service.PutGrantAsync(User("tenant-a", "user-1"), GrantScope.Self, Widgets, Key(Widgets), "");
        await service.PutGrantAsync(User("tenant-a", "user-1"), GrantScope.Self, Widgets, Key(Widgets), "dev");
        await service.PutGrantAsync(User("tenant-a", "user-2"), GrantScope.Self, Widgets, Key(Widgets), "");
        await service.PutGrantAsync(User("tenant-b", "user-1"), GrantScope.Self, Widgets, Key(Widgets), "");
        await service.PutGrantAsync(App("tenant-a"), GrantScope.Tenant, Widgets, Key(Widgets), "");

        Assert.AreEqual(2, await service.DeleteGrantsAsync(User("tenant-a", "user-1"), GrantScope.Self, Key(Widgets), RepositoryGrantStore.AllBranches));
        Assert.AreEqual(0, await service.DeleteGrantsAsync(User("tenant-a", "user-1"), GrantScope.Self, Key(Widgets), ""));
        Assert.AreEqual(1, await service.DeleteGrantsAsync(App("tenant-a"), GrantScope.Tenant, Key(Widgets), ""));

        var rows = Grants();
        Assert.AreEqual(2, rows.Count);
        Assert.IsTrue(rows.All(r => r.Principal == "user-2" || r.TenantId == "tenant-b"));
    }

    // ==== limits ====================================================================================

    [TestMethod]
    public async Task Limits_PerPrincipal_RefusesANewGrant_ButARefreshOrAnotherPrincipalPasses()
    {
        var service = Start(o => o with { MaxGrantsPerPrincipal = 2, MaxGrantsPerTenant = 100 });
        var user = User("tenant-a", "user-1");
        await service.PutGrantAsync(user, GrantScope.Self, Widgets, Key(Widgets), "");
        await service.PutGrantAsync(user, GrantScope.Self, Gadgets, Key(Gadgets), "");

        var refused = await service.PutGrantAsync(user, GrantScope.Self, Gizmos, Key(Gizmos), "");
        var refreshed = await service.PutGrantAsync(user, GrantScope.Self, Widgets, Key(Widgets), "");
        var other = await service.PutGrantAsync(User("tenant-a", "user-2"), GrantScope.Self, Gizmos, Key(Gizmos), "");
        var tenantWide = await service.PutGrantAsync(App("tenant-a"), GrantScope.Tenant, Widgets, Key(Widgets), "");
        var tenantWide2 = await service.PutGrantAsync(App("tenant-a"), GrantScope.Tenant, Gadgets, Key(Gadgets), "");
        var tenantWide3 = await service.PutGrantAsync(App("tenant-a"), GrantScope.Tenant, Gizmos, Key(Gizmos), "");

        Assert.AreEqual(GrantReason.GrantLimit, refused.Refusal);
        Assert.IsNull(refused.Grant);
        Assert.IsNull(refreshed.Refusal, "re-PUT of an existing grant is not a new grant");
        Assert.IsFalse(refreshed.Created);
        Assert.IsNull(other.Refusal, "the limit is per principal");
        Assert.IsNull(tenantWide.Refusal);
        Assert.IsNull(tenantWide2.Refusal);
        Assert.IsNull(tenantWide3.Refusal, "the per-principal limit does not apply to the tenant-wide principal");
        Assert.AreEqual(6, Grants().Count);

        var denied = service.RecentAudit(action: AuditAction.Grant).Where(a => a.Outcome == AuditOutcome.Denied).ToList();
        Assert.AreEqual(1, denied.Count);
        Assert.AreEqual($"put_self;{GrantReason.GrantLimit}", denied[0].Detail);
        Assert.AreEqual(Key(Gizmos), denied[0].RepositoryScope);
    }

    [TestMethod]
    public async Task Limits_PerTenant_CountsEveryPrincipal_AndOnlyThatTenant()
    {
        var service = Start(o => o with { MaxGrantsPerPrincipal = 100, MaxGrantsPerTenant = 2 });
        await service.PutGrantAsync(User("tenant-a", "user-1"), GrantScope.Self, Widgets, Key(Widgets), "");
        await service.PutGrantAsync(App("tenant-a"), GrantScope.Tenant, Gadgets, Key(Gadgets), "");

        var user = await service.PutGrantAsync(User("tenant-a", "user-2"), GrantScope.Self, Gizmos, Key(Gizmos), "");
        var app = await service.PutGrantAsync(App("tenant-a"), GrantScope.Tenant, Gizmos, Key(Gizmos), "");
        var otherTenant = await service.PutGrantAsync(User("tenant-b", "user-1"), GrantScope.Self, Gizmos, Key(Gizmos), "");

        Assert.AreEqual(GrantReason.GrantLimit, user.Refusal);
        Assert.AreEqual(GrantReason.GrantLimit, app.Refusal);
        Assert.IsNull(otherTenant.Refusal, "another tenant has its own limit");
    }

    // ==== visibility ================================================================================

    [TestMethod]
    public async Task Visibility_IsOwnPlusTenantWide_ForTheSameTenantOnly()
    {
        var service = Start();
        await service.PutGrantAsync(User("tenant-a", "user-1"), GrantScope.Self, Widgets, Key(Widgets), "");
        await service.PutGrantAsync(App("tenant-a"), GrantScope.Tenant, Gadgets, Key(Gadgets), "");

        CollectionAssert.AreEquivalent(new[] { Key(Widgets), Key(Gadgets) },
            service.GetVisibleRepositoryKeys(User("tenant-a", "user-1")).ToList());
        CollectionAssert.AreEquivalent(new[] { Key(Gadgets) },
            service.GetVisibleRepositoryKeys(User("tenant-a", "user-2")).ToList());
        CollectionAssert.AreEquivalent(new[] { Key(Gadgets) }, service.GetVisibleRepositoryKeys(App("tenant-a")).ToList(),
            "an application caller sees only the tenant-wide grants");
        Assert.AreEqual(0, service.GetVisibleRepositoryKeys(User("tenant-b", "user-1")).Count,
            "the same subject under another tenant sees nothing");

        Assert.IsTrue(service.IsRepositoryVisible(User("tenant-a", "user-1"), "https://GitHub.com/Acme/Widgets.git"),
            "any spelling of a granted repository is visible");
        Assert.IsFalse(service.IsRepositoryVisible(User("tenant-a", "user-1"), Gizmos));
        Assert.IsFalse(service.IsRepositoryVisible(User("tenant-a", "user-1"), null));
    }

    [TestMethod]
    public async Task ImplicitSelection_IsTheOneVisibleRepositoryWithACompleteDefaultHead()
    {
        PublishOnBranch(Widgets, "commit-w1", "main", isDefault: true);
        PublishOnBranch(Gadgets, "commit-g1", "main", isDefault: true);
        PublishOnBranch(Gizmos, "commit-z1", "feature", isDefault: false);
        var service = Start();
        var user = User("tenant-a", "user-1");

        Assert.IsNull(service.ResolveImplicitRepository(service.GetVisibleRepositoryKeys(user)), "no grant: nothing to select");

        await service.PutGrantAsync(user, GrantScope.Self, Widgets, Key(Widgets), "");
        await service.PutGrantAsync(user, GrantScope.Self, Gizmos, Key(Gizmos), "");
        await service.PutGrantAsync(user, GrantScope.Self, "https://github.com/acme/unindexed", Key("https://github.com/acme/unindexed"), "");
        Assert.AreEqual(Widgets, service.ResolveImplicitRepository(service.GetVisibleRepositoryKeys(user)),
            "a visible repository without a complete default-branch snapshot does not count");

        await service.PutGrantAsync(App("tenant-a"), GrantScope.Tenant, Gadgets, Key(Gadgets), "");
        Assert.IsNull(service.ResolveImplicitRepository(service.GetVisibleRepositoryKeys(user)),
            "two candidates: the read must name one");
        Assert.AreEqual(Gadgets, service.ResolveImplicitRepository(service.GetVisibleRepositoryKeys(App("tenant-a"))));
    }

    [TestMethod]
    public async Task SelectableRepositories_AreOnlyTheCallersVisibleOnes_WithACompleteDefaultHead_UpToTheLimit()
    {
        PublishOnBranch(Widgets, "commit-w1", "main", isDefault: true);
        PublishOnBranch(Gadgets, "commit-g1", "main", isDefault: true);
        PublishOnBranch(Gizmos, "commit-z1", "feature", isDefault: false);
        PublishOnBranch("https://github.com/acme/hidden", "commit-h1", "main", isDefault: true);
        var service = Start();
        var user = User("tenant-a", "user-1");
        await service.PutGrantAsync(user, GrantScope.Self, Gadgets, Key(Gadgets), "");
        await service.PutGrantAsync(user, GrantScope.Self, Widgets, Key(Widgets), "");
        await service.PutGrantAsync(user, GrantScope.Self, Gizmos, Key(Gizmos), "");
        // Another caller's grant on a repository this one cannot read.
        await service.PutGrantAsync(User("tenant-a", "user-2"), GrantScope.Self, "https://github.com/acme/hidden",
            Key("https://github.com/acme/hidden"), "");
        var keys = service.GetVisibleRepositoryKeys(user);

        CollectionAssert.AreEqual(new[] { Widgets, Gadgets }, service.ListSelectableRepositories(keys, 10).ToArray(),
            "the visible repositories with a complete default head, in catalog order; never another caller's");
        CollectionAssert.AreEqual(new[] { Widgets }, service.ListSelectableRepositories(keys, 1).ToArray());
        Assert.AreEqual(0, service.ListSelectableRepositories(new HashSet<string>(), 10).Count, "no grant: nothing");
    }

    // ==== status and list_repositories ==============================================================

    [TestMethod]
    public async Task ListGrants_ReportsCompleteMissingAndPending()
    {
        var snapId = PublishOnBranch(Widgets, "commit-w1", "main", isDefault: true);
        _worker = new FakeSnapshotWorker(_db) { UseGate = true };
        var service = Start(worker: _worker);
        var user = User("tenant-a", "user-1");
        await service.PutGrantAsync(user, GrantScope.Self, Widgets, Key(Widgets), "");
        await service.PutGrantAsync(user, GrantScope.Self, Widgets, Key(Widgets), "feature");
        await service.PutGrantAsync(user, GrantScope.Self, Gadgets, Key(Gadgets), "");
        await service.PutGrantAsync(user, GrantScope.Self, Gizmos, Key(Gizmos), "");

        var pending = await service.BeginEnsureSnapshotAsync(ServiceTestFixtures.Request(repo: Gadgets, commit: "commit-g1"));
        Assert.IsFalse(SnapshotJobStatus.IsTerminal(pending.Status));
        try
        {
            var grants = service.ListGrants(user, GrantScope.Self);

            Assert.AreEqual(4, grants.Count);
            var widgetsDefault = grants.Single(g => g.Repository == Widgets && g.Branch == "");
            Assert.AreEqual(GrantSnapshotStatus.Complete, widgetsDefault.Status.SnapshotStatus);
            Assert.AreEqual("main", widgetsDefault.Status.ResolvedBranch, "a default-branch grant resolves to the branch name");
            Assert.AreEqual("commit-w1", widgetsDefault.Status.CommitSha);
            Assert.AreEqual(new SnapshotStore(_db.GetConnection()).GetById(snapId)!.IdentityHash, widgetsDefault.Status.IdentityHash);
            Assert.IsNotNull(widgetsDefault.Status.PublishedAt);

            var feature = grants.Single(g => g.Branch == "feature");
            Assert.AreEqual(GrantSnapshotStatus.Missing, feature.Status.SnapshotStatus);
            Assert.AreEqual("feature", feature.Status.ResolvedBranch);

            Assert.AreEqual(GrantSnapshotStatus.Pending, grants.Single(g => g.Repository == Gadgets).Status.SnapshotStatus,
                "an ensure in flight for the default branch makes the grant pending");
            var gizmos = grants.Single(g => g.Repository == Gizmos);
            Assert.AreEqual(GrantSnapshotStatus.Missing, gizmos.Status.SnapshotStatus);
            Assert.IsNull(gizmos.Status.ResolvedBranch, "an unknown default branch is not guessed");
            Assert.IsNull(gizmos.Status.CommitSha);

            Assert.AreEqual(0, service.ListGrants(User("tenant-a", "user-2"), GrantScope.Self).Count);
        }
        finally
        {
            _worker.Gate.SetResult();
        }
    }

    [TestMethod]
    public async Task ListVisibleRepositories_MergesCatalogBranches_WithGrantedOnes()
    {
        PublishOnBranch(Widgets, "commit-w1", "main", isDefault: true);
        PublishOnBranch(Widgets, "commit-w2", "dev", isDefault: false);
        var service = Start();
        var user = User("tenant-a", "user-1");
        await service.PutGrantAsync(user, GrantScope.Self, Widgets + ".git", Key(Widgets), "");
        await service.PutGrantAsync(App("tenant-a"), GrantScope.Tenant, Widgets, Key(Widgets), "release");
        await service.PutGrantAsync(user, GrantScope.Self, Gadgets, Key(Gadgets), "");
        await service.PutGrantAsync(User("tenant-a", "user-2"), GrantScope.Self, Gizmos, Key(Gizmos), "");

        var repositories = service.ListVisibleRepositories(user);

        Assert.AreEqual(2, repositories.Count, "another subject's grant is not listed");
        var gadgets = repositories.Single(r => r.Repository == Gadgets);
        Assert.AreEqual(1, gadgets.Branches.Count);
        Assert.AreEqual(("", true, GrantSnapshotStatus.Missing), (gadgets.Branches[0].Branch, gadgets.Branches[0].IsDefault, gadgets.Branches[0].Status));

        var widgets = repositories.Single(r => RepositoryGrantKey.Of(r.Repository) == Key(Widgets));
        Assert.AreEqual(Widgets, widgets.Repository, "the tenant-wide spelling is preferred");
        CollectionAssert.AreEqual(new[] { RepositoryGrantSource.Self, RepositoryGrantSource.Tenant }, widgets.Sources.ToList());
        CollectionAssert.AreEqual(new[] { "dev", "main", "release" }, widgets.Branches.Select(b => b.Branch).ToList(),
            "every catalog branch of a visible repository plus the granted branch the catalog does not know");
        var main = widgets.Branches.Single(b => b.Branch == "main");
        Assert.IsTrue(main.IsDefault);
        Assert.AreEqual(GrantSnapshotStatus.Complete, main.Status);
        Assert.AreEqual("commit-w1", main.CommitSha);
        Assert.AreEqual(GrantSnapshotStatus.Complete, widgets.Branches.Single(b => b.Branch == "dev").Status);
        Assert.AreEqual(GrantSnapshotStatus.Missing, widgets.Branches.Single(b => b.Branch == "release").Status);

        Assert.AreEqual(1, service.ListVisibleRepositories(App("tenant-a")).Count);
        Assert.AreEqual(0, service.ListVisibleRepositories(User("tenant-b", "user-1")).Count);
    }

    // ==== audit =====================================================================================

    [TestMethod]
    public async Task Audit_CarriesTheCallerSuffix_OnAcceptedAndDeniedRows()
    {
        var service = Start();
        var user = User("tenant-a", "user-1");
        var auditor = AuditCaller.ForCaller(user);

        await service.PutGrantAsync(user, GrantScope.Self, Widgets, Key(Widgets), "", auditor);
        await service.PutGrantAsync(user, GrantScope.Self, Widgets, Key(Widgets), "", auditor);
        await service.DeleteGrantsAsync(user, GrantScope.Self, Key(Widgets), RepositoryGrantStore.AllBranches, auditor);
        await service.RecordGrantDeniedAsync("put_self", GrantReason.BranchNotAllowed, Key(Widgets), auditor);
        await service.RecordGrantDeniedAsync("put_self", "scheme_not_allowed", null, auditor);

        const string suffix = ";idp=processstack;kid=kid-a;via=mcp-surface;cid=conn-1;dep=dep-1;jti=jti-1";
        var rows = service.RecentAudit(action: AuditAction.Grant).OrderBy(r => r.Id).ToList();
        CollectionAssert.AreEqual(new[]
        {
            "put_self;created" + suffix, "put_self;updated" + suffix, "delete_self;deleted_1" + suffix,
            "put_self;branch_not_allowed" + suffix, "put_self;scheme_not_allowed" + suffix
        }, rows.Select(r => r.Detail).ToList());
        CollectionAssert.AreEqual(new[] { AuditOutcome.Accepted, AuditOutcome.Accepted, AuditOutcome.Accepted, AuditOutcome.Denied, AuditOutcome.Denied },
            rows.Select(r => r.Outcome).ToList());
        Assert.IsTrue(rows.All(r => r.Actor == AuditLogStore.HashActor("tenant-a/user-1")));
        Assert.IsNull(rows[^1].RepositoryScope, "a refused URL is never stored");
        Assert.IsTrue(rows.Take(4).All(r => r.RepositoryScope == Key(Widgets)));
    }

    [TestMethod]
    public void AuditCaller_SanitizesEveryClaimValue()
    {
        var hostile = User("tenant-a", "user-1") with
        {
            Idp = "a;b",
            KeyId = "kid\nforged",
            Via = new string('v', 65),
            Connection = "",
            Deployment = "dep ok",
            Jti = "jti-1.ok:_-"
        };

        var caller = AuditCaller.ForCaller(hostile);
        AuditCaller bare = "control-bearer";

        Assert.AreEqual(";idp=-;kid=-;via=-;cid=-;dep=-;jti=jti-1.ok:_-", caller.DetailSuffix);
        Assert.AreEqual("tenant-a/user-1", caller.Principal);
        Assert.AreEqual("x" + caller.DetailSuffix, caller.Detail("x"));
        Assert.AreEqual("x", bare.Detail("x"), "a bare principal carries no suffix");
        Assert.AreEqual(AuditLogStore.HashActor("control-bearer"), bare.Actor);
        Assert.AreEqual("tenant-b/app:sextant", AuditCaller.ForCaller(App("tenant-b")).Principal);
    }

    [TestMethod]
    public void AuditCaller_NullDeployment_RendersDepDash()
    {
        var caller = AuditCaller.ForCaller(User("tenant-a", "user-1") with { Deployment = null });

        Assert.AreEqual(";idp=processstack;kid=kid-a;via=mcp-surface;cid=conn-1;dep=-;jti=jti-1", caller.DetailSuffix);
        StringAssert.Contains(caller.DetailSuffix, ";dep=-;");
        Assert.AreEqual("tenant-a/user-1", caller.Principal, "an absent dep does not change the actor");
    }

    // ==== write availability ========================================================================

    [TestMethod]
    public async Task GrantWrite_AfterLeaseLoss_IsUnavailable_AndWritesNothing()
    {
        var service = Start(o => o with { LeaseTtl = TimeSpan.FromSeconds(3) });
        StealLease();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!service.LeaseLost && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.IsTrue(service.LeaseLost);

        await Assert.ThrowsExactlyAsync<GrantStoreUnavailableException>(
            () => service.PutGrantAsync(User("tenant-a", "user-1"), GrantScope.Self, Widgets, Key(Widgets), ""));
        await Assert.ThrowsExactlyAsync<GrantStoreUnavailableException>(
            () => service.RecordGrantDeniedAsync("put_self", GrantReason.InvalidBody));

        Assert.AreEqual(0, Grants().Count);
        Assert.AreEqual(0, service.RecentAudit(action: AuditAction.Grant).Count);
    }

    [TestMethod]
    public async Task GrantWrite_AfterDispose_IsRefused()
    {
        var service = Start();
        service.Dispose();
        _service = null;

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            () => service.PutGrantAsync(User("tenant-a", "user-1"), GrantScope.Self, Widgets, Key(Widgets), ""));
    }

    // ==== the grant authorizer ======================================================================

    [TestMethod]
    public void Authorizer_AllowsOnlyVisibleRepositories_WithOneUniformDenial()
    {
        IReadOnlySet<string>? keys = null;
        var urls = new Dictionary<long, string> { [1] = Widgets, [2] = Gadgets };
        var authorizer = new GrantReadAuthorizer(() => keys, id => urls.GetValueOrDefault(id));

        Assert.IsTrue(authorizer.IsEnforcing);
        var noCaller = authorizer.AuthorizeRepository(1, Widgets);
        Assert.IsFalse(noCaller.Allowed, "no verified caller: everything is denied");

        keys = new HashSet<string>(StringComparer.Ordinal);
        Assert.IsFalse(authorizer.AuthorizeRepository(1, Widgets).Allowed, "no grants: everything is denied");

        keys = new HashSet<string>(StringComparer.Ordinal) { Key(Widgets) };
        Assert.IsTrue(authorizer.AuthorizeRepository(1, "https://github.com/ACME/widgets.git/").Allowed);
        Assert.IsTrue(authorizer.Authorize(Row(1)).Allowed);
        Assert.IsTrue(authorizer.IsVisible(Widgets));

        var denials = new[]
        {
            noCaller,
            authorizer.AuthorizeRepository(2, Gadgets),
            authorizer.Authorize(Row(2)),
            authorizer.Authorize(Row(99)),
            authorizer.Authorize(null)
        };
        Assert.IsTrue(denials.All(d => !d.Allowed));
        Assert.AreEqual(1, denials.Select(d => d.Reason).Distinct().Count(), "every denial reads the same");
    }

    [TestMethod]
    public void Authorizer_FiltersCrossRepositoryConsumers()
    {
        var provider = PublishOnBranch(Widgets, "commit-w1", "main", isDefault: true);
        SeedConsumer(Gadgets, "commit-g1", provider);
        SeedConsumer(Gizmos, "commit-z1", provider);
        IReadOnlySet<string> keys = new HashSet<string>(StringComparer.Ordinal) { Key(Widgets), Key(Gadgets) };
        var authorizer = new GrantReadAuthorizer(() => keys, _ => null);
        var conn = _db.GetConnection();

        var consumers = CrossRepositoryUsageResolver.ResolveConsumers(conn, Widgets, null, CrossRepoUsageScope.DefaultHeads, authorizer);
        CollectionAssert.AreEqual(new[] { Gadgets }, consumers.Select(c => c.ConsumerRepositoryUrl).ToList(),
            "an ungranted consumer is dropped silently");

        keys = new HashSet<string>(StringComparer.Ordinal) { Key(Gadgets), Key(Gizmos) };
        Assert.AreEqual(0, CrossRepositoryUsageResolver.ResolveConsumers(conn, Widgets, null, CrossRepoUsageScope.DefaultHeads, authorizer).Count,
            "an ungranted provider answers nothing");
    }

    [TestMethod]
    public void Authorizer_VisibilityReadFailure_FailsClosed()
    {
        var provider = PublishOnBranch(Widgets, "commit-w1", "main", isDefault: true);
        SeedConsumer(Gadgets, "commit-g1", provider);
        var authorizer = new GrantReadAuthorizer(() => throw new InvalidOperationException("the grant catalog is unreadable"), _ => Widgets);
        var conn = _db.GetConnection();

        // A failed visibility read propagates (the tool call fails) instead of reading as "allowed".
        Assert.ThrowsExactly<InvalidOperationException>(() => authorizer.Authorize(Row(1)));
        Assert.ThrowsExactly<InvalidOperationException>(() => authorizer.AuthorizeRepository(1, Widgets));
        Assert.ThrowsExactly<InvalidOperationException>(() => authorizer.IsVisible(Widgets));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            CrossRepositoryUsageResolver.ResolveConsumers(conn, Widgets, null, CrossRepoUsageScope.DefaultHeads, authorizer));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            CrossRepositoryUsageResolver.Resolve(conn, Widgets, "global::App.Type0", CrossRepoUsageScope.DefaultHeads, authorizer));
    }

    [TestMethod]
    public void RepositoryGrantKey_FoldsSpellings_AndRevocationSurvivesANarrowedAllowList()
    {
        Assert.AreEqual(Key(Widgets), Key("https://GitHub.com/acme/Widgets.git"));
        Assert.AreEqual(string.Empty, Key(" "));

        var narrowed = new RepositoryUrlPolicy(["gitlab.example.test"]);
        Assert.IsFalse(narrowed.Evaluate(Widgets).Ok);
        Assert.IsTrue(RepositoryGrantKey.EvaluateForRevocation(narrowed, Widgets).Ok,
            "a grant made before the allow-list narrowed can still be revoked");
        Assert.IsFalse(RepositoryGrantKey.EvaluateForRevocation(narrowed, "http://github.com/acme/widgets").Ok,
            "the shape rules still apply");
        Assert.IsFalse(RepositoryGrantKey.EvaluateForRevocation(narrowed, "https://127.0.0.1/acme/widgets").Ok);
    }

    // ==== helpers ===================================================================================

    private SnapshotService Start(Func<ServiceOptions, ServiceOptions>? configure = null, FakeSnapshotWorker? worker = null)
    {
        var options = ServiceTestFixtures.NewOptions(_dbPath);
        if (configure is not null)
            options = configure(options);
        _worker = worker ?? new FakeSnapshotWorker(_db);
        _service = SnapshotService.Start(options, _worker, _db);
        return _service;
    }

    private static string Key(string url) => RepositoryGrantKey.Of(url);

    private List<RepositoryGrantRow> Grants()
    {
        using var cmd = _db.GetConnection().CreateCommand();
        cmd.CommandText = "SELECT DISTINCT tenant_id, principal FROM repository_grants;";
        var principals = new List<(string, string)>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                principals.Add((reader.GetString(0), reader.GetString(1)));
        }
        var store = new RepositoryGrantStore(_db.GetConnection());
        return principals.SelectMany(p => store.ListForPrincipal(p.Item1, p.Item2)).ToList();
    }

    internal static CallerPrincipal User(string tenant, string sub) => new()
    {
        TenantId = tenant,
        TenantSlug = "acme",
        Actor = CallerActor.User,
        Idp = CallerAssertionOptions.PlatformIdp,
        UserId = sub,
        App = "sextant",
        Deployment = "dep-1",
        Connection = "conn-1",
        Via = "mcp-surface",
        KeyId = tenant == "tenant-b" ? "kid-b" : "kid-a",
        Jti = "jti-1"
    };

    internal static CallerPrincipal App(string tenant) => User(tenant, "unused") with
    {
        Actor = CallerActor.Application,
        Idp = null,
        UserId = null
    };

    private static SnapshotRow Row(long repositoryId) => new()
    {
        Id = 1,
        IdentityHash = "hash",
        RepositoryId = repositoryId,
        SchemaVersion = IndexDatabase.SnapshotSchemaVersion,
        AnalyzerVersion = "test",
        Status = SnapshotStatus.Complete,
        CreatedAt = 1
    };

    private long PublishOnBranch(string repo, string commit, string branch, bool isDefault) =>
        PublishOnBranch(_db, repo, commit, branch, isDefault);

    internal static long PublishOnBranch(IndexDatabase db, string repo, string commit, string branch, bool isDefault)
    {
        var snapId = ServiceTestFixtures.PublishComplete(db, ServiceTestFixtures.Request(repo: repo, commit: commit), symbolCount: 1, recordCommit: true);
        var snapshots = new SnapshotStore(db.GetConnection());
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        snapshots.SetBranchPointer(snapshots.EnsureBranch(snapshots.GetRepositoryId(repo)!.Value, branch, isDefault, now), snapId, now);
        return snapId;
    }

    private void SeedConsumer(string repo, string commit, long providerSnapshotId) =>
        SeedConsumer(_db, repo, commit, providerSnapshotId);

    // A consumer repository whose default-branch head pins the provider snapshot's project.
    internal static void SeedConsumer(IndexDatabase db, string repo, string commit, long providerSnapshotId)
    {
        var conn = db.GetConnection();
        var snapshots = new SnapshotStore(conn);
        var providerSnapshot = snapshots.GetById(providerSnapshotId)!;
        long providerProjectId;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT project_id FROM snapshot_projects WHERE snapshot_id = @s LIMIT 1;";
            cmd.Parameters.AddWithValue("@s", providerSnapshotId);
            providerProjectId = (long)cmd.ExecuteScalar()!;
        }

        var repoId = snapshots.EnsureRepository(repo, now: 1);
        var commitId = snapshots.EnsureCommit(repoId, commit, null, now: 1);
        var (snapId, _, _) = snapshots.BeginPending(ServiceTestFixtures.Request(repo: repo, commit: commit).ToIdentity(), repoId, commitId, null, now: 1);
        var canonical = $"logical_{commit}";
        var logicalId = snapshots.EnsureLogicalProject(repoId, canonical, "src/App/App.csproj", "net10.0", now: 1);
        var projectId = new ProjectStore(conn).UpsertSnapshotProject(
            new ProjectIdentity { CanonicalId = canonical, GitRemoteUrl = repo, RepoRelativePath = "src/App/App.csproj", TargetFramework = "net10.0" },
            snapId, logicalId, lastIndexedAt: 1);
        snapshots.MapProject(snapId, projectId);
        snapshots.MarkComplete(snapId, publishedAt: 1);
        snapshots.SetBranchPointer(snapshots.EnsureBranch(repoId, "main", isDefault: true, now: 2), snapId, now: 3);
        new SnapshotDependencyStore(conn).Insert(new SnapshotDependencyEdge
        {
            ConsumerSnapshotId = snapId,
            ConsumerProjectId = projectId,
            ProviderSnapshotId = providerSnapshotId,
            ProviderProjectId = providerProjectId,
            ProviderRepositoryId = providerSnapshot.RepositoryId,
            ProviderCommitSha = "provider-pin",
            ReferenceKind = "submodule_ref",
            SubmoduleDirty = false,
            CreatedAt = 1
        });
    }

    private void StealLease()
    {
        using var thief = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        thief.Open();
        using var cmd = thief.CreateCommand();
        cmd.CommandText = "UPDATE writer_lease SET owner_token = 'thief' WHERE id = 1;";
        cmd.ExecuteNonQuery();
    }
}
