using Microsoft.Extensions.Hosting;

namespace Sextant.Cli.Handlers;

internal static class ServeHandler
{
    public static async Task<int> RunAsync(bool useStdio, int? port, string? db, string? profile)
    {
        var config = Core.SextantConfiguration.Load();
        var dbPath = DbResolver.Resolve(db, profile, config);
        if (dbPath == null) return 1;

        WarnIfIndexNotReady(dbPath);

        if (config.AutoSpawnDaemon)
        {
            var repoRoot = Core.SextantConfiguration.FindRepoRoot(Directory.GetCurrentDirectory());
            _ = Core.DaemonDiscovery.EnsureDaemonRunningAsync(
                dbPath: dbPath,
                repoRoot: repoRoot,
                log: msg => Console.Error.WriteLine($"[daemon] {msg}"));
        }

        if (useStdio)
        {
            var host = Mcp.McpServerSetup.CreateMcpHost([], dbPath, Core.SextantConfiguration.LogsPathFor(dbPath));
            await host.Build().RunAsync();
            return 0;
        }

        var httpPort = port ?? 3001;
        Console.WriteLine($"Starting Sextant MCP server on http://localhost:{httpPort}");
        var app = Mcp.McpServerSetup.CreateHttpMcpHost([], httpPort, dbPath, Core.SextantConfiguration.LogsPathFor(dbPath));
        await app.RunAsync();
        return 0;
    }

    // Surfaces an actionable rebuild message when the database exists but is not a complete, servable
    // index (older/incompatible schema, or a compact schema awaiting its first full run). The server
    // still starts — an auto-spawned daemon rebuilds in the background and the MCP tools echo the same
    // guidance — but the operator is told up front rather than seeing empty results.
    private static void WarnIfIndexNotReady(string dbPath)
    {
        if (!File.Exists(dbPath))
            return;
        try
        {
            using var db = new Store.IndexDatabase(dbPath);
            var readiness = db.CheckReadiness();
            if (!readiness.Ready)
                Console.Error.WriteLine($"[sextant] {readiness.Message}");
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // A malformed/locked database is reported by the tools themselves; don't block serving.
        }
    }
}
