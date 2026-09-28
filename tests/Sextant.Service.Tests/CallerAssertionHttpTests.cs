using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Sextant.Core;
using Sextant.Mcp;
using Sextant.Service.CallerIdentity;
using Sextant.Service.Host;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// SVC-3 at the HTTP boundary: delegate tokens and caller assertions on <c>/mcp</c>, <c>/query/*</c> and
/// <c>/control/*</c>. Keys are generated in-test; the tenants are <c>tenant-a</c> (key <c>kid-a</c>) and
/// <c>tenant-b</c> (key <c>kid-b</c>). No grant exists here, so every delegate read is denied (SVC-4's grant
/// authorizer: a caller sees only the repositories it holds grants on, <see cref="GrantHttpTests"/>), so a delegate
/// token opens nothing on its own.
/// </summary>
[TestClass]
public class CallerAssertionHttpTests
{
    internal const string QueryToken = "query-secret";
    internal const string ControlToken = "control-secret";
    internal const string ContributeToken = "contribute-secret";
    internal const string DelegateToken = "delegate-secret";
    internal const string ReaderToken = "widgets-reader";
    internal const string Widgets = "https://github.com/acme/widgets";
    internal const string Header = CallerAssertionOptions.DefaultHeader;
    internal const string FindSymbolArguments = """{"name":"global::App.Type0"}""";

    // ==== /mcp: discovery ===========================================================================

    [TestMethod]
    public async Task Mcp_DelegateDiscovery_WithoutAssertion_Works()
    {
        await using var host = await Harness.StartAsync();

        var init = await host.RpcAsync("initialize",
            """{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"pool","version":"1"}}""", DelegateToken);
        Assert.AreEqual(HttpStatusCode.OK, init.Status, init.Raw);
        Assert.IsTrue(init.Result.HasValue, init.Raw);

        var ping = await host.RpcAsync("ping", "{}", DelegateToken);
        Assert.AreEqual(HttpStatusCode.OK, ping.Status, ping.Raw);

        var list = await host.RpcAsync("tools/list", "{}", DelegateToken);
        Assert.AreEqual(HttpStatusCode.OK, list.Status, list.Raw);
        Assert.IsTrue(list.Result!.Value.GetProperty("tools").GetArrayLength() > 0, "a pooled client lists tools without a caller");
    }

    [TestMethod]
    public async Task Mcp_DelegateDiscovery_WithAssertion_MustVerify()
    {
        await using var host = await Harness.StartAsync();

        var valid = await host.RpcAsync("tools/list", "{}", DelegateToken, host.UserAssertion());
        Assert.AreEqual(HttpStatusCode.OK, valid.Status, valid.Raw);

        var forged = await host.RpcAsync("tools/list", "{}", DelegateToken, host.UserAssertion(key: CallerAssertionSigner.NewKey()));
        AssertUnauthorized(forged, CallerAssertionGate.InvalidAssertionCode, "invalid_token");
    }

    // ==== /mcp: tools/call ==========================================================================

    [TestMethod]
    public async Task Mcp_DelegateCall_WithoutAssertion_IsCallerRequired()
    {
        await using var host = await Harness.StartAsync();

        var call = await host.CallAsync("find_symbol", FindSymbolArguments, DelegateToken);

        Assert.IsTrue(call.IsError, call.Body.ToString());
        Assert.AreEqual(CallerAssertionGate.RequiredCode, ErrorCode(call.Body));
        Assert.AreEqual(0, host.Probe.Calls, "the tool is never reached");
    }

    [TestMethod]
    public async Task Mcp_DelegateCall_WithoutAssertion_IsAnsweredBeforeTheSelectionFilter()
    {
        await using var host = await Harness.StartAsync();

        // An invalid selector would be `invalid_selector` from the SVC-2 filter; the caller check runs first.
        var call = await host.CallAsync("find_symbol", """{"name":"global::App.Type0","repository":"http://10.0.0.1/x"}""", DelegateToken);

        Assert.AreEqual(CallerAssertionGate.RequiredCode, ErrorCode(call.Body), call.Body.ToString());
    }

    [TestMethod]
    public async Task Mcp_DelegateCall_WithVerifiedCaller_MustSelect_AndIsDeniedUniformly()
    {
        await using var host = await Harness.StartAsync();

        var unselected = await host.CallAsync("find_symbol", FindSymbolArguments, DelegateToken, host.UserAssertion());
        Assert.AreEqual(ResponseBuilder.RepositoryRequiredCode, ErrorCode(unselected.Body),
            $"a verified caller's reads are scoped, so it must name the repository: {unselected.Body}");

        var selected = await host.CallAsync("find_symbol", """{"name":"global::App.Type0","repository":"acme/widgets"}""",
            DelegateToken, host.UserAssertion());
        var notFound = WithoutTimestamp(JsonDocument.Parse(ResponseBuilder.BuildNotFound()).RootElement);
        Assert.AreEqual(notFound, WithoutTimestamp(selected.Body),
            "without a grant every delegate read is the uniform not-found, even of a published repository");

        var application = await host.CallAsync("find_symbol", """{"name":"global::App.Type0","repository":"acme/widgets"}""",
            DelegateToken, host.Sign(CallerAssertionSigner.ApplicationClaims(DateTimeOffset.UtcNow)));
        Assert.AreEqual(notFound, WithoutTimestamp(application.Body), "an application caller is denied the same way");
    }

