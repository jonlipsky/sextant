using Sextant.Service.Host;

namespace Sextant.Service.Tests;

/// <summary>
/// Phase 17 / issue #61 — control vs query bind isolation. Pins the pure port-decision that lets an
/// operator expose only the query port publicly while keeping the control plane on an internal interface:
/// with no distinct query port every port serves both planes (pre-Phase-17 behavior, no-op), and with a
/// distinct query port each plane is confined to its own port so a control request on the public query
/// port — or a query request on the internal control port — is refused. Also pins the listen-URL
/// construction: the configured bind address flows into every listen URL (default loopback preserved) and
/// a bare IPv6 literal is bracketed so the URL authority stays well-formed.
/// </summary>
[TestClass]
public class BindIsolationTests
{
    [TestMethod]
    public void NoDistinctQueryPort_IsNeverWrongPlane()
    {
        // Shared-port deployments (QueryPort null, or equal to ControlPort) keep serving both planes on the
        // one port — isolation must be a strict no-op there.
        Assert.IsFalse(ServiceApp.IsWrongPlanePort(3011, 3011, null, controlPath: true));
        Assert.IsFalse(ServiceApp.IsWrongPlanePort(3011, 3011, null, controlPath: false));
        Assert.IsFalse(ServiceApp.IsWrongPlanePort(3011, 3011, 3011, controlPath: true));
        Assert.IsFalse(ServiceApp.IsWrongPlanePort(3011, 3011, 3011, controlPath: false));
    }

    [TestMethod]
    public void DistinctQueryPort_ConfinesControlToControlPort()
    {
        // Control endpoints belong on the control port only.
        Assert.IsFalse(ServiceApp.IsWrongPlanePort(3011, 3011, 3012, controlPath: true));
        Assert.IsTrue(ServiceApp.IsWrongPlanePort(3012, 3011, 3012, controlPath: true),
            "a control request on the public query port must be refused");
    }

    [TestMethod]
    public void DistinctQueryPort_ConfinesQueryToQueryPort()
    {
        // Query endpoints belong on the query port only.
        Assert.IsFalse(ServiceApp.IsWrongPlanePort(3012, 3011, 3012, controlPath: false));
        Assert.IsTrue(ServiceApp.IsWrongPlanePort(3011, 3011, 3012, controlPath: false),
            "a query request on the internal control port must be refused");
    }

    [TestMethod]
    public void ListenUrls_DefaultBind_IsLoopback()
    {
        var options = ServiceTestFixtures.NewOptions(ServiceTestFixtures.NewDbPath()) with { BindAddress = "localhost", ControlPort = 3011 };
        CollectionAssert.AreEqual(
            new[] { "http://localhost:3011" },
            ServiceHostRunner.BuildListenUrls(options).ToArray(),
            "the default loopback bind must build exactly the pre-feature URL");
    }

    [TestMethod]
    public void ListenUrls_RoutableBind_UsesConfiguredAddress()
    {
        var options = ServiceTestFixtures.NewOptions(ServiceTestFixtures.NewDbPath()) with { BindAddress = "0.0.0.0", ControlPort = 3011 };
        CollectionAssert.AreEqual(
            new[] { "http://0.0.0.0:3011" },
            ServiceHostRunner.BuildListenUrls(options).ToArray(),
            "a routable bind address flows into the listen URL so the socket accepts non-loopback connections");
    }

    [TestMethod]
    public void ListenUrls_DistinctQueryPort_BindsBothPortsToConfiguredAddress()
    {
        var options = ServiceTestFixtures.NewOptions(ServiceTestFixtures.NewDbPath()) with
        {
            BindAddress = "0.0.0.0",
            ControlPort = 3011,
            QueryPort = 3012
        };
        CollectionAssert.AreEqual(
            new[] { "http://0.0.0.0:3011", "http://0.0.0.0:3012" },
            ServiceHostRunner.BuildListenUrls(options).ToArray(),
            "a distinct query port is bound to the same configured interface as the control port");
    }

    [TestMethod]
    public void ListenUrls_IPv6Literal_IsBracketed()
    {
        var options = ServiceTestFixtures.NewOptions(ServiceTestFixtures.NewDbPath()) with { BindAddress = "::1", ControlPort = 3011 };
        CollectionAssert.AreEqual(
            new[] { "http://[::1]:3011" },
            ServiceHostRunner.BuildListenUrls(options).ToArray(),
            "a bare IPv6 literal must be bracketed so the URL authority is well-formed");
    }
}
