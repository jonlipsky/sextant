using System.Net;
using System.Text.Json;
using Sextant.Mcp;
using Sextant.Service.Grants;
using Sextant.Service.Host;
using Sextant.Store;
using static Sextant.Service.Tests.CallerAssertionHttpTests;

namespace Sextant.Service.Tests;

/// <summary>
/// SVC-4 at the HTTP boundary: the grant routes (<c>/control/grants/*</c>), per-caller visibility of delegate reads,
/// implicit selection, the <c>act=user</c> gates on ensure and status, and <c>list_repositories</c>. The harness is
/// SVC-3's (<see cref="CallerAssertionHttpTests.Harness"/>): Widgets is published with a complete <c>main</c>; the
/// tenants are <c>tenant-a</c> (<c>kid-a</c>) and <c>tenant-b</c> (<c>kid-b</c>).
/// </summary>
[TestClass]
public class GrantHttpTests
{
    private const string Gadgets = "https://github.com/acme/gadgets";
    private const string Gizmos = "https://github.com/acme/gizmos";
    private const string SelfPath = "/control/grants/self";
    private const string TenantPath = "/control/grants/tenant";
    private const string TargetsPath = "/control/grants?scope=tenant";
    private const string Suffix = ";idp=processstack;kid=kid-a;via=mcp-surface;cid=conn-1;dep=dep-1;jti=jti-1";
    // An application caller carries no idp claim.
    private const string AppSuffix = ";idp=-;kid=kid-a;via=mcp-surface;cid=conn-1;dep=dep-1;jti=jti-1";

    private static readonly string NotFound = WithoutTimestamp(JsonDocument.Parse(ResponseBuilder.BuildNotFound()).RootElement);

    // ==== admission =================================================================================

    [TestMethod]
    [DataRow("PUT", SelfPath, true)]
    [DataRow("DELETE", SelfPath + "?repository=https://github.com/acme/widgets", true)]
    [DataRow("GET", SelfPath, false)]
    [DataRow("PUT", TenantPath, true)]
    [DataRow("DELETE", TenantPath + "?repository=https://github.com/acme/widgets", true)]
    [DataRow("GET", TargetsPath, false)]
    public async Task GrantRoute_WithoutCaller_Is401CallerRequired(string method, string path, bool audited)
    {
        await using var host = await Harness.StartAsync();

        using var response = await host.ControlAsync(new HttpMethod(method), path, ControlToken, jsonBody: method == "PUT" ? Body(Widgets) : null);

        await AssertUnauthorizedAsync(response, "caller_required", "invalid_request");
        var audit = host.Service.RecentAudit(action: AuditAction.Grant);
        Assert.AreEqual(audited ? 1 : 0, audit.Count, "a refused write is audited; a listing is not");
        if (audited)
        {
            StringAssert.EndsWith(audit[0].Detail, ";caller_required");
            Assert.AreEqual(AuditOutcome.Denied, audit[0].Outcome);
            Assert.AreEqual(AuditLogStore.HashActor(ControlToken), audit[0].Actor);
        }
        Assert.AreEqual(0, GrantCount(host));
    }

    [TestMethod]
    [DataRow("PUT", SelfPath, false)]
    [DataRow("DELETE", SelfPath + "?repository=https://github.com/acme/widgets", false)]
    [DataRow("GET", SelfPath, false)]
    [DataRow("PUT", TenantPath, true)]
    [DataRow("DELETE", TenantPath + "?repository=https://github.com/acme/widgets", true)]
    [DataRow("GET", TargetsPath, true)]
    public async Task GrantRoute_WrongActor_Is403(string method, string path, bool sendUser)
    {
        await using var host = await Harness.StartAsync();
        var caller = sendUser ? host.UserAssertion() : AppAssertion(host);

        using var response = await host.ControlAsync(new HttpMethod(method), path, ControlToken, caller, method == "PUT" ? Body(Widgets) : null);

        await AssertRejectedAsync(response, HttpStatusCode.Forbidden, GrantReason.WrongActor);
        Assert.AreEqual(0, GrantCount(host));
    }

    [TestMethod]
    public async Task GrantSelf_UserWhoseSubjectIsTheTenantWidePrincipal_Is403()
    {
        await using var host = await Harness.StartAsync();

        using var response = await host.ControlAsync(HttpMethod.Put, SelfPath, ControlToken, host.UserAssertion(sub: "*"), Body(Widgets));

        await AssertForbiddenAsync(response);
        Assert.AreEqual(0, GrantCount(host), "a user can never write the tenant-wide principal through /self");
    }

    [TestMethod]
    [DataRow(QueryToken)]
    [DataRow(DelegateToken)]
    [DataRow(ContributeToken)]
    public async Task GrantRoute_NeedsTheControlToken(string token)
    {
        await using var host = await Harness.StartAsync();

        using var response = await host.ControlAsync(HttpMethod.Put, SelfPath, token, host.UserAssertion(), Body(Widgets));

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual(0, GrantCount(host));
    }

    // ==== principal_in_body =========================================================================

    [TestMethod]
    [DataRow("""{"repository":"https://github.com/acme/widgets","tenant_id":"tenant-b"}""")]
    [DataRow("""{"repository":"https://github.com/acme/widgets","principal":"*"}""")]
    [DataRow("""{"repository":"https://github.com/acme/widgets","sub":"user-2"}""")]
    [DataRow("""{"repository":"https://github.com/acme/widgets","User":"user-2"}""")]
    [DataRow("""{"repository":"https://github.com/acme/widgets","user_id":"user-2"}""")]
    [DataRow("""{"tid":"tenant-b","repository":"https://github.com/acme/widgets"}""")]
    [DataRow("""{"repository":"not a url","principal":"user-2"}""")]
    public async Task Put_BodyNamingAPrincipal_Is400(string body)
    {
        await using var host = await Harness.StartAsync();

        using var self = await host.ControlAsync(HttpMethod.Put, SelfPath, ControlToken, host.UserAssertion(), body);
        using var tenant = await host.ControlAsync(HttpMethod.Put, TenantPath, ControlToken, AppAssertion(host), body);

        await AssertRejectedAsync(self, HttpStatusCode.BadRequest, GrantReason.PrincipalInBody);
        await AssertRejectedAsync(tenant, HttpStatusCode.BadRequest, GrantReason.PrincipalInBody);
        Assert.AreEqual(0, GrantCount(host));
        var audit = host.Service.RecentAudit(action: AuditAction.Grant).OrderBy(a => a.Id).ToList();
        CollectionAssert.AreEqual(new[] { "put_self;principal_in_body" + Suffix, "put_tenant;principal_in_body" + AppSuffix },
            audit.Select(a => a.Detail).ToList());
        Assert.IsTrue(audit.All(a => a.Outcome == AuditOutcome.Denied && a.RepositoryScope is null));
    }

    [TestMethod]
    [DataRow("GET", SelfPath + "?sub=user-2", true)]
    [DataRow("PUT", SelfPath + "?tenant_id=tenant-b", true)]
    [DataRow("DELETE", SelfPath + "?repository=https://github.com/acme/widgets&principal=user-2", true)]
    [DataRow("DELETE", TenantPath + "?repository=https://github.com/acme/widgets&TID=tenant-b", false)]
    [DataRow("GET", TargetsPath + "&user=user-2", false)]
    public async Task Request_QueryNamingAPrincipal_Is400(string method, string path, bool sendUser)
    {
        await using var host = await Harness.StartAsync();
        var caller = sendUser ? host.UserAssertion() : AppAssertion(host);

        using var response = await host.ControlAsync(new HttpMethod(method), path, ControlToken, caller, method == "PUT" ? Body(Widgets) : null);

        await AssertRejectedAsync(response, HttpStatusCode.BadRequest, GrantReason.PrincipalInBody);
        Assert.AreEqual(0, GrantCount(host));
    }

    // ==== body, URL and branch validation ===========================================================

    [TestMethod]
    [DataRow("[]")]
    [DataRow("\"https://github.com/acme/widgets\"")]
    [DataRow("not json")]
    [DataRow("""{"repository":1}""")]
    [DataRow("""{"repository":"https://github.com/acme/widgets","repository":"https://github.com/acme/gadgets"}""")]
    [DataRow("""{"repository":"https://github.com/acme/widgets","branch":5}""")]
    [DataRow("""{"repository":"https://github.com/acme/widgets","branch":"a","branch":"b"}""")]
    public async Task Put_InvalidBody_Is400(string body)
    {
        await using var host = await Harness.StartAsync();

        using var response = await host.ControlAsync(HttpMethod.Put, SelfPath, ControlToken, host.UserAssertion(), body);

        await AssertRejectedAsync(response, HttpStatusCode.BadRequest, GrantReason.InvalidBody);
        Assert.AreEqual(0, GrantCount(host));
        Assert.AreEqual("put_self;invalid_body" + Suffix, host.Service.RecentAudit(action: AuditAction.Grant).Single().Detail);
    }

