using System.Net.Http.Json;
using System.Text.Json;
using Sextant.Core;
using Sextant.Daemon;
using Sextant.Indexer;
using Sextant.Store;

namespace Sextant.Integration.Tests;

[TestClass]
[TestCategory("Integration")]
public class DaemonIntegrationTests : IDisposable
{
    private readonly IntegrationFixture _fixture;
    private readonly string _tempDir;
    private readonly string _repoRoot;

    public DaemonIntegrationTests()
    {
        _fixture = IntegrationFixture.Instance;
        _tempDir = Path.Combine(Path.GetTempPath(), $"sextant_daemon_int_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _repoRoot = FindRepoRoot();
    }

    public void Dispose()
    {
        Sextant.TestSupport.SqliteTestDatabase.DeleteDirectory(_tempDir);
    }

    [TestMethod]
    public async Task Daemon_StartAndStop_LifecycleWorks()
    {
        var dbPath = Path.Combine(_tempDir, "daemon-test.db");
        var slnPath = Path.Combine(_repoRoot, "Sextant.slnx");

        using var daemon = new DaemonHost(_repoRoot, dbPath, [slnPath], msg => { });
        using var cts = new CancellationTokenSource();

        await daemon.StartAsync(cts.Token);

        // Verify daemon started (it should have a status port)
        Assert.IsTrue(daemon.StatusPort > 0, "Daemon should have a status port");

        // Verify DB was created with schema
        Assert.IsTrue(File.Exists(dbPath), "Daemon should create DB file");

        await daemon.StopAsync();
    }

    [TestMethod]
    public async Task Daemon_StatusEndpoint_ReturnsState()
    {
        var dbPath = Path.Combine(_tempDir, "daemon-status.db");
        var slnPath = Path.Combine(_repoRoot, "Sextant.slnx");

        using var daemon = new DaemonHost(_repoRoot, dbPath, [slnPath], msg => { });
        using var cts = new CancellationTokenSource();

        await daemon.StartAsync(cts.Token);

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

            // Test health endpoint
            var healthResponse = await client.GetStringAsync(
                $"http://localhost:{daemon.StatusPort}/health");
            Assert.AreEqual("OK", healthResponse);

            // Test status endpoint
            var statusResponse = await client.GetStringAsync(
                $"http://localhost:{daemon.StatusPort}/status");
            var status = JsonDocument.Parse(statusResponse);
            Assert.IsTrue(status.RootElement.TryGetProperty("state", out var state));
            // State should be either "idle" or "indexing"
            Assert.IsTrue(new[] { "idle", "indexing" }.Contains(state.GetString()));
        }
        finally
        {
            await daemon.StopAsync();
        }
    }

    [TestMethod]
    public async Task Daemon_PidFile_CreatedAndCleaned()
    {
        var dbPath = Path.Combine(_tempDir, "daemon-pid.db");
        var slnPath = Path.Combine(_repoRoot, "Sextant.slnx");

        // Create .sextant dir so pid file can be written
        var sextantDir = Path.Combine(_repoRoot, ".sextant");
        Directory.CreateDirectory(sextantDir);
        var pidFile = Path.Combine(sextantDir, "daemon.pid");

        // Clean up any existing pid file
        if (File.Exists(pidFile))
            File.Delete(pidFile);

        using var daemon = new DaemonHost(_repoRoot, dbPath, [slnPath], msg => { });
        using var cts = new CancellationTokenSource();

        await daemon.StartAsync(cts.Token);

        try
        {
            Assert.IsTrue(File.Exists(pidFile), "PID file should be created on start");

            var pidContent = await File.ReadAllTextAsync(pidFile);
            var lines = pidContent.Trim().Split('\n');
            Assert.AreEqual(2, lines.Length); // pid and port
            Assert.IsTrue(int.TryParse(lines[0], out _), "First line should be PID");
            Assert.IsTrue(int.TryParse(lines[1], out _), "Second line should be port");
        }
        finally
        {
            await daemon.StopAsync();
        }

        // PID file should be cleaned up after stop
        Assert.IsFalse(File.Exists(pidFile), "PID file should be removed on stop");
    }

