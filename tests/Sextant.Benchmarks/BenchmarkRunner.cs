using System.Diagnostics;
using System.Runtime.InteropServices;
using Sextant.Core;
using Sextant.Indexer;
using Sextant.Store;

namespace Sextant.Benchmarks;

/// <summary>Inputs that select and shape a benchmark run.</summary>
public sealed class BenchmarkOptions
{
    /// <summary>One of: self, correctness, large, external.</summary>
    public string Corpus { get; init; } = "correctness";

    /// <summary>Solution/project path for the self and external corpora.</summary>
    public string? SolutionPath { get; init; }

    /// <summary>Scratch directory for generated corpora and isolated databases (never the source tree).</summary>
    public string WorkDir { get; init; } = Path.Combine(Path.GetTempPath(), "sextant-benchmarks");

    public bool Restore { get; init; } = true;
    public bool RunIncremental { get; init; } = true;
    public bool Redact { get; init; }
    public int SampleIntervalMs { get; init; } = 50;

    public int LargeProjects { get; init; } = 25;
    public int LargeTypes { get; init; } = 8;
    public int LargeMethods { get; init; } = 6;

    public string? MachineDescription { get; init; }
    public Action<string>? Log { get; init; }

    /// <summary>Maximum diagnostic messages retained in the report (the full count is always kept).</summary>
    public int MaxDiagnostics { get; init; } = 100;
}

/// <summary>
/// Runs full and (for generated corpora) incremental indexing against an isolated database,
/// sampling resource usage throughout, and assembles a <see cref="BenchmarkReport"/>.
/// </summary>
public sealed class BenchmarkRunner
{
    public static async Task<BenchmarkReport> RunAsync(BenchmarkOptions options, CancellationToken cancellationToken = default)
    {
        var redact = options.Redact || options.Corpus == "external";
        // In redact mode, suppress all free-text logging (project, solution, and file names) so
        // nothing identifying reaches the console or CI logs; the redacted report is the sole output.
        Action<string> log = redact ? (_ => { }) : (options.Log ?? (_ => { }));
        Directory.CreateDirectory(options.WorkDir);

        var (solutionPath, isGenerated) = ResolveSolution(options, log);

        if (options.Restore)
            Restore(solutionPath, log);

        var dbPath = PrepareDatabasePath(options);

        var report = new BenchmarkReport
        {
            CorpusName = options.Corpus,
            SchemaVersion = BenchmarkReport.CurrentSchemaVersion,
            Environment = BuildEnvironment(options, solutionPath)
        };

        using var db = new IndexDatabase(dbPath);
        db.RunMigrations();

        report.FullIndex = await RunFullAsync(db, solutionPath, options, log, cancellationToken);

        var incrementalAllowed = options.RunIncremental && isGenerated
            && report.FullIndex.Status == IndexRunStatus.Completed;
        if (incrementalAllowed)
            report.IncrementalIndex = await RunIncrementalAsync(db, solutionPath, options, log, cancellationToken);

        return redact ? Redactor.Apply(report) : report;
    }

    private static async Task<IndexingMetrics> RunFullAsync(
        IndexDatabase db, string solutionPath, BenchmarkOptions options, Action<string> log, CancellationToken ct)
    {
        var metrics = new IndexingMetrics { Mode = "full" };
        using var sampler = new ResourceSampler(db, TimeSpan.FromMilliseconds(options.SampleIntervalMs));

        var runSw = Stopwatch.StartNew();
        try
        {
            // Solution loading is inside the try so a cancellation (or failure) during load still
            // produces a preserved, cancelled/failed report rather than escaping the runner.
            var loadSw = Stopwatch.StartNew();
            var solution = await SolutionLoader.LoadSolutionAsync(
                solutionPath, d => CaptureDiagnostic(metrics, d, options.MaxDiagnostics), ct);
            loadSw.Stop();
            metrics.SolutionLoadMs = loadSw.ElapsedMilliseconds;

            var orchestrator = new IndexOrchestrator(db, log);
            await orchestrator.IndexSolutionAsync(solution, progress: null, metrics, ct);
        }
        catch (OperationCanceledException)
        {
            metrics.Status = IndexRunStatus.Cancelled;
            metrics.FailureReason = "cancelled";
            MarkInterruptedPhase(metrics, IndexRunStatus.Cancelled);
        }
        catch (Exception ex)
        {
            metrics.Status = IndexRunStatus.Failed;
            metrics.FailureReason = ex.Message;
            MarkInterruptedPhase(metrics, IndexRunStatus.Failed);
        }
        runSw.Stop();

        // The orchestrator owns total_duration_ms (measured with the metrics clock, excluding the
        // post-index aggregate queries). Fall back to the wall clock only if it was never set.
        if (metrics.TotalDurationMs == 0)
            metrics.TotalDurationMs = runSw.ElapsedMilliseconds;

        // On success the orchestrator already collected row metrics; collect partial counts here for
        // cancelled/failed runs so the report still reflects rows written before the interruption.
        if (metrics.Status != IndexRunStatus.Completed)
            metrics.Rows = SafeCollectRows(db);

        FinalizeMeasurements(db, sampler, metrics);
        return metrics;
    }