    [TestMethod]
    public async Task Put_OversizedBody_Is400()
    {
        await using var host = await Harness.StartAsync();
        var body = $$"""{"repository":"{{Widgets}}","padding":"{{new string('x', GrantEndpoints.MaxBodyBytes)}}"}""";

        using var response = await host.ControlAsync(HttpMethod.Put, SelfPath, ControlToken, host.UserAssertion(), body);

        await AssertRejectedAsync(response, HttpStatusCode.BadRequest, GrantReason.InvalidBody);
        Assert.AreEqual(0, GrantCount(host));
    }

    [TestMethod]
    [DataRow("http://github.com/acme/widgets")]
    [DataRow("https://gitlab.example.test/acme/widgets")]
    [DataRow("https://github.com/acme/widgets?x=1")]
    [DataRow("https://user@github.com/acme/widgets")]
    [DataRow("https://127.0.0.1/acme/widgets")]
    public async Task Put_RefusedUrl_Is400_WithThePolicyReason_AndNoAuditScope(string url)
    {
        await using var host = await Harness.StartAsync();
        var expected = new RepositoryUrlPolicy(["github.com"]).Evaluate(url);
        Assert.IsFalse(expected.Ok);

        using var response = await host.ControlAsync(HttpMethod.Put, SelfPath, ControlToken, host.UserAssertion(), Body(url));

        var raw = await AssertRejectedAsync(response, HttpStatusCode.BadRequest, expected.Reason!);
        Assert.IsFalse(raw.Contains("acme", StringComparison.Ordinal), "the refusal never echoes the URL");
        var audit = host.Service.RecentAudit(action: AuditAction.Grant).Single();
        Assert.AreEqual($"put_self;{expected.Reason}{Suffix}", audit.Detail);
        Assert.IsNull(audit.RepositoryScope, "a refused URL is never stored");
        Assert.AreEqual(0, GrantCount(host));
    }

    [TestMethod]
    [DataRow("*")]
    [DataRow("a b")]
    [DataRow("tab\there")]
    [DataRow("feature/*")]
    [DataRow("256")]
    public async Task Put_BranchNotAllowed_Is400(string branch)
    {
        await using var host = await Harness.StartAsync();
        if (branch == "256")
            branch = new string('b', GrantEndpoints.MaxBranchLength + 1);

        using var response = await host.ControlAsync(HttpMethod.Put, SelfPath, ControlToken, host.UserAssertion(), Body(Widgets, branch));

        await AssertRejectedAsync(response, HttpStatusCode.BadRequest, GrantReason.BranchNotAllowed);
        var audit = host.Service.RecentAudit(action: AuditAction.Grant).Single();
        Assert.AreEqual(RepositoryGrantKey.Of(Widgets), audit.RepositoryScope, "an accepted repository scopes the refusal");
        Assert.AreEqual(0, GrantCount(host));
    }

    // ==== writes ====================================================================================

    [TestMethod]
    public async Task PutSelf_CreatesThenRefreshes_AndAuditsBoth()
    {
        await using var host = await Harness.StartAsync();

        using var first = await host.ControlAsync(HttpMethod.Put, SelfPath, ControlToken, host.UserAssertion(), Body("https://github.com/acme/widgets.git"));
        using var second = await host.ControlAsync(HttpMethod.Put, SelfPath, ControlToken, host.UserAssertion(), Body(Widgets, "  "));

        var created = await JsonAsync(first, HttpStatusCode.OK);
        var refreshed = await JsonAsync(second, HttpStatusCode.OK);
        Assert.IsTrue(created.GetProperty("created").GetBoolean());
        Assert.IsFalse(refreshed.GetProperty("created").GetBoolean(), "a blank branch is the default branch: the same grant");
        var grant = refreshed.GetProperty("grant");
        Assert.AreEqual("https://github.com/acme/widgets.git", grant.GetProperty("repository").GetString(), "the first spelling is kept");
        Assert.AreEqual("", grant.GetProperty("branch").GetString());
        Assert.AreEqual(RepositoryGrantSource.Self, grant.GetProperty("source").GetString());
        Assert.IsTrue(grant.GetProperty("created_at").GetInt64() > 0);

        var audit = host.Service.RecentAudit(action: AuditAction.Grant).OrderBy(a => a.Id).ToList();
        CollectionAssert.AreEqual(new[] { "put_self;created" + Suffix, "put_self;updated" + Suffix }, audit.Select(a => a.Detail).ToList());
        Assert.IsTrue(audit.All(a => a.Outcome == AuditOutcome.Accepted
            && a.Actor == AuditLogStore.HashActor("tenant-a/user-1")
            && a.RepositoryScope == RepositoryGrantKey.Of(Widgets)));
        Assert.AreEqual(1, GrantCount(host));
    }

    [TestMethod]
    public async Task DeleteSelf_OneBranch_OrEveryBranch()
    {
        await using var host = await Harness.StartAsync();
        foreach (var branch in new[] { "", "dev", "rel" })
            await PutSelfAsync(host, host.UserAssertion(), Widgets, branch);

        Assert.AreEqual(1, await DeleteAsync(host, SelfPath + "?repository=https://github.com/acme/widgets&branch=dev", host.UserAssertion()));
        Assert.AreEqual(2, await DeleteAsync(host, SelfPath + "?repository=https://github.com/acme/widgets.git&branch=*", host.UserAssertion()));
        Assert.AreEqual(0, await DeleteAsync(host, SelfPath + "?repository=https://github.com/acme/widgets", host.UserAssertion()), "deleting nothing is not an error");

        using var repeated = await host.ControlAsync(HttpMethod.Delete, SelfPath + "?repository=https://github.com/acme/widgets&repository=https://github.com/acme/gadgets", ControlToken, host.UserAssertion());
        await AssertRejectedAsync(repeated, HttpStatusCode.BadRequest, GrantReason.InvalidBody);
        using var missing = await host.ControlAsync(HttpMethod.Delete, SelfPath, ControlToken, host.UserAssertion());
        Assert.AreEqual(HttpStatusCode.BadRequest, missing.StatusCode);

        var accepted = host.Service.RecentAudit(action: AuditAction.Grant).Where(a => a.Outcome == AuditOutcome.Accepted && a.Detail!.StartsWith("delete", StringComparison.Ordinal))
            .OrderBy(a => a.Id).Select(a => a.Detail).ToList();
        CollectionAssert.AreEqual(new[] { "delete_self;deleted_1" + Suffix, "delete_self;deleted_2" + Suffix, "delete_self;deleted_0" + Suffix }, accepted);
    }

    [TestMethod]
    public async Task DeleteSelf_RevokesOnlyTheCallersOwnGrant()
    {
        await using var host = await Harness.StartAsync();
        await PutSelfAsync(host, host.UserAssertion(sub: "user-1"), Widgets);
        await PutSelfAsync(host, host.UserAssertion(sub: "user-2"), Widgets);
        await PutSelfAsync(host, host.UserAssertion(tenant: "tenant-b", sub: "user-1"), Widgets);

        Assert.AreEqual(1, await DeleteAsync(host, SelfPath + "?repository=https://github.com/acme/widgets&branch=*", host.UserAssertion(sub: "user-1")));

        Assert.AreEqual(2, GrantCount(host));
    }

    [TestMethod]
    public async Task TenantGrant_PutAndDelete_ByTheApplication()
    {
        await using var host = await Harness.StartAsync();

        using var put = await host.ControlAsync(HttpMethod.Put, TenantPath, ControlToken, AppAssertion(host), Body(Widgets, "main"));
        var body = await JsonAsync(put, HttpStatusCode.OK);
        Assert.AreEqual(RepositoryGrantSource.Tenant, body.GetProperty("grant").GetProperty("source").GetString());
        Assert.AreEqual("main", body.GetProperty("grant").GetProperty("branch").GetString());

        Assert.AreEqual(1, await DeleteAsync(host, TenantPath + "?repository=https://github.com/acme/widgets&branch=main", AppAssertion(host)));

        var audit = host.Service.RecentAudit(action: AuditAction.Grant).OrderBy(a => a.Id).ToList();
        CollectionAssert.AreEqual(new[] { "put_tenant;created", "delete_tenant;deleted_1" },
            audit.Select(a => a.Detail![..a.Detail!.IndexOf(";idp=", StringComparison.Ordinal)]).ToList());
        Assert.IsTrue(audit.All(a => a.Actor == AuditLogStore.HashActor("tenant-a/app:sextant")));
        StringAssert.Contains(audit[0].Detail, ";idp=-;kid=kid-a;", "an application caller has no idp");
    }

