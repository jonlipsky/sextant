using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sextant.Core;
using Sextant.Indexer;
using SymbolKind = Sextant.Core.SymbolKind;

namespace Sextant.Indexer.Tests;

[TestClass]
public class SemanticSymbolKeyTests
{
    private static Project CreateProject(params (string fileName, string source)[] documents)
    {
        var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var references = new MetadataReference[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
        };

        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();

        var solution = workspace.CurrentSolution
            .AddProject(projectId, "TestProject", "TestAssembly", LanguageNames.CSharp)
            .WithProjectCompilationOptions(projectId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .WithProjectMetadataReferences(projectId, references);

        foreach (var (fileName, source) in documents)
        {
            var documentId = DocumentId.CreateNewId(projectId);
            solution = solution.AddDocument(documentId, fileName, source, filePath: fileName);
        }

        return solution.GetProject(projectId)!;
    }

    // A single source exercising every collision case that the display FQN collapses:
    // same member name across types, overloads, constructors, generics, explicit interface
    // implementations, and records with primary constructors.
    private const string CollisionSource = """
        namespace Collide
        {
            public class AlphaService
            {
                public AlphaService() { }
                public AlphaService(int seed) { }
                public int Handle(int id) => id + 1;
                public string Handle(string name) => name + "!";
            }

            public class BetaService
            {
                public int Handle(int id) => id - 1;
                public string Handle(string name) => name + "?";
            }

            public class Calculator
            {
                public int Add(int a, int b) => a + b;
                public double Add(double a, double b) => a + b;
                public int Add(int a, int b, int c) => a + b + c;
            }

            public class Box<T>
            {
                public T? Value { get; set; }
                public TOut Map<TOut>(System.Func<T, TOut> f) => f(Value!);
            }

            public interface ILeft { void Run(); }
            public interface IRight { void Run(); }

            public class Worker : ILeft, IRight
            {
                void ILeft.Run() { }
                void IRight.Run() { }
            }

            public record Point(int X, int Y);
        }
        """;

    [TestMethod]
    public async Task DisplayFqnCollides_ButSymbolKeyIsDistinct()
    {
        var project = CreateProject(("Collide.cs", CollisionSource));
        var symbols = await SymbolExtractor.ExtractFromProjectAsync(project, 1);

        // The display FQN really does collide: at least one FQN is shared by several symbols.
        var fqnCollisions = symbols
            .GroupBy(s => s.FullyQualifiedName)
            .Where(g => g.Count() > 1)
            .ToList();
        Assert.IsTrue(fqnCollisions.Count > 0, "Expected the display FQN to collide for overloads/same-named members.");

        // But no two distinct declarations share a symbol key: within every colliding FQN group the
        // keys are all distinct.
        foreach (var group in fqnCollisions)
        {
            var keys = group.Select(s => s.SymbolKey).ToList();
            CollectionAssert.AllItemsAreUnique(keys,
                $"Symbols sharing FQN '{group.Key}' must have distinct symbol keys.");
        }
    }

    [TestMethod]
    public async Task Overloads_GetDistinctKeys()
    {
        var project = CreateProject(("Collide.cs", CollisionSource));
        var symbols = await SymbolExtractor.ExtractFromProjectAsync(project, 1);

        var adds = symbols.Where(s => s.DisplayName == "Add").Select(s => s.SymbolKey).ToList();
        Assert.AreEqual(3, adds.Count);
        CollectionAssert.AllItemsAreUnique(adds);
    }

    [TestMethod]
    public async Task SameMemberNameAcrossTypes_GetDistinctKeys()
    {
        var project = CreateProject(("Collide.cs", CollisionSource));
        var symbols = await SymbolExtractor.ExtractFromProjectAsync(project, 1);

        var alphaHandleInt = symbols.Single(s =>
            s.SymbolKey.Contains("AlphaService.Handle") && s.SymbolKey.Contains("System.Int32"));
        var betaHandleInt = symbols.Single(s =>
            s.SymbolKey.Contains("BetaService.Handle") && s.SymbolKey.Contains("System.Int32"));

        Assert.AreNotEqual(alphaHandleInt.SymbolKey, betaHandleInt.SymbolKey);
    }

    [TestMethod]
    public async Task Constructors_GetDistinctKeys()
    {
        var project = CreateProject(("Collide.cs", CollisionSource));
        var symbols = await SymbolExtractor.ExtractFromProjectAsync(project, 1);

        var ctors = symbols
            .Where(s => s.Kind == SymbolKind.Constructor && s.SymbolKey.Contains("AlphaService"))
            .Select(s => s.SymbolKey)
            .ToList();
        Assert.AreEqual(2, ctors.Count, "Both AlphaService constructors should be indexed.");
        CollectionAssert.AllItemsAreUnique(ctors);
    }

    [TestMethod]
    public async Task ExplicitInterfaceImplementations_GetDistinctKeys()
    {
        var project = CreateProject(("Collide.cs", CollisionSource));
        var symbols = await SymbolExtractor.ExtractFromProjectAsync(project, 1);

        var runs = symbols
            .Where(s => s.DisplayName.Contains("Run"))
            .Select(s => s.SymbolKey)
            .ToList();
        Assert.IsTrue(runs.Count >= 2, "Both explicit Run implementations should be indexed.");
        CollectionAssert.AllItemsAreUnique(runs);
    }

    [TestMethod]
    public async Task GenericTypeAndMethod_ProduceKeys()
    {
        var project = CreateProject(("Collide.cs", CollisionSource));
        var symbols = await SymbolExtractor.ExtractFromProjectAsync(project, 1);

        var box = symbols.Single(s => s.DisplayName == "Box" && s.Kind == SymbolKind.Class);
        StringAssert.StartsWith(box.SymbolKey, "T:", "Generic type should use a documentation-ID key.");
        StringAssert.Contains(box.SymbolKey, "`1", "Generic arity should be encoded in the key.");

        var map = symbols.Single(s => s.DisplayName == "Map");
        StringAssert.StartsWith(map.SymbolKey, "M:", "Generic method should use a documentation-ID key.");
    }

    [TestMethod]
    public async Task RecordType_ProducesStableDocIdKey()
    {
        var project = CreateProject(("Collide.cs", CollisionSource));
        var symbols = await SymbolExtractor.ExtractFromProjectAsync(project, 1);

        var point = symbols.Single(s => s.DisplayName == "Point" && s.Kind == SymbolKind.Record);
        StringAssert.StartsWith(point.SymbolKey, "T:", "A record type should use a documentation-ID key.");
        StringAssert.Contains(point.SymbolKey, "Point");

        // The record's key is unique among all extracted symbols.
        Assert.AreEqual(1, symbols.Count(s => s.SymbolKey == point.SymbolKey));
    }

    [TestMethod]
    public async Task RepeatedExtraction_ProducesIdenticalKeys()
    {
        var first = await SymbolExtractor.ExtractFromProjectAsync(
            CreateProject(("Collide.cs", CollisionSource)), 1);
        var second = await SymbolExtractor.ExtractFromProjectAsync(
            CreateProject(("Collide.cs", CollisionSource)), 1);

        var firstKeys = first.Select(s => s.SymbolKey).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var secondKeys = second.Select(s => s.SymbolKey).OrderBy(k => k, StringComparer.Ordinal).ToList();

        CollectionAssert.AreEqual(firstKeys, secondKeys, "Keys must be deterministic across extractions.");
    }

    [TestMethod]
    public async Task AnonymousObjectProperties_AreNotTopLevelSymbols()
    {
        var source = """
            namespace Anon
            {
                public class Api
                {
                    public object Describe() => new { type = "widget", description = "a thing" };
                }
            }
            """;

        var project = CreateProject(("Anon.cs", source));
        var symbols = await SymbolExtractor.ExtractFromProjectAsync(project, 1);

        Assert.IsFalse(symbols.Any(s => s.DisplayName == "type"),
            "Anonymous-object property 'type' must not become a top-level symbol.");
        Assert.IsFalse(symbols.Any(s => s.DisplayName == "description"),
            "Anonymous-object property 'description' must not become a top-level symbol.");
        Assert.IsTrue(symbols.Any(s => s.DisplayName == "Describe"),
            "The real method must still be indexed.");
    }

    [TestMethod]
    public async Task SourceFallbackKey_IsRepoRelativeAndDeterministic()
    {
        const string source = """
            namespace F
            {
                public class Box<T>
                {
                    public T? Value { get; set; }
                }
            }
            """;

        var keyFirst = await TypeParameterKey(source);
        var keySecond = await TypeParameterKey(source);

        StringAssert.StartsWith(keyFirst, "src:",
            "A doc-ID-less type parameter must use the versioned source-declaration fallback.");
        StringAssert.Contains(keyFirst, "Fallback.cs", "The fallback key embeds the declaring source file.");
        StringAssert.EndsWith(keyFirst, ":T", "The fallback key embeds the metadata name.");
        Assert.IsFalse(Path.IsPathRooted(keyFirst.Split(':')[2]),
            "The file component must be repo-relative, not an absolute path.");
        Assert.AreEqual(keyFirst, keySecond,
            "The source-declaration fallback key must be deterministic across extractions.");
    }

    private static async Task<string> TypeParameterKey(string source)
    {
        var project = CreateProject(("Fallback.cs", source));
        var compilation = await project.GetCompilationAsync();
        var tree = compilation!.SyntaxTrees.Single();
        var model = compilation.GetSemanticModel(tree);
        var typeParam = (await tree.GetRootAsync())
            .DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeParameterSyntax>()
            .Single();
        var symbol = model.GetDeclaredSymbol(typeParam)!;
        return SemanticSymbolKeyFactory.DeclarationKey(symbol);
    }

    [TestMethod]
    public async Task RefOverload_GetsDistinctKeyFromByValue()
    {
        // ref/out cannot be overloaded together in C#, but by-value vs. by-ref is a legal overload
        // pair whose keys must differ. (A doc ID encodes by-ref as '@'; the metadata fallback encodes
        // the RefKind explicitly — see SemanticSymbolKeyFactory.ParameterSignature.)
        const string source = """
            namespace R
            {
                public class Mutator
                {
                    public void Apply(int value) { }
                    public void Apply(ref int value) { value++; }
                }
            }
            """;

        var project = CreateProject(("Ref.cs", source));
        var symbols = await SymbolExtractor.ExtractFromProjectAsync(project, 1);

        var applies = symbols.Where(s => s.DisplayName == "Apply").Select(s => s.SymbolKey).ToList();
        Assert.AreEqual(2, applies.Count, "Both overloads should be indexed.");
        CollectionAssert.AllItemsAreUnique(applies,
            "An overload differing by ref must get a distinct key from the by-value overload.");
    }

    [TestMethod]
    public async Task PartialType_ResolvesToOneStableKey()
    {
        var part1 = """
            namespace P
            {
                public partial class Widget
                {
                    public int Width { get; set; }
                }
            }
            """;
        var part2 = """
            namespace P
            {
                public partial class Widget
                {
                    public int Height { get; set; }
                }
            }
            """;

        var project = CreateProject(("Widget.Part1.cs", part1), ("Widget.Part2.cs", part2));
        var symbols = await SymbolExtractor.ExtractFromProjectAsync(project, 1);

        var widgetKeys = symbols
            .Where(s => s.DisplayName == "Widget" && s.Kind == SymbolKind.Class)
            .Select(s => s.SymbolKey)
            .Distinct()
            .ToList();

        Assert.AreEqual(1, widgetKeys.Count,
            "Both partial declarations must map to a single stable key.");
    }

    [TestMethod]
    public void SemanticSymbolKey_IsProjectAndFrameworkScoped()
    {
        var keyA = new SemanticSymbolKey
        {
            ProjectCanonicalId = "git@x:repo|src/Lib1",
            TargetFramework = "net10.0",
            DeclarationKey = "T:Ns.Type"
        };
        var keyB = keyA with { ProjectCanonicalId = "git@x:repo|src/Lib2" };
        var keyC = keyA with { TargetFramework = "netstandard2.0" };

        Assert.AreNotEqual(keyA.Value, keyB.Value, "Different projects must yield different identities.");
        Assert.AreNotEqual(keyA.Value, keyC.Value, "Different target frameworks must yield different identities.");
        Assert.AreEqual(keyA, keyA with { }, "Identical inputs must be equal.");
    }
}

[TestClass]
public class SymbolCatalogTests
{
    [TestMethod]
    public void Resolve_PrefersRequestingProject()
    {
        var catalog = new SymbolCatalog();
        catalog.Add(projectId: 1, "M:Ns.Type.M", symbolId: 10);
        catalog.Add(projectId: 2, "M:Ns.Type.M", symbolId: 20);

        var fromProject2 = catalog.Resolve("M:Ns.Type.M", preferProjectId: 2);
        Assert.IsTrue(fromProject2.Found);
        Assert.AreEqual(20, fromProject2.SymbolId);
        Assert.IsFalse(fromProject2.Ambiguous, "A same-project match is never ambiguous.");
    }

