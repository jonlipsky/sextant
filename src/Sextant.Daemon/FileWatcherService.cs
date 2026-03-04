namespace Sextant.Daemon;

public sealed class FileWatcherService : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Action<IReadOnlyList<string>> _onBatchReady;
    private readonly int _debounceMs;
    private readonly HashSet<string> _pendingFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private CancellationTokenSource? _debounceCts;

    public FileWatcherService(string rootPath, Action<IReadOnlyList<string>> onBatchReady, int debounceMs = 500)
    {
        _onBatchReady = onBatchReady;
        _debounceMs = debounceMs;

        // Watch *.cs files
        var csWatcher = CreateWatcher(rootPath, "*.cs");
        _watchers.Add(csWatcher);

        // Watch *.csproj files
        var csprojWatcher = CreateWatcher(rootPath, "*.csproj");
        _watchers.Add(csprojWatcher);
    }

    private FileSystemWatcher CreateWatcher(string rootPath, string filter)
    {
        var watcher = new FileSystemWatcher(rootPath, filter)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime
        };

        watcher.Changed += OnFileEvent;
        watcher.Created += OnFileEvent;
        watcher.Deleted += OnFileEvent;
        watcher.Renamed += (_, e) => OnFileChanged(e.FullPath);

        return watcher;
    }

    public void Start()
    {
        foreach (var w in _watchers)
            w.EnableRaisingEvents = true;
    }

    public void Stop()
    {
        foreach (var w in _watchers)
            w.EnableRaisingEvents = false;
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        OnFileChanged(e.FullPath);
    }

    private void OnFileChanged(string filePath)
    {
        // Ignore obj/, bin/, .git/ directories
        if (ShouldIgnore(filePath))
            return;

        lock (_lock)
        {
            _pendingFiles.Add(filePath);
            _debounceCts?.Cancel();
            _debounceCts = new CancellationTokenSource();
            var token = _debounceCts.Token;

            Task.Delay(_debounceMs, token).ContinueWith(_ =>
            {
                List<string> batch;
                lock (_lock)
                {
                    batch = [.. _pendingFiles];
                    _pendingFiles.Clear();
                }
                if (batch.Count > 0)
                    _onBatchReady(batch);
            }, TaskContinuationOptions.OnlyOnRanToCompletion);
        }
    }

    private static bool ShouldIgnore(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/obj/") ||
               normalized.Contains("/bin/") ||
               normalized.Contains("/.git/");
    }

    public void Dispose()
    {
        Stop();
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        foreach (var w in _watchers)
            w.Dispose();
    }
}
