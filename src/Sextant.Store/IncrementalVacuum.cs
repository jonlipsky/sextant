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
    /// <summary>The most pages one step returns (32 MiB at the default 4 KiB page size); also the fixed size of <see cref="Step"/> without a count.</summary>
    public const int DefaultStepPages = 8192;

    /// <summary>The fewest pages an adaptive step shrinks to.</summary>
    public const int MinStepPages = 16;

    /// <summary>The cap an adaptive step grows to.</summary>
    public const int MaxStepPages = DefaultStepPages;

    /// <summary>Where the adaptive steps of a pass start (issue #344): small, because a step's cost depends on the catalog.</summary>
    public const int InitialStepPages = 256;

    /// <summary>The write-lock hold one adaptive step aims for.</summary>
    public static readonly TimeSpan DefaultTargetHold = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The page count of the next step, from the last step's count and how long it held the lock (issue #344). Pure.
    /// A step well under the target doubles, one well over it shrinks in proportion to how far it overshot (at least
    /// halving), anything near the target keeps its size; always within <see cref="MinStepPages"/>..<see cref="MaxStepPages"/>.
    /// </summary>
    public static int NextStepPages(int currentPages, TimeSpan measured, TimeSpan target)
    {
        var current = Math.Clamp(currentPages, MinStepPages, MaxStepPages);
        if (target <= TimeSpan.Zero) return current;
        double next;
        if (measured <= TimeSpan.Zero || measured.TotalMilliseconds < target.TotalMilliseconds / 2)
            next = current * 2.0;
        else if (measured.TotalMilliseconds > target.TotalMilliseconds * 1.5)
            next = current * Math.Min(0.5, target.TotalMilliseconds / measured.TotalMilliseconds);
        else
            next = current;
        return (int)Math.Clamp(Math.Floor(next), MinStepPages, MaxStepPages);
    }

    /// <summary>What one <see cref="RunStep"/> did.</summary>
    public readonly record struct StepResult(long Freed, int Pages, TimeSpan Elapsed);

    public const long AutoVacuumNone = 0;
    public const long AutoVacuumFull = 1;
    public const long AutoVacuumIncremental = 2;

    public static long AutoVacuumMode(SqliteConnection connection) => Scalar(connection, "PRAGMA auto_vacuum;");

    public static bool IsIncremental(SqliteConnection connection) =>
        AutoVacuumMode(connection) == AutoVacuumIncremental;

    public static long FreelistPages(SqliteConnection connection) => Scalar(connection, "PRAGMA freelist_count;");

    /// <summary>Releases up to <see cref="DefaultStepPages"/> free pages in one step; see <see cref="RunStep"/>.</summary>
    public static long Step(SqliteConnection connection, WriteLockTracker? tracker = null) =>
        RunStep(connection, DefaultStepPages, tracker).Freed;

    /// <summary>
    /// Releases up to <paramref name="pages"/> free pages (clamped to <see cref="MinStepPages"/>..<see cref="MaxStepPages"/>;
    /// the count is formatted into the SQL from that validated integer, since a PRAGMA takes no bound parameter).
    /// <c>Freed</c> is zero on a database that is not incremental (nothing is attempted) or has no free pages;
    /// <c>Elapsed</c> is the time the step held the write lock (it may include the busy wait).
    /// </summary>
    public static StepResult RunStep(SqliteConnection connection, int pages, WriteLockTracker? tracker = null)
    {
        pages = Math.Clamp(pages, MinStepPages, MaxStepPages);
        if (!IsIncremental(connection)) return new StepResult(0, pages, TimeSpan.Zero);
        var before = FreelistPages(connection);
        if (before == 0) return new StepResult(0, pages, TimeSpan.Zero);
        tracker ??= WriteLockTracker.Untracked;
        // Not BeginAcquire: the PRAGMA waits and holds in one call, so this is no waiting writer for others to yield to.
        var started = tracker.Timestamp();
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"PRAGMA incremental_vacuum({pages});");
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        {
            tracker.AcquireFailedAt("incremental-vacuum", null, started);
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
        var elapsed = tracker.ElapsedSince(started);
        tracker.Released(new WriteLockHold("incremental-vacuum", null, started));
        return new StepResult(Math.Max(0, before - FreelistPages(connection)), pages, elapsed);
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
        var started = tracker.Timestamp();
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
