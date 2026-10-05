using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Sextant.Indexer;
using Sextant.Store;
using static Sextant.Service.Tests.CallerAssertionHttpTests;

namespace Sextant.Service.Tests;

/// <summary>
/// Issues #149 and #163 at the remote MCP boundary: the query tools resolve the symbol arguments coding agents
/// naturally type (no <c>global::</c>, <c>Type.Member</c>, a parameter list, a documentation ID), never silently pick
/// one of several symbols, and report every failure as an MCP tool error (<c>isError: true</c>) whose message says
/// what to pass instead. The service runs as it does in production: delegate token + verified caller assertion,
/// <c>REQUIRE_REPOSITORY_SELECTION=true</c>, grants deciding visibility. The library repository is indexed by the
/// real orchestrator (document extractor) from C# source, so its symbol keys, display names and signatures are exactly
/// what a live snapshot stores.
/// </summary>
[TestClass]
public class QueryToolInputResolutionHttpTests
{
    private const string Library = "https://github.com/acme/library";

    // A user granted only the library (implicit selection), one granted both indexed repositories, one granted only
    // Widgets, and one with no grant at all.
    private const string Solo = "user-solo";
    private const string Duo = "user-duo";
    private const string WidgetsOnly = "user-widgets";
    private const string Nobody = "user-nobody";

    private static Harness _host = null!;
    private static string _root = "";

    [ClassInitialize]
    public static async Task StartAsync(TestContext _)
    {
        _root = Path.Combine(Path.GetTempPath(), "sextant-resolution-" + Guid.NewGuid().ToString("N"));
        _host = await Harness.StartAsync(
            configure: o => o with { RequireRepositorySelection = true },
            seed: db => IndexLibrary(db, _root));
        await GrantAsync(Solo, Library);
        await GrantAsync(Duo, Library);
        await GrantAsync(Duo, Widgets);
        await GrantAsync(WidgetsOnly, Widgets);
    }

