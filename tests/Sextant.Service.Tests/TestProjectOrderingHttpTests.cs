using System.Text.Json;
using System.Text.Json.Nodes;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #241: find_references, get_call_hierarchy and get_implementors list production code before test
/// projects, whatever the file paths sort like. The seeded test project sits under a path that sorts FIRST
/// and holds more rows than all production code, so a path-only order would fill the first pages with tests.
/// </summary>
[TestClass]
public sealed class TestProjectOrderingHttpTests
{
    private const string EarlyDir = "aaa/Early.Tests";
    private const int EarlyFiles = 3;
    private const int RefsPerFile = 100;
    private const int EarlyHandlers = 130;

    private static void Seed(IndexDatabase db, AgentOutputFixture fixture)
    {
        var conn = db.GetConnection();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var snapshots = new SnapshotStore(conn);
        var project = AgentOutputFixture.Project(conn, snapshots, fixture.SnapshotA, fixture.CheckoutRoot,
            AgentOutputFixture.RepoA, $"{EarlyDir}/Early.Tests.csproj", $"{EarlyDir}/Early.Tests.csproj", true, now);

        var iface = Id(conn, AgentOutputFixture.TargetInterface);
        var get = Id(conn, AgentOutputFixture.TargetMethod);
        var handler = 0;
        for (var f = 0; f < EarlyFiles; f++)
        {
            var lines = new List<string>();
            for (var r = 0; r < RefsPerFile; r++)
                lines.Add($"    private readonly global::App.Core.Versioning.IStore _early{r} = default!;");
            var perFile = EarlyHandlers / EarlyFiles + (f < EarlyHandlers % EarlyFiles ? 1 : 0);
            for (var h = 0; h < perFile; h++)
                lines.Add($"public sealed class EarlyHandler{handler + h} : IStore {{ }}");
            var fv = AgentOutputFixture.SourceFile(conn, fixture.CheckoutRoot, project,
                $"{EarlyDir}/Early{f}.cs", $"{EarlyDir}/Early{f}.cs", lines, now);
            for (var r = 0; r < RefsPerFile; r++)
                AgentOutputFixture.Occurrence(conn, project, iface, null, fv, r + 1, ReferenceKind.TypeRef, now);
            for (var h = 0; h < perFile; h++)
            {
                var line = RefsPerFile + h + 1;
                var cls = AgentOutputFixture.Symbol(conn, project, $"global::App.Early.EarlyHandler{handler}", $"EarlyHandler{handler}",
                    SymbolKind.Class, fv, line, line, now);
                var run = AgentOutputFixture.Symbol(conn, project, $"global::App.Early.EarlyHandler{handler}.Get", "Get",
                    SymbolKind.Method, fv, line, line, now);
                AgentOutputFixture.Relationship(conn, cls, iface, RelationshipKind.Implements, now);
                AgentOutputFixture.Occurrence(conn, project, get, run, fv, line, ReferenceKind.Invocation, now);
                handler++;
            }
        }
    }

    private static long Id(Microsoft.Data.Sqlite.SqliteConnection conn, string fqn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM symbols WHERE fully_qualified_name = @f;";
        cmd.Parameters.AddWithValue("@f", fqn);
        return (long)cmd.ExecuteScalar()!;
    }

    private static bool IsTestPath(string path) => path.StartsWith("aaa/", StringComparison.Ordinal) || path.StartsWith("tests/", StringComparison.Ordinal);

    // Walks every page (small limit) and returns the file of each row, asserting no row repeats or is skipped.
    private static async Task<(List<(string File, int Line)> Rows, JsonElement First)> WalkAsync(
        AgentOutputHarness host, string tool, JsonObject args, string fileProperty, string lineProperty, int expected)
    {
        var rows = new List<(string, int)>();
        JsonElement first = default;
        string? cursor = null;
        var pages = 0;
        do
        {
            var call = new JsonObject();
            foreach (var kv in args) call[kv.Key] = kv.Value?.DeepClone();
            call["limit"] = 40;
            if (cursor is not null) call["cursor"] = cursor;
            var body = await host.CallAsync(tool, call);
            if (pages == 0) first = body;
            pages++;
            Assert.AreEqual(expected, body.GetProperty("meta").GetProperty("total").GetInt32(), "meta.total is stable across pages");
            rows.AddRange(body.GetProperty("results").EnumerateArray()
                .Select(r => (r.GetProperty(fileProperty).GetString()!, r.GetProperty(lineProperty).GetInt32())));
            cursor = body.GetProperty("meta").TryGetProperty("next_cursor", out var next) ? next.GetString() : null;
        }
        while (cursor is not null && pages < 100);
        Assert.AreEqual(expected, rows.Count, "the pages hold exactly meta.total rows");
        return (rows, first);
    }

