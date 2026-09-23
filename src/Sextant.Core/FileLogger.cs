namespace Sextant.Core;

/// <summary>
/// Simple file logger with timestamped lines and size-based rotation.
/// Thread-safe via lock. Implements IDisposable for flush/close.
///
/// <para>
/// A logger must never crash the operation it logs. The log is opened with
/// <see cref="FileShare.ReadWrite"/> so a concurrent writer (a second index run, or a daemon
/// running alongside a one-shot index) never fails to open the same file, and open/write
/// failures degrade to console-only logging (<see cref="IsFileBacked"/>) instead of throwing.
/// </para>
/// </summary>
public sealed class FileLogger : IDisposable
{
    private readonly string _filePath;
    private readonly long _maxSizeBytes;
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private long _currentSize;
    private bool _disposed;
    private long _nextReopenTicks;

    // How long to wait before retrying a failed open. Bounds reopen attempts (and the console
    // warning that accompanies a real failure) to roughly one every few seconds for a long-lived
    // daemon whose log path is temporarily unavailable, rather than hammering the filesystem per line.
    private const int ReopenBackoffMs = 5000;

    private FileLogger(string filePath, long maxSizeBytes)
    {
        _filePath = filePath;
        _maxSizeBytes = maxSizeBytes;
        _writer = TryOpenWriter(filePath);
        if (_writer != null)
        {
            _currentSize = SafeLength(filePath);
        }
        else
        {
            SafeConsoleError($"[sextant] Log file '{filePath}' unavailable; logging to console only until it recovers.");
            _nextReopenTicks = Environment.TickCount64 + ReopenBackoffMs;
        }
    }

    /// <summary>
    /// True while writing to the log file; false when the logger has degraded to console-only
    /// output because the file could not be opened or written.
    /// </summary>
    public bool IsFileBacked
    {
        get { lock (_lock) { return _writer != null; } }
    }

    /// <summary>
    /// Opens a file logger. Creates the directory if needed. Fail-soft: if the directory can't be
    /// created (or the file can't be opened), the logger degrades to console-only rather than throwing.
    /// </summary>
    public static FileLogger Open(string logDir, string fileName, long maxSizeBytes = 10 * 1024 * 1024)
    {
        try
        {
            Directory.CreateDirectory(logDir);
        }
        catch (Exception ex) when (IsFileOpFailure(ex))
        {
            // Directory unavailable/unauthorized: the logger constructed below will find the file
            // unopenable and degrade to console-only, so the operation being logged still proceeds.
        }

        var filePath = Path.Combine(logDir, fileName);
        return new FileLogger(filePath, maxSizeBytes);
    }

    /// <summary>
    /// Writes a timestamped line to the log file. Thread-safe.
    /// </summary>
    public void Write(string message)
    {
        var line = $"[{DateTime.UtcNow:O}] {message}";
        lock (_lock)
        {
            // Never resurrect a disposed logger (a retained callback could still call Write); doing so
            // would reopen and leak a file handle that the completed Dispose will not close again.
            if (_disposed)
                return;

            if (_writer == null && !TryReopen())
            {
                // Degraded to console-only (file unopenable) and not yet time to retry.
                SafeConsoleError(line);
                return;
            }

            try
            {
                RotateIfNeeded();
                if (_writer == null)
                {
                    SafeConsoleError(line);
                    return;
                }

                _writer.WriteLine(line);
                _writer.Flush();
                _currentSize += System.Text.Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
            }
            catch (Exception ex) when (IsFileOpFailure(ex))
            {
                // A logger must never abort the operation it logs. If writing fails (handle
                // invalidated, disk full, ACL change), degrade to console and schedule a reopen so a
                // long-lived daemon/serve recovers file logging once the transient condition clears.
                DisposeWriterQuietly();
                _nextReopenTicks = Environment.TickCount64 + ReopenBackoffMs;
                SafeConsoleError($"[sextant] Log file '{_filePath}' write failed ({ex.Message}); logging to console until it recovers.");
                SafeConsoleError(line);
            }
        }
    }

