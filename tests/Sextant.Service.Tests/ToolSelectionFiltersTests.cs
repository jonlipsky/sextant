using System.Text.Json;
using ModelContextProtocol.Protocol;
using Sextant.Service.Host;

namespace Sextant.Service.Tests;

/// <summary>
/// SVC-2 (SX-4): the pure parts of the per-call selection filters — argument stripping and precedence
/// (<see cref="ToolSelectionFilters.Resolve"/>), the repository short forms
/// (<see cref="ToolSelectionFilters.EvaluateRepository"/>), and the <c>tools/list</c> schema rewrite. The
/// end-to-end behavior over <c>/mcp</c> is covered by <see cref="ToolArgumentSelectionHttpTests"/>.
/// </summary>
[TestClass]
public sealed class ToolSelectionFiltersTests
{
    private const string Widgets = "https://github.com/acme/widgets";
    private static readonly IReadOnlySet<string> BothReserved = new HashSet<string> { "repository", "branch" };

    // ==== Resolve =================================================================================

    [TestMethod]
    public void Resolve_StripsReservedArguments_AndKeepsTheRest()
    {
        var arguments = Arguments(("name", "Foo"), ("repository", "acme/widgets"), ("branch", "main"));

        var outcome = Resolve(arguments);

        Assert.IsNull(outcome.Error);
        Assert.AreEqual(new ToolCallSelection(Widgets, "main", ToolSelectionSource.Argument), outcome.Selection);
        CollectionAssert.AreEquivalent(new[] { "name" }, arguments.Keys.ToList());
    }

    [TestMethod]
    public void Resolve_DoesNotStripAnArgumentTheToolDeclaresItself()
    {
        var arguments = Arguments(("own_argument", Widgets), ("repository", Widgets), ("branch", "release"));

        var outcome = ToolSelectionFilters.Resolve(
            arguments, new HashSet<string> { "repository" }, null, RepositoryUrlPolicy.Default);

        Assert.AreEqual(new ToolCallSelection(Widgets, null, ToolSelectionSource.Argument), outcome.Selection,
            "the tool's own branch argument is neither a selector nor stripped");
        CollectionAssert.AreEquivalent(new[] { "own_argument", "branch" }, arguments.Keys.ToList());
    }

    [TestMethod]
    public void Resolve_NullOrBlankSelectors_CountAsAbsent()
    {
        var arguments = new Dictionary<string, JsonElement>
        {
            ["repository"] = JsonSerializer.SerializeToElement<string?>(null),
            ["branch"] = JsonSerializer.SerializeToElement("   ")
        };

        var outcome = Resolve(arguments);

        Assert.AreEqual(new ToolCallSelection(null, null, ToolSelectionSource.None), outcome.Selection);
        Assert.AreEqual(0, arguments.Count, "absent-valued reserved arguments are still stripped");
    }

    [TestMethod]
    public void Resolve_NoArguments_IsAnEmptySelection()
    {
        var outcome = ToolSelectionFilters.Resolve(null, BothReserved, null, RepositoryUrlPolicy.Default);

        Assert.AreEqual(new ToolCallSelection(null, null, ToolSelectionSource.None), outcome.Selection);
    }

    [TestMethod]
    [DataRow("repository")]
    [DataRow("branch")]
    public void Resolve_NonStringSelector_IsInvalid(string key)
    {
        var arguments = new Dictionary<string, JsonElement> { [key] = JsonSerializer.SerializeToElement(42) };

        var outcome = Resolve(arguments);

        Assert.IsNull(outcome.Selection);
        Assert.AreEqual("invalid_selector", ErrorCode(outcome.Error));
    }

    [TestMethod]
    public void Resolve_RefusedRepository_ReportsTheReasonWithoutEchoingTheValue()
    {
        const string refused = "https://internal.example.test/acme/widgets";

        var outcome = Resolve(Arguments(("repository", refused)));

        Assert.AreEqual("invalid_selector", ErrorCode(outcome.Error));
        StringAssert.Contains(outcome.Error, RepositoryUrlRejection.HostNotAllowed);
        Assert.IsFalse(outcome.Error!.Contains("internal.example.test", StringComparison.OrdinalIgnoreCase),
            "the refused value is never echoed");
    }

    [TestMethod]
    public void Resolve_HeaderOnly_KeepsTheHeaderAsSent()
    {
        var outcome = ToolSelectionFilters.Resolve(
            Arguments(("branch", "main")), BothReserved, "https://GitHub.com/acme/widgets.git",
            RepositoryUrlPolicy.Default);

        Assert.AreEqual(
            new ToolCallSelection("https://GitHub.com/acme/widgets.git", "main", ToolSelectionSource.Header),
            outcome.Selection);
    }

