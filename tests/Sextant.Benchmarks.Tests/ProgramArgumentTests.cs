using Sextant.Benchmarks;

namespace Sextant.Benchmarks.Tests;

[TestClass]
public class ProgramArgumentTests
{
    [TestMethod]
    public void StaticGraphRestore_IsOnByDefault_LikeTheService()
    {
        var (options, _) = Program.ParseArgs([]);

        Assert.IsTrue(options.StaticGraphRestore);
        Assert.AreEqual(Sextant.Service.Restore.PackageRestoreRunner.DefaultStaticGraphRestore, options.StaticGraphRestore);
    }

    [TestMethod]
    public void NoStaticGraphRestore_TurnsItOff()
    {
        var (options, _) = Program.ParseArgs(["--service-path", "--no-static-graph-restore"]);

        Assert.IsFalse(options.StaticGraphRestore);
    }

    [TestMethod]
    public void TheReport_RecordsWhetherStaticGraphWasUsed()
    {
        var report = new LoadReport { Mode = "union", StaticGraphRestore = true, StaticGraphPasses = 2, StaticGraphFallbacks = 1 };
        var json = System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower
        });

        StringAssert.Contains(json, "\"static_graph_restore\":true");
        StringAssert.Contains(json, "\"static_graph_passes\":2");
    }
}
