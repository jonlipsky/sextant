using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Sextant.Daemon;

public sealed class StatusServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Func<StatusInfo> _getStatus;
    private CancellationTokenSource? _cts;

    public int Port { get; private set; }

    public StatusServer(Func<StatusInfo> getStatus)
    {
        _getStatus = getStatus;
    }

    public void Start()
    {
        Port = FindAvailablePort();
        _listener.Prefixes.Add($"http://localhost:{Port}/");
        _listener.Start();

        _cts = new CancellationTokenSource();
        _ = ListenAsync(_cts.Token);
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                HandleRequest(context);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    private void HandleRequest(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "";
        var response = context.Response;

        try
        {
            if (path == "/health")
            {
                response.StatusCode = 200;
                response.ContentType = "text/plain";
                var buffer = "OK"u8.ToArray();
                response.ContentLength64 = buffer.Length;
                response.OutputStream.Write(buffer);
            }
            else if (path == "/status")
            {
                var status = _getStatus();
                var json = JsonSerializer.Serialize(status, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
                });
                response.StatusCode = 200;
                response.ContentType = "application/json";
                var buffer = System.Text.Encoding.UTF8.GetBytes(json);
                response.ContentLength64 = buffer.Length;
                response.OutputStream.Write(buffer);
            }
            else
            {
                response.StatusCode = 404;
            }
        }
        finally
        {
            response.Close();
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _listener.Stop();
        _listener.Close();
        _cts?.Dispose();
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

public sealed class StatusInfo
{
    public required string State { get; init; }
    public int QueuedFiles { get; init; }
    public int BackgroundTasks { get; init; }
    public long? LastIndexedAt { get; init; }

    // Progress fields (populated when State == "indexing")
    public string? Phase { get; init; }
    public string? CurrentProject { get; init; }
    public int ProjectIndex { get; init; }
    public int ProjectCount { get; init; }
    public long? IndexingStartedAt { get; init; }
    public long? ElapsedMs { get; init; }
}
