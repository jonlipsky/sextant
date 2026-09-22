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
        JournalSizeLimitBytes = config.JournalSizeLimitBytes
    };
}