    [TestMethod]
    public async Task Mcp_DelegateCall_ReachesTheToolWithItsCaller()
    {
        await using var host = await Harness.StartAsync();

        await host.CallAsync("find_symbol", FindSymbolArguments, DelegateToken,
            host.UserAssertion(sub: "user-7", jti: "jti-7"));

        Assert.AreEqual(1, host.Probe.Calls);
        var principal = host.Probe.Principals.Single().Principal!;
        Assert.AreEqual("tenant-a", principal.TenantId);
        Assert.AreEqual("acme", principal.TenantSlug);
        Assert.AreEqual(CallerActor.User, principal.Actor);
        Assert.AreEqual("processstack", principal.Idp);
        Assert.AreEqual("user-7", principal.UserId);
        Assert.AreEqual("sextant", principal.App);
        Assert.AreEqual("dep-1", principal.Deployment);
        Assert.AreEqual("conn-1", principal.Connection);
        Assert.AreEqual("mcp-surface", principal.Via);
        Assert.AreEqual("kid-a", principal.KeyId);
        Assert.AreEqual("jti-7", principal.Jti);
        Assert.AreEqual("tenant-a/user-7", principal.AuditPrincipal);
    }

    [TestMethod]
    public async Task Mcp_DelegateCall_WithoutDeployment_ReachesTheToolWithANullDeployment()
    {
        await using var host = await Harness.StartAsync();
        var claims = CallerAssertionSigner.UserClaims(DateTimeOffset.UtcNow, sub: "user-8", jti: "jti-8");
        claims.Remove("dep");

        await host.CallAsync("find_symbol", FindSymbolArguments, DelegateToken, host.Sign(claims));

        Assert.AreEqual(1, host.Probe.Calls, "an assertion without dep is admitted and reaches the tool");
        var principal = host.Probe.Principals.Single().Principal!;
        Assert.AreEqual("user-8", principal.UserId);
        Assert.IsNull(principal.Deployment, "a run bound to no deployment carries no dep");
        Assert.AreEqual("tenant-a/user-8", principal.AuditPrincipal);
    }

    [TestMethod]
    public async Task Mcp_TwentyParallelCallers_EachResolveTheirOwn()
    {
        await using var host = await Harness.StartAsync();

        var calls = Enumerable.Range(0, 20).Select(i =>
        {
            var tenant = i % 2 == 0 ? "tenant-a" : "tenant-b";
            var assertion = host.UserAssertion(tenant: tenant, sub: $"user-{i}", jti: $"jti-{i}");
            return host.CallAsync("find_symbol", $$"""{"name":"user-{{i}}"}""", DelegateToken, assertion);
        }).ToArray();
        await Task.WhenAll(calls);

        Assert.AreEqual(20, host.Probe.Calls);
        foreach (var (name, principal) in host.Probe.Principals)
        {
            Assert.IsNotNull(principal, name);
            Assert.AreEqual(name, principal.UserId, "every call resolves the caller of its own request");
            var index = int.Parse(name["user-".Length..], System.Globalization.CultureInfo.InvariantCulture);
            Assert.AreEqual(index % 2 == 0 ? "tenant-a" : "tenant-b", principal.TenantId);
            Assert.AreEqual(index % 2 == 0 ? "kid-a" : "kid-b", principal.KeyId);
        }
        CollectionAssert.AreEquivalent(
            Enumerable.Range(0, 20).Select(i => $"user-{i}").ToArray(),
            host.Probe.Principals.Select(p => p.Name).ToArray());
    }

    // ==== /mcp: refusals ============================================================================

    [TestMethod]
    public async Task Mcp_InvalidAssertion_Is401_WithBearerChallenge()
    {
        await using var host = await Harness.StartAsync();

        var tampered = host.UserAssertion();
        var parts = tampered.Split('.');
        var claims = CallerAssertionSigner.UserClaims(DateTimeOffset.UtcNow, sub: "user-2");
        tampered = $"{parts[0]}.{CallerAssertionSigner.Encode(JsonSerializer.Serialize(claims))}.{parts[2]}";

        foreach (var method in new[] { "tools/list", "tools/call" })
        {
            var response = await host.RpcAsync(method, method == "tools/call"
                ? $$"""{"name":"find_symbol","arguments":{{FindSymbolArguments}}}"""
                : "{}", DelegateToken, tampered);
            AssertUnauthorized(response, CallerAssertionGate.InvalidAssertionCode, "invalid_token");
        }
        Assert.AreEqual(0, host.Probe.Calls);
    }

    [TestMethod]
    public async Task Mcp_OneTenantsKey_CannotAssertAnotherTenant()
    {
        await using var host = await Harness.StartAsync();

        // kid-a is tenant-a's key; a tenant-b claim set signed under it is refused (the multi-tenant guard).
        var crossTenant = CallerAssertionSigner.Sign(host.KeyA, "kid-a",
            CallerAssertionSigner.UserClaims(DateTimeOffset.UtcNow, tenantId: "tenant-b"));

        var response = await host.RpcAsync("tools/list", "{}", DelegateToken, crossTenant);

        AssertUnauthorized(response, CallerAssertionGate.InvalidAssertionCode, "invalid_token");
        StringAssert.Contains(host.Logs(), CallerAssertionReasons.TenantMismatch);
    }

