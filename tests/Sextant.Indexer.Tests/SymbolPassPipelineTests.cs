using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Indexer.Tests;

/// <summary>
/// Issue #282 through the real orchestrator and store: the symbol pass analyzes projects on the pipeline's producer
/// while its single writer persists earlier ones. The persisted symbols, files and file versions (row order and
/// ids included) must not depend on how many projects are in flight or how deep the queue is, and a cancellation
/// in the middle of the pass must tear down cleanly without publishing. In-memory projects with on-disk sources (no
/// MSBuild), so the tests are fast.
/// </summary>
[TestClass]
public class SymbolPassPipelineTests
{
    private const int Projects = 7;

    private string _root = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sextant_symbolpass_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [TestMethod]
    [DataRow(1, 1, 1)]
    [DataRow(4, 1, 1)]
    [DataRow(4, 2, 2)]
    [DataRow(8, 4, 2)]
    [DataRow(8, 8, 1)]
    public async Task PersistedSymbolsAndFiles_AreIdenticalAtEveryPipelineShape(
        int maxParallelism, int projectsInFlight, int queueCapacity)
    {
        var sequential = await IndexAndDumpAsync("seq", ExtractionParallelismOptions.Sequential);
        var pipelined = await IndexAndDumpAsync($"p{maxParallelism}-{projectsInFlight}-{queueCapacity}",
            new ExtractionParallelismOptions
            {
                MaxParallelism = maxParallelism, ProjectsInFlight = projectsInFlight, QueueCapacity = queueCapacity
            });

        StringAssert.Contains(sequential, "Widget6", "every project's symbols are persisted");
        Assert.AreEqual(sequential, pipelined,
            "the symbol pass's rows, their order and ids must not depend on the pipeline's shape");
    }

    [TestMethod]
    public async Task PerProjectTimings_AreRecordedInProjectOrder()
    {
        var metrics = new IndexingMetrics { Mode = "full" };
        var dump = await IndexAndDumpAsync("metrics",
            new ExtractionParallelismOptions { MaxParallelism = 4, ProjectsInFlight = 3, QueueCapacity = 2 }, metrics);

        var symbolTimings = metrics.Projects.Where(p => p.Phase == IndexPhaseNames.ExtractingSymbols).ToList();
        CollectionAssert.AreEqual(Enumerable.Range(0, Projects).Select(i => $"P{i}").ToList(),
            symbolTimings.Select(t => t.Project).ToList(), "the writer records each project once, in project order");
        foreach (var timing in symbolTimings)
            Assert.IsGreaterThan(0L, timing.Rows, $"{timing.Project} rows");
        var persistedSymbols = dump[..dump.IndexOf("---", StringComparison.Ordinal)].Count(c => c == '\n');
        Assert.AreEqual(persistedSymbols, symbolTimings.Sum(t => t.Rows), "each project's rows are the symbols the writer persisted");
        var phase = metrics.Phases.Single(p => p.Name == IndexPhaseNames.ExtractingSymbols);
        Assert.AreEqual(Projects, phase.ProjectsProcessed);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(4)]
    public async Task Cancellation_DuringTheSymbolPass_TearsDownAndPublishesNothing(int projectsInFlight)
    {
        using var db = new IndexDatabase(Path.Combine(_root, $"cancel{projectsInFlight}.db"));
        db.RunMigrations();
        using var cts = new CancellationTokenSource();
        var metrics = new IndexingMetrics { Mode = "full" };
        var symbolReports = 0;
        // Cancel as the third project enters the pass, while earlier ones are being (or have been) persisted.
        var progress = new SyncProgress(p =>
        {
            if (p.Phase == IndexPhaseNames.ExtractingSymbols && p.CurrentProject != null && ++symbolReports == 3)
                cts.Cancel();
        });
        var orchestrator = new IndexOrchestrator(db, useDocumentExtractor: true,
            parallelism: new ExtractionParallelismOptions { MaxParallelism = 4, ProjectsInFlight = projectsInFlight, QueueCapacity = 1 });

        var run = orchestrator.IndexSolutionAsync(BuildSolution(), progress, metrics, cts.Token);
        if (await Task.WhenAny(run, Task.Delay(60_000)) != run)
            Assert.Fail("the cancelled symbol pass did not tear down — a stage deadlocked");
        await Assert.ThrowsAsync<OperationCanceledException>(() => run);

        Assert.AreEqual(IndexRunStatus.Cancelled, metrics.Status);
        Assert.AreEqual(IndexRunStatus.Cancelled, metrics.Phases.Single(p => p.Name == IndexPhaseNames.ExtractingSymbols).Status);
        Assert.IsNull(new IndexRunStore(db.GetConnection()).GetLastCompleteRun(), "a cancelled run publishes nothing");
    }

