namespace Sextant.Store;

/// <summary>
/// Tunables for the bounded, batched write path. Batch size is a measured trade-off: larger
/// transactions cut per-commit overhead but grow the WAL and raise recovery/temp-space cost, so the
/// default is deliberately conservative. WAL controls keep the write-ahead log bounded during a run
/// rather than letting it grow to many times the size of the final database before checkpoint.
/// </summary>
public sealed record IndexWriteOptions
{
    /// <summary>
    /// Safety-net row count that forces a commit mid-unit so one abnormally large project cannot
    /// build an unbounded transaction (and thus an unbounded WAL). Normal projects commit at their
    /// explicit document/project boundary well before this fires. 0 disables the safety net.
    /// </summary>
    public int BatchRowThreshold { get; init; } = 10_000;

    /// <summary>
    /// <c>PRAGMA wal_autocheckpoint</c> value in pages. SQLite checkpoints committed WAL frames into
    /// the main database when the log reaches this many pages at a commit boundary, so peak WAL stays
    /// near this bound plus at most one in-flight batch.
    /// </summary>
    public int WalAutocheckpointPages { get; init; } = 1_000;

    /// <summary>
    /// <c>PRAGMA journal_size_limit</c> in bytes. Caps the WAL file the writer leaves on disk after a
    /// checkpoint resets it, so a transient spike does not permanently inflate the footprint.
    /// </summary>
    public long JournalSizeLimitBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>
    /// <c>PRAGMA cache_size</c> of the writer connection, in MiB. SQLite's default (about 2 MiB) is tiny beside a
    /// multi-gigabyte catalog: every upsert walks the unique-key and secondary index B-trees, and their interior
    /// pages fall out of a small cache. 0 or less keeps SQLite's default.
    /// </summary>
    public int CacheMib { get; init; } = 256;

    /// <summary>
    /// <c>PRAGMA mmap_size</c> of the writer connection, in MiB: reads of the first this-many bytes of the catalog
    /// come straight from the operating system's file cache instead of a copy per page. 0 turns it off.
    /// </summary>
    public int MmapMib { get; init; } = 256;

    /// <summary>
    /// Longest an open batch may hold the catalog write lock (issue #344): once it has, the next
    /// <see cref="IndexWriteSession.RowsWritten"/> commits it and begins a new one, like the row threshold. The
    /// commit lands at a row boundary, so one row can run past the cap. It is a backstop for long write-only
    /// stretches: the orchestrator releases the batch before every compile, extraction and wait. Zero or negative
    /// (the default, so the local CLI/daemon are unchanged) disables the cap; the index service turns it on.
    /// </summary>
    public TimeSpan MaxBatchHold { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// After a commit, when other in-process writers are waiting for the lock (grant writes, the lease renewal, raw
    /// service transactions), the session holds off its next <c>BEGIN IMMEDIATE</c> until none is left waiting, at
    /// most this long (issue #344). Zero or negative (the default, so the local CLI/daemon are unchanged) disables
    /// yielding; the index service turns it on.
    /// </summary>
    public TimeSpan YieldToWaitersCap { get; init; } = TimeSpan.Zero;

    /// <summary>Maximum retries for a transient busy/locked failure on BEGIN/COMMIT.</summary>
    public int MaxRetries { get; init; } = 5;

    /// <summary>Base backoff (doubled each attempt) for a transient busy/locked retry.</summary>
    public int RetryBaseDelayMs { get; init; } = 20;

    /// <summary>Upper bound on a single backoff sleep.</summary>
    public int RetryMaxDelayMs { get; init; } = 500;

    public static IndexWriteOptions Default { get; } = new();

    /// <summary>Builds write options from the repo's <see cref="Sextant.Core.SextantConfiguration"/>.</summary>
    public static IndexWriteOptions FromConfiguration(Sextant.Core.SextantConfiguration config) => new()
    {
        BatchRowThreshold = config.WriteBatchSize,
        WalAutocheckpointPages = config.WalAutocheckpointPages,
        JournalSizeLimitBytes = config.JournalSizeLimitBytes,
        CacheMib = config.WriterCacheMib,
        MmapMib = config.WriterMmapMib
    };
}
