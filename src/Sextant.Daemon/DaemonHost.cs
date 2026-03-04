using Sextant.Core;
using Sextant.Indexer;
using Sextant.Store;
using Microsoft.CodeAnalysis;

namespace Sextant.Daemon;

public sealed class DaemonHost : IDisposable
{
    private readonly string _repoRoot;
    private readonly string _dbPath;
    private readonly string[] _solutionPaths;
    private readonly Action<string>? _log;

    private IndexDatabase? _db;
    private FileWatcherService? _fileWatcher;
    private StatusServer? _statusServer;
    private IndexingQueue? _queue;
    private CancellationTokenSource? _cts;
    private Task? _workerTask;
    private volatile string _state = "idle";
    private long _lastIndexedAt;
    private long _indexingStartedAt;
    private volatile IndexingProgress? _currentProgress;
    private Solution? _currentSolution;

    public int StatusPort => _statusServer?.Port ?? 0;

    public DaemonHost(string repoRoot, string dbPath, string[] solutionPaths, Action<string>? log = null)
    {
        _repoRoot = repoRoot;
        _dbPath = dbPath;
        _solutionPaths = solutionPaths;
        _log = log;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _log?.Invoke("Starting Sextant Daemon...");

        // Ensure database directory exists
        var dbDir = Path.GetDirectoryName(Path.GetFullPath(_dbPath));
        if (!string.IsNullOrEmpty(dbDir))
            Directory.CreateDirectory(dbDir);

        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _queue = new IndexingQueue();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Start status server
        _statusServer = new StatusServer(GetStatus);
        _statusServer.Start();
        _log?.Invoke($"Status server on port {_statusServer.Port}");

        // Write pid file
        WritePidFile();

        // Check if database needs full index or incremental catch-up
        var needsFullIndex = !File.Exists(_dbPath) || IsEmptyDatabase();
        if (needsFullIndex)
        {
            _log?.Invoke("No existing index — performing full initial index...");
            await PerformFullIndexAsync();
        }
        else
        {
            _log?.Invoke("Existing index found — checking for changed files...");
            await PerformIncrementalCatchUpAsync();
        }

        // Start file watcher
        _fileWatcher = new FileWatcherService(_repoRoot, OnFilesChanged);
        _fileWatcher.Start();
        _log?.Invoke("File watcher started.");

        // Start background worker
        _workerTask = ProcessQueueAsync(_cts.Token);

        _log?.Invoke("Daemon started.");
    }

    public async Task StopAsync()
    {
        _log?.Invoke("Stopping daemon...");

        _fileWatcher?.Stop();
        _queue?.Complete();
        _cts?.Cancel();

        if (_workerTask != null)
        {
            try { await _workerTask; }
            catch (OperationCanceledException) { }
        }

        RemovePidFile();
        _log?.Invoke("Daemon stopped.");
    }

