using Sextant.Mcp.Tools;
using Sextant.Service.Host;

namespace Sextant.Service.Tests;

/// <summary>
/// Phase 17 — criterion 1. The remote HTTP MCP surface (<see cref="ServiceApp.RemoteQueryTools"/>) is a
/// default-DENY allowlist: it must expose ONLY the index-query tools that route through the fail-closed
/// <c>DatabaseProvider.TryBeginRead</c> gate, and must NEVER expose the two local-only tools that bypass it
/// — <c>get_source_context</c> (reads an arbitrary absolute path off disk) and <c>get_daemon_status</c>
/// (probes a local daemon). These are pure reflection asserts, no host needed.
/// </summary>
[TestClass]
public class RemoteToolAllowlistTests
{
    [TestMethod]
    public void RemoteSurface_ExcludesUnauthenticatedLocalTools()
    {
        Assert.IsFalse(ServiceApp.RemoteQueryTools.Contains(typeof(GetSourceContextTool)),
            "get_source_context reads an arbitrary absolute path with no authorization — it must never be reachable over the remote HTTP MCP surface (criterion 1)");
        Assert.IsFalse(ServiceApp.RemoteQueryTools.Contains(typeof(GetDaemonStatusTool)),
            "get_daemon_status probes a local daemon and bypasses the read gate — excluded from the remote surface");
    }

    [TestMethod]
    public void RemoteSurface_IsExactlyTheAuthorizedQueryTools()
    {
        // Discover every real MCP tool type in the Sextant.Mcp assembly by its attribute (matched by name so
        // the test needs no ModelContextProtocol package reference).
        var allToolTypes = typeof(GetSourceContextTool).Assembly.GetTypes()
            .Where(t => t.GetCustomAttributes(false).Any(a => a.GetType().Name == "McpServerToolTypeAttribute"))
            .ToHashSet();

        var excluded = new HashSet<Type> { typeof(GetSourceContextTool), typeof(GetDaemonStatusTool) };
        var expected = allToolTypes.Where(t => !excluded.Contains(t)).ToList();

        // The allowlist must equal ALL MCP tools minus the two local-only tools. This fails closed on drift
        // in BOTH directions: a newly added tool is not silently exposed remotely (it must be triaged and
        // added here), and a query tool is not silently dropped from the remote surface.
        CollectionAssert.AreEquivalent(expected, ServiceApp.RemoteQueryTools.ToList(),
            "the remote allowlist must equal all MCP tools minus the unauthenticated local-only tools");
    }
}