    [TestMethod]
    public void Resolve_FlagsAmbiguityAcrossProjects()
    {
        var catalog = new SymbolCatalog();
        catalog.Add(projectId: 3, "M:Ns.Type.M", symbolId: 30);
        catalog.Add(projectId: 5, "M:Ns.Type.M", symbolId: 50);

        // Requesting project has no match, and the key exists in several other projects.
        var resolution = catalog.Resolve("M:Ns.Type.M", preferProjectId: 99);
        Assert.IsTrue(resolution.Found);
        Assert.IsTrue(resolution.Ambiguous);
        Assert.AreEqual(30, resolution.SymbolId, "Ambiguous resolution is deterministic (lowest project id).");
    }

    [TestMethod]
    public void Resolve_SingleMatchIsNotAmbiguous()
    {
        var catalog = new SymbolCatalog();
        catalog.Add(projectId: 7, "M:Ns.Type.M", symbolId: 70);

        var resolution = catalog.Resolve("M:Ns.Type.M", preferProjectId: 99);
        Assert.IsTrue(resolution.Found);
        Assert.IsFalse(resolution.Ambiguous);
        Assert.AreEqual(70, resolution.SymbolId);
    }

    [TestMethod]
    public void Resolve_UnknownKeyIsNotFound()
    {
        var catalog = new SymbolCatalog();
        var resolution = catalog.Resolve("M:Ns.Missing", preferProjectId: 1);
        Assert.IsFalse(resolution.Found);
    }

