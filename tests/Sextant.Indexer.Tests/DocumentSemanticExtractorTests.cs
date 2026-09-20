using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sextant.Core;

namespace Sextant.Indexer.Tests;

/// <summary>
/// Fast, in-memory unit tests for the document-oriented extractor (Phase 5). Each test parses a small
/// known source, runs the single-pass <see cref="DocumentSemanticExtractor.ExtractDocument"/> over its
/// cached model + root, and asserts the exact usage-site contributions — reference kinds, access
/// kinds, call edges, relationships, enclosing-member attribution, and deduplication.
/// </summary>
[TestClass]
public class DocumentSemanticExtractorTests
{
    private static DocumentContributionSet Extract(string source, string fileName = "Test.cs")
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, path: fileName);
        var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var compilation = CSharpCompilation.Create("TestAssembly",
            [syntaxTree],
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
                MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
            ],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var model = compilation.GetSemanticModel(syntaxTree);
        var root = syntaxTree.GetRoot();
        var text = syntaxTree.GetText();

        var sink = new DocumentContributionSet();
        DocumentSemanticExtractor.ExtractDocument(root, model, fileName, text, sink);
        return sink;
    }

    private static bool HasReference(DocumentContributionSet sink, string targetKeyContains, ReferenceKind kind)
        => sink.References.Any(r => r.TargetKey.Contains(targetKeyContains) && r.Kind == kind);

    [TestMethod]
    public void Reference_ClassifiesKindsBySyntacticRole()
    {
        var source = """
            using System;
            namespace N
            {
                public sealed class MarkerAttribute : Attribute { }
                public interface IThing { }
                public class BaseThing { }

                [Marker]
                public class Derived : BaseThing, IThing
                {
                    public void Run()
                    {
                        var made = new Derived();
                        Helper();
                    }

                    public void Helper() { }
                }
            }
            """;

        var sink = Extract(source);

        // Base list -> Inheritance (both base class and interface).
        Assert.IsTrue(HasReference(sink, "BaseThing", ReferenceKind.Inheritance), "base class should be Inheritance");
        Assert.IsTrue(HasReference(sink, "IThing", ReferenceKind.Inheritance), "interface should be Inheritance");

        // Attribute usage -> Attribute (retargeted from the implicit constructor to the type).
        Assert.IsTrue(HasReference(sink, "MarkerAttribute", ReferenceKind.Attribute), "attribute usage should be Attribute");

        // Object creation type -> ObjectCreation.
        Assert.IsTrue(HasReference(sink, "Derived", ReferenceKind.ObjectCreation), "new Derived() should be ObjectCreation");

        // Invoked name -> Invocation.
        Assert.IsTrue(HasReference(sink, "Helper", ReferenceKind.Invocation), "Helper() should be Invocation");
    }

    [TestMethod]
    public void Reference_MetadataTargetsAreNotStored()
    {
        var source = """
            using System;
            namespace N
            {
                public class C
                {
                    public void M()
                    {
                        Console.WriteLine("hi");
                        string s = "x";
                    }
                }
            }
            """;

        var sink = Extract(source);

        // No reference should target a metadata symbol (String / Console live in metadata).
        Assert.IsFalse(sink.References.Any(r => r.TargetKey.Contains("Console")), "Console is metadata, not stored");
        Assert.IsFalse(sink.References.Any(r => r.TargetKey.Contains("String")), "String is metadata, not stored");
    }

    [TestMethod]
    public void Calls_EmitsCallerCalleeEdges()
    {
        var source = """
            namespace N
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

        var sink = Extract(source);

        var fromCompute = sink.Calls.Where(c => c.CallerKey.Contains("Compute")).ToList();
        Assert.IsTrue(fromCompute.Any(c => c.CalleeKey.Contains("Add")), "Compute -> Add edge missing");
        Assert.IsTrue(fromCompute.Any(c => c.CalleeKey.Contains("Subtract")), "Compute -> Subtract edge missing");
        Assert.IsTrue(fromCompute.All(c => c.CallSiteLine > 0), "call sites must carry a 1-based line");
    }

    [TestMethod]
    public void Calls_LambdaBodyAttributesToEnclosingMethod()
    {
        var source = """
            using System;
            namespace N
            {
                public class C
                {
                    public void Outer()
                    {
                        Action a = () => Helper();
                        a();
                    }

                    public void Helper() { }
                }
            }
            """;

        var sink = Extract(source);

        // The call inside the lambda is attributed to Outer (lambda is transparent), not to a
        // synthesized lambda symbol. The delegate invoke `a()` targets metadata and is dropped.
        Assert.IsTrue(sink.Calls.Any(c => c.CallerKey.Contains("Outer") && c.CalleeKey.Contains("Helper")),
            "Helper() inside the lambda should attribute to Outer");
    }

    [TestMethod]
    public void Relationships_InheritsImplementsOverridesReturnsParameterOf()
    {
        var source = """
            namespace N
            {
                public interface IService { }
                public class BaseService { public virtual string Name() => ""; }
                public class Request { }
                public class Response { }

                public class MyService : BaseService, IService
                {
                    public override string Name() => "svc";
                    public Response Handle(Request request) => new Response();
                }
            }
            """;

        var sink = Extract(source);

        Assert.IsTrue(sink.Relationships.Any(r => r.Kind == RelationshipKind.Inherits && r.ToKey.Contains("BaseService")));
        Assert.IsTrue(sink.Relationships.Any(r => r.Kind == RelationshipKind.Implements && r.ToKey.Contains("IService")));
        Assert.IsTrue(sink.Relationships.Any(r => r.Kind == RelationshipKind.Overrides && r.FromKey.Contains("Name")));
        Assert.IsTrue(sink.Relationships.Any(r => r.Kind == RelationshipKind.Returns
            && r.FromKey.Contains("Handle") && r.ToKey.Contains("Response")));
        Assert.IsTrue(sink.Relationships.Any(r => r.Kind == RelationshipKind.ParameterOf
            && r.FromKey.Contains("Request") && r.ToKey.Contains("Handle")));
    }

    [TestMethod]
    public void Relationships_InstantiatesDeduplicatesRepeatedCreation()
    {
        var source = """
            namespace N
            {
                public class SomeClass { }

                public class Factory
                {
                    public SomeClass Create()
                    {
                        var a = new SomeClass();
                        var b = new SomeClass();
                        return a;
                    }
                }
            }
            """;

        var sink = Extract(source);

        var instantiates = sink.Relationships
            .Where(r => r.Kind == RelationshipKind.Instantiates
                        && r.FromKey.Contains("Create") && r.ToKey.Contains("SomeClass"))
            .ToList();
        Assert.AreEqual(1, instantiates.Count, "two creations of the same type in one method coalesce to one Instantiates");
    }

    [TestMethod]
    public void Reference_SameLineOccurrencesDeduplicate()
    {
        // Two `new Dup()` occurrences on the same physical line share the (target, file, line, kind,
        // access) occurrence key and must collapse to a single reference row.
        var source = """
            namespace N
            {
                public class Dup { }
                public class C
                {
                    public void M() { var a = new Dup(); var b = new Dup(); }
                }
            }
            """;

        var sink = Extract(source);

        var objectCreations = sink.References
            .Where(r => r.TargetKey.Contains("Dup") && r.Kind == ReferenceKind.ObjectCreation)
            .ToList();
        Assert.AreEqual(1, objectCreations.Count, "same-line duplicate object-creation occurrences should coalesce");
    }

    [TestMethod]
    public void Access_ClassifiesReadWriteReadWriteOnFields()
    {
        var source = """
            namespace N
            {
                public class C
                {
                    private int _f;
                    public void Read() { var x = _f; }
                    public void Write() { _f = 5; }
                    public void Compound() { _f += 1; }
                    public void Increment() { _f++; }
                }
            }
            """;

        var sink = Extract(source);

        var fieldRefs = sink.References.Where(r => r.TargetKey.Contains("_f")).ToList();
        Assert.IsTrue(fieldRefs.Any(r => r.Access == AccessKind.Read), "read access missing");
        Assert.IsTrue(fieldRefs.Any(r => r.Access == AccessKind.Write), "write access missing");
        Assert.AreEqual(2, fieldRefs.Count(r => r.Access == AccessKind.ReadWrite),
            "compound assignment and increment are both read-write");
    }

    [TestMethod]
    public void Instantiates_ImplicitNewEmitsReferenceAndRelationship()
    {
        var source = """
            namespace N
            {
                public class Widget { }
                public class C
                {
                    public Widget Make()
                    {
                        Widget w = new();
                        return w;
                    }
                }
            }
            """;

        var sink = Extract(source);

        Assert.IsTrue(HasReference(sink, "Widget", ReferenceKind.ObjectCreation),
            "target-typed new() should emit an ObjectCreation reference to the type");
        Assert.IsTrue(sink.Relationships.Any(r => r.Kind == RelationshipKind.Instantiates
            && r.FromKey.Contains("Make") && r.ToKey.Contains("Widget")),
            "target-typed new() should emit an Instantiates relationship");
    }

    [TestMethod]
    public void Reference_ImplicitVarDoesNotEmitPhantomTypeReference()
    {
        // `var` binds to the inferred type but the type name never textually appears at the `var`
        // token. The legacy FindReferencesAsync path does not report implicit-var occurrences, so the
        // document extractor must not emit a phantom TypeRef at the `var` position. `Element` appears
        // explicitly only on the field-declaration line (via `List<Element>`); the `foreach` line
        // reaches it solely through `var`, so a phantom would surface as an extra TypeRef there.
        var source = """
            namespace N
            {
                public class Element { }
                public class C
                {
                    public System.Collections.Generic.List<Element> Bag = new();
                    public void M()
                    {
                        foreach (var e in Bag) { System.Console.WriteLine(e); }
                    }
                }
            }
            """;

        var sink = Extract(source);

        // No TypeRef to Element may originate on the `foreach (var ...)` line (line 9, 1-based).
        var elementTypeRefsOnForeachLine = sink.References
            .Where(r => r.TargetKey.Contains("Element") && r.Kind == ReferenceKind.TypeRef && r.Line == 9)
            .ToList();
        Assert.AreEqual(0, elementTypeRefsOnForeachLine.Count,
            "implicit `var` must not emit a phantom type reference at the var position");

        // Sanity: the explicit `List<Element>` on the field line is still recorded, so the assertion
        // above is checking suppression of the phantom, not absence of all Element references.
        Assert.IsTrue(sink.References.Any(r => r.TargetKey.Contains("Element") && r.Kind == ReferenceKind.TypeRef),
            "the explicit List<Element> type reference should still be recorded");
    }

    [TestMethod]
    public void Call_DistinctSameLineCallsKeepSeparateDataflow()
    {
        // Two distinct calls to the same method on one physical line are separate occurrences whose
        // arguments differ. They share (caller, callee, file, line) but not the call-site column, so
        // they must not coalesce — otherwise the second call's argument dataflow would be lost.
        var source = """
            namespace N
            {
                public class C
                {
                    public int Add(int x) => x;
                    public void M() { var a = Add(1); var b = Add(2); }
                }
            }
            """;

        var sink = Extract(source);

        var addCalls = sink.Calls.Where(c => c.CalleeKey.Contains("Add")).ToList();
        Assert.AreEqual(2, addCalls.Count, "two distinct same-line calls must produce two edges, not one");
        var argExprs = addCalls
            .SelectMany(c => c.Dataflow.Arguments.Select(a => a.ArgumentExpression))
            .OrderBy(e => e)
            .ToList();
        CollectionAssert.AreEqual(new[] { "1", "2" }, argExprs,
            "each same-line call must retain its own argument dataflow");
    }
}



