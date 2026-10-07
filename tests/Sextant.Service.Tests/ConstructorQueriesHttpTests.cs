using System.Text.Json;
using static Sextant.Service.Tests.CallerAssertionHttpTests;

namespace Sextant.Service.Tests;

[TestClass]
public class ConstructorQueriesHttpTests
{
    private const string Library = "https://github.com/acme/constructors";
    private const string Other = "https://github.com/acme/other-constructors";
    private const string Reader = "constructor-reader";
    private static Harness _host = null!;
    private static string _root = "";

    private static readonly (string Path, string Source)[] Sources =
    [
        ("Widget.cs", """
            namespace Construction
            {
                public class Widget
                {
                    public Widget() : this(0) { }
                    public Widget(int value) { }
                    public Widget(string value) { }
                }
                public class Derived : Widget
                {
                    public Derived() : base(1) { }
                }
                public class Factory
                {
                    public Widget Empty() => new Widget();
                    public Widget Number() => new Construction.Widget(2);
                    public Widget Text() => new("text");
                    public Widget Field = new Widget(3);
                }
            }
            """),
        ("Generic.cs", """
            namespace Construction
            {
                public class Box<T>
                {
                    public Box(T value) { }
                }
                public class Boxes
                {
                    public Box<int> Make() => new Box<int>(1);
                }
            }
            """),
        ("Candidates.cs", """
            namespace Construction
            {
                public class Unbound
                {
                    public Unbound(int value) { }
                    public Unbound(string value) { }
                }
                public class Candidates
                {
                    public object Make() => new Unbound(new object());
                }
            }
            """)
    ];

    [ClassInitialize]
    public static async Task StartAsync(TestContext _)
    {
        _root = Path.Combine(Path.GetTempPath(), "sextant-constructors-" + Guid.NewGuid().ToString("N"));
        _host = await Harness.StartAsync(
            configure: o => o with { RequireRepositorySelection = true },
            seed: db =>
            {
                RepositoryInferenceHttpTests.Index(db, _root, Library, "constructors-1", Sources);
                RepositoryInferenceHttpTests.Index(db, _root, Other, "constructors-2", [Sources[0]]);
            });
        await GrantAsync(Reader, Library);
        await GrantAsync(Reader, Other);
        await GrantAsync("constructor-solo", Library);
    }

