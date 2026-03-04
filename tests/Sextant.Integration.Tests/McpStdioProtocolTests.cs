using System.Diagnostics;
using System.Text.Json;

namespace Sextant.Integration.Tests;

[TestClass]
[TestCategory("Integration")]
public class McpStdioProtocolTests
{
    private IntegrationFixture _fixture = null!;
    private string _repoRoot = null!;

    [TestInitialize]
    public void Setup()
    {
        _fixture = IntegrationFixture.Instance;
        _repoRoot = FindRepoRoot();
    }

    [TestMethod]
    public async Task Stdio_Initialize_ReturnsCapabilities()
    {
        await using var server = await StartStdioServer();

        var response = await server.SendRequestAsync(1, "initialize", new
        {
            protocolVersion = "2024-11-05",
            capabilities = new { },
            clientInfo = new { name = "test", version = "1.0" }
        });

        Assert.AreEqual(1, response.GetProperty("id").GetInt32());
        var result = response.GetProperty("result");
        Assert.IsTrue(result.TryGetProperty("serverInfo", out _));
    }

    [TestMethod]
    public async Task Stdio_ToolsList_ReturnsAllTools()
    {
        await using var server = await StartStdioServer();
        await server.InitializeAsync();

        var response = await server.SendRequestAsync(2, "tools/list", new { });

        var result = response.GetProperty("result");
        var tools = result.GetProperty("tools");
        Assert.IsTrue(tools.GetArrayLength() >= 22, $"Expected >= 22 tools, got {tools.GetArrayLength()}");

        var toolNames = tools.EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .ToList();
        Assert.IsTrue(toolNames.Contains("find_symbol"));
        Assert.IsTrue(toolNames.Contains("find_references"));
        Assert.IsTrue(toolNames.Contains("get_source_context"));
        Assert.IsTrue(toolNames.Contains("find_comments"));
        Assert.IsTrue(toolNames.Contains("trace_value"));
    }

    [TestMethod]
    public async Task Stdio_ToolsCall_FindSymbol_ReturnsResults()
    {
        await using var server = await StartStdioServer();
        await server.InitializeAsync();

        var response = await server.SendRequestAsync(3, "tools/call", new
        {
            name = "find_symbol",
            arguments = new { name = "global::Sextant.Mcp.DatabaseProvider" }
        });

        var result = response.GetProperty("result");
        var content = result.GetProperty("content");
        Assert.IsTrue(content.GetArrayLength() >= 1);

        var textContent = content[0].GetProperty("text").GetString()!;
        var parsed = JsonDocument.Parse(textContent);
        var meta = parsed.RootElement.GetProperty("meta");
        Assert.IsTrue(meta.GetProperty("result_count").GetInt32() >= 1);
    }

    [TestMethod]
    public async Task Stdio_ToolsCall_GetIndexStatus_ReturnsProjects()
    {
        await using var server = await StartStdioServer();
        await server.InitializeAsync();

        var response = await server.SendRequestAsync(4, "tools/call", new
        {
            name = "get_index_status",
            arguments = new { }
        });

        var result = response.GetProperty("result");
        var content = result.GetProperty("content");
        var textContent = content[0].GetProperty("text").GetString()!;
        var parsed = JsonDocument.Parse(textContent);
        var results = parsed.RootElement.GetProperty("results");
        Assert.IsTrue(results.GetArrayLength() >= 1);
    }

    [TestMethod]
    public async Task Stdio_ToolsCall_FindReferences_ReturnsReferences()
    {
        await using var server = await StartStdioServer();
        await server.InitializeAsync();

        var response = await server.SendRequestAsync(5, "tools/call", new
        {
            name = "find_references",
            arguments = new { symbol_fqn = "global::Sextant.Store.IndexDatabase" }
        });

        var result = response.GetProperty("result");
        var content = result.GetProperty("content");
        var textContent = content[0].GetProperty("text").GetString()!;
        var parsed = JsonDocument.Parse(textContent);
        var meta = parsed.RootElement.GetProperty("meta");
        Assert.IsTrue(meta.GetProperty("result_count").GetInt32() >= 1);
    }

    private async Task<StdioServerHandle> StartStdioServer()
    {
        var cliProject = Path.Combine(_repoRoot, "src", "Sextant.Cli");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --no-build --project {cliProject} -- serve --stdio --db {_fixture.DbPath}",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _repoRoot
        };

        var process = Process.Start(psi)!;
        return new StdioServerHandle(process);
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

internal sealed class StdioServerHandle : IAsyncDisposable
{
    private readonly Process _process;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public StdioServerHandle(Process process)
    {
        _process = process;
    }

    public async Task InitializeAsync()
    {
        await SendRequestAsync(0, "initialize", new
        {
            protocolVersion = "2024-11-05",
            capabilities = new { },
            clientInfo = new { name = "integration-test", version = "1.0" }
        });

        // Send initialized notification
        var notification = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method = "notifications/initialized"
        }, JsonOptions);
        await _process.StandardInput.WriteLineAsync(notification);
        await _process.StandardInput.FlushAsync();
    }

    public async Task<JsonElement> SendRequestAsync(int id, string method, object @params)
    {
        var request = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params
        }, JsonOptions);

        await _process.StandardInput.WriteLineAsync(request);
        await _process.StandardInput.FlushAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!cts.Token.IsCancellationRequested)
        {
            var line = await _process.StandardOutput.ReadLineAsync(cts.Token);
            if (line == null) break;

            // Skip empty lines and non-JSON
            if (string.IsNullOrWhiteSpace(line) || !line.TrimStart().StartsWith("{"))
                continue;

            try
            {
                var doc = JsonDocument.Parse(line);
                // Skip notifications (no id)
                if (doc.RootElement.TryGetProperty("id", out var respId) &&
                    respId.GetInt32() == id)
                {
                    return doc.RootElement.Clone();
                }
            }
            catch (JsonException)
            {
                continue;
            }
        }

        throw new TimeoutException($"No response received for request {id} ({method})");
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(true);
            await _process.WaitForExitAsync();
        }
        _process.Dispose();
    }
}
