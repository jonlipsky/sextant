using Microsoft.Data.Sqlite;

namespace Sextant.Store;

/// <summary>
/// Hands free pages back to the file system in bounded steps (issue #252). Only a database created with
/// <c>auto_vacuum = INCREMENTAL</c> can do this; a database with <c>auto_vacuum = NONE</c> only reuses its free pages
/// and needs the offline <see cref="CatalogCompactor"/> to switch. A step is one short write transaction, so a caller
/// releases the writer between steps.
/// </summary>
public static class IncrementalVacuum
{
    /// <summary>The pages one step returns (32 MiB at the default 4 KiB page size).</summary>
    public const int DefaultStepPages = 8192;

    public const long AutoVacuumNone = 0;
    public const long AutoVacuumFull = 1;
    public const long AutoVacuumIncremental = 2;

    public static long AutoVacuumMode(SqliteConnection connection) => Scalar(connection, "PRAGMA auto_vacuum;");

    public static bool IsIncremental(SqliteConnection connection) =>
        AutoVacuumMode(connection) == AutoVacuumIncremental;

    public static long FreelistPages(SqliteConnection connection) => Scalar(connection, "PRAGMA freelist_count;");

    /// <summary>
    /// Releases up to <see cref="DefaultStepPages"/> free pages (the count is a literal in the constant SQL: a PRAGMA
    /// takes no bound parameter). Returns the pages released: zero on a database that is not incremental (nothing is
    /// attempted) or has no free pages.
    /// </summary>
    public static long Step(SqliteConnection connection, WriteLockTracker? tracker = null)
    {
        if (!IsIncremental(connection)) return 0;
        var before = FreelistPages(connection);
        if (before == 0) return 0;
        tracker ??= WriteLockTracker.Untracked;
        var started = tracker.BeginAcquire();
        try
        {
            using var cmd = connection.CreateCommand();
            // Keep this literal equal to DefaultStepPages.
            cmd.CommandText = "PRAGMA incremental_vacuum(8192);";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        {
            tracker.AcquireFailed("incremental-vacuum", null, started);
            throw;
        }
        catch
        {
            // Not a lock failure: the lock was held while the step ran, so it still counts as a hold.
            tracker.Released(new WriteLockHold("incremental-vacuum", null, started));
            throw;
        }
        // The PRAGMA waits for and holds the write lock in one call, so the two cannot be separated: the whole
        // duration is recorded as a hold and may include the busy wait.
        tracker.Released(new WriteLockHold("incremental-vacuum", null, started));
        return Math.Max(0, before - FreelistPages(connection));
    }

    /// <summary>
    /// Tries to truncate the WAL after a vacuum, without ever waiting: the native busy wait is set to zero around the
    /// checkpoint (and restored), so a reader pinning the WAL makes it return false at once instead of holding the
    /// writer for the connection's <c>busy_timeout</c>. A false result (busy or a partial checkpoint) means "try again
    /// next pass"; the WAL is still bounded by its autocheckpoint and <c>journal_size_limit</c>.
    /// </summary>
    public static bool CheckpointTruncate(SqliteConnection connection, WriteLockTracker? tracker = null)
    {
        tracker ??= WriteLockTracker.Untracked;
        var started = tracker.BeginAcquire();
        var ran = CheckpointTruncateCore(connection, out var completed);
        // A checkpoint that returned busy at once held nothing; recording it would pollute hold metrics.
        if (ran) tracker.Released(new WriteLockHold("wal-checkpoint", null, started));
        return completed;
    }

    private static bool CheckpointTruncateCore(SqliteConnection connection, out bool completed)
    {
        completed = false;
        var previous = Scalar(connection, "PRAGMA busy_timeout;");
        SQLitePCL.raw.sqlite3_busy_timeout(connection.Handle, 0);
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.CommandTimeout = 1;
            using var reader = cmd.ExecuteReader();
            // Columns: busy (1 = could not complete), WAL frames, frames checkpointed.
            var ok = reader.Read() && reader.GetInt64(0) == 0;
            completed = ok;
            return ok;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        {
            return false;
        }
        finally
        {
            SQLitePCL.raw.sqlite3_busy_timeout(connection.Handle, (int)previous);
        }
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
}
