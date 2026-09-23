using Sextant.Mcp.Tools;
using Sextant.Service.Host;

namespace Sextant.Service.Tests;

/// <summary>
/// Phase 17 — criterion 1. The remote HTTP MCP surface (<see cref="ServiceApp.RemoteQueryTools"/>) is a
/// default-DENY allowlist: it must expose ONLY the index-query tools that route through the fail-closed
/// <c>DatabaseProvider.TryBeginRead</c> gate, and must NEVER expose the local-only tools that bypass or
/// out-scope it — <c>get_source_context</c> (reads an arbitrary absolute path off disk),
/// <c>get_daemon_status</c> (probes a local daemon), and <c>get_base_snapshot_symbols</c> (issue #60: it
/// serves a caller-supplied <c>identity_hash</c> not bound to the repository scope <c>TryBeginRead</c>
/// authorizes, so it is a single-tenant LOCAL planner tool only; the service surface federates snapshots
/// through the per-hash-authorized <c>/query/snapshots/{identityHash}/symbols</c> HTTP endpoint instead).
/// These are pure reflection asserts, no host needed.
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
        Assert.IsFalse(ServiceApp.RemoteQueryTools.Contains(typeof(GetBaseSnapshotSymbolsTool)),
            "get_base_snapshot_symbols serves a caller-supplied identity_hash not bound to the TryBeginRead repository scope (#60) — it is a single-tenant local planner tool and must never be reachable over the multi-tenant remote surface");
    }

    [TestMethod]
    public void RemoteSurface_IsExactlyTheAuthorizedQueryTools()
    {
        // Discover every real MCP tool type in the Sextant.Mcp assembly by its attribute (matched by name so
        // the test needs no ModelContextProtocol package reference).
        var allToolTypes = typeof(GetSourceContextTool).Assembly.GetTypes()
            .Where(t => t.GetCustomAttributes(false).Any(a => a.GetType().Name == "McpServerToolTypeAttribute"))
            .ToHashSet();

        var excluded = new HashSet<Type>
        {
            typeof(GetSourceContextTool), typeof(GetDaemonStatusTool), typeof(GetBaseSnapshotSymbolsTool)
        };
        var expected = allToolTypes.Where(t => !excluded.Contains(t)).ToList();

        // The allowlist must equal ALL MCP tools minus the two local-only tools. This fails closed on drift
        // in BOTH directions: a newly added tool is not silently exposed remotely (it must be triaged and
        // added here), and a query tool is not silently dropped from the remote surface.
        CollectionAssert.AreEquivalent(expected, ServiceApp.RemoteQueryTools.ToList(),
            "the remote allowlist must equal all MCP tools minus the unauthenticated local-only tools");
    }
}
