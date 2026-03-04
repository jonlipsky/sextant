using Sextant.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Sextant.Mcp;

public static class McpServerSetup
{
    /// <summary>
    /// Creates an MCP host using stdio transport (primary, for Claude Code integration).
    /// </summary>
    public static IHostBuilder CreateMcpHost(string[] args, string? dbPath = null, string? logsPath = null)
    {
        var fileLogger = logsPath != null
            ? FileLogger.Open(logsPath, $"mcp-{Environment.ProcessId}.log")
            : null;

        return Host.CreateDefaultBuilder(args)
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole(options =>
                {
                    options.LogToStandardErrorThreshold = LogLevel.Trace;
                });
                if (fileLogger != null)
                    logging.AddProvider(new FileLoggerProvider(fileLogger));
            })
            .ConfigureServices(services =>
            {
                if (fileLogger != null)
                    services.AddSingleton(fileLogger);
                services.AddSingleton(new DatabaseProvider(dbPath));
                services.AddMcpServer()
                    .WithStdioServerTransport()
                    .WithToolsFromAssembly();
            });
    }

    /// <summary>
    /// Creates an MCP host using HTTP Streamable transport on the specified port.
    /// </summary>
    public static WebApplication CreateHttpMcpHost(string[] args, int port = 3001, string? dbPath = null, string? logsPath = null)
    {
        var builder = WebApplication.CreateBuilder(args);

        var fileLogger = logsPath != null
            ? FileLogger.Open(logsPath, $"mcp-{Environment.ProcessId}.log")
            : null;

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        if (fileLogger != null)
            builder.Logging.AddProvider(new FileLoggerProvider(fileLogger));

        if (fileLogger != null)
            builder.Services.AddSingleton(fileLogger);
        builder.Services.AddSingleton(new DatabaseProvider(dbPath));
        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly();

        builder.WebHost.UseUrls($"http://localhost:{port}");

        var app = builder.Build();
        app.MapMcp("/mcp");

        return app;
    }
}
