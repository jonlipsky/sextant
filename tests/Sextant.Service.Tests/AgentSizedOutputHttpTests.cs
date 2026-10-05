using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sextant.Mcp;
using Sextant.Service.Host;

namespace Sextant.Service.Tests;

/// <summary>
/// The remote MCP surface's output fits an agent's context window (issue #145 and the agent-UX review): large
/// results are paged with <c>limit</c>/<c>cursor</c>/<c>meta.total</c> and lead with a summary when truncated, the
/// per-response <c>meta.snapshot</c> is lean (repository, branch, commit, coverage and a short warning), paths are
/// repository-relative in and out, <c>tools/list</c> is small, and <c>initialize</c> carries short instructions.
/// Every case goes through the service's <c>/mcp</c> over HTTP, against a catalog published from a worker checkout
/// with absolute project paths, like the live service.
/// </summary>
[TestClass]
public sealed partial class AgentSizedOutputHttpTests
{
    // Before #145 tools/list was 29159 characters for 24 tools (the #145 budget was half of that, 14579). S12 lists
    // the eight agent tools plus the service-only search_symbols: 5350 characters with a required selection, 5560
    // with delegate callers.
    private const int ToolsListBudget = 5700;
    private const int InstructionsBudget = 600;
    private const int UnboundedChars = 10_000_000;

    // The remote surface lists the agent tools only (S12); RemoteToolSurfaceGuardTests pins what their texts may name.
    private static readonly string[] ExpectedTools =
    [
        "find_references", "find_symbol", "get_call_hierarchy", "get_file_symbols", "get_implementors",
        "get_type_hierarchy", "get_type_members", "list_repositories", "search_symbols"
    ];

    // ==== tools/list and initialize ===================================================================

