using Microsoft.Data.Sqlite;
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

        if (!execute)
        {
            using var reader = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
            reader.Open();
            using var schema = reader.CreateCommand();
            schema.CommandText = "SELECT MAX(version) FROM schema_version;";
            if (Convert.ToInt32(schema.ExecuteScalar()) != IndexDatabase.LatestSchemaVersion)
            {
                Console.Error.WriteLine("Retention requires a current-schema catalog. Upgrade with the writer stopped before retrying.");
                return 1;
            }
            PrintReport(dbPath, new RetentionService(reader, config.Retention).Plan());
            return 0;
        }

        using var indexDb = new Store.IndexDatabase(dbPath, Store.IndexWriteOptions.FromConfiguration(config));

        // #38: executed retention is a WRITER. Migrate WITHOUT recovery
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
        PrintReport(dbPath, service.Execute());
        return 0;
    }

    private static void PrintReport(string dbPath, RetentionReport report)
    {
        Console.WriteLine(report.DryRun ? "Retention (dry-run):" : "Retention (executed):");
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
        Console.WriteLine($"  Reusable pages:                {(report.ReclaimedBytesKnown ? FormatBytes(report.ReclaimedBytes) : "not estimated (read-only plan)")}");
        Console.WriteLine($"  More remaining:                {report.MoreRemaining}");
        if (report.StopReason != null) Console.WriteLine($"  Stopped:                       {report.StopReason}");

        if (report.DryRun)
        {
            Console.WriteLine();
            Console.WriteLine("  Dry-run only — re-run with --execute to apply.");
        }
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