    [TestMethod]
    public async Task Put_OverTheLimit_Is409_ButARefreshPasses()
    {
        await using var host = await Harness.StartAsync(configure: o => o with { MaxGrantsPerPrincipal = 1 });
        await PutSelfAsync(host, host.UserAssertion(), Widgets);

        using var over = await host.ControlAsync(HttpMethod.Put, SelfPath, ControlToken, host.UserAssertion(), Body(Gadgets));
        using var refresh = await host.ControlAsync(HttpMethod.Put, SelfPath, ControlToken, host.UserAssertion(), Body(Widgets));

        await AssertRejectedAsync(over, HttpStatusCode.Conflict, GrantReason.GrantLimit);
        Assert.IsFalse((await JsonAsync(refresh, HttpStatusCode.OK)).GetProperty("created").GetBoolean());
        Assert.AreEqual(1, GrantCount(host));
        Assert.AreEqual(1, host.Service.RecentAudit(action: AuditAction.Grant).Count(a => a.Detail == "put_self;grant_limit" + Suffix));
    }

    [TestMethod]
    public async Task Put_OverTheTenantLimit_Is409()
    {
        await using var host = await Harness.StartAsync(configure: o => o with { MaxGrantsPerTenant = 1 });
        await PutSelfAsync(host, host.UserAssertion(sub: "user-1"), Widgets);

        using var user = await host.ControlAsync(HttpMethod.Put, SelfPath, ControlToken, host.UserAssertion(sub: "user-2"), Body(Widgets));
        using var tenant = await host.ControlAsync(HttpMethod.Put, TenantPath, ControlToken, AppAssertion(host), Body(Widgets));
        using var otherTenant = await host.ControlAsync(HttpMethod.Put, SelfPath, ControlToken, host.UserAssertion(tenant: "tenant-b"), Body(Widgets));

        await AssertRejectedAsync(user, HttpStatusCode.Conflict, GrantReason.GrantLimit);
        await AssertRejectedAsync(tenant, HttpStatusCode.Conflict, GrantReason.GrantLimit);
        Assert.AreEqual(HttpStatusCode.OK, otherTenant.StatusCode, "each tenant has its own limit");
    }

    // ==== listings ==================================================================================

    [TestMethod]
    public async Task GetSelf_ListsTheCallersOwnGrants_WithTheirStatus()
    {
        await using var host = await Harness.StartAsync();
        await PutSelfAsync(host, host.UserAssertion(), Widgets);
        await PutSelfAsync(host, host.UserAssertion(), Gadgets, "dev");
        await PutSelfAsync(host, host.UserAssertion(sub: "user-2"), Gizmos);
        using (var tenant = await host.ControlAsync(HttpMethod.Put, TenantPath, ControlToken, AppAssertion(host), Body(Gizmos)))
            Assert.AreEqual(HttpStatusCode.OK, tenant.StatusCode);

        using var response = await host.ControlAsync(HttpMethod.Get, SelfPath, ControlToken, host.UserAssertion());

        var body = await JsonAsync(response, HttpStatusCode.OK);
        Assert.IsFalse(body.GetRawText().Contains("user-", StringComparison.Ordinal), "the listing does not echo a principal");
        Assert.AreEqual(2, body.GetProperty("result_count").GetInt32(), "only the caller's own grants: not another user's, not the tenant's");
        var grants = body.GetProperty("grants").EnumerateArray().ToList();
        var gadgets = grants.Single(g => g.GetProperty("repository").GetString() == Gadgets);
        Assert.AreEqual("dev", gadgets.GetProperty("branch").GetString());
        Assert.AreEqual(GrantSnapshotStatus.Missing, gadgets.GetProperty("status").GetProperty("snapshot_status").GetString());
        var widgets = grants.Single(g => g.GetProperty("repository").GetString() == Widgets).GetProperty("status");
        Assert.AreEqual(GrantSnapshotStatus.Complete, widgets.GetProperty("snapshot_status").GetString());
        Assert.AreEqual("main", widgets.GetProperty("resolved_branch").GetString());
        Assert.AreEqual(host.WidgetsHash, widgets.GetProperty("identity_hash").GetString());
        Assert.IsTrue(widgets.GetProperty("published_at").GetInt64() > 0);
    }

