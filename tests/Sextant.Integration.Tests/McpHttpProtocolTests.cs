using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Sextant.Mcp;

namespace Sextant.Integration.Tests;

[TestClass]
[TestCategory("Integration")]
public class McpHttpProtocolTests
{
    private IntegrationFixture _fixture = null!;
    private Microsoft.AspNetCore.Builder.WebApplication? _app;
    private int _port;

    [TestInitialize]
    public async Task TestInitialize()
    {
        _fixture = IntegrationFixture.Instance;
        _port = FindAvailablePort();
        _app = McpServerSetup.CreateHttpMcpHost([], _port, _fixture.DbPath);
        await _app.StartAsync();
    }

    [TestCleanup]
    public async Task TestCleanup()
    {
        if (_app != null)
            await _app.StopAsync();
    }

    [TestMethod]
    public async Task HttpMcp_PostEndpoint_AcceptsJsonRpcInitialize()
    {
        using var client = new HttpClient();
        var initRequest = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = "test", version = "1.0" }
            }
        };

        var json = JsonSerializer.Serialize(initRequest);
        var request = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{_port}/mcp")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        // MCP Streamable HTTP requires client to accept both JSON and SSE
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

        var response = await client.SendAsync(request);

        Assert.IsTrue(response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Accepted,
            $"Expected success, got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    [TestMethod]
    public async Task HttpMcp_GetEndpoint_ReturnsResponse()
    {
        using var client = new HttpClient();
        var response = await client.GetAsync($"http://localhost:{_port}/mcp");

        // SSE endpoint should return 200 with text/event-stream or a valid HTTP response
        Assert.AreNotEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task HttpMcp_ServerIsListening_OnConfiguredPort()
    {
        using var client = new HttpClient();
        // Just verify we can connect and get a response (not a connection refused)
        try
        {
            var response = await client.GetAsync($"http://localhost:{_port}/");
            // Any response (even 404) means the server is listening
            Assert.IsTrue(true, "Server is listening");
        }
        catch (HttpRequestException ex) when (ex.InnerException is SocketException)
        {
            Assert.Fail("Server should be listening on the configured port");
        }
    }

    private static int FindAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