    private static async Task<IndexingMetrics> RunIncrementalAsync(
        IndexDatabase db, string solutionPath, BenchmarkOptions options, Action<string> log, CancellationToken ct)
    {
        var metrics = new IndexingMetrics { Mode = "incremental" };

        var changedFile = PickAndEditSourceFile(solutionPath);
        metrics.ChangedFileCount = 1;

        using var sampler = new ResourceSampler(db, TimeSpan.FromMilliseconds(options.SampleIntervalMs));

        var phase = new PhaseMetric { Name = "incremental_reindex" };
        metrics.Phases.Add(phase);

        try
        {
            var loadSw = Stopwatch.StartNew();
            var solution = await SolutionLoader.LoadSolutionAsync(
                solutionPath, d => CaptureDiagnostic(metrics, d, options.MaxDiagnostics), ct);
            loadSw.Stop();
            metrics.SolutionLoadMs = loadSw.ElapsedMilliseconds;
            metrics.ProjectCount = solution.Projects.Count();
            phase.ProjectsProcessed = metrics.ProjectCount;

            var incremental = new IncrementalIndexer(db, log);
            // total_duration_ms excludes solution load, so time only the reindex.
            var indexSw = Stopwatch.StartNew();
            await incremental.IndexChangedFilesAsync(solution, [changedFile], ct);
            indexSw.Stop();
            phase.DurationMs = indexSw.ElapsedMilliseconds;
            metrics.TotalDurationMs = indexSw.ElapsedMilliseconds;
            phase.Status = IndexRunStatus.Completed;
            metrics.Status = IndexRunStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            metrics.Status = IndexRunStatus.Cancelled;
            metrics.FailureReason = "cancelled";
            phase.Status = IndexRunStatus.Cancelled;
        }
        catch (Exception ex)
        {
            metrics.Status = IndexRunStatus.Failed;
            metrics.FailureReason = ex.Message;
            phase.Status = IndexRunStatus.Failed;
        }

        // The incremental indexer does not populate row metrics; collect them once here for all outcomes.
        metrics.Rows = SafeCollectRows(db);
        FinalizeMeasurements(db, sampler, metrics);
        return metrics;
    }

    /// <summary>
    /// Records final vs. peak storage and peak memory. The final WAL size is captured before the
    /// checkpoint folds it into the main database; the peak DB+WAL is the transient maximum sampled
    /// during the run, which is what later phases must reduce.
    /// </summary>
    private static void FinalizeMeasurements(IndexDatabase db, ResourceSampler sampler, IndexingMetrics metrics)
    {
        sampler.Sample();

        metrics.Storage.FinalWalBytes = db.WalBytes;
        metrics.Storage.FinalShmBytes = db.ShmBytes;
        db.Checkpoint();
        metrics.Storage.FinalDbBytes = db.MainDbBytes;
        metrics.Storage.PeakDbPlusWalBytes = Math.Max(sampler.PeakDbPlusWalBytes, metrics.Storage.FinalDbBytes);
        metrics.Storage.PeakWalBytes = sampler.PeakWalBytes;
        metrics.Storage.PeakShmBytes = sampler.PeakShmBytes;
        metrics.Storage.PeakStagedArtifactBytes = Math.Max(sampler.PeakStagedArtifactBytes, metrics.Storage.FinalDbBytes);

        metrics.Memory.PeakManagedBytes = sampler.PeakManagedBytes;
        metrics.Memory.PeakWorkingSetBytes = sampler.PeakWorkingSetBytes;
    }

    /// <summary>Best-effort row-metric collection; never throws so it is safe on a partially-written index.</summary>
    private static RowCountMetrics SafeCollectRows(IndexDatabase db)
    {
        try { return new IndexMetricsStore(db.GetConnection()).Collect(); }
        catch { return new RowCountMetrics(); }
    }

