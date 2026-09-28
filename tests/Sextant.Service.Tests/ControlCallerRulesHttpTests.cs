using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Sextant.Service.Host;
using Sextant.Store;
using static Sextant.Service.Tests.CallerAssertionHttpTests;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #193 at the HTTP boundary: a user caller (a verified <c>act=user</c> assertion) reaches only the control
/// routes that apply their own <c>act=user</c> rule (ensure, status, resolve and the grant routes). Branch retire
/// and every operational route refuse it with 403 <c>caller_not_allowed</c> before reading anything, and an
/// application caller or an assertion-less control call behaves as before.
/// </summary>
[TestClass]
public class ControlCallerRulesHttpTests
{
    private const string Suffix = ";idp=processstack;kid=kid-a;via=mcp-surface;cid=conn-1;dep=dep-1;jti=jti-1";
    private const string Forbidden = """{"error":"caller_not_allowed"}""";
    private const string Retire = "/control/branches/retire";
    private const string Feature = "feature";

    // ==== retire ===================================================================================

    [TestMethod]
    public async Task Retire_UserCaller_Is403_WhateverTheRequestNames()
    {
        long featureSnapshot = 0;
        await using var host = await Harness.StartAsync(seed: db => featureSnapshot = SeedFeature(db));
        string[] bodies =
        [
            RetireBody(Widgets, Feature),
            RetireBody(Widgets, Feature, expectedHead: "commit-w2"),
            RetireBody(Widgets, "main"),
            RetireBody(Widgets, "gone"),
            RetireBody("https://github.com/acme/absent", Feature),
            RetireBody("http://localhost/acme/widgets", Feature),
            RetireBody(Widgets, " "),
            "{}"
        ];

        foreach (var body in bodies)
        {
            using var response = await host.ControlAsync(HttpMethod.Post, Retire, ControlToken, host.UserAssertion(), body);
            await AssertCallerNotAllowedAsync(response, body);
        }

        AssertFeatureUnchanged(host, featureSnapshot);
        Assert.IsNotNull(host.Service.ResolveBranchHead(Widgets, "main"), "the default branch is untouched");
        var rows = host.Service.RecentAudit(action: AuditAction.Retire);
        Assert.AreEqual(bodies.Length, rows.Count, "every refused retire is audited");
        foreach (var row in rows)
        {
            Assert.AreEqual(AuditOutcome.Denied, row.Outcome);
            Assert.AreEqual(CallerAssertionGate.NotAllowedCode + Suffix, row.Detail);
            Assert.IsNull(row.RepositoryScope, "the refusal precedes reading the request, so it names no repository");
            Assert.AreEqual(AuditLogStore.HashActor("tenant-a/user-1"), row.Actor);
        }
    }

    [TestMethod]
    public async Task Retire_UserCallerHoldingAGrant_IsStill403()
    {
        long featureSnapshot = 0;
        await using var host = await Harness.StartAsync(seed: db => featureSnapshot = SeedFeature(db));
        using (var grant = await host.ControlAsync(HttpMethod.Put, "/control/grants/self", ControlToken, host.UserAssertion(),
            JsonSerializer.Serialize(new { repository = Widgets })))
            Assert.AreEqual(HttpStatusCode.OK, grant.StatusCode, await grant.Content.ReadAsStringAsync());
        Assert.IsNotNull(host.Service.ResolveBranchHead(Widgets, Feature), "precondition: the user can see the branch");

        using var response = await host.ControlAsync(HttpMethod.Post, Retire, ControlToken, host.UserAssertion(),
            RetireBody(Widgets, Feature, expectedHead: "commit-w2"));

        await AssertCallerNotAllowedAsync(response, "a grant makes a repository visible, never retirable");
        AssertFeatureUnchanged(host, featureSnapshot);
    }

