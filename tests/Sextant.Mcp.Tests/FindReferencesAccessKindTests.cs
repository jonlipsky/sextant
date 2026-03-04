using System.Text.Json;
using Sextant.Mcp.Tools;

namespace Sextant.Mcp.Tests;

[TestClass]
public class FindReferencesAccessKindTests
{
    private static readonly McpTestFixture _fixture = McpTestFixtureInstance.Instance;

    [TestMethod]
    public void FindReferences_AccessKindShowsInOutput()
    {
        var result = FindReferencesTool.FindReferences(_fixture.DbProvider,
            "global::Alpha.BaseService.Process(string)");
        var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");

        var hasAccessKind = false;
        foreach (var r in results.EnumerateArray())
        {
            if (r.TryGetProperty("access_kind", out var ak) &&
                ak.ValueKind != JsonValueKind.Null)
            {
                hasAccessKind = true;
                var val = ak.GetString();
                Assert.IsTrue(new[] { "read", "write", "readwrite" }.Contains(val));
            }
        }
        Assert.IsTrue(hasAccessKind, "At least one reference should have access_kind set");
    }

    [TestMethod]
    public void FindReferences_AccessKindReadFilter_OnlyReturnsReads()
    {
        var result = FindReferencesTool.FindReferences(_fixture.DbProvider,
            "global::Alpha.BaseService.Process(string)", access_kind: "read");
        var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");
        Assert.AreEqual(2, results.GetArrayLength());

        foreach (var r in results.EnumerateArray())
        {
            var ak = r.GetProperty("access_kind").GetString();
            Assert.AreEqual("read", ak);
        }
    }

    [TestMethod]
    public void FindReferences_AccessKindNull_ReturnsAllReferences()
    {
        // Init() has a reference with null access_kind + Process has 3 with access_kind set
        // When access_kind filter is null, all references should be returned (backward compatible)
        var result = FindReferencesTool.FindReferences(_fixture.DbProvider,
            "global::Alpha.BaseService.Init()");
        var doc = JsonDocument.Parse(result);
        var results = doc.RootElement.GetProperty("results");
        Assert.IsTrue(results.GetArrayLength() >= 1);
    }
}