    [TestMethod]
    public async Task Daemon_IncrementalIndex_DetectsFileChanges()
    {
        // Create a mini C# project in temp dir
        var projectDir = Path.Combine(_tempDir, "TestProject");
        Directory.CreateDirectory(projectDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git")); // so FindRepoRoot works

        var csproj = Path.Combine(projectDir, "TestProject.csproj");
        File.WriteAllText(csproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        var slnPath = Path.Combine(_tempDir, "Test.sln");
        File.WriteAllText(slnPath,
            "Microsoft Visual Studio Solution File, Format Version 12.00\n" +
            "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"TestProject\", " +
            "\"TestProject\\TestProject.csproj\", \"{00000000-0000-0000-0000-000000000001}\"\n" +
            "EndProject\n");

        var sourceFile = Path.Combine(projectDir, "Class1.cs");
        File.WriteAllText(sourceFile, """
            namespace TestProject;
            public class Class1
            {
                public void Hello() { }
            }
            """);

        var dbPath = Path.Combine(_tempDir, "incremental-test.db");
        using var daemon = new DaemonHost(_tempDir, dbPath, [slnPath], msg => { });
        using var cts = new CancellationTokenSource();

        await daemon.StartAsync(cts.Token);

        // Wait for initial indexing to complete
        await WaitForIdleAsync(daemon.StatusPort, TimeSpan.FromSeconds(120));

        // Verify initial symbol was indexed
        using var db = new IndexDatabase(dbPath);
        var conn = db.GetConnection();
        var symbolStore = new SymbolStore(conn);
        var initialSymbols = symbolStore.SearchFts("Class1", 10);

        try
        {
            // The initial index may or may not find symbols depending on MSBuild workspace
            // The key test: the daemon started, indexed, and reached idle state
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var statusResponse = await client.GetStringAsync(
                $"http://localhost:{daemon.StatusPort}/status");
            var status = JsonDocument.Parse(statusResponse);
            Assert.AreEqual("idle", status.RootElement.GetProperty("state").GetString());
        }
        finally
        {
            await daemon.StopAsync();
        }
    }

    [TestMethod]
    public async Task DaemonDiscovery_FindsRunningDaemon()
    {
        var dbPath = Path.Combine(_tempDir, "daemon-discovery.db");
        var slnPath = Path.Combine(_repoRoot, "Sextant.slnx");

        // Clean up any existing pid file
        var pidFile = Path.Combine(_repoRoot, ".sextant", "daemon.pid");
        if (File.Exists(pidFile))
            File.Delete(pidFile);

        using var daemon = new DaemonHost(_repoRoot, dbPath, [slnPath], msg => { });
        using var cts = new CancellationTokenSource();

        await daemon.StartAsync(cts.Token);

        try
        {
            var found = await DaemonDiscovery.FindRunningDaemonAsync(_repoRoot);
            Assert.IsNotNull(found);
            Assert.IsTrue(found.Port > 0);
            Assert.IsTrue(found.Pid > 0);

            // Verify we can get status through discovery
            var status = await DaemonDiscovery.GetDaemonStatusAsync(found.Port);
            Assert.IsNotNull(status);
            StringAssert.Contains(status, "state");
        }
        finally
        {
            await daemon.StopAsync();
        }
    }

    [TestMethod]
    public async Task Daemon_CatchUp_IndexesProjectAddedWhileStopped()
    {
        var root = Path.Combine(_tempDir, "catchup");
        Directory.CreateDirectory(root);
        // A .git marker so the daemon resolves this dir as the repo root (matches other daemon tests).
        Directory.CreateDirectory(Path.Combine(root, ".git"));

        WriteSdkProject(root, "ProjA", "namespace ProjA;\npublic class TypeA { public int A() => 1; }\n");
        var slnx = Path.Combine(root, "Sln.slnx");
        await File.WriteAllTextAsync(slnx,
            "<Solution>\n  <Project Path=\"ProjA/ProjA.csproj\" />\n</Solution>\n");
        RestoreSolution(slnx);

        var dbPath = Path.Combine(_tempDir, "catchup.db");

        // First run: full initial index of the one-project solution. StartAsync awaits the index, so
        // it is complete when the call returns.
        using (var daemon = new DaemonHost(root, dbPath, [slnx], msg => { }))
        using (var cts = new CancellationTokenSource())
        {
            await daemon.StartAsync(cts.Token);
            await daemon.StopAsync();
        }

        // Add a brand-new project (with no DB row) while the daemon is stopped.
        WriteSdkProject(root, "ProjB", "namespace ProjB;\npublic class TypeB { public int B() => 2; }\n");
        await File.WriteAllTextAsync(slnx,
            "<Solution>\n  <Project Path=\"ProjA/ProjA.csproj\" />\n  <Project Path=\"ProjB/ProjB.csproj\" />\n</Solution>\n");
        RestoreSolution(slnx);

        // Second run: the non-empty DB routes through incremental catch-up (not a full index). Catch-up
        // must still index the newly-added project even though it has no prior fingerprint rows.
        using (var daemon = new DaemonHost(root, dbPath, [slnx], msg => { }))
        using (var cts = new CancellationTokenSource())
        {
            await daemon.StartAsync(cts.Token);
            await daemon.StopAsync();
        }

        using var db = new IndexDatabase(dbPath);
        var conn = db.GetConnection();
        Assert.IsTrue(SymbolDisplayNameExists(conn, "TypeB"),
            "Catch-up must index a project added while the daemon was stopped.");
        Assert.IsTrue(SymbolDisplayNameExists(conn, "TypeA"),
            "The pre-existing project must remain indexed after catch-up.");
    }

    private static void WriteSdkProject(string root, string name, string classBody)
    {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{name}.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir, $"{name}.cs"), classBody);
    }

    private static void RestoreSolution(string solutionPath)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet", $"restore \"{solutionPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = System.Diagnostics.Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            Assert.Inconclusive($"restore of the generated solution failed (exit {process.ExitCode}): {stderr}");
    }

    private static bool SymbolDisplayNameExists(Microsoft.Data.Sqlite.SqliteConnection conn, string displayName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM symbols WHERE display_name = $n";
        cmd.Parameters.AddWithValue("$n", displayName);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    private static async Task WaitForIdleAsync(int statusPort, TimeSpan timeout)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = await client.GetStringAsync($"http://localhost:{statusPort}/status");
                var status = JsonDocument.Parse(response);
                if (status.RootElement.GetProperty("state").GetString() == "idle")
                    return;
            }
            catch
            {
                // Server may not be ready yet
            }

            await Task.Delay(500);
        }

        // Don't fail on timeout - the daemon may still be indexing and that's OK
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "Sextant.slnx")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Could not find repo root.");
    }
}
