using Sextant.Service.Host;

namespace Sextant.Service.Tests;

/// <summary>
/// Phase 17 / issue #61 — control vs query bind isolation. Pins the pure port-decision that lets an
/// operator expose only the query port publicly while keeping the control plane on an internal interface:
/// with no distinct query port every port serves both planes (pre-Phase-17 behavior, no-op), and with a
/// distinct query port each plane is confined to its own port so a control request on the public query
/// port — or a query request on the internal control port — is refused.
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
}
