using Sextant.Core;
using Sextant.Store;

namespace Sextant.Cli.Handlers;

internal static class RetentionHandler
{
    public static int Run(string? db, string? profile, bool execute)
    {
        var config = Core.SextantConfiguration.Load();
        var dbPath = DbResolver.Resolve(db, profile, config);
        if (dbPath == null) return 1;

        if (!File.Exists(dbPath))
        {
            Console.Error.WriteLine($"No index database at {Path.GetFullPath(dbPath)}. Nothing to retain.");
            return 1;
        }

        using var indexDb = new Store.IndexDatabase(dbPath, Store.IndexWriteOptions.FromConfiguration(config));

        // #38: retention is a WRITER. Take the single-writer lease before touching the database so a
        // dry-run or --execute can never race a live daemon/service writer. Migrate WITHOUT recovery
        // first (the writer_lease table must exist to acquire), then acquire, then recover — recovery
        // must not run while another writer owns a staging generation.
        indexDb.RunMigrations(recover: false);

        using var lease = Store.WriterLease.TryAcquire(dbPath, $"retention:pid={Environment.ProcessId}");
        if (lease == null)
        {
            var holder = Store.WriterLease.GetCurrent(indexDb.GetConnection())?.Holder ?? "another writer";
            Console.Error.WriteLine(
                $"Refusing to run retention: {holder} currently holds the writer lease. " +
                "Stop the daemon/service (or wait for its lease to expire) and retry.");
            return 1;
        }

        indexDb.Recover();

        var conn = indexDb.GetConnection();
        var service = new RetentionService(conn, config.Retention);
        var report = execute ? service.Execute() : service.Plan();

        Console.WriteLine(execute ? "Retention (executed):" : "Retention (dry-run):");
        Console.WriteLine($"  Database: {Path.GetFullPath(dbPath)}");
        Console.WriteLine();

        Console.WriteLine($"  Protected generations ({report.Protected.Count}):");
        foreach (var g in report.Protected)
            Console.WriteLine($"    #{g.Id} [{g.Status}]{ProfileSuffix(g)} — {g.Reason}");

        Console.WriteLine($"  Retained generations ({report.Retained.Count}):");
        foreach (var g in report.Retained)
            Console.WriteLine($"    #{g.Id} [{g.Status}]{ProfileSuffix(g)} — {g.Reason}");

        Console.WriteLine($"  Deleted generations ({report.Deleted.Count}):");
        foreach (var g in report.Deleted)
            Console.WriteLine($"    #{g.Id} [{g.Status}]{ProfileSuffix(g)} — {g.Reason}");

        Console.WriteLine();
        Console.WriteLine($"  Snapshots GC'd:                {report.SnapshotsDeleted}");
        Console.WriteLine($"  Snapshot project-versions GC'd:{report.SnapshotProjectVersionsDeleted}");
        Console.WriteLine($"  API-surface snapshots deleted: {report.ApiSnapshotsDeleted}");
        Console.WriteLine($"  Source blobs deleted:          {report.FileVersionsDeleted}");
        Console.WriteLine($"  Reclaimed:                     {FormatBytes(report.ReclaimedBytes)}");

        if (!execute)
        {
            Console.WriteLine();
            Console.WriteLine("  Dry-run only — re-run with --execute to apply.");
        }

        return 0;
    }

    private static string ProfileSuffix(RetentionGeneration g)
        => g.Profile == null ? "" : $" ({g.Profile})";

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:F1} {units[unit]}";
    }
}
