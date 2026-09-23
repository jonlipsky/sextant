using System.Diagnostics;

namespace Sextant.Cli.Handlers;

internal static class IndexHandler
{
    public static async Task<int> RunAsync(string solutionPath, string? db, string? profile)
    {
        if (!File.Exists(solutionPath))
        {
            Console.Error.WriteLine($"Solution file not found: {solutionPath}");
            return 1;
        }

        var config = Core.SextantConfiguration.Load();
        var dbPath = DbResolver.Resolve(db, profile, config);
        if (dbPath == null) return 1;

        Console.WriteLine($"Sextant Indexer");
        Console.WriteLine($"  Solution: {Path.GetFullPath(solutionPath)}");
        Console.WriteLine($"  Database: {Path.GetFullPath(dbPath)}");
        Console.WriteLine();

        // Derive the log path from the RESOLVED db (honoring --db), and scope the filename to this
        // process so two concurrent index runs from the same CWD never collide (issue #91), mirroring
        // the MCP host's mcp-{ProcessId}.log convention.
        using var fileLogger = Core.FileLogger.Open(
            Core.SextantConfiguration.LogsPathFor(dbPath), $"indexer-{Environment.ProcessId}.log");

        try
        {
            var dbDir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
            if (!string.IsNullOrEmpty(dbDir))
                Directory.CreateDirectory(dbDir);

            Console.WriteLine("Loading solution...");
            var loadResult = await Indexer.SolutionLoader.LoadSolutionResilientlyAsync(
                solutionPath, fileLogger.CreateCallback());
            var solution = loadResult.Solution;
            Console.WriteLine($"  Loaded {solution.ProjectIds.Count} projects");

            foreach (var project in solution.Projects)
                Console.WriteLine($"    - {project.Name}");

            Console.WriteLine();

            using var indexDb = new Store.IndexDatabase(dbPath, Store.IndexWriteOptions.FromConfiguration(config));
            indexDb.RunMigrations(recover: false);

            // Single-writer lease (issue #38 / #59): a one-shot index is a write path too, so fail closed
            // if a daemon or index service already owns this database instead of racing a second writer and
            // risking a corrupt publish (criterion 3). Held only for this run; released when it disposes.
            using var lease = Store.WriterLease.AcquireOrThrow(
                indexDb.DbPath, $"sextant-index@{Environment.MachineName}#{Environment.ProcessId}");
            // Abort the index between batches if the lease is ever stolen (issue #38 / criterion 3).
            indexDb.SetWriterLostProbe(() => lease.IsLost);
            indexDb.Recover();

            var stopwatch = Stopwatch.StartNew();
            var isInteractive = !Console.IsOutputRedirected;
            var lastPhase = "";

            var logCallback = fileLogger.CreateCallback(msg =>
            {
                // Detailed log lines go to file; only phase headers go to console
                // (progress reporting handles the per-project status)
            });

            var progress = new Progress<Indexer.IndexingProgress>(p =>
            {
                if (p.Phase != lastPhase)
                {
                    // Starting a new phase — print a header line
                    if (isInteractive && lastPhase != "")
                        ClearLine();

                    var phaseLabel = p.Phase switch
                    {
                        "registering_projects" => "Registering projects",
                        "extracting_symbols" => "Extracting symbols",
                        "extracting_relationships" => "Extracting relationships",
                        "extracting_references" => "Extracting references",
                        "extracting_comments" => "Extracting comments",
                        "extracting_call_graph" => "Extracting call graph",
                        "recording_dependencies" => "Recording dependencies",
                        "capturing_api_surface" => "Capturing API surface",
                        "complete" => "Done",
                        _ => p.Description
                    };

                    if (p.Phase == "complete")
                    {
                        Console.WriteLine($"  {phaseLabel}. ({stopwatch.Elapsed:mm\\:ss} elapsed)");
                    }
                    else
                    {
                        Console.WriteLine($"  {phaseLabel}...");
                    }

                    lastPhase = p.Phase;
                }

                // Show per-project progress on the same line
                if (p.CurrentProject != null && p.ProjectCount > 0 && isInteractive)
                {
                    ClearLine();
                    Console.Write($"    [{p.ProjectIndex}/{p.ProjectCount}] {p.CurrentProject}");
                }
            });

            var orchestrator = new Indexer.IndexOrchestrator(
                indexDb, logCallback, config.DocumentExtractor,
                Indexer.ExtractionParallelismOptions.FromConfiguration(config),
                Core.IndexProfileDescriptor.FromConfiguration(config));

            // Issue #49: if HEAD/the working tree moves mid-index the pass aborts before publishing
            // (no mixed-state generation). Retry a bounded number of times, RELOADING the solution each
            // attempt so it reflects the now-current on-disk state and a fresh git pin is captured. If the
            // tree is still moving after the retries, report cleanly without having published anything.
            const int maxAttempts = 3;
            var published = false;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    await orchestrator.IndexSolutionAsync(solution, progress);
                    published = true;
                    break;
                }
                catch (Indexer.GitStateMovedException)
                {
                    if (isInteractive) ClearLine();
                    if (attempt < maxAttempts)
                    {
                        Console.WriteLine($"  Repository changed during indexing (attempt {attempt}/{maxAttempts}); reloading and retrying...");
                        // Reassign the WHOLE result, not just the Solution: the reloaded solution may have
                        // a different skipped-project set (the on-disk state changed), and we must report
                        // the completeness of the generation we actually publish (issue #90 accuracy).
                        loadResult = await Indexer.SolutionLoader.LoadSolutionResilientlyAsync(
                            solutionPath, fileLogger.CreateCallback());
                        solution = loadResult.Solution;
                        lastPhase = "";
                    }
                }
            }

            if (isInteractive)
                ClearLine();

            if (!published)
            {
                Console.Error.WriteLine(
                    "Repository kept changing during indexing (HEAD or working tree moved); nothing was " +
                    "published. Re-run 'sextant index' once the working tree is stable.");
                return 1;
            }

            // Report completeness against the generation that was actually published (loadResult is the
            // final reload if a git-move retry occurred), so the PARTIAL warning matches the DB on disk.
            ReportSkippedProjects(loadResult);

            Console.WriteLine();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to index solution: {ex.Message}");
            return 1;
        }
    }

    private static void ReportSkippedProjects(Indexer.SolutionLoadResult loadResult)
    {
        if (!loadResult.IsPartial)
            return;

        Console.WriteLine();
        Console.WriteLine(
            $"  WARNING: {loadResult.SkippedProjects.Count} project(s) could not be loaded and were skipped. " +
            "The index is PARTIAL — symbols from these projects are missing:");
        foreach (var skipped in loadResult.SkippedProjects)
            Console.WriteLine($"    - {skipped.ProjectName}: {skipped.Reason}");
    }

    private static void ClearLine()
    {
        Console.Write($"\r{new string(' ', Console.WindowWidth - 1)}\r");
    }
}
