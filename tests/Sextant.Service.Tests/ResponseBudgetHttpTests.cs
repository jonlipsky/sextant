using System.Text.Json;
using System.Text.Json.Nodes;
using Sextant.Mcp;

namespace Sextant.Service.Tests;

/// <summary>
/// Every tool result fits a character budget (<see cref="ResponseBudget"/>, <c>SEXTANT_SERVICE_MAX_RESPONSE_CHARS</c>),
/// so an MCP client that caps tool output (Claude Code's <c>MAX_MCP_OUTPUT_TOKENS</c>) never refuses it. A paged tool
/// ends a page at the last row that fits and returns <c>meta.next_cursor</c> exactly as at <c>limit</c>, with
/// <c>meta.page_truncated_by: "size"</c>; an unpaged tool keeps the leading rows that fit and says so. Every case goes
/// through the service's <c>/mcp</c> over HTTP, where the budget is measured on the text the client receives.
/// </summary>
[TestClass]
public sealed class ResponseBudgetHttpTests
{
    private const int SmallBudget = 4_000;

    // The call an agent made against the live service: Claude Code refused its 57,953-character result.
    [TestMethod]
    public async Task FindReferences_TheRefusedCall_FitsTheDefaultBudget_AndPointsAtTheNextPage()
    {
        await using var host = await AgentOutputHarness.StartAsync();

        var text = await host.CallTextAsync("find_references", new JsonObject
        {
            ["symbol_fqn"] = "IStore", ["repository"] = AgentOutputFixture.RepoA, ["limit"] = 200, ["group_by"] = "project"
        });

        Assert.IsTrue(text.Length <= ResponseBudget.DefaultMaxChars, $"the page is {text.Length} characters");
        var body = JsonDocument.Parse(text).RootElement;
        var meta = body.GetProperty("meta");
        Assert.AreEqual(ResponseBudget.SizeTruncation, meta.GetProperty("page_truncated_by").GetString());
        Assert.AreEqual(AgentOutputFixture.ReferenceCount, meta.GetProperty("total").GetInt32(), "the total stays exact");
        var rows = GroupedRows(body).Count;
        Assert.AreEqual(rows, meta.GetProperty("result_count").GetInt32());
        Assert.IsTrue(rows is > 0 and < 200, $"{rows} rows");
        Assert.IsFalse(string.IsNullOrEmpty(meta.GetProperty("next_cursor").GetString()));
        StringAssert.Contains(body.GetProperty("message").GetString(), "next_cursor");
        Assert.IsTrue(body.TryGetProperty("summary", out _), "a first page cut by size still leads with the summary");
    }

    [TestMethod]
    [DataRow(null, DisplayName = "flat")]
    [DataRow("project", DisplayName = "grouped by project")]
    [DataRow("project,file", DisplayName = "grouped by project and file")]
    public async Task FindReferences_SizeCutPages_WalkEveryRowExactlyOnce_InOrder(string? groupBy)
    {
        await using var host = await AgentOutputHarness.StartAsync(maxResponseChars: SmallBudget);

        var seen = new List<(string File, int Line)>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var arguments = new JsonObject { ["symbol_fqn"] = AgentOutputFixture.TargetInterface, ["limit"] = 200 };
            if (groupBy is not null) arguments["group_by"] = groupBy;
            if (cursor is not null) arguments["cursor"] = cursor;
            arguments["repository"] = AgentOutputFixture.RepoA;
            var text = await host.CallTextAsync("find_references", arguments);
            pages++;

            Assert.IsTrue(text.Length <= SmallBudget, $"page {pages} is {text.Length} characters");
            var body = JsonDocument.Parse(text).RootElement;
            var meta = body.GetProperty("meta");
            Assert.AreEqual(AgentOutputFixture.ReferenceCount, meta.GetProperty("total").GetInt32());
            var rows = groupBy is null
                ? body.GetProperty("results").EnumerateArray().ToList()
                : GroupedRows(body);
            Assert.AreEqual(rows.Count, meta.GetProperty("result_count").GetInt32());
            Assert.IsTrue(rows.Count >= 1, "a page holds at least one row");
            seen.AddRange(rows.Select(r => (r.GetProperty("file_path").GetString()!, r.GetProperty("line").GetInt32())));

            cursor = meta.TryGetProperty("next_cursor", out var next) ? next.GetString() : null;
            if (cursor is not null)
                Assert.AreEqual(ResponseBudget.SizeTruncation, meta.GetProperty("page_truncated_by").GetString(),
                    "every page but the last ended at the budget, not at limit");
        }
        while (cursor is not null && pages < 500);

