using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sextant.Indexer;

namespace Sextant.Indexer.Tests;

/// <summary>
/// Issue #269: the symbol walk no longer asks the semantic model about declarations inside executable bodies (which
/// binds the body), yet must emit exactly what the full walk did, in the same order, at every parallelism. The oracle
/// is the full walk as it was before the change, kept here verbatim.
/// </summary>
[TestClass]
public sealed class SymbolWalkEquivalenceTests
{
    private const string Library = """
        using System;
        using System.Collections.Generic;
        using System.Linq;

        namespace Walk;

        /// <summary>A record with a <c>primary</c> constructor.</summary>
        public record Point(int X, int Y)
        {
            public static Point Origin { get; } = new(0, 0);
            public int Sum => X + Y;
        }

        public enum Level { Low = 1, High = Low << 3 }

        public delegate T Factory<T>(int seed) where T : class;

        public interface IShape { double Area { get; } event EventHandler? Changed; }

        public abstract class Base<TItem> : IShape
        {
            private readonly Func<int, int> _twice = x => { int Inner<TLocal>(TLocal v) => x; return Inner<string>("a") * 2; };
            protected Base(int seed) { }
            protected Base() : this(Pick(() => { static int Nested<TN>() => 1; return Nested<int>(); })) { }
            public abstract double Area { get; }
            public event EventHandler? Changed;
            public TItem this[int index] => default!;
            protected static int Pick(Func<int> f, int fallback = 3) => f() + fallback;
            public virtual IEnumerable<object> Query(IEnumerable<int> source)
            {
                var anon = new { Name = "n", Count = 1 };
                (int A, string B) tuple = (1, "b");
                IEnumerable<TBody> Local<TBody, UBody>(IEnumerable<TBody> items) where UBody : struct => items;
                foreach (var item in from s in source let doubled = s * 2 where doubled > 1 select doubled)
                {
                    if (item is int matched && matched > anon.Count) { }
                }
                try { } catch (InvalidOperationException ex) when (ex.Message.Length > tuple.A) { }
                return Local<object, int>(source.Select(s => (object)new { Value = s }));
            }
            protected virtual void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
            ~Base() { }
            public static implicit operator string(Base<TItem> b) => "";
            public static Base<TItem> operator +(Base<TItem> a, Base<TItem> b) => a;
            public class Inner { public const int Size = 4; public int this[string key] { get => 0; set { } } }
        }

        public sealed partial class Square(double side) : Base<string>(1)
        {
            public override double Area => side * side;
            public override IEnumerable<object> Query(IEnumerable<int> source) => base.Query(source);
            partial class Nested { }
        }

        public static class Extensions
        {
            public static T Echo<T>(this T value) where T : notnull => value;
        }

        public struct Pair<TKey, TValue> { public TKey Key; public TValue Value; }
        """;

    private const string Program = """
        using System;

        int Add<T>(T a, int b) => b;
        var sum = Add<string>("x", 2);
        Console.WriteLine(sum);

        static T Identity<T>(T value) => value;

        partial class Program { public static int Helper() => 1; }
        """;

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(8)]
    public async Task TheBodySkippingWalk_EmitsExactlyWhatTheFullWalkDid_InOrder(int parallelism)
    {
        foreach (var project in new[]
                 {
                     CreateProject(OutputKind.DynamicallyLinkedLibrary, ("Walk.cs", Library), ("More.cs", "namespace Walk; public sealed partial class Square { public int Extra => 1; }")),
                     CreateProject(OutputKind.ConsoleApplication, ("Program.cs", Program))
                 })
        {
            var errors = (await project.GetCompilationAsync())!.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()).ToList();
            Assert.IsEmpty(errors, $"the {project.Name} fixture compiles cleanly:\n{string.Join("\n", errors)}");
            var expected = await FullWalkAsync(project);
            var (actual, available) = await SymbolExtractor.ExtractFromProjectWithStatusAsync(project, 1, maxParallelism: parallelism);

            Assert.IsTrue(available);
            CollectionAssert.AreEqual(expected, actual.Select(s => Canonical(s)).ToList(),
                $"{project.Name} at parallelism {parallelism}");
        }
    }

    [TestMethod]
    public async Task ALocalFunctionsTypeParameters_AreStillIndexed()
    {
        var project = CreateProject(OutputKind.DynamicallyLinkedLibrary, ("Walk.cs", Library));

        var (symbols, _) = await SymbolExtractor.ExtractFromProjectWithStatusAsync(project, 1);

        var typeParameters = symbols.Where(s => s.Kind == Sextant.Core.SymbolKind.TypeParameter).Select(s => s.DisplayName).ToList();
        CollectionAssert.IsSubsetOf(new[] { "TLocal", "TN", "TBody", "UBody" }, typeParameters,
            "local functions' type parameters, in a field initializer, a constructor initializer and a method body");
    }

    private static string Canonical(Sextant.Core.SymbolInfo s, string? filePath = null) => string.Join("|",
        s.SymbolKey, s.FullyQualifiedName, s.DisplayName, s.Kind, s.Accessibility, s.IsStatic, s.IsAbstract, s.IsVirtual,
        s.IsOverride, s.Signature, s.SignatureHash, s.Declaration, s.DocComment, filePath ?? s.FilePath, s.LineStart, s.LineEnd,
        s.Attributes);

    // The symbol walk before issue #269, verbatim apart from the dropped timestamp: every node, asked of the semantic
    // model, in pre-order.
    private static async Task<List<string>> FullWalkAsync(Project project)
    {
        var compilation = (await project.GetCompilationAsync())!;
        var symbols = new List<string>();
        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            if (SymbolExtractor.IsGeneratedFile(syntaxTree.FilePath))
                continue;
            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            var root = await syntaxTree.GetRootAsync();
            foreach (var node in root.DescendantNodes())
            {
                var declaredSymbol = semanticModel.GetDeclaredSymbol(node);
                if (declaredSymbol == null || declaredSymbol.IsImplicitlyDeclared)
                    continue;
                // The full walk recorded the walked tree's path (a partial type's first location may be another file).
                if (SymbolExtractor.ExtractSymbolInfo(declaredSymbol, 1) is { } info)
                    symbols.Add(Canonical(info, syntaxTree.FilePath));
            }
        }
        return symbols;
    }

    private static Project CreateProject(OutputKind kind, params (string FileName, string Source)[] documents)
    {
        var runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var references = new[] { "System.Runtime.dll", "System.Linq.dll", "System.Collections.dll", "System.Console.dll" }
            .Select(f => MetadataReference.CreateFromFile(Path.Combine(runtime, f)))
            .Append(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .ToList();
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution
            .AddProject(projectId, kind.ToString(), kind.ToString(), LanguageNames.CSharp)
            .WithProjectCompilationOptions(projectId, new CSharpCompilationOptions(kind, nullableContextOptions: NullableContextOptions.Enable))
            .WithProjectParseOptions(projectId, new CSharpParseOptions(LanguageVersion.Latest, DocumentationMode.Parse))
            .WithProjectMetadataReferences(projectId, references);
        foreach (var (fileName, source) in documents)
            solution = solution.AddDocument(DocumentId.CreateNewId(projectId), fileName, source, filePath: "/src/" + fileName);
        return solution.GetProject(projectId)!;
    }
}
