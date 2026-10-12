using Sextant.Core;
using Sextant.Indexer;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>Issue #288: the compile-cost hints come from the previous completed job of the same repository.</summary>
[TestClass]
public class CompileCostHintLoaderTests
{
    private const string Repo = "https://example.test/a.git";
    private string _dbPath = null!;

    [TestInitialize]
    public void TestInitialize() =>
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_test_{Guid.NewGuid():N}.db");

    [TestCleanup]
    public void TestCleanup() => SqliteTestDatabase.Delete(_dbPath);

    private static string Timings(params (string Key, long Ms)[] costs) => JobTimingsJson.Serialize(new JobTimings
    {
        CompileCosts = costs.Select(c => new ProjectCompileCost(c.Key, c.Ms)).ToList()
    });

    private static void Record(SnapshotJobStore jobs, string identity, string url, string branch, string status, string json)
    {
        var (job, _) = jobs.EnsureJob(identity, url, identity, branch);
        jobs.MarkResult(job.Id, status, null, null);
        jobs.ReplaceTimings(job.Id, json);
        Thread.Sleep(5);
    }

    [TestMethod]
    public void UsesTheLatestCompletedJobOfTheSameRepository_PreferringTheSameBranch()
    {
        using var db = new IndexDatabase(_dbPath);
        db.RunMigrations();
        var jobs = new SnapshotJobStore(db.GetConnection());
        Record(jobs, "i1", Repo, "main", SnapshotJobStatus.Complete, Timings(("old/Old.csproj|net10.0", 3000)));
        Record(jobs, "i2", Repo, "main", SnapshotJobStatus.Complete, Timings(("Api/Api.csproj|net10.0", 31700)));
        Record(jobs, "i3", Repo, "dev", SnapshotJobStatus.Complete, Timings(("Dev/Dev.csproj|net10.0", 9000)));
        Record(jobs, "i4", "https://example.test/other.git", "main", SnapshotJobStatus.Complete, Timings(("X/X.csproj|", 8000)));
        Record(jobs, "i5", Repo, "main", SnapshotJobStatus.Failed, Timings(("F/F.csproj|", 8000)));
        Record(jobs, "i6", Repo, "main", SnapshotJobStatus.Complete, Timings());

        var sameBranch = CompileCostHintLoader.Load(jobs, Repo, "main")!;
        CollectionAssert.AreEqual(new[] { "Api/Api.csproj|net10.0" }, sameBranch.CostMs.Keys.ToArray(),
            "the latest completed main job that has costs; not another repository's, not a failed job's, not one without costs");
        Assert.AreEqual(31700, sameBranch.CostMs["Api/Api.csproj|net10.0"]);

        var otherBranch = CompileCostHintLoader.Load(jobs, Repo, "feature")!;
        CollectionAssert.AreEqual(new[] { "Dev/Dev.csproj|net10.0" }, otherBranch.CostMs.Keys.ToArray(),
            "no job on the branch: the latest of any branch");
    }

    [TestMethod]
    public void NoHistory_GivesNoHints()
    {
        using var db = new IndexDatabase(_dbPath);
        db.RunMigrations();
        Assert.IsNull(CompileCostHintLoader.Load(new SnapshotJobStore(db.GetConnection()), Repo, "main"));
    }

    [TestMethod]
    public void MalformedEntries_AreSkipped_AndOldJsonWithoutTheField_GivesNoHints()
    {
        using var db = new IndexDatabase(_dbPath);
        db.RunMigrations();
        var jobs = new SnapshotJobStore(db.GetConnection());
        Record(jobs, "i0", Repo, "main", SnapshotJobStatus.Complete, "{\"phases\":[]}");
        Assert.IsNull(CompileCostHintLoader.Load(jobs, Repo, "main"), "a document from before the field existed");

        Record(jobs, "i1", Repo, "main", SnapshotJobStatus.Complete,
            "{\"compile_costs\":[null,{\"key\":null,\"ms\":5000},{\"key\":\"B|\",\"ms\":1.5},{\"key\":\"C|\",\"ms\":\"x\"},"
            + "{\"key\":\"A/A.csproj|net10.0\",\"ms\":4000},7]}");
        var hints = CompileCostHintLoader.Load(jobs, Repo, "main")!;
        CollectionAssert.AreEqual(new[] { "A/A.csproj|net10.0" }, hints.CostMs.Keys.ToArray());
        Assert.AreEqual(4000, hints.CostMs["A/A.csproj|net10.0"]);

        Record(jobs, "i2", Repo, "main", SnapshotJobStatus.Complete, "{\"compile_costs\":[null,{\"key\":null}]}");
        CollectionAssert.AreEqual(new[] { "A/A.csproj|net10.0" }, CompileCostHintLoader.Load(jobs, Repo, "main")!.CostMs.Keys.ToArray(),
            "a later job whose entries are all unusable records no costs, so the earlier one is used");
    }

    [TestMethod]
    public void AFailureReadingTheHistory_GivesNoHints_AndLogsOnce()
    {
        var jobs = new SnapshotJobStore(new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:"));
        var log = new List<string>();

        Assert.IsNull(CompileCostHintLoader.Load(jobs, Repo, "main", log.Add));
        Assert.HasCount(1, log);
    }

    [TestMethod]
    public void CompileCostsRecorded_AreTheSymbolPhasesLongCompiles_ByKey()
    {
        var costs = JobTimingsRecorder.CompileCostsOf(
        [
            Timing(IndexPhaseNames.ExtractingSymbols, "A|net10.0", 100, 3100),
            Timing(IndexPhaseNames.ExtractingSymbols, "B|net10.0", 0, 500),
            Timing(IndexPhaseNames.ExtractingOccurrences, "C|net10.0", 0, 9000),
            Timing(IndexPhaseNames.ExtractingSymbols, null, 0, 9000)
        ]);

        CollectionAssert.AreEqual(new[] { new ProjectCompileCost("A|net10.0", 3000) }, costs.ToArray());
    }

    [TestMethod]
    public void ANullBranch_HintsAndTheJobShareTheEffectiveBranch()
    {
        var request = ServiceTestFixtures.Request(branch: "main") with { BranchName = null };

        Assert.AreEqual("main", LocalIndexerSnapshotWorker.EffectiveBranchName(request));
        Assert.AreEqual(LocalIndexerSnapshotWorker.EffectiveBranchName(request),
            LocalIndexerSnapshotWorker.CreateSnapshotContext(request, capability: null).BranchName);
    }

    [TestMethod]
    public void CompileCostsRecorded_UseTheFullCompileCost_WhenPresent()
    {
        var costs = JobTimingsRecorder.CompileCostsOf(
        [
            new ProjectTiming
            {
                Phase = IndexPhaseNames.ExtractingSymbols, Project = "p", Key = "A|net10.0",
                CompileStartedAtMs = 29_000, CompileEndedAtMs = 32_000, CompileCostMs = 31_000
            }
        ]);

        CollectionAssert.AreEqual(new[] { new ProjectCompileCost("A|net10.0", 31_000) }, costs.ToArray());
    }

    private static ProjectTiming Timing(string phase, string? key, long started, long ended) => new()
    {
        Phase = phase, Project = "p", Key = key, CompileStartedAtMs = started, CompileEndedAtMs = ended
    };
}
