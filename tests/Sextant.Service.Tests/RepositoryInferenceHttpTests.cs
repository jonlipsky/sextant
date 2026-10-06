using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Sextant.Indexer;
using Sextant.Store;
using static Sextant.Service.Tests.CallerAssertionHttpTests;

namespace Sextant.Service.Tests;

/// <summary>
/// A symbol or path call that names no repository, from a caller that can read several, is answered from the ONE
/// readable repository that holds the symbol or path, and the answer says so (<c>meta.snapshot.repository_selection:
/// "inferred"</c>). When several readable repositories hold it the call is <c>repository_required</c> listing only those;
/// when none does it is today's <c>repository_required</c>. Inference never widens access: a repository the caller
/// cannot read is never probed, chosen or named, even when it is the only one that holds the symbol. The service runs
/// as in production (delegate token, verified caller assertion, <c>REQUIRE_REPOSITORY_SELECTION=true</c>, grants), and
/// every repository is indexed by the real orchestrator from C# source.
/// </summary>
[TestClass]
public class RepositoryInferenceHttpTests
{
    private const string Library = "https://github.com/acme/library";
    private const string Geometry = "https://github.com/acme/geometry";
    private const string Secret = "https://github.com/acme/secret";

    // Reader can read the library, geometry and widgets but NOT secret; Keeper can read secret and the library;
    // Solo can read only the library (the SVC-4 implicit selection).
    private const string Reader = "user-reader";
    private const string Keeper = "user-keeper";
    private const string Solo = "user-solo";

    private static Harness _host = null!;
    private static string _root = "";

    [ClassInitialize]
    public static async Task StartAsync(TestContext _)
    {
        _root = Path.Combine(Path.GetTempPath(), "sextant-inference-" + Guid.NewGuid().ToString("N"));
        _host = await Harness.StartAsync(
            configure: o => o with { RequireRepositorySelection = true },
            seed: db =>
            {
                Index(db, _root, Library, "commit-l1", LibrarySources);
                Index(db, _root, Geometry, "commit-g1", GeometrySources);
                Index(db, _root, Secret, "commit-s1", SecretSources);
            });
        await GrantAsync(Reader, Library);
        await GrantAsync(Reader, Geometry);
        await GrantAsync(Reader, Widgets);
        await GrantAsync(Keeper, Secret);
        await GrantAsync(Keeper, Library);
        await GrantAsync(Solo, Library);
        await GrantAsync(Reader, Secret, tenant: "tenant-b");
        await GrantAsync(Reader, Geometry, tenant: "tenant-b");
    }

