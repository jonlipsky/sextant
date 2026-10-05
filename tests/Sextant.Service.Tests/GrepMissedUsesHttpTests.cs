using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Sextant.Indexer;
using Sextant.Store;
using static Sextant.Service.Tests.CallerAssertionHttpTests;

namespace Sextant.Service.Tests;

/// <summary>
/// The claims the <c>find_references</c> and <c>get_call_hierarchy</c> descriptions make to an agent, checked over the
/// remote <c>/mcp</c> against a repository indexed by the real orchestrator: they return the uses a grep for the
/// usual spelling (<c>new ProcessRunner(</c>, <c>ProcessRunner.Create(</c>, <c>IRunner</c>) misses, and none of the
/// comments, strings or namesakes such a grep matches.
/// </summary>
[TestClass]
public class GrepMissedUsesHttpTests
{
    private const string Repository = "https://github.com/acme/runner";
    private const string Reader = "user-runner";
    private const string HostFile = "src/App/Host.cs";

    private const string LibSource = """
        namespace Acme.Process
        {
            public interface IRunner
            {
                void Start(string name);
            }

            public class ProcessRunner : IRunner
            {
                public void Start(string name) { }
                public static ProcessRunner Create() => new ProcessRunner();
            }
        }

        namespace Acme.Other
        {
            public class ProcessRunner
            {
                public void Start(string name) { }
            }
        }
        """;

    private const string HostSource = """
        using Runners = Acme.Process.ProcessRunner;
        using static Acme.Process.ProcessRunner;

        namespace Acme.App
        {
            public class Host
            {
                private readonly Acme.Process.IRunner _runner;
                private readonly Process.ProcessRunner _owned = new();

                public Host(Acme.Process.IRunner runner) { _runner = runner; }

                public object Qualified() => new Process.ProcessRunner();
                public void Pass() => Use(new());
                public void ThroughInterface() => _runner.Start("a");
                public void Direct() => _owned.Start("b");
                public object Aliased() => Runners.Create();
                public object Imported() => Create();
                public object QualifiedFactory() => Acme.Process.ProcessRunner.Create();
                public object Namesake() => new Other.ProcessRunner();
                // a new ProcessRunner() is made by the factory, not here
                public string Label() => "new ProcessRunner()";

                private static void Use(Process.ProcessRunner runner) { }
            }
        }
        """;

    private static Harness _host = null!;
    private static string _root = "";

