using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Sextant.Store;

/// <summary>
/// One compiled write statement bound by POSITION and run once per row on the writer connection (issue #271). The
/// indexer's hot inserts run hundreds of thousands of times per index; through <see cref="SqliteCommand"/> every
/// execution re-resolves each parameter by name (<c>sqlite3_bind_parameter_index</c> on a freshly marshalled UTF-8
/// name) and wraps the step in a data reader, which measured as a fifth of the symbol-persist time on ProcessStack.
/// Here the statement is prepared once, values are bound to <c>?1..?N</c> by index, and the step is driven directly.
/// </summary>
/// <remarks>
/// The statement runs on the connection's native handle, so it joins whatever transaction is open on that connection
/// (the write session drives raw <c>BEGIN</c>/<c>COMMIT</c>), exactly like a store command whose <c>Transaction</c> is
/// null. Not thread-safe; owned by the single writer for the duration of one run. Dispose it before the connection.
/// <para>
/// Unlike <see cref="SqliteCommand"/>, a step that returns <c>SQLITE_BUSY</c>/<c>SQLITE_LOCKED</c> is not retried here:
/// it throws at once. File-level contention is still absorbed by the connection's <c>busy_timeout</c>, and nothing else
/// shares the writer's cache (readers use <see cref="IndexDatabase.OpenReadConnection"/>, a private cache), so a
/// shared-cache table lock cannot arise. A new shared-cache reader of the catalog would have to revisit this.
/// </para>
/// </remarks>
public sealed class PreparedInsert : IDisposable
{
    private readonly sqlite3 _db;
    private readonly sqlite3_stmt _stmt;

    public PreparedInsert(SqliteConnection connection, string sql)
    {
        _db = connection.Handle ?? throw new InvalidOperationException("The connection is not open.");
        var rc = raw.sqlite3_prepare_v2(_db, sql, out _stmt);
        if (rc != raw.SQLITE_OK)
            SqliteException.ThrowExceptionForRC(rc, _db);
        Parameters = raw.sqlite3_bind_parameter_count(_stmt);
    }

    /// <summary>Number of <c>?N</c> parameters the statement declares.</summary>
    public int Parameters { get; }

    public PreparedInsert Bind(int index, long value)
    {
        Check(raw.sqlite3_bind_int64(_stmt, index, value));
        return this;
    }

    public PreparedInsert Bind(int index, long? value)
        => value is { } v ? Bind(index, v) : BindNull(index);

    public PreparedInsert Bind(int index, bool value) => Bind(index, value ? 1L : 0L);

    public PreparedInsert Bind(int index, string? value)
    {
        Check(value is null ? raw.sqlite3_bind_null(_stmt, index) : raw.sqlite3_bind_text(_stmt, index, value));
        return this;
    }

    public PreparedInsert Bind(int index, byte[]? value)
    {
        Check(value is null ? raw.sqlite3_bind_null(_stmt, index) : raw.sqlite3_bind_blob(_stmt, index, value));
        return this;
    }

    public PreparedInsert BindNull(int index)
    {
        Check(raw.sqlite3_bind_null(_stmt, index));
        return this;
    }

    /// <summary>Runs the statement and returns the first column of its single <c>RETURNING</c> row.</summary>
    public long ExecuteReturningId()
    {
        try
        {
            var rc = raw.sqlite3_step(_stmt);
            if (rc != raw.SQLITE_ROW)
            {
                if (rc != raw.SQLITE_DONE)
                    SqliteException.ThrowExceptionForRC(rc, _db);
                throw new InvalidOperationException("The insert returned no row.");
            }
            var id = raw.sqlite3_column_int64(_stmt, 0);
            // Step to completion: a RETURNING statement applies its write as it runs, and finishes on DONE.
            while ((rc = raw.sqlite3_step(_stmt)) == raw.SQLITE_ROW) { }
            if (rc != raw.SQLITE_DONE)
                SqliteException.ThrowExceptionForRC(rc, _db);
            return id;
        }
        finally
        {
            Reset();
        }
    }

    /// <summary>Runs the statement to completion (no result rows).</summary>
    public void Execute()
    {
        try
        {
            int rc;
            while ((rc = raw.sqlite3_step(_stmt)) == raw.SQLITE_ROW) { }
            if (rc != raw.SQLITE_DONE)
                SqliteException.ThrowExceptionForRC(rc, _db);
        }
        finally
        {
            Reset();
        }
    }

    // Leaves the statement ready for the next row with no value carried over from this one.
    private void Reset()
    {
        raw.sqlite3_reset(_stmt);
        raw.sqlite3_clear_bindings(_stmt);
    }

    private void Check(int rc)
    {
        if (rc != raw.SQLITE_OK)
            SqliteException.ThrowExceptionForRC(rc, _db);
    }

    public void Dispose() => _stmt.Dispose();
}
