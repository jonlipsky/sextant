using System.ComponentModel;
using System.Text.Json;
using Sextant.Core;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

public static class GetDaemonStatusTool
{
    [McpServerTool(Name = "get_daemon_status"), Description(
        "Get the status of the Sextant daemon including indexing progress. " +
        "Returns state (idle/indexing), current phase, project being indexed, " +
        "progress counts, elapsed time, and queue depth.")]
    public static async Task<string> GetDaemonStatus()
    {
        var repoRoot = SextantConfiguration.FindRepoRoot(Directory.GetCurrentDirectory());
        var daemon = await DaemonDiscovery.FindRunningDaemonAsync(repoRoot);

        if (daemon == null)
        {
            return ResponseBuilder.BuildEmpty("Daemon is not running. Start it with 'sextant daemon start'.");
        }

        var statusJson = await DaemonDiscovery.GetDaemonStatusAsync(daemon.Port);
        if (statusJson == null)
        {
            return ResponseBuilder.BuildEmpty("Daemon is running but not responding to status queries.");
        }

        var statusDoc = JsonDocument.Parse(statusJson);
        var root = statusDoc.RootElement;

        var result = new Dictionary<string, object?>
        {
            ["pid"] = daemon.Pid,
            ["port"] = daemon.Port,
            ["state"] = root.TryGetProperty("state", out var s) ? s.GetString() : "unknown",
            ["queued_files"] = root.TryGetProperty("queued_files", out var qf) ? qf.GetInt32() : 0,
            ["background_tasks"] = root.TryGetProperty("background_tasks", out var bt) ? bt.GetInt32() : 0,
        };

        if (root.TryGetProperty("last_indexed_at", out var lia) && lia.ValueKind != JsonValueKind.Null)
            result["last_indexed_at"] = lia.GetInt64();

        // Progress details
        if (root.TryGetProperty("phase", out var phase) && phase.ValueKind != JsonValueKind.Null)
            result["phase"] = phase.GetString();
        if (root.TryGetProperty("current_project", out var cp) && cp.ValueKind != JsonValueKind.Null)
            result["current_project"] = cp.GetString();
        if (root.TryGetProperty("project_index", out var pi))
            result["project_index"] = pi.GetInt32();
        if (root.TryGetProperty("project_count", out var pc))
            result["project_count"] = pc.GetInt32();
        if (root.TryGetProperty("elapsed_ms", out var elapsed) && elapsed.ValueKind != JsonValueKind.Null)
            result["elapsed_ms"] = elapsed.GetInt64();

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return ResponseBuilder.Build(new List<Dictionary<string, object?>> { result }, now);
    }
}