    [TestMethod]
    public void Add_SameKeyAndProjectKeepsLatestId()
    {
        var catalog = new SymbolCatalog();
        catalog.Add(projectId: 1, "T:Ns.Type", symbolId: 1);
        catalog.Add(projectId: 1, "T:Ns.Type", symbolId: 2);

        Assert.IsTrue(catalog.TryResolve("T:Ns.Type", 1, out var id));
        Assert.AreEqual(2, id, "A re-added (project, key) mirrors the DB upsert and keeps one row.");
    }

    [TestMethod]
    public void TryResolveEdge_BindsAmbiguousDeterministicallyAndCounts()
    {
        var catalog = new SymbolCatalog();
        catalog.Add(projectId: 3, "M:Ns.Type.M", symbolId: 30);
        catalog.Add(projectId: 5, "M:Ns.Type.M", symbolId: 50);

        // Requesting project has no match and the key is defined in two others: the edge is bound to
        // the deterministic pick (lowest project id) so the graph stays complete, and the ambiguous
        // binding is counted for diagnostics.
        Assert.IsTrue(catalog.TryResolveEdge("M:Ns.Type.M", preferProjectId: 99, out var id));
        Assert.AreEqual(30, id, "The deterministic pick is the lowest project id.");
        Assert.AreEqual(1, catalog.AmbiguousEdgeBindings);
    }

