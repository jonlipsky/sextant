namespace Sextant.Cli.Handlers;

internal static class DaemonHandler
{
    public static async Task<int> StartAsync(string? db, string? profile, string? repoRootOpt)
    {
        var config = Core.SextantConfiguration.Load();
        var dbPath = DbResolver.Resolve(db, profile, config);
        if (dbPath == null) return 1;

        var repoRoot = repoRootOpt
            ?? Core.SextantConfiguration.FindRepoRoot(Directory.GetCurrentDirectory())
            ?? Directory.GetCurrentDirectory();

        var solutions = config.Solutions;
        if (solutions.Count == 0)
        {
            var slnFiles = Directory.GetFiles(repoRoot, "*.sln", SearchOption.TopDirectoryOnly);
            solutions = slnFiles.ToList();
        }

        if (solutions.Count == 0)
        {
            Console.Error.WriteLine("No solutions found. Specify solutions in sextant.json or place .sln files in the repo root.");
            return 1;
        }

        Console.WriteLine("Sextant Daemon");
        Console.WriteLine($"  Repo root: {repoRoot}");
        Console.WriteLine($"  Database:  {Path.GetFullPath(dbPath)}");
        Console.WriteLine($"  Solutions: {string.Join(", ", solutions.Select(Path.GetFileName))}");
        Console.WriteLine();

        using var fileLogger = Core.FileLogger.Open(config.LogsPath, "daemon.log");
        var logCallback = fileLogger.CreateCallback(Console.WriteLine);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        using var daemon = new Daemon.DaemonHost(
            repoRoot, dbPath, solutions.ToArray(), logCallback);

        await daemon.StartAsync(cts.Token);

        Console.WriteLine("Daemon running. Press Ctrl+C to stop.");
        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException) { }

        await daemon.StopAsync();
        Console.WriteLine("Daemon stopped.");
        return 0;
    }

    public static async Task<int> StatusAsync()
    {
        var repoRoot = Core.SextantConfiguration.FindRepoRoot(Directory.GetCurrentDirectory());
        var daemon = await Core.DaemonDiscovery.FindRunningDaemonAsync(repoRoot);

        if (daemon == null)
        {
            Console.WriteLine("Daemon is not running.");
            return 1;
        }

        Console.WriteLine($"Daemon is running (pid={daemon.Pid}, port={daemon.Port})");

        var status = await Core.DaemonDiscovery.GetDaemonStatusAsync(daemon.Port);
        if (status == null) return 0;

        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(status);
            var root = doc.RootElement;

            var state = root.GetProperty("state").GetString() ?? "unknown";
            Console.WriteLine($"  State: {state}");

            if (root.TryGetProperty("queued_files", out var qf) && qf.GetInt32() > 0)
                Console.WriteLine($"  Queued files: {qf.GetInt32()}");
            if (root.TryGetProperty("background_tasks", out var bt) && bt.GetInt32() > 0)
                Console.WriteLine($"  Background tasks: {bt.GetInt32()}");

            if (root.TryGetProperty("last_indexed_at", out var lia) && lia.ValueKind != System.Text.Json.JsonValueKind.Null)
            {
                var ts = DateTimeOffset.FromUnixTimeMilliseconds(lia.GetInt64());
                Console.WriteLine($"  Last indexed: {ts.LocalDateTime:yyyy-MM-dd HH:mm:ss}");
            }

            if (state == "indexing")
            {
                if (root.TryGetProperty("phase", out var phase) && phase.ValueKind != System.Text.Json.JsonValueKind.Null)
                    Console.WriteLine($"  Phase: {phase.GetString()}");

                if (root.TryGetProperty("current_project", out var cp) && cp.ValueKind != System.Text.Json.JsonValueKind.Null)
                    Console.WriteLine($"  Current project: {cp.GetString()}");

                if (root.TryGetProperty("project_count", out var pc) && pc.GetInt32() > 0)
                {
                    var idx = root.TryGetProperty("project_index", out var pi) ? pi.GetInt32() : 0;
                    Console.WriteLine($"  Progress: project {idx}/{pc.GetInt32()}");
                }

                if (root.TryGetProperty("elapsed_ms", out var elapsed) && elapsed.ValueKind != System.Text.Json.JsonValueKind.Null)
                {
                    var elapsedTs = TimeSpan.FromMilliseconds(elapsed.GetInt64());
                    Console.WriteLine($"  Elapsed: {elapsedTs:hh\\:mm\\:ss}");
                }
            }
        }
        catch
        {
            Console.WriteLine(status);
        }

        return 0;
    }

    public static async Task<int> StopAsync()
    {
        var repoRoot = Core.SextantConfiguration.FindRepoRoot(Directory.GetCurrentDirectory());
        var stopped = await Core.DaemonDiscovery.StopDaemonAsync(repoRoot);

        if (stopped)
        {
            Console.WriteLine("Daemon stopped.");
            return 0;
        }

        Console.WriteLine("No running daemon found.");
        return 1;
    }
}