    private async Task<string> IndexAndDumpAsync(
        string name, ExtractionParallelismOptions parallelism, IndexingMetrics? metrics = null)
    {
        using var db = new IndexDatabase(Path.Combine(_root, $"{name}.db"));
        db.RunMigrations();
        await new IndexOrchestrator(db, useDocumentExtractor: true, parallelism: parallelism)
            .IndexSolutionAsync(BuildSolution(), metrics: metrics);
        return Dump(db.GetConnection());
    }

    // Projects of different sizes, so with several in flight the smaller later ones finish analysis first.
    private Solution BuildSolution()
    {
        var solution = new AdhocWorkspace().CurrentSolution;
        for (var i = 0; i < Projects; i++)
            solution = AddProject(solution, i, classes: 1 + (Projects - i) * 3);
        return solution;
    }

    private Solution AddProject(Solution solution, int index, int classes)
    {
        var name = $"P{index}";
        var dir = Path.Combine(_root, "src", name);
        Directory.CreateDirectory(dir);
        var projectPath = Path.Combine(dir, $"{name}.csproj");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var id = ProjectId.CreateNewId();
        solution = solution.AddProject(ProjectInfo.Create(id, VersionStamp.Default, name, name, LanguageNames.CSharp,
            filePath: projectPath,
            metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]));
        // Two files per project, one of them declaring nothing, so file seeding covers a file with no symbols.
        var source = $"namespace {name};\n" + string.Join("\n", Enumerable.Range(0, classes).Select(c =>
            $"public class Widget{index}_{c} {{ public int Size() => {c}; public string Name {{ get; set; }} = \"\"; }}"))
            + $"\npublic interface IWidget{index} {{ void Run(); }}\npublic class Widget{index} : IWidget{index} {{ public void Run() {{ }} }}\n";
        var sourcePath = Path.Combine(dir, $"{name}.cs");
        var emptyPath = Path.Combine(dir, "Empty.cs");
        if (!File.Exists(sourcePath))
        {
            File.WriteAllText(sourcePath, source);
            File.WriteAllText(emptyPath, "// nothing declared here\n");
        }
        return solution
            .AddDocument(DocumentId.CreateNewId(id), $"{name}.cs", SourceText.From(source), filePath: sourcePath)
            .AddDocument(DocumentId.CreateNewId(id), "Empty.cs", SourceText.From("// nothing declared here\n"), filePath: emptyPath);
    }

    private static string Dump(SqliteConnection conn)
    {
        var sb = new System.Text.StringBuilder();
        Append(sb, conn, """
            SELECT s.id, p.canonical_id, s.symbol_key, s.fully_qualified_name, s.kind, s.line_start, s.line_end,
                   COALESCE(f.repo_relative_path, '')
            FROM symbols s
            JOIN projects p ON p.id = s.project_id
            LEFT JOIN file_versions fv ON fv.id = s.file_version_id
            LEFT JOIN files f ON f.id = fv.file_id
            ORDER BY s.rowid
            """);
        Append(sb, conn, """
            SELECT f.id, p.canonical_id, f.repo_relative_path FROM files f JOIN projects p ON p.id = f.project_id ORDER BY f.rowid
            """);
        Append(sb, conn, "SELECT fv.id, fv.file_id, hex(fv.content_hash) FROM file_versions fv ORDER BY fv.rowid");
        return sb.ToString();
    }

    private static void Append(System.Text.StringBuilder sb, SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            for (var i = 0; i < reader.FieldCount; i++)
                sb.Append(reader.IsDBNull(i) ? "∅" : reader.GetValue(i)?.ToString()).Append('|');
            sb.Append('\n');
        }
        sb.Append("---\n");
    }

    private sealed class SyncProgress(Action<IndexingProgress> report) : IProgress<IndexingProgress>
    {
        public void Report(IndexingProgress value) => report(value);
    }
}