    [TestMethod]
    public void Resolve_ArgumentMatchingTheHeader_Wins()
    {
        var outcome = ToolSelectionFilters.Resolve(
            Arguments(("repository", "acme/widgets")), BothReserved,
            "https://github.com/Acme/Widgets.git", RepositoryUrlPolicy.Default);

        Assert.AreEqual(new ToolCallSelection(Widgets, null, ToolSelectionSource.Argument), outcome.Selection);
    }

    [TestMethod]
    public void Resolve_ArgumentConflictingWithTheHeader_IsAConflict()
    {
        var outcome = ToolSelectionFilters.Resolve(
            Arguments(("repository", "acme/widgets")), BothReserved,
            "https://github.com/acme/gadgets", RepositoryUrlPolicy.Default);

        Assert.IsNull(outcome.Selection);
        Assert.AreEqual("selector_conflict", ErrorCode(outcome.Error));
    }

    // ==== EvaluateRepository ========================================================================

    [TestMethod]
    [DataRow("https://github.com/acme/widgets.git")]
    [DataRow("github.com/acme/widgets")]
    [DataRow("acme/widgets")]
    [DataRow("Acme/Widgets.git")]
    public void EvaluateRepository_AcceptsEveryForm_OnTheSingleHost(string selector)
    {
        var decision = ToolSelectionFilters.EvaluateRepository(selector, RepositoryUrlPolicy.Default);

        Assert.IsTrue(decision.Ok, decision.Reason);
        Assert.AreEqual(Widgets, decision.Canonical);
    }

    [TestMethod]
    public void EvaluateRepository_ShortForm_NeedsExactlyOneHost()
    {
        var multi = new RepositoryUrlPolicy(["github.com", "git.example.test"]);
        var anyHost = new RepositoryUrlPolicy(["*"]);

        Assert.AreEqual("host_required", ToolSelectionFilters.EvaluateRepository("acme/widgets", multi).Reason);
        Assert.AreEqual("host_required", ToolSelectionFilters.EvaluateRepository("acme/widgets", anyHost).Reason);
        Assert.IsTrue(ToolSelectionFilters.EvaluateRepository("git.example.test/acme/widgets", multi).Ok,
            "the host/owner/repo form still works");
        var withWildcard = ToolSelectionFilters.EvaluateRepository(
            "acme/widgets", new RepositoryUrlPolicy(["*", "git.example.test"]));
        Assert.AreEqual("https://git.example.test/acme/widgets", withWildcard.Canonical,
            "one explicit host next to '*' still expands the short form");
    }

    [TestMethod]
    [DataRow("widgets", RepositoryUrlRejection.PathNotAllowed)]
    [DataRow("github.com/acme/widgets/extra", RepositoryUrlRejection.PathNotAllowed)]
    [DataRow("http://github.com/acme/widgets", RepositoryUrlRejection.SchemeNotAllowed)]
    [DataRow("127.0.0.1/acme/widgets", RepositoryUrlRejection.HostNotAllowed)]
    [DataRow("user@github.com/acme/widgets", RepositoryUrlRejection.UrlComponentNotAllowed)]
    [DataRow("acme/../widgets", RepositoryUrlRejection.HostNotAllowed, DisplayName = "three segments: 'acme' is the host")]
    [DataRow("github.com/acme/..", RepositoryUrlRejection.PathNotAllowed)]
    public void EvaluateRepository_RefusesWhatThePolicyRefuses(string selector, string reason)
    {
        var decision = ToolSelectionFilters.EvaluateRepository(selector, RepositoryUrlPolicy.Default);

        Assert.IsFalse(decision.Ok);
        Assert.AreEqual(reason, decision.Reason);
    }

    // ==== tools/list =================================================================================

    [TestMethod]
    public async Task ListToolsFilter_AugmentsOnlyScopedTools_WithoutMutatingTheShared()
    {
        var scoped = Tool("find_symbol", """{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}""");
        var ownBranch = Tool("own_branch_tool",
            """{"type":"object","properties":{"branch":{"type":"string","description":"own"}}}""");
        var unscoped = Tool("list_everything", """{"type":"object","properties":{}}""");
        var originalSchema = scoped.InputSchema.GetRawText();
        var filter = ToolSelectionFilters.ListToolsFilter(
            RepositoryUrlPolicy.Default, new HashSet<string> { "find_symbol", "own_branch_tool" });
        var shared = new ListToolsResult { Tools = [scoped, ownBranch, unscoped] };

        var result = await filter((_, _) => ValueTask.FromResult(shared))(null!, CancellationToken.None);

        Assert.AreEqual(3, result.Tools.Count);
        Assert.AreSame(unscoped, result.Tools[2], "an unscoped tool is passed through untouched");
        Assert.AreNotSame(scoped, result.Tools[0], "a scoped tool is a copy");
        Assert.AreEqual(originalSchema, scoped.InputSchema.GetRawText(), "the shared definition is not mutated");

        var properties = result.Tools[0].InputSchema.GetProperty("properties");
        Assert.AreEqual("string", properties.GetProperty("repository").GetProperty("type").GetString());
        Assert.AreEqual("string", properties.GetProperty("branch").GetProperty("type").GetString());
        Assert.IsTrue(properties.TryGetProperty("name", out _), "the tool's own arguments are kept");
        CollectionAssert.AreEqual(
            new[] { "name" },
            result.Tools[0].InputSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList(),
            "the reserved arguments are optional");
        Assert.AreEqual(scoped.Description, result.Tools[0].Description);

        var ownProperties = result.Tools[1].InputSchema.GetProperty("properties");
        Assert.AreEqual("own", ownProperties.GetProperty("branch").GetProperty("description").GetString(),
            "a tool's own argument of the same name is kept");
        Assert.IsTrue(ownProperties.TryGetProperty("repository", out _));
    }

