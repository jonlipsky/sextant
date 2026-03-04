namespace Sextant.Core;

/// <summary>
/// Simple file logger with timestamped lines and size-based rotation.
/// Thread-safe via lock. Implements IDisposable for flush/close.
/// </summary>
public sealed class FileLogger : IDisposable
{
    private readonly string _filePath;
    private readonly long _maxSizeBytes;
    private readonly object _lock = new();
    private StreamWriter _writer;
    private long _currentSize;
    private bool _disposed;

    private FileLogger(string filePath, long maxSizeBytes)
    {
        _filePath = filePath;
        _maxSizeBytes = maxSizeBytes;
        _writer = OpenWriter(filePath);
        _currentSize = new FileInfo(filePath).Length;
    }

    /// <summary>
    /// Opens a file logger. Creates the directory if needed.
    /// </summary>
    public static FileLogger Open(string logDir, string fileName, long maxSizeBytes = 10 * 1024 * 1024)
    {
        Directory.CreateDirectory(logDir);
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
            RotateIfNeeded();
            _writer.WriteLine(line);
            _writer.Flush();
            _currentSize += System.Text.Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
        }
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
        if (_currentSize < _maxSizeBytes)
            return;

        _writer.Flush();
        _writer.Dispose();

        var rotatedPath = _filePath + ".1";
        if (File.Exists(rotatedPath))
            File.Delete(rotatedPath);

        File.Move(_filePath, rotatedPath);

        _writer = OpenWriter(_filePath);
        _currentSize = 0;
    }

    private static StreamWriter OpenWriter(string filePath)
    {
        var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
        return new StreamWriter(stream, System.Text.Encoding.UTF8) { AutoFlush = false };
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _writer.Flush();
            _writer.Dispose();
        }
    }
}
