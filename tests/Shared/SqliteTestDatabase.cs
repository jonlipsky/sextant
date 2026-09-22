using Microsoft.Data.Sqlite;

namespace Sextant.TestSupport;

/// <summary>
/// Shared cleanup helper for tests that create a temporary on-disk SQLite database. On Windows,
/// <c>File.Delete</c> throws <see cref="IOException"/> ("used by another process") when a pooled
/// SQLite connection still holds the file handle, even after the owning <c>IndexDatabase</c> has been
/// disposed — Microsoft.Data.Sqlite keeps the handle in its connection pool. Disposing the owner and
/// then calling <see cref="SqliteConnection.ClearAllPools"/> before deleting releases the handle; a
/// short retry absorbs the brief window where a background checkpoint is still finishing.
/// </summary>
public static class SqliteTestDatabase
{
    /// <summary>
    /// Disposes <paramref name="owner"/> (typically the test's IndexDatabase), clears all pooled
    /// SQLite connections, and deletes the database file plus its <c>-wal</c>/<c>-shm</c> sidecars.
    /// Safe to call with a null path/owner. Never throws for a lingering lock — it retries briefly and
    /// gives up quietly so a cleanup lock cannot fail an otherwise-passing test.
    /// </summary>
    public static void Delete(string? dbPath, IDisposable? owner = null)
    {
        owner?.Dispose();
        SqliteConnection.ClearAllPools();

        if (string.IsNullOrEmpty(dbPath))
            return;

        foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
            TryDelete(path);
    }

    private static void TryDelete(string path)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ReleaseHandlesAndWait(attempt);
            }
        }
    }

    /// <summary>
    /// Recursively deletes a temp directory that may contain on-disk SQLite databases. Disposes
    /// <paramref name="owner"/>, clears pooled SQLite connections, then retries the recursive delete
    /// to absorb the brief window where a handle is still being released. Never throws for a lingering
    /// lock — it gives up quietly so a cleanup lock cannot fail an otherwise-passing test.
    /// </summary>
    public static void DeleteDirectory(string? dir, IDisposable? owner = null)
    {
        owner?.Dispose();
        SqliteConnection.ClearAllPools();

        if (string.IsNullOrEmpty(dir))
            return;

        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ReleaseHandlesAndWait(attempt);
            }
        }
    }

    /// <summary>
    /// On a retry, forces pending SQLite connection finalizers to run and re-clears the pool before
    /// backing off. A connection that was never explicitly disposed only releases its file handle when
    /// finalized, so under heavy concurrent load a plain <see cref="SqliteConnection.ClearAllPools"/>
    /// (which only touches idle pooled connections) is not always enough on the first pass.
    /// </summary>
    private static void ReleaseHandlesAndWait(int attempt)
    {
        if (attempt == 0)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            SqliteConnection.ClearAllPools();
        }
        Thread.Sleep(100);
    }
}