    [TestMethod]
    public async Task Mcp_DuplicateAssertionHeader_Is401()
    {
        await using var host = await Harness.StartAsync();

        var response = await host.RpcAsync("tools/list", "{}", DelegateToken, host.UserAssertion(), host.UserAssertion(sub: "user-2"));

        AssertUnauthorized(response, CallerAssertionGate.InvalidAssertionCode, "invalid_token");
        StringAssert.Contains(host.Logs(), CallerAssertionReasons.DuplicateHeader);
    }

    [TestMethod]
    public async Task Mcp_IdpNotAllowed_IsCallerNotAllowedOnCall_ButDiscoveryPasses()
    {
        await using var host = await Harness.StartAsync();
        var claims = CallerAssertionSigner.UserClaims(DateTimeOffset.UtcNow, sub: "slack:conn-9:peer-1");
        claims["idp"] = "slack";
        var assertion = host.Sign(claims);

        var list = await host.RpcAsync("tools/list", "{}", DelegateToken, assertion);
        Assert.AreEqual(HttpStatusCode.OK, list.Status, list.Raw);

        var call = await host.CallAsync("find_symbol", FindSymbolArguments, DelegateToken, assertion);
        Assert.AreEqual(CallerAssertionGate.NotAllowedCode, ErrorCode(call.Body), call.Body.ToString());
        Assert.AreEqual(0, host.Probe.Calls);
    }

    [TestMethod]
    public async Task Mcp_LegacyToken_WithAssertionHeader_Is401AssertionNotAllowed()
    {
        await using var host = await Harness.StartAsync();

        var response = await host.RpcAsync("tools/list", "{}", QueryToken, host.UserAssertion());

        AssertUnauthorized(response, CallerAssertionGate.AssertionNotAllowedCode, "invalid_request");
    }

    [TestMethod]
    public async Task Mcp_ReadPolicyPrincipal_WithAssertionHeader_Is401AssertionNotAllowed()
    {
        await using var host = await Harness.StartAsync(readPolicy: true);

        var plain = await host.CallAsync("find_symbol", """{"name":"global::App.Type0","repository":"acme/widgets"}""", ReaderToken);
        Assert.IsFalse(plain.IsError, plain.Body.ToString());
        Assert.AreEqual(1, plain.Body.GetProperty("results").GetArrayLength(), "the read-policy principal reads as before");

        var response = await host.RpcAsync("tools/list", "{}", ReaderToken, host.UserAssertion());
        AssertUnauthorized(response, CallerAssertionGate.AssertionNotAllowedCode, "invalid_request");
    }

    [TestMethod]
    public async Task Mcp_LegacyToken_WithoutHeader_IsUnchanged()
    {
        await using var host = await Harness.StartAsync();

        var call = await host.CallAsync("find_symbol", FindSymbolArguments, QueryToken);

        Assert.IsFalse(call.IsError, call.Body.ToString());
        Assert.AreEqual(1, call.Body.GetProperty("results").GetArrayLength(), "the query token keeps its unselected read");
        Assert.IsNull(host.Probe.Principals.Single().Principal);
    }

