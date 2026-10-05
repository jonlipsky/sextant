using System.Reflection;
using ModelContextProtocol.Server;
using Sextant.Mcp;
using Sextant.Mcp.Tools;
using Sextant.Service.Host;

namespace Sextant.Service.Tests;

/// <summary>
/// Phase 17 — criterion 1, narrowed by S12. The remote HTTP MCP surface (<see cref="ServiceApp.RemoteQueryTools"/>) is
/// a default-DENY allowlist of the tools agents use: it exposes only index-query tools that route through the
/// fail-closed <c>DatabaseProvider.TryBeginRead</c> gate, plus the service-only <c>list_repositories</c> and
/// <c>search_symbols</c>, and NEVER the local-only
/// tools that bypass or out-scope that gate: <c>get_source_context</c> (reads an arbitrary absolute path off
/// disk), <c>get_daemon_status</c> (probes a local daemon), and <c>get_base_snapshot_symbols</c> (issue #60: it
/// serves a caller-supplied <c>identity_hash</c> not bound to the repository scope <c>TryBeginRead</c> authorizes,
/// so it is a single-tenant LOCAL planner tool only; the service surface federates snapshots through the
/// per-hash-authorized <c>/query/snapshots/{identityHash}/symbols</c> HTTP endpoint instead). These are pure
/// reflection asserts, no host needed; <see cref="RemoteToolSurfaceGuardTests"/> checks the same set over
/// <c>/mcp</c>.
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
    public void RemoteSurface_IsGatedIndexToolsPlusTheServiceOnlyTools()
    {
        // RemoteQueryTools is the one place the set is decided (the agent-behaviour harness picks it); whatever it
        // holds, each entry declares exactly one tool, and every tool but the service-only ones (which read the
        // caller's grants, not one index) reads the index through the DatabaseProvider (so through the fail-closed
        // TryBeginRead gate).
        var types = ServiceApp.RemoteQueryTools;
        Assert.AreEqual(types.Count, types.Distinct().Count(), "no tool type is listed twice");
        CollectionAssert.Contains(types.ToList(), typeof(Sextant.Service.Grants.ListRepositoriesTool));
        foreach (var type in types)
        {
            var tools = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
                .ToList();
            Assert.AreEqual(1, tools.Count, $"{type.Name} declares one tool");
            if (ServiceOnlyTools.Contains(type))
                continue;
            Assert.IsTrue(tools[0].GetParameters().Any(p => p.ParameterType == typeof(DatabaseProvider)),
                $"{type.Name} reads through the DatabaseProvider gate");
        }
    }

    [TestMethod]
    public void ServiceOnlyTools_AreTheOnlyMcpToolsInTheServiceAssembly()
    {
        // A tool added to Sextant.Service is service-only by construction; it must be triaged into the allowlist.
        var serviceTools = typeof(Sextant.Service.Grants.ListRepositoriesTool).Assembly.GetTypes()
            .Where(t => t.GetCustomAttributes(false).Any(a => a.GetType().Name == "McpServerToolTypeAttribute"))
            .ToList();
        CollectionAssert.AreEquivalent(ServiceOnlyTools, serviceTools);
    }

    private static readonly Type[] ServiceOnlyTools =
        [typeof(Sextant.Service.Grants.ListRepositoriesTool), typeof(Sextant.Service.Search.SearchSymbolsTool)];
}