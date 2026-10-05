using System.Text.Json;
using Sextant.Core;
using Sextant.Mcp.Tools;
using Sextant.Store;

namespace Sextant.Mcp.Tests;

/// <summary>
/// Issue #244: every tool that serves source text (find_references snippets and context, find_symbol and
/// get_call_hierarchy <c>include_source</c>) serves the indexed version of a file. With a
/// <see cref="SourceTextStore"/> (the service) that holds once the working tree has moved to another commit
/// (lines shifted and rewritten, or the file deleted); without one (local) a moved file serves nothing, never
/// the drifted text at the indexed line numbers.
/// </summary>
[TestClass]
public class SnapshotSourceTextTests
{
    private static readonly string[] WidgetLines =
        ["namespace App;", "public class Widget", "{", "    public int Make() => 42;", "}"];

    private static readonly string[] UserLines =
        ["namespace App;", "public class User", "{", "    public int Use() => new Widget().Make();", "}"];

    private static readonly string[] MovedUserLines =
        ["namespace App;", "// moved", "// moved", "public class User", "{", "    public int Drifted() => 7;",
         "    public int Use() => new Widget().Make() * 2;", "}"];

    private string _root = null!;
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SourceTextStore _texts = null!;
    private string _widget = null!;
    private string _user = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sextant_snaptext_{Guid.NewGuid():N}");
        var source = Path.Combine(_root, "repo", "src", "App");
        Directory.CreateDirectory(source);
        _widget = Path.Combine(source, "Widget.cs");
        _user = Path.Combine(source, "User.cs");
        File.WriteAllLines(_widget, WidgetLines);
        File.WriteAllLines(_user, UserLines);

        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_snaptext_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _texts = new SourceTextStore(Path.Combine(_root, "texts"));
        Seed();
    }

    [TestCleanup]
    public void TestCleanup()
    {
        SqliteTestDatabase.Delete(_dbPath, _db);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public void WithTheStore_EveryToolServesTheIndexedText_AfterTheWorkingTreeMoved()
    {
        MoveWorkingTree();
        using var provider = new DatabaseProvider(_dbPath) { SourceTexts = _texts };

        var reference = Results(FindReferencesTool.FindReferences(provider, "global::App.Widget", include_source: true)).Single();
        Assert.AreEqual(UserLines[3].Trim(), reference.GetProperty("context_snippet").GetString());
        Assert.AreEqual(string.Join("\n", UserLines[1..5]), reference.GetProperty("source_context").GetString());

        // Widget.cs was deleted: its declaration still comes from the indexed bytes.
        var declaration = Results(FindSymbolTool.FindSymbol(provider, "App.Widget", include_source: true).GetAwaiter().GetResult())
            .Single().GetProperty("source_context");
        Assert.AreEqual(2, declaration.GetProperty("start_line").GetInt32());
        Assert.AreEqual(5, declaration.GetProperty("end_line").GetInt32());
        CollectionAssert.AreEqual(WidgetLines[1..5], Contents(declaration));
        CollectionAssert.AreEqual(new[] { 2, 3, 4, 5 }, LineNumbers(declaration));

        var callSite = Results(GetCallHierarchyTool.GetCallHierarchy(provider, "App.Widget.Make", "callers", include_source: true))
            .Single().GetProperty("source_context");
        Assert.AreEqual(2, callSite.GetProperty("start_line").GetInt32());
        Assert.AreEqual(5, callSite.GetProperty("end_line").GetInt32());
        CollectionAssert.AreEqual(UserLines[1..5], Contents(callSite));
    }

    [TestMethod]
    public void WithoutTheStore_AMovedFileServesNothing_NeverTheDriftedText()
    {
        MoveWorkingTree();
        using var provider = new DatabaseProvider(_dbPath);

        var references = FindReferencesTool.FindReferences(provider, "global::App.Widget", include_source: true);
        var reference = Results(references).Single();
        Assert.AreEqual(JsonValueKind.Null, reference.GetProperty("context_snippet").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, reference.GetProperty("source_context").ValueKind);

        var symbol = FindSymbolTool.FindSymbol(provider, "App.User.Use", include_source: true).GetAwaiter().GetResult();
        Assert.AreEqual(JsonValueKind.Null, Results(symbol).Single().GetProperty("source_context").ValueKind, symbol);

        var callers = GetCallHierarchyTool.GetCallHierarchy(provider, "App.Widget.Make", "callers", include_source: true);
        Assert.AreEqual(JsonValueKind.Null, Results(callers).Single().GetProperty("source_context").ValueKind, callers);

        foreach (var json in new[] { references, symbol, callers })
            Assert.IsFalse(json.Contains("Drifted", StringComparison.Ordinal) || json.Contains("// moved", StringComparison.Ordinal), json);
    }

    [TestMethod]
    public void WithoutTheStore_AnUnchangedFileIsServedFromDisk()
    {
        using var provider = new DatabaseProvider(_dbPath);

        var declaration = Results(FindSymbolTool.FindSymbol(provider, "App.User.Use", include_source: true).GetAwaiter().GetResult())
            .Single().GetProperty("source_context");
        Assert.AreEqual(4, declaration.GetProperty("start_line").GetInt32());
        CollectionAssert.AreEqual(new[] { UserLines[3] }, Contents(declaration));

        var callSite = Results(GetCallHierarchyTool.GetCallHierarchy(provider, "App.Widget.Make", "callers", include_source: true))
            .Single().GetProperty("source_context");
        CollectionAssert.AreEqual(UserLines[1..5], Contents(callSite));
    }

    [TestMethod]
    public void Retriever_ACorruptStoredTextAndAMovedFile_ServeNothing()
    {
        MoveWorkingTree();
        var conn = _db.GetConnection();
        var files = new FileStore(conn);
        var projectId = new ProjectStore(conn).GetAll().Single().id;
        foreach (var blob in Directory.EnumerateFiles(_texts.Root, "*.br", SearchOption.AllDirectories))
            File.WriteAllBytes(blob, [0xFF, 0x00, 0x13, 0x37, 0x42]);

        var retriever = new SourceContextRetriever(files, _texts);

        Assert.IsNull(retriever.GetLineSnippet(projectId, _user, 4));
        Assert.IsNull(retriever.GetDeclaration(projectId, _widget, 2, 5));
        Assert.IsNull(retriever.GetContextBlock(projectId, _user, 4, 2));
    }

    private void Seed()
    {
        var conn = _db.GetConnection();
        var projectId = new ProjectStore(conn).Insert(new ProjectIdentity
        {
            CanonicalId = "snaptext00000001",
            GitRemoteUrl = "https://github.com/org/app",
            RepoRelativePath = "src/App/App.csproj",
            DiskPath = Path.Combine(Path.GetDirectoryName(_widget)!, "App.csproj"),
            TargetFramework = "net10.0"
        }, 1);

        // The indexer's path: one FileStore that keeps each hashed file's bytes (the service worker).
        var files = new FileStore(conn) { SourceTexts = _texts };
        var symbols = new SymbolStore(conn) { Files = files };
        var widget = symbols.Insert(Symbol(projectId, "App.Widget", "Widget", SymbolKind.Class, _widget, 2, 5));
        var make = symbols.Insert(Symbol(projectId, "App.Widget.Make", "Make", SymbolKind.Method, _widget, 4, 4));
        var use = symbols.Insert(Symbol(projectId, "App.User.Use", "Use", SymbolKind.Method, _user, 4, 4));
        new ReferenceStore(conn) { Files = files }.Insert(new ReferenceInfo
        {
            SymbolId = widget, InProjectId = projectId, FilePath = _user, Line = 4,
            ReferenceKind = ReferenceKind.ObjectCreation, LastIndexedAt = 1
        });
        new CallGraphStore(conn) { Files = files }.Insert(new CallGraphEdge
        {
            CallerSymbolId = use, CalleeSymbolId = make, CallSiteFile = _user, CallSiteLine = 4, CallSiteColumn = 29,
            LastIndexedAt = 1
        }, projectId);
    }

    private static SymbolInfo Symbol(long projectId, string name, string display, SymbolKind kind, string file, int start, int end) => new()
    {
        ProjectId = projectId,
        SymbolKey = "global::" + name, FullyQualifiedName = "global::" + name, DisplayName = display,
        Kind = kind, Accessibility = Accessibility.Public, FilePath = file, LineStart = start, LineEnd = end,
        LastIndexedAt = 1
    };

    // Another commit is checked out: User.cs gains lines above the indexed ones and is rewritten, Widget.cs is gone.
    private void MoveWorkingTree()
    {
        File.WriteAllLines(_user, MovedUserLines);
        File.Delete(_widget);
    }

    private static List<JsonElement> Results(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("results").EnumerateArray().ToList();

    private static string?[] Contents(JsonElement sourceContext) =>
        sourceContext.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("content").GetString()).ToArray();

    private static int[] LineNumbers(JsonElement sourceContext) =>
        sourceContext.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("line_number").GetInt32()).ToArray();
}
