using System.Net;
using System.Text.Json;
using Sextant.Service.Observability;
using Sextant.Store;
using static Sextant.Service.Tests.CallerAssertionHttpTests;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #355 at the HTTP boundary: the per-repository metric series (lag, cost) of <c>/control/metrics</c> name only
/// the repositories the caller's tenant holds a tenant-wide grant for, in JSON and in Prometheus text. The operator
/// (no assertion) sees every repository; a user caller is refused the route altogether.
/// </summary>
[TestClass]
public class MetricsTenantVisibilityHttpTests
{
    private const string Gadgets = "https://github.com/acme/gadgets";

    private sealed class Resolver : IRemoteDefaultBranchResolver, IRemoteBranchHeadResolver
    {
        public string? ResolveDefaultBranch(string repositoryRemoteUrl) => "main";

        public string? ResolveBranchHead(string repositoryRemoteUrl, string branch, CancellationToken cancellationToken = default) =>
            "remote-head-" + repositoryRemoteUrl;
    }

    [TestMethod]
    public async Task LagFamilies_NeverNameAnotherTenantsRepository()
    {
        await using var host = await StartAsync();
        await AssertBothFormatsAsync(host, family: "sextant_default_branch_lag", jsonProperty: "freshness", jsonKey: "repository");
    }

    [TestMethod]
    public async Task CostFamilies_NeverNameAnotherTenantsRepository()
    {
        await using var host = await StartAsync();
        await AssertBothFormatsAsync(host, family: "sextant_repository_index_ms", jsonProperty: "cost_by_repository", jsonKey: "repository_scope");
    }

    private static async Task<Harness> StartAsync()
    {
        var host = await Harness.StartAsync(
            seed: db =>
            {
                // The freshness signal needs a default-branch head with a recorded commit.
                GrantServiceTests.PublishOnBranch(db, Widgets, "commit-w9", "main", isDefault: true);
                GrantServiceTests.PublishOnBranch(db, Gadgets, "commit-g1", "main", isDefault: true);
            },
            remoteDefaults: new Resolver(),
            configure: o => o with { FreshnessCheckInterval = TimeSpan.FromMinutes(5) });
        var a = host.Sign(CallerAssertionSigner.ApplicationClaims(DateTimeOffset.UtcNow, tenantId: "tenant-a"), "kid-a");
        var b = host.Sign(CallerAssertionSigner.ApplicationClaims(DateTimeOffset.UtcNow, tenantId: "tenant-b"), "kid-b");
        using (var grantA = await host.ControlAsync(HttpMethod.Put, "/control/grants/tenant", ControlToken, a,
            JsonSerializer.Serialize(new { repository = Widgets })))
            Assert.IsTrue(grantA.IsSuccessStatusCode, await grantA.Content.ReadAsStringAsync());
        using (var grantB = await host.ControlAsync(HttpMethod.Put, "/control/grants/tenant", ControlToken, b,
            JsonSerializer.Serialize(new { repository = Gadgets })))
            Assert.IsTrue(grantB.IsSuccessStatusCode, await grantB.Content.ReadAsStringAsync());
        return host;
    }

    private static async Task AssertBothFormatsAsync(Harness host, string family, string jsonProperty, string jsonKey)
    {
        var tenantA = host.Sign(CallerAssertionSigner.ApplicationClaims(DateTimeOffset.UtcNow, tenantId: "tenant-a"), "kid-a");
        var tenantB = host.Sign(CallerAssertionSigner.ApplicationClaims(DateTimeOffset.UtcNow, tenantId: "tenant-b"), "kid-b");

        // The operator sees both once the family has been populated (the freshness pass runs in the background).
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            var seen = await JsonRepositoriesAsync(host, null, jsonProperty, jsonKey);
            if (seen.Contains(Widgets) && seen.Contains(Gadgets))
                break;
            Assert.IsTrue(DateTime.UtcNow < deadline, $"the operator never saw both repositories in {jsonProperty}");
            await Task.Delay(100);
        }
        var prometheusAll = await PrometheusAsync(host, null);
        StringAssert.Contains(prometheusAll, $"{family}");
        StringAssert.Contains(prometheusAll, Widgets);
        StringAssert.Contains(prometheusAll, Gadgets);

        foreach (var (assertion, own, foreign) in new[] { (tenantA, Widgets, Gadgets), (tenantB, Gadgets, Widgets) })
        {
            CollectionAssert.AreEqual(new[] { own }, (await JsonRepositoriesAsync(host, assertion, jsonProperty, jsonKey)).ToArray());
            var text = await PrometheusAsync(host, assertion);
            StringAssert.Contains(text, $"{family}");
            StringAssert.Contains(text, own);
            Assert.IsFalse(text.Contains(foreign, StringComparison.OrdinalIgnoreCase), $"{foreign} leaked into the Prometheus output");
            Assert.IsFalse(text.Contains(foreign.Split('/')[^1], StringComparison.OrdinalIgnoreCase));
            using var raw = await host.ControlAsync(HttpMethod.Get, "/control/metrics", ControlToken, assertion);
            Assert.IsFalse((await raw.Content.ReadAsStringAsync()).Contains(foreign, StringComparison.OrdinalIgnoreCase),
                $"{foreign} leaked into the JSON output");
        }

        using var userDenied = await host.ControlAsync(HttpMethod.Get, "/control/metrics", ControlToken,
            host.Sign(CallerAssertionSigner.UserClaims(DateTimeOffset.UtcNow), "kid-a"));
        Assert.AreEqual(HttpStatusCode.Forbidden, userDenied.StatusCode);
    }

    private static async Task<List<string>> JsonRepositoriesAsync(Harness host, string? assertion, string property, string key)
    {
        using var response = await host.ControlAsync(HttpMethod.Get, "/control/metrics", ControlToken, assertion);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty(property).EnumerateArray()
            .Select(e => e.GetProperty(key).GetString()!).Order(StringComparer.Ordinal).ToList();
    }

    private static async Task<string> PrometheusAsync(Harness host, string? assertion)
    {
        using var response = await host.ControlAsync(HttpMethod.Get, "/control/metrics?format=prometheus", ControlToken, assertion);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }
}
