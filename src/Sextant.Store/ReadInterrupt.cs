using System.Data;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Sextant.Store;

/// <summary>
/// Lets a caller put a hard time bound on the read connections opened inside a scope: while a scope is active, every
/// read connection opened on the same call flow (<see cref="IndexDatabase.OpenReadConnection"/>, the service catalog
/// reader) is interrupted (<c>sqlite3_interrupt</c>) when the scope's token is cancelled, so a query that is running
/// then fails with SQLite error 9 instead of holding the thread to its end. Nothing changes outside a scope.
/// </summary>
public static class ReadInterrupt
{
    /// <summary>SQLite's <c>SQLITE_INTERRUPT</c> result code, the error an interrupted query fails with.</summary>
    public const int InterruptedErrorCode = 9;

    private static readonly AsyncLocal<CancellationToken> Current = new();

    /// <summary>Interrupts read connections opened until the returned scope is disposed when <paramref name="token"/> is cancelled.</summary>
    public static IDisposable Begin(CancellationToken token)
    {
        var previous = Current.Value;
        Current.Value = token;
        return new Scope(previous);
    }

    /// <summary>Arms <paramref name="connection"/> for the active scope, if any; the arming ends when the connection closes.</summary>
    public static void Attach(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var token = Current.Value;
        if (!token.CanBeCanceled || connection.Handle is not { } handle)
            return;

        var registration = token.Register(() => raw.sqlite3_interrupt(handle));
        // A closed connection goes back to the pool and may serve another reader: never interrupt it then.
        connection.StateChange += (_, e) =>
        {
            if (e.CurrentState == ConnectionState.Closed)
                registration.Dispose();
        };
    }

    private sealed class Scope(CancellationToken previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
