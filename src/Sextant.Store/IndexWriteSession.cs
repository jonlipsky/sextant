using Microsoft.Data.Sqlite;

namespace Sextant.Store;

/// <summary>
/// An explicit, bounded unit-of-work over a single writer connection. It replaces the previous
/// pattern of one implicit transaction per row — which let the WAL grow to many times the final
/// database size — with a small number of explicit, batch-bounded transactions.
///
/// The transaction is driven with raw <c>BEGIN IMMEDIATE</c>/<c>COMMIT</c>/<c>ROLLBACK</c> rather
/// than <see cref="SqliteConnection.BeginTransaction"/>. That keeps the ADO-tracked
/// <c>SqliteConnection.Transaction</c> null, so every existing store command (whose
/// <c>Transaction</c> is null) continues to run unchanged inside the ambient transaction without
/// having to thread a transaction object through dozens of methods. Prepared, reused commands work
/// the same way.
/// </summary>
public sealed class IndexWriteSession : IDisposable
{
    private const int SqliteError = 1;
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;

    private readonly SqliteConnection _connection;
    private readonly IndexWriteOptions _options;
    private readonly Action? _onConnectionPoisoned;
    private bool _open;
    private int _pendingRows;
    private bool _disposed;
    private bool _poisoned;

    public IndexWriteSession(SqliteConnection connection, IndexWriteOptions? options = null, Action? onConnectionPoisoned = null)
    {
        _connection = connection;
        _options = options ?? IndexWriteOptions.Default;
        _onConnectionPoisoned = onConnectionPoisoned;
    }

    /// <summary>True while a transaction is currently open.</summary>
    public bool IsOpen => _open;

    /// <summary>Number of batches committed so far (auto-commits, boundary commits, and the final commit).</summary>
    public long CommittedBatches { get; private set; }

    /// <summary>Opens a transaction if one is not already open. Idempotent.</summary>
    public void Begin()
    {
        if (_poisoned)
            throw new InvalidOperationException(
                "The write session's connection is in an unknown transaction state after a failed rollback " +
                "and must be reset before further writes.");
        if (_open) return;
        ExecuteWithRetry("BEGIN IMMEDIATE;");
        _open = true;
        _pendingRows = 0;
    }

    /// <summary>
    /// Records that <paramref name="count"/> rows were written and forces a commit if the safety-net
    /// threshold is reached, so an abnormally large unit cannot build an unbounded transaction.
    /// </summary>
    public void RowsWritten(int count = 1)
    {
        if (!_open || count <= 0) return;
        _pendingRows += count;
        if (_options.BatchRowThreshold > 0 && _pendingRows >= _options.BatchRowThreshold)
            CommitBatch();
    }

    /// <summary>Commits the current batch at a document/project boundary and immediately begins a new one.</summary>
    public void CommitBatch()
    {
        if (!_open) return;
        ExecuteWithRetry("COMMIT;");
        _open = false;
        _pendingRows = 0;
        CommittedBatches++;
        Begin();
    }

    /// <summary>Commits the final batch. Does not begin a new transaction.</summary>
    public void Complete()
    {
        if (!_open) return;
        ExecuteWithRetry("COMMIT;");
        _open = false;
        _pendingRows = 0;
        CommittedBatches++;
    }

    /// <summary>Rolls back any open, uncommitted batch. Already-committed batches are unaffected.</summary>
    public void Rollback()
    {
        if (!_open) return;
        try
        {
            // Route through the same bounded retry as BEGIN/COMMIT so a transient busy/locked ROLLBACK
            // does not leave the ambient transaction open on this (potentially long-lived, reused)
            // connection. A ROLLBACK with no active transaction is the only benign failure.
            ExecuteWithRetry("ROLLBACK;");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteError)
        {
            // "cannot rollback - no transaction is active": nothing to undo.
        }
        catch (SqliteException)
        {
            // The transaction may still be open on the shared connection; refuse further reuse of this
            // session AND ask the owner to discard the underlying connection so the unknown transaction
            // state cannot leak into the next write session or ledger update. Startup recovery is the
            // final backstop.
            _poisoned = true;
            _onConnectionPoisoned?.Invoke();
        }
        _open = false;
        _pendingRows = 0;
    }

    /// <summary>True if a rollback failed non-benignly and the connection's transaction state is unknown.</summary>
    public bool IsPoisoned => _poisoned;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Rollback();
    }

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void ExecuteWithRetry(string sql)
    {
        var attempts = 0;
        while (true)
        {
            try
            {
                Exec(sql);
                return;
            }
            catch (SqliteException ex) when (IsTransient(ex) && attempts < _options.MaxRetries)
            {
                attempts++;
                var delay = Math.Min(_options.RetryBaseDelayMs * (1 << (attempts - 1)), _options.RetryMaxDelayMs);
                Thread.Sleep(delay);
            }
        }
    }

    private static bool IsTransient(SqliteException ex) =>
        ex.SqliteErrorCode is SqliteBusy or SqliteLocked;
}
