using System.Text.Json;
using Sextant.Mcp;
using static Sextant.Service.Tests.CallerAssertionHttpTests;

namespace Sextant.Service.Tests;

/// <summary>
/// The response size budget over a repository whose call sites did not bind (the <see cref="UnboundCallSitesHttpTests"/>
/// fixture, indexed by the real orchestrator): a page cut by size keeps every row's <c>candidate</c> marker, the walk
/// returns every row exactly once.
/// </summary>
[TestClass]
public class CandidateSizeBudgetHttpTests
{
    private const string Store = "https://github.com/acme/store";
    private const string Reader = "user-store";
    private const int CallerMethods = 20;
    private const int Budget = 2_500;

    private static Harness _host = null!;
    private static string _root = "";

    [ClassInitialize]
    public static async Task StartAsync(TestContext _)
    {
        _root = Path.Combine(Path.GetTempPath(), "sextant-candidate-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        _host = await Harness.StartAsync(
            configure: o => o with { RequireRepositorySelection = true, MaxResponseChars = Budget },
            seed: db => UnboundCallSitesHttpTests.IndexStore(db, _root));
        using var response = await _host.ControlAsync(HttpMethod.Put, "/control/grants/self", ControlToken,
            _host.UserAssertion(sub: Reader), JsonSerializer.Serialize(new { repository = Store }));
        Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [ClassCleanup]
    public static async Task StopAsync()
    {
        await _host.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [TestMethod]
    [DataRow("find_references", "\"symbol_fqn\":\"Core.IStore.EnsureAsync\",\"limit\":200", DisplayName = "find_references")]
    [DataRow("get_call_hierarchy", "\"symbol_fqn\":\"Core.IStore.EnsureAsync\",\"direction\":\"callers\",\"depth\":1,\"limit\":200", DisplayName = "get_call_hierarchy callers")]
    public async Task SizeCutPages_KeepTheCandidateMarker_AndTheWalkReturnsEveryRowOnce(string tool, string arguments)
    {
        var pages = await WalkAsync(tool, arguments);

        Assert.IsTrue(pages.Count > 1, "the budget cuts the result into several pages");
        Assert.IsTrue(pages.SkipLast(1).All(p => IsSizeCut(p)), "every page but the last ends at the size budget");
        var rows = pages.SelectMany(p => p.GetProperty("results").EnumerateArray()).ToList();
        Assert.AreEqual(CallerMethods, rows.Count);
        Assert.AreEqual(CallerMethods, pages[0].GetProperty("meta").GetProperty("total").GetInt32());
        Assert.AreEqual(CallerMethods, rows.Select(r => r.GetRawText()).Distinct().Count(), "no row is returned twice");
        Assert.IsTrue(rows.All(r => r.GetProperty("candidate").GetBoolean()), "every unbound site stays a candidate");
    }

    private static async Task<List<JsonElement>> WalkAsync(string tool, string arguments)
    {
        var pages = new List<JsonElement>();
        string? cursor = null;
        do
        {
            var args = "{" + arguments + (cursor is null ? "" : $",\"cursor\":\"{cursor}\"") + "}";
            var call = await _host.CallAsync(tool, args, DelegateToken, _host.UserAssertion(sub: Reader));
            Assert.IsFalse(call.IsError, call.Body.ToString());
            var text = call.Body.GetRawText();
            var rows = call.Body.GetProperty("results").GetArrayLength();
            Assert.IsTrue(text.Length <= Budget || rows == 1, $"a {rows}-row page is {text.Length} characters");
            pages.Add(call.Body);
            cursor = call.Body.GetProperty("meta").TryGetProperty("next_cursor", out var next) && next.ValueKind == JsonValueKind.String
                ? next.GetString()
                : null;
            Assert.IsTrue(pages.Count < 100, "the walk ends");
        }
        while (cursor is not null);
        return pages;
    }

    private static bool IsSizeCut(JsonElement page) =>
        page.GetProperty("meta").TryGetProperty("page_truncated_by", out var by) && by.GetString() == ResponseBudget.SizeTruncation;
}