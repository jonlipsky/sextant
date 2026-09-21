using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sextant.Core;
using Sextant.Service;
using Sextant.Store;

namespace Sextant.Service.Host;

/// <summary>
/// Composition root for the standalone index service, shared by the <c>Sextant.Service.Host</c> entry
/// point and the <c>sextant service</c> CLI convenience command. It wires the durable catalog database,
/// the single-node local worker, and the ASP.NET Core control/query/health endpoints.
///
/// The caller MUST have already run <c>MSBuildLocator.RegisterDefaults()</c> before invoking this (the
/// local worker pulls in Roslyn). This method does not register MSBuildLocator itself so it can be
/// called from a process that already did (the CLI).
/// </summary>
public static class ServiceHostRunner
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var config = SextantConfiguration.Load();
        var options = ServiceOptions.FromEnvironment(config);

        var paths = new ServicePaths(options.Volumes);
        var database = new IndexDatabase(options.CatalogDbPath, IndexWriteOptions.FromConfiguration(config));

        var worker = new LocalIndexerSnapshotWorker(
            database, config, new PersistentVolumeCheckoutProvider(paths), Console.Error.WriteLine);

        SnapshotService service;
        try
        {
            service = SnapshotService.Start(options, worker, database);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to start Sextant index service: {ex.Message}");
            database.Dispose();
            return 1;
        }

        try
        {
            var builder = WebApplication.CreateBuilder(args);
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole();
            ServiceApp.RegisterServices(builder, options, service);

            var urls = new List<string> { $"http://localhost:{options.ControlPort}" };
            if (options.QueryPort is int queryPort && queryPort != options.ControlPort)
                urls.Add($"http://localhost:{queryPort}");
            builder.WebHost.UseUrls([.. urls]);

            var app = builder.Build();
            ServiceApp.MapEndpoints(app, options);

            Console.WriteLine($"Sextant index service listening on {string.Join(", ", urls)}");
            Console.WriteLine($"  Catalog:   {Path.GetFullPath(options.CatalogDbPath)}");
            Console.WriteLine($"  Checkouts: {options.Volumes.CheckoutRoot}");
            Console.WriteLine($"  Scratch:   {options.Volumes.ScratchRoot}");

            // WebApplication.RunAsync(string?) shadows the IHost token overload; cast so Ctrl+C / the CLI
            // cancellation token drives a graceful shutdown.
            await ((IHost)app).RunAsync(cancellationToken);
            return 0;
        }
        finally
        {
            service.Dispose();
            database.Dispose();
        }
    }
}