    /// <summary>Stamps the last still-running phase with a terminal status when a run is interrupted.</summary>
    private static void MarkInterruptedPhase(IndexingMetrics metrics, IndexRunStatus status)
    {
        var phase = metrics.Phases.LastOrDefault(p => p.Status == IndexRunStatus.Running);
        if (phase != null) phase.Status = status;
    }

    private static void CaptureDiagnostic(IndexingMetrics metrics, string message, int max)
    {
        metrics.WorkspaceDiagnosticCount++;
        if (metrics.WorkspaceDiagnostics.Count < max)
            metrics.WorkspaceDiagnostics.Add(message);
    }

    private static (string solutionPath, bool isGenerated) ResolveSolution(BenchmarkOptions options, Action<string> log)
    {
        switch (options.Corpus)
        {
            case "self":
                var self = options.SolutionPath ?? FindSextantSolution()
                    ?? throw new InvalidOperationException("Could not locate Sextant.slnx; pass --path.");
                return (Path.GetFullPath(self), false);

            case "external":
                if (string.IsNullOrWhiteSpace(options.SolutionPath))
                    throw new InvalidOperationException("The external corpus requires --path <solution>.");
                return (Path.GetFullPath(options.SolutionPath), false);

            case "correctness":
                log("Generating correctness corpus...");
                return (CorpusGenerator.GenerateCorrectnessCorpus(Path.Combine(options.WorkDir, "correctness-src")), true);

            case "large":
                log($"Generating large corpus ({options.LargeProjects} projects)...");
                return (CorpusGenerator.GenerateLargeSolution(
                    Path.Combine(options.WorkDir, "large-src"),
                    options.LargeProjects, options.LargeTypes, options.LargeMethods), true);

            default:
                throw new ArgumentException($"Unknown corpus '{options.Corpus}'. Use self, correctness, large, or external.");
        }
    }

    private static string PrepareDatabasePath(BenchmarkOptions options)
    {
        var dbDir = Path.Combine(options.WorkDir, "db", options.Corpus);
        if (Directory.Exists(dbDir))
            Directory.Delete(dbDir, recursive: true);
        Directory.CreateDirectory(dbDir);
        return Path.Combine(dbDir, "index.db");
    }

    private static void Restore(string solutionPath, Action<string> log)
    {
        log($"Restoring {Path.GetFileName(solutionPath)}...");
        try
        {
            var psi = new ProcessStartInfo("dotnet", $"restore \"{solutionPath}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(solutionPath) ?? "."
            };
            using var process = Process.Start(psi);
            if (process == null) { log("Restore could not start; continuing."); return; }
            // Drain both redirected streams concurrently: reading one to EOF before the other can
            // deadlock if the child fills the other pipe's buffer (dotnet restore writes to stdout).
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEnd();
            stdoutTask.GetAwaiter().GetResult();
            process.WaitForExit();
            if (process.ExitCode != 0)
                log($"Restore reported exit code {process.ExitCode} (continuing): {stderr.Trim()}");
        }
        catch (Exception ex)
        {
            log($"Restore failed to run ({ex.Message}); continuing.");
        }
    }

    /// <summary>Picks a deterministic source file from a generated corpus and appends a method to it.</summary>
    private static string PickAndEditSourceFile(string solutionPath)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        var candidate = Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .OrderBy(p => p, StringComparer.Ordinal)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("No source file found to edit for the incremental run.");

        var text = File.ReadAllText(candidate);
        var lastBrace = text.LastIndexOf('}');
        if (lastBrace < 0)
            throw new InvalidOperationException($"Unexpected source layout in {candidate}.");

        var edited = text[..lastBrace]
            + "    public int Benchmark_Touch() => 42;\n"
            + text[lastBrace..];
        File.WriteAllText(candidate, edited);
        return candidate;
    }

    private static BenchmarkEnvironment BuildEnvironment(BenchmarkOptions options, string solutionPath)
    {
        return new BenchmarkEnvironment
        {
            TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
            OsDescription = RuntimeInformation.OSDescription,
            RuntimeVersion = RuntimeInformation.FrameworkDescription,
            Architecture = RuntimeInformation.OSArchitecture.ToString(),
            ProcessorCount = System.Environment.ProcessorCount,
            MachineDescription = options.MachineDescription ?? System.Environment.MachineName,
            GitCommit = TryGetGitCommit(Path.GetDirectoryName(Path.GetFullPath(solutionPath)))
        };
    }

    private static string? TryGetGitCommit(string? dir)
    {
        if (dir == null) return null;
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse --short HEAD")
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process == null) return null;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindSextantSolution()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            var slnx = Path.Combine(dir, "Sextant.slnx");
            if (File.Exists(slnx)) return slnx;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