    [TestMethod]
    public async Task Mcp_UnknownBearer_IsStillRefused()
    {
        await using var host = await Harness.StartAsync();

        var response = await host.RpcAsync("tools/list", "{}", "not-a-token", host.UserAssertion());

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.Status, "an assertion never stands in for a bearer");
    }

    // ==== /query/* =================================================================================

    [TestMethod]
    public async Task Query_Delegate_WithoutAssertion_Is401CallerRequired()
    {
        await using var host = await Harness.StartAsync();

        using var response = await host.SnapshotPageAsync(DelegateToken, host.WidgetsHash, assertions: []);

        await AssertUnauthorizedAsync(response, CallerAssertionGate.RequiredCode, "invalid_request");
    }

    [TestMethod]
    public async Task Query_Delegate_WithVerifiedCaller_IsTheUniform404()
    {
        await using var host = await Harness.StartAsync();

        using var published = await host.SnapshotPageAsync(DelegateToken, host.WidgetsHash, host.UserAssertion());
        using var unknown = await host.SnapshotPageAsync(DelegateToken, new string('0', 64), host.UserAssertion());

        Assert.AreEqual(HttpStatusCode.NotFound, published.StatusCode, "a delegate caller without a grant reads nothing");
        Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.AreEqual(await unknown.Content.ReadAsStringAsync(), await published.Content.ReadAsStringAsync(),
            "a published snapshot and an unknown one are indistinguishable");

        using var legacy = await host.SnapshotPageAsync(QueryToken, host.WidgetsHash, assertions: []);
        Assert.AreEqual(HttpStatusCode.OK, legacy.StatusCode, "the query token still pages it");
    }

    [TestMethod]
    public async Task Query_Delegate_PolicyFailure_Is403()
    {
        await using var host = await Harness.StartAsync();
        var claims = CallerAssertionSigner.UserClaims(DateTimeOffset.UtcNow, sub: "slack:conn-9:peer-1");
        claims["idp"] = "slack";

        using var response = await host.SnapshotPageAsync(DelegateToken, host.WidgetsHash, host.Sign(claims));

        await AssertForbiddenAsync(response);
    }

    [TestMethod]
    public async Task Query_Delegate_InvalidAssertion_Is401()
    {
        await using var host = await Harness.StartAsync();
        var expired = CallerAssertionSigner.UserClaims(DateTimeOffset.UtcNow.AddMinutes(-10));

        using var response = await host.SnapshotPageAsync(DelegateToken, host.WidgetsHash, host.Sign(expired));

        await AssertUnauthorizedAsync(response, CallerAssertionGate.InvalidAssertionCode, "invalid_token");
    }

    [TestMethod]
    public async Task Query_LegacyToken_WithAssertionHeader_Is401AssertionNotAllowed()
    {
        await using var host = await Harness.StartAsync();

        using var response = await host.SnapshotPageAsync(QueryToken, host.WidgetsHash, host.UserAssertion());

        await AssertUnauthorizedAsync(response, CallerAssertionGate.AssertionNotAllowedCode, "invalid_request");
    }

    // ==== app allow-list ===========================================================================

    [TestMethod]
    public async Task AppNotAllowed_IsRefusedOnEveryPlane()
    {
        await using var host = await Harness.StartAsync(apps: ["widgets-app"]);
        var assertion = host.UserAssertion(); // app=sextant

        var call = await host.CallAsync("find_symbol", FindSymbolArguments, DelegateToken, assertion);
        Assert.AreEqual(CallerAssertionGate.NotAllowedCode, ErrorCode(call.Body), call.Body.ToString());

        using (var query = await host.SnapshotPageAsync(DelegateToken, host.WidgetsHash, assertion))
            await AssertForbiddenAsync(query);
        using (var ensure = await host.ControlAsync(HttpMethod.Post, "/control/ensure", ControlToken, assertion, EnsureBody()))
            await AssertForbiddenAsync(ensure);
        using (var retention = await host.ControlAsync(HttpMethod.Post, "/control/retention", ControlToken, assertion))
            await AssertForbiddenAsync(retention);

        Assert.AreEqual(0, host.Worker.Calls, "a refused control caller never reaches the service");
        StringAssert.Contains(host.Logs(), CallerAssertionReasons.AppNotAllowed);
    }

    // ==== /control/* ===============================================================================

    [TestMethod]
    public async Task Control_VerifiedCaller_IsTheAuditActor()
    {
        await using var host = await Harness.StartAsync();
        var application = host.Sign(CallerAssertionSigner.ApplicationClaims(DateTimeOffset.UtcNow));

        using (var retention = await host.ControlAsync(HttpMethod.Post, "/control/retention", ControlToken, application))
            Assert.AreEqual(HttpStatusCode.OK, retention.StatusCode, await retention.Content.ReadAsStringAsync());
        using (var ensure = await host.ControlAsync(HttpMethod.Post, "/control/ensure", ControlToken,
            host.Sign(CallerAssertionSigner.ApplicationClaims(DateTimeOffset.UtcNow, tenantId: "tenant-b"), "kid-b"), EnsureBody()))
            Assert.AreEqual(HttpStatusCode.OK, ensure.StatusCode, await ensure.Content.ReadAsStringAsync());
        using (var retire = await host.ControlAsync(HttpMethod.Post, "/control/branches/retire", ControlToken,
            application, """{"repository":"https://github.com/acme/widgets","branch":"gone"}"""))
            Assert.AreEqual(HttpStatusCode.OK, retire.StatusCode, await retire.Content.ReadAsStringAsync());
        using (var refused = await host.ControlAsync(HttpMethod.Post, "/control/branches/retire", ControlToken,
            application, """{"repository":"https://github.com/acme/widgets","branch":" "}"""))
            Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode, await refused.Content.ReadAsStringAsync());
        using (var user = await host.ControlAsync(HttpMethod.Post, "/control/ensure", ControlToken, host.UserAssertion(), EnsureBody()))
            Assert.AreEqual(HttpStatusCode.Forbidden, user.StatusCode, await user.Content.ReadAsStringAsync());

        Assert.AreEqual(AuditLogStore.HashActor("tenant-a/app:sextant"),
            host.Service.RecentAudit(action: AuditAction.Retention).Single().Actor);
        var ensures = host.Service.RecentAudit(action: AuditAction.Ensure);
        Assert.AreEqual(2, ensures.Count);
        CollectionAssert.AreEquivalent(
            new[] { AuditLogStore.HashActor("tenant-b/app:sextant"), AuditLogStore.HashActor("tenant-a/user-1") },
            ensures.Select(e => e.Actor).ToArray(),
            "an application and a user caller are each the actor of their own ensure row");
        var retires = host.Service.RecentAudit(action: AuditAction.Retire);
        Assert.AreEqual(2, retires.Count);
        Assert.IsTrue(retires.All(r => r.Actor == AuditLogStore.HashActor("tenant-a/app:sextant")),
            "both a completed and a refused retire are attributed to the verified caller");
    }

    [TestMethod]
    public async Task Control_VerifiedCallerWithoutDeployment_IsTheAuditActor()
    {
        await using var host = await Harness.StartAsync();
        var claims = CallerAssertionSigner.ApplicationClaims(DateTimeOffset.UtcNow);
        claims.Remove("dep");

        using var retention = await host.ControlAsync(HttpMethod.Post, "/control/retention", ControlToken, host.Sign(claims));

        Assert.AreEqual(HttpStatusCode.OK, retention.StatusCode, await retention.Content.ReadAsStringAsync());
        var row = host.Service.RecentAudit(action: AuditAction.Retention).Single();
        Assert.AreEqual(AuditLogStore.HashActor("tenant-a/app:sextant"), row.Actor);
        StringAssert.Contains(row.Detail, ";dep=-;", "a missing deployment renders as '-'");
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("1")]
    [DataRow("\"\"")]
    public async Task PresentDeployment_ThatIsNotANonEmptyString_Is401_OnEveryPlane(string depJson)
    {
        await using var host = await Harness.StartAsync();
        var claims = CallerAssertionSigner.UserClaims(DateTimeOffset.UtcNow);
        claims["dep"] = JsonSerializer.Deserialize<JsonElement>(depJson);
        var assertion = host.Sign(claims);

        AssertUnauthorized(await host.RpcAsync("tools/list", "{}", DelegateToken, assertion),
            CallerAssertionGate.InvalidAssertionCode, "invalid_token");
        using (var query = await host.SnapshotPageAsync(DelegateToken, host.WidgetsHash, assertion))
            await AssertUnauthorizedAsync(query, CallerAssertionGate.InvalidAssertionCode, "invalid_token");
        using (var control = await host.ControlAsync(HttpMethod.Post, "/control/retention", ControlToken, assertion))
            await AssertUnauthorizedAsync(control, CallerAssertionGate.InvalidAssertionCode, "invalid_token");
        Assert.AreEqual(0, host.Probe.Calls);
        Assert.AreEqual(0, host.Service.RecentAudit(action: AuditAction.Retention).Count);
        StringAssert.Contains(host.Logs(), CallerAssertionReasons.BadClaims);
    }

    [TestMethod]
    public async Task Control_WithoutAssertion_IsUnchanged()
    {
        await using var host = await Harness.StartAsync();

        using var retention = await host.ControlAsync(HttpMethod.Post, "/control/retention", ControlToken);

        Assert.AreEqual(HttpStatusCode.OK, retention.StatusCode);
        Assert.AreEqual(AuditLogStore.HashActor(ControlToken),
            host.Service.RecentAudit(action: AuditAction.Retention).Single().Actor);
    }

    [TestMethod]
    public async Task Control_InvalidAssertion_Is401()
    {
        await using var host = await Harness.StartAsync();

        using var response = await host.ControlAsync(HttpMethod.Post, "/control/retention", ControlToken,
            host.UserAssertion(key: CallerAssertionSigner.NewKey()));

        await AssertUnauthorizedAsync(response, CallerAssertionGate.InvalidAssertionCode, "invalid_token");
        Assert.AreEqual(0, host.Service.RecentAudit(action: AuditAction.Retention).Count);
    }

    [TestMethod]
    public async Task UndecodableJoseHeader_Is401_NotAServerError_OnEveryPlane()
    {
        // The JSON parser defers string decoding; a lone surrogate or an invalid UTF-8 byte must never escape as a 500.
        await using var host = await Harness.StartAsync();
        var key = CallerAssertionSigner.NewKey();
        string[] assertions =
        [
            CallerAssertionSigner.SignRaw(key, """{"alg":"\uD800","kid":"kid-a"}""", "{}"),
            CallerAssertionSigner.SignRaw(key, """{"\uDC00":1,"alg":"HS256","kid":"kid-a"}""", "{}"),
            CallerAssertionSigner.SignRawBytes(key, [.. "{\"alg\":\"HS256\",\"kid\":\"kid-"u8, 0xFF, .. "\"}"u8], "{}"u8.ToArray())
        ];
        foreach (var assertion in assertions)
        {
            AssertUnauthorized(await host.RpcAsync("tools/list", "{}", DelegateToken, assertion),
                CallerAssertionGate.InvalidAssertionCode, "invalid_token");
            using (var query = await host.SnapshotPageAsync(DelegateToken, host.WidgetsHash, assertion))
                await AssertUnauthorizedAsync(query, CallerAssertionGate.InvalidAssertionCode, "invalid_token");
            using var control = await host.ControlAsync(HttpMethod.Post, "/control/retention", ControlToken, assertion);
            await AssertUnauthorizedAsync(control, CallerAssertionGate.InvalidAssertionCode, "invalid_token");
        }
        Assert.AreEqual(0, host.Probe.Calls);
    }

    [TestMethod]
    public async Task Control_AssertionWithoutTheControlToken_IsStillRefused()
    {
        await using var host = await Harness.StartAsync();

        using var delegateBearer = await host.ControlAsync(HttpMethod.Post, "/control/retention", DelegateToken, host.UserAssertion());

        Assert.AreEqual(HttpStatusCode.Unauthorized, delegateBearer.StatusCode, "a delegate token never reaches the control plane");
        Assert.AreEqual(0, host.Service.RecentAudit(action: AuditAction.Retention).Count);
    }

    [TestMethod]
    public async Task Contribute_WithAssertion_Is401AssertionNotAllowed()
    {
        await using var host = await Harness.StartAsync();

        using var response = await host.ControlAsync(HttpMethod.Post, "/control/contribute", ContributeToken, host.UserAssertion(), "{}");

        await AssertUnauthorizedAsync(response, CallerAssertionGate.AssertionNotAllowedCode, "invalid_request");
    }

    // ==== feature off ==============================================================================

    [TestMethod]
    public async Task FeatureOff_AnyAssertionHeader_Is401AssertionNotAllowed()
    {
        await using var host = await Harness.StartAsync(callerIdentity: false);

        using (var control = await host.ControlAsync(HttpMethod.Post, "/control/retention", ControlToken, host.UserAssertion()))
            await AssertUnauthorizedAsync(control, CallerAssertionGate.AssertionNotAllowedCode, "invalid_request");
        var mcp = await host.RpcAsync("tools/list", "{}", QueryToken, host.UserAssertion());
        AssertUnauthorized(mcp, CallerAssertionGate.AssertionNotAllowedCode, "invalid_request");
    }

    [TestMethod]
    public async Task FeatureOff_RequestsWithoutHeader_AreUnchanged()
    {
        await using var host = await Harness.StartAsync(callerIdentity: false);

        var call = await host.CallAsync("find_symbol", FindSymbolArguments, QueryToken);
        Assert.IsFalse(call.IsError, call.Body.ToString());
        Assert.AreEqual(1, call.Body.GetProperty("results").GetArrayLength());

        var delegateBearer = await host.RpcAsync("tools/list", "{}", DelegateToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, delegateBearer.Status, "with no delegate tokens it is just an unknown bearer");
    }

    // ==== logging ==================================================================================

    [TestMethod]
    public async Task RefusalLogs_CarryTheReason_NeverTokenMaterial()
    {
        await using var host = await Harness.StartAsync();
        const string sub = "user-secretive-42";

        var forged = host.UserAssertion(sub: sub, key: CallerAssertionSigner.NewKey());
        var expired = host.Sign(CallerAssertionSigner.UserClaims(DateTimeOffset.UtcNow.AddMinutes(-10), sub: sub));
        var slackClaims = CallerAssertionSigner.UserClaims(DateTimeOffset.UtcNow, sub: $"slack:conn-9:{sub}");
        slackClaims["idp"] = "slack";
        var slack = host.Sign(slackClaims);
        var hostileJti = host.Sign(CallerAssertionSigner.UserClaims(DateTimeOffset.UtcNow.AddMinutes(-10), sub: sub, jti: "jti\nforged-line"));

        await host.RpcAsync("tools/list", "{}", DelegateToken, forged);
        await host.RpcAsync("tools/list", "{}", DelegateToken, expired);
        await host.RpcAsync("tools/list", "{}", DelegateToken, slack);
        await host.RpcAsync("tools/list", "{}", DelegateToken, hostileJti);
        await host.RpcAsync("tools/list", "{}", DelegateToken, "not.a-jws");
        using (await host.SnapshotPageAsync(QueryToken, host.WidgetsHash, forged)) { }

        var logs = host.Logs();
        foreach (var reason in new[]
                 {
                     CallerAssertionReasons.BadSignature, CallerAssertionReasons.Expired, CallerAssertionReasons.IdpNotAllowed,
                     CallerAssertionReasons.Malformed, CallerAssertionGate.AssertionNotAllowedCode
                 })
            StringAssert.Contains(logs, reason);
        StringAssert.Contains(logs, "kid-a", "the configured key id is logged");
        StringAssert.Contains(logs, "jti-1", "a plain signed jti is logged");

        foreach (var secret in new[] { sub, DelegateToken, QueryToken, "forged-line", "not.a-jws" }
                     .Concat(new[] { forged, expired, slack, hostileJti }.SelectMany(a => a.Split('.'))))
            Assert.IsFalse(logs.Contains(secret, StringComparison.Ordinal), $"the logs must not carry '{secret}'");
    }

    // ==== helpers ==================================================================================

    internal static string EnsureBody() =>
        JsonSerializer.Serialize(ServiceTestFixtures.Request(repo: "https://github.com/acme/gadgets", commit: "commit-g1"), ServiceJson.Options);

    internal static void AssertUnauthorized(RpcResponse response, string code, string bearerError)
    {
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.Status, response.Raw);
        Assert.AreEqual($$"""{"error":"{{code}}"}""", response.Raw);
        Assert.AreEqual($"Bearer error=\"{bearerError}\"", response.Challenge);
    }

    internal static async Task AssertUnauthorizedAsync(HttpResponseMessage response, string code, string bearerError)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, raw);
        Assert.AreEqual($$"""{"error":"{{code}}"}""", raw);
        Assert.AreEqual($"Bearer error=\"{bearerError}\"", response.Headers.WwwAuthenticate.ToString());
    }

    internal static async Task AssertForbiddenAsync(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, raw);
        Assert.AreEqual($$"""{"error":"{{CallerAssertionGate.NotAllowedCode}}"}""", raw);
        Assert.AreEqual(0, response.Headers.WwwAuthenticate.Count, "a policy refusal is not an authentication challenge");
    }

    internal static string? ErrorCode(JsonElement body) =>
        body.GetProperty("meta").TryGetProperty("error", out var error) ? error.GetProperty("code").GetString() : null;

    internal static string WithoutTimestamp(JsonElement body)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(body.GetRawText())!.AsObject();
        node["meta"]!.AsObject().Remove("queried_at");
        return node.ToJsonString();
    }

    internal readonly record struct ToolCall(bool IsError, JsonElement Body);

    internal readonly record struct RpcResponse(HttpStatusCode Status, string Raw, string Challenge, JsonElement? Result);

    /// <summary>A call filter registered after the service's own: it sees the caller the tool would see.</summary>
    internal sealed class CallProbe
    {
        private int _calls;
        public int Calls => _calls;
        public ConcurrentBag<(string Name, CallerPrincipal? Principal)> Principals { get; } = [];

        public void Record(string name, CallerPrincipal? principal)
        {
            Interlocked.Increment(ref _calls);
            Principals.Add((name, principal));
        }
    }

    internal sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public string Text => string.Join('\n', _lines);

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _lines);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (category != CallerAssertionGate.LoggerCategory)
                    return;
                // Record the rendered message AND every structured value, since a sink may persist either.
                var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                    ? string.Join(' ', pairs.Select(p => $"{p.Key}={p.Value}"))
                    : "";
                lines.Enqueue($"{formatter(state, exception)} | {values} | {exception}");
            }
        }
    }

    internal sealed class Harness : IAsyncDisposable
    {
        public byte[] KeyA { get; } = CallerAssertionSigner.NewKey();
        public byte[] KeyB { get; } = CallerAssertionSigner.NewKey();
        public string WidgetsHash { get; private set; } = "";
        public CallProbe Probe { get; } = new();
        public SnapshotService Service { get; private set; } = null!;
        public FakeSnapshotWorker Worker { get; private set; } = null!;
        private readonly CapturingLoggerProvider _logs = new();
        private HttpClient Client { get; set; } = null!;
        private WebApplication App { get; set; } = null!;
        private IndexDatabase Db { get; set; } = null!;
        private string DbPath { get; set; } = "";
        private int _nextId;

        public static async Task<Harness> StartAsync(
            bool callerIdentity = true, bool readPolicy = false, string[]? apps = null,
            Func<ServiceOptions, ServiceOptions>? configure = null, Action<IndexDatabase>? seed = null)
        {
            var harness = new Harness();
            var dbPath = ServiceTestFixtures.NewDbPath();
            var db = new IndexDatabase(dbPath);
            db.RunMigrations();
            var request = ServiceTestFixtures.Request(repo: Widgets, commit: "commit-w1");
            var snapId = ServiceTestFixtures.PublishComplete(db, request, symbolCount: 1);
            var snapshots = new SnapshotStore(db.GetConnection());
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            snapshots.SetBranchPointer(snapshots.EnsureBranch(snapshots.GetRepositoryId(Widgets)!.Value, "main", true, now), snapId, now);
            seed?.Invoke(db);

            var options = ServiceTestFixtures.NewOptions(dbPath, controlToken: ControlToken, queryToken: readPolicy ? null : QueryToken) with
            {
                ContributeToken = ContributeToken,
                ReadPolicy = readPolicy
                    ? new ReadAuthorizationPolicy
                    {
                        Enabled = true,
                        Principals = [new ReadPrincipal { Token = ReaderToken, Repositories = new HashSet<string> { Widgets } }]
                    }
                    : new ReadAuthorizationPolicy()
            };
            if (callerIdentity)
            {
                options = options with
                {
                    DelegateTokens = [DelegateToken],
                    CallerAssertion = new CallerAssertionOptions
                    {
                        Keys = CallerKeyRing.Create([("kid-a", harness.KeyA, "tenant-a"), ("kid-b", harness.KeyB, "tenant-b")]),
                        Audience = CallerAssertionSigner.Audience,
                        Issuers = new HashSet<string>(StringComparer.Ordinal) { CallerAssertionSigner.Issuer },
                        Apps = new HashSet<string>(apps ?? [], StringComparer.Ordinal)
                    }
                };
            }

            var worker = new FakeSnapshotWorker(db);
            if (configure is not null)
                options = configure(options);
            var service = SnapshotService.Start(options, worker, db);

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(harness._logs);
            builder.Logging.SetMinimumLevel(LogLevel.Trace);
            ServiceApp.RegisterServices(builder, options, service);
            builder.Services.Configure<McpServerOptions>(o => o.Filters.Request.CallToolFilters.Add(next => async (context, ct) =>
            {
                var http = context.Services!.GetRequiredService<IHttpContextAccessor>();
                var name = context.Params?.Arguments is { } args && args.TryGetValue("name", out var value) ? value.GetString() ?? "" : "";
                harness.Probe.Record(name, CallerAssertionGate.CallerPrincipalAccessor(http)());
                return await next(context, ct);
            }));
            var app = builder.Build();
            ServiceApp.MapEndpoints(app, options);
            await app.StartAsync();

            harness.App = app;
            harness.Client = app.GetTestClient();
            harness.Service = service;
            harness.Worker = worker;
            harness.Db = db;
            harness.DbPath = dbPath;
            harness.WidgetsHash = request.ToIdentity().Hash;
            return harness;
        }

        public string Logs() => _logs.Text;

        /// <summary>Every route endpoint the app maps (for route-inventory guards).</summary>
        public IReadOnlyList<Microsoft.AspNetCore.Routing.RouteEndpoint> RouteEndpoints() =>
            ((Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)App).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
                .ToList();

        /// <summary>How many grant rows the catalog holds (read on its own connection).</summary>
        public long GrantRows()
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = DbPath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM repository_grants;";
            return (long)cmd.ExecuteScalar()!;
        }

        public string UserAssertion(string tenant = "tenant-a", string sub = "user-1", string jti = "jti-1", byte[]? key = null)
        {
            var kid = tenant == "tenant-b" ? "kid-b" : "kid-a";
            var claims = CallerAssertionSigner.UserClaims(DateTimeOffset.UtcNow, tenantId: tenant, sub: sub, jti: jti);
            return CallerAssertionSigner.Sign(key ?? (kid == "kid-b" ? KeyB : KeyA), kid, claims);
        }

        public string Sign(Dictionary<string, object?> claims, string kid = "kid-a") =>
            CallerAssertionSigner.Sign(kid == "kid-b" ? KeyB : KeyA, kid, claims);

        public async Task<ToolCall> CallAsync(string tool, string argumentsJson, string token, params string[] assertions)
        {
            var response = await RpcAsync("tools/call", $$"""{"name":"{{tool}}","arguments":{{argumentsJson}}}""", token, assertions);
            Assert.AreEqual(HttpStatusCode.OK, response.Status, response.Raw);
            var result = response.Result!.Value;
            var isError = result.TryGetProperty("isError", out var flag) && flag.GetBoolean();
            var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
            return new ToolCall(isError, JsonDocument.Parse(text).RootElement.Clone());
        }

        /// <summary>When set, every <c>/mcp</c> request carries it as the <c>X-Sextant-Repository</c> header.</summary>
        public string? RepositoryHeader { get; set; }

        public async Task<RpcResponse> RpcAsync(string method, string paramsJson, string token, params string[] assertions)
        {
            var (status, raw, challenge, payload) = await SendRpcAsync(method, paramsJson, token, assertions);
            if (status != HttpStatusCode.OK)
                return new RpcResponse(status, raw, challenge, null);

            using var rpc = JsonDocument.Parse(payload);
            Assert.IsTrue(rpc.RootElement.TryGetProperty("result", out var result), payload);
            return new RpcResponse(status, raw, challenge, result.Clone());
        }

        /// <summary>One JSON-RPC exchange on <c>/mcp</c>, returned as sent back (no assertion on its shape).</summary>
        public async Task<(HttpStatusCode Status, string Raw, string Challenge, string Payload)> SendRpcAsync(
            string method, string paramsJson, string token, params string[] assertions)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Content = new StringContent(
                    $$"""{"jsonrpc":"2.0","id":{{Interlocked.Increment(ref _nextId)}},"method":"{{method}}","params":{{paramsJson}}}""",
                    Encoding.UTF8, "application/json")
            };
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            foreach (var assertion in assertions)
                request.Headers.TryAddWithoutValidation(Header, assertion);
            if (RepositoryHeader is not null)
                request.Headers.TryAddWithoutValidation(ServiceApp.RepositoryHeader, RepositoryHeader);

            using var response = await Client.SendAsync(request);
            var raw = await response.Content.ReadAsStringAsync();
            var challenge = response.Headers.WwwAuthenticate.ToString();
            var payload = raw.Contains("data:", StringComparison.Ordinal)
                ? string.Concat(raw.Split('\n')
                    .Where(l => l.StartsWith("data:", StringComparison.Ordinal))
                    .Select(l => l["data:".Length..].Trim()))
                : raw;
            return (response.StatusCode, raw, challenge, payload);
        }

        /// <summary>Takes the single-writer lease from this service (another owner claims it) and waits until it notices.</summary>
        public async Task StealLeaseAsync()
        {
            ExecuteOnCatalog("UPDATE writer_lease SET owner_token = 'thief' WHERE id = 1;");
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!Service.LeaseLost && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            Assert.IsTrue(Service.LeaseLost, "the service noticed it lost the lease");
        }

        /// <summary>Runs one statement on the catalog through a connection of its own (not the service's).</summary>
        public void ExecuteOnCatalog(string sql)
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = DbPath,
                Pooling = false
            }.ToString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        public Task<HttpResponseMessage> SnapshotPageAsync(string token, string identityHash, params string[] assertions)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"/query/snapshots/{identityHash}/symbols");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            foreach (var assertion in assertions)
                request.Headers.TryAddWithoutValidation(Header, assertion);
            return Client.SendAsync(request);
        }

        public Task<HttpResponseMessage> ControlAsync(
            HttpMethod method, string path, string token, string? assertion = null, string? jsonBody = null)
        {
            var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (assertion is not null)
                request.Headers.TryAddWithoutValidation(Header, assertion);
            if (jsonBody is not null)
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            return Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
            Service.Dispose();
            SqliteTestDatabase.Delete(DbPath, Db);
        }
    }
}
