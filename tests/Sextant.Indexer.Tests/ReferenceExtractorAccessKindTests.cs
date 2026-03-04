using Sextant.Core;
using Sextant.Indexer;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Sextant.Indexer.Tests;

[TestClass]
public class ReferenceExtractorAccessKindTests
{
    private static SyntaxNode FindIdentifier(SyntaxTree tree, string name)
    {
        var root = tree.GetRoot();
        return root.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .First(n => n.Identifier.Text == name &&
                        n.Parent is not VariableDeclaratorSyntax &&
                        n.Parent is not PropertyDeclarationSyntax &&
                        n.Parent is not FieldDeclarationSyntax &&
                        n.Parent is not ParameterSyntax);
    }

    private static SyntaxNode FindIdentifierInMethod(SyntaxTree tree, string methodName, string identifierName)
    {
        var root = tree.GetRoot();
        var method = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .First(m => m.Identifier.Text == methodName);
        return method.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .First(n => n.Identifier.Text == identifierName);
    }

    [TestMethod]
    public void SimpleAssignment_IsWrite()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class Foo
            {
                int _x;
                void Bar() { _x = 5; }
            }
            """);
        var node = FindIdentifierInMethod(tree, "Bar", "_x");
        var result = ReferenceExtractor.ClassifyAccessKind(node);
        Assert.AreEqual(AccessKind.Write, result);
    }

    [TestMethod]
    public void ReadingVariable_IsRead()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class Foo
            {
                int _x;
                void Bar() { var y = _x; }
            }
            """);
        var node = FindIdentifierInMethod(tree, "Bar", "_x");
        var result = ReferenceExtractor.ClassifyAccessKind(node);
        Assert.AreEqual(AccessKind.Read, result);
    }

    [TestMethod]
    public void CompoundAssignment_IsReadWrite()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class Foo
            {
                int _x;
                void Bar() { _x += 1; }
            }
            """);
        var node = FindIdentifierInMethod(tree, "Bar", "_x");
        var result = ReferenceExtractor.ClassifyAccessKind(node);
        Assert.AreEqual(AccessKind.ReadWrite, result);
    }

    [TestMethod]
    public void PostIncrement_IsReadWrite()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class Foo
            {
                int _x;
                void Bar() { _x++; }
            }
            """);
        var node = FindIdentifierInMethod(tree, "Bar", "_x");
        var result = ReferenceExtractor.ClassifyAccessKind(node);
        Assert.AreEqual(AccessKind.ReadWrite, result);
    }

    [TestMethod]
    public void PreDecrement_IsReadWrite()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class Foo
            {
                int _x;
                void Bar() { --_x; }
            }
            """);
        var node = FindIdentifierInMethod(tree, "Bar", "_x");
        var result = ReferenceExtractor.ClassifyAccessKind(node);
        Assert.AreEqual(AccessKind.ReadWrite, result);
    }

    [TestMethod]
    public void OutParameter_IsWrite()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class Foo
            {
                int _x;
                void Baz(out int v) { v = 0; }
                void Bar() { Baz(out _x); }
            }
            """);
        var node = FindIdentifierInMethod(tree, "Bar", "_x");
        var result = ReferenceExtractor.ClassifyAccessKind(node);
        Assert.AreEqual(AccessKind.Write, result);
    }

    [TestMethod]
    public void RefParameter_IsReadWrite()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class Foo
            {
                int _x;
                void Baz(ref int v) { v = 0; }
                void Bar() { Baz(ref _x); }
            }
            """);
        var node = FindIdentifierInMethod(tree, "Bar", "_x");
        var result = ReferenceExtractor.ClassifyAccessKind(node);
        Assert.AreEqual(AccessKind.ReadWrite, result);
    }

    [TestMethod]
    public void RightSideOfAssignment_IsRead()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class Foo
            {
                int _x;
                int _y;
                void Bar() { _y = _x; }
            }
            """);
        var node = FindIdentifierInMethod(tree, "Bar", "_x");
        var result = ReferenceExtractor.ClassifyAccessKind(node);
        Assert.AreEqual(AccessKind.Read, result);
    }
}