    [TestMethod]
    public void TryResolveEdge_ResolvesSameProjectEdgeWithoutCounting()
    {
        var catalog = new SymbolCatalog();
        catalog.Add(projectId: 3, "M:Ns.Type.M", symbolId: 30);
        catalog.Add(projectId: 5, "M:Ns.Type.M", symbolId: 50);

        // A same-project target (the common case, e.g. an intra-compilation call) resolves exactly
        // even when the key also exists elsewhere, and never counts as an ambiguous binding.
        Assert.IsTrue(catalog.TryResolveEdge("M:Ns.Type.M", preferProjectId: 5, out var id));
        Assert.AreEqual(50, id);
        Assert.AreEqual(0, catalog.AmbiguousEdgeBindings);
    }

    [TestMethod]
    public void TryResolveExact_BindsExactProjectAndNeverCounts()
    {
        var catalog = new SymbolCatalog();
        // The same declaration key registered under two projects models a multi-targeted dependency
        // whose two TFMs are both indexed (projects 3 and 5 hold the same logical symbol).
        catalog.Add(projectId: 3, "T:Dep.Foo", symbolId: 30);
        catalog.Add(projectId: 5, "T:Dep.Foo", symbolId: 50);

        // Key-only edge resolution from a consumer that declares neither is ambiguous: it picks the
        // lowest project id (30) and counts the ambiguity.
        Assert.IsTrue(catalog.TryResolveEdge("T:Dep.Foo", preferProjectId: 99, out var ambiguousId));
        Assert.AreEqual(30, ambiguousId, "Key-only resolution picks the lowest project id.");
        Assert.AreEqual(1, catalog.AmbiguousEdgeBindings);

        // Compilation-scoped exact resolution binds the EXACT bound TFM's row (the consumer bound
        // project 5, not the lowest-id project 3) and must NOT bump the ambiguity counter.
        Assert.IsTrue(catalog.TryResolveExact("T:Dep.Foo", targetProjectId: 5, out var exactId));
        Assert.AreEqual(50, exactId, "Exact resolution returns the bound TFM's row, not the lowest id.");
        Assert.AreEqual(1, catalog.AmbiguousEdgeBindings, "Exact resolution never counts an ambiguity.");

        Assert.IsTrue(catalog.TryResolveExact("T:Dep.Foo", targetProjectId: 3, out var otherExactId));
        Assert.AreEqual(30, otherExactId);
    }

    [TestMethod]
    public void TryResolveExact_MissesWhenKeyNotInThatProject()
    {
        var catalog = new SymbolCatalog();
        catalog.Add(projectId: 3, "T:Dep.Foo", symbolId: 30);

        // The key is not registered in project 9 (e.g. an excluded target or a project outside the
        // indexed set), so exact resolution reports a miss and the caller falls back to key resolution.
        Assert.IsFalse(catalog.TryResolveExact("T:Dep.Foo", targetProjectId: 9, out var id));
        Assert.AreEqual(0, id);
        Assert.AreEqual(0, catalog.AmbiguousEdgeBindings, "A miss is not an ambiguous binding.");

        // Unknown key also misses.
        Assert.IsFalse(catalog.TryResolveExact("T:Dep.Missing", targetProjectId: 3, out _));
    }
}