    [TestMethod]
    public async Task GetTargets_AreDistinctTargetsWithCounts_AndNoUserIds()
    {
        await using var host = await Harness.StartAsync();
        await PutSelfAsync(host, host.UserAssertion(sub: "user-alpha"), Widgets);
        await PutSelfAsync(host, host.UserAssertion(sub: "user-bravo"), Widgets);
        await PutSelfAsync(host, host.UserAssertion(sub: "user-alpha"), Widgets, "dev");
        using (var tenant = await host.ControlAsync(HttpMethod.Put, TenantPath, ControlToken, AppAssertion(host), Body(Widgets)))
            Assert.AreEqual(HttpStatusCode.OK, tenant.StatusCode);
        await PutSelfAsync(host, host.UserAssertion(tenant: "tenant-b", sub: "user-charlie"), Gadgets);

        using var response = await host.ControlAsync(HttpMethod.Get, TargetsPath, ControlToken, AppAssertion(host));

        var raw = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, raw);
        foreach (var secret in new[] { "user-alpha", "user-bravo", "user-charlie", "tenant-a", "tenant-b", "principal", "gadgets" })
            Assert.IsFalse(raw.Contains(secret, StringComparison.Ordinal), $"reconcile targets must not carry '{secret}': {raw}");
        using var document = JsonDocument.Parse(raw);
        var targets = document.RootElement.GetProperty("targets").EnumerateArray().ToList();
        Assert.AreEqual(2, document.RootElement.GetProperty("result_count").GetInt32());
        Assert.AreEqual("", targets[0].GetProperty("branch").GetString());
        Assert.AreEqual(Widgets, targets[0].GetProperty("repository").GetString());
        Assert.AreEqual(3, targets[0].GetProperty("watchers").GetInt32());
        CollectionAssert.AreEqual(new[] { "self", "tenant" }, targets[0].GetProperty("sources").EnumerateArray().Select(s => s.GetString()).ToList());
        Assert.AreEqual(("dev", 1), (targets[1].GetProperty("branch").GetString(), targets[1].GetProperty("watchers").GetInt32()));
    }

    [TestMethod]
    [DataRow("/control/grants")]
    [DataRow("/control/grants?scope=self")]
    [DataRow("/control/grants?scope=Tenant")]
    public async Task GetTargets_WithoutScopeTenant_Is400(string path)
    {
        await using var host = await Harness.StartAsync();

        using var response = await host.ControlAsync(HttpMethod.Get, path, ControlToken, AppAssertion(host));

        await AssertRejectedAsync(response, HttpStatusCode.BadRequest, GrantReason.InvalidScope);
    }

    // ==== visibility ================================================================================

    [TestMethod]
    public async Task Grant_OpensTheRepository_AndRevocationClosesItOnTheNextCall()
    {
        await using var host = await Harness.StartAsync();
        const string args = """{"name":"global::App.Type0","repository":"acme/widgets"}""";

        var before = await host.CallAsync("find_symbol", args, DelegateToken, host.UserAssertion());
        await PutSelfAsync(host, host.UserAssertion(), Widgets);
        var granted = await host.CallAsync("find_symbol", args, DelegateToken, host.UserAssertion());
        await DeleteAsync(host, SelfPath + "?repository=https://github.com/acme/widgets", host.UserAssertion());
        var revoked = await host.CallAsync("find_symbol", args, DelegateToken, host.UserAssertion());

        Assert.AreEqual(NotFound, WithoutTimestamp(before.Body));
        Assert.IsTrue(granted.Body.GetProperty("results").GetArrayLength() > 0, granted.Body.ToString());
        Assert.AreEqual(NotFound, WithoutTimestamp(revoked.Body), "a revocation takes effect on the next call");
    }

    [TestMethod]
    public async Task CrossTenant_TheSameSubjectUnderAnotherTenant_SeesNothing()
    {
        await using var host = await Harness.StartAsync();
        await PutSelfAsync(host, host.UserAssertion(tenant: "tenant-a", sub: "user-1"), Widgets);
        const string args = """{"name":"global::App.Type0","repository":"acme/widgets"}""";

        var ownTenant = await host.CallAsync("find_symbol", args, DelegateToken, host.UserAssertion(tenant: "tenant-a", sub: "user-1"));
        var otherTenant = await host.CallAsync("find_symbol", args, DelegateToken, host.UserAssertion(tenant: "tenant-b", sub: "user-1"));
        var listed = await host.CallAsync("list_repositories", "{}", DelegateToken, host.UserAssertion(tenant: "tenant-b", sub: "user-1"));
        using var page = await host.SnapshotPageAsync(DelegateToken, host.WidgetsHash, host.UserAssertion(tenant: "tenant-b", sub: "user-1"));

        Assert.IsTrue(ownTenant.Body.GetProperty("results").GetArrayLength() > 0);
        Assert.AreEqual(NotFound, WithoutTimestamp(otherTenant.Body));
        Assert.AreEqual(0, listed.Body.GetProperty("repositories").GetArrayLength());
        Assert.AreEqual(HttpStatusCode.NotFound, page.StatusCode);
    }

    [TestMethod]
    public async Task Application_SeesOnlyTenantWideGrants_AndEveryUserSeesThem()
    {
        await using var host = await Harness.StartAsync();
        await PutSelfAsync(host, host.UserAssertion(sub: "user-1"), Widgets);
        const string args = """{"name":"global::App.Type0","repository":"acme/widgets"}""";

        var appBefore = await host.CallAsync("find_symbol", args, DelegateToken, AppAssertion(host));
        var otherUserBefore = await host.CallAsync("find_symbol", args, DelegateToken, host.UserAssertion(sub: "user-2"));
        using (var tenant = await host.ControlAsync(HttpMethod.Put, TenantPath, ControlToken, AppAssertion(host), Body(Widgets)))
            Assert.AreEqual(HttpStatusCode.OK, tenant.StatusCode);
        var appAfter = await host.CallAsync("find_symbol", args, DelegateToken, AppAssertion(host));
        var otherUserAfter = await host.CallAsync("find_symbol", args, DelegateToken, host.UserAssertion(sub: "user-2"));

        Assert.AreEqual(NotFound, WithoutTimestamp(appBefore.Body), "a user's own grant does not open the repository to the application");
        Assert.AreEqual(NotFound, WithoutTimestamp(otherUserBefore.Body), "nor to another user");
        Assert.IsTrue(appAfter.Body.GetProperty("results").GetArrayLength() > 0);
        Assert.IsTrue(otherUserAfter.Body.GetProperty("results").GetArrayLength() > 0, "a tenant-wide grant opens it to every user");
    }

    [TestMethod]
    public async Task UngrantedRepository_IsTheUniformNotFound_LikeAnAbsentOne()
    {
        await using var host = await Harness.StartAsync(seed: db => GrantServiceTests.PublishOnBranch(db, Gadgets, "commit-g1", "main", isDefault: true));
        await PutSelfAsync(host, host.UserAssertion(), Widgets);

        var ungranted = await host.CallAsync("find_symbol", """{"name":"global::App.Type0","repository":"acme/gadgets"}""", DelegateToken, host.UserAssertion());
        var absent = await host.CallAsync("find_symbol", """{"name":"global::App.Type0","repository":"acme/absent"}""", DelegateToken, host.UserAssertion());
        var branch = await host.CallAsync("find_symbol", """{"name":"global::App.Type0","repository":"acme/gadgets","branch":"main"}""", DelegateToken, host.UserAssertion());

        Assert.AreEqual(NotFound, WithoutTimestamp(ungranted.Body), "an ungranted, published repository reads as not found");
        Assert.AreEqual(NotFound, WithoutTimestamp(absent.Body), "exactly like one that does not exist");
        Assert.AreEqual(NotFound, WithoutTimestamp(branch.Body));
    }

    [TestMethod]
    public async Task SnapshotPage_FollowsTheGrant()
    {
        await using var host = await Harness.StartAsync();

        using var before = await host.SnapshotPageAsync(DelegateToken, host.WidgetsHash, host.UserAssertion());
        await PutSelfAsync(host, host.UserAssertion(), Widgets);
        using var granted = await host.SnapshotPageAsync(DelegateToken, host.WidgetsHash, host.UserAssertion());
        await DeleteAsync(host, SelfPath + "?repository=https://github.com/acme/widgets", host.UserAssertion());
        using var revoked = await host.SnapshotPageAsync(DelegateToken, host.WidgetsHash, host.UserAssertion());

        Assert.AreEqual(HttpStatusCode.NotFound, before.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, granted.StatusCode, await granted.Content.ReadAsStringAsync());
        Assert.AreEqual(HttpStatusCode.NotFound, revoked.StatusCode);
    }

    [TestMethod]
    public async Task CrossRepositoryTool_DropsUngrantedConsumers_AndAnUngrantedProviderIsNotFound()
    {
        await using var host = await Harness.StartAsync(seed: db =>
        {
            var provider = new SnapshotStore(db.GetConnection()).GetSelectedSnapshotIdForRepository(Widgets)!.Value;
            GrantServiceTests.SeedConsumer(db, Gadgets, "commit-g1", provider);
            GrantServiceTests.SeedConsumer(db, Gizmos, "commit-z1", provider);
        });
        var args = $$"""{"provider_repository_url":"{{Widgets}}"}""";
        await PutSelfAsync(host, host.UserAssertion(sub: "user-1"), Widgets);
        await PutSelfAsync(host, host.UserAssertion(sub: "user-1"), Gadgets);
        await PutSelfAsync(host, host.UserAssertion(sub: "user-2"), Gadgets);
        await PutSelfAsync(host, host.UserAssertion(sub: "user-2"), Gizmos);

        var granted = await host.CallAsync("find_submodule_consumers", args, DelegateToken, host.UserAssertion(sub: "user-1"));
        var noProvider = await host.CallAsync("find_submodule_consumers", args, DelegateToken, host.UserAssertion(sub: "user-2"));

        CollectionAssert.AreEqual(new[] { Gadgets },
            granted.Body.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("consumer_repository").GetString()).ToList(),
            $"an ungranted consumer is dropped: {granted.Body}");
        Assert.AreEqual(NotFound, WithoutTimestamp(noProvider.Body), "a caller who cannot read the provider learns nothing about it");
    }

    // ==== implicit selection ========================================================================

    [TestMethod]
    public async Task ImplicitSelection_TheOneVisibleRepository_IsSelected()
    {
        await using var host = await Harness.StartAsync();
        await PutSelfAsync(host, host.UserAssertion(), Widgets);
        await PutSelfAsync(host, host.UserAssertion(), "https://github.com/acme/unindexed");

        var call = await host.CallAsync("find_symbol", FindSymbolArguments, DelegateToken, host.UserAssertion());

        Assert.IsTrue(call.Body.GetProperty("results").GetArrayLength() > 0,
            $"a caller that can read one indexed repository need not name it: {call.Body}");
    }

    [TestMethod]
    public async Task ImplicitSelection_SeveralVisibleRepositories_OrABranchAlone_RequireARepository()
    {
        await using var host = await Harness.StartAsync(seed: db => GrantServiceTests.PublishOnBranch(db, Gadgets, "commit-g1", "main", isDefault: true));
        await PutSelfAsync(host, host.UserAssertion(sub: "user-1"), Widgets);
        await PutSelfAsync(host, host.UserAssertion(sub: "user-1"), Gadgets);
        await PutSelfAsync(host, host.UserAssertion(sub: "user-2"), Widgets);

        var several = await host.CallAsync("find_symbol", FindSymbolArguments, DelegateToken, host.UserAssertion(sub: "user-1"));
        var branchOnly = await host.CallAsync("find_symbol", """{"name":"global::App.Type0","branch":"main"}""", DelegateToken, host.UserAssertion(sub: "user-2"));
        var none = await host.CallAsync("find_symbol", FindSymbolArguments, DelegateToken, host.UserAssertion(sub: "user-3"));

        Assert.AreEqual(ResponseBuilder.RepositoryRequiredCode, ErrorCode(several.Body), several.Body.ToString());
        Assert.AreEqual(ResponseBuilder.RepositoryRequiredCode, ErrorCode(branchOnly.Body), "a branch alone never picks a repository");
        Assert.AreEqual(ResponseBuilder.RepositoryRequiredCode, ErrorCode(none.Body));
    }

    // ==== ensure and status gates ===================================================================

    [TestMethod]
    public async Task Ensure_UserCaller_WithoutAGrant_Is403NotGranted_AndAudited()
    {
        await using var host = await Harness.StartAsync();

        using var refused = await host.ControlAsync(HttpMethod.Post, "/control/ensure", ControlToken, host.UserAssertion(), EnsureBody());

        await AssertRejectedAsync(refused, HttpStatusCode.Forbidden, GrantReason.NotGranted);
        Assert.AreEqual(0, host.Worker.Calls, "nothing is indexed for an ungranted user");
        var audit = host.Service.RecentAudit(action: AuditAction.Ensure).Single();
        Assert.AreEqual(AuditOutcome.Denied, audit.Outcome);
        Assert.AreEqual(GrantReason.NotGranted + Suffix, audit.Detail);
        Assert.AreEqual(RepositoryGrantKey.Of(Gadgets), audit.RepositoryScope);
        Assert.AreEqual(AuditLogStore.HashActor("tenant-a/user-1"), audit.Actor);
    }

    [TestMethod]
    public async Task Ensure_UserCaller_WithItsOwnOrTheTenantsGrant_Proceeds()
    {
        await using var host = await Harness.StartAsync();
        await PutSelfAsync(host, host.UserAssertion(sub: "user-1"), Gadgets);

        using var own = await host.ControlAsync(HttpMethod.Post, "/control/ensure", ControlToken, host.UserAssertion(sub: "user-1"), EnsureBody());
        using var other = await host.ControlAsync(HttpMethod.Post, "/control/ensure", ControlToken, host.UserAssertion(sub: "user-2"), EnsureBody());
        using (var tenant = await host.ControlAsync(HttpMethod.Put, TenantPath, ControlToken, AppAssertion(host), Body(Gadgets)))
            Assert.AreEqual(HttpStatusCode.OK, tenant.StatusCode);
        using var viaTenant = await host.ControlAsync(HttpMethod.Post, "/control/ensure", ControlToken, host.UserAssertion(sub: "user-2"), EnsureBody());

        Assert.AreEqual(HttpStatusCode.OK, own.StatusCode, await own.Content.ReadAsStringAsync());
        await AssertRejectedAsync(other, HttpStatusCode.Forbidden, GrantReason.NotGranted);
        Assert.AreEqual(HttpStatusCode.OK, viaTenant.StatusCode, await viaTenant.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task Ensure_ApplicationCaller_AndNoCaller_AreNotGrantGated()
    {
        await using var host = await Harness.StartAsync();

        using var application = await host.ControlAsync(HttpMethod.Post, "/control/ensure", ControlToken, AppAssertion(host), EnsureBody());
        using var operatorCall = await host.ControlAsync(HttpMethod.Post, "/control/ensure", ControlToken, jsonBody: EnsureBody());

        Assert.AreEqual(HttpStatusCode.OK, application.StatusCode, await application.Content.ReadAsStringAsync());
        Assert.AreEqual(HttpStatusCode.OK, operatorCall.StatusCode, await operatorCall.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task Status_UserCaller_SeesOnlyAJobOnAVisibleRepository()
    {
        await using var host = await Harness.StartAsync();
        using var ensure = await host.ControlAsync(HttpMethod.Post, "/control/ensure", ControlToken, jsonBody: EnsureBody());
        var jobId = (await JsonAsync(ensure, HttpStatusCode.OK)).GetProperty("job_id").GetInt64();
        var path = $"/control/status/{jobId}";

        using var ungranted = await host.ControlAsync(HttpMethod.Get, path, ControlToken, host.UserAssertion());
        using var unknown = await host.ControlAsync(HttpMethod.Get, "/control/status/999999", ControlToken, host.UserAssertion());
        using var application = await host.ControlAsync(HttpMethod.Get, path, ControlToken, AppAssertion(host));
        using var operatorCall = await host.ControlAsync(HttpMethod.Get, path, ControlToken);
        await PutSelfAsync(host, host.UserAssertion(), Gadgets);
        using var granted = await host.ControlAsync(HttpMethod.Get, path, ControlToken, host.UserAssertion());

        Assert.AreEqual(HttpStatusCode.NotFound, ungranted.StatusCode);
        Assert.AreEqual(await unknown.Content.ReadAsStringAsync(), await ungranted.Content.ReadAsStringAsync(),
            "an ungranted job reads exactly like an unknown id");
        Assert.AreEqual(HttpStatusCode.OK, application.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, operatorCall.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, granted.StatusCode);
    }

    // ==== list_repositories =========================================================================

    [TestMethod]
    public async Task ListRepositories_ListsTheCallersVisibleRepositories()
    {
        await using var host = await Harness.StartAsync();
        await PutSelfAsync(host, host.UserAssertion(), Widgets);
        using (var tenant = await host.ControlAsync(HttpMethod.Put, TenantPath, ControlToken, AppAssertion(host), Body(Gadgets, "release")))
            Assert.AreEqual(HttpStatusCode.OK, tenant.StatusCode);
        await PutSelfAsync(host, host.UserAssertion(sub: "user-2"), Gizmos);

        var call = await host.CallAsync("list_repositories", "{}", DelegateToken, host.UserAssertion());

        Assert.IsFalse(call.IsError, call.Body.ToString());
        var repositories = call.Body.GetProperty("repositories").EnumerateArray().ToList();
        Assert.AreEqual(2, repositories.Count, $"another user's grant is not listed: {call.Body}");
        Assert.AreEqual(2, call.Body.GetProperty("meta").GetProperty("result_count").GetInt32());
        Assert.IsTrue(call.Body.GetProperty("meta").GetProperty("index_freshness").GetInt64() > 0);

        var widgets = repositories.Single(r => r.GetProperty("repository").GetString() == Widgets);
        var main = widgets.GetProperty("branches").EnumerateArray().Single();
        Assert.AreEqual("main", main.GetProperty("branch").GetString());
        Assert.IsTrue(main.GetProperty("is_default").GetBoolean());
        Assert.AreEqual(GrantSnapshotStatus.Complete, main.GetProperty("status").GetString());
        CollectionAssert.AreEqual(new[] { "self" }, widgets.GetProperty("sources").EnumerateArray().Select(s => s.GetString()).ToList());

        var gadgets = repositories.Single(r => r.GetProperty("repository").GetString() == Gadgets);
        var release = gadgets.GetProperty("branches").EnumerateArray().Single();
        Assert.AreEqual(("release", false, GrantSnapshotStatus.Missing),
            (release.GetProperty("branch").GetString(), release.GetProperty("is_default").GetBoolean(), release.GetProperty("status").GetString()));
        Assert.IsFalse(call.Body.ToString().Contains("user-", StringComparison.Ordinal), "the listing names no principal");
    }

    [TestMethod]
    public async Task ListRepositories_WithoutAVerifiedCaller_IsCallerRequired()
    {
        await using var host = await Harness.StartAsync();

        var legacy = await host.CallAsync("list_repositories", "{}", QueryToken);

        Assert.AreEqual(ListRepositoriesTool.CallerRequiredCode, ErrorCode(legacy.Body), legacy.Body.ToString());
    }

    [TestMethod]
    public async Task ListRepositories_IsExemptFromTheReservedSelectorArguments()
    {
        await using var host = await Harness.StartAsync();

        var list = await host.RpcAsync("tools/list", "{}", DelegateToken);

        var tool = list.Result!.Value.GetProperty("tools").EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "list_repositories");
        var schema = tool.GetProperty("inputSchema");
        Assert.IsFalse(schema.TryGetProperty("properties", out var properties)
            && (properties.TryGetProperty("repository", out _) || properties.TryGetProperty("branch", out _)),
            $"list_repositories takes no selection: {schema}");
        CollectionAssert.Contains(ToolSelectionFilters.SelectionExemptTools.ToList(), "list_repositories");
    }

    // ==== review additions: fail-closed, cross-tenant, uniform not-found, header, concurrency, resolve ==============

    [TestMethod]
    public async Task StoreUnavailable_GrantWritesAndEnsureNotGranted_Are503_AndWriteNothing()
    {
        await using var host = await Harness.StartAsync(configure: o => o with { LeaseTtl = TimeSpan.FromSeconds(3) });
        await PutSelfAsync(host, host.UserAssertion(sub: "user-1"), Widgets);
        var auditBefore = host.Service.RecentAudit(action: AuditAction.Grant).Count;
        await host.StealLeaseAsync();

        using var put = await host.ControlAsync(HttpMethod.Put, SelfPath, ControlToken, host.UserAssertion(sub: "user-1"), Body(Gadgets));
        using var tenantPut = await host.ControlAsync(HttpMethod.Put, TenantPath, ControlToken, AppAssertion(host), Body(Gadgets));
        using var delete = await host.ControlAsync(HttpMethod.Delete, SelfPath + "?repository=https://github.com/acme/widgets",
            ControlToken, host.UserAssertion(sub: "user-1"));
        using var ensure = await host.ControlAsync(HttpMethod.Post, "/control/ensure", ControlToken, host.UserAssertion(sub: "user-2"), EnsureBody());

        foreach (var response in new[] { put, tenantPut, delete, ensure })
        {
            var raw = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode, raw);
            Assert.AreEqual("unavailable", JsonDocument.Parse(raw).RootElement.GetProperty("status").GetString(), raw);
        }
        Assert.AreEqual(1, GrantCount(host), "no grant was written and none was revoked");
        Assert.AreEqual(auditBefore, host.Service.RecentAudit(action: AuditAction.Grant).Count, "no grant audit row was written");
        Assert.AreEqual(0, host.Service.RecentAudit(action: AuditAction.Ensure).Count, "no not_granted row was written");
        Assert.AreEqual(0, host.Worker.Calls, "nothing was indexed for the ungranted user");
    }

    [TestMethod]
    public async Task VisibilityReadFailure_FailsClosed_OnEveryReadSurface()
    {
        await using var host = await Harness.StartAsync();
        await PutSelfAsync(host, host.UserAssertion(), Widgets);
        const string args = """{"name":"global::App.Type0","repository":"acme/widgets"}""";
        var granted = await host.CallAsync("find_symbol", args, DelegateToken, host.UserAssertion());
        Assert.IsTrue(granted.Body.GetProperty("results").GetArrayLength() > 0, granted.Body.ToString());

        // The grant catalog becomes unreadable: every visibility read now throws.
        host.ExecuteOnCatalog("ALTER TABLE repository_grants RENAME TO repository_grants_unreadable;");

        foreach (var (tool, arguments) in new[] { ("find_symbol", args), ("find_symbol", FindSymbolArguments), ("list_repositories", "{}") })
        {
            var (status, raw, _, payload) = await host.SendRpcAsync("tools/call",
                $$"""{"name":"{{tool}}","arguments":{{arguments}}}""", DelegateToken, host.UserAssertion());
            Assert.IsTrue(status != HttpStatusCode.OK || IsFailure(payload), $"{tool} must fail: {raw}");
            Assert.IsFalse(payload.Contains("Type0", StringComparison.Ordinal) || payload.Contains("acme", StringComparison.OrdinalIgnoreCase),
                $"{tool} returned data: {raw}");
        }
        Assert.AreNotEqual(HttpStatusCode.OK, await StatusOrNullAsync(() => host.SnapshotPageAsync(DelegateToken, host.WidgetsHash, host.UserAssertion())));
        Assert.AreNotEqual(HttpStatusCode.OK, await StatusOrNullAsync(() => host.ControlAsync(HttpMethod.Get,
            "/control/resolve?repository=https://github.com/acme/widgets", ControlToken, host.UserAssertion())));
        var ensure = await StatusOrNullAsync(() => host.ControlAsync(HttpMethod.Post, "/control/ensure", ControlToken, host.UserAssertion(), EnsureBody()));
        Assert.IsFalse(ensure is HttpStatusCode.OK or HttpStatusCode.Accepted, $"an ensure must not proceed: {ensure}");
        Assert.AreEqual(0, host.Worker.Calls);

        static bool IsFailure(string payload)
        {
            using var rpc = JsonDocument.Parse(payload);
            return rpc.RootElement.TryGetProperty("error", out _)
                || (rpc.RootElement.TryGetProperty("result", out var result)
                    && result.TryGetProperty("isError", out var isError) && isError.GetBoolean());
        }
    }

    [TestMethod]
    public async Task FindCrossRepositoryUsages_UngrantedConsumersAreDropped()
    {
        await using var host = await Harness.StartAsync(seed: SeedUsages);
        var args = $$"""{"provider_repository_url":"{{Widgets}}","symbol_fqn":"global::App.Type0"}""";
        await PutSelfAsync(host, host.UserAssertion(sub: "user-1"), Widgets);
        await PutSelfAsync(host, host.UserAssertion(sub: "user-1"), Gadgets);
        foreach (var repository in new[] { Widgets, Gadgets, Gizmos })
        {
            using var tenant = await host.ControlAsync(HttpMethod.Put, TenantPath, ControlToken, AppAssertion(host), Body(repository));
            Assert.AreEqual(HttpStatusCode.OK, tenant.StatusCode);
        }

        var everything = await host.CallAsync("find_cross_repository_usages", args, DelegateToken, AppAssertion(host));
        var otherTenant = await host.CallAsync("find_cross_repository_usages", args, DelegateToken, host.UserAssertion(tenant: "tenant-b", sub: "user-1"));
        using (var revoke = await host.ControlAsync(HttpMethod.Delete, TenantPath + "?repository=https://github.com/acme/gizmos", ControlToken, AppAssertion(host)))
            Assert.AreEqual(HttpStatusCode.OK, revoke.StatusCode);
        var ownGrants = await host.CallAsync("find_cross_repository_usages", args, DelegateToken, host.UserAssertion(sub: "user-1"));

        CollectionAssert.AreEquivalent(new[] { Gadgets, Gizmos }, Consumers(everything.Body), $"both consumers use the symbol: {everything.Body}");
        Assert.AreEqual(NotFound, WithoutTimestamp(otherTenant.Body), "another tenant's grants open nothing");
        CollectionAssert.AreEquivalent(new[] { Gadgets }, Consumers(ownGrants.Body),
            $"an ungranted consumer contributes no row: {ownGrants.Body}");
        Assert.IsFalse(ownGrants.Body.ToString().Contains("gizmos", StringComparison.OrdinalIgnoreCase));

        static List<string?> Consumers(JsonElement body) =>
            body.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("consumer_repository").GetString()).ToList();
    }

    [TestMethod]
    public async Task FindCrossRepositoryUsages_UngrantedProvider_IsTheUniformNotFound_LikeAnAbsentOne()
    {
        await using var host = await Harness.StartAsync(seed: SeedUsages);
        await PutSelfAsync(host, host.UserAssertion(), Gadgets);
        await PutSelfAsync(host, host.UserAssertion(), Gizmos);

        var ungranted = await host.CallAsync("find_cross_repository_usages",
            $$"""{"provider_repository_url":"{{Widgets}}","symbol_fqn":"global::App.Type0"}""", DelegateToken, host.UserAssertion());
        var absent = await host.CallAsync("find_cross_repository_usages",
            """{"provider_repository_url":"https://github.com/acme/absent","symbol_fqn":"global::App.Type0"}""", DelegateToken, host.UserAssertion());

        Assert.AreEqual(NotFound, WithoutTimestamp(ungranted.Body), "a caller who cannot read the provider learns nothing about it");
        Assert.AreEqual(WithoutTimestamp(absent.Body), WithoutTimestamp(ungranted.Body));
    }

    [TestMethod]
    public async Task TenantGrant_Revoked_TakesEffectOnTheNextCall()
    {
        await using var host = await Harness.StartAsync();
        const string args = """{"name":"global::App.Type0","repository":"acme/widgets"}""";
        using (var tenant = await host.ControlAsync(HttpMethod.Put, TenantPath, ControlToken, AppAssertion(host), Body(Widgets)))
            Assert.AreEqual(HttpStatusCode.OK, tenant.StatusCode);

        var userGranted = await host.CallAsync("find_symbol", args, DelegateToken, host.UserAssertion(sub: "user-2"));
        var appGranted = await host.CallAsync("find_symbol", args, DelegateToken, AppAssertion(host));
        Assert.AreEqual(1, await DeleteAsync(host, TenantPath + "?repository=https://github.com/acme/widgets", AppAssertion(host)));
        var userRevoked = await host.CallAsync("find_symbol", args, DelegateToken, host.UserAssertion(sub: "user-2"));
        var appRevoked = await host.CallAsync("find_symbol", args, DelegateToken, AppAssertion(host));
        var listed = await host.CallAsync("list_repositories", "{}", DelegateToken, host.UserAssertion(sub: "user-2"));
        using var page = await host.SnapshotPageAsync(DelegateToken, host.WidgetsHash, host.UserAssertion(sub: "user-2"));

        Assert.IsTrue(userGranted.Body.GetProperty("results").GetArrayLength() > 0, userGranted.Body.ToString());
        Assert.IsTrue(appGranted.Body.GetProperty("results").GetArrayLength() > 0, appGranted.Body.ToString());
        Assert.AreEqual(NotFound, WithoutTimestamp(userRevoked.Body), "a tenant-wide revocation closes the repository to users");
        Assert.AreEqual(NotFound, WithoutTimestamp(appRevoked.Body), "and to the application");
        Assert.AreEqual(0, listed.Body.GetProperty("repositories").GetArrayLength());
        Assert.AreEqual(HttpStatusCode.NotFound, page.StatusCode);
    }

    [TestMethod]
    public async Task TenantGrant_IsInvisibleToAnotherTenant_UserAndApplication()
    {
        await using var host = await Harness.StartAsync();
        const string args = """{"name":"global::App.Type0","repository":"acme/widgets"}""";
        using (var tenant = await host.ControlAsync(HttpMethod.Put, TenantPath, ControlToken, AppAssertion(host, "tenant-a"), Body(Widgets)))
            Assert.AreEqual(HttpStatusCode.OK, tenant.StatusCode);
        var otherUser = host.UserAssertion(tenant: "tenant-b", sub: "user-1");
        var otherApp = AppAssertion(host, "tenant-b");

        var ownUser = await host.CallAsync("find_symbol", args, DelegateToken, host.UserAssertion(tenant: "tenant-a", sub: "user-1"));
        foreach (var assertion in new[] { otherUser, otherApp })
        {
            var read = await host.CallAsync("find_symbol", args, DelegateToken, assertion);
            var listed = await host.CallAsync("list_repositories", "{}", DelegateToken, assertion);
            using var page = await host.SnapshotPageAsync(DelegateToken, host.WidgetsHash, assertion);
            Assert.AreEqual(NotFound, WithoutTimestamp(read.Body));
            Assert.AreEqual(0, listed.Body.GetProperty("repositories").GetArrayLength(), listed.Body.ToString());
            Assert.AreEqual(HttpStatusCode.NotFound, page.StatusCode);
        }
        using var targets = await host.ControlAsync(HttpMethod.Get, TargetsPath, ControlToken, otherApp);
        using var ownList = await host.ControlAsync(HttpMethod.Get, SelfPath, ControlToken, otherUser);

        Assert.IsTrue(ownUser.Body.GetProperty("results").GetArrayLength() > 0, "the granting tenant's users see it");
        Assert.AreEqual(0, (await JsonAsync(targets, HttpStatusCode.OK)).GetProperty("targets").GetArrayLength());
        Assert.AreEqual(0, (await JsonAsync(ownList, HttpStatusCode.OK)).GetProperty("grants").GetArrayLength());
    }

    [TestMethod]
    public async Task GetIndexStatus_Ungranted_IsByteIdenticalToAbsent()
    {
        await using var host = await Harness.StartAsync(seed: db => GrantServiceTests.PublishOnBranch(db, Gadgets, "commit-g1", "main", isDefault: true));
        await PutSelfAsync(host, host.UserAssertion(), Widgets);

        var granted = await host.CallAsync("get_index_status", """{"repository":"acme/widgets"}""", DelegateToken, host.UserAssertion());
        var ungranted = await host.CallAsync("get_index_status", """{"repository":"acme/gadgets"}""", DelegateToken, host.UserAssertion());
        var absent = await host.CallAsync("get_index_status", """{"repository":"acme/absent"}""", DelegateToken, host.UserAssertion());

        Assert.AreNotEqual(NotFound, WithoutTimestamp(granted.Body), granted.Body.ToString());
        Assert.AreEqual(NotFound, WithoutTimestamp(ungranted.Body));
        Assert.AreEqual(WithoutTimestamp(absent.Body), WithoutTimestamp(ungranted.Body),
            "an ungranted, published repository's status reads exactly like an absent one");
        Assert.AreEqual(absent.IsError, ungranted.IsError);
    }

    [TestMethod]
    public async Task SnapshotPage_Ungranted_IsByteIdenticalToAbsent()
    {
        await using var host = await Harness.StartAsync();
        var absentHash = new string('0', host.WidgetsHash.Length);

        using var ungranted = await host.SnapshotPageAsync(DelegateToken, host.WidgetsHash, host.UserAssertion());
        using var absent = await host.SnapshotPageAsync(DelegateToken, absentHash, host.UserAssertion());

        Assert.AreEqual(HttpStatusCode.NotFound, ungranted.StatusCode);
        Assert.AreEqual(absent.StatusCode, ungranted.StatusCode);
        Assert.AreEqual(await absent.Content.ReadAsStringAsync(), await ungranted.Content.ReadAsStringAsync());
        Assert.AreEqual(absent.Content.Headers.ContentType?.ToString(), ungranted.Content.Headers.ContentType?.ToString());
        Assert.AreEqual(absent.Content.Headers.ContentLength, ungranted.Content.Headers.ContentLength);
    }

    [TestMethod]
    public async Task RepositoryHeader_NamingAnUngrantedRepository_IsTheUniformNotFound()
    {
        await using var host = await Harness.StartAsync(seed: db => GrantServiceTests.PublishOnBranch(db, Gadgets, "commit-g1", "main", isDefault: true));
        await PutSelfAsync(host, host.UserAssertion(), Widgets);

        host.RepositoryHeader = Gadgets;
        var ungranted = await host.CallAsync("find_symbol", FindSymbolArguments, DelegateToken, host.UserAssertion());
        host.RepositoryHeader = "https://github.com/acme/absent";
        var absent = await host.CallAsync("find_symbol", FindSymbolArguments, DelegateToken, host.UserAssertion());
        host.RepositoryHeader = Widgets;
        var granted = await host.CallAsync("find_symbol", FindSymbolArguments, DelegateToken, host.UserAssertion());

        Assert.AreEqual(NotFound, WithoutTimestamp(ungranted.Body), "the header never bypasses the grant");
        Assert.AreEqual(WithoutTimestamp(absent.Body), WithoutTimestamp(ungranted.Body));
        Assert.IsTrue(granted.Body.GetProperty("results").GetArrayLength() > 0, granted.Body.ToString());
    }

    [TestMethod]
    public async Task RepositoryHeader_AndArgument_AgreeingIsAccepted_AndADisagreementIsAConflict()
    {
        await using var host = await Harness.StartAsync(seed: db => GrantServiceTests.PublishOnBranch(db, Gadgets, "commit-g1", "main", isDefault: true));
        await PutSelfAsync(host, host.UserAssertion(), Widgets);

        host.RepositoryHeader = Widgets;
        var sameRepository = await host.CallAsync("find_symbol", """{"name":"global::App.Type0","repository":"Acme/Widgets.git"}""",
            DelegateToken, host.UserAssertion());
        var argumentUngranted = await host.CallAsync("find_symbol", """{"name":"global::App.Type0","repository":"acme/gadgets"}""",
            DelegateToken, host.UserAssertion());
        host.RepositoryHeader = Gadgets;
        var headerUngranted = await host.CallAsync("find_symbol", """{"name":"global::App.Type0","repository":"acme/widgets"}""",
            DelegateToken, host.UserAssertion());
        var conflictAbsent = await host.CallAsync("find_symbol", """{"name":"global::App.Type0","repository":"acme/absent"}""",
            DelegateToken, host.UserAssertion());

        Assert.IsTrue(sameRepository.Body.GetProperty("results").GetArrayLength() > 0, sameRepository.Body.ToString());
        Assert.AreEqual(ToolSelectionFilters.SelectorConflictCode, ErrorCode(argumentUngranted.Body), argumentUngranted.Body.ToString());
        Assert.AreEqual(ToolSelectionFilters.SelectorConflictCode, ErrorCode(headerUngranted.Body),
            "a granted argument cannot launder an ungranted header, and vice versa");
        Assert.AreEqual(WithoutTimestamp(conflictAbsent.Body), WithoutTimestamp(headerUngranted.Body),
            "the conflict is decided before any grant or catalog lookup, so it reveals nothing");
    }

    [TestMethod]
    public async Task ConcurrentPuts_AtTheLimit_ExactlyTheLimitSucceed()
    {
        const int limit = 3;
        await using var host = await Harness.StartAsync(configure: o => o with { MaxGrantsPerPrincipal = limit });

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            using var response = await host.ControlAsync(HttpMethod.Put, SelfPath, ControlToken, host.UserAssertion(),
                Body($"https://github.com/acme/repo{i}"));
            return (response.StatusCode, Body: await response.Content.ReadAsStringAsync());
        }));

        Assert.AreEqual(limit, responses.Count(r => r.StatusCode == HttpStatusCode.OK), string.Join("\n", responses));
        Assert.AreEqual(8 - limit, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict
            && r.Body == $$"""{"status":"rejected","reason":"{{GrantReason.GrantLimit}}"}"""), string.Join("\n", responses));
        Assert.AreEqual(limit, GrantCount(host));
    }

    [TestMethod]
    public async Task Resolve_UserWithoutAGrant_IsTheBare404_LikeAnAbsentRepository()
    {
        await using var host = await Harness.StartAsync();
        const string path = "/control/resolve?repository=https://github.com/acme/widgets&branch=main";

        using var ungranted = await host.ControlAsync(HttpMethod.Get, path, ControlToken, host.UserAssertion());
        using var absent = await host.ControlAsync(HttpMethod.Get, "/control/resolve?repository=https://github.com/acme/absent", ControlToken, host.UserAssertion());
        using var application = await host.ControlAsync(HttpMethod.Get, path, ControlToken, AppAssertion(host));
        using var operatorCall = await host.ControlAsync(HttpMethod.Get, path, ControlToken);
        await PutSelfAsync(host, host.UserAssertion(), Widgets);
        using var granted = await host.ControlAsync(HttpMethod.Get, path, ControlToken, host.UserAssertion());
        using var grantedAbsentBranch = await host.ControlAsync(HttpMethod.Get,
            "/control/resolve?repository=https://github.com/acme/widgets&branch=gone", ControlToken, host.UserAssertion());

        Assert.AreEqual(HttpStatusCode.NotFound, ungranted.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, absent.StatusCode);
        var ungrantedBody = await ungranted.Content.ReadAsStringAsync();
        Assert.AreEqual(string.Empty, ungrantedBody, "a bare 404");
        Assert.AreEqual(await absent.Content.ReadAsStringAsync(), ungrantedBody);
        Assert.AreEqual(await grantedAbsentBranch.Content.ReadAsStringAsync(), ungrantedBody);
        Assert.AreEqual(HttpStatusCode.NotFound, grantedAbsentBranch.StatusCode);
        foreach (var allowed in new[] { application, operatorCall, granted })
        {
            var body = await JsonAsync(allowed, HttpStatusCode.OK);
            Assert.AreEqual(host.WidgetsHash, body.GetProperty("identity_hash").GetString(), body.ToString());
        }
    }

    [TestMethod]
    public async Task VerifiedCallerWithoutDeployment_IsAuditedWithDepDash()
    {
        await using var host = await Harness.StartAsync();
        var claims = CallerAssertionSigner.UserClaims(DateTimeOffset.UtcNow);
        claims.Remove("dep");
        var assertion = host.Sign(claims);

        await PutSelfAsync(host, assertion, Widgets);
        using (var refused = await host.ControlAsync(HttpMethod.Put, SelfPath, ControlToken, assertion, Body(Widgets, "bad branch")))
            await AssertRejectedAsync(refused, HttpStatusCode.BadRequest, GrantReason.BranchNotAllowed);
        using (var ensure = await host.ControlAsync(HttpMethod.Post, "/control/ensure", ControlToken, assertion, EnsureBody()))
            await AssertRejectedAsync(ensure, HttpStatusCode.Forbidden, GrantReason.NotGranted);

        const string noDep = ";idp=processstack;kid=kid-a;via=mcp-surface;cid=conn-1;dep=-;jti=jti-1";
        var grants = host.Service.RecentAudit(action: AuditAction.Grant).OrderBy(r => r.Id).ToList();
        CollectionAssert.AreEqual(new[] { "put_self;created" + noDep, "put_self;" + GrantReason.BranchNotAllowed + noDep },
            grants.Select(r => r.Detail).ToList());
        Assert.AreEqual(GrantReason.NotGranted + noDep, host.Service.RecentAudit(action: AuditAction.Ensure).Single().Detail);
        Assert.IsTrue(grants.All(r => r.Actor == AuditLogStore.HashActor("tenant-a/user-1")));
    }

    // ==== helpers ===================================================================================

    // Gadgets and Gizmos pin Widgets (the provider) and each use its symbol global::App.Type0 once.
    private static void SeedUsages(IndexDatabase db)
    {
        var provider = new SnapshotStore(db.GetConnection()).GetSelectedSnapshotIdForRepository(Widgets)!.Value;
        foreach (var (consumer, commit) in new[] { (Gadgets, "commit-g1"), (Gizmos, "commit-z1") })
        {
            GrantServiceTests.SeedConsumer(db, consumer, commit, provider);
            var conn = db.GetConnection();
            var consumerRepository = new SnapshotStore(conn).GetRepositoryId(consumer)!.Value;
            var consumerProject = Scalar(conn, """
                SELECT d.consumer_project_id FROM snapshot_dependencies d
                JOIN snapshots s ON s.id = d.consumer_snapshot_id WHERE s.repository_id = @a;
                """, consumerRepository);
            var target = Scalar(conn, """
                SELECT s.id FROM symbols s JOIN snapshot_projects sp ON sp.project_id = s.project_id
                WHERE sp.snapshot_id = @a AND s.fully_qualified_name = 'global::App.Type0';
                """, provider);
            var file = Scalar(conn, "INSERT INTO files (project_id, repo_relative_path) VALUES (@a, 'src/App/Use.cs') RETURNING id;", consumerProject);
            var fileVersion = Scalar(conn,
                "INSERT INTO file_versions (file_id, content_hash, last_indexed_at) VALUES (@a, randomblob(32), 1) RETURNING id;", file);
            using var occurrence = conn.CreateCommand();
            occurrence.CommandText = """
                INSERT INTO occurrences (in_project_id, target_symbol_id, source_symbol_id, file_version_id, line, col, kind, flags)
                VALUES (@project, @target, NULL, @fv, 7, 3, 0, 0);
                """;
            occurrence.Parameters.AddWithValue("@project", consumerProject);
            occurrence.Parameters.AddWithValue("@target", target);
            occurrence.Parameters.AddWithValue("@fv", fileVersion);
            occurrence.ExecuteNonQuery();
        }

        static long Scalar(Microsoft.Data.Sqlite.SqliteConnection conn, string sql, long a)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@a", a);
            return (long)cmd.ExecuteScalar()!;
        }
    }

    // The status of a request whose handler may throw (the test server then surfaces the failure to the client).
    private static async Task<HttpStatusCode?> StatusOrNullAsync(Func<Task<HttpResponseMessage>> send)
    {
        try
        {
            using var response = await send();
            return response.StatusCode;
        }
        catch (Exception ex) when (ex is not AssertFailedException)
        {
            return null;
        }
    }

    private static string AppAssertion(Harness host, string tenant = "tenant-a") =>
        host.Sign(CallerAssertionSigner.ApplicationClaims(DateTimeOffset.UtcNow, tenantId: tenant), tenant == "tenant-b" ? "kid-b" : "kid-a");

    private static string Body(string repository, string? branch = null) =>
        branch is null
            ? JsonSerializer.Serialize(new { repository })
            : JsonSerializer.Serialize(new { repository, branch });

    private static async Task PutSelfAsync(Harness host, string assertion, string repository, string? branch = null)
    {
        using var response = await host.ControlAsync(HttpMethod.Put, SelfPath, ControlToken, assertion, Body(repository, branch));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<int> DeleteAsync(Harness host, string path, string assertion)
    {
        using var response = await host.ControlAsync(HttpMethod.Delete, path, ControlToken, assertion);
        return (await JsonAsync(response, HttpStatusCode.OK)).GetProperty("deleted").GetInt32();
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(expected, response.StatusCode, raw);
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private static async Task<string> AssertRejectedAsync(HttpResponseMessage response, HttpStatusCode status, string reason)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(status, response.StatusCode, raw);
        Assert.AreEqual($$"""{"status":"rejected","reason":"{{reason}}"}""", raw);
        return raw;
    }

    private static long GrantCount(Harness host) => host.GrantRows();
}
