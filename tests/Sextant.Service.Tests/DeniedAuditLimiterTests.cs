using System.Net;
using System.Text.Json;
using Sextant.Service.Tests;
using Sextant.Store;
using static Sextant.Service.Tests.CallerAssertionHttpTests;

namespace Sextant.Service.Tests;

/// <summary>Issue #198 (item 3): a flood of refusals writes a bounded number of denied audit rows.</summary>
[TestClass]
public class DeniedAuditLimiterTests
{
    private const string Retire = "/control/branches/retire";

    [TestMethod]
    public void Flood_RecordsTheBudget_ThenCountsTheRest_AndReportsItOnTheNextWindowsFirstRow()
    {
        var clock = new ManualClock();
        var limiter = new DeniedAuditLimiter(clock);
        var admitted = 0;
        for (var i = 0; i < 1000; i++)
        {
            if (limiter.TryAdmit("a", "retire", out var suppressed))
            {
                admitted++;
                Assert.AreEqual(0, suppressed, "nothing was left out yet");
            }
            if (i == 0)
                Assert.AreEqual(1, admitted, "the first refusal is always recorded");
        }
        Assert.AreEqual(DeniedAuditLimiter.MaxRowsPerWindow, admitted);

        clock.Advance(DeniedAuditLimiter.Window);
        Assert.IsTrue(limiter.TryAdmit("a", "retire", out var carried));
        Assert.AreEqual(1000 - DeniedAuditLimiter.MaxRowsPerWindow, carried, "the left-out refusals are reported");
        Assert.IsTrue(limiter.TryAdmit("a", "retire", out carried));
        Assert.AreEqual(0, carried, "reported once");
    }

    [TestMethod]
    public void ADifferentCallerOrRoute_IsNotSuppressed()
    {
        var limiter = new DeniedAuditLimiter(new ManualClock());
        for (var i = 0; i < 100; i++)
            limiter.TryAdmit("a", "retire", out _);

        Assert.IsFalse(limiter.TryAdmit("a", "retire", out _));
        Assert.IsTrue(limiter.TryAdmit("b", "retire", out _), "another caller");
        Assert.IsTrue(limiter.TryAdmit("a", "backup", out _), "another route");
        Assert.IsTrue(limiter.TryAdmit(null, "retire", out _), "no caller");
    }

    [TestMethod]
    public void ManyDistinctCallers_StayBounded_AndEachFirstRefusalIsRecordedUntilTheKeyCap()
    {
        var clock = new ManualClock();
        var limiter = new DeniedAuditLimiter(clock);
        var recorded = 0;
        for (var i = 0; i < DeniedAuditLimiter.MaxKeys * 3; i++)
            if (limiter.TryAdmit($"caller-{i}", "retire", out _))
                recorded++;
        // Past the key cap the overflow entry shares one budget.
        Assert.AreEqual(DeniedAuditLimiter.MaxKeys + DeniedAuditLimiter.MaxRowsPerWindow, recorded);

        clock.Advance(DeniedAuditLimiter.Window);
        Assert.IsTrue(limiter.TryAdmit("caller-new", "retire", out _), "expired entries are dropped to make room");
    }

    [TestMethod]
    public void WithSuppressed_AppendsTheCountOnlyWhenSomethingWasLeftOut()
    {
        Assert.AreEqual("not_allowed;idp=x", DeniedAuditLimiter.WithSuppressed("not_allowed;idp=x", 0));
        Assert.AreEqual("not_allowed;idp=x;suppressed=7", DeniedAuditLimiter.WithSuppressed("not_allowed;idp=x", 7));
    }

    [TestMethod]
    public async Task RefusalFlood_OverHttp_WritesBoundedRows_ForTheFloodingCallerOnly()
    {
        await using var host = await Harness.StartAsync();
        var body = JsonSerializer.Serialize(new { repository = Widgets, branch = "feature" });
        const int flood = DeniedAuditLimiter.MaxRowsPerWindow * 3;

        for (var i = 0; i < flood; i++)
        {
            using var response = await host.ControlAsync(HttpMethod.Post, Retire, ControlToken, host.UserAssertion(sub: "flooder"), body);
            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode, "every refusal is still answered");
        }

        Assert.AreEqual(DeniedAuditLimiter.MaxRowsPerWindow, host.Service.RecentAudit(limit: 500, action: AuditAction.Retire).Count);

        using (var other = await host.ControlAsync(HttpMethod.Post, Retire, ControlToken, host.UserAssertion(sub: "bystander"), body))
            Assert.AreEqual(HttpStatusCode.Forbidden, other.StatusCode);
        using (var otherRoute = await host.ControlAsync(HttpMethod.Post, "/control/retention", ControlToken, host.UserAssertion(sub: "flooder"), null))
            Assert.AreEqual(HttpStatusCode.Forbidden, otherRoute.StatusCode);

        Assert.AreEqual(DeniedAuditLimiter.MaxRowsPerWindow + 1, host.Service.RecentAudit(limit: 500, action: AuditAction.Retire).Count,
            "a different caller is recorded");
        Assert.AreEqual(1, host.Service.RecentAudit(limit: 500, action: AuditAction.Retention).Count, "a different route is recorded");
    }
}
