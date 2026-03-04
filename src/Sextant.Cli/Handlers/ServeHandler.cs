using Microsoft.Extensions.Hosting;

namespace Sextant.Cli.Handlers;

internal static class ServeHandler
{
    public static async Task<int> RunAsync(bool useStdio, int? port, string? db, string? profile)
    {
        var config = Core.SextantConfiguration.Load();
        var dbPath = DbResolver.Resolve(db, profile, config);
        if (dbPath == null) return 1;

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
            var host = Mcp.McpServerSetup.CreateMcpHost([], dbPath, config.LogsPath);
            await host.Build().RunAsync();
            return 0;
        }

        var httpPort = port ?? 3001;
        Console.WriteLine($"Starting Sextant MCP server on http://localhost:{httpPort}");
        var app = Mcp.McpServerSetup.CreateHttpMcpHost([], httpPort, dbPath, config.LogsPath);
        await app.RunAsync();
        return 0;
    }
}
