using System.Text.Json.Nodes;

namespace Sextant.Mcp.Tests;

[TestClass]
public class PathPresenterTests
{
    // A summary keyed by file path counts files outside every checkout too (a NuGet package's source, a linked
    // file); the remote surface shows those keys the way it shows the same path as a value: by file name only.
    [TestMethod]
    public void Redact_PathShapedSummaryKeys_KeepNoServerPath_AndMergeTheirCounts()
    {
        var paths = new PathPresenter(db: null, isRemote: true);
        var response = new JsonObject
        {
            ["summary"] = new JsonObject
            {
                ["by_file"] = new JsonObject
                {
                    ["/home/worker/.nuget/packages/lib/1.0.0/src/Shared.cs"] = 3,
                    [@"D:\worker\linked\Shared.cs"] = 2,
                    ["src/App/Handler.cs"] = 1
                }
            },
            ["results"] = new JsonArray(new JsonObject { ["file_path"] = "/home/worker/.nuget/packages/lib/1.0.0/src/Shared.cs" })
        };

        var text = paths.Redact(response)!.ToJsonString();

        var byFile = response["summary"]!["by_file"]!.AsObject();
        Assert.AreEqual(5, (int)byFile["Shared.cs"]!, text);
        Assert.AreEqual(1, (int)byFile["src/App/Handler.cs"]!, text);
        Assert.AreEqual(2, byFile.Count, text);
        Assert.AreEqual("Shared.cs", (string?)response["results"]![0]!["file_path"], text);
        StringAssert.DoesNotMatch(text, new System.Text.RegularExpressions.Regex("worker"), text);
    }

    // find_references grouped by file carries the path as group_key: a file outside every checkout is shown by
    // name there too, while a project or kind group key is left as it is.
    [TestMethod]
    public void Redact_FileGroupKeyOutsideEveryCheckout_KeepsNoServerPath()
    {
        var paths = new PathPresenter(db: null, isRemote: true);
        var response = new JsonObject
        {
            ["results"] = new JsonArray(
                new JsonObject
                {
                    ["group_key"] = "/home/worker/.nuget/packages/lib/1.0.0/src/Shared.cs", ["group_type"] = "file",
                    ["items"] = new JsonArray(new JsonObject
                    {
                        ["group_key"] = "App.Core:1", ["group_type"] = "project", ["items"] = new JsonArray()
                    })
                },
                new JsonObject { ["group_key"] = @"D:\worker\linked\Linked.cs", ["group_type"] = "file" })
        };

        var text = paths.Redact(response)!.ToJsonString();

        var groups = response["results"]!.AsArray();
        Assert.AreEqual("Shared.cs", (string?)groups[0]!["group_key"], text);
        Assert.AreEqual("App.Core:1", (string?)groups[0]!["items"]![0]!["group_key"], text);
        Assert.AreEqual("Linked.cs", (string?)groups[1]!["group_key"], text);
        StringAssert.DoesNotMatch(text, new System.Text.RegularExpressions.Regex("worker"), text);
    }
}
