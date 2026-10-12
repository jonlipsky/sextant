using Sextant.Core;

namespace Sextant.Core.Tests;

[TestClass]
public class DaemonDiscoveryTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sextant_daemon_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));
        Directory.CreateDirectory(Path.Combine(_tempDir, ".sextant"));
    }

    [TestCleanup]
    public void TestCleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public async Task FindRunningDaemonAsync_ReturnsNull_WhenNoPidFile()
    {
        var result = await DaemonDiscovery.FindRunningDaemonAsync(_tempDir);
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task FindRunningDaemonAsync_ReturnsNull_WhenInvalidPidFile()
    {
        File.WriteAllText(Path.Combine(_tempDir, ".sextant", "daemon.pid"), "not-a-number\nalso-bad");

        var result = await DaemonDiscovery.FindRunningDaemonAsync(_tempDir);
        Assert.IsNull(result);

        // PID file should be cleaned up
        Assert.IsFalse(File.Exists(Path.Combine(_tempDir, ".sextant", "daemon.pid")));
    }

    [TestMethod]
    public async Task FindRunningDaemonAsync_ReturnsNull_WhenProcessNotRunning()
    {
        // Use a PID that is very unlikely to be running
        File.WriteAllText(Path.Combine(_tempDir, ".sextant", "daemon.pid"), "999999999\n12345");

        var result = await DaemonDiscovery.FindRunningDaemonAsync(_tempDir);
        Assert.IsNull(result);

        // Stale PID file should be cleaned up
        Assert.IsFalse(File.Exists(Path.Combine(_tempDir, ".sextant", "daemon.pid")));
    }

    [TestMethod]
    public async Task FindRunningDaemonAsync_ReturnsNull_WhenEmptyPidFile()
    {
        File.WriteAllText(Path.Combine(_tempDir, ".sextant", "daemon.pid"), "");

        var result = await DaemonDiscovery.FindRunningDaemonAsync(_tempDir);
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task FindRunningDaemonAsync_ReturnsNull_WhenSingleLinePidFile()
    {
        File.WriteAllText(Path.Combine(_tempDir, ".sextant", "daemon.pid"), "12345");

        var result = await DaemonDiscovery.FindRunningDaemonAsync(_tempDir);
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task FindRunningDaemonAsync_ReturnsNull_WhenNoRepoRoot()
    {
        var noGitDir = Path.Combine(Path.GetTempPath(), $"no_git_{Guid.NewGuid():N}");
        Directory.CreateDirectory(noGitDir);
        try
        {
            var result = await DaemonDiscovery.FindRunningDaemonAsync(noGitDir);
            Assert.IsNull(result);
        }
        finally
        {
            Directory.Delete(noGitDir, recursive: true);
        }
    }

    [TestMethod]
    public void SpawnDaemon_DoesNotThrow()
    {
        // The process may or may not start depending on environment; the method must not throw.
        System.Diagnostics.Process? process = null;
        try
        {
            process = DaemonDiscovery.SpawnDaemon(
                dbPath: Path.Combine(_tempDir, ".sextant", "sextant.db"),
                repoRoot: _tempDir);
            Assert.IsTrue(process is null || process.Id > 0);
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
                process.Dispose();
            }
        }
    }

    [TestMethod]
    public async Task EnsureDaemonRunningAsync_LogsNoDaemonDetected()
    {
        var messages = new List<string>();

        await DaemonDiscovery.EnsureDaemonRunningAsync(
            dbPath: Path.Combine(_tempDir, ".sextant", "sextant.db"),
            repoRoot: _tempDir,
            log: msg => messages.Add(msg));

        Assert.IsTrue(messages.Any(m => m.Contains("No daemon detected")));
    }

    [TestMethod]
    public void AutoSpawnDaemon_DefaultsToTrue()
    {
        var config = new SextantConfiguration();
        Assert.IsTrue(config.AutoSpawnDaemon);
    }

    [TestMethod]
    public void AutoSpawnDaemon_LoadedFromJson()
    {
        var json = """{ "auto_spawn_daemon": false }""";
        File.WriteAllText(Path.Combine(_tempDir, "sextant.json"), json);

        var config = SextantConfiguration.Load(_tempDir);
        Assert.IsFalse(config.AutoSpawnDaemon);
    }

    [TestMethod]
    public void AutoSpawnDaemon_EnvVarOverridesConfig()
    {
        var json = """{ "auto_spawn_daemon": true }""";
        File.WriteAllText(Path.Combine(_tempDir, "sextant.json"), json);

        Environment.SetEnvironmentVariable("SEXTANT_AUTO_SPAWN_DAEMON", "false");
        try
        {
            var config = SextantConfiguration.Load(_tempDir);
            Assert.IsFalse(config.AutoSpawnDaemon);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SEXTANT_AUTO_SPAWN_DAEMON", null);
        }
    }

    [TestMethod]
    public void AutoSpawnDaemon_EnvVarZeroDisables()
    {
        Environment.SetEnvironmentVariable("SEXTANT_AUTO_SPAWN_DAEMON", "0");
        try
        {
            var config = SextantConfiguration.Load(_tempDir);
            Assert.IsFalse(config.AutoSpawnDaemon);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SEXTANT_AUTO_SPAWN_DAEMON", null);
        }
    }
}