    [ClassInitialize]
    public static async Task StartAsync(TestContext _)
    {
        _root = Path.Combine(Path.GetTempPath(), "sextant-grep-missed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        _host = await Harness.StartAsync(
            configure: o => o with { RequireRepositorySelection = true },
            seed: db => Index(db, _root));
        using var response = await _host.ControlAsync(HttpMethod.Put, "/control/grants/self", ControlToken,
            _host.UserAssertion(sub: Reader), JsonSerializer.Serialize(new { repository = Repository }));
        Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [ClassCleanup]
    public static async Task StopAsync()
    {
        await _host.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ==== find_references ==============================================================================

    [TestMethod]
    public async Task FindReferences_OnAType_ReportsQualifiedAndTargetTypedCreations_ThatGrepMisses()
    {
        var rows = await ReferencesAsync("Acme.Process.ProcessRunner");

        var grep = new Regex(@"\bnew ProcessRunner\(");
        foreach (var marker in new[] { "new Process.ProcessRunner()", "_owned = new()", "Use(new())" })
        {
            var line = LineOf(marker);
            Assert.IsFalse(grep.IsMatch(HostLine(line)), $"a grep would find '{marker}' anyway");
            Assert.IsTrue(rows.Any(r => r.File == HostFile && r.Line == line && r.Kind == "objectcreation"),
                $"'{marker}' (line {line}) is not reported as an object creation: {Show(rows)}");
        }
        Assert.IsFalse(HostLine(LineOf("Use(new())")).Contains("ProcessRunner", StringComparison.Ordinal),
            "the target-typed argument does not name the type at all");
    }

    [TestMethod]
    public async Task FindReferences_OnAType_ReportsNoCommentStringOrNamesake()
    {
        var rows = await ReferencesAsync("Acme.Process.ProcessRunner");

        foreach (var marker in new[] { "// a new ProcessRunner()", "\"new ProcessRunner()\"", "new Other.ProcessRunner()" })
        {
            var line = LineOf(marker);
            Assert.IsTrue(HostLine(line).Contains("ProcessRunner", StringComparison.Ordinal), "a grep for the name matches it");
            Assert.IsFalse(rows.Any(r => r.File == HostFile && r.Line == line), $"'{marker}' (line {line}) is a false match: {Show(rows)}");
        }
    }

    [TestMethod]
    public async Task FindReferences_OnAnInterfaceMethod_ReportsTheCallThroughTheInterface()
    {
        var rows = await ReferencesAsync("Acme.Process.IRunner.Start");

        var line = LineOf("_runner.Start(\"a\")");
        Assert.IsFalse(HostLine(line).Contains("IRunner", StringComparison.Ordinal), "the call site does not name the interface");
        Assert.IsTrue(rows.Any(r => r.File == HostFile && r.Line == line && r.Kind == "invocation"), Show(rows));
        Assert.IsFalse(rows.Any(r => r.Line == LineOf("_owned.Start(\"b\")") && r.File == HostFile),
            "a call on the concrete type is the implementation's, not the interface member's");
    }

    // The scope of "calls through interfaces": such a call binds to the interface member, so the implementation
    // lists only the calls made on it directly. An agent asks the interface member for the dispatched calls.
    [TestMethod]
    public async Task FindReferences_OnTheImplementation_ListsOnlyItsDirectCalls()
    {
        var rows = await ReferencesAsync("Acme.Process.ProcessRunner.Start");

        Assert.IsTrue(rows.Any(r => r.File == HostFile && r.Line == LineOf("_owned.Start(\"b\")")), Show(rows));
        Assert.IsFalse(rows.Any(r => r.File == HostFile && r.Line == LineOf("_runner.Start(\"a\")")), Show(rows));
    }

    // ==== get_call_hierarchy ===========================================================================

    [TestMethod]
    public async Task CallHierarchy_CallersOfAnInterfaceMethod_IncludeTheCallThroughTheInterface()
    {
        var callers = await CallersAsync("Acme.Process.IRunner.Start");

        Assert.IsTrue(callers.Any(c => c.Name == "ThroughInterface" && c.File == HostFile && c.Line == LineOf("_runner.Start(\"a\")")),
            Show(callers));
        Assert.IsFalse(callers.Any(c => c.Name == "Direct"), Show(callers));
    }

    [TestMethod]
    public async Task CallHierarchy_CallersOfTheImplementation_AreOnlyItsDirectCallers()
    {
        var callers = await CallersAsync("Acme.Process.ProcessRunner.Start");

        CollectionAssert.AreEquivalent(new[] { "Direct" }, callers.Select(c => c.Name).ToArray(), Show(callers));
    }

    [TestMethod]
    public async Task CallHierarchy_CallersOfAStaticMethod_IncludeAliasedAndUsingStaticCalls_ThatGrepMisses()
    {
        var callers = await CallersAsync("Acme.Process.ProcessRunner.Create");

        var grep = new Regex(@"\bProcessRunner\.Create\(");
        foreach (var (caller, marker) in new[] { ("Aliased", "Runners.Create()"), ("Imported", "=> Create()") })
        {
            var line = LineOf(marker);
            Assert.IsFalse(grep.IsMatch(HostLine(line)), $"a grep would find '{marker}' anyway");
            Assert.IsTrue(callers.Any(c => c.Name == caller && c.File == HostFile && c.Line == line), $"{caller}: {Show(callers)}");
        }

        // A qualified call is reported too, but the same grep finds it, so the description does not claim it.
        var qualified = LineOf("Acme.Process.ProcessRunner.Create()");
        Assert.IsTrue(grep.IsMatch(HostLine(qualified)));
        Assert.IsTrue(callers.Any(c => c.Name == "QualifiedFactory" && c.Line == qualified), Show(callers));
    }

    [TestMethod]
    public async Task FindReferences_OnAType_ReportsTheAliasedMention()
    {
        var rows = await ReferencesAsync("Acme.Process.ProcessRunner");

        var line = LineOf("Runners.Create()");
        Assert.IsFalse(HostLine(line).Contains("ProcessRunner", StringComparison.Ordinal), "the call site names only the alias");
        Assert.IsTrue(rows.Any(r => r.File == HostFile && r.Line == line && r.Kind == "typeref"), Show(rows));
    }

    // ==== helpers ======================================================================================

    private sealed record Row(string File, int Line, string Kind, string Name);

    private static string Show(IEnumerable<Row> rows) => string.Join("; ", rows.Select(r => $"{r.Name}{r.Kind} {r.File}:{r.Line}"));

    private static async Task<List<Row>> ReferencesAsync(string symbol)
    {
        var call = await _host.CallAsync("find_references", JsonSerializer.Serialize(new { symbol_fqn = symbol, limit = 200 }),
            DelegateToken, _host.UserAssertion(sub: Reader));
        Assert.IsFalse(call.IsError, call.Body.ToString());
        return call.Body.GetProperty("results").EnumerateArray()
            .Select(r => new Row(r.GetProperty("file_path").GetString()!, r.GetProperty("line").GetInt32(),
                r.GetProperty("reference_kind").GetString()!, ""))
            .ToList();
    }

    private static async Task<List<Row>> CallersAsync(string method)
    {
        var call = await _host.CallAsync("get_call_hierarchy",
            JsonSerializer.Serialize(new { symbol_fqn = method, direction = "callers", depth = 1, limit = 200 }),
            DelegateToken, _host.UserAssertion(sub: Reader));
        Assert.IsFalse(call.IsError, call.Body.ToString());
        return call.Body.GetProperty("results").EnumerateArray()
            .Select(r => new Row(r.GetProperty("call_site_file").GetString()!, r.GetProperty("call_site_line").GetInt32(),
                "", r.GetProperty("display_name").GetString()!))
            .ToList();
    }

    private static readonly string[] HostLines = HostSource.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

    private static string HostLine(int line) => HostLines[line - 1];

    private static int LineOf(string marker)
    {
        var index = Array.FindIndex(HostLines, l => l.Contains(marker, StringComparison.Ordinal));
        Assert.IsTrue(index >= 0, marker);
        Assert.AreEqual(index, Array.FindLastIndex(HostLines, l => l.Contains(marker, StringComparison.Ordinal)), $"'{marker}' is not unique");
        return index + 1;
    }

    // ==== fixture ======================================================================================

    private static void Index(IndexDatabase db, string root)
    {
        var request = new EnsureSnapshotRequest
        {
            RepositoryRemoteUrl = Repository, CommitSha = "commit-r1", BranchName = "main", IsDefaultBranch = true
        };
        var context = LocalIndexerSnapshotWorker.CreateSnapshotContext(request, capability: null);
        new IndexOrchestrator(db, useDocumentExtractor: true)
            .IndexSolutionAsync(Solution(root), snapshotContext: context).GetAwaiter().GetResult();
    }

    private static Solution Solution(string root)
    {
        var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        MetadataReference[] framework =
        [
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
        ];
        var lib = ProjectId.CreateNewId();
        var app = ProjectId.CreateNewId();
        var solution = new AdhocWorkspace().CurrentSolution;
        solution = Add(solution, root, framework, lib, "Lib", "Runners.cs", LibSource);
        solution = Add(solution, root, framework, app, "App", "Host.cs", HostSource);
        return solution.AddProjectReference(app, new ProjectReference(lib));
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
