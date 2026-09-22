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
        indexDb.RunMigrations();

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
