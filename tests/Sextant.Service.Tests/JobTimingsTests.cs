using Microsoft.CodeAnalysis;
using Sextant.Core;
using Sextant.Indexer;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #267: the worker's <see cref="JobTimingsRecorder"/> assembles a job's timings — its own phases, then the
/// indexer's, then the publish remainder — and the stored JSON round-trips.
/// </summary>
[TestClass]
public class JobTimingsTests
{
    [TestMethod]
    public void Build_OrdersWorkerPhases_ThenIndexerPhases_ThenIndexerOther_AndKeepsTheSlowestProjects()
    {
        var recorder = new JobTimingsRecorder();
        using (recorder.Phase("checkout")) { }
        using (recorder.Phase("restore")) Thread.Sleep(20);

        var indexing = new IndexingMetrics { Mode = "full" };
        indexing.Phases.Add(new PhaseMetric { Name = "extracting_symbols", DurationMs = 300, CpuMs = 290, ProjectsProcessed = 3, Status = IndexRunStatus.Completed });
        indexing.Phases.Add(new PhaseMetric { Name = "extracting_occurrences", DurationMs = 200, CpuMs = 900, ChildCpuMs = 5, Status = IndexRunStatus.Completed });
        for (var i = 0; i < 15; i++)
            indexing.RecordProject(new ProjectTiming { Phase = "extracting_symbols", Project = $"P{i}", WallMs = i });
        recorder.RecordIndexing(indexing, indexWallMs: 560);

        var timings = recorder.Build();

        CollectionAssert.AreEqual(
            new[] { "checkout", "restore", "extracting_symbols", "extracting_occurrences", "indexer_other" },
            timings.Phases.Select(p => p.Name).ToArray());
        Assert.IsGreaterThanOrEqualTo(20L, timings.Phases[1].WallMs);
        Assert.IsNull(timings.Phases[0].Status, "worker phases carry no indexer status");
        Assert.AreEqual(new JobPhaseTiming("extracting_occurrences", 200, 900, 5, 0, "completed"), timings.Phases[3]);
        Assert.AreEqual(60, timings.Phases[4].WallMs, "the indexer's wall time its phases do not cover");
        Assert.IsNull(timings.Phases[4].CpuMs, "not measured, so unknown rather than zero");
        Assert.HasCount(JobTimingsRecorder.Top, timings.SlowestProjects);
        Assert.AreEqual("P14", timings.SlowestProjects[0].Project);
        Assert.IsGreaterThanOrEqualTo(timings.Phases[1].WallMs, timings.TotalMs);
        Assert.IsNull(timings.Load, "no load recorded");
    }

    [TestMethod]
    public void RecordLoad_KeepsTheModeTheBuildHostsAndTheSlowestOpens()
    {
        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution
            .AddProject("A", "A", LanguageNames.CSharp).Solution
            .AddProject("B", "B", LanguageNames.CSharp).Solution;
        var opens = Enumerable.Range(0, 12)
            .Select(i => new ProjectTiming { Phase = "load", Project = $"P{i}.csproj", WallMs = i * 10, ProjectsAdded = 1 })
            .ToList();
        var load = new MultiSolutionLoadResult(solution, [], [])
        {
            LoadMode = SolutionLoadModes.Union, LoadMs = 1234, LoadTimings = opens
        };
        var recorder = new JobTimingsRecorder();

        recorder.RecordLoad(load, new ChildProcessUsage(103, 2, 512_000_000));
        var timings = recorder.Build().Load!;

        Assert.AreEqual("union", timings.Mode);
        Assert.AreEqual(1234, timings.WallMs);
        Assert.AreEqual(2, timings.ProjectsLoaded);
        Assert.AreEqual(12, timings.ProjectsOpened);
        Assert.AreEqual(new ChildProcessUsage(103, 2, 512_000_000), timings.BuildHosts);
        Assert.HasCount(JobTimingsRecorder.Top, timings.SlowestOpens);
        Assert.AreEqual("P11.csproj", timings.SlowestOpens[0].Project);
    }

    [TestMethod]
    public void Json_RoundTrips_InTheServiceWireFormat_AndUnreadableJsonIsNoTimings()
    {
        var timings = new JobTimings
        {
            TotalMs = 1_656_000,
            CpuMs = 900_000,
            ChildCpuMs = 120_000,
            Phases = [new JobPhaseTiming("load", 912_000, 60_000, 800_000), new JobPhaseTiming("extracting_symbols", 447_000, 470_000, 0, 130, "completed")],
            Load = new JobLoadTimings
            {
                Mode = "union", WallMs = 912_000, ProjectsLoaded = 187, ProjectsOpened = 127,
                BuildHosts = new ChildProcessUsage(1090, 1, 210_000_000),
                SlowestOpens = [new ProjectTiming { Phase = "load", Project = "Api.csproj", WallMs = 30_000, ProjectsAdded = 14 }]
            },
            SlowestProjects = [new ProjectTiming { Phase = "extracting_symbols", Project = "Core", WallMs = 40_000, CompileMs = 25_000, AnalyzeMs = 12_000, PersistMs = 3_000, Rows = 9_000 }]
        };

        var json = JobTimingsJson.Serialize(timings);
        StringAssert.Contains(json, "\"build_hosts\":{\"launches\":1090");
        StringAssert.Contains(json, "\"child_cpu_ms\":800000");
        var back = JobTimingsJson.Parse(json)!;

        Assert.AreEqual(timings.TotalMs, back.TotalMs);
        CollectionAssert.AreEqual(timings.Phases.ToArray(), back.Phases.ToArray());
        Assert.AreEqual(timings.Load.BuildHosts, back.Load!.BuildHosts);
        Assert.AreEqual(14, back.Load.SlowestOpens[0].ProjectsAdded);
        Assert.AreEqual(25_000, back.SlowestProjects[0].CompileMs);
        Assert.IsNull(JobTimingsJson.Parse("{not json"));
        Assert.IsNull(JobTimingsJson.Parse(null));
    }
}
