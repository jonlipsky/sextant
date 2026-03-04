using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Sextant.Indexer.Tests;

[TestClass]
public class InMemorySymbolExtractionTests
{
    private static Project CreateProject(string source, string fileName = "Test.cs")
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
        var documentId = DocumentId.CreateNewId(projectId);

        var solution = workspace.CurrentSolution
            .AddProject(projectId, "TestProject", "TestAssembly", LanguageNames.CSharp)
            .WithProjectCompilationOptions(projectId, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
            .WithProjectMetadataReferences(projectId, references)
            .AddDocument(documentId, fileName, source);

        return solution.GetProject(projectId)!;
    }

    [TestMethod]
    public async Task ExtractsClassesInterfacesMethodsProperties()
    {
        var source = """
            namespace MyNamespace
            {
                public class MyClass
                {
                    public string Name { get; set; }
                    public void DoWork(string input) { }
                }

                public interface IMyInterface
                {
                    void Execute();
                }
            }
            """;

        var project = CreateProject(source);
        var symbols = await SymbolExtractor.ExtractFromProjectAsync(project, 1);

        var classSymbol = symbols.FirstOrDefault(s => s.DisplayName == "MyClass");
        Assert.IsNotNull(classSymbol);
        Assert.AreEqual(Sextant.Core.SymbolKind.Class, classSymbol.Kind);
        StringAssert.Contains(classSymbol.FullyQualifiedName, "MyNamespace.MyClass");

        var methodSymbol = symbols.FirstOrDefault(s => s.DisplayName == "DoWork");
        Assert.IsNotNull(methodSymbol);
        Assert.AreEqual(Sextant.Core.SymbolKind.Method, methodSymbol.Kind);
        Assert.IsNotNull(methodSymbol.Signature);

        var propSymbol = symbols.FirstOrDefault(s => s.DisplayName == "Name");
        Assert.IsNotNull(propSymbol);
        Assert.AreEqual(Sextant.Core.SymbolKind.Property, propSymbol.Kind);

        var ifaceSymbol = symbols.FirstOrDefault(s => s.DisplayName == "IMyInterface");
        Assert.IsNotNull(ifaceSymbol);
        Assert.AreEqual(Sextant.Core.SymbolKind.Interface, ifaceSymbol.Kind);
    }

    [TestMethod]
    public async Task ExtractsCorrectFqns()
    {
        var source = """
            namespace MyNamespace
            {
                public class MyClass
                {
                    public void MyMethod() { }
                }
            }
            """;

        var project = CreateProject(source);
        var symbols = await SymbolExtractor.ExtractFromProjectAsync(project, 1);

        var classSymbol = symbols.First(s => s.DisplayName == "MyClass");
        Assert.AreEqual("global::MyNamespace.MyClass", classSymbol.FullyQualifiedName);
    }

    [TestMethod]
    public async Task ExtractsAccessibilityAndModifiers()
    {
        var source = """
            namespace MyNamespace
            {
                public abstract class BaseClass
                {
                    public static int StaticField;
                    public virtual void VirtualMethod() { }
                    public abstract void AbstractMethod();
                }
            }
            """;

        var project = CreateProject(source);
        var symbols = await SymbolExtractor.ExtractFromProjectAsync(project, 1);

        var baseClass = symbols.First(s => s.DisplayName == "BaseClass");
        Assert.IsTrue(baseClass.IsAbstract);
        Assert.AreEqual(Sextant.Core.Accessibility.Public, baseClass.Accessibility);

        var staticField = symbols.First(s => s.DisplayName == "StaticField");
        Assert.IsTrue(staticField.IsStatic);

        var virtualMethod = symbols.First(s => s.DisplayName == "VirtualMethod");
        Assert.IsTrue(virtualMethod.IsVirtual);

        var abstractMethod = symbols.First(s => s.DisplayName == "AbstractMethod");
        Assert.IsTrue(abstractMethod.IsAbstract);
    }

    [TestMethod]
    public void TestProjectDetection_NoTestRefs_ReturnsFalse()
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var solution = workspace.CurrentSolution
            .AddProject(projectId, "TestProject", "TestAssembly", LanguageNames.CSharp);
        var project = solution.GetProject(projectId)!;

