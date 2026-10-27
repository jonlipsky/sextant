using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Sextant.Indexer;
using Sextant.Store;
using static Sextant.Service.Tests.CallerAssertionHttpTests;

namespace Sextant.Service.Tests;

/// <summary>
/// Issues #223 and #224 at the remote MCP boundary, on a repository whose one project is multi-targeted (net8.0 and
/// net10.0 versions of the same source, members differing by an <c>#if</c>): an explicit interface implementation is
/// addressable by <c>Ns.C.IDisposable.Dispose</c> and <c>Ns.C.System.IDisposable.Dispose</c>, an indexer by
/// <c>Type.Item(int)</c>, and a symbol tool's <c>project_id</c> reaches each project version's own answer.
/// </summary>
[TestClass]
public class ExplicitImplementationAndTargetFrameworkHttpTests
{
    private const string Repo = "https://github.com/acme/multitfm";
    private const string Solo = "user-solo";

    private static Harness _host = null!;
    private static string _root = "";
    private static string _net8 = "";
    private static string _net10 = "";

    [ClassInitialize]
    public static async Task StartAsync(TestContext _)
    {
        _root = Path.Combine(Path.GetTempPath(), "sextant-multitfm-" + Guid.NewGuid().ToString("N"));
        _host = await Harness.StartAsync(
            configure: o => o with { RequireRepositorySelection = true },
            seed: db => Index(db, _root));
        using var response = await _host.ControlAsync(HttpMethod.Put, "/control/grants/self", ControlToken,
            _host.UserAssertion(sub: Solo), JsonSerializer.Serialize(new { repository = Repo }));
        Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());

