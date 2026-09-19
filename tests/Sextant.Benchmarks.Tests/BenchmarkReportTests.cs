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
