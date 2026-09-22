using Microsoft.Build.Locator;

namespace Sextant.Benchmarks;

/// <summary>
/// Console entry point for the indexing benchmark harness. Registers MSBuild before any Roslyn
/// type is touched, parses arguments, runs the benchmark, and writes JSON + markdown reports.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Must run before any Roslyn/MSBuild type loads. Keep this the first statement and keep
        // all Roslyn-touching work in Execute so the JIT does not resolve those types earlier.
        if (!MSBuildLocator.IsRegistered)
            MSBuildLocator.RegisterDefaults();

        return await Execute(args);
    }

    private static async Task<int> Execute(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return 0;
        }

        string outDir;
        BenchmarkOptions options;
        try
        {
            (options, outDir) = ParseArgs(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            PrintUsage();
            return 2;
        }

        Directory.CreateDirectory(outDir);

        var profiles = string.Equals(options.Profile, "all", StringComparison.OrdinalIgnoreCase)
            ? new[] { Core.IndexProfiles.Core, Core.IndexProfiles.Standard, Core.IndexProfiles.Deep }
            : [options.Profile];

        var reports = new List<BenchmarkReport>();
        foreach (var profile in profiles)
        {
            options.Profile = profile;
            Console.WriteLine($"Running benchmark: corpus={options.Corpus}, profile={profile}, out={outDir}");

            BenchmarkReport report;
            try
            {
                report = await BenchmarkRunner.RunAsync(options);
            }
            catch (Exception ex)
            {
                var redactErrors = options.Redact || options.Corpus == "external";
                Console.Error.WriteLine(redactErrors
                    ? $"benchmark failed: {ex.GetType().Name}"
                    : $"benchmark failed: {ex}");
                return 1;
            }

            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var baseName = profiles.Length > 1
                ? $"benchmark-{options.Corpus}-{profile}-{stamp}"
                : $"benchmark-{options.Corpus}-{stamp}";
            var jsonPath = Path.Combine(outDir, baseName + ".json");
            var mdPath = Path.Combine(outDir, baseName + ".md");
            await File.WriteAllTextAsync(jsonPath, report.ToJson());
            await File.WriteAllTextAsync(mdPath, report.ToMarkdown());

            PrintSummary(report);
            Console.WriteLine();
            Console.WriteLine($"JSON:     {jsonPath}");
            Console.WriteLine($"Markdown: {mdPath}");
            reports.Add(report);
        }

        if (reports.Count > 1)
        {
            var comparison = BenchmarkReport.ToProfileComparison(reports);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var comparisonPath = Path.Combine(outDir, $"benchmark-{options.Corpus}-profiles-{stamp}.md");
            await File.WriteAllTextAsync(comparisonPath, comparison);
            Console.WriteLine();
            Console.WriteLine(comparison);
            Console.WriteLine($"Profile comparison: {comparisonPath}");
        }

        // Fail the command if any requested run did not complete, so CI never accepts a benchmark
        // whose full or incremental pass was cancelled or failed.
        var allOk = reports.All(report =>
        {
            var fullOk = report.FullIndex?.Status == Core.IndexRunStatus.Completed;
            var incrementalOk = report.IncrementalIndex == null
                || report.IncrementalIndex.Status == Core.IndexRunStatus.Completed;
            return fullOk && incrementalOk;
        });
        return allOk ? 0 : 1;
    }

    private static (BenchmarkOptions, string outDir) ParseArgs(string[] args)
    {
        var corpus = "correctness";
        string? path = null;
        string? work = null;
        var outDir = Path.Combine(Directory.GetCurrentDirectory(), "benchmark-results");
        var restore = true;
        var incremental = true;
        var redact = false;
        var intervalMs = 50;
        var largeProjects = 25;
        var largeTypes = 8;
        var largeMethods = 6;
        string? machine = null;
        var documentExtractor = false;
        var maxParallelism = 0;
        var profile = Core.IndexProfiles.Deep;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--corpus": corpus = RequireValue(args, ref i); break;
                case "--path": path = RequireValue(args, ref i); break;
                case "--out": outDir = RequireValue(args, ref i); break;
                case "--work": work = RequireValue(args, ref i); break;
                case "--no-restore": restore = false; break;
                case "--no-incremental": incremental = false; break;
                case "--redact": redact = true; break;
                case "--interval-ms": intervalMs = int.Parse(RequireValue(args, ref i)); break;
                case "--large-projects": largeProjects = int.Parse(RequireValue(args, ref i)); break;
                case "--large-types": largeTypes = int.Parse(RequireValue(args, ref i)); break;
                case "--large-methods": largeMethods = int.Parse(RequireValue(args, ref i)); break;
                case "--machine": machine = RequireValue(args, ref i); break;
                case "--document-extractor": documentExtractor = true; break;
                case "--max-parallelism": maxParallelism = int.Parse(RequireValue(args, ref i)); break;
                case "--profile": profile = RequireValue(args, ref i); break;
                default: throw new ArgumentException($"unknown argument '{args[i]}'");
            }
        }

        var options = new BenchmarkOptions
        {
            Corpus = corpus,
            SolutionPath = path,
            WorkDir = work ?? Path.Combine(Path.GetTempPath(), "sextant-benchmarks"),
            Restore = restore,
            RunIncremental = incremental,
            Redact = redact,
            SampleIntervalMs = intervalMs,
            LargeProjects = largeProjects,
            LargeTypes = largeTypes,
            LargeMethods = largeMethods,
            MachineDescription = machine,
            UseDocumentExtractor = documentExtractor,
            MaxParallelism = maxParallelism,
            Profile = profile,
            Log = msg => Console.WriteLine($"  {msg}")
        };
        return (options, outDir);
    }

    private static string RequireValue(string[] args, ref int i)
    {
        if (i + 1 >= args.Length)
            throw new ArgumentException($"missing value for '{args[i]}'");
        return args[++i];
    }

    private static void PrintSummary(BenchmarkReport report)
    {
        Console.WriteLine();
        Console.WriteLine(report.ToMarkdown());
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            Sextant indexing benchmark harness

            Usage:
              dotnet run --project tests/Sextant.Benchmarks -- [options]

            Options:
              --corpus <name>       self | correctness | large | external (default: correctness)
              --path <solution>     solution/project path (required for self override and external)
              --out <dir>           report output directory (default: ./benchmark-results)
              --work <dir>          scratch dir for generated corpora + databases
              --no-restore          skip 'dotnet restore' before loading
              --no-incremental      skip the incremental pass (generated corpora only)
              --redact              strip identifying data (implied for external)
              --interval-ms <n>     resource sampling interval (default: 50)
              --large-projects <n>  large-corpus project count (default: 25)
              --large-types <n>     types per project for the large corpus (default: 8)
              --large-methods <n>   methods per type for the large corpus (default: 6)
              --document-extractor  use the Phase 5 document-oriented extractor (default: legacy)
              --max-parallelism <n> analysis worker cap for the document extractor (0 = auto)
              --profile <name>      core | standard | deep | all (default: deep; 'all' sweeps every profile)
              --machine <label>     machine label recorded in the report
              -h, --help            show this help
            """);
    }
}
