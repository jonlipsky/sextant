using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sextant.Core;
using Sextant.Core.Platform;
using Sextant.Service;
using Sextant.Service.Contributions;
using Sextant.Service.Placement;
using Sextant.Service.Sandbox;
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

        // Phase 15: the default (Linux, in-process) placement — the node's own capability — behind the
        // routing seam. With no native placements registered this behaves exactly like the Phase-13 direct
        // worker (single-node/local operation needs zero routing infrastructure — CRITICAL 2). Real native
        // Windows/macOS placements are wired by ProcessStack in Phase 14 behind this same seam.
        var nodeCapability = WorkerCapability.LocalDefault;
        var checkoutProvider = new PersistentVolumeCheckoutProvider(paths);
        // Criterion 2: the service worker evaluates UNTRUSTED checkouts, so wrap its MSBuild evaluation in
        // the enforced sandbox (time/memory/secret/filesystem isolation) — applied to private and public
        // repos alike. The local CLI/daemon path does not construct this worker, so it stays byte-identical.
        var sandbox = new EvaluationSandbox(options.Sandbox, paths, Console.Error.WriteLine);
        var localWorker = new LocalIndexerSnapshotWorker(
            database, config, checkoutProvider, Console.Error.WriteLine, nodeCapability, sandbox);
        var defaultPlacement = new LocalPlacement(nodeCapability, localWorker);
        var worker = new CapabilityRoutingSnapshotWorker(
            defaultPlacement,
            nativePlacements: [],
            probe: new AssumeLinuxCapableProbe(),
            policy: options.PlatformRouting,
            log: Console.Error.WriteLine);

        SnapshotService service;
        try
        {
            // #68: when a deployment REQUIRES Git-content verification, wire the real git-CLI provider over
            // the persistent checkout volume so declared blobs are verified against the repository's content
            // at the exact commit. Left unset (dev default) the service uses the Unavailable provider and the
            // fail-closed startup guard keeps required-verification deployments from silently running open.
            IGitContentProvider? gitContent = options.Contribution.RequireGitContentVerification
                ? GitCliContentProvider.ForVolume(paths)
                : null;

            service = SnapshotService.Start(options, worker, database, gitContent: gitContent);
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
