using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sextant.Core;

namespace Sextant.Indexer.Tests;

/// <summary>
/// <c>nameof</c> (the operator) and the <c>notnull</c>/<c>unmanaged</c> type constraints are contextual keywords:
/// they never bind to a symbol, so they must not be counted as unbound names, while the argument of a
/// <c>nameof</c> is examined as usual and a real method of that name stays an ordinary name.
/// </summary>
[TestClass]
public class KeywordNameBindingTests
{
    private static (DocumentContributionSet Sink, CSharpCompilation Compilation) Extract(string source)
    {
        var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var compilation = CSharpCompilation.Create("K",
            [CSharpSyntaxTree.ParseText(source, path: "K.cs")],
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
            ],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var tree = compilation.SyntaxTrees.Single();
        var sink = new DocumentContributionSet();
        DocumentSemanticExtractor.ExtractDocument(tree.GetRoot(), compilation.GetSemanticModel(tree), "K.cs", sink);
        return (sink, compilation);
    }

    private static int Errors(Compilation compilation) =>
        compilation.GetDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Error);

    [TestMethod]
    public void NameofOfALocalMemberAndType_IsNotUnbound()
    {
        var (sink, compilation) = Extract("""
            public class Thing { public int Member; }
            public class C
            {
                public string F(int local)
                {
                    var a = nameof(local);
                    var b = nameof(Thing.Member);
                    var c = nameof(Thing);
                    return a + b + c;
                }
            }
            """);
        Assert.AreEqual(0, Errors(compilation));
        Assert.AreEqual(0, sink.UnboundNames);
        Assert.IsTrue(sink.NamesExamined > 0);
    }

    [TestMethod]
    public void NameofInACaseLabelAndAnInterpolation_IsNotUnbound()
    {
        var (sink, compilation) = Extract("""
            public class C
            {
                public const string Name = "Name";
                public string F(string s)
                {
                    switch (s)
                    {
                        case nameof(Name): return $"{nameof(s)}";
                        default: return $"x{nameof(C)}";
                    }
                }
            }
            """);
        Assert.AreEqual(0, Errors(compilation));
        Assert.AreEqual(0, sink.UnboundNames);
    }

    [TestMethod]
    public void NameofOfAMissingName_CountsTheArgumentButNotTheKeyword()
    {
        var (sink, compilation) = Extract("""
            public class C
            {
                public string F() => nameof(Missing);
            }
            """);
        Assert.IsTrue(compilation.GetDiagnostics().Any(d => d.Id == "CS0103"), "the fixture must not compile");
        Assert.AreEqual(1, sink.UnboundNames, "only Missing is unbound; the nameof keyword is exempt");
    }

    [TestMethod]
    public void NameofInAttributeFieldPropertyAndLocalFunction_IsNotUnbound()
    {
        var (sink, compilation) = Extract("""
            using System;
            public class MarkAttribute : Attribute { public MarkAttribute(string s) { } }
            [Mark(nameof(C))]
            public class C
            {
                [Mark(nameof(Field))] public string Field = nameof(Field);
                public string Prop { get; } = nameof(Prop);
                public string F(int arg)
                {
                    string Local() => nameof(arg);
                    return Local();
                }
            }
            """);
        Assert.AreEqual(0, Errors(compilation));
        Assert.AreEqual(0, sink.UnboundNames);
    }

    [TestMethod]
    public void NameofOfAMissingName_InEachPosition_CountsOnlyTheArgument()
    {
        var (sink, compilation) = Extract("""
            using System;
            public class MarkAttribute : Attribute { public MarkAttribute(string s) { } }
            [Mark(nameof(M1))]
            public class C
            {
                public string Field = nameof(M2);
                public string Prop { get; } = nameof(M3);
                public string F()
                {
                    string Local() => nameof(M4);
                    return Local();
                }
            }
            """);
        Assert.IsTrue(compilation.GetDiagnostics().Count(d => d.Id == "CS0103") >= 4, "the fixture must not compile");
        Assert.AreEqual(4, sink.UnboundNames, "each missing argument is counted once; no nameof keyword is");
    }

    [TestMethod]
    public void NotNullAndUnmanagedConstraints_AreNotUnbound()
    {
        var (sink, compilation) = Extract("""
            public class A<T> where T : notnull { }
            public class B<T> where T : unmanaged { }
            public class D<T> where T : class, notnull { }
            """);
        Assert.AreEqual(0, sink.UnboundNames);
    }

    [TestMethod]
    public void ARealMethodNamedNameof_IsAnOrdinaryName()
    {
        var (bound, boundCompilation) = Extract("""
            public class C
            {
                public static string nameof(int x) => "";
                public string F() => nameof(1);
            }
            """);
        Assert.AreEqual(0, Errors(boundCompilation));
        Assert.AreEqual(0, bound.UnboundNames);
        Assert.IsTrue(bound.References.Any(r => r.TargetKey.StartsWith("M:C.nameof", StringComparison.Ordinal)));

        var (failed, failedCompilation) = Extract("""
            public class C
            {
                public static string nameof(int x) => "";
                public string F() => nameof("x");
            }
            """);
        Assert.IsTrue(failedCompilation.GetDiagnostics().Any(d => d.Id == "CS1503"));
        Assert.AreEqual(1, failed.UnboundNames, "an ordinary invocation named nameof that fails to bind is counted");
    }
}