    [ClassCleanup]
    public static async Task StopAsync()
    {
        await _host.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ==== one readable repository holds it ===========================================================

    [TestMethod]
    [DataRow("find_symbol", """{"name":"Library.Shapes.Circle"}""", "library")]
    [DataRow("find_references", """{"symbol_fqn":"Library.Shapes.IShape"}""", "library")]
    [DataRow("get_call_hierarchy", """{"symbol_fqn":"Geometry.Polygon.Perimeter","direction":"callers"}""", "geometry")]
    [DataRow("get_implementors", """{"symbol_fqn":"Library.Shapes.IShape"}""", "library")]
    [DataRow("get_type_hierarchy", """{"symbol_fqn":"Library.Shapes.Cube","direction":"up"}""", "library")]
    [DataRow("get_type_members", """{"symbol_fqn":"Geometry.Polygon"}""", "geometry")]
    public async Task OneReadableRepositoryHoldsTheSymbol_ItIsRead_AndTheAnswerSaysItWasInferred(
        string tool, string arguments, string repository)
    {
        var call = await CallAsync(tool, arguments, Reader);

        Assert.IsFalse(call.IsError, call.Body.ToString());
        Assert.IsTrue(call.Body.GetProperty("results").GetArrayLength() > 0, call.Body.ToString());
        AssertInferred(call.Body, repository);
        Assert.IsFalse(call.Body.GetProperty("meta").TryGetProperty("warning", out _), "an exact name carries no warning");
    }

    [TestMethod]
    public async Task OneReadableRepositoryHoldsThePath_GetFileSymbols_IsInferred()
    {
        var path = await PresentedPathAsync("Geometry.Polygon", Geometry);

        var call = await CallAsync("get_file_symbols", Json(new { file_path = path }), Reader);

        Assert.IsFalse(call.IsError, call.Body.ToString());
        CollectionAssert.Contains(Names(call.Body), "global::Geometry.Polygon", call.Body.ToString());
        AssertInferred(call.Body, "geometry");
    }

    [TestMethod]
    public async Task ExactHolder_OutranksARepositoryThatHoldsOnlyTheTrailingName()
    {
        // The library holds Library.Shapes.Circle (so 'Shapes.Circle' exactly); geometry holds only a Geometry.Circle,
        // which the trailing-name fallback would find.
        var call = await CallAsync("find_symbol", """{"name":"Shapes.Circle"}""", Reader);

        Assert.IsFalse(call.IsError, call.Body.ToString());
        Assert.AreEqual("global::Library.Shapes.Circle", Names(call.Body).Single(), call.Body.ToString());
        AssertInferred(call.Body, "library");
    }

    [TestMethod]
    public async Task OnlyATrailingNameHolder_IsInferred_AndTheAnswerWarnsAboutTheName()
    {
        var call = await CallAsync("find_symbol", """{"name":"Wrong.Place.Polygon"}""", Reader);

        Assert.IsFalse(call.IsError, call.Body.ToString());
        Assert.AreEqual("global::Geometry.Polygon", Names(call.Body).Single(), call.Body.ToString());
        AssertInferred(call.Body, "geometry");
        StringAssert.Contains(call.Body.GetProperty("meta").GetProperty("warning").GetString(), "global::Geometry.Polygon");
    }

    [TestMethod]
    public async Task AmbiguousNameHeldByOneRepository_IsInferred_AndAnswersWithItsAmbiguity()
    {
        // 'Area' is a homonym inside the library only, so the library is the repository; the tool then refuses to
        // pick one of its Area methods, exactly as an explicit call would.
        var call = await CallAsync("find_references", """{"symbol_fqn":"Area"}""", Reader);

        Assert.IsTrue(call.IsError, call.Body.ToString());
        Assert.AreEqual("ambiguous_symbol", ErrorCode(call.Body));
        var candidates = Candidates(call.Body);
        CollectionAssert.Contains(candidates, "global::Library.Shapes.Circle.Area()", call.Body.ToString());
        CollectionAssert.Contains(candidates, "global::Library.Shapes.IShape.Area()", call.Body.ToString());
    }

    // ==== several readable repositories hold it ======================================================

    [TestMethod]
    [DataRow("""{"name":"Polyg*","fuzzy":true}""", "geometry", "global::Geometry.Polygon")]
    [DataRow("""{"name":"IShape OR Perimeter","fuzzy":true,"kind":"interface"}""", "library", "global::Library.Shapes.IShape")]
    [DataRow("""{"name":"IShape OR Perimeter","fuzzy":true,"kind":"method"}""", "geometry", "global::Geometry.Polygon.Perimeter()")]
    [DataRow("""{"name":"IShape OR Perimeter","fuzzy":true,"kind":"type","scope":"all"}""", "library", "global::Library.Shapes.IShape")]
    public async Task FuzzySearch_OneReadableRepositoryHasScopedFtsMatches_IsInferred(
        string arguments, string repository, string symbol)
    {
        var call = await CallAsync("find_symbol", arguments, Reader);

        Assert.IsFalse(call.IsError, call.Body.ToString());
        CollectionAssert.Contains(Names(call.Body), symbol, call.Body.ToString());
        AssertInferred(call.Body, repository);
    }

    [TestMethod]
    [DataRow("find_symbol", """{"name":"Shared.Common.Clock"}""")]
    [DataRow("find_symbol", """{"name":"Circle","fuzzy":true}""")]
    [DataRow("find_symbol", """{"name":"IShape OR Polygon","fuzzy":true}""")]
    [DataRow("get_type_members", """{"symbol_fqn":"Shared.Common.Clock"}""")]
    [DataRow("find_references", """{"symbol_fqn":"Circle"}""")]
    public async Task SeveralReadableRepositoriesHoldIt_IsRepositoryRequired_ListingOnlyThem(string tool, string arguments)
    {
        var call = await CallAsync(tool, arguments, Reader);

        Assert.IsTrue(call.IsError, call.Body.ToString());
        Assert.AreEqual("repository_required", ErrorCode(call.Body));
        var message = Message(call.Body);
        StringAssert.Contains(message, "in 2 repositories");
        StringAssert.Contains(message, "'acme/library'");
        StringAssert.Contains(message, "'acme/geometry'");
        StringAssert.Contains(message, "'repository'");
        Assert.IsFalse(message.Contains("widgets", StringComparison.Ordinal), $"a readable non-holder is not listed: {message}");
        Assert.IsFalse(call.Body.ToString().Contains("secret", StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(0, call.Body.GetProperty("results").GetArrayLength());
    }

    [TestMethod]
    public async Task SeveralReadableRepositoriesHoldThePath_IsRepositoryRequired_ListingThem()
    {
        var inLibrary = await PresentedPathAsync("Shared.Common.Clock", Library);
        var inGeometry = await PresentedPathAsync("Shared.Common.Clock", Geometry);
        Assert.AreEqual(inLibrary, inGeometry, "both repositories hold the file at the same path");

        var call = await CallAsync("get_file_symbols", Json(new { file_path = inLibrary }), Reader);

        Assert.IsTrue(call.IsError, call.Body.ToString());
        Assert.AreEqual("repository_required", ErrorCode(call.Body));
        var message = Message(call.Body);
        StringAssert.Contains(message, "This file is in 2 repositories");
        StringAssert.Contains(message, "'acme/library'");
        StringAssert.Contains(message, "'acme/geometry'");
    }

    // ==== no readable repository holds it ============================================================

    [TestMethod]
    [DataRow("""{"name":"Nowhere.Missing"}""")]
    [DataRow("""{"name":"NoFtsCandidate*","fuzzy":true}""")]
    public async Task NoReadableRepositoryHoldsIt_IsTodaysRepositoryRequired_ListingTheCallersRepositories(string arguments)
    {
        var call = await CallAsync("find_symbol", arguments, Reader);

        Assert.IsTrue(call.IsError, call.Body.ToString());
        Assert.AreEqual("repository_required", ErrorCode(call.Body));
        var message = Message(call.Body);
        StringAssert.Contains(message, "'acme/library'");
        StringAssert.Contains(message, "'acme/geometry'");
        StringAssert.Contains(message, "'acme/widgets'");
        Assert.IsFalse(message.Contains("repositories this caller can read", StringComparison.Ordinal), message);
        Assert.IsFalse(call.Body.ToString().Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    // ==== CRITICAL: inference never widens access ====================================================

    [TestMethod]
    public async Task OnlyAnUnreadableRepositoryHoldsIt_ItIsNeverUsed_OrNamed()
    {
        // Secret.Vault.Key exists only in acme/secret, which Reader cannot read: the call is the same
        // repository_required a missing symbol gets, and nothing in it mentions the secret repository.
        var reader = await CallAsync("find_symbol", """{"name":"Secret.Vault.Key"}""", Reader);
        var missing = await CallAsync("find_symbol", """{"name":"Nowhere.Missing"}""", Reader);
        var references = await CallAsync("find_references", """{"symbol_fqn":"Secret.Vault.Key.Open"}""", Reader);

        foreach (var call in new[] { reader, references })
        {
            Assert.IsTrue(call.IsError, call.Body.ToString());
            Assert.AreEqual("repository_required", ErrorCode(call.Body));
            Assert.AreEqual(0, call.Body.GetProperty("results").GetArrayLength());
            Assert.IsFalse(call.Body.ToString().Contains("secret", StringComparison.OrdinalIgnoreCase), call.Body.ToString());
        }
        Assert.AreEqual(WithoutTimestamp(missing.Body), WithoutTimestamp(reader.Body),
            "a symbol only an unreadable repository holds reads exactly like a symbol nobody holds");

        // The control: a caller who CAN read acme/secret is answered from it.
        var keeper = await CallAsync("find_symbol", """{"name":"Secret.Vault.Key"}""", Keeper);
        Assert.IsFalse(keeper.IsError, keeper.Body.ToString());
        AssertInferred(keeper.Body, "secret");
    }

    [TestMethod]
    public async Task AReadableAndAnUnreadableRepositoryHoldIt_TheReadableOneIsInferred()
    {
        // Shared.Only.Mirror is in the library and in acme/secret. For Reader only the library counts.
        var reader = await CallAsync("find_symbol", """{"name":"Shared.Only.Mirror"}""", Reader);

        Assert.IsFalse(reader.IsError, reader.Body.ToString());
        AssertInferred(reader.Body, "library");
        Assert.IsFalse(reader.Body.ToString().Contains("secret", StringComparison.OrdinalIgnoreCase), reader.Body.ToString());

        // For Keeper, who can read both, it is held by two repositories.
        var keeper = await CallAsync("find_symbol", """{"name":"Shared.Only.Mirror"}""", Keeper);
        Assert.IsTrue(keeper.IsError, keeper.Body.ToString());
        Assert.AreEqual("repository_required", ErrorCode(keeper.Body));
        StringAssert.Contains(Message(keeper.Body), "'acme/secret'");
        StringAssert.Contains(Message(keeper.Body), "'acme/library'");
    }

    // ==== calls that are never inferred ==============================================================

    [TestMethod]
    public async Task FuzzySearch_GrantsOfAnotherTenantAreNeverUsed()
    {
        var arguments = """{"name":"Key","fuzzy":true}""";
        var reader = await CallAsync("find_symbol", arguments, Reader);
        Assert.IsTrue(reader.IsError, reader.Body.ToString());
        Assert.AreEqual("repository_required", ErrorCode(reader.Body));
        Assert.IsFalse(reader.Body.ToString().Contains("secret", StringComparison.OrdinalIgnoreCase));

        var otherTenant = await _host.CallAsync("find_symbol", arguments, DelegateToken,
            _host.UserAssertion(tenant: "tenant-b", sub: Reader));
        Assert.IsFalse(otherTenant.IsError, otherTenant.Body.ToString());
        AssertInferred(otherTenant.Body, "secret");
    }

    [TestMethod]
    public async Task FuzzySearch_UnreadableHolder_IsNeverUsedOrNamed()
    {
        var reader = await CallAsync("find_symbol", """{"name":"Key","fuzzy":true}""", Reader);
        var missing = await CallAsync("find_symbol", """{"name":"NoFtsCandidate*","fuzzy":true}""", Reader);

        Assert.IsTrue(reader.IsError, reader.Body.ToString());
        Assert.AreEqual("repository_required", ErrorCode(reader.Body));
        Assert.AreEqual(WithoutTimestamp(missing.Body), WithoutTimestamp(reader.Body));
        Assert.IsFalse(reader.Body.ToString().Contains("secret", StringComparison.OrdinalIgnoreCase));

        var keeper = await CallAsync("find_symbol", """{"name":"Key","fuzzy":true}""", Keeper);
        Assert.IsFalse(keeper.IsError, keeper.Body.ToString());
        AssertInferred(keeper.Body, "secret");
    }

    [TestMethod]
    public async Task FuzzySearch_UnreadableMatchDoesNotMakeReadableHolderAmbiguous()
    {
        var reader = await CallAsync("find_symbol", """{"name":"Mirror","fuzzy":true}""", Reader);

        Assert.IsFalse(reader.IsError, reader.Body.ToString());
        AssertInferred(reader.Body, "library");
        Assert.IsFalse(reader.Body.ToString().Contains("secret", StringComparison.OrdinalIgnoreCase));

        var keeper = await CallAsync("find_symbol", """{"name":"Mirror","fuzzy":true}""", Keeper);
        Assert.IsTrue(keeper.IsError, keeper.Body.ToString());
        Assert.AreEqual("repository_required", ErrorCode(keeper.Body));
        StringAssert.Contains(Message(keeper.Body), "'acme/secret'");
        StringAssert.Contains(Message(keeper.Body), "'acme/library'");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FuzzySearch_ExplicitRepositoryIsPreserved(bool header)
    {
        var before = _host.InferenceProbes();
        if (header)
            _host.RepositoryHeader = Geometry;
        try
        {
            var arguments = header
                ? """{"name":"IShape","fuzzy":true}"""
                : """{"name":"IShape","fuzzy":true,"repository":"acme/geometry"}""";
            var call = await CallAsync("find_symbol", arguments, Reader);

            Assert.IsFalse(call.IsError, call.Body.ToString());
            Assert.AreEqual(0, call.Body.GetProperty("results").GetArrayLength(), call.Body.ToString());
            var snapshot = call.Body.GetProperty("meta").GetProperty("snapshot");
            Assert.AreEqual("github.com/acme/geometry", snapshot.GetProperty("repository").GetString());
            Assert.IsFalse(snapshot.TryGetProperty("repository_selection", out _));
            Assert.AreEqual(before, _host.InferenceProbes(), "explicit selection is never probed");
        }
        finally
        {
            _host.RepositoryHeader = null;
        }
    }

    [TestMethod]
    public async Task TheOnlyReadableRepository_StaysTheImplicitSelection()
    {
        var call = await CallAsync("find_symbol", """{"name":"Library.Shapes.Circle"}""", Solo);

        Assert.IsFalse(call.IsError, call.Body.ToString());
        var snapshot = call.Body.GetProperty("meta").GetProperty("snapshot");
        Assert.AreEqual("github.com/acme/library", snapshot.GetProperty("repository").GetString());
        Assert.AreEqual("implicit", snapshot.GetProperty("repository_selection").GetString());
    }

    [TestMethod]
    public async Task ANamedRepository_IsNeverReplaced()
    {
        var call = await CallAsync("find_symbol", """{"name":"Library.Shapes.IShape","repository":"acme/widgets"}""", Reader);

        Assert.IsTrue(call.IsError, call.Body.ToString());
        Assert.AreEqual("symbol_not_found", ErrorCode(call.Body));
    }

    [TestMethod]
    public async Task ARepositoryNamedByTheHeader_IsNeverReplaced()
    {
        _host.RepositoryHeader = Widgets;
        try
        {
            var call = await CallAsync("find_symbol", """{"name":"Library.Shapes.IShape"}""", Reader);

            Assert.IsTrue(call.IsError, call.Body.ToString());
            Assert.AreEqual("symbol_not_found", ErrorCode(call.Body));
            var snapshot = call.Body.GetProperty("meta").GetProperty("snapshot");
            Assert.AreEqual("github.com/acme/widgets", snapshot.GetProperty("repository").GetString(), call.Body.ToString());
            Assert.IsFalse(snapshot.TryGetProperty("repository_selection", out _), call.Body.ToString());
        }
        finally
        {
            _host.RepositoryHeader = null;
        }
    }

    [TestMethod]
    [DataRow("""{"name":"Circle","fuzzy":true,"project_id":"unknown"}""")]
    [DataRow("""{"name":"Circle","fuzzy":true,"scope":"project:1"}""")]
    [DataRow("""{"name":"Circle","fuzzy":true,"branch":"main"}""")]
    [DataRow("""{"name":"Library.Shapes.Circle","branch":"main"}""")]
    [DataRow("""{"name":"Library.Shapes.Circle","scope":"project:1"}""")]
    public async Task ANarrowedFindSymbol_IsNotInferred(string arguments)
    {
        var call = await CallAsync("find_symbol", arguments, Reader);

        Assert.IsTrue(call.IsError, call.Body.ToString());
        Assert.AreEqual("repository_required", ErrorCode(call.Body));
    }

    // ==== helpers ====================================================================================

    private static void AssertInferred(JsonElement body, string repository)
    {
        var snapshot = body.GetProperty("meta").GetProperty("snapshot");
        Assert.AreEqual($"github.com/acme/{repository}", snapshot.GetProperty("repository").GetString(), body.ToString());
        Assert.AreEqual("inferred", snapshot.GetProperty("repository_selection").GetString(), body.ToString());
    }

    // The file path find_symbol presents for a type of an explicitly named repository.
    private static async Task<string> PresentedPathAsync(string type, string repository)
    {
        var call = await CallAsync("find_symbol",
            Json(new { name = type, repository = repository["https://github.com/".Length..] }), Reader);
        Assert.IsFalse(call.IsError, call.Body.ToString());
        return call.Body.GetProperty("results").EnumerateArray().Single().GetProperty("file_path").GetString()!;
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static Task<ToolCall> CallAsync(string tool, string arguments, string sub) =>
        _host.CallAsync(tool, arguments, DelegateToken, _host.UserAssertion(sub: sub));

    private static List<string> Names(JsonElement body) =>
        body.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("fully_qualified_name").GetString()!).ToList();

    private static List<string> Candidates(JsonElement body) =>
        body.GetProperty("meta").TryGetProperty("candidates", out var candidates)
            ? candidates.EnumerateArray().Select(c => c.GetProperty("fully_qualified_name").GetString()!).ToList()
            : [];

    private static string Message(JsonElement body) =>
        body.TryGetProperty("message", out var message) ? message.GetString() ?? "" : "";

    private static async Task GrantAsync(string sub, string repository, string tenant = "tenant-a")
    {
        using var response = await _host.ControlAsync(HttpMethod.Put, "/control/grants/self", ControlToken,
            _host.UserAssertion(tenant: tenant, sub: sub), JsonSerializer.Serialize(new { repository }));
        Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    // Indexes one repository with the service worker's own orchestrator step and publishes it as the default branch.
    // Every repository has the same layout (one project, Lib/Lib.csproj), so a file common to two of them is presented
    // at the same repository-relative path.
    internal static void Index(IndexDatabase db, string root, string url, string commit, (string Path, string Source)[] sources)
    {
        var request = new EnsureSnapshotRequest
        {
            RepositoryRemoteUrl = url, CommitSha = commit, BranchName = "main", IsDefaultBranch = true
        };
        var context = LocalIndexerSnapshotWorker.CreateSnapshotContext(request, capability: null);
        var checkout = Path.Combine(root, url[(url.LastIndexOf('/') + 1)..]);
        new IndexOrchestrator(db, useDocumentExtractor: true)
            .IndexSolutionAsync(Solution(checkout, sources), snapshotContext: context).GetAwaiter().GetResult();
    }

    private static Solution Solution(string checkout, (string Path, string Source)[] sources)
    {
        var projectDir = Path.Combine(checkout, "Lib");
        Directory.CreateDirectory(projectDir);
        var projectPath = Path.Combine(projectDir, "Lib.csproj");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution.AddProject(ProjectInfo.Create(
            projectId, VersionStamp.Default, "Lib", "Lib", LanguageNames.CSharp, filePath: projectPath,
            metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]));
        foreach (var (relative, source) in sources)
        {
            var path = Path.Combine(projectDir, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, source);
            solution = solution.AddDocument(
                DocumentId.CreateNewId(projectId), Path.GetFileName(path), SourceText.From(source), filePath: path);
        }
        return solution;
    }

    private const string Clock = """
        namespace Shared.Common
        {
            public class Clock
            {
                public long Now() => 0;
            }
        }
        """;

    private static readonly (string Path, string Source)[] LibrarySources =
    [
        ("Shapes/Shapes.cs", """
            namespace Library.Shapes
            {
                public interface IShape
                {
                    double Area();
                }

                public class Circle : IShape
                {
                    public double Area() => 3.14;
                    public double Twice() => Area() * 2;
                }

                public class Square : IShape
                {
                    public double Area() => 1;
                }

                public class Cube : Square { }
            }
            """),
        ("Common/Clock.cs", Clock),
        ("Only/Mirror.cs", """
            namespace Shared.Only
            {
                public class Mirror { }
            }
            """)
    ];

    private static readonly (string Path, string Source)[] GeometrySources =
    [
        ("Polygons/Polygon.cs", """
            namespace Geometry
            {
                public class Polygon
                {
                    public int Sides;
                    public double Perimeter() => Sides * 1.0;
                    public double Twice() => Perimeter() * 2;
                }

                public class Circle
                {
                    public double Radius;
                }
            }
            """),
        ("Common/Clock.cs", Clock)
    ];

    private static readonly (string Path, string Source)[] SecretSources =
    [
        ("Vault/Key.cs", """
            namespace Secret.Vault
            {
                public class Key
                {
                    public string Open() => "open";
                    public string Use() => Open();
                }
            }
            """),
        ("Only/Mirror.cs", """
            namespace Shared.Only
            {
                public class Mirror { }
            }
            """)
    ];
}
