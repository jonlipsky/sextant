using System.Diagnostics;
using System.Net.Http;

namespace Sextant.Core;

public sealed record DaemonInfo(int Pid, int Port);

public static class DaemonDiscovery
{
    private const string PidFileName = "daemon.pid";
    private const int HealthCheckTimeoutMs = 2000;

    /// <summary>
    /// Finds a running daemon by reading the PID file and verifying the process is alive.
    /// Cleans up stale PID files if the process is dead.
    /// </summary>
    public static async Task<DaemonInfo?> FindRunningDaemonAsync(string? repoRoot = null)
    {
        var root = repoRoot ?? SextantConfiguration.FindRepoRoot(Directory.GetCurrentDirectory());
        if (root == null) return null;

        var pidFile = Path.Combine(root, ".sextant", PidFileName);
        if (!File.Exists(pidFile)) return null;

        string content;
        try
        {
            content = await File.ReadAllTextAsync(pidFile);
        }
        catch (IOException)
        {
            return null;
        }

        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2
            || !int.TryParse(lines[0].Trim(), out var pid)
            || !int.TryParse(lines[1].Trim(), out var port))
        {
            // Invalid format — clean up
            TryDeleteFile(pidFile);
            return null;
        }

        // Check if the process is alive
        if (!IsProcessRunning(pid))
        {
            TryDeleteFile(pidFile);
            return null;
        }

        // Confirm health endpoint responds
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(HealthCheckTimeoutMs) };
            var response = await client.GetAsync($"http://localhost:{port}/health");
            if (response.IsSuccessStatusCode)
                return new DaemonInfo(pid, port);
        }
        catch
        {
            // Health check failed — process exists but daemon isn't responding
        }

        // Process exists but health check failed; don't clean up PID file
        // since the process might still be starting up
        return null;
    }

    /// <summary>
    /// Spawns a new daemon process in the background.
    /// </summary>
    public static Process? SpawnDaemon(string? dbPath = null, string? repoRoot = null)
    {
        var exePath = GetSextantExecutablePath();
        if (exePath == null) return null;

        var arguments = new List<string>();

        // If running under dotnet host, we need "dotnet <dll> daemon"
        // If running as published exe, just "<exe> daemon"
        string fileName;
        if (exePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            fileName = "dotnet";
            arguments.Add(exePath);
        }
        else
        {
            fileName = exePath;
        }

        arguments.Add("daemon");

        if (!string.IsNullOrEmpty(dbPath))
        {
            arguments.Add("--db");
            arguments.Add(dbPath);
        }

        if (!string.IsNullOrEmpty(repoRoot))
        {
            arguments.Add("--repo-root");
            arguments.Add(repoRoot);
        }

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        try
        {
            return Process.Start(psi);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// High-level fire-and-forget: checks for a running daemon, spawns one if needed.
    /// </summary>
    public static async Task EnsureDaemonRunningAsync(
        string? dbPath = null,
        string? repoRoot = null,
        Action<string>? log = null)
    {
        try
        {
            var existing = await FindRunningDaemonAsync(repoRoot);
            if (existing != null)
            {
                log?.Invoke($"Daemon already running (pid={existing.Pid}, port={existing.Port})");
                return;
            }

            log?.Invoke("No daemon detected — auto-spawning...");
            var process = SpawnDaemon(dbPath, repoRoot);
            if (process != null)
                log?.Invoke($"Daemon spawned (pid={process.Id})");
            else
                log?.Invoke("Failed to spawn daemon");
        }
        catch (Exception ex)
        {
            log?.Invoke($"Daemon auto-spawn error: {ex.Message}");
        }
    }

    /// <summary>
    /// Stops a running daemon by sending a kill signal.
    /// Returns true if the daemon was found and stopped.
    /// </summary>
    public static async Task<bool> StopDaemonAsync(string? repoRoot = null)
    {
        var root = repoRoot ?? SextantConfiguration.FindRepoRoot(Directory.GetCurrentDirectory());
        if (root == null) return false;

        var pidFile = Path.Combine(root, ".sextant", PidFileName);
        if (!File.Exists(pidFile)) return false;

        string content;
        try
        {
            content = await File.ReadAllTextAsync(pidFile);
        }
        catch (IOException)
        {
            return false;
        }

        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 1 || !int.TryParse(lines[0].Trim(), out var pid))
        {
            TryDeleteFile(pidFile);
            return false;
        }

        try
        {
            var process = Process.GetProcessById(pid);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            TryDeleteFile(pidFile);
            return true;
        }
        catch (ArgumentException)
        {
            // Process not found — already dead
            TryDeleteFile(pidFile);
            return false;
        }
        catch (InvalidOperationException)
        {
            // Process already exited
            TryDeleteFile(pidFile);
            return false;
        }
    }

    /// <summary>
    /// Gets detailed status from a running daemon's status endpoint.
    /// Returns the JSON status string, or null if the daemon is not reachable.
    /// </summary>
    public static async Task<string?> GetDaemonStatusAsync(int port)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(HealthCheckTimeoutMs) };
            var response = await client.GetAsync($"http://localhost:{port}/status");
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadAsStringAsync();
        }
        catch
        {
            // Not reachable
        }
        return null;
    }

    private static bool IsProcessRunning(int pid)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch { /* best effort */ }
    }

    private static string? GetSextantExecutablePath()
    {
        // Get the entry assembly's location — works for both dotnet run and published exe
        var entryAssembly = System.Reflection.Assembly.GetEntryAssembly();
        if (entryAssembly == null) return null;

        var location = entryAssembly.Location;

        // If location is a .dll, we're running under dotnet host
        if (!string.IsNullOrEmpty(location) && location.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return location;

        // Published single-file or native — use the process path
        var processPath = Environment.ProcessPath;
        return processPath;
    }
}