        Assert.IsTrue(pages > 2, $"{pages} pages");
        Assert.AreEqual(AgentOutputFixture.ReferenceCount, seen.Count, "the pages hold exactly meta.total rows");
        Assert.AreEqual(AgentOutputFixture.ReferenceCount, seen.Distinct().Count(), "no row repeats");
        if (groupBy is null)
        {
            // find_references orders by file then line, so a page that resumed anywhere but the next row breaks this.
            var ordered = seen.OrderBy(r => r.File, StringComparer.Ordinal).ThenBy(r => r.Line).ToList();
            CollectionAssert.AreEqual(ordered, seen, "each page resumes exactly at the row after the previous one");
        }
    }

    [TestMethod]
    public async Task SizeCutCursor_IsBoundToItsToolArgumentsAndSnapshot()
    {
        await using var host = await AgentOutputHarness.StartAsync(maxResponseChars: SmallBudget);

        var first = await host.CallAsync("find_references", new JsonObject
        {
            ["symbol_fqn"] = AgentOutputFixture.TargetInterface, ["limit"] = 200
        });
        var meta = first.GetProperty("meta");
        Assert.AreEqual(ResponseBudget.SizeTruncation, meta.GetProperty("page_truncated_by").GetString());
        var cursor = meta.GetProperty("next_cursor").GetString()!;
        var kept = first.GetProperty("meta").GetProperty("result_count").GetInt32();

        var otherLimit = await host.CallAsync("find_references", new JsonObject
        {
            ["symbol_fqn"] = AgentOutputFixture.TargetInterface, ["limit"] = 200, ["group_by"] = "file", ["cursor"] = cursor
        });
        Assert.AreEqual(Paging.InvalidCursorCode, ErrorCode(otherLimit), "a size-cut cursor is bound to the arguments");

        var otherTool = await host.CallAsync("find_comments", new JsonObject { ["cursor"] = cursor });
        Assert.AreEqual(Paging.InvalidCursorCode, ErrorCode(otherTool), "and to the tool");

        var otherSnapshot = await host.CallAsync("find_references", new JsonObject
        {
            ["symbol_fqn"] = AgentOutputFixture.TargetInterface, ["limit"] = 200, ["cursor"] = cursor
        }, AgentOutputFixture.RepoB);
        Assert.AreEqual(Paging.InvalidCursorCode, ErrorCode(otherSnapshot), "and to the served snapshot");

        // A smaller limit is a different query: a cursor never carries over to it.
        var smaller = await host.CallAsync("find_references", new JsonObject
        {
            ["symbol_fqn"] = AgentOutputFixture.TargetInterface, ["limit"] = kept, ["cursor"] = cursor
        });
        Assert.IsNull(ErrorCode(smaller), "limit is not part of the binding, so the next page may use another limit");
    }

    [TestMethod]
    [DataRow("find_symbol")]
    [DataRow("semantic_search")]
    [DataRow("get_namespace_tree")]
    public async Task UnpagedTools_AreCutToTheBudget_WithTheTotalAndAHint(string tool)
    {
        const int budget = 2_000;
        await using var host = await AgentOutputHarness.StartAsync(maxResponseChars: budget);

        var arguments = tool switch
        {
            "find_symbol" => new JsonObject { ["name"] = "Handler*", ["fuzzy"] = true },
            "semantic_search" => new JsonObject { ["query"] = "Handler*" },
            _ => new JsonObject { ["namespace_prefix"] = "App.Feature" }
        };
        arguments["repository"] = AgentOutputFixture.RepoA;
        var text = await host.CallTextAsync(tool, arguments);

        Assert.IsTrue(text.Length <= budget, $"{tool} returned {text.Length} characters");
        var body = JsonDocument.Parse(text).RootElement;
        var meta = body.GetProperty("meta");
        Assert.IsNull(ErrorCode(body), text);
        Assert.IsTrue(meta.TryGetProperty("page_truncated_by", out var cut), text);
        Assert.AreEqual(ResponseBudget.SizeTruncation, cut.GetString(), text);
        var kept = meta.GetProperty("result_count").GetInt32();
        Assert.IsTrue(kept >= 1 && kept < meta.GetProperty("total").GetInt32(), text);
        Assert.IsFalse(meta.TryGetProperty("next_cursor", out _), "an unpaged tool has no cursor");
        StringAssert.Contains(body.GetProperty("message").GetString(), "Narrow the query");
        if (tool == "get_namespace_tree")
            Assert.AreEqual(kept, body.GetProperty("results")[0].GetProperty("symbols").GetArrayLength(),
                "the namespace row keeps only the entries that fit");
    }

    [TestMethod]
    public async Task UnpagedTool_ThatFits_IsUnchanged()
    {
        await using var host = await AgentOutputHarness.StartAsync();

        var body = await host.CallAsync("get_namespace_tree", new JsonObject { ["namespace_prefix"] = "App.Feature" });

        var meta = body.GetProperty("meta");
        Assert.IsFalse(meta.TryGetProperty("page_truncated_by", out _));
        Assert.IsFalse(meta.TryGetProperty("total", out _), "a result that fits keeps its pre-budget shape");
        Assert.AreEqual(AgentOutputFixture.HandlerCount, body.GetProperty("results")[0].GetProperty("symbols").GetArrayLength());
    }

    // ==== get_index_status ============================================================================

    [TestMethod]
    public async Task GetIndexStatus_SummarizesTheIndex_AndPagesItsProjects()
    {
        await using var host = await AgentOutputHarness.StartAsync();

        var paths = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var arguments = new JsonObject { ["limit"] = 1 };
            if (cursor is not null) arguments["cursor"] = cursor;
            var body = await host.CallAsync("get_index_status", arguments);
            pages++;

            var index = body.GetProperty("index");
            var totals = index.GetProperty("totals");
            Assert.AreEqual(4, totals.GetProperty("projects").GetInt32(), "the totals cover every project on every page");
            Assert.AreEqual(1, totals.GetProperty("test_projects").GetInt32());
            Assert.AreEqual(AgentOutputFixture.ReferenceCount, totals.GetProperty("references").GetInt64());
            Assert.AreEqual(2 + 2 * AgentOutputFixture.HandlerCount, totals.GetProperty("symbols").GetInt64());
            Assert.IsTrue(index.TryGetProperty("snapshot", out _), "every page keeps the full provenance");
            Assert.AreEqual(4, body.GetProperty("meta").GetProperty("total").GetInt32());

            paths.AddRange(body.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("repo_relative_path").GetString()!));
            cursor = body.GetProperty("meta").TryGetProperty("next_cursor", out var next) ? next.GetString() : null;
        }
        while (cursor is not null && pages < 10);

        Assert.AreEqual(4, pages);
        Assert.AreEqual(4, paths.Distinct(StringComparer.Ordinal).Count(), "each project is listed once");
        CollectionAssert.AreEqual(paths.Order(StringComparer.Ordinal).ToList(), paths, "projects page in path order");

        var stale = await host.CallAsync("get_index_status", new JsonObject { ["cursor"] = "not-a-cursor" });
        Assert.AreEqual(Paging.InvalidCursorCode, ErrorCode(stale));
    }

    [TestMethod]
    public async Task GetIndexStatus_FitsTheBudget()
    {
        const int budget = 1_500;
        await using var host = await AgentOutputHarness.StartAsync(maxResponseChars: budget);

        var text = await host.CallTextAsync("get_index_status", new JsonObject { ["repository"] = AgentOutputFixture.RepoA });

        Assert.IsTrue(text.Length <= budget, $"get_index_status is {text.Length} characters");
        var body = JsonDocument.Parse(text).RootElement;
        var meta = body.GetProperty("meta");
        Assert.AreEqual(ResponseBudget.SizeTruncation, meta.GetProperty("page_truncated_by").GetString());
        Assert.IsFalse(string.IsNullOrEmpty(meta.GetProperty("next_cursor").GetString()));
        Assert.IsTrue(body.GetProperty("index").TryGetProperty("totals", out _));
    }

    // ==== helpers =====================================================================================

    // The reference rows of a grouped result, depth first in response order.
    private static List<JsonElement> GroupedRows(JsonElement body)
    {
        var rows = new List<JsonElement>();
        void Walk(JsonElement items)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("group_key", out _))
                    Walk(item.GetProperty("items"));
                else
                    rows.Add(item);
            }
        }
        Walk(body.GetProperty("results"));
        return rows;
    }

    private static string? ErrorCode(JsonElement body) =>
        body.GetProperty("meta").TryGetProperty("error", out var error) ? error.GetProperty("code").GetString() : null;
}