    private async Task PerformFullIndexAsync()
    {
        _state = "indexing";
        _indexingStartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _currentProgress = null;
        var progressReporter = new Progress<IndexingProgress>(p => _currentProgress = p);
        try
        {
            foreach (var solutionPath in _solutionPaths)
            {
                _log?.Invoke($"Loading solution: {solutionPath}");
                _currentProgress = new IndexingProgress
                {
                    Phase = "loading_solution",
                    Description = $"Loading {Path.GetFileName(solutionPath)}",
                    ProjectIndex = 0,
                    ProjectCount = 0
                };
                var solution = await SolutionLoader.LoadSolutionAsync(solutionPath);
                _currentSolution = solution;

                var orchestrator = new IndexOrchestrator(_db!, _log);
                await orchestrator.IndexSolutionAsync(solution, progressReporter);
            }
            _lastIndexedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
        finally
        {
            _state = "idle";
            _currentProgress = null;
            _indexingStartedAt = 0;
        }
    }

    private async Task PerformIncrementalCatchUpAsync()
    {
        _state = "indexing";
        _indexingStartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _currentProgress = null;
        try
        {
            foreach (var solutionPath in _solutionPaths)
            {
                var solution = await SolutionLoader.LoadSolutionAsync(solutionPath);
                _currentSolution = solution;

                // Find all .cs files and check hashes
                var conn = _db!.GetConnection();
                var fileIndexStore = new FileIndexStore(conn);
                var changedFiles = new List<string>();

                foreach (var project in solution.Projects)
                {
                    if (project.FilePath == null) continue;
                    var identity = GitRemoteResolver.Resolve(project.FilePath);
                    var existing = new ProjectStore(conn).GetByCanonicalId(identity.CanonicalId);
                    if (existing == null) continue;

                    var projectId = existing.Value.id;
                    var compilation = await project.GetCompilationAsync();
                    if (compilation == null) continue;

                    foreach (var tree in compilation.SyntaxTrees)
                    {
                        if (string.IsNullOrEmpty(tree.FilePath) || SymbolExtractor.IsGeneratedFile(tree.FilePath))
                            continue;

                        var currentHash = IncrementalIndexer.ComputeFileHash(tree.FilePath);
                        var entry = fileIndexStore.GetByProjectAndFile(projectId, tree.FilePath);

                        if (entry == null || entry.ContentHash != currentHash)
                            changedFiles.Add(tree.FilePath);
                    }
                }

                if (changedFiles.Count > 0)
                {
                    _log?.Invoke($"Found {changedFiles.Count} changed files since last index.");
                    var incremental = new IncrementalIndexer(_db!, _log);
                    var signatureChanged = await incremental.IndexChangedFilesAsync(solution, changedFiles);

                    if (signatureChanged.Count > 0)
                    {
                        _queue!.Enqueue(new WorkItem
                        {
                            Priority = WorkPriority.Background,
                            FilePaths = signatureChanged,
                            Description = "Re-resolve references for signature-changed files"
                        });
                    }
                }
                else
                {
                    _log?.Invoke("All files up to date.");
                }
            }
            _lastIndexedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
        finally
        {
            _state = "idle";
            _currentProgress = null;
            _indexingStartedAt = 0;
        }
    }

    private void OnFilesChanged(IReadOnlyList<string> filePaths)
    {
        _queue?.Enqueue(new WorkItem
        {
            Priority = WorkPriority.Immediate,
            FilePaths = filePaths,
            Description = $"Re-index {filePaths.Count} changed files"
        });
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            WorkItem? item;
            try
            {
                item = await _queue!.DequeueAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (item == null) continue;

            _state = "indexing";
            _indexingStartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _currentProgress = new IndexingProgress
            {
                Phase = "incremental_index",
                Description = item.Description ?? "Processing work item",
                ProjectIndex = 0,
                ProjectCount = 0,
                ItemsTotal = item.FilePaths.Count
            };
            _log?.Invoke($"Processing: {item.Description}");

            try
            {
                if (_currentSolution != null)
                {
                    var incremental = new IncrementalIndexer(_db!, _log);
                    var signatureChanged = await incremental.IndexChangedFilesAsync(
                        _currentSolution, item.FilePaths);

                    if (signatureChanged.Count > 0 && item.Priority == WorkPriority.Immediate)
                    {
                        // Find all files that reference symbols from the changed files
                        var dependentFiles = FindDependentFiles(signatureChanged);
                        var allFilesToReindex = new HashSet<string>(signatureChanged);
                        foreach (var f in dependentFiles)
                            allFilesToReindex.Add(f);

                        _queue.Enqueue(new WorkItem
                        {
                            Priority = WorkPriority.Background,
                            FilePaths = allFilesToReindex.ToList(),
                            Description = $"Re-resolve references for {allFilesToReindex.Count} files affected by signature changes"
                        });
                    }
                }
                _lastIndexedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Error processing work item: {ex.Message}");
            }
            finally
            {
                _state = "idle";
                _currentProgress = null;
                _indexingStartedAt = 0;
            }
        }
    }

    private List<string> FindDependentFiles(IReadOnlyList<string> changedFiles)
    {
        var dependentFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var conn = _db!.GetConnection();
            var symbolStore = new SymbolStore(conn);
            var referenceStore = new ReferenceStore(conn);

            foreach (var filePath in changedFiles)
            {
                var symbols = symbolStore.GetByFile(filePath);
                foreach (var symbol in symbols)
                {
                    var refs = referenceStore.GetBySymbolId(symbol.Id);
                    foreach (var r in refs)
                    {
                        if (!changedFiles.Contains(r.FilePath))
                            dependentFiles.Add(r.FilePath);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Warning: could not find dependent files: {ex.Message}");
        }
        return dependentFiles.ToList();
    }

    private StatusInfo GetStatus()
    {
        var progress = _currentProgress;
        var startedAt = _indexingStartedAt;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        return new StatusInfo
        {
            State = _state,
            QueuedFiles = _queue?.GetImmediateCount() ?? 0,
            BackgroundTasks = _queue?.GetBackgroundCount() ?? 0,
            LastIndexedAt = _lastIndexedAt > 0 ? _lastIndexedAt : null,
            Phase = progress?.Phase,
            CurrentProject = progress?.CurrentProject,
            ProjectIndex = progress?.ProjectIndex ?? 0,
            ProjectCount = progress?.ProjectCount ?? 0,
            IndexingStartedAt = startedAt > 0 ? startedAt : null,
            ElapsedMs = startedAt > 0 ? now - startedAt : null
        };
    }

    private bool IsEmptyDatabase()
    {
        try
        {
            var conn = _db!.GetConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM projects;";
            var count = (long)cmd.ExecuteScalar()!;
            return count == 0;
        }
        catch
        {
            return true;
        }
    }

    private void WritePidFile()
    {
        var pidDir = Path.Combine(_repoRoot, ".sextant");
        Directory.CreateDirectory(pidDir);
        var pidFile = Path.Combine(pidDir, "daemon.pid");
        File.WriteAllText(pidFile, $"{Environment.ProcessId}\n{_statusServer!.Port}");
        _log?.Invoke($"PID file written: {pidFile}");
    }

    private void RemovePidFile()
    {
        var pidFile = Path.Combine(_repoRoot, ".sextant", "daemon.pid");
        if (File.Exists(pidFile))
        {
            File.Delete(pidFile);
            _log?.Invoke("PID file removed.");
        }
    }

    public void Dispose()
    {
        _fileWatcher?.Dispose();
        _statusServer?.Dispose();
        _cts?.Dispose();
    }
}
