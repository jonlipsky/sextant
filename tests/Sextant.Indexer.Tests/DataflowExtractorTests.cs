using Sextant.Indexer;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Sextant.Indexer.Tests;

[TestClass]
public class DataflowExtractorTests
{
    private static (SemanticModel model, InvocationExpressionSyntax invocation) CompileAndFindInvocation(
        string code, string methodName, string calleeName)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var compilation = CSharpCompilation.Create("Test",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var model = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();

        var method = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .First(m => m.Identifier.Text == methodName);

        var invocation = method.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .First(inv =>
            {
                var name = inv.Expression switch
                {
                    IdentifierNameSyntax id => id.Identifier.Text,
                    MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                    _ => ""
                };
                return name == calleeName;
            });

        return (model, invocation);
    }

    [TestMethod]
    public void LiteralArgument_KindIsLiteral()
    {
        var (model, invocation) = CompileAndFindInvocation("""
            class Foo
            {
                void Target(int x) { }
                void Caller() { Target(42); }
            }
            """, "Caller", "Target");

        var result = DataflowExtractor.ExtractFromInvocation(invocation, model);
        Assert.AreEqual(1, result.Arguments.Count);
        Assert.AreEqual("literal", result.Arguments[0].ArgumentKind);
        Assert.AreEqual("42", result.Arguments[0].ArgumentExpression);
        Assert.AreEqual(0, result.Arguments[0].ParameterOrdinal);
        Assert.AreEqual("x", result.Arguments[0].ParameterName);
    }

    [TestMethod]
    public void VariableArgument_KindIsVariable()
    {
        var (model, invocation) = CompileAndFindInvocation("""
            class Foo
            {
                void Target(int x) { }
                void Caller() { int order = 1; Target(order); }
            }
            """, "Caller", "Target");

        var result = DataflowExtractor.ExtractFromInvocation(invocation, model);
        Assert.AreEqual(1, result.Arguments.Count);
        Assert.AreEqual("variable", result.Arguments[0].ArgumentKind);
        Assert.AreEqual("order", result.Arguments[0].ArgumentExpression);
    }

    [TestMethod]
    public void PropertyAccessArgument_KindIsPropertyAccess()
    {
        var (model, invocation) = CompileAndFindInvocation("""
            class Order { public int Id { get; set; } }
            class Foo
            {
                void Target(int x) { }
                void Caller() { var o = new Order(); Target(o.Id); }
            }
            """, "Caller", "Target");

        var result = DataflowExtractor.ExtractFromInvocation(invocation, model);
        Assert.AreEqual(1, result.Arguments.Count);
        Assert.AreEqual("property_access", result.Arguments[0].ArgumentKind);
    }

    [TestMethod]
    public void MethodCallArgument_KindIsMethodCall()
    {
        var (model, invocation) = CompileAndFindInvocation("""
            class Foo
            {
                int GetValue() { return 1; }
                void Target(int x) { }
                void Caller() { Target(GetValue()); }
            }
            """, "Caller", "Target");

        var result = DataflowExtractor.ExtractFromInvocation(invocation, model);
        Assert.AreEqual(1, result.Arguments.Count);
        Assert.AreEqual("method_call", result.Arguments[0].ArgumentKind);
    }

    [TestMethod]
    public void NewObjectArgument_KindIsNewObject()
    {
        var (model, invocation) = CompileAndFindInvocation("""
            class Order { }
            class Foo
            {
                void Target(Order x) { }
                void Caller() { Target(new Order()); }
            }
            """, "Caller", "Target");

        var result = DataflowExtractor.ExtractFromInvocation(invocation, model);
        Assert.AreEqual(1, result.Arguments.Count);
        Assert.AreEqual("new_object", result.Arguments[0].ArgumentKind);
    }

    [TestMethod]
    public void NamedArgument_ResolvesCorrectOrdinal()
    {
        var (model, invocation) = CompileAndFindInvocation("""
            class Foo
            {
                void Target(int a, int b) { }
                void Caller() { Target(b: 5, a: 3); }
            }
            """, "Caller", "Target");

        var result = DataflowExtractor.ExtractFromInvocation(invocation, model);
        Assert.AreEqual(2, result.Arguments.Count);
        var argB = result.Arguments.First(a => a.ParameterName == "b");
        Assert.AreEqual(1, argB.ParameterOrdinal);
        Assert.AreEqual("5", argB.ArgumentExpression);
        var argA = result.Arguments.First(a => a.ParameterName == "a");
        Assert.AreEqual(0, argA.ParameterOrdinal);
        Assert.AreEqual("3", argA.ArgumentExpression);
    }

    [TestMethod]
    public void ReturnAssignedToVariable_DestinationIsAssignment()
    {
        var (model, invocation) = CompileAndFindInvocation("""
            class Foo
            {
                int GetValue() { return 1; }
                void Caller() { var x = GetValue(); }
            }
            """, "Caller", "GetValue");

        var result = DataflowExtractor.ExtractFromInvocation(invocation, model);
        Assert.IsNotNull(result.ReturnDestination);
        Assert.AreEqual("assignment", result.ReturnDestination.DestinationKind);
        Assert.AreEqual("x", result.ReturnDestination.DestinationVariable);
    }

    [TestMethod]
    public void ReturnPassedAsArgument_DestinationIsArgument()
    {
        var (model, invocation) = CompileAndFindInvocation("""
            class Foo
            {
                int GetValue() { return 1; }
                void Other(int x) { }
                void Caller() { Other(GetValue()); }
            }
            """, "Caller", "GetValue");

        var result = DataflowExtractor.ExtractFromInvocation(invocation, model);
        Assert.IsNotNull(result.ReturnDestination);
        Assert.AreEqual("argument", result.ReturnDestination.DestinationKind);
    }

    [TestMethod]
    public void ReturnDiscarded_DestinationIsDiscard()
    {
        var (model, invocation) = CompileAndFindInvocation("""
            class Foo
            {
                int GetValue() { return 1; }
                void Caller() { GetValue(); }
            }
            """, "Caller", "GetValue");

        var result = DataflowExtractor.ExtractFromInvocation(invocation, model);
        Assert.IsNotNull(result.ReturnDestination);
        Assert.AreEqual("discard", result.ReturnDestination.DestinationKind);
    }

    [TestMethod]
    public void ReturnInReturnStatement_DestinationIsReturn()
    {
        var (model, invocation) = CompileAndFindInvocation("""
            class Foo
            {
                int GetValue() { return 1; }
                int Caller() { return GetValue(); }
            }
            """, "Caller", "GetValue");

        var result = DataflowExtractor.ExtractFromInvocation(invocation, model);
        Assert.IsNotNull(result.ReturnDestination);
        Assert.AreEqual("return", result.ReturnDestination.DestinationKind);
    }

    [TestMethod]
    public void AwaitExpression_DestinationIsAwait()
    {
        var (model, invocation) = CompileAndFindInvocation("""
            using System.Threading.Tasks;
            class Foo
            {
                Task<int> GetValueAsync() { return Task.FromResult(1); }
                async Task Caller() { await GetValueAsync(); }
            }
            """, "Caller", "GetValueAsync");

        var result = DataflowExtractor.ExtractFromInvocation(invocation, model);
        Assert.IsNotNull(result.ReturnDestination);
        Assert.AreEqual("await", result.ReturnDestination.DestinationKind);
    }
}