    [ClassCleanup]
    public static async Task StopAsync()
    {
        await _host.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [TestMethod]
    [DataRow("Construction.Widget.Widget()", new[] { 15 })]
    [DataRow("Construction.Widget.Widget(int)", new[] { 5, 11, 16, 18 })]
    [DataRow("M:Construction.Widget.#ctor(System.Int32)", new[] { 5, 11, 16, 18 })]
    [DataRow("Construction.Widget.Widget(string)", new[] { 17 })]
    public async Task References_SelectTheBoundOverload_IncludingInitializersAndTargetTypedNew(string symbol, int[] lines)
    {
        var call = await CallAsync("find_references", new { symbol_fqn = symbol, repository = "acme/constructors" });

        Assert.IsFalse(call.IsError, call.Body.ToString());
        CollectionAssert.AreEquivalent(lines, Rows(call.Body).Select(r => r.GetProperty("line").GetInt32()).ToArray(),
            call.Body.ToString());
        Assert.IsFalse(Rows(call.Body).Any(r => r.TryGetProperty("candidate", out _)), call.Body.ToString());
    }

    [TestMethod]
    [DataRow("Construction.Widget.Widget()", new[] { "global::Construction.Factory.Empty()" })]
    [DataRow("Construction.Widget.Widget(int)", new[]
    {
        "global::Construction.Widget.Widget()", "global::Construction.Derived.Derived()",
        "global::Construction.Factory.Number()"
    })]
    [DataRow("Construction.Widget.Widget(string)", new[] { "global::Construction.Factory.Text()" })]
    public async Task Callers_SelectTheBoundOverload_AndAttributeInitializersToTheConstructor(string symbol, string[] names)
    {
        var call = await CallAsync("get_call_hierarchy",
            new { symbol_fqn = symbol, direction = "callers", depth = 1, repository = "acme/constructors" });

        Assert.IsFalse(call.IsError, call.Body.ToString());
        CollectionAssert.AreEquivalent(names,
            Rows(call.Body).Select(r => r.GetProperty("fully_qualified_name").GetString()!).ToArray(), call.Body.ToString());
    }

    [TestMethod]
    public async Task Callees_IncludeTheExactConstructor_AndTransitiveInitializer()
    {
        var call = await CallAsync("get_call_hierarchy", new
        {
            symbol_fqn = "Construction.Factory.Empty", direction = "callees", depth = 2, repository = "acme/constructors"
        });

        Assert.IsFalse(call.IsError, call.Body.ToString());
        CollectionAssert.AreEquivalent(
            new[] { "global::Construction.Widget.Widget()", "global::Construction.Widget.Widget(int)" },
            Rows(call.Body).Select(r => r.GetProperty("fully_qualified_name").GetString()!).ToArray(), call.Body.ToString());
    }

    [TestMethod]
    public async Task AmbiguousConstructor_GuidanceNamesAnActionableOverload()
    {
        foreach (var tool in new[] { "find_references", "get_call_hierarchy" })
        {
            var call = await CallAsync(tool, new
            {
                symbol_fqn = "Construction.Widget.Widget", direction = "callers", repository = "acme/constructors"
            });
            Assert.IsTrue(call.IsError, call.Body.ToString());
            Assert.AreEqual("ambiguous_symbol", ErrorCode(call.Body));
            StringAssert.Contains(call.Body.GetProperty("message").GetString(), "constructor overload");
            var candidates = call.Body.GetProperty("meta").GetProperty("candidates").EnumerateArray().ToArray();
            Assert.AreEqual(3, candidates.Length, call.Body.ToString());
            foreach (var candidate in candidates)
            {
                var exact = await CallAsync(tool, new
                {
                    symbol_fqn = candidate.GetProperty("fully_qualified_name").GetString(),
                    direction = "callers", depth = 1, repository = "acme/constructors"
                });
                Assert.IsFalse(exact.IsError, exact.Body.ToString());
                Assert.IsTrue(Rows(exact.Body).Length > 0, exact.Body.ToString());
            }
        }
    }

    [TestMethod]
    public async Task GenericConstructor_RoundTripsFromDeclaration_AndInfersOnlyItsRepository()
    {
        var declaration = await CallAsync("find_symbol",
            new { name = "M:Construction.Box`1.#ctor(`0)", repository = "acme/constructors" });
        Assert.IsFalse(declaration.IsError, declaration.Body.ToString());
        var name = Rows(declaration.Body).Single().GetProperty("fully_qualified_name").GetString();

        foreach (var tool in new[] { "find_references", "get_call_hierarchy" })
        {
            var call = await CallAsync(tool, new { symbol_fqn = name, direction = "callers", depth = 1 });
            Assert.IsFalse(call.IsError, call.Body.ToString());
            Assert.AreEqual(1, Rows(call.Body).Length, call.Body.ToString());
            var snapshot = call.Body.GetProperty("meta").GetProperty("snapshot");
            Assert.AreEqual("github.com/acme/constructors", snapshot.GetProperty("repository").GetString());
            Assert.AreEqual("inferred", snapshot.GetProperty("repository_selection").GetString());
        }
    }

    [TestMethod]
    public async Task ConstructorInTwoReadableRepositories_RemainsExplicitlyAmbiguous()
    {
        var call = await CallAsync("find_references", new { symbol_fqn = "Construction.Widget.Widget(int)" });

        Assert.IsTrue(call.IsError, call.Body.ToString());
        Assert.AreEqual("repository_required", ErrorCode(call.Body));
        var message = call.Body.GetProperty("message").GetString();
        StringAssert.Contains(message, "'acme/constructors'");
        StringAssert.Contains(message, "'acme/other-constructors'");
    }

    [TestMethod]
    public async Task ConstructorReferences_HonorScope_AndCannotReadAnUngrantableRepository()
    {
        var scoped = await CallAsync("find_references", new
        {
            symbol_fqn = "Construction.Widget.Widget(int)", repository = "acme/constructors", scope = "file:Generic.cs"
        });
        Assert.IsFalse(scoped.IsError, scoped.Body.ToString());
        Assert.AreEqual(0, Rows(scoped.Body).Length, scoped.Body.ToString());
        var matching = await CallAsync("find_references", new
        {
            symbol_fqn = "Construction.Widget.Widget(int)", repository = "acme/constructors", scope = "file:Widget.cs"
        });
        Assert.IsFalse(matching.IsError, matching.Body.ToString());
        Assert.AreEqual(4, Rows(matching.Body).Length, matching.Body.ToString());

        var denied = await _host.CallAsync("find_references", JsonSerializer.Serialize(new
        {
            symbol_fqn = "Construction.Widget.Widget(int)", repository = "acme/other-constructors"
        }), DelegateToken, _host.UserAssertion(sub: "constructor-solo"));
        Assert.IsTrue(denied.IsError, denied.Body.ToString());
        Assert.AreEqual("repository_not_found", ErrorCode(denied.Body));
    }

    [TestMethod]
    public async Task UnboundCreation_ReportsEachOverloadAsCandidate_ThroughBothQueryTools()
    {
        foreach (var symbol in new[] { "Construction.Unbound.Unbound(int)", "Construction.Unbound.Unbound(string)" })
        {
            foreach (var tool in new[] { "find_references", "get_call_hierarchy" })
            {
                var call = await CallAsync(tool, new
                {
                    symbol_fqn = symbol, direction = "callers", depth = 1, repository = "acme/constructors"
                });
                Assert.IsFalse(call.IsError, call.Body.ToString());
                Assert.IsTrue(Rows(call.Body).Single().GetProperty("candidate").GetBoolean(), call.Body.ToString());
            }
        }
    }

    [TestMethod]
    public async Task TypeReferences_StillIncludeEveryObjectCreation()
    {
        var call = await CallAsync("find_references",
            new { symbol_fqn = "Construction.Widget", repository = "acme/constructors" });
        Assert.IsFalse(call.IsError, call.Body.ToString());
        CollectionAssert.AreEquivalent(new[] { 15, 16, 17, 18 }, Rows(call.Body)
            .Where(r => r.GetProperty("reference_kind").GetString() == "objectcreation")
            .Select(r => r.GetProperty("line").GetInt32()).ToArray(), call.Body.ToString());
    }

    private static JsonElement[] Rows(JsonElement body) => body.GetProperty("results").EnumerateArray().ToArray();

    private static Task<ToolCall> CallAsync(string tool, object arguments) =>
        _host.CallAsync(tool, JsonSerializer.Serialize(arguments), DelegateToken, _host.UserAssertion(sub: Reader));

    private static async Task GrantAsync(string sub, string repository)
    {
        using var response = await _host.ControlAsync(HttpMethod.Put, "/control/grants/self", ControlToken,
            _host.UserAssertion(sub: sub), JsonSerializer.Serialize(new { repository }));
        Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}
