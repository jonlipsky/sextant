using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using Sextant.Core;
using Sextant.Core.Platform;
using Sextant.Service;
using Sextant.Service.Backup;
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

            var urls = BuildListenUrls(options);
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

    /// <summary>
    /// Builds the Kestrel listen URLs from the configured bind address and ports. The query surface shares
    /// the control port unless a distinct <see cref="ServiceOptions.QueryPort"/> is set. An IPv6 literal
    /// bind address is wrapped in brackets so the composed URL authority is well-formed
    /// (e.g. <c>http://[::1]:3011</c>); a hostname or IPv4 literal is used verbatim.
    /// </summary>
    internal static IReadOnlyList<string> BuildListenUrls(ServiceOptions options)
    {
        var host = FormatHostForUrl(options.BindAddress);
        var urls = new List<string> { $"http://{host}:{options.ControlPort}" };
        if (options.QueryPort is int queryPort && queryPort != options.ControlPort)
            urls.Add($"http://{host}:{queryPort}");
        return urls;
    }

    /// <summary>
    /// Wraps a bind address in brackets when it is a bare IPv6 literal so it composes into a valid URL
    /// authority. A hostname, an IPv4 literal, or an already-bracketed IPv6 literal is returned unchanged.
    /// </summary>
    private static string FormatHostForUrl(string host) =>
        !host.StartsWith('[')
        && IPAddress.TryParse(host, out var ip)
        && ip.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{host}]"
            : host;

    /// <summary>
    /// Offline DR backup (criterion 6): writes a consistent catalog + artifact backup into
    /// <paramref name="destinationDir"/> without starting the HTTP surface. It uses SQLite's online backup
    /// API for a point-in-time catalog image, but copies the immutable artifact volume separately, so it is
    /// OFFLINE-ONLY: it refuses to run while a live writer lease is held (a running service could publish or
    /// GC artifacts mid-copy). For a backup of a live service use the gated online path (POST
    /// /control/backup). Configuration comes from the same <c>SEXTANT_SERVICE_*</c> environment as the
    /// service itself, so the backup targets the operator's real catalog + artifact volume.
    /// </summary>
    public static Task<int> RunBackupAsync(string destinationDir, CancellationToken cancellationToken = default)
    {
        try
        {
            var config = SextantConfiguration.Load();
            var options = ServiceOptions.FromEnvironment(config);
            var paths = new ServicePaths(options.Volumes);

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = options.CatalogDbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();
            using var source = new SqliteConnection(connectionString);
            source.Open();

            // `sextant service backup` is OFFLINE: it copies the immutable artifact volume alongside the
            // catalog without holding the writer lease, so a concurrently-running service could publish or
            // GC artifacts mid-copy and produce a catalog/artifact mismatch. If a LIVE writer lease is held,
            // refuse and direct the operator to the gated online path (POST /control/backup), which runs the
            // copy under the writer gate. A stale/expired lease is fine (no live writer).
            var lease = WriterLease.GetCurrent(source);
            if (lease is not null && !lease.IsExpiredAt(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
            {
                Console.Error.WriteLine(
                    "Backup refused: a live service holds the writer lease on this catalog. Offline `service " +
                    "backup` cannot guarantee a consistent catalog+artifact image while a writer is active. " +
                    "Use the gated online backup (POST /control/backup) or stop the service first.");
                return Task.FromResult(1);
            }

            var manifest = ServiceBackup.Create(
                source, IndexDatabase.LatestSchemaVersion, paths, destinationDir,
                configFingerprint: options.DefaultConfigHash ?? "none");

            Console.WriteLine(
                $"Backup written to {Path.GetFullPath(destinationDir)} (catalog schema v{manifest.SchemaVersion}).");
            Console.WriteLine(
                "  Credentials are NOT included; re-provide these environment variables on restore: " +
                string.Join(", ", manifest.CredentialsBoundary));
            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Backup failed: {ex.Message}");
            return Task.FromResult(1);
        }
    }

    /// <summary>
    /// Offline DR restore (criterion 6): lays a backup's catalog + immutable artifact volume back down into
    /// the operator's configured locations, then instructs the operator to start the service. Starting the
    /// restored service runs migrations, recovers the WAL, reconciles orphaned jobs, and RE-ENFORCES the
    /// read-authorization policy from configuration — so a restored service is a queryable AUTHORIZED
    /// service, never a policy-stripped one. Refuses a backup taken at a newer schema than this build.
    /// </summary>
    public static Task<int> RunRestoreAsync(string backupDir, CancellationToken cancellationToken = default)
    {
        try
        {
            var config = SextantConfiguration.Load();
            var options = ServiceOptions.FromEnvironment(config);
            var paths = new ServicePaths(options.Volumes);

            var manifest = ServiceBackup.Restore(backupDir, options.CatalogDbPath, paths);

            Console.WriteLine(
                $"Restored catalog + artifacts from {Path.GetFullPath(backupDir)} " +
                $"(backup schema v{manifest.SchemaVersion}).");
            Console.WriteLine(
                "Start `sextant service` (or the host) to run migrations, recover, reconcile, and " +
                "re-enforce authorization on the restored catalog.");
            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Restore failed: {ex.Message}");
            return Task.FromResult(1);
        }
    }
}