    private static void AssertProductionFirst(List<(string File, int Line)> rows, int productionCount)
    {
        Assert.IsTrue(rows.Take(productionCount).All(r => !IsTestPath(r.File)), "every production row precedes every test row");
        Assert.IsTrue(rows.Skip(productionCount).All(r => IsTestPath(r.File)));
    }

    private static void AssertSummaryLeadsWithProduction(JsonElement first)
    {
        var byFile = first.GetProperty("summary").GetProperty("by_file").EnumerateObject().Select(p => p.Name).ToList();
        Assert.IsFalse(IsTestPath(byFile[0]), $"by_file leads with {byFile[0]}");
    }

    [TestMethod]
    public async Task FindReferences_ListsProductionBeforeTests_AndPagesWalkEveryRowOnce()
    {
        await using var host = await AgentOutputHarness.StartAsync(seed: Seed);

        var (rows, first) = await WalkAsync(host, "find_references",
            new JsonObject { ["symbol_fqn"] = AgentOutputFixture.TargetInterface }, "file_path", "line",
            AgentOutputFixture.ReferenceCount + EarlyFiles * RefsPerFile);

        Assert.AreEqual(rows.Count, rows.Distinct().Count(), "no row repeats");
        var productionRows = rows.Count(r => !IsTestPath(r.File));
        Assert.IsTrue(productionRows < rows.Count - productionRows, "the fixture has more test references than production");
        AssertProductionFirst(rows, productionRows);
        Assert.IsTrue(productionRows > 0);
        // Within each block the order is still file then line.
        var prod = rows.Take(productionRows).ToList();
        CollectionAssert.AreEqual(prod.OrderBy(r => r.File, StringComparer.Ordinal).ThenBy(r => r.Line).ToList(), prod);
        AssertSummaryLeadsWithProduction(first);
    }

    [TestMethod]
    public async Task GetImplementors_ListsProductionBeforeTests_AndPagesWalkEveryRowOnce()
    {
        await using var host = await AgentOutputHarness.StartAsync(seed: Seed);

        var (rows, first) = await WalkAsync(host, "get_implementors",
            new JsonObject { ["symbol_fqn"] = AgentOutputFixture.TargetInterface }, "file_path", "line_start",
            AgentOutputFixture.HandlerCount + EarlyHandlers);

        Assert.AreEqual(rows.Count, rows.Distinct().Count());
        AssertProductionFirst(rows, rows.Count(r => !IsTestPath(r.File)));
        Assert.IsTrue(rows.Count(r => !IsTestPath(r.File)) > 0);
        Assert.IsTrue(IsTestPath(rows[^1].File));
        AssertSummaryLeadsWithProduction(first);
    }

    [TestMethod]
    public async Task GetCallHierarchy_Callers_ListsProductionBeforeTests_AndPagesWalkEveryRowOnce()
    {
        await using var host = await AgentOutputHarness.StartAsync(seed: Seed);

        var (rows, first) = await WalkAsync(host, "get_call_hierarchy",
            new JsonObject { ["symbol_fqn"] = AgentOutputFixture.TargetMethod, ["direction"] = "callers", ["depth"] = 1 },
            "call_site_file", "call_site_line", AgentOutputFixture.HandlerCount + EarlyHandlers);

        Assert.AreEqual(rows.Count, rows.Distinct().Count());
        AssertProductionFirst(rows, rows.Count(r => !IsTestPath(r.File)));
        Assert.IsTrue(rows.Count(r => !IsTestPath(r.File)) > 0);
        AssertSummaryLeadsWithProduction(first);
    }
}