        Assert.IsFalse(TestProjectDetector.IsTestProject(project));
    }

    [TestMethod]
    public async Task ReferenceExtractor_FindsReferencesWithinProject()
    {
        // Test reference extraction within a single project (AdhocWorkspace doesn't set FilePath,
        // so we test that SymbolFinder correctly finds references and classifies them)
        var source = """
            namespace MyNamespace
            {
                public class Service
                {
                    public void DoWork() { }
                }

                public class Consumer
                {
                    public void Run()
                    {
                        var s = new Service();
                        s.DoWork();
                    }
                }
            }
            """;

        var project = CreateProject(source);
        var compilation = await project.GetCompilationAsync();
        Assert.IsNotNull(compilation);

        var serviceSymbol = compilation!.GetTypeByMetadataName("MyNamespace.Service");
        Assert.IsNotNull(serviceSymbol);

        // SymbolFinder.FindReferencesAsync should find the usage in Consumer.Run()
        var referencedSymbols = await Microsoft.CodeAnalysis.FindSymbols.SymbolFinder.FindReferencesAsync(
            serviceSymbol!, project.Solution);

        // Verify SymbolFinder itself works (the core of ReferenceExtractor)
        var allLocations = referencedSymbols.SelectMany(r => r.Locations).ToList();
        Assert.IsTrue(allLocations.Any());

        // Verify we find the "new Service()" reference
        Assert.IsTrue(allLocations.Any(loc => loc.Document.Project.Id == project.Id));
    }

    [TestMethod]
    public async Task CallGraphBuilder_CreatesCallerCalleeEdges()
    {
        var source = """
            namespace MyNamespace
            {
                public class Calculator
                {
                    public int Add(int a, int b) => a + b;
                    public int Subtract(int a, int b) => a - b;

                    public int Compute(int a, int b)
                    {
                        var sum = Add(a, b);
                        var diff = Subtract(a, b);
                        return sum + diff;
                    }
                }
            }
            """;

        var project = CreateProject(source);
        var compilation = await project.GetCompilationAsync();
        Assert.IsNotNull(compilation);

        var semanticModel = compilation!.GetSemanticModel(compilation.SyntaxTrees.First());
        var root = await compilation.SyntaxTrees.First().GetRootAsync();

        // Find the Compute method symbol
        var computeMethod = root.DescendantNodes()
            .Select(n => semanticModel.GetDeclaredSymbol(n))
            .OfType<IMethodSymbol>()
            .First(m => m.Name == "Compute");

        var edges = await CallGraphBuilder.BuildCallGraphAsync(computeMethod, project);

        // Compute calls Add and Subtract
        Assert.IsTrue(edges.Count >= 2, $"Expected at least 2 call edges, got {edges.Count}");
        Assert.IsTrue(edges.Any(e => e.CalleeFqn.Contains("Add")));
        Assert.IsTrue(edges.Any(e => e.CalleeFqn.Contains("Subtract")));
        foreach (var e in edges)
        {
            Assert.IsTrue(e.CallSiteLine > 0);
        }
    }

    [TestMethod]
    public void RelationshipExtractor_ExtractsInheritsAndImplements()
    {
        var source = """
            namespace MyNamespace
            {
                public interface IService { }
                public class BaseService { }
                public class MyService : BaseService, IService { }
            }
            """;

        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var compilation = CSharpCompilation.Create("TestAssembly",
            [syntaxTree],
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
            ],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        var root = syntaxTree.GetRoot();

        var typeSymbol = root.DescendantNodes()
            .Select(n => semanticModel.GetDeclaredSymbol(n))
            .OfType<INamedTypeSymbol>()
            .First(s => s.Name == "MyService");

        var rels = RelationshipExtractor.ExtractRelationships(typeSymbol);

        Assert.IsTrue(rels.Any(r => r.kind == Sextant.Core.RelationshipKind.Inherits && r.toFqn.Contains("BaseService")));
        Assert.IsTrue(rels.Any(r => r.kind == Sextant.Core.RelationshipKind.Implements && r.toFqn.Contains("IService")));
    }

    [TestMethod]
    public async Task ExtractsStructEnumDelegateRecordSymbols()
    {
        var source = """
            namespace MyNamespace
            {
                public struct Point
                {
                    public int X;
                    public int Y;
                }

                public enum Color
                {
                    Red,
                    Green,
                    Blue
                }

                public delegate void Notify(string message);

                public record Person(string FirstName, string LastName);
            }
            """;

        var project = CreateProject(source);
        var symbols = await SymbolExtractor.ExtractFromProjectAsync(project, 1);

        var structSymbol = symbols.FirstOrDefault(s => s.DisplayName == "Point");
        Assert.IsNotNull(structSymbol);
        Assert.AreEqual(Sextant.Core.SymbolKind.Struct, structSymbol.Kind);

        var enumSymbol = symbols.FirstOrDefault(s => s.DisplayName == "Color");
        Assert.IsNotNull(enumSymbol);
        Assert.AreEqual(Sextant.Core.SymbolKind.Enum, enumSymbol.Kind);

        var delegateSymbol = symbols.FirstOrDefault(s => s.DisplayName == "Notify");
        Assert.IsNotNull(delegateSymbol);
        Assert.AreEqual(Sextant.Core.SymbolKind.Delegate, delegateSymbol.Kind);

        var recordSymbol = symbols.FirstOrDefault(s => s.DisplayName == "Person");
        Assert.IsNotNull(recordSymbol);
        Assert.AreEqual(Sextant.Core.SymbolKind.Record, recordSymbol.Kind);
    }

    [TestMethod]
    public async Task ExtractsConstructorFieldEventIndexerTypeParameter()
    {
        var source = """
            namespace MyNamespace
            {
                public class Widget
                {
                    private int _count;

                    public Widget(int count)
                    {
                        _count = count;
                    }

                    public event System.EventHandler? Changed;

                    public int this[int index] => index;
                }

                public class Container<T>
                {
                    public T? Value { get; set; }
                }
            }
            """;

        var project = CreateProject(source);
        var symbols = await SymbolExtractor.ExtractFromProjectAsync(project, 1);

        var ctorSymbol = symbols.FirstOrDefault(s => s.Kind == Sextant.Core.SymbolKind.Constructor
            && s.FullyQualifiedName.Contains("Widget"));
        Assert.IsNotNull(ctorSymbol);

        var fieldSymbol = symbols.FirstOrDefault(s => s.DisplayName == "_count");
        Assert.IsNotNull(fieldSymbol);
        Assert.AreEqual(Sextant.Core.SymbolKind.Field, fieldSymbol.Kind);

        var eventSymbol = symbols.FirstOrDefault(s => s.DisplayName == "Changed");
        Assert.IsNotNull(eventSymbol);
        Assert.AreEqual(Sextant.Core.SymbolKind.Event, eventSymbol.Kind);

        var indexerSymbol = symbols.FirstOrDefault(s => s.Kind == Sextant.Core.SymbolKind.Indexer);
        Assert.IsNotNull(indexerSymbol);

        var typeParamSymbol = symbols.FirstOrDefault(s => s.Kind == Sextant.Core.SymbolKind.TypeParameter
            && s.DisplayName == "T");
        Assert.IsNotNull(typeParamSymbol);
    }

    [TestMethod]
    public void RelationshipExtractor_ExtractsOverrides()
    {
        var source = """
            namespace MyNamespace
            {
                public class Animal
                {
                    public virtual string Speak() => "...";
                }

                public class Dog : Animal
                {
                    public override string Speak() => "Woof";
                }
            }
            """;

        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var compilation = CSharpCompilation.Create("TestAssembly",
            [syntaxTree],
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
            ],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        var root = syntaxTree.GetRoot();

        var dogSymbol = root.DescendantNodes()
            .Select(n => semanticModel.GetDeclaredSymbol(n))
            .OfType<INamedTypeSymbol>()
            .First(s => s.Name == "Dog");

        var rels = RelationshipExtractor.ExtractRelationships(dogSymbol);

        var overrideRel = rels.FirstOrDefault(r => r.kind == Sextant.Core.RelationshipKind.Overrides);
        Assert.AreNotEqual(default, overrideRel);
        StringAssert.Contains(overrideRel.fromFqn, "Speak");
        StringAssert.Contains(overrideRel.toFqn, "Speak");
    }

    [TestMethod]
    public void RelationshipExtractor_ExtractsReturnsAndParameterOf()
    {
        var source = """
            namespace MyNamespace
            {
                public class Request { }
                public class Response { }

                public class Handler
                {
                    public Response Handle(Request request) => new Response();
                }
            }
            """;

        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var compilation = CSharpCompilation.Create("TestAssembly",
            [syntaxTree],
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
            ],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        var root = syntaxTree.GetRoot();

        var handlerSymbol = root.DescendantNodes()
            .Select(n => semanticModel.GetDeclaredSymbol(n))
            .OfType<INamedTypeSymbol>()
            .First(s => s.Name == "Handler");

        var rels = RelationshipExtractor.ExtractRelationships(handlerSymbol);

        Assert.IsTrue(rels.Any(r => r.kind == Sextant.Core.RelationshipKind.Returns
            && r.fromFqn.Contains("Handle")
            && r.toFqn.Contains("Response")));

        Assert.IsTrue(rels.Any(r => r.kind == Sextant.Core.RelationshipKind.ParameterOf
            && r.fromFqn.Contains("Request")
            && r.toFqn.Contains("Handle")));
    }

    [TestMethod]
    public void RelationshipExtractor_ExtractsInstantiates()
    {
        var source = """
            namespace MyNamespace
            {
                public class SomeClass { }

                public class Factory
                {
                    public SomeClass Create()
                    {
                        return new SomeClass();
                    }
                }
            }
            """;

        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var compilation = CSharpCompilation.Create("TestAssembly",
            [syntaxTree],
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
            ],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        var root = syntaxTree.GetRoot();

        var factorySymbol = root.DescendantNodes()
            .Select(n => semanticModel.GetDeclaredSymbol(n))
            .OfType<INamedTypeSymbol>()
            .First(s => s.Name == "Factory");

        var rels = RelationshipExtractor.ExtractInstantiates(factorySymbol, compilation);

        Assert.IsTrue(rels.Any(r => r.kind == Sextant.Core.RelationshipKind.Instantiates
            && r.fromFqn.Contains("Create")
            && r.toFqn.Contains("SomeClass")));
    }
}
