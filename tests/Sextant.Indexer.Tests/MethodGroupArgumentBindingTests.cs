using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Sextant.Indexer.Tests;

/// <summary>
/// Issue #352: a method group passed to a <c>Delegate</c> parameter (<c>group.MapPost("/x", Handler)</c>) binds to no
/// single method, so the model reports OverloadResolutionFailure although the program compiles. That is not an
/// unbound name; an overload that really fails to resolve carries a compiler error and stays one.
/// </summary>
[TestClass]
public class MethodGroupArgumentBindingTests
{
    private const string Header = """
        using System;
        public class Group { public void MapPost(string pattern, Delegate handler) { } }
        """;

    private static (DocumentContributionSet Sink, CSharpCompilation Compilation) Extract(string body)
    {
        var compilation = CSharpCompilation.Create("App",
            [CSharpSyntaxTree.ParseText(Header + body, path: "App.cs")],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
             MetadataReference.CreateFromFile(Path.Combine(
                 System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "System.Runtime.dll"))],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var tree = compilation.SyntaxTrees.Single();
        var sink = new DocumentContributionSet();
        DocumentSemanticExtractor.ExtractDocument(tree.GetRoot(), compilation.GetSemanticModel(tree), "App.cs", sink);
        return (sink, compilation);
    }

    [TestMethod]
    public void MethodGroupPassedToDelegateParameter_IsNotUnbound()
    {
        var (sink, compilation) = Extract("""
            public class Endpoints
            {
                public static string Run() => "x";
                public void Map(Group group) => group.MapPost("/x", Run);
            }
            """);

        Assert.AreEqual(0, compilation.GetDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Error));
        Assert.AreEqual(0, sink.UnboundNames);
        Assert.AreEqual(0, sink.UnboundInvocations);
        Assert.IsTrue(sink.References.Any(r => r.TargetKey.StartsWith("M:Group.MapPost", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void OverloadedMethodGroupPassedToDelegateParameter_IsNotUnbound()
    {
        var (sink, compilation) = Extract("""
            public class Endpoints
            {
                public static string Run() => "x";
                public static string Run<T>() => "y";
                public void Map(Group group) => group.MapPost("/x", Run);
            }
            """);

        Assert.AreEqual(0, compilation.GetDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Error));
        Assert.AreEqual(0, sink.UnboundNames);
    }

    [TestMethod]
    public void RealOverloadFailure_StaysUnboundAndKeepsItsCandidateEdge()
    {
        var (sink, compilation) = Extract("""
            public class Endpoints
            {
                public static void Take(int a) { }
                public static void Take(string a) { }
                public static string Run() => "x";
                public void Call() { Take(1.5); Take(Run); }
            }
            """);

        Assert.IsTrue(compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error),
            "the fixture must be a real overload failure");
        Assert.IsTrue(sink.UnboundNames >= 2, $"unbound names: {sink.UnboundNames}");
        Assert.IsTrue(sink.References.Any(r => r.IsCandidate && r.TargetKey.StartsWith("M:Endpoints.Take", StringComparison.Ordinal)),
            "the candidates are still recorded");
    }
}