    /// <summary>
    /// Attempts to (re)open the log after a prior failure, throttled by <see cref="ReopenBackoffMs"/>
    /// so a permanently-unavailable path doesn't hammer the filesystem. Must be called under the lock.
    /// Returns true when the writer is available afterwards.
    /// </summary>
    private bool TryReopen()
    {
        if (_writer != null)
            return true;
        if (Environment.TickCount64 < _nextReopenTicks)
            return false;

        _nextReopenTicks = Environment.TickCount64 + ReopenBackoffMs;
        _writer = TryOpenWriter(_filePath);
        if (_writer == null)
            return false;

        _currentSize = SafeLength(_filePath);
        return true;
    }

    /// <summary>
    /// Returns an Action&lt;string&gt; that writes to this logger AND calls the optional passthrough.
    /// Drop-in replacement for existing <c>msg => Console.WriteLine(msg)</c> callbacks.
    /// </summary>
    public Action<string> CreateCallback(Action<string>? also = null)
    {
        return msg =>
        {
            Write(msg);
            also?.Invoke(msg);
        };
    }

    private void RotateIfNeeded()
    {
        if (_writer == null || _currentSize < _maxSizeBytes)
            return;

        DisposeWriterQuietly();

        var rotated = false;
        try
        {
            var rotatedPath = _filePath + ".1";
            if (File.Exists(rotatedPath))
                File.Delete(rotatedPath);

            File.Move(_filePath, rotatedPath);
            rotated = true;
        }
        catch (Exception ex) when (IsFileOpFailure(ex))
        {
            // Another handle (a concurrent process) may hold the file, the move raced, or ACLs
            // forbid it. Skip rotation this pass, reopen in append mode, and keep logging rather
            // than crashing the operation being logged.
        }

        _writer = TryOpenWriter(_filePath);
        if (_writer == null)
        {
            _nextReopenTicks = Environment.TickCount64 + ReopenBackoffMs;
            _currentSize = 0;
        }
        else
        {
            // On a failed rotation the file is unchanged (still oversized). Reset the counter so the
            // next rotation is retried after ~one more full file rather than on every single line.
            _currentSize = rotated ? SafeLength(_filePath) : 0;
        }
    }

    /// <summary>
    /// Opens the log for appending, tolerating a concurrent writer via <see cref="FileShare.ReadWrite"/>.
    /// Returns <c>null</c> (never throws) when the file cannot be opened, so callers degrade to
    /// console-only logging instead of aborting the operation being logged.
    /// </summary>
    private static StreamWriter? TryOpenWriter(string filePath)
    {
        try
        {
            var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            return new StreamWriter(stream, System.Text.Encoding.UTF8) { AutoFlush = false };
        }
        catch (Exception ex) when (IsFileOpFailure(ex))
        {
            return null;
        }
    }

    private static long SafeLength(string filePath)
    {
        try
        {
            return new FileInfo(filePath).Length;
        }
        catch (Exception ex) when (IsFileOpFailure(ex))
        {
            return 0;
        }
    }

    // A logger must never abort the operation it logs, so every file operation degrades on the same
    // set of "the file is unavailable" failures (locked by another handle, permission denied, disk full).
    private static bool IsFileOpFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    // The console is the fail-soft sink, so it must not itself abort the logged operation: a closed or
    // redirected-then-broken stderr can throw. Swallow only the I/O failures that stem from that.
    private static void SafeConsoleError(string line)
    {
        try
        {
            Console.Error.WriteLine(line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private void DisposeWriterQuietly()
    {
        // Detach first so a throwing Dispose can never leave a disposed/broken writer installed.
        var writer = _writer;
        _writer = null;
        if (writer == null)
            return;

        try
        {
            // StreamWriter.Dispose flushes buffered data, which can throw on a broken handle.
            writer.Dispose();
        }
        catch (Exception ex) when (IsFileOpFailure(ex)) { }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            DisposeWriterQuietly();
        }
    }
}
