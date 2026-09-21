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
    private WriterLease? _lease;
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
    private bool _useDocumentExtractor;
    private Indexer.ExtractionParallelismOptions _parallelism = Indexer.ExtractionParallelismOptions.Default;
    private IndexProfileDescriptor _profile = IndexProfileDescriptor.Full;

    // Phase 10: when true (single solution + git repo) the daemon drives a Git-authoritative overlay
    // reconciliation instead of the legacy file_index catch-up. The file watcher's events are hints;
    // this pass reconstructs the working-tree state from git and never relies on missed events.
    private bool _snapshotEnabled;
    private int _reconcileIntervalSeconds = 30;
    private Task? _periodicTask;
    private volatile OverlayReconcileResult? _lastReconcileResult;

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

        var config = SextantConfiguration.Load(_repoRoot);
        _db = new IndexDatabase(_dbPath, IndexWriteOptions.FromConfiguration(config));
        _useDocumentExtractor = config.DocumentExtractor;
        _parallelism = Indexer.ExtractionParallelismOptions.FromConfiguration(config);
        _profile = IndexProfileDescriptor.FromConfiguration(config);
        _reconcileIntervalSeconds = config.ReconcileIntervalSeconds;

        // Single-writer lease (issue #38 / #59): the daemon is a long-lived writer, so it holds the lease
        // for its whole lifetime and fails closed if another writer (a second daemon, the index service, or
        // a one-shot `sextant index`) already owns this database. Ordering matches the service: migrate
        // WITHOUT recovery, acquire the lease, THEN recover — so recovery never abandons a live writer's
        // staging generation.
        _db.RunMigrations(recover: false);
        _lease = WriterLease.AcquireOrThrow(
            _db.DbPath, $"sextant-daemon@{Environment.MachineName}#{Environment.ProcessId}");
        // Abort any in-flight index between batches if the lease is ever stolen (issue #38 / criterion 3).
        _db.SetWriterLostProbe(() => _lease.IsLost);
        _db.Recover();
        _queue = new IndexingQueue();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Phase 10: a single-solution repo under git is eligible for Git-authoritative overlay
        // reconciliation (which also produces Phase-9 snapshots). A multi-solution repo, or a non-git
        // working copy, stays on the legacy file_index catch-up path.
        _snapshotEnabled = _solutionPaths.Length == 1 && GitRemoteResolver.ResolveGitRoot(_repoRoot) != null;

        // Start status server
        _statusServer = new StatusServer(GetStatus);
        _statusServer.Start();
        _log?.Invoke($"Status server on port {_statusServer.Port}");

        // Write pid file
        WritePidFile();

        if (_snapshotEnabled)
        {
            // The authoritative Git reconciliation pass subsumes full index, incremental catch-up, and
            // overlay staging. It reconstructs the working-tree state purely from git and MUST run before
            // the file watcher is enabled (criterion 3), so a restart never depends on missed events.
            _log?.Invoke("Snapshot mode — running authoritative Git reconciliation...");
            await ReconcileAuthoritativeAsync(_cts.Token);
        }
        else
        {
            // Check if database needs full index or incremental catch-up
            var needsFullIndex = !File.Exists(_dbPath) || IsEmptyDatabase();
            if (needsFullIndex)
            {
                _log?.Invoke("No existing index — performing full initial index...");
            }
            else if (ConfigurationChangedSinceLastRun())
            {
                // The indexing profile / feature configuration changed since the last complete run (e.g.
                // core→standard, which never built the optional tables). An incremental catch-up would
                // leave the newly-enabled tables unbuilt (or the newly-disabled ones stale), so treat this
                // like a generation-invalidating change and rebuild fully. A null recorded hash (a
                // pre-Phase-8 generation) counts as changed, mirroring the null-fingerprint semantics.
                _log?.Invoke("Indexing configuration changed since last index — performing full re-index...");
                needsFullIndex = true;
            }

            if (needsFullIndex)
            {
                await PerformFullIndexAsync();
            }
            else
            {
                _log?.Invoke("Existing index found — checking for changed files...");
                await PerformIncrementalCatchUpAsync();
            }
        }

        // Start file watcher
        _fileWatcher = new FileWatcherService(_repoRoot, OnFilesChanged);
        _fileWatcher.Start();
        _log?.Invoke("File watcher started.");

        // Start background worker
        _workerTask = ProcessQueueAsync(_cts.Token);

        // Phase 10: periodic authoritative reconciliation catches changes the watcher missed (criterion 3).
        if (_snapshotEnabled)
            _periodicTask = RunPeriodicReconcileAsync(_cts.Token);

        _log?.Invoke("Daemon started.");
    }

    public async Task StopAsync()
    {
        _log?.Invoke("Stopping daemon...");

        _fileWatcher?.Stop();
        _queue?.Complete();
        _cts?.Cancel();

        if (_periodicTask != null)
        {
            try { await _periodicTask; }
            catch (OperationCanceledException) { }
        }

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

                var orchestrator = new IndexOrchestrator(_db!, _log, _useDocumentExtractor, _parallelism, _profile);
                // All solutions of one repo share ONE snapshot identity (commit+tree+schema+analyzer+
                // config+toolchain — no per-solution component), so with 2+ solutions the second and later
                // would idempotently attach to the first's snapshot and skip their own indexing. Until
                // multi-solution→one-snapshot aggregation lands (tracked follow-up), a multi-solution repo
                // stays on the legacy mutable-row path; a single-solution repo gets full Phase-9 snapshots.
                await orchestrator.IndexSolutionAsync(
                    solution, progressReporter, enableSnapshots: _solutionPaths.Length == 1);
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
            var conn = _db!.GetConnection();
            var projectStore = new ProjectStore(conn);
            var fileIndexStore = new FileIndexStore(conn);

            var loaded = new List<(Solution solution, List<string> changedPaths)>();
            var liveCanonicalIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var solutionPath in _solutionPaths)
            {
                var solution = await SolutionLoader.LoadSolutionAsync(solutionPath);
                _currentSolution = solution;

                var changedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var project in solution.Projects)
                {
                    if (project.FilePath == null) continue;
                    var targetFramework = ProjectIdentityFactory.ResolveEvaluatedTargetFramework(project);
                    var identity = GitRemoteResolver.Resolve(project.FilePath, targetFramework);
                    liveCanonicalIds.Add(identity.CanonicalId);

                    var existing = projectStore.GetByCanonicalId(identity.CanonicalId);
                    if (existing == null)
                    {
                        // A project added (or a new target framework) while the daemon was stopped has
                        // no DB row yet. Catch-up runs an incremental pass, not a full index, so mark
                        // all of the project's on-disk source files changed: IncrementalIndexer then
                        // registers the new per-TFM logical project and indexes it from scratch (its
                        // fingerprints are absent, so every file reads as stale).
                        foreach (var document in project.Documents)
                        {
                            var docPath = document.FilePath;
                            if (!string.IsNullOrEmpty(docPath)
                                && !SymbolExtractor.IsGeneratedFile(docPath)
                                && File.Exists(docPath))
                                changedPaths.Add(docPath);
                        }
                        continue;
                    }
                    var projectId = existing.Value.id;

                    // Changed / new source files: on-disk hash differs from the recorded fingerprint.
                    var compilation = await project.GetCompilationAsync();
                    if (compilation != null)
                    {
                        foreach (var tree in compilation.SyntaxTrees)
                        {
                            if (string.IsNullOrEmpty(tree.FilePath) || SymbolExtractor.IsGeneratedFile(tree.FilePath))
                                continue;
                            if (!File.Exists(tree.FilePath)) continue;
                            var currentHash = IncrementalIndexer.ComputeFileHash(tree.FilePath);
                            var entry = fileIndexStore.GetByProjectAndFile(projectId, tree.FilePath);
                            if (entry == null || entry.ContentHash != currentHash)
                                changedPaths.Add(tree.FilePath);
                        }
                    }

                    // Deleted / renamed-away files: recorded in file_index but gone from disk.
                    foreach (var entry in fileIndexStore.GetByProject(projectId))
                    {
                        if (!File.Exists(entry.FilePath))
                            changedPaths.Add(entry.FilePath);
                    }
                }

                loaded.Add((solution, changedPaths.ToList()));
            }

            // Purge projects that were removed from every watched solution (their historical snapshots
            // go with them); a rebuild of a still-present project never deletes its project row.
            foreach (var (projectId, identity) in projectStore.GetAll())
            {
                if (!liveCanonicalIds.Contains(identity.CanonicalId))
                {
                    _log?.Invoke($"Removing project no longer in solution: {identity.CanonicalId}");
                    projectStore.Delete(projectId);
                }
            }

            // Reindex each solution's invalidated closure. IncrementalIndexer also recomputes evaluation
            // fingerprints, so a config/props/global.json/assets change escalates even with no .cs delta;
            // an unchanged solution yields an empty closure and does no work.
            foreach (var (solution, changedPaths) in loaded)
            {
                var incremental = new IncrementalIndexer(_db!, _log, _useDocumentExtractor, _parallelism, _profile);
                await incremental.IndexChangedFilesAsync(solution, changedPaths);
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

    /// <summary>
    /// Issue #28: re-reads <c>sextant.json</c> so a live config edit (extractor toggle, parallelism,
    /// profile, reconcile interval) is picked up on the authoritative pass — not only when the daemon
    /// first started. Called at the head of every reconciliation, so a stale cached runtime config never
    /// drives an index.
    /// </summary>
    private void RefreshRuntimeConfig()
    {
        var config = SextantConfiguration.Load(_repoRoot);
        _useDocumentExtractor = config.DocumentExtractor;
        _parallelism = Indexer.ExtractionParallelismOptions.FromConfiguration(config);
        _profile = IndexProfileDescriptor.FromConfiguration(config);
        _reconcileIntervalSeconds = config.ReconcileIntervalSeconds;
    }

    /// <summary>
    /// The AUTHORITATIVE Git reconciliation pass (Phase 10). Re-reads config (issue #28), re-resolves the
    /// solution from disk (so a project/config change since the last pass is honoured — issue #28), and
    /// runs <see cref="LocalOverlayReconciler"/>, which reconstructs the working-tree state from git and
    /// publishes a base/overlay/fallback generation atomically. Serialized through the single worker (and
    /// the startup call), so it is the only writer.
    /// </summary>
    private async Task ReconcileAuthoritativeAsync(CancellationToken ct)
    {
        _state = "indexing";
        _indexingStartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _currentProgress = new IndexingProgress
        {
            Phase = "reconciling",
            Description = "Git-authoritative reconciliation",
            ProjectIndex = 0,
            ProjectCount = 0
        };
        try
        {
            // #28: re-read config and re-resolve the solution every pass, never a stale cached copy.
            RefreshRuntimeConfig();
            var solutionPath = _solutionPaths[0];
            var solution = await SolutionLoader.LoadSolutionAsync(solutionPath);
            _currentSolution = solution;

            var reconciler = new LocalOverlayReconciler(_db!, _log, _useDocumentExtractor, _parallelism, _profile);
            var result = await reconciler.ReconcileAsync(solution, ct);
            _lastReconcileResult = result;
            var reasonSuffix = result.FallbackReason is { } r ? $" — {r}" : string.Empty;
            _log?.Invoke($"Reconcile: {result.Kind} ({result.ChangeCount} change(s)){reasonSuffix}");
            _lastIndexedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
        finally
        {
            _state = "idle";
            _currentProgress = null;
            _indexingStartedAt = 0;
        }
    }

    /// <summary>
    /// Periodic authoritative reconciliation (criterion 3): even if the file watcher drops or coalesces
    /// events, a full git reconstruction runs on the interval and converges the overlay on the true
    /// working-tree state. Enqueues a hint so the pass serializes through the single worker.
    /// </summary>
    private async Task RunPeriodicReconcileAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Re-read the interval each loop so a live edit to reconcile_interval_seconds takes effect.
            var seconds = _reconcileIntervalSeconds > 0 ? _reconcileIntervalSeconds : 30;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (ct.IsCancellationRequested) break;

            // A negative/zero interval disables periodic reconciliation, but keep the loop alive so a
            // later positive edit re-enables it without a daemon restart.
            if (_reconcileIntervalSeconds <= 0) continue;

            // Coalesce: a periodic reconcile is an IDEMPOTENT full reconstruction of the working-tree
            // delta from git, so at most one needs to be pending at a time. Skipping the enqueue while a
            // Background item is already queued bounds the queue under sustained load — a slow indexer no
            // longer accretes a backlog of redundant reconciles. Convergence is preserved: once the
            // pending pass is dequeued and starts, GetBackgroundCount drops to 0 and the next tick
            // enqueues a fresh reconcile that reads the current git state at execution time.
            if (_queue is { } queue && queue.GetBackgroundCount() == 0)
            {
                queue.Enqueue(new WorkItem
                {
                    Priority = WorkPriority.Background,
                    FilePaths = Array.Empty<string>(),
                    Description = "Periodic Git reconciliation"
                });
            }
        }
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
                if (_snapshotEnabled)
                {
                    // Git is authoritative: the watcher's file list is only a hint that SOMETHING may
                    // have changed. Reconstruct the whole working-tree delta from git and reconcile the
                    // overlay, so a missed/merged event never leaves the index diverged (criterion 3).
                    await ReconcileAuthoritativeAsync(ct);
                    continue;
                }

                if (_currentSolution != null)
                {
                    // The incremental indexer rebuilds the full invalidated project closure, so there
                    // is no signature-changed follow-up set to re-enqueue: every dependent in the
                    // closure was already rebuilt in the same pass.
                    var incremental = new IncrementalIndexer(_db!, _log, _useDocumentExtractor, _parallelism, _profile);
                    await incremental.IndexChangedFilesAsync(_currentSolution, item.FilePaths);
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

    /// <summary>
    /// True when the served generation was built with inputs incompatible with the current environment,
    /// so an incremental catch-up would be unsafe and the caller forces a full re-index instead. Phase 9
    /// compares the full snapshot fingerprint tuple of the selected snapshot — schema version, analyzer
    /// version, configuration hash, and toolchain fingerprint — so a change to ANY of them prevents
    /// unsafe reuse (criterion 4b) exactly as a config-hash change did in Phase 8. Falls back to the
    /// Phase-8 config-hash-only comparison against the <c>index_runs</c> ledger for a pre-Phase-9
    /// generation with no snapshot. A null recorded value counts as changed. Never treats a
    /// missing/broken ledger as changed, so a fresh full-index decision is left to
    /// <see cref="IsEmptyDatabase"/>.
    /// </summary>
    private bool ConfigurationChangedSinceLastRun()
    {
        try
        {
            var conn = _db!.GetConnection();
            var snapshotStore = new SnapshotStore(conn);
            if (snapshotStore.GetSelectedSnapshotId() is long id && snapshotStore.GetById(id) is { } snap)
            {
                return snap.SchemaVersion != IndexDatabase.LatestSchemaVersion
                    || !string.Equals(snap.AnalyzerVersion, IndexConfigurationHash.AnalyzerVersion, StringComparison.Ordinal)
                    || !string.Equals(snap.ConfigHash, _profile.ConfigurationHash, StringComparison.Ordinal)
                    || !string.Equals(snap.ToolchainFingerprint, ToolchainFingerprint.Current, StringComparison.Ordinal);
            }

            var lastComplete = new IndexRunStore(conn).GetLastCompleteRun();
            if (lastComplete == null)
                return false;
            return !string.Equals(lastComplete.ConfigHash, _profile.ConfigurationHash, StringComparison.Ordinal);
        }
        catch
        {
            return false;
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
        // Release the single-writer lease BEFORE closing the database so the next writer can acquire it
        // immediately on a clean shutdown (a crash leaves it to expire).
        _lease?.Dispose();
        _db?.Dispose();
    }
}
