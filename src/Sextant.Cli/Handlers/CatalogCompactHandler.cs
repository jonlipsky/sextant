using Sextant.Store;

namespace Sextant.Cli.Handlers;

internal static class CatalogCompactHandler
{
    public static int Run(string input, string output, bool fullIntegrityCheck, bool countRows)
    {
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true; // let the compaction clean up its staging files instead of dying mid-write
            cancel.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        CatalogCompactResult result;
        try
        {
            result = CatalogCompactor.Compact(input, output, fullIntegrityCheck, countRows, Console.WriteLine, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Compaction cancelled. The partial copy was removed; the input is untouched.");
            return 130;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException
                                       or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Compaction failed: {ex.Message}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }

        Console.WriteLine($"  Input:   {Path.GetFullPath(input)} ({result.InputBytes:N0} bytes, auto_vacuum={result.InputAutoVacuum})");
        Console.WriteLine($"  Output:  {Path.GetFullPath(output)} ({result.OutputBytes:N0} bytes, auto_vacuum={result.OutputAutoVacuum})");
        Console.WriteLine($"  Saved:   {result.InputBytes - result.OutputBytes:N0} bytes; elapsed {result.Elapsed.TotalSeconds:F0}s");
        Console.WriteLine($"  {result.CheckName}: {result.CheckResult}");
        if (result.RowCounts is not null)
            Console.WriteLine($"  Row counts: {(result.RowCountsMatch ? "identical in all " + result.RowCounts.Count + " tables" : "MISMATCH")}");

        if (!result.CheckPassed || !result.RowCountsMatch)
        {
            Console.Error.WriteLine("The copy failed verification and was removed. The input is untouched.");
            return 1;
        }
        Console.WriteLine("  The copy is verified. Swap it in only with the service stopped; keep the old file until the service has run on it.");
        return 0;
    }
}