    [TestMethod]
    [DataRow(false, false, "owner/repo")]
    [DataRow(true, false, "Required: owner/repo")]
    [DataRow(false, true, "Required unless exactly one is granted: owner/repo")]
    [DataRow(true, true, "Required unless exactly one is granted: owner/repo")]
    public async Task ListTools_RepositoryDescription_StatesTheRequirement_InOneShortLine(
        bool selectionRequired, bool implicitSelection, string expected)
    {
        var filter = ToolSelectionFilters.ListToolsFilter(RepositoryUrlPolicy.Default, new HashSet<string> { "find_symbol" },
            selectionRequired, implicitSelection);
        var shared = new ListToolsResult { Tools = [Tool("find_symbol", """{"type":"object","properties":{}}""")] };

        var result = await filter((_, _) => ValueTask.FromResult(shared))(null!, CancellationToken.None);

        Assert.AreEqual(expected,
            result.Tools[0].InputSchema.GetProperty("properties").GetProperty("repository").GetProperty("description").GetString());
    }

    [TestMethod]
    public async Task SchemaCompaction_DropsOnlyNullUnionsAndNullDefaults_WithoutMutatingTheShared()
    {
        var nullable = Tool("find_references",
            """{"type":"object","properties":{"limit":{"type":["integer","null"],"default":null},"flag":{"type":"boolean","default":false},"either":{"type":["string","integer"]}}}""");
        var plain = Tool("plain_tool", """{"type":"object","properties":{"name":{"type":"string"}}}""");
        var originalSchema = nullable.InputSchema.GetRawText();
        var shared = new ListToolsResult { Tools = [nullable, plain] };

        var result = await ToolSchemaCompaction.ListToolsFilter()((_, _) => ValueTask.FromResult(shared))(null!, CancellationToken.None);

        Assert.AreEqual(originalSchema, nullable.InputSchema.GetRawText(), "the shared definition is not mutated");
        Assert.AreNotSame(nullable, result.Tools[0], "a compacted tool is a copy");
        Assert.AreSame(plain, result.Tools[1], "a tool with nothing to compact is passed through");
        var properties = result.Tools[0].InputSchema.GetProperty("properties");
        Assert.AreEqual("""{"type":"integer"}""", properties.GetProperty("limit").GetRawText());
        Assert.AreEqual("""{"type":"boolean","default":false}""", properties.GetProperty("flag").GetRawText(), "a real default is kept");
        Assert.AreEqual("""{"type":["string","integer"]}""", properties.GetProperty("either").GetRawText(), "a non-null union is kept");
        Assert.AreEqual(nullable.Description, result.Tools[0].Description);
    }

    [TestMethod]
    public void RepositoryScopedTools_CoverEveryRemoteTool()
    {
        var names = ToolSelectionFilters.RepositoryScopedToolNames(ServiceApp.RemoteQueryTools);

        CollectionAssert.AreEquivalent(names.ToList(), ServiceApp.RepositoryScopedTools.ToList());
        CollectionAssert.AreEquivalent(
            RemoteToolSurfaceGuardTests.AgentTools.Where(n => n is not ("list_repositories" or "search_symbols")).ToList(),
            names.ToList(), "every remote tool but list_repositories and search_symbols reads one selected repository");
    }

    // ==== helpers ===================================================================================

    private static ToolSelectionFilters.SelectionOutcome Resolve(IDictionary<string, JsonElement> arguments) =>
        ToolSelectionFilters.Resolve(arguments, BothReserved, null, RepositoryUrlPolicy.Default);

    private static Dictionary<string, JsonElement> Arguments(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value));

    private static string? ErrorCode(string? body) =>
        body is null ? null : JsonDocument.Parse(body).RootElement.GetProperty("meta").GetProperty("error").GetProperty("code").GetString();

    private static Tool Tool(string name, string schema) => new()
    {
        Name = name,
        Description = $"{name} description",
        InputSchema = JsonDocument.Parse(schema).RootElement.Clone()
    };
}
