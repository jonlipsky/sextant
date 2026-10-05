using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Sextant.Core;
using Sextant.Indexer;
using Sextant.Store;
using static Sextant.Service.Tests.CallerAssertionHttpTests;

namespace Sextant.Service.Tests;

/// <summary>
/// The live incident at the remote MCP boundary. A repository whose App project names a type from an assembly it
/// does not reference (the trailing optional parameter of an interface method; on the indexer, a transitive
/// ProjectReference that never flowed because restore did not run) is indexed by the real orchestrator. Before the
/// fix, <c>find_references</c> of that method returned nothing, <c>get_call_hierarchy</c> of its callers was empty, and
/// the snapshot claimed complete coverage. Now every call site is returned (marked <c>candidate</c>), and the lean
/// <c>meta.snapshot.warning</c> names the project that did not bind.
/// </summary>
[TestClass]
public class UnboundCallSitesHttpTests
{
    private const string Store = "https://github.com/acme/store";
    private const string Reader = "user-store";
    private const int CallerMethods = 20;

    private static Harness _host = null!;
    private static string _root = "";

    [ClassInitialize]
    public static async Task StartAsync(TestContext _)
    {
        _root = Path.Combine(Path.GetTempPath(), "sextant-unbound-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        _host = await Harness.StartAsync(
            configure: o => o with { RequireRepositorySelection = true },
            seed: db => IndexStore(db, _root));
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

    private static Task<ToolCall> CallAsync(string tool, string arguments) =>
        _host.CallAsync(tool, arguments, DelegateToken, _host.UserAssertion(sub: Reader));

    [TestMethod]
    public async Task FindReferences_ReturnsEveryUnboundCallSite_MarkedCandidate()
    {
        var call = await CallAsync("find_references", """{"symbol_fqn":"Core.IStore.EnsureAsync","limit":200}""");

        Assert.IsFalse(call.IsError, call.Body.ToString());
        var results = call.Body.GetProperty("results").EnumerateArray().ToList();
        Assert.AreEqual(CallerMethods, call.Body.GetProperty("meta").GetProperty("total").GetInt32(),
            "before the fix this was 0");
        Assert.IsTrue(results.All(r => r.GetProperty("candidate").GetBoolean()));
        Assert.IsTrue(results.All(r => r.GetProperty("file_path").GetString() == "src/App/Controller.cs"));
    }

    [TestMethod]
    public async Task FindReferences_OnlyTheUnboundSitesAreCandidates()
    {
        var call = await CallAsync("find_references", """{"symbol_fqn":"Core.IStore.PublishAsync","limit":200}""");

        var results = call.Body.GetProperty("results").EnumerateArray().ToList();
        Assert.AreEqual(CallerMethods + 1, results.Count, "the calls taking the error-typed result are returned too");
        var exact = results.Where(r => !r.TryGetProperty("candidate", out _)).ToList();
        Assert.AreEqual(1, exact.Count, "an exactly bound site carries no candidate field at all");
    }

    [TestMethod]
    public async Task CallHierarchy_CalleesOfACaller_AreNoLongerEmpty()
    {
        var call = await CallAsync("get_call_hierarchy",
            """{"symbol_fqn":"App.Controller.Publish3Async","direction":"callees","depth":1}""");

        Assert.IsFalse(call.IsError, call.Body.ToString());
        var callees = call.Body.GetProperty("results").EnumerateArray().ToList();
        CollectionAssert.AreEquivalent(new[] { "EnsureAsync", "PublishAsync" },
            callees.Select(c => c.GetProperty("display_name").GetString()).ToArray());
        Assert.IsTrue(callees.All(c => c.GetProperty("candidate").GetBoolean()));
    }

    [TestMethod]
    public async Task Meta_Snapshot_IsPartial_AndTheWarningNamesTheProjectThatDidNotBind()
    {
        var call = await CallAsync("find_symbol", """{"name":"Core.IStore"}""");

        var snapshot = call.Body.GetProperty("meta").GetProperty("snapshot");
        Assert.AreEqual("partial", snapshot.GetProperty("coverage").GetString());
        var warning = snapshot.GetProperty("warning").GetString()!;
        StringAssert.StartsWith(warning,
            "Partial index: Code in 1 project(s) did not fully compile on the indexer, so references and calls " +
            "inside them may be missing (src/App/App.csproj: ");
        StringAssert.EndsWith(warning,
            "Calls that failed to bind are kept as candidate matches. Call get_index_status for details.");
        StringAssert.Contains(warning, "Partial index: Code in 1 project(s)");
        Assert.IsFalse(warning.Contains("some projects or submodules were not indexed", StringComparison.Ordinal),
            "not the generic caution");
        var keys = snapshot.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(
            new[] { "branch", "commit", "coverage", "repository", "repository_selection", "warning" }, keys,
            "the lean #225 shape is unchanged");
    }

    [TestMethod]
    public async Task GetIndexStatus_ReportsBindingHealthPerProject_AndTheNotes()
    {
        var call = await CallAsync("get_index_status", "{}");

        var coverage = call.Body.GetProperty("index").GetProperty("coverage");
        Assert.AreEqual("partial", coverage.GetProperty("verdict").GetString());
        var binding = coverage.GetProperty("binding");
        Assert.AreEqual(1, binding.GetProperty("projects_degraded").GetInt32());
        var app = binding.GetProperty("projects")[0];
        Assert.AreEqual("src/App/App.csproj", app.GetProperty("project").GetString());
        Assert.IsTrue(app.GetProperty("degraded").GetBoolean());
        Assert.IsTrue(app.GetProperty("unbound_names").GetInt64() >= BindingHealthBuilder.DegradedMinUnboundNames);
        Assert.IsTrue(app.GetProperty("candidate_occurrences").GetInt64() >= 4L * CallerMethods);
        Assert.AreEqual(
            "1 project file(s) outside every selected solution were not indexed: samples/Demo/Demo.csproj.",
            coverage.GetProperty("notes")[0].GetString());
    }

    // ==== fixture ======================================================================================

    internal static void IndexStore(IndexDatabase db, string root)
    {
        var request = new EnsureSnapshotRequest
        {
            RepositoryRemoteUrl = Store, CommitSha = "commit-s1", BranchName = "main", IsDefaultBranch = true
        };
        // The worker's own coverage for this checkout: every solution project loaded; one stray sample project.
        var coverage = new SnapshotCoverage
        {
            Verdict = SnapshotCoverageVerdict.Complete,
            SelectionSource = "default_union",
            ProjectFilesOnDisk = 4,
            ProjectFilesUnreferenced = 1,
            Notes = ["1 project file(s) outside every selected solution were not indexed: samples/Demo/Demo.csproj."]
        };
        var context = LocalIndexerSnapshotWorker.CreateSnapshotContext(request, capability: null) with { Coverage = coverage };
        new IndexOrchestrator(db, useDocumentExtractor: true)
            .IndexSolutionAsync(StoreSolution(root), snapshotContext: context).GetAwaiter().GetResult();
    }

    private static Solution StoreSolution(string root)
    {
        var app = new System.Text.StringBuilder("""
            using System.Threading;
            using System.Threading.Tasks;
            namespace App
            {
                public class Controller
                {
                    private readonly Core.IStore _store;
                    public Controller(Core.IStore store) { _store = store; }
                    public Task PublishPlainAsync(string id, CancellationToken ct) => _store.PublishAsync(id, ct);

            """);
        for (var i = 0; i < CallerMethods; i++)
        {
            app.Append($$"""
                        public async Task Publish{{i}}Async(string name, CancellationToken ct)
                        {
                            var id = await _store.EnsureAsync(name, ct);
                            await _store.PublishAsync(id, ct);
                        }

                """);
        }
        app.Append("    }\n}\n");

        var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        MetadataReference[] framework =
        [
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Threading.Tasks.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Threading.dll")),
        ];
        var abs = ProjectId.CreateNewId();
        var core = ProjectId.CreateNewId();
        var appId = ProjectId.CreateNewId();
        var solution = new AdhocWorkspace().CurrentSolution;
        solution = Add(solution, root, framework, abs, "Abs", "OwnerKind.cs",
            "namespace Abs { public enum OwnerKind { User, Team } }");
        solution = Add(solution, root, framework, core, "Core", "IStore.cs", """
            using System.Threading;
            using System.Threading.Tasks;
            namespace Core
            {
                public interface IStore
                {
                    Task<string> EnsureAsync(string name, CancellationToken ct, Abs.OwnerKind owner = Abs.OwnerKind.User);
                    Task PublishAsync(string id, CancellationToken ct);
                }
            }
            """);
        solution = Add(solution, root, framework, appId, "App", "Controller.cs", app.ToString());
        // App references Core only: the incident's missing transitive reference to Abs.
        return solution.AddProjectReference(core, new ProjectReference(abs))
            .AddProjectReference(appId, new ProjectReference(core));
    }

    private static Solution Add(
        Solution solution, string root, MetadataReference[] framework, ProjectId id, string name, string file, string source)
    {
        var dir = Path.Combine(root, "src", name);
        Directory.CreateDirectory(dir);
        var projectPath = Path.Combine(dir, name + ".csproj");
        var sourcePath = Path.Combine(dir, file);
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(sourcePath, source);
        return solution
            .AddProject(ProjectInfo.Create(id, VersionStamp.Default, name, name, LanguageNames.CSharp,
                filePath: projectPath, metadataReferences: framework,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)))
            .AddDocument(DocumentId.CreateNewId(id), file, SourceText.From(source), filePath: sourcePath);
    }
}
