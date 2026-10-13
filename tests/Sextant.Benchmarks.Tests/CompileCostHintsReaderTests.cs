using System.Text;
using Sextant.Benchmarks;

namespace Sextant.Benchmarks.Tests;

/// <summary>Issue #288: <c>--compile-cost-hints</c> reads the per-project compile timings of a previous report.</summary>
[TestClass]
public class CompileCostHintsReaderTests
{
    [TestMethod]
    public void ReadsTheSymbolPhasesCompileWindows_ByKey_AndIgnoresTheRest()
    {
        const string json = """
            {
              "corpus_name": "x",
              "full_index": {
                "projects": [
                  { "phase": "extracting_symbols", "project": "Api", "key": "src/Api/Api.csproj|net10.0",
                    "compile_requested_at_ms": 10, "compile_started_at_ms": 100, "compile_ended_at_ms": 31800 },
                  { "phase": "extracting_symbols", "project": "Redacted", "compile_started_at_ms": 1, "compile_ended_at_ms": 9000 },
                  { "phase": "extracting_occurrences", "project": "Api", "key": "src/Api/Api.csproj|net10.0",
                    "compile_started_at_ms": 0, "compile_ended_at_ms": 5 },
                  { "phase": "extracting_symbols", "project": "NoWindow", "key": "n|" },
                  { "phase": "extracting_symbols", "project": "Zero", "key": "z|", "compile_started_at_ms": 7, "compile_ended_at_ms": 7 }
                ]
              }
            }
            """;

        var hints = CompileCostHintsReader.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)));

        Assert.AreEqual(1, hints.Count);
        Assert.AreEqual(31700, hints.CostMs["src/Api/Api.csproj|net10.0"]);
    }

    [TestMethod]
    public void TheFullCompileCost_IsPreferredOverTheWindow()
    {
        const string json = """
            {"full_index":{"projects":[
              { "phase": "extracting_symbols", "key": "a|", "compile_started_at_ms": 29000, "compile_ended_at_ms": 32000, "compile_cost_ms": 31000 }
            ]}}
            """;

        var hints = CompileCostHintsReader.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)));

        Assert.AreEqual(31000, hints.CostMs["a|"]);
    }

    [TestMethod]
    public void AReportWithoutProjects_GivesNoHints()
    {
        var hints = CompileCostHintsReader.Parse(new MemoryStream(Encoding.UTF8.GetBytes("""{"full_index":{}}""")));
        Assert.AreEqual(0, hints.Count);
    }
}
