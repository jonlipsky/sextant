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

        using var fileLogger = Core.FileLogger.Open(config.LogsPath, "indexer.log");

        try
        {
            var dbDir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
            if (!string.IsNullOrEmpty(dbDir))
                Directory.CreateDirectory(dbDir);

            Console.WriteLine("Loading solution...");
            var solution = await Indexer.SolutionLoader.LoadSolutionAsync(solutionPath);
            Console.WriteLine($"  Loaded {solution.ProjectIds.Count} projects");

            foreach (var project in solution.Projects)
                Console.WriteLine($"    - {project.Name}");

            Console.WriteLine();

            var indexDb = new Store.IndexDatabase(dbPath);
            indexDb.RunMigrations();

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

            var orchestrator = new Indexer.IndexOrchestrator(indexDb, logCallback);
            await orchestrator.IndexSolutionAsync(solution, progress);

            if (isInteractive)
                ClearLine();

            Console.WriteLine();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to index solution: {ex.Message}");
            return 1;
        }
    }

    private static void ClearLine()
    {
        Console.Write($"\r{new string(' ', Console.WindowWidth - 1)}\r");
    }
}
