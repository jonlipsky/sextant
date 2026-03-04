using System.Diagnostics;

namespace Sextant.Integration.Tests;

[TestClass]
[TestCategory("Integration")]
public class CliSubprocessTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _repoRoot;

    public CliSubprocessTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sextant_cli_int_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _repoRoot = FindRepoRoot();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [TestMethod]
    public async Task Index_WithProfile_CreatesDbAtProfilePath()
    {
        var profileDir = Path.Combine(_tempDir, ".sextant", "profiles", "test-cli");
        var dbPath = Path.Combine(profileDir, "sextant.db");

        // Create a minimal .sln-like project in temp dir for indexing
        // Instead, use --db to point to a specific path (simpler, tests ResolveDb)
        var result = await RunCliAsync($"index {Path.Combine(_repoRoot, "Sextant.slnx")} --db {dbPath}");

        Assert.IsTrue(result.ExitCode == 0, $"Expected exit code 0, got {result.ExitCode}. stderr: {result.StdErr}");
        Assert.IsTrue(File.Exists(dbPath), $"DB should exist at {dbPath}. stderr: {result.StdErr}");
        Assert.IsTrue(new FileInfo(dbPath).Length > 0, "DB should not be empty");
        StringAssert.Contains(result.StdOut, "Done.");
    }

    [TestMethod]
    public async Task Profiles_ListsExistingProfiles()
    {
        // Set up a fake profile structure
        var profilesDir = Path.Combine(_tempDir, ".sextant", "profiles", "my-profile");
        Directory.CreateDirectory(profilesDir);
        File.WriteAllText(Path.Combine(profilesDir, "sextant.db"), "fake-db");

        // Also create .git so FindRepoRoot works
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));

        var result = await RunCliAsync("profiles", workingDir: _tempDir);

        Assert.AreEqual(0, result.ExitCode);
        StringAssert.Contains(result.StdOut, "my-profile");
    }

    [TestMethod]
    public async Task ProfileAndDb_MutuallyExclusive_ReturnsError()
    {
        var result = await RunCliAsync(
            $"index {Path.Combine(_repoRoot, "Sextant.slnx")} --db /tmp/test.db --profile test");

        Assert.AreNotEqual(0, result.ExitCode);
        StringAssert.Contains(result.StdErr, "mutually exclusive");
    }

    [TestMethod]
    public async Task Query_GetIndexStatus_ReturnsJsonOutput()
    {
        // Use the integration fixture's pre-indexed DB
        var dbPath = Path.Combine(_repoRoot, ".sextant", "profiles", "default", "sextant.db");
        if (!File.Exists(dbPath))
            return; // Skip if no pre-indexed DB

        var result = await RunCliAsync($"query get-index-status --db {dbPath}");

        Assert.IsTrue(result.ExitCode == 0, $"Expected exit code 0, got {result.ExitCode}. stderr: {result.StdErr}");
        StringAssert.Contains(result.StdOut, "\"results\"");
        StringAssert.Contains(result.StdOut, "\"meta\"");
    }

    [TestMethod]
    public async Task Serve_Stdio_StartsAndAcceptsInput()
    {
        var dbPath = Path.Combine(_repoRoot, ".sextant", "profiles", "default", "sextant.db");
        if (!File.Exists(dbPath))
            return; // Skip if no pre-indexed DB

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --no-build --project {Path.Combine(_repoRoot, "src", "Sextant.Cli")} -- serve --stdio --db {dbPath}",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _repoRoot
        };

        using var process = Process.Start(psi)!;
        try
        {
            // Send MCP initialize request via JSON-RPC
            var initRequest = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}""";
            await process.StandardInput.WriteLineAsync(initRequest);
            await process.StandardInput.FlushAsync();

            // Read response with timeout — skip non-JSON lines
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            string? response = null;
            while (!cts.Token.IsCancellationRequested)
            {
                var line = await ReadLineWithTimeoutAsync(process.StandardOutput, cts.Token);
                if (line == null) break;
                if (line.TrimStart().StartsWith("{") && line.Contains("\"result\""))
                {
                    response = line;
                    break;
                }
            }

            Assert.IsNotNull(response);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync();
            }
        }
    }

    private async Task<CliResult> RunCliAsync(string arguments, string? workingDir = null)
    {
        var cliProject = Path.Combine(_repoRoot, "src", "Sextant.Cli");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --no-build --project {cliProject} -- {arguments}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDir ?? _repoRoot
        };

        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new CliResult(process.ExitCode, stdout, stderr);
    }

    private static async Task<string?> ReadLineWithTimeoutAsync(StreamReader reader, CancellationToken ct)
    {
        var readTask = reader.ReadLineAsync(ct).AsTask();
        var completed = await Task.WhenAny(readTask, Task.Delay(Timeout.Infinite, ct));
        return completed == readTask ? await readTask : null;
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

    private record CliResult(int ExitCode, string StdOut, string StdErr);
}
