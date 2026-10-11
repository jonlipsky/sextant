using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Sextant.Store;

/// <summary>Bounds SQLite VM work as well as managed retention loops; a command timeout alone only bounds lock waits.</summary>
internal sealed class RetentionWorkBudget : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly CancellationToken _cancellationToken;
    private readonly long _started = Stopwatch.GetTimestamp();
    private TimeSpan _duration;
    private TimeSpan? _unitEnd;
    private readonly int _previousTimeout;
    private readonly int _previousBusyTimeout;

    internal RetentionWorkBudget(SqliteConnection connection, TimeSpan duration, CancellationToken cancellationToken)
    {
        _connection = connection;
        _duration = duration;
        _cancellationToken = cancellationToken;
        _previousTimeout = connection.DefaultTimeout;
        using var timeout = connection.CreateCommand();
        timeout.CommandText = "PRAGMA busy_timeout;";
        _previousBusyTimeout = Convert.ToInt32(timeout.ExecuteScalar());
        connection.DefaultTimeout = 1;
        // Microsoft.Data.Sqlite retries SQLITE_BUSY up to CommandTimeout, but each native attempt
        // first waits busy_timeout. Do not inherit the writer's five-second native wait.
        raw.sqlite3_busy_timeout(connection.Handle, 100);
        raw.sqlite3_progress_handler(connection.Handle, 1000, _ => ShouldStop ? 1 : 0, null);
    }

    internal bool ShouldStop => _cancellationToken.IsCancellationRequested ||
        Stopwatch.GetElapsedTime(_started) >= (_unitEnd ?? _duration);

    /// <summary>True while a unit runs and its own cap has passed (cancellation is not an expired unit).</summary>
    internal bool UnitExpired => _unitEnd is { } end && !_cancellationToken.IsCancellationRequested &&
        Stopwatch.GetElapsedTime(_started) >= end;

    /// <summary>The unit runs under its own cap instead of the batch limit: one unit always gets to finish.</summary>
    internal void BeginUnit(TimeSpan cap) => _unitEnd = Stopwatch.GetElapsedTime(_started) + cap;

    /// <summary>Ends the unit; the batch keeps a short grace after it so the commit is not cut by the batch limit.</summary>
    internal void EndUnit()
    {
        _unitEnd = null;
        var grace = Stopwatch.GetElapsedTime(_started) + TimeSpan.FromSeconds(1);
        if (grace > _duration) _duration = grace;
    }

    internal void Check()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (ShouldStop) throw new TimeoutException("Retention work budget exhausted.");
    }

    public void Dispose()
    {
        raw.sqlite3_progress_handler(_connection.Handle, 0, null, null);
        raw.sqlite3_busy_timeout(_connection.Handle, _previousBusyTimeout);
        _connection.DefaultTimeout = _previousTimeout;
    }
}
