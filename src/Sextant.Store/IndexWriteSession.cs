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
    private readonly Func<bool>? _isWriterLost;
    private bool _open;
    private int _pendingRows;
    private bool _disposed;
    private bool _poisoned;
    private readonly WriteLockTracker _tracker;
    private readonly string _owner;
    private readonly string? _detail;
    private WriteLockHold? _hold;

    public IndexWriteSession(
        SqliteConnection connection,
        IndexWriteOptions? options = null,
        Action? onConnectionPoisoned = null,
        Func<bool>? isWriterLost = null,
        WriteLockTracker? tracker = null,
        string owner = "index-write-session",
        string? detail = null)
    {
        _tracker = tracker ?? WriteLockTracker.Untracked;
        _owner = owner;
        _detail = detail;
        _connection = connection;
        _options = options ?? IndexWriteOptions.Default;
        _onConnectionPoisoned = onConnectionPoisoned;
        _isWriterLost = isWriterLost;
    }

    /// <summary>True while a transaction is currently open.</summary>
    public bool IsOpen => _open;

    /// <summary>Number of batches committed so far (auto-commits, boundary commits, and the final commit).</summary>
    public long CommittedBatches { get; private set; }

    /// <summary>
    /// Opens a transaction if one is not already open. Idempotent. After an earlier commit it first lets any in-process
    /// writer waiting for the lock take it (up to <see cref="IndexWriteOptions.YieldToWaitersCap"/>); SQLite's busy
    /// handler only polls, so without this a waiter almost never wins against an immediate re-BEGIN (issue #344).
    /// </summary>
    public void Begin()
    {
        if (_poisoned)
            throw new InvalidOperationException(
                "The write session's connection is in an unknown transaction state after a failed rollback " +
                "and must be reset before further writes.");
        if (_open) return;
        if (CommittedBatches > 0)
            _tracker.YieldToWaiters(_options.YieldToWaitersCap);
        var waitStarted = _tracker.BeginAcquire();
        try
        {
            ExecuteWithRetry("BEGIN IMMEDIATE;");
        }
        catch
        {
            _tracker.AcquireFailed(_owner, _detail, waitStarted);
            throw;
        }
        _hold = _tracker.Acquired(_owner, _detail, waitStarted);
        _open = true;
        _pendingRows = 0;
    }

    /// <summary>
    /// Records that <paramref name="count"/> rows were written and forces a commit if the safety-net
    /// threshold is reached, so an abnormally large unit cannot build an unbounded transaction. Rows are only ever
    /// written inside the session's transaction (autocommit would be one WAL frame set per row), so calling this with
    /// no open transaction is a bug in the caller: it must <see cref="Begin"/> before writing.
    /// </summary>
    public void RowsWritten(int count = 1)
    {
        if (count <= 0) return;
        if (!_open)
            throw new InvalidOperationException(
                "Rows were written with no open write transaction (autocommit). Call Begin() before writing; " +
                "Release() ends the batch and the next write must Begin() again.");
        _pendingRows += count;
        if (_options.BatchRowThreshold > 0 && _pendingRows >= _options.BatchRowThreshold)
            CommitBatch();
        else if (HeldTooLong())
            CommitBatch();
    }

    // The open batch has held the write lock past MaxBatchHold (issue #344).
    private bool HeldTooLong() =>
        _hold is { } hold && _options.MaxBatchHold > TimeSpan.Zero && _tracker.ElapsedSince(hold.StartedAt) >= _options.MaxBatchHold;

    /// <summary>
    /// Commits the current batch and immediately begins a new one (after the yield in <see cref="Begin"/>). Only for
    /// write-only stretches; before a compile, an extraction or a wait use <see cref="Release"/>, which leaves no
    /// transaction (and so no write lock) open.
    /// </summary>
    public void CommitBatch()
    {
        if (!_open) return;
        AbortIfWriterLost();
        ExecuteWithRetry("COMMIT;");
        EndHold();
        _open = false;
        _pendingRows = 0;
        CommittedBatches++;
        Begin();
    }

    /// <summary>
    /// Commits the open batch, if any, WITHOUT beginning another, so no write lock is held while the caller compiles,
    /// extracts or waits (issue #344). The next write must <see cref="Begin"/> again. Idempotent.
    /// </summary>
    public void Release()
    {
        if (!_open) return;
        AbortIfWriterLost();
        ExecuteWithRetry("COMMIT;");
        EndHold();
        _open = false;
        _pendingRows = 0;
        CommittedBatches++;
    }

    /// <summary>Commits the final batch (the publish). Does not begin a new transaction.</summary>
    public void Complete() => Release();

    private void EndHold()
    {
        if (_hold is not { } hold) return;
        _hold = null;
        _tracker.Released(hold);
    }

    // Fail closed BETWEEN batches when this process lost the single-writer lease (issue #38): roll the
    // open, uncommitted batch back and throw so the caller aborts. Already-committed staging batches are
    // left for recovery; crucially the generation/snapshot pointer flip (which the orchestrator runs
    // inside the final batch, right before Complete) never commits, so no corrupt publish can occur
    // (Phase 17 criterion 3). Checked before COMMIT so a lost lease can never publish a batch.
    private void AbortIfWriterLost()
    {
        if (_isWriterLost?.Invoke() != true) return;
        Rollback();
        throw new WriterLeaseLostException(
            "Aborting the in-flight index write: this process lost the single-writer lease (it expired and " +
            "was stolen by another writer). The open batch was rolled back and no generation was published " +
            "(issue #38 / Phase 17 criterion 3).");
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
        EndHold();
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
