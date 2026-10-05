using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Indexer.Tests;

/// <summary>
/// Issue #245 through the real orchestrator and store: when the service worker's time budget runs out, the
/// projects it cannot cover are left out and the snapshot is still PUBLISHED, with the gap recorded as partial
/// coverage, instead of the whole evaluation being aborted with nothing. Time is a hand-driven clock advanced
/// from the orchestrator's own progress lines, so each deadline passes at an exact, repeatable point.
/// Four independent in-memory projects (no MSBuild): solution S1 declares P1 and P2, S2 declares P3 and P4.
/// </summary>
[TestClass]
public class TimeBudgetIndexingTests
{
    private const int Projects = 4;

    private string _root = null!;
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private ManualClock _clock = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sextant_budget_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        _dbPath = Path.Combine(_root, "index.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _clock = new ManualClock();
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteTestDatabase.Delete(_dbPath, _db);
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [TestMethod]
    public async Task SymbolDeadline_LeavesLaterProjectsRegisteredButEmpty_AndPublishesPartial()
    {
        // Extraction may run 100 s; symbols get 40% of that (40 s). Each project's symbols take 15 s, so P1, P2
        // and P3 start before +40 and P4 (at +45) is left out.
        var log = await Index(Budget(extractionSeconds: 100), advanceOn: Lines(("  P1...", 15), ("  P2...", 15), ("  P3...", 15)));

        var coverage = RecordedCoverage();
        Assert.AreEqual(SnapshotCoverageVerdict.Partial, coverage.Verdict);
        StringAssert.StartsWith(coverage.Reasons[0], "The indexing time budget (30 min) ran out");
        var gap = coverage.TimeBudget!;
        Assert.AreEqual(0, gap.ProjectsNotLoaded);
        Assert.AreEqual(1, gap.ProjectsNotIndexed);
        Assert.AreEqual(0, gap.ProjectsNotFullyExtracted);
        CollectionAssert.AreEqual(new[] { "S2.slnx" }, gap.UnfinishedSolutions.ToArray(), "S1 was fully indexed");
        Assert.AreEqual(1800L, gap.BudgetSeconds);

        for (var i = 1; i <= 3; i++)
            AssertIndexed(i, symbols: true, occurrences: true, comments: true);
        Assert.IsTrue(IsMapped(4), "the left-out project is still registered in the published snapshot");
        AssertIndexed(4, symbols: false, occurrences: false, comments: false);
        Assert.IsTrue(log.Any(l => l.Contains("symbol phase deadline passed after 3 project(s)")), string.Join("\n", log));
    }

    [TestMethod]
    public async Task ExtractionDeadline_StopsTheRemainingProjectsComments()
    {
        // Symbols may use the whole extraction window; the deadline passes once P2's comments are done.
        await Index(Budget(extractionSeconds: 100, symbolShare: 1.0), advanceOn: Lines(("  P2: comments extracted", 200)));

        var gap = RecordedCoverage().TimeBudget!;
        Assert.AreEqual(0, gap.ProjectsNotIndexed);
        Assert.AreEqual(2, gap.ProjectsNotFullyExtracted, "P3 and P4 got no comments");
        CollectionAssert.AreEqual(new[] { "S2.slnx" }, gap.UnfinishedSolutions.ToArray());

        AssertIndexed(1, symbols: true, occurrences: true, comments: true);
        AssertIndexed(2, symbols: true, occurrences: true, comments: true);
        AssertIndexed(3, symbols: true, occurrences: true, comments: false);
        AssertIndexed(4, symbols: true, occurrences: true, comments: false);
    }

    [TestMethod]
    public async Task ExtractionDeadlinePassedDuringSymbols_LeavesEveryProjectWithoutOccurrences()
    {
        // P4 is admitted to the symbol phase at +0, then its symbols take 200 s: past the extraction deadline.
        await Index(Budget(extractionSeconds: 100, symbolShare: 1.0), advanceOn: Lines(("  P4...", 200)));

        var gap = RecordedCoverage().TimeBudget!;
        Assert.AreEqual(0, gap.ProjectsNotIndexed);
        Assert.AreEqual(Projects, gap.ProjectsNotFullyExtracted);
        CollectionAssert.AreEqual(new[] { "S1.slnx", "S2.slnx" }, gap.UnfinishedSolutions.ToArray());
        for (var i = 1; i <= Projects; i++)
            AssertIndexed(i, symbols: true, occurrences: false, comments: false);
    }

    [TestMethod]
    public async Task LegacyExtractor_HonoursTheExtractionDeadlineToo()
    {
        await Index(Budget(extractionSeconds: 100, symbolShare: 1.0), advanceOn: Lines(("  P4...", 200)),
            documentExtractor: false);

        Assert.AreEqual(Projects, RecordedCoverage().TimeBudget!.ProjectsNotFullyExtracted);
        for (var i = 1; i <= Projects; i++)
            AssertIndexed(i, symbols: true, occurrences: false, comments: false);
    }

    [TestMethod]
    public async Task BudgetNotReached_IsComplete_AndRecordsNoGap()
    {
        await Index(Budget(extractionSeconds: 100), advanceOn: Lines());

        var coverage = RecordedCoverage();
        Assert.AreEqual(SnapshotCoverageVerdict.Complete, coverage.Verdict);
        Assert.AreEqual(0, coverage.Reasons.Count);
        Assert.IsNull(coverage.TimeBudget, "nothing was left out, so the row keeps its pre-#245 shape");
        for (var i = 1; i <= Projects; i++)
            AssertIndexed(i, symbols: true, occurrences: true, comments: true);
    }

    [TestMethod]
    public async Task NoBudget_IndexesEverything_HoweverLongItTakes()
    {
        await Index(budget: null, advanceOn: Lines(("  P1...", 100_000)));

        Assert.AreEqual(SnapshotCoverageVerdict.Complete, RecordedCoverage().Verdict);
        for (var i = 1; i <= Projects; i++)
            AssertIndexed(i, symbols: true, occurrences: true, comments: true);
    }

    [TestMethod]
    public async Task ProjectsTheLoaderDidNotOpen_MakeTheirSolutionUnfinished()
    {
        var notLoaded = Path.Combine(_root, "src", "P5", "P5.csproj");
        var budget = Budget(extractionSeconds: 100) with { NotLoaded = [notLoaded] };
        var membership = Membership().Append(new SolutionMembership(Path.Combine(_root, "S3.slnx"), [notLoaded])).ToList();

        await Index(budget, advanceOn: Lines(), membership: membership);

        var coverage = RecordedCoverage();
        Assert.AreEqual(SnapshotCoverageVerdict.Partial, coverage.Verdict);
        Assert.AreEqual(1, coverage.TimeBudget!.ProjectsNotLoaded);
        Assert.AreEqual(0, coverage.TimeBudget.ProjectsNotIndexed);
        CollectionAssert.AreEqual(new[] { "S3.slnx" }, coverage.TimeBudget.UnfinishedSolutions.ToArray());
    }

    [TestMethod]
    public async Task SingleSolutionWorkspace_NamesItsOwnSolution()
    {
        await Index(Budget(extractionSeconds: 100), advanceOn: Lines(("  P1...", 50)), membership: null,
            solutionFile: Path.Combine(_root, "build", "All.sln"));

        CollectionAssert.AreEqual(new[] { "build/All.sln" }, RecordedCoverage().TimeBudget!.UnfinishedSolutions.ToArray());
    }

    [TestMethod]
    public async Task ProviderProjects_AreNeverLeftOut()
    {
        // The symbol deadline passes on the first (parent) project, but the provider project after it is still
        // indexed: a provider snapshot is shared, so an empty provider project would be reused empty.
        var orchestrator = new IndexOrchestrator(_db, log: _ => { }, useDocumentExtractor: true)
        {
            SubmoduleDiscoverer = _ => Task.FromResult(new List<SubmoduleInfo>
            {
                new() { Path = "libs/mix", CommitSha = new string('8', 40), RemoteUrl = "https://github.com/org/Mix" }
            })
        };
        var budget = Budget(extractionSeconds: 100) with { ExtractionDeadline = _clock.GetUtcNow() };
        var workspace = new AdhocWorkspace();
        var solution = AddProject(workspace.CurrentSolution, "src/App", "App", Source("App"));
        solution = AddProject(solution, "libs/mix/Mix", "Mix", Source("Mix"));
        solution = AddProject(solution, "src/Late", "Late", Source("Late"));

        await orchestrator.IndexSolutionAsync(solution, snapshotContext: Context(budget));

        Assert.IsTrue(SymbolCount("Mix.csproj") > 0, "the provider project is indexed past the deadline");
        Assert.IsTrue(SymbolCount("App.csproj") > 0, "the first project is always indexed");
        Assert.AreEqual(0, SymbolCount("Late.csproj"), "a parent project after the deadline is left out");
        Assert.AreEqual(1, RecordedCoverage().TimeBudget!.ProjectsNotIndexed);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private IndexTimeBudget Budget(int extractionSeconds, double symbolShare = IndexTimeBudget.DefaultSymbolShare) => new()
    {
        Clock = _clock,
        Budget = TimeSpan.FromMinutes(30),
        ExtractionDeadline = _clock.GetUtcNow() + TimeSpan.FromSeconds(extractionSeconds),
        SymbolShare = symbolShare,
        CheckoutRoot = _root
    };

    private static Dictionary<string, int> Lines(params (string Line, int Seconds)[] advances) =>
        advances.ToDictionary(a => a.Line, a => a.Seconds, StringComparer.Ordinal);

    private List<SolutionMembership> Membership() =>
    [
        new(Path.Combine(_root, "S1.slnx"), [ProjectPath(1), ProjectPath(2)]),
        new(Path.Combine(_root, "S2.slnx"), [ProjectPath(3), ProjectPath(4)])
    ];

    private SnapshotContext Context(IndexTimeBudget? budget) => new()
    {
        RepositoryRemoteUrl = "https://github.com/org/budget",
        CommitSha = "c1",
        BranchName = "main",
        Coverage = new SnapshotCoverage { Verdict = SnapshotCoverageVerdict.Complete, SolutionsSelected = 2 },
        TimeBudget = budget
    };

    // Runs a full index, advancing the clock when the orchestrator logs one of advanceOn's lines; returns the log.
    private async Task<List<string>> Index(
        IndexTimeBudget? budget, Dictionary<string, int> advanceOn, bool documentExtractor = true,
        IReadOnlyList<SolutionMembership>? membership = null, string? solutionFile = null)
    {
        var log = new List<string>();
        var orchestrator = new IndexOrchestrator(_db, line =>
        {
            lock (log)
                log.Add(line);
            if (advanceOn.TryGetValue(line, out var seconds))
                _clock.Advance(TimeSpan.FromSeconds(seconds));
        }, documentExtractor);
        membership ??= solutionFile is null ? Membership() : null;
        await orchestrator.IndexSolutionAsync(
            BuildSolution(solutionFile), snapshotContext: Context(budget), solutionMembership: membership);
        return log;
    }

    private string ProjectPath(int i) => Path.Combine(_root, "src", $"P{i}", $"P{i}.csproj");

    private static string Source(string name) => $$"""
        namespace {{name}}
        {
            // TODO: tidy {{name}}
            public class Widget
            {
                public int Size() => Helper() + 1;
                private int Helper() => 1;
            }
        }
        """;

    private Solution BuildSolution(string? solutionFile)
    {
        var workspace = new AdhocWorkspace();
        if (solutionFile is not null)
            workspace.AddSolution(SolutionInfo.Create(SolutionId.CreateNewId(), VersionStamp.Default, solutionFile));
        var solution = workspace.CurrentSolution;
        for (var i = 1; i <= Projects; i++)
            solution = AddProject(solution, $"src/P{i}", $"P{i}", Source($"P{i}"));
        return solution;
    }

    private Solution AddProject(Solution solution, string relativeDir, string name, string source)
    {
        var dir = Path.Combine(_root, relativeDir.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(dir);
        var projectPath = Path.Combine(dir, $"{name}.csproj");
        var sourcePath = Path.Combine(dir, $"{name}.cs");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(sourcePath, source);
        var id = ProjectId.CreateNewId();
        return solution
            .AddProject(ProjectInfo.Create(id, VersionStamp.Default, name, name, LanguageNames.CSharp,
                filePath: projectPath,
                metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]))
            .AddDocument(DocumentId.CreateNewId(id), $"{name}.cs", SourceText.From(source), filePath: sourcePath);
    }

    private SnapshotCoverage RecordedCoverage()
    {
        var conn = _db.GetConnection();
        var selected = new SnapshotStore(conn).GetSelectedSnapshotId();
        Assert.IsNotNull(selected, "the snapshot is published");
        return new SnapshotCoverageStore(conn).Get(selected.Value)!;
    }

    private void AssertIndexed(int i, bool symbols, bool occurrences, bool comments)
    {
        var project = $"P{i}.csproj";
        Assert.AreEqual(symbols, SymbolCount(project) > 0, $"P{i} symbols");
        Assert.AreEqual(occurrences, Count(project, "SELECT COUNT(*) FROM occurrences o JOIN projects p ON p.id = o.in_project_id") > 0,
            $"P{i} occurrences");
        Assert.AreEqual(comments, Count(project, "SELECT COUNT(*) FROM comments c JOIN projects p ON p.id = c.project_id") > 0,
            $"P{i} comments");
    }

    private long SymbolCount(string project) =>
        Count(project, "SELECT COUNT(*) FROM symbols s JOIN projects p ON p.id = s.project_id");

    private bool IsMapped(int i)
    {
        var selected = new SnapshotStore(_db.GetConnection()).GetSelectedSnapshotId()!.Value;
        return Count($"P{i}.csproj",
            $"SELECT COUNT(*) FROM snapshot_projects sp JOIN projects p ON p.id = sp.project_id AND sp.snapshot_id = {selected}") == 1;
    }

    private long Count(string projectFile, string selectFromJoin)
    {
        using var cmd = _db.GetConnection().CreateCommand();
        cmd.CommandText = selectFromJoin + " WHERE p.repo_relative_path LIKE @p;";
        cmd.Parameters.AddWithValue("@p", "%" + projectFile);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
}