    [ClassCleanup]
    public static async Task StopAsync()
    {
        await _host.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ==== repository selection =======================================================================

    [TestMethod]
    public async Task OmittedRepository_TheOnlyGrantedRepository_IsRead_AndTheAnswerNamesIt()
    {
        var call = await CallAsync("find_symbol", """{"name":"global::Library.Shapes.Circle"}""", Solo);

        Assert.IsFalse(call.IsError, call.Body.ToString());
        Assert.AreEqual("global::Library.Shapes.Circle", Names(call.Body).Single());
        var snapshot = call.Body.GetProperty("meta").GetProperty("snapshot");
        // The lean remote meta names it in the host/owner/repo form the repository argument accepts.
        Assert.AreEqual("github.com/acme/library", snapshot.GetProperty("repository").GetString(),
            "the answer says which repository it read");
        Assert.AreEqual("implicit", snapshot.GetProperty("repository_selection").GetString());
    }

    [TestMethod]
    public async Task OmittedRepository_SeveralGrantedRepositories_IsAToolError_ListingThem()
    {
        var call = await CallAsync("find_symbol", """{"name":"global::Library.Shapes.Circle"}""", Duo);

        Assert.IsTrue(call.IsError, call.Body.ToString());
        Assert.AreEqual("repository_required", ErrorCode(call.Body));
        var message = Message(call.Body);
        StringAssert.Contains(message, "'repository'");
        StringAssert.Contains(message, "'acme/library'");
        StringAssert.Contains(message, "'acme/widgets'");
        Assert.AreEqual(0, call.Body.GetProperty("results").GetArrayLength());
    }

    [TestMethod]
    public async Task OmittedRepository_TheErrorListsOnlyTheCallersOwnRepositories()
    {
        // A branch alone never selects a repository, so even a caller with one grant is asked to name it, and the
        // list is ITS grants: the library (indexed, but not granted to it) is never revealed.
        var widgetsOnly = await CallAsync("find_symbol", """{"name":"global::App.Type0","branch":"main"}""", WidgetsOnly);
        var nobody = await CallAsync("find_symbol", """{"name":"global::App.Type0"}""", Nobody);

        Assert.IsTrue(widgetsOnly.IsError, widgetsOnly.Body.ToString());
        StringAssert.Contains(Message(widgetsOnly.Body), "'acme/widgets'");
        Assert.IsFalse(widgetsOnly.Body.ToString().Contains("library", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(nobody.IsError, nobody.Body.ToString());
        Assert.AreEqual("repository_required", ErrorCode(nobody.Body));
        StringAssert.Contains(Message(nobody.Body), "no repository grants");
        Assert.IsFalse(nobody.Body.ToString().Contains("acme/", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ToolsList_TheRepositoryArgument_IsNotAdvertisedAsOptional()
    {
        var list = await _host.RpcAsync("tools/list", "{}", DelegateToken);

        var tool = list.Result!.Value.GetProperty("tools").EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "find_references");
        var description = tool.GetProperty("inputSchema").GetProperty("properties").GetProperty("repository")
            .GetProperty("description").GetString()!;
        Assert.IsTrue(description.StartsWith("Required", StringComparison.Ordinal), description);
        Assert.IsFalse(description.Contains("Optional", StringComparison.Ordinal), description);
        // The description stays one short line (it is repeated on every scoped tool); omitting the argument
        // when the caller can read several repositories is the repository_required tool error.
        StringAssert.Contains(description, "unless exactly one is granted");
    }

    [TestMethod]
    public async Task NamedRepositoryTheCallerCannotRead_IsAToolError_IndistinguishableFromAnAbsentOne()
    {
        // Solo can read only the library: acme/widgets is indexed but not granted to it, acme/absent does not exist,
        // and the library has no 'no-such-branch'. Each is the ONE uniform repository_not_found error, so a wrong
        // repository is never read as "no matches", and nothing tells an indexed repository from an absent one.
        var ungranted = await CallAsync("find_symbol", """{"name":"Circle","repository":"acme/widgets"}""", Solo);
        var absent = await CallAsync("find_symbol", """{"name":"Circle","repository":"acme/absent"}""", Solo);
        var branch = await CallAsync("find_symbol",
            """{"name":"Circle","repository":"acme/library","branch":"no-such-branch"}""", Solo);

        foreach (var call in new[] { ungranted, absent, branch })
        {
            Assert.IsTrue(call.IsError, call.Body.ToString());
            Assert.AreEqual("repository_not_found", ErrorCode(call.Body));
            StringAssert.Contains(Message(call.Body), "list_repositories");
            Assert.AreEqual(0, call.Body.GetProperty("results").GetArrayLength());
        }
        Assert.AreEqual(WithoutTimestamp(absent.Body), WithoutTimestamp(ungranted.Body),
            "an ungranted repository reads exactly like an absent one");
        Assert.AreEqual(WithoutTimestamp(absent.Body), WithoutTimestamp(branch.Body));
        Assert.IsFalse(ungranted.Body.ToString().Contains("widgets", StringComparison.OrdinalIgnoreCase),
            "nothing requested is echoed");
    }

    // ==== #149: an FQN without global:: ===============================================================

    [TestMethod]
    [DataRow("find_references", """{"symbol_fqn":"Library.Shapes.IShape"}""")]
    [DataRow("get_implementors", """{"symbol_fqn":"Library.Shapes.IShape"}""")]
    [DataRow("get_type_hierarchy", """{"symbol_fqn":"Library.Shapes.Circle"}""")]
    [DataRow("get_type_members", """{"symbol_fqn":"Library.Shapes.Circle"}""")]
    [DataRow("get_call_hierarchy", """{"symbol_fqn":"Library.Drawing.Canvas.Draw","direction":"callees"}""")]
    public async Task FqnWithoutTheGlobalAlias_Resolves(string tool, string arguments)
    {
        var call = await CallAsync(tool, WithRepository(arguments), Solo);

        Assert.IsFalse(call.IsError, call.Body.ToString());
        Assert.IsTrue(call.Body.GetProperty("results").GetArrayLength() > 0, call.Body.ToString());
    }

    // ==== #163: exact type lookup, kind, member identity ==============================================

    [TestMethod]
    public async Task FindSymbol_ATypeName_ReturnsTheType_NotItsConstructors_OrAFieldOfTheSameName()
    {
        // The ImageButton shape of #163: a class, its constructors and a field elsewhere all named Circle.
        var bare = await CallAsync("find_symbol", WithRepository("""{"name":"Circle"}"""), Solo);
        var asClass = await CallAsync("find_symbol", WithRepository("""{"name":"Circle","kind":"class"}"""), Solo);
        var asField = await CallAsync("find_symbol", WithRepository("""{"name":"Circle","kind":"field"}"""), Solo);

        Assert.IsFalse(bare.IsError, bare.Body.ToString());
        Assert.AreEqual("global::Library.Shapes.Circle", Names(bare.Body).Single(), bare.Body.ToString());
        Assert.AreEqual("class", bare.Body.GetProperty("results")[0].GetProperty("kind").GetString());
        StringAssert.Contains(Message(bare.Body), "global::Library.Styles.StyleKeys.Circle",
            "the answer names the other match so the caller can target it");
        Assert.AreEqual("global::Library.Shapes.Circle", Names(asClass.Body).Single(), asClass.Body.ToString());
        Assert.AreEqual("global::Library.Styles.StyleKeys.Circle", Names(asField.Body).Single(),
            $"kind is honoured on the exact path: {asField.Body}");
        Assert.AreEqual("field", asField.Body.GetProperty("results")[0].GetProperty("kind").GetString());
    }

    [TestMethod]
    public async Task BareHomonymMethodName_IsAnAmbiguityError_ListingEveryCandidate()
    {
        var call = await CallAsync("find_references", WithRepository("""{"symbol_fqn":"Area"}"""), Solo);

        Assert.IsTrue(call.IsError, $"a homonym is never resolved to an arbitrary one: {call.Body}");
        Assert.AreEqual("ambiguous_symbol", ErrorCode(call.Body));
        Assert.AreEqual(0, call.Body.GetProperty("results").GetArrayLength());
        CollectionAssert.AreEquivalent(
            new[]
            {
                "global::Library.Shapes.Circle.Area()", "global::Library.Shapes.IShape.Area()",
                "global::Library.Shapes.Square.Area()"
            },
            Candidates(call.Body));
        StringAssert.Contains(Message(call.Body), "Type.Method(int)");
    }

    [TestMethod]
    [DataRow("Circle.Area", "Canvas.cs|Circle.Drawing.cs")]
    [DataRow("Library.Shapes.Square.Area", "Canvas.cs")]
    [DataRow("global::Library.Shapes.Circle.Area()", "Canvas.cs|Circle.Drawing.cs")]
    [DataRow("Library.Shapes.Circle.Scale(double)", "Canvas.cs|Shapes.cs")]
    [DataRow("global::Library.Shapes.Circle.Scale(double, bool)", "Canvas.cs")]
    [DataRow("Circle.Scale(System.Double, System.Boolean)", "Canvas.cs")]
    [DataRow("M:Library.Shapes.Circle.Scale(System.Double)", "Canvas.cs|Shapes.cs")]
    [DataRow("Library.Shapes.IShape.Area", "Canvas.cs")]
    public async Task MemberSpellings_ResolveTheNamedMember(string name, string expectedFiles)
    {
        var call = await CallAsync("find_references",
            WithRepository($$"""{"symbol_fqn":{{JsonSerializer.Serialize(name)}}}"""), Solo);

        Assert.IsFalse(call.IsError, call.Body.ToString());
        var files = call.Body.GetProperty("results").EnumerateArray()
            .Select(r => Path.GetFileName(r.GetProperty("file_path").GetString()!))
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(expectedFiles.Split('|'), files, $"the references of exactly that member: {call.Body}");
    }

    [TestMethod]
    public async Task OverloadedMemberWithoutAParameterList_IsAnAmbiguityError_ListingTheOverloads()
    {
        var call = await CallAsync("get_call_hierarchy",
            WithRepository("""{"symbol_fqn":"Library.Shapes.Circle.Scale","direction":"callers"}"""), Solo);

        Assert.IsTrue(call.IsError, call.Body.ToString());
        Assert.AreEqual("ambiguous_symbol", ErrorCode(call.Body));
        CollectionAssert.AreEquivalent(
            new[] { "global::Library.Shapes.Circle.Scale(double)", "global::Library.Shapes.Circle.Scale(double, bool)" },
            Candidates(call.Body));
    }

    // ==== get_type_members ============================================================================

    [TestMethod]
    [DataRow("global::Library.Shapes.Circle",
        "global::Library.Shapes.Circle.Area()|global::Library.Shapes.Circle.Circle(double)|" +
        "global::Library.Shapes.Circle.Describe()|global::Library.Shapes.Circle.Radius|" +
        "global::Library.Shapes.Circle.Scale(double)|global::Library.Shapes.Circle.Scale(double, bool)")]
    [DataRow("global::Library.Shapes.IShape", "global::Library.Shapes.IShape.Area()|global::Library.Shapes.IShape.Name")]
    [DataRow("global::Library.Shapes.Square",
        "global::Library.Shapes.Square.Area()|global::Library.Shapes.Square.Changed|" +
        "global::Library.Shapes.Square.Pick<T>(T)|global::Library.Shapes.Square.Side|global::Library.Shapes.Square.this[int]")]
    public async Task GetTypeMembers_ListsTheMembers_InAFormEveryToolAcceptsBack(string type, string expected)
    {
        var call = await CallAsync("get_type_members", WithRepository($$"""{"symbol_fqn":"{{type}}"}"""), Solo);

        Assert.IsFalse(call.IsError, call.Body.ToString());
        var names = Names(call.Body);
        foreach (var member in expected.Split('|'))
            CollectionAssert.Contains(names, member, $"{type}: {string.Join(", ", names)}");
        Assert.IsFalse(names.Any(n => n.Contains("Corner.Index", StringComparison.Ordinal)), "a nested type's members are not listed");

        // Every listed name resolves back to exactly that member.
        var listed = call.Body.GetProperty("results").EnumerateArray().ToList();
        foreach (var member in listed)
        {
            var name = member.GetProperty("fully_qualified_name").GetString()!;
            var back = await CallAsync("find_symbol", WithRepository($$"""{"name":{{JsonSerializer.Serialize(name)}}}"""), Solo);
            Assert.IsFalse(back.IsError, $"{name}: {back.Body}");
            var hit = back.Body.GetProperty("results").EnumerateArray().Single();
            Assert.AreEqual(name, hit.GetProperty("fully_qualified_name").GetString());
            Assert.AreEqual(member.GetProperty("kind").GetString(), hit.GetProperty("kind").GetString(), name);
            Assert.AreEqual(member.GetProperty("line_start").GetInt32(), hit.GetProperty("line_start").GetInt32(), name);
        }
    }

    // ==== generic types and members ==================================================================

    [TestMethod]
    [DataRow("IStore.PublishAsync")]
    [DataRow("IStore<T>.PublishAsync")]
    [DataRow("IStore`1.PublishAsync")]
    [DataRow("Library.Storage.IStore<T>.PublishAsync(T, System.Threading.CancellationToken)")]
    [DataRow("global::Library.Storage.IStore<T>.PublishAsync(T, CancellationToken)")]
    [DataRow("Library.Storage.IStore<string>.PublishAsync(string, CancellationToken)")]
    [DataRow("M:Library.Storage.IStore`1.PublishAsync(`0,System.Threading.CancellationToken)")]
    public async Task GenericInterfaceMethod_ResolvesInEverySpelling_ToItsCallsThroughTheInterface(string name)
    {
        var call = await CallAsync("find_references",
            WithRepository($$"""{"symbol_fqn":{{JsonSerializer.Serialize(name)}}}"""), Solo);

        Assert.IsFalse(call.IsError, call.Body.ToString());
        var files = call.Body.GetProperty("results").EnumerateArray()
            .Select(r => Path.GetFileName(r.GetProperty("file_path").GetString()!))
            .Distinct()
            .ToArray();
        CollectionAssert.AreEqual(new[] { "Publisher.cs" }, files, $"the call through IStore<string>: {call.Body}");
    }

    [TestMethod]
    public async Task GenericInterfaceMethod_ThePrintedName_ResolvesBackToIt()
    {
        var members = await CallAsync("get_type_members", WithRepository("""{"symbol_fqn":"Library.Storage.IStore"}"""), Solo);
        Assert.IsFalse(members.IsError, members.Body.ToString());
        var printed = Names(members.Body).Single(n => n.Contains("PublishAsync", StringComparison.Ordinal));

        var call = await CallAsync("find_references",
            WithRepository($$"""{"symbol_fqn":{{JsonSerializer.Serialize(printed)}}}"""), Solo);

        Assert.IsFalse(call.IsError, $"{printed}: {call.Body}");
        Assert.IsTrue(call.Body.GetProperty("meta").GetProperty("result_count").GetInt32() > 0, call.Body.ToString());
    }

    [TestMethod]
    [DataRow("Library.Storage.Result", "global::Library.Storage.Result")]
    [DataRow("global::Library.Storage.Result", "global::Library.Storage.Result")]
    [DataRow("Result", "global::Library.Storage.Result")]
    [DataRow("Library.Storage.Result<T>", "global::Library.Storage.Result<T>")]
    [DataRow("Result`1", "global::Library.Storage.Result<T>")]
    [DataRow("T:Library.Storage.Result`1", "global::Library.Storage.Result<T>")]
    public async Task NameWithoutTypeArguments_IsTheNonGenericType_WhenOneExists(string name, string expected)
    {
        var call = await CallAsync("find_symbol", WithRepository($$"""{"name":{{JsonSerializer.Serialize(name)}}}"""), Solo);

        Assert.IsFalse(call.IsError, call.Body.ToString());
        Assert.AreEqual(expected, Names(call.Body).Single(), call.Body.ToString());
    }

    [TestMethod]
    public async Task GetTypeMembers_OfANonGenericTypeWithAGenericNamesake_ListsItsOwnMembers()
    {
        var call = await CallAsync("get_type_members", WithRepository("""{"symbol_fqn":"Library.Storage.Result"}"""), Solo);

        Assert.IsFalse(call.IsError, call.Body.ToString());
        CollectionAssert.AreEqual(new[] { "global::Library.Storage.Result.Ok" }, Names(call.Body), call.Body.ToString());
    }

    [TestMethod]
    public async Task BareTypeName_IsTheType_NotATypeParameterOfTheSameName()
    {
        var symbol = await CallAsync("find_symbol", WithRepository("""{"name":"Item"}"""), Solo);
        var references = await CallAsync("find_references", WithRepository("""{"symbol_fqn":"Item"}"""), Solo);

        Assert.IsFalse(symbol.IsError, symbol.Body.ToString());
        Assert.AreEqual("global::Library.Storage.Item", Names(symbol.Body).Single(), symbol.Body.ToString());
        Assert.AreEqual("class", symbol.Body.GetProperty("results")[0].GetProperty("kind").GetString());
        Assert.IsFalse(references.IsError, references.Body.ToString());
        var files = references.Body.GetProperty("results").EnumerateArray()
            .Select(r => Path.GetFileName(r.GetProperty("file_path").GetString()!))
            .Distinct()
            .ToArray();
        CollectionAssert.AreEqual(new[] { "Publisher.cs" }, files, references.Body.ToString());
    }

    [TestMethod]
    [DataRow(3000, 1)]
    [DataRow(0, 40)]
    public async Task OversizedOrTooDeeplyNestedInput_IsAnInvalidArgumentError(int length, int depth)
    {
        var name = length > 0
            ? "Library." + new string('a', length)
            : "Circle.Scale(" + string.Concat(Enumerable.Repeat("A<", depth)) + "B" + new string('>', depth) + ")";

        var call = await CallAsync("find_references", WithRepository($$"""{"symbol_fqn":{{JsonSerializer.Serialize(name)}}}"""), Solo);

        Assert.IsTrue(call.IsError, call.Body.ToString());
        Assert.AreEqual("invalid_argument", ErrorCode(call.Body));
    }

    // ==== loud failures, and honest empty answers =====================================================

    [TestMethod]
    [DataRow("find_references", """{"symbol_fqn":"Library.Shapes.Circel"}""", "global::Library.Shapes.Circle")]
    [DataRow("get_type_members", """{"symbol_fqn":"Library.Shape.IShape"}""", "global::Library.Shapes.IShape")]
    [DataRow("get_call_hierarchy", """{"symbol_fqn":"Circle.Aera","direction":"callers"}""", "global::Library.Shapes.Circle.Area()")]
    public async Task UnknownName_IsAToolError_WithTheClosestCandidates(string tool, string arguments, string closest)
    {
        var call = await CallAsync(tool, WithRepository(arguments), Solo);

        Assert.IsTrue(call.IsError, call.Body.ToString());
        Assert.AreEqual("symbol_not_found", ErrorCode(call.Body));
        var candidates = Candidates(call.Body);
        Assert.IsTrue(candidates.Count is > 0 and <= 5, call.Body.ToString());
        CollectionAssert.Contains(candidates, closest, call.Body.ToString());
        StringAssert.Contains(Message(call.Body), closest);
    }

    [TestMethod]
    public async Task TypeToolGivenAMethod_IsAToolError_NamingTheKind()
    {
        var call = await CallAsync("get_type_members", WithRepository("""{"symbol_fqn":"Library.Drawing.Canvas.Draw"}"""), Solo);

        Assert.IsTrue(call.IsError, call.Body.ToString());
        Assert.AreEqual("symbol_not_found", ErrorCode(call.Body));
        StringAssert.Contains(Message(call.Body), "No type matches");
    }

    [TestMethod]
    [DataRow("find_references", """{"symbol_fqn":"Library.Drawing.Canvas.Unused"}""", "no references to global::library.drawing.canvas.unused()")]
    [DataRow("get_implementors", """{"symbol_fqn":"Library.Drawing.Canvas"}""", "has no indexed implementors")]
    public async Task ValidSymbolWithAnEmptyAnswer_IsNotAnError_AndSaysSo(string tool, string arguments, string phrase)
    {
        var call = await CallAsync(tool, WithRepository(arguments), Solo);

        Assert.IsFalse(call.IsError, call.Body.ToString());
        Assert.AreEqual(0, call.Body.GetProperty("meta").GetProperty("result_count").GetInt32());
        StringAssert.Contains(Message(call.Body).ToLowerInvariant(), phrase, call.Body.ToString());
    }

    // ==== helpers ====================================================================================

    private static Task<ToolCall> CallAsync(string tool, string arguments, string sub) =>
        _host.CallAsync(tool, arguments, DelegateToken, _host.UserAssertion(sub: sub));

    private static string WithRepository(string arguments) => arguments[..^1] + ",\"repository\":\"acme/library\"}";

    private static List<string> Names(JsonElement body) =>
        body.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("fully_qualified_name").GetString()!).ToList();

    private static List<string> Candidates(JsonElement body) =>
        body.GetProperty("meta").TryGetProperty("candidates", out var candidates)
            ? candidates.EnumerateArray().Select(c => c.GetProperty("fully_qualified_name").GetString()!).ToList()
            : [];

    private static string Message(JsonElement body) =>
        body.TryGetProperty("message", out var message) ? message.GetString() ?? "" : "";

    private static async Task GrantAsync(string sub, string repository)
    {
        using var response = await _host.ControlAsync(HttpMethod.Put, "/control/grants/self", ControlToken,
            _host.UserAssertion(sub: sub), JsonSerializer.Serialize(new { repository }));
        Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    // Indexes the library with the service worker's own orchestrator step and publishes it as the default branch.
    private static void IndexLibrary(IndexDatabase db, string root)
    {
        var request = new EnsureSnapshotRequest
        {
            RepositoryRemoteUrl = Library, CommitSha = "commit-l1", BranchName = "main", IsDefaultBranch = true
        };
        var context = LocalIndexerSnapshotWorker.CreateSnapshotContext(request, capability: null);
        new IndexOrchestrator(db, useDocumentExtractor: true)
            .IndexSolutionAsync(LibrarySolution(root), snapshotContext: context).GetAwaiter().GetResult();
    }

    private static readonly (string Path, string Source)[] LibrarySources =
    [
        ("src/Library/Shapes/Shapes.cs", """
            namespace Library.Shapes
            {
                public interface IShape
                {
                    double Area();
                    string Name { get; }
                }

                public partial class Circle : IShape
                {
                    static Circle() { }
                    public Circle() : this(1) { }
                    public Circle(double radius) { Radius = radius; }
                    public double Radius { get; private set; }
                    public double Area() => 3.14 * Radius * Radius;
                    public string Name => "circle";
                    public void Scale(double factor) { Radius *= factor; }
                    public void Scale(double factor, bool round) { Scale(round ? System.Math.Round(factor) : factor); }
                }

                public sealed class UnitCircle : Circle { }

                public class Square : IShape
                {
                    public double Side;
                    public event System.EventHandler Changed;
                    public double Area() => Side * Side;
                    public string Name => "square";
                    public double this[int corner] => Side * corner;
                    public T Pick<T>(T value) => value;
                    public void Touch() => Changed?.Invoke(this, System.EventArgs.Empty);
                    public class Corner { public int Index; }
                }
            }
            """),
        ("src/Library/Shapes/Circle.Drawing.cs", """
            namespace Library.Shapes
            {
                public partial class Circle
                {
                    public string Describe() => Name + Area();
                }
            }
            """),
        ("src/Library/Styles/StyleKeys.cs", """
            namespace Library.Styles
            {
                public static class StyleKeys
                {
                    public const string Circle = "circle";
                }
            }
            """),
        ("src/Library/Drawing/Canvas.cs", """
            using Library.Shapes;

            namespace Library.Drawing
            {
                public class Canvas
                {
                    public double Total(IShape shape) => shape.Area();

                    public double Draw()
                    {
                        var circle = new Circle(2);
                        circle.Scale(2.0);
                        circle.Scale(3.0, true);
                        var square = new Square { Side = 1 };
                        return square.Area() + circle.Area() + Total(circle);
                    }

                    [System.Obsolete("use Draw")]
                    public void Old() { }

                    public void Unused() { }
                }
            }
            """),
        ("src/Library/Storage/Store.cs", """
            namespace Library.Storage
            {
                public interface IStore<T>
                {
                    System.Threading.Tasks.Task PublishAsync(T item, System.Threading.CancellationToken ct);
                }

                public sealed class MemoryStore<T> : IStore<T>
                {
                    public System.Threading.Tasks.Task PublishAsync(T item, System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.Task.CompletedTask;
                }

                public class Result { public bool Ok; }

                public class Result<T> : Result { public T Value; }

                public class Item { }

                public class Holder<Item> { public Item Held; }
            }
            """),
        ("src/Library/Storage/Publisher.cs", """
            namespace Library.Storage
            {
                public class Publisher
                {
                    public Item Stored = new Item();

                    public System.Threading.Tasks.Task Send(IStore<string> store) =>
                        store.PublishAsync("a", System.Threading.CancellationToken.None);

                    public Result Check(Result<int> typed) => typed;
                }
            }
            """)
    ];

    private static Solution LibrarySolution(string root)
    {
        var projectDir = Path.Combine(root, "src", "Library");
        Directory.CreateDirectory(projectDir);
        var projectPath = Path.Combine(projectDir, "Library.csproj");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId, VersionStamp.Default, "Library", "Library", LanguageNames.CSharp, filePath: projectPath,
            metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]));
        foreach (var (relative, source) in LibrarySources)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, source);
            solution = solution.AddDocument(
                DocumentId.CreateNewId(projectId), Path.GetFileName(path), SourceText.From(source), filePath: path);
        }
        return solution;
    }
}