    [TestMethod]
    [DataRow(false, DisplayName = "query token, selection required")]
    [DataRow(true, DisplayName = "delegate callers (implicit selection)")]
    public async Task ToolsList_FitsTheBudget_AndListsExactlyTheAgentTools(bool delegateCallers)
    {
        await using var host = await AgentOutputHarness.StartAsync(delegateCallers);

        var list = await host.RpcAsync("tools/list");

        Assert.IsTrue(list.Length <= ToolsListBudget, $"tools/list is {list.Length} characters; the budget is {ToolsListBudget}.");
        using var doc = JsonDocument.Parse(list);
        var names = doc.RootElement.GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!).Order(StringComparer.Ordinal).ToList();
        CollectionAssert.AreEqual(ExpectedTools, names, "no tool is renamed, added or dropped");
    }

    [TestMethod]
    [DataRow(false, "Required: owner/repo")]
    [DataRow(true, "Required unless exactly one is granted: owner/repo")]
    public async Task ToolsList_RepositoryIsOneShortLine_AndBranchIsBare(bool delegateCallers, string expected)
    {
        await using var host = await AgentOutputHarness.StartAsync(delegateCallers);

        using var doc = JsonDocument.Parse(await host.RpcAsync("tools/list"));
        var properties = doc.RootElement.GetProperty("tools").EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "find_references")
            .GetProperty("inputSchema").GetProperty("properties");

        Assert.AreEqual(expected, properties.GetProperty("repository").GetProperty("description").GetString(),
            "a client may drop the server instructions, so the schema itself says when the repository must be named");
        Assert.AreEqual("""{"type":"string"}""", properties.GetProperty("branch").GetRawText(),
            "an optional branch explains itself; a description would repeat on every scoped tool");
        Assert.IsTrue(properties.TryGetProperty("limit", out _), "find_references takes a limit");
        Assert.IsTrue(properties.TryGetProperty("cursor", out _), "find_references takes a cursor");
    }

    [TestMethod]
    public async Task ToolsList_OptionalArgumentsAreCompact_AndAnExplicitNullStillBinds()
    {
        await using var host = await AgentOutputHarness.StartAsync();

        var first = await host.RpcAsync("tools/list");
        using var doc = JsonDocument.Parse(first);
        var limit = doc.RootElement.GetProperty("tools").EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "find_references")
            .GetProperty("inputSchema").GetProperty("properties").GetProperty("limit");
        Assert.AreEqual("integer", limit.GetProperty("type").GetString(), "an optional int? is advertised as a plain integer");
        Assert.IsFalse(limit.TryGetProperty("default", out _), "with no default:null");

        var arguments = Args(AgentOutputFixture.TargetInterface);
        arguments["limit"] = null;
        var body = await host.CallAsync("find_references", arguments);
        Assert.AreEqual(Paging.DefaultLimit, body.GetProperty("meta").GetProperty("result_count").GetInt32(),
            "an explicit null means the default, exactly like omitting the argument");
    }

    [TestMethod]
    public async Task Initialize_ReturnsShortServerInstructions()
    {
        await using var host = await AgentOutputHarness.StartAsync();

        var result = await host.RpcAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "test", ["version"] = "1" }
        });

        using var doc = JsonDocument.Parse(result);
        var instructions = doc.RootElement.GetProperty("instructions").GetString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(instructions));
        Assert.IsTrue(instructions!.Length <= InstructionsBudget, $"instructions are {instructions.Length} characters");
        StringAssert.Contains(instructions, "repository");
        StringAssert.Contains(instructions, "next_cursor");
    }

    // ==== paging ======================================================================================

    [TestMethod]
    public async Task FindReferences_LargeResult_IsOnePageWithTotalAndSummaryFirst()
    {
        await using var host = await AgentOutputHarness.StartAsync();

        var arguments = Args(AgentOutputFixture.TargetInterface);
        arguments["repository"] = AgentOutputFixture.RepoA;
        var text = await host.CallTextAsync("find_references", arguments);
        var body = JsonDocument.Parse(text).RootElement;

        var meta = body.GetProperty("meta");
        Assert.AreEqual(Paging.DefaultLimit, meta.GetProperty("result_count").GetInt32());
        Assert.AreEqual(AgentOutputFixture.ReferenceCount, meta.GetProperty("total").GetInt32());
        Assert.AreEqual(Paging.DefaultLimit, body.GetProperty("results").GetArrayLength());
        Assert.IsFalse(string.IsNullOrEmpty(meta.GetProperty("next_cursor").GetString()));

        var summary = body.GetProperty("summary");
        Assert.AreEqual(AgentOutputFixture.ReferenceCount,
            summary.GetProperty("by_project").EnumerateObject().Sum(p => p.Value.GetInt32()), "by_project covers every row");
        Assert.AreEqual(AgentOutputFixture.ReferenceCount,
            summary.GetProperty("by_file").EnumerateObject().Sum(p => p.Value.GetInt32()), "by_file covers every row");
        Assert.IsTrue(text.IndexOf("\"summary\"", StringComparison.Ordinal) < text.IndexOf("\"results\"", StringComparison.Ordinal),
            "the summary comes before the rows");
        Assert.IsTrue(text.Length < 20_000, $"the first page is {text.Length} characters");
    }

    [TestMethod]
    public async Task FindReferences_LimitIsClamped_AndASmallResultHasNoSummaryOrCursor()
    {
        // A budget no page reaches, so only the row limit cuts (ResponseBudgetHttpTests pins the size cut).
        await using var host = await AgentOutputHarness.StartAsync(maxResponseChars: UnboundedChars);

        var body = await host.CallAsync("find_references", Args(AgentOutputFixture.TargetInterface, limit: 300));
        Assert.AreEqual(Paging.MaxLimit, body.GetProperty("results").GetArrayLength(), "the limit is clamped to the maximum");
        Assert.AreEqual(Paging.MaxLimit, body.GetProperty("meta").GetProperty("result_count").GetInt32());

        var file = host.Fixture.ReferenceFiles[0];
        var single = await host.CallAsync("find_references",
            Args(AgentOutputFixture.TargetInterface, scope: $"file:{file}"));
        Assert.IsFalse(single.TryGetProperty("summary", out _), "a result that fits its page has no summary");
        Assert.IsFalse(single.GetProperty("meta").TryGetProperty("next_cursor", out _), "and no cursor");
        Assert.AreEqual(single.GetProperty("results").GetArrayLength(), single.GetProperty("meta").GetProperty("total").GetInt32());
    }

    [TestMethod]
    public async Task FindReferences_CursorWalksEveryRowExactlyOnce()
    {
        await using var host = await AgentOutputHarness.StartAsync(maxResponseChars: UnboundedChars);

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var body = await host.CallAsync("find_references", Args(AgentOutputFixture.TargetInterface, limit: 100, cursor: cursor));
            pages++;
            if (pages > 1)
                Assert.IsFalse(body.TryGetProperty("summary", out _), "only the first page leads with a summary");
            Assert.AreEqual(AgentOutputFixture.ReferenceCount, body.GetProperty("meta").GetProperty("total").GetInt32());
            seen.AddRange(body.GetProperty("results").EnumerateArray()
                .Select(r => $"{r.GetProperty("file_path").GetString()}:{r.GetProperty("line").GetInt32()}"));
            cursor = body.GetProperty("meta").TryGetProperty("next_cursor", out var next) ? next.GetString() : null;
        }
        while (cursor is not null && pages < 10);

        Assert.AreEqual(3, pages);
        Assert.AreEqual(AgentOutputFixture.ReferenceCount, seen.Count);
        Assert.AreEqual(AgentOutputFixture.ReferenceCount, seen.Distinct(StringComparer.Ordinal).Count(), "no row repeats");
    }

    [TestMethod]
    public async Task GetImplementors_WalksEveryReadableRowOnceInOrder_AndSummarizesOnlyThose()
    {
        const string tool = "get_implementors";
        await using var host = await AgentOutputHarness.StartAsync();

        var rows = new List<(string Name, string File, int Line)>();
        JsonElement summary = default;
        string? cursor = null;
        var pages = 0;
        do
        {
            var arguments = new JsonObject { ["symbol_fqn"] = AgentOutputFixture.TargetInterface, ["limit"] = 25 };
            if (cursor is not null) arguments["cursor"] = cursor;
            var body = await host.CallAsync(tool, arguments);
            pages++;
            if (pages == 1)
                summary = body.GetProperty("summary").Clone();
            Assert.AreEqual(AgentOutputFixture.HandlerCount, body.GetProperty("meta").GetProperty("total").GetInt32(),
                "the total counts only implementors this read can see");
            rows.AddRange(body.GetProperty("results").EnumerateArray().Select(r => (
                r.GetProperty("fully_qualified_name").GetString()!,
                r.GetProperty("file_path").GetString()!,
                r.GetProperty("line_start").GetInt32())));
            cursor = body.GetProperty("meta").TryGetProperty("next_cursor", out var next) ? next.GetString() : null;
        }
        while (cursor is not null && pages < 20);

        Assert.AreEqual(5, pages);
        Assert.AreEqual(AgentOutputFixture.HandlerCount, rows.Select(r => r.Name).Distinct(StringComparer.Ordinal).Count(), "no row repeats");
        Assert.IsFalse(rows.Any(r => r.Name.Contains("OtherStore", StringComparison.Ordinal)), "another repository's implementor is not listed");
        var ordered = rows.OrderBy(r => r.File, StringComparer.Ordinal).ThenBy(r => r.Line).ToList();
        CollectionAssert.AreEqual(ordered, rows, $"{tool} pages in its documented order");

        Assert.AreEqual(AgentOutputFixture.HandlerCount, summary.GetProperty("by_file").EnumerateObject().Sum(p => p.Value.GetInt32()));
    }

    [TestMethod]
    public async Task Cursor_TamperedOrReusedWithOtherArguments_IsInvalidCursor()
    {
        await using var host = await AgentOutputHarness.StartAsync();

        var first = await host.CallAsync("find_references", Args(AgentOutputFixture.TargetInterface));
        var cursor = first.GetProperty("meta").GetProperty("next_cursor").GetString()!;

        var tampered = await host.CallAsync("find_references",
            Args(AgentOutputFixture.TargetInterface, cursor: cursor[..^2] + (cursor[^2] == 'A' ? "B" : "A") + cursor[^1]));
        Assert.AreEqual(Paging.InvalidCursorCode, ErrorCode(tampered));

        var otherArguments = await host.CallAsync("find_references",
            Args(AgentOutputFixture.TargetInterface, cursor: cursor, scope: "project:App.Core"));
        Assert.AreEqual(Paging.InvalidCursorCode, ErrorCode(otherArguments), "a cursor is bound to the arguments that issued it");

        var otherTool = await host.CallAsync("get_implementors",
            new JsonObject { ["symbol_fqn"] = AgentOutputFixture.TargetInterface, ["cursor"] = cursor });
        Assert.AreEqual(Paging.InvalidCursorCode, ErrorCode(otherTool), "and to the tool");

        var otherRepository = await host.CallAsync("find_references",
            Args(AgentOutputFixture.TargetInterface, cursor: cursor), AgentOutputFixture.RepoB);
        Assert.AreEqual(Paging.InvalidCursorCode, ErrorCode(otherRepository), "and to the snapshot");
    }

    [TestMethod]
    [DataRow("get_implementors", AgentOutputFixture.HandlerCount)]
    [DataRow("get_call_hierarchy", AgentOutputFixture.HandlerCount)]
    [DataRow("get_file_symbols", -1)]
    public async Task LargeResultTools_ArePaged(string tool, int expectedTotal)
    {
        await using var host = await AgentOutputHarness.StartAsync();

        var arguments = tool switch
        {
            "get_implementors" => new JsonObject { ["symbol_fqn"] = AgentOutputFixture.TargetInterface },
            "get_call_hierarchy" => new JsonObject { ["symbol_fqn"] = AgentOutputFixture.TargetMethod, ["direction"] = "callers", ["depth"] = 1 },
            "get_file_symbols" => new JsonObject { ["file_path"] = host.Fixture.ReferenceFiles[0] },
            _ => new JsonObject()
        };
        arguments["limit"] = 2;

        var body = await host.CallAsync(tool, arguments);

        var meta = body.GetProperty("meta");
        Assert.IsFalse(meta.TryGetProperty("error", out var error), error.ToString());
        var total = meta.GetProperty("total").GetInt32();
        if (expectedTotal >= 0)
            Assert.AreEqual(expectedTotal, total);
        Assert.IsTrue(total > 2, $"{tool} total {total}");
        Assert.IsTrue(body.GetProperty("results").GetArrayLength() <= 2);
        Assert.IsFalse(string.IsNullOrEmpty(meta.GetProperty("next_cursor").GetString()));
        if (tool != "get_file_symbols")
            Assert.IsTrue(body.TryGetProperty("summary", out _), $"{tool} leads a truncated page with a summary");

        arguments["cursor"] = meta.GetProperty("next_cursor").GetString();
        var second = await host.CallAsync(tool, arguments);
        Assert.IsFalse(second.GetProperty("meta").TryGetProperty("error", out _), $"{tool} accepts its own cursor");
        Assert.AreEqual(total, second.GetProperty("meta").GetProperty("total").GetInt32());
    }

    // ==== lean meta ===================================================================================

    [TestMethod]
    public async Task Meta_Snapshot_IsLean()
    {
        await using var host = await AgentOutputHarness.StartAsync();

        var body = await host.CallAsync("find_symbol", new JsonObject { ["name"] = "IStore" });

        var snapshot = body.GetProperty("meta").GetProperty("snapshot");
        var keys = snapshot.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
        CollectionAssert.AreEqual(new[] { "branch", "commit", "coverage", "repository" }, keys, string.Join(",", keys));
        Assert.AreEqual("github.com/org/app", snapshot.GetProperty("repository").GetString());
        Assert.AreEqual("main", snapshot.GetProperty("branch").GetString());
        Assert.AreEqual(AgentOutputFixture.CommitA[..12], snapshot.GetProperty("commit").GetString());
        Assert.AreEqual("complete", snapshot.GetProperty("coverage").GetString());
    }

    [TestMethod]
    public async Task Meta_Snapshot_WarnsOnlyWhenTheIndexIsPartial()
    {
        await using var host = await AgentOutputHarness.StartAsync();

        var body = await host.CallAsync("find_symbol", new JsonObject { ["name"] = "OtherStore" }, AgentOutputFixture.RepoB);

        var snapshot = body.GetProperty("meta").GetProperty("snapshot");
        Assert.AreEqual("partial", snapshot.GetProperty("coverage").GetString());
        Assert.AreEqual(
            "Partial index: 1 of 1 submodules were not checked out, so results may be incomplete.",
            snapshot.GetProperty("warning").GetString(), "the warning counts what is missing");
        Assert.IsFalse(snapshot.TryGetProperty("reasons", out _), "the partial reasons are not repeated on every result");
    }

    // get_index_status is local-only (S12); the full provenance it reports is read from the same catalog.
    [TestMethod]
    public async Task LocalGetIndexStatus_KeepsTheFullProvenance()
    {
        await using var host = await AgentOutputHarness.StartAsync();
        using var local = host.LocalProvider(AgentOutputFixture.RepoB);

        var body = JsonDocument.Parse(Sextant.Mcp.Tools.GetIndexStatusTool.GetIndexStatus(local)).RootElement;

        var full = body.GetProperty("index").GetProperty("snapshot");
        Assert.AreEqual(host.Fixture.SnapshotB, full.GetProperty("base_snapshot_id").GetInt64());
        Assert.AreEqual("partial", full.GetProperty("completeness").GetString());
        Assert.IsTrue(full.TryGetProperty("compatible", out _));
        Assert.AreEqual("partial", full.GetProperty("coverage").GetProperty("verdict").GetString());
        StringAssert.Contains(body.GetProperty("index").GetProperty("coverage").GetRawText(), "submodule_unpopulated");
    }

    // ==== paths out ===================================================================================

    [TestMethod]
    [DataRow("find_references")]
    [DataRow("find_references_grouped")]
    [DataRow("find_references_source")]
    [DataRow("find_symbol")]
    [DataRow("get_implementors")]
    [DataRow("get_call_hierarchy")]
    [DataRow("get_file_symbols")]
    [DataRow("get_type_hierarchy")]
    [DataRow("get_type_members")]
    public async Task Outputs_NeverExposeTheWorkerCheckout(string call)
    {
        await using var host = await AgentOutputHarness.StartAsync();

        var (tool, arguments) = call switch
        {
            "find_references" => ("find_references", Args(AgentOutputFixture.TargetInterface, limit: 200)),
            "find_references_grouped" => ("find_references", Args(AgentOutputFixture.TargetInterface, groupBy: "project,file")),
            "find_references_source" => ("find_references", Args(AgentOutputFixture.TargetInterface, includeSource: true)),
            "find_symbol" => ("find_symbol", new JsonObject { ["name"] = "IStore", ["include_source"] = true }),
            "get_implementors" => ("get_implementors", new JsonObject { ["symbol_fqn"] = AgentOutputFixture.TargetInterface }),
            "get_call_hierarchy" => ("get_call_hierarchy", new JsonObject
            {
                ["symbol_fqn"] = AgentOutputFixture.TargetMethod, ["direction"] = "callers", ["include_source"] = true
            }),
            "get_file_symbols" => ("get_file_symbols", new JsonObject { ["file_path"] = host.Fixture.ReferenceFiles[^1] }),
            "get_type_hierarchy" => ("get_type_hierarchy", new JsonObject { ["symbol_fqn"] = AgentOutputFixture.TargetInterface }),
            "get_type_members" => ("get_type_members", new JsonObject { ["symbol_fqn"] = AgentOutputFixture.TargetInterface }),
            _ => (call, new JsonObject())
        };

        arguments["repository"] = AgentOutputFixture.RepoA;
        var text = await host.CallTextAsync(tool, arguments);

        AssertNoCheckoutPath(host.Fixture, text);
        Assert.IsFalse(JsonDocument.Parse(text).RootElement.GetProperty("meta").TryGetProperty("error", out var error), error.ToString());
    }

    [TestMethod]
    public async Task Outputs_FilePathsAreRepositoryRelative_AndASubmoduleFileReadsUnderItsCheckoutPath()
    {
        await using var host = await AgentOutputHarness.StartAsync();

        var body = await host.CallAsync("find_references", Args(AgentOutputFixture.TargetInterface, limit: 200));
        var rows = body.GetProperty("results").EnumerateArray().ToList();
        var page = body;
        while (page.GetProperty("meta").TryGetProperty("next_cursor", out var next) && rows.Count < AgentOutputFixture.ReferenceCount)
        {
            page = await host.CallAsync("find_references",
                Args(AgentOutputFixture.TargetInterface, limit: 200, cursor: next.GetString()));
            rows.AddRange(page.GetProperty("results").EnumerateArray());
        }

        var paths = rows
            .Select(r => r.GetProperty("file_path").GetString()!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        CollectionAssert.AreEquivalent(host.Fixture.ReferenceFiles.ToList(), paths);
        Assert.IsTrue(paths.Any(p => p.StartsWith("external/lib/src/Lib/", StringComparison.Ordinal)),
            "a submodule file reads under its path in the checkout");
        foreach (var key in body.GetProperty("summary").GetProperty("by_file").EnumerateObject().Select(p => p.Name))
            Assert.IsTrue(key.EndsWith(" more)", StringComparison.Ordinal) || host.Fixture.ReferenceFiles.Contains(key), key);
    }

    // ==== paths in ====================================================================================

    [TestMethod]
    public async Task RelativePathInputs_AreAccepted()
    {
        await using var host = await AgentOutputHarness.StartAsync();
        var file = host.Fixture.ReferenceFiles[^1];

        var symbols = await host.CallAsync("get_file_symbols", new JsonObject { ["file_path"] = file });
        Assert.AreEqual(6, symbols.GetProperty("meta").GetProperty("total").GetInt32(), "3 handlers and their Get methods");
        Assert.IsTrue(symbols.GetProperty("results").EnumerateArray().All(r => r.GetProperty("file_path").GetString() == file));

        var inFile = await host.CallAsync("find_references", Args(AgentOutputFixture.TargetInterface, scope: $"file:{file}"));
        var fileTotal = inFile.GetProperty("meta").GetProperty("total").GetInt32();
        Assert.IsTrue(fileTotal is > 0 and < 10, $"file: narrows to one file ({fileTotal})");
        Assert.IsTrue(inFile.GetProperty("results").EnumerateArray().All(r => r.GetProperty("file_path").GetString() == file));

        var inSolution = await host.CallAsync("find_references",
            Args(AgentOutputFixture.TargetInterface, scope: $"solution:{AgentOutputFixture.SolutionRelative}", limit: 200));
        var solutionTotal = inSolution.GetProperty("meta").GetProperty("total").GetInt32();
        Assert.IsTrue(solutionTotal is > 0 and < AgentOutputFixture.ReferenceCount, $"solution: narrows to its projects ({solutionTotal})");
        Assert.IsTrue(inSolution.GetProperty("results").EnumerateArray()
            .All(r => r.GetProperty("file_path").GetString()!.StartsWith("src/App.", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task SolutionScope_AnotherRepositorysSolution_ReadsExactlyLikeAnUnknownOne()
    {
        // #145 item 2: solutions are recorded by absolute worker path in one catalog-wide table. A relative path that
        // climbs into a sibling checkout must not reach another repository's solution: it reads like a path that
        // names nothing, so the scope is no existence oracle.
        await using var host = await AgentOutputHarness.StartAsync();
        var known = $"../{AgentOutputFixture.OtherCheckoutDirectory}/{AgentOutputFixture.OtherSolutionRelative}";
        var unknown = $"../{AgentOutputFixture.OtherCheckoutDirectory}/Missing.slnx";

        var knownBody = await host.CallAsync("find_references", Args(AgentOutputFixture.TargetInterface, scope: $"solution:{known}"));
        var unknownBody = await host.CallAsync("find_references", Args(AgentOutputFixture.TargetInterface, scope: $"solution:{unknown}"));

        Assert.AreEqual(ResponseBuilder.InvalidArgumentCode, ErrorCode(knownBody), knownBody.ToString());
        Assert.AreEqual(
            unknownBody.GetProperty("meta").GetProperty("error").ToString().Replace(unknown, "<path>", StringComparison.Ordinal),
            knownBody.GetProperty("meta").GetProperty("error").ToString().Replace(known, "<path>", StringComparison.Ordinal));
        Assert.AreEqual(Shape(unknownBody), Shape(knownBody));
        AssertNoCheckoutPath(host.Fixture, knownBody.ToString());

        static string Shape(JsonElement body) => string.Join(",",
            body.EnumerateObject().Select(p => p.Name).Concat(body.GetProperty("meta").EnumerateObject().Select(p => "meta." + p.Name)));
    }

    [TestMethod]
    [DataRow("get_file_symbols")]
    [DataRow("file_scope")]
    [DataRow("solution_scope")]
    public async Task AbsolutePathInputs_AreRefusedWithoutEchoingThePath(string call)
    {
        await using var host = await AgentOutputHarness.StartAsync();
        var absoluteFile = Path.Combine(host.Fixture.CheckoutRoot, host.Fixture.ReferenceFiles[0].Replace('/', Path.DirectorySeparatorChar));
        var absoluteSolution = Path.Combine(host.Fixture.CheckoutRoot, AgentOutputFixture.SolutionRelative);

        var (tool, arguments) = call switch
        {
            "get_file_symbols" => ("get_file_symbols", new JsonObject { ["file_path"] = absoluteFile }),
            "file_scope" => ("find_references", Args(AgentOutputFixture.TargetInterface, scope: $"file:{absoluteFile}")),
            _ => ("find_references", Args(AgentOutputFixture.TargetInterface, scope: $"solution:{absoluteSolution}"))
        };

        arguments["repository"] = AgentOutputFixture.RepoA;
        var text = await host.CallTextAsync(tool, arguments);

        Assert.AreEqual(ResponseBuilder.InvalidArgumentCode, ErrorCode(JsonDocument.Parse(text).RootElement), text);
        AssertNoCheckoutPath(host.Fixture, text);
    }

    // ==== helpers =====================================================================================

    private static JsonObject Args(
        string symbol, int? limit = null, string? cursor = null, string? scope = null, string? groupBy = null,
        bool includeSource = false)
    {
        var arguments = new JsonObject { ["symbol_fqn"] = symbol };
        if (limit is not null)
            arguments["limit"] = limit;
        if (cursor is not null)
            arguments["cursor"] = cursor;
        if (scope is not null)
            arguments["scope"] = scope;
        if (groupBy is not null)
            arguments["group_by"] = groupBy;
        if (includeSource)
            arguments["include_source"] = true;
        return arguments;
    }

    private static string? ErrorCode(JsonElement body) =>
        body.GetProperty("meta").TryGetProperty("error", out var error) ? error.GetProperty("code").GetString() : null;

    // No string (value or property name) names the worker's data root or checkout, in either separator form, and
    // nothing looks like an absolute path.
    private static void AssertNoCheckoutPath(AgentOutputFixture fixture, string text)
    {
        var forbidden = new[] { fixture.DataRoot, fixture.CheckoutRoot }
            .SelectMany(p => new[] { p, p.Replace('\\', '/') })
            .Append(Path.GetFileName(fixture.CheckoutRoot))
            .ToList();
        foreach (var value in Strings(JsonNode.Parse(text)))
        {
            foreach (var bad in forbidden)
                Assert.IsFalse(value.Contains(bad, StringComparison.OrdinalIgnoreCase), $"'{value}' exposes the checkout");
            Assert.IsFalse(AbsolutePath().IsMatch(value), $"'{value}' is an absolute path");
        }
    }

    private static IEnumerable<string> Strings(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, child) in obj)
                {
                    yield return name;
                    foreach (var s in Strings(child))
                        yield return s;
                }
                break;
            case JsonArray array:
                foreach (var s in array.SelectMany(Strings))
                    yield return s;
                break;
            case JsonValue value when value.TryGetValue<string>(out var s):
                yield return s;
                break;
        }
    }

    // A leading "//" or "/*" is a code comment, not a path.
    [GeneratedRegex(@"^(/[^/*\s]|[A-Za-z]:[\\/]|\\\\)")]
    private static partial Regex AbsolutePath();
}