    [TestMethod]
    public async Task Retire_UserCallerWithoutDeployment_AuditsDashDeployment()
    {
        await using var host = await Harness.StartAsync(seed: db => SeedFeature(db));
        var claims = CallerAssertionSigner.UserClaims(DateTimeOffset.UtcNow);
        claims.Remove("dep");

        using var response = await host.ControlAsync(HttpMethod.Post, Retire, ControlToken, host.Sign(claims), RetireBody(Widgets, Feature));

        await AssertCallerNotAllowedAsync(response, "no dep claim");
        var row = host.Service.RecentAudit(action: AuditAction.Retire).Single();
        Assert.AreEqual(AuditOutcome.Denied, row.Outcome);
        Assert.AreEqual(CallerAssertionGate.NotAllowedCode + Suffix.Replace(";dep=dep-1;", ";dep=-;", StringComparison.Ordinal), row.Detail);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Retire_ApplicationOrOperator_IsUnchanged(bool application)
    {
        long featureSnapshot = 0;
        await using var host = await Harness.StartAsync(seed: db => featureSnapshot = SeedFeature(db));
        var assertion = application ? AppAssertion(host) : null;

        using (var defaultBranch = await host.ControlAsync(HttpMethod.Post, Retire, ControlToken, assertion, RetireBody(Widgets, "main")))
            await AssertRejectedAsync(defaultBranch, HttpStatusCode.Conflict, BranchGuardReason.DefaultBranch);
        using (var mismatch = await host.ControlAsync(HttpMethod.Post, Retire, ControlToken, assertion,
            RetireBody(Widgets, Feature, expectedHead: "commit-other")))
            await AssertRejectedAsync(mismatch, HttpStatusCode.Conflict, BranchGuardReason.HeadMismatch);
        using (var blank = await host.ControlAsync(HttpMethod.Post, Retire, ControlToken, assertion, RetireBody(Widgets, " ")))
            await AssertRejectedAsync(blank, HttpStatusCode.BadRequest, BranchGuardReason.BranchRequired);
        AssertFeatureUnchanged(host, featureSnapshot);

        using (var retired = await host.ControlAsync(HttpMethod.Post, Retire, ControlToken, assertion,
            RetireBody(Widgets, Feature, expectedHead: "commit-w2")))
            await AssertBodyAsync(retired, HttpStatusCode.OK, """{"retired":true}""");
        using (var again = await host.ControlAsync(HttpMethod.Post, Retire, ControlToken, assertion, RetireBody(Widgets, Feature)))
            await AssertBodyAsync(again, HttpStatusCode.OK, """{"retired":false}""");

        Assert.IsNull(host.Service.ResolveBranchHead(Widgets, Feature), "the branch pointer is gone");
        Assert.IsNotNull(host.Service.ResolveBranchHead(Widgets, "main"));
        var actor = application ? AuditLogStore.HashActor("tenant-a/app:sextant") : AuditLogStore.HashActor(ControlToken);
        var rows = host.Service.RecentAudit(action: AuditAction.Retire);
        Assert.AreEqual(5, rows.Count, string.Join("\n", rows.Select(r => $"{r.Outcome} {r.Detail}")));
        Assert.IsTrue(rows.All(r => r.Actor == actor), "every retire is attributed to its own caller");
        Assert.IsFalse(rows.Any(r => r.Detail?.StartsWith(CallerAssertionGate.NotAllowedCode, StringComparison.Ordinal) == true));
    }

    [TestMethod]
    public async Task Retire_TamperedAssertion_Is401()
    {
        long featureSnapshot = 0;
        await using var host = await Harness.StartAsync(seed: db => featureSnapshot = SeedFeature(db));
        var parts = host.UserAssertion().Split('.');
        var elevated = CallerAssertionSigner.ApplicationClaims(DateTimeOffset.UtcNow);
        var tampered = $"{parts[0]}.{CallerAssertionSigner.Encode(JsonSerializer.Serialize(elevated))}.{parts[2]}";

        using var response = await host.ControlAsync(HttpMethod.Post, Retire, ControlToken, tampered,
            RetireBody(Widgets, Feature, expectedHead: "commit-w2"));

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), CallerAssertionGate.InvalidAssertionCode);
        StringAssert.Contains(response.Headers.WwwAuthenticate.ToString(), "invalid_token");
        AssertFeatureUnchanged(host, featureSnapshot);
        Assert.AreEqual(0, host.Service.RecentAudit(action: AuditAction.Retire).Count, "an unverified call is never audited as a caller");
    }

    // ==== operational routes =======================================================================

    [TestMethod]
    [DataRow("POST", "/control/retention", AuditAction.Retention)]
    [DataRow("POST", "/control/retention?execute=true", AuditAction.Retention)]
    [DataRow("POST", "/control/backup?dir={dir}", AuditAction.Backup)]
    [DataRow("POST", "/control/backup", AuditAction.Backup)]
    [DataRow("GET", "/control/metrics", null)]
    [DataRow("GET", "/control/metrics?format=prometheus", null)]
    [DataRow("GET", "/control/audit", null)]
    [DataRow("GET", "/control/audit?action=retire&limit=5", null)]
    [DataRow("GET", "/control/pilot", null)]
    [DataRow("GET", "/control/pilot?workload=untrusted", null)]
    [DataRow("GET", "/control/not-a-route", null)]
    [DataRow("DELETE", "/control/retention", null)]
    public async Task OperationalRoute_UserCaller_Is403(string method, string path, string? auditedAs)
    {
        await using var host = await Harness.StartAsync();
        var dir = Path.Combine(Path.GetTempPath(), "sextant-193-" + Guid.NewGuid().ToString("N"));
        var before = host.Service.RecentAudit(limit: 1000).Count;

        using var response = await host.ControlAsync(new HttpMethod(method), path.Replace("{dir}", Uri.EscapeDataString(dir), StringComparison.Ordinal),
            ControlToken, host.UserAssertion());

        await AssertCallerNotAllowedAsync(response, $"{method} {path}");
        Assert.IsFalse(Directory.Exists(dir), "a refused backup writes nothing");
        var rows = host.Service.RecentAudit(limit: 1000);
        if (auditedAs is null)
        {
            Assert.AreEqual(before, rows.Count, "an unaudited route writes no row when it refuses");
            return;
        }
        var row = rows.Single(r => r.Action == auditedAs);
        Assert.AreEqual(AuditOutcome.Denied, row.Outcome);
        Assert.AreEqual(CallerAssertionGate.NotAllowedCode + Suffix, row.Detail);
        Assert.IsNull(row.RepositoryScope);
        Assert.AreEqual(AuditLogStore.HashActor("tenant-a/user-1"), row.Actor);
    }

    [TestMethod]
    public async Task Audit_UserCaller_CannotReadAnyCallersRows()
    {
        await using var host = await Harness.StartAsync(seed: db => SeedFeature(db));
        using (var own = await host.ControlAsync(HttpMethod.Post, Retire, ControlToken, host.UserAssertion(), RetireBody(Widgets, Feature)))
            await AssertCallerNotAllowedAsync(own, "seeding the user's own denied row");
        using (var other = await host.ControlAsync(HttpMethod.Post, "/control/retention", ControlToken, AppAssertion(host)))
            Assert.AreEqual(HttpStatusCode.OK, other.StatusCode);

        foreach (var query in new[] { "", "?action=retire", "?action=retention", "?repository=https://github.com/acme/widgets", "?limit=1" })
        {
            using var response = await host.ControlAsync(HttpMethod.Get, "/control/audit" + query, ControlToken, host.UserAssertion());
            await AssertCallerNotAllowedAsync(response, query);
        }

        using var operatorRead = await host.ControlAsync(HttpMethod.Get, "/control/audit", ControlToken);
        var body = await operatorRead.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, operatorRead.StatusCode, body);
        Assert.IsTrue(JsonDocument.Parse(body).RootElement.GetProperty("result_count").GetInt32() >= 2, body);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task OperationalRoutes_ApplicationOrOperator_AreUnchanged(bool application)
    {
        await using var host = await Harness.StartAsync();
        var assertion = application ? AppAssertion(host) : null;
        var actor = application ? AuditLogStore.HashActor("tenant-a/app:sextant") : AuditLogStore.HashActor(ControlToken);
        var dir = Path.Combine(Path.GetTempPath(), "sextant-193-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var (method, path) in new[]
            {
                ("POST", "/control/retention"),
                ("POST", "/control/retention?execute=true"),
                ("POST", "/control/backup?dir=" + Uri.EscapeDataString(dir)),
                ("GET", "/control/metrics"),
                ("GET", "/control/audit"),
                ("GET", "/control/pilot"),
                ("GET", "/control/pilot?workload=untrusted")
            })
            {
                using var response = await host.ControlAsync(new HttpMethod(method), path, ControlToken, assertion);
                var body = await response.Content.ReadAsStringAsync();
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"{method} {path}: {body}");
                Assert.AreNotEqual(Forbidden, body);
            }

            using (var prometheus = await host.ControlAsync(HttpMethod.Get, "/control/metrics?format=prometheus", ControlToken, assertion))
            {
                Assert.AreEqual(HttpStatusCode.OK, prometheus.StatusCode);
                Assert.AreEqual("text/plain", prometheus.Content.Headers.ContentType?.MediaType);
            }
            Assert.IsTrue(Directory.Exists(dir), "the backup ran");
            var retention = host.Service.RecentAudit(action: AuditAction.Retention);
            Assert.AreEqual(2, retention.Count);
            Assert.IsTrue(retention.All(r => r.Actor == actor && r.Outcome != AuditOutcome.Denied));
            var backup = host.Service.RecentAudit(action: AuditAction.Backup).Single();
            Assert.AreEqual(AuditOutcome.Complete, backup.Outcome);
            Assert.AreEqual(actor, backup.Actor);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of the temp backup copy.
            }
        }
    }

    // ==== routes that decide user callers themselves ===============================================

    [TestMethod]
    public async Task SelfGatedRoutes_StillServeUserCallers()
    {
        await using var host = await Harness.StartAsync();
        var user = host.UserAssertion();

        using (var resolve = await host.ControlAsync(HttpMethod.Get, "/control/resolve?repository=https://github.com/acme/widgets", ControlToken, user))
            Assert.AreEqual(HttpStatusCode.NotFound, resolve.StatusCode, "an ungranted resolve keeps SVC-4's bare 404");
        using (var status = await host.ControlAsync(HttpMethod.Get, "/control/status/999999", ControlToken, user))
            Assert.AreEqual(HttpStatusCode.NotFound, status.StatusCode);
        using (var ensure = await host.ControlAsync(HttpMethod.Post, "/control/ensure", ControlToken, user, EnsureBody()))
            await AssertRejectedAsync(ensure, HttpStatusCode.Forbidden, "not_granted");
        using (var own = await host.ControlAsync(HttpMethod.Get, "/control/grants/self", ControlToken, user))
            Assert.AreEqual(HttpStatusCode.OK, own.StatusCode, await own.Content.ReadAsStringAsync());
        using (var tenant = await host.ControlAsync(HttpMethod.Put, "/control/grants/tenant", ControlToken, user,
            JsonSerializer.Serialize(new { repository = Widgets })))
            await AssertRejectedAsync(tenant, HttpStatusCode.Forbidden, "wrong_actor");
        using (var targets = await host.ControlAsync(HttpMethod.Get, "/control/grants?scope=tenant", ControlToken, user))
            await AssertRejectedAsync(targets, HttpStatusCode.Forbidden, "wrong_actor");
    }

    [TestMethod]
    public async Task ControlRouteInventory_OnlySelfGatedRoutesAdmitUserCallers()
    {
        await using var host = await Harness.StartAsync();
        var control = host.RouteEndpoints()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/control/", StringComparison.Ordinal) == true)
            .ToList();

        var admitted = control.Where(ControlCallerRules.AdmitsUserCallers).Select(Describe).Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(new[]
        {
            "DELETE /control/grants/self",
            "DELETE /control/grants/tenant",
            "GET /control/grants",
            "GET /control/grants/self",
            "GET /control/resolve",
            "GET /control/status/{jobId:long}",
            "POST /control/ensure",
            "PUT /control/grants/self",
            "PUT /control/grants/tenant"
        }, admitted, "a new control route refuses user callers unless it applies its own act=user rule");

        var audited = control
            .Select(e => (Route: Describe(e), Audit: e.Metadata.GetMetadata<ControlCallerRules.RefusedUserCallAudit>()))
            .Where(x => x.Audit is not null)
            .Select(x => $"{x.Route} -> {x.Audit!.Action}")
            .Order(StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(new[]
        {
            "POST /control/backup -> backup",
            "POST /control/branches/retire -> retire",
            "POST /control/retention -> retention"
        }, audited);

        var refused = control.Where(e => !ControlCallerRules.AdmitsUserCallers(e)).Select(Describe).Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.IsSubsetOf(new[]
        {
            "GET /control/audit",
            "GET /control/metrics",
            "GET /control/pilot",
            "POST /control/backup",
            "POST /control/branches/retire",
            "POST /control/contribute",
            "POST /control/retention"
        }, refused);
    }

    // ==== helpers ==================================================================================

    // A non-default "feature" branch on widgets whose head is a published snapshot at a recorded commit.
    private static long SeedFeature(IndexDatabase db) =>
        GrantServiceTests.PublishOnBranch(db, Widgets, "commit-w2", Feature, isDefault: false);

    private static void AssertFeatureUnchanged(Harness host, long featureSnapshot)
    {
        var head = host.Service.ResolveBranchHead(Widgets, Feature);
        Assert.IsNotNull(head, "the feature branch still resolves");
        Assert.AreEqual(featureSnapshot, head.Snapshot.Id, "the feature pointer did not move");
        Assert.AreEqual(SnapshotStatus.Complete, head.Snapshot.Status, "the feature snapshot is untouched");
    }

    private static string RetireBody(string repository, string branch, string? expectedHead = null) =>
        expectedHead is null
            ? JsonSerializer.Serialize(new { repository, branch })
            : JsonSerializer.Serialize(new { repository, branch, expected_head_commit = expectedHead });

    private static string AppAssertion(Harness host) =>
        host.Sign(CallerAssertionSigner.ApplicationClaims(DateTimeOffset.UtcNow));

    private static string Describe(RouteEndpoint endpoint) =>
        $"{string.Join(",", endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])} {endpoint.RoutePattern.RawText}";

    private static async Task AssertCallerNotAllowedAsync(HttpResponseMessage response, string context)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, $"{context}: {body}");
        Assert.AreEqual(Forbidden, body, context);
        Assert.AreEqual("", response.Headers.WwwAuthenticate.ToString(), "a policy refusal is not a credential challenge");
    }

    private static async Task AssertRejectedAsync(HttpResponseMessage response, HttpStatusCode status, string reason) =>
        await AssertBodyAsync(response, status, $$"""{"status":"rejected","reason":"{{reason}}"}""");

    private static async Task AssertBodyAsync(HttpResponseMessage response, HttpStatusCode status, string expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(status, response.StatusCode, body);
        Assert.AreEqual(expected, body);
    }
}