        var found = await CallAsync("find_symbol", """{"name":"Ns.C","kind":"class"}""");
        var ids = found.Body.GetProperty("meta").GetProperty("candidates").EnumerateArray()
            .Select(r => r.GetProperty("project_id").GetString()!).ToList();
        Assert.AreEqual(2, ids.Count, found.Body.ToString());
        foreach (var id in ids)
        {
            var members = await CallAsync("get_type_members", $$"""{"symbol_fqn":"Ns.C","project_id":"{{id}}"}""");
            if (members.Body.ToString().Contains("OnlyNet8", StringComparison.Ordinal))
                _net8 = id;
            else
                _net10 = id;
        }
        Assert.AreNotEqual("", _net8);
        Assert.AreNotEqual("", _net10);
    }

    [ClassCleanup]
    public static async Task StopAsync()
    {
        await _host.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ==== #223 ========================================================================================

    [TestMethod]
    [DataRow("Ns.C.IDisposable.Dispose")]
    [DataRow("Ns.C.System.IDisposable.Dispose")]
    [DataRow("C.IDisposable.Dispose")]
    public async Task AnExplicitImplementation_ResolvesBy_TypeInterfaceAndName(string name)
    {
        var call = await CallAsync("find_references", $$"""{"symbol_fqn":"{{name}}","project_id":"{{_net8}}"}""");

        Assert.IsFalse(call.IsError, call.Body.ToString());
        StringAssert.Contains(call.Body.ToString(), "Ns.C.System#IDisposable#Dispose");
    }

    [TestMethod]
    [DataRow("Ns.Cmp.IComparable.CompareTo")]
    [DataRow("Ns.Cmp.IComparable<string>.CompareTo")]
    [DataRow("Cmp.System.IComparable<string>.CompareTo")]
    public async Task AnExplicitImplementation_OfAConstructedGenericInterface_Resolves(string name)
    {
        var call = await CallAsync("find_references", JsonSerializer.Serialize(new { symbol_fqn = name, project_id = _net8 }));

        Assert.IsFalse(call.IsError, $"'{name}': {call.Body}");
        StringAssert.Contains(call.Body.ToString(), "IComparable{System#String}#CompareTo");
    }

    [TestMethod]
    public async Task AnExplicitImplementation_ThatTheTypeDoesNotImplement_IsNotFound()
    {
        var call = await CallAsync("find_references", """{"symbol_fqn":"Ns.C.IComparable.Dispose"}""");

        Assert.IsTrue(call.IsError, call.Body.ToString());
        Assert.AreEqual("symbol_not_found", call.Body.GetProperty("meta").GetProperty("error").GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task GetTypeMembers_PrintsTheExplicitImplementation_InAFormFindReferencesAccepts()
    {
        var members = await CallAsync("get_type_members", $$"""{"symbol_fqn":"Ns.C","project_id":"{{_net8}}"}""");
        Assert.IsFalse(members.IsError, members.Body.ToString());
        var printed = members.Body.GetProperty("results").EnumerateArray()
            .Select(r => r.GetProperty("fully_qualified_name").GetString()!)
            .Single(n => n.Contains("Dispose", StringComparison.Ordinal));

        var references = await CallAsync("find_references",
            JsonSerializer.Serialize(new { symbol_fqn = printed, project_id = _net8 }));

        Assert.IsFalse(references.IsError, $"'{printed}': {references.Body}");
    }

    [TestMethod]
    [DataRow("Ns.C.Item(int)")]
    [DataRow("Ns.C.this[int]")]
    [DataRow("Ns.C.Item[int]")]
    public async Task AnIndexer_IsAddressableBy_ItsItemSpelling(string name)
    {
        var call = await CallAsync("find_references", $$"""{"symbol_fqn":"{{name}}","project_id":"{{_net8}}"}""");

        Assert.IsFalse(call.IsError, call.Body.ToString());
        StringAssert.Contains(call.Body.ToString(), "P:Ns.C.Item(System.Int32)");
    }

    // ==== #224 ========================================================================================

    [TestMethod]
    public async Task GetTypeMembers_ProjectId_ReachesEachVersionsMembers()
    {
        var net8 = await CallAsync("get_type_members", $$"""{"symbol_fqn":"Ns.C","project_id":"{{_net8}}"}""");
        var net10 = await CallAsync("get_type_members", $$"""{"symbol_fqn":"Ns.C","project_id":"{{_net10}}"}""");

        Assert.IsFalse(net8.IsError, net8.Body.ToString());
        Assert.IsFalse(net10.IsError, net10.Body.ToString());
        StringAssert.Contains(net8.Body.ToString(), "OnlyNet8");
        Assert.IsFalse(net8.Body.ToString().Contains("OnlyNet10", StringComparison.Ordinal));
        StringAssert.Contains(net10.Body.ToString(), "OnlyNet10");
        Assert.IsFalse(net10.Body.ToString().Contains("OnlyNet8", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task GetTypeMembers_WithoutProjectId_AnswersForOneVersion_AndListsTheOthersInMeta()
    {
        var call = await CallAsync("get_type_members", """{"symbol_fqn":"Ns.C"}""");

        Assert.IsFalse(call.IsError, call.Body.ToString());
        var text = call.Body.ToString();
        Assert.AreNotEqual(text.Contains("OnlyNet8", StringComparison.Ordinal),
            text.Contains("OnlyNet10", StringComparison.Ordinal), "exactly one version answers");
    }

    [TestMethod]
    public async Task GetCallHierarchy_ProjectId_ReachesEachVersionsCallEdges()
    {
        var net8 = await CallAsync("get_call_hierarchy",
            $$"""{"symbol_fqn":"Ns.C.Run","direction":"callees","project_id":"{{_net8}}"}""");
        var net10 = await CallAsync("get_call_hierarchy",
            $$"""{"symbol_fqn":"Ns.C.Run","direction":"callees","project_id":"{{_net10}}"}""");

        Assert.IsFalse(net8.IsError, net8.Body.ToString());
        Assert.IsFalse(net10.IsError, net10.Body.ToString());
        StringAssert.Contains(net8.Body.ToString(), "Helper8");
        Assert.IsFalse(net10.Body.ToString().Contains("Helper8", StringComparison.Ordinal));
        StringAssert.Contains(net10.Body.ToString(), "Always");
    }

    [TestMethod]
    public async Task GetTypeHierarchy_AndImplementors_AcceptProjectId()
    {
        var hierarchy = await CallAsync("get_type_hierarchy", $$"""{"symbol_fqn":"Ns.C","project_id":"{{_net10}}"}""");
        var implementors = await CallAsync("get_implementors", $$"""{"symbol_fqn":"Ns.IThing","project_id":"{{_net8}}"}""");

        Assert.IsFalse(hierarchy.IsError, hierarchy.Body.ToString());
        Assert.IsFalse(implementors.IsError, implementors.Body.ToString());
        StringAssert.Contains(implementors.Body.ToString(), "Ns.C");
    }

    [TestMethod]
    public async Task AnUnknownProjectId_IsAnInvalidArgumentToolError()
    {
        var call = await CallAsync("get_type_members", """{"symbol_fqn":"Ns.C","project_id":"no/such/project"}""");

        Assert.IsTrue(call.IsError, call.Body.ToString());
        Assert.AreEqual("invalid_argument", call.Body.GetProperty("meta").GetProperty("error").GetProperty("code").GetString());
        StringAssert.Contains(call.Body.ToString(), "Unknown project_id");
    }

    [TestMethod]
    public async Task AProjectIdThatDoesNotDeclareTheSymbol_IsSymbolNotFound_NeverAnotherVersion()
    {
        var call = await CallAsync("get_type_members",
            $$"""{"symbol_fqn":"Ns.OnlyIn10","project_id":"{{_net8}}"}""");

        Assert.IsTrue(call.IsError, call.Body.ToString());
        Assert.AreEqual("symbol_not_found", call.Body.GetProperty("meta").GetProperty("error").GetProperty("code").GetString());
    }

    // ==== helpers ====================================================================================

    private static Task<ToolCall> CallAsync(string tool, string arguments) =>
        _host.CallAsync(tool, arguments[..^1] + ",\"repository\":\"acme/multitfm\"}", DelegateToken,
            _host.UserAssertion(sub: Solo));

    private static void Index(IndexDatabase db, string root)
    {
        var request = new EnsureSnapshotRequest
        {
            RepositoryRemoteUrl = Repo, CommitSha = "commit-t1", BranchName = "main", IsDefaultBranch = true
        };
        var context = LocalIndexerSnapshotWorker.CreateSnapshotContext(request, capability: null);
        new IndexOrchestrator(db, useDocumentExtractor: true)
            .IndexSolutionAsync(Solution(root), snapshotContext: context).GetAwaiter().GetResult();
    }

    private const string Source = """
        namespace Ns
        {
            public interface IThing { }

            public class Cmp : System.IComparable<string>
            {
                int System.IComparable<string>.CompareTo(string other) => 0;
            }

            public class C : System.IDisposable, IThing
            {
                void System.IDisposable.Dispose() { }
                public int Always() => 1;
                public int this[int i] => i;
        #if NET8
                int Helper8() => 8;
                public int OnlyNet8() => Helper8();
                public void Run() { Helper8(); }
        #else
                public int OnlyNet10() => 10;
                public void Run() { Always(); }
        #endif
            }
        #if !NET8
            public class OnlyIn10 { }
        #endif
        }
        """;

    private static Solution Solution(string root)
    {
        var projectDir = Path.Combine(root, "src", "Lib");
        Directory.CreateDirectory(projectDir);
        var projectPath = Path.Combine(projectDir, "Lib.csproj");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var sourcePath = Path.Combine(projectDir, "C.cs");
        File.WriteAllText(sourcePath, Source);

        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution;
        foreach (var (tfm, symbol) in new[] { ("net8.0", "NET8"), ("net10.0", "NET10") })
        {
            var projectId = ProjectId.CreateNewId();
            solution = solution.AddProject(ProjectInfo.Create(
                    projectId, VersionStamp.Default, $"Lib({tfm})", "Lib", LanguageNames.CSharp, filePath: projectPath,
                    parseOptions: new CSharpParseOptions(preprocessorSymbols: [symbol]),
                    metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]))
                .AddDocument(DocumentId.CreateNewId(projectId), "C.cs", SourceText.From(Source), filePath: sourcePath);
        }
        return solution;
    }
}
