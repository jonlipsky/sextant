using Sextant.Core;

namespace Sextant.Benchmarks.Tests;

[TestClass]
public sealed class BenchmarkReportTests
{
    [TestMethod]
    public void DuplicateReferenceRatioIsComputedFromRows()
    {
        var rows = new RowCountMetrics
        {
            References = 100,
            DistinctReferenceOccurrences = 25,
            DuplicateReferenceRows = 75
        };

        Assert.AreEqual(0.75, rows.DuplicateReferenceRatio, 1e-9);
    }

    [TestMethod]
    public void DuplicateReferenceRatioIsZeroWithNoReferences()
    {
        var rows = new RowCountMetrics { References = 0 };
        Assert.AreEqual(0.0, rows.DuplicateReferenceRatio, 1e-9);
    }

    [TestMethod]
    public void JsonDistinguishesFinalFromPeakStorageInSnakeCase()
    {
        var report = BuildReport();
        report.FullIndex!.Storage = new StorageMetrics
        {
            FinalDbBytes = 100,
            FinalWalBytes = 50,
            PeakDbPlusWalBytes = 300
        };

        var json = report.ToJson();

        StringAssert.Contains(json, "\"final_db_bytes\": 100");
        StringAssert.Contains(json, "\"final_wal_bytes\": 50");
        StringAssert.Contains(json, "\"peak_db_plus_wal_bytes\": 300");
        // Enum values serialize snake_case.
        StringAssert.Contains(json, "\"status\": \"completed\"");
        StringAssert.Contains(json, "\"mode\": \"full\"");
    }

    [TestMethod]
    public void RedactorStripsIdentifyingDataButKeepsCounts()
    {
        var report = BuildReport();
        report.Environment.MachineDescription = "secret-host";
        report.Environment.GitCommit = "deadbeef";
        report.FullIndex!.WorkspaceDiagnostics.Add("C:/private/repo/Secret.cs error");
        report.FullIndex.WorkspaceDiagnostics.Add("another private path");
        report.FullIndex.WorkspaceDiagnosticCount = 2;
        report.FullIndex.FailureReason = "C:/private/path failed";

        var redacted = Redactor.Apply(report);

        Assert.IsTrue(redacted.Redacted);
        Assert.AreEqual("external", redacted.CorpusName);
        Assert.IsNull(redacted.Environment.MachineDescription);
        Assert.IsNull(redacted.Environment.GitCommit);
        Assert.AreEqual(0, redacted.FullIndex!.WorkspaceDiagnostics.Count);
        Assert.AreEqual(2, redacted.FullIndex.WorkspaceDiagnosticCount, "diagnostic count must survive redaction");
        Assert.AreEqual("redacted", redacted.FullIndex.FailureReason);

        var json = redacted.ToJson();
        StringAssert.Contains(json, "\"redacted\": true");
        Assert.IsFalse(json.Contains("secret-host"));
        Assert.IsFalse(json.Contains("deadbeef"));
        Assert.IsFalse(json.Contains("private"));
    }

    // === Phase 8, criterion 6: per-profile benchmark output ========================================

    [TestMethod]
    public void IndexingProfile_IsRecordedInJsonAndMarkdown()
    {
        var report = BuildReport();
        report.IndexingProfile = "core";

        StringAssert.Contains(report.ToJson(), "\"indexing_profile\": \"core\"");
        StringAssert.Contains(report.ToMarkdown(), "- Profile: core");
    }

    [TestMethod]
    public void ToProfileComparison_RendersOneRowPerProfile()
    {
        var reports = new[]
        {
            ReportForProfile("core"),
            ReportForProfile("standard"),
            ReportForProfile("deep"),
        };

        var table = BenchmarkReport.ToProfileComparison(reports);

        StringAssert.Contains(table, "profile comparison (correctness)");
        StringAssert.Contains(table, "| Profile |");
        foreach (var profile in new[] { "core", "standard", "deep" })
            StringAssert.Contains(table, $"| {profile} ");
    }

    private static BenchmarkReport ReportForProfile(string profile)
    {
        var report = BuildReport();
        report.IndexingProfile = profile;
        report.FullIndex!.Rows = new RowCountMetrics { Symbols = 10, References = 20, Comments = 5 };
        report.FullIndex.Storage = new StorageMetrics { FinalDbBytes = 1000, PeakDbPlusWalBytes = 2000 };
        return report;
    }

    [TestMethod]
    public void ReportCarriesPhaseCpu_ProjectTimings_AndTheServicePathLoad_AndRedactionRelabelsProjects()
    {
        // Issue #267.
        var report = BuildReport();
        report.FullIndex!.Phases.Add(new PhaseMetric
        {
            Name = "extracting_symbols", DurationMs = 400, CpuMs = 380, ChildCpuMs = 0, Status = IndexRunStatus.Completed
        });
        report.FullIndex.RecordProject(new ProjectTiming
        {
            Phase = "extracting_symbols", Project = "Acme.Secret.Core", WallMs = 400, CompileMs = 250, AnalyzeMs = 120, PersistMs = 30, Rows = 99
        });
        report.Load = new LoadReport
        {
            Mode = "union", SolutionsSelected = 4, RestoreMs = 80_000, WallMs = 912_000, ProjectsLoaded = 187,
            ProjectsOpened = 127, BuildHosts = new ChildProcessUsage(1090, 1, 210_000_000),
            SlowestOpens = [new ProjectTiming { Phase = "load", Project = "Acme.Secret.Api.csproj", WallMs = 30_000, ProjectsAdded = 14 }]
        };

        var json = report.ToJson();
        StringAssert.Contains(json, "\"cpu_ms\": 380");
        StringAssert.Contains(json, "\"compile_ms\": 250");
        StringAssert.Contains(json, "\"launches\": 1090");
        StringAssert.Contains(json, "\"projects_added\": 14");
        var markdown = report.ToMarkdown();
        StringAssert.Contains(markdown, "Service-path load");
        StringAssert.Contains(markdown, "load mode **union**");
        StringAssert.Contains(markdown, "| extracting_symbols | 400 ms | 380 ms | 1.0 |");
        StringAssert.Contains(markdown, "| Acme.Secret.Core | extracting_symbols |");

        var redacted = Redactor.Apply(report).ToJson();
        Assert.IsFalse(redacted.Contains("Acme.Secret", StringComparison.Ordinal), "project names identify the repository");
        StringAssert.Contains(redacted, "\"project\": \"project-1\"");
        StringAssert.Contains(redacted, "\"compile_ms\": 250", "the timings themselves survive redaction");
    }

    private static BenchmarkReport BuildReport() => new()
    {
        CorpusName = "correctness",
        SchemaVersion = BenchmarkReport.CurrentSchemaVersion,
        Environment = new BenchmarkEnvironment
        {
            TimestampUtc = "2026-09-19T00:00:00.0000000Z",
            OsDescription = "Test OS",
            RuntimeVersion = ".NET Test",
            Architecture = "X64",
            ProcessorCount = 8
        },
        FullIndex = new IndexingMetrics { Mode = "full", Status = IndexRunStatus.Completed }
    };
}
