using Sextant.Indexer;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Sextant.Indexer.Tests;

[TestClass]
public class CommentExtractorTests
{
    private static SyntaxTree ParseCode(string code)
    {
        return CSharpSyntaxTree.ParseText(code, path: "Test.cs");
    }

    [TestMethod]
    public void ExtractComments_TodoComment_ExtractsTagAndText()
    {
        var tree = ParseCode("""
            class Foo
            {
                // TODO: fix this
                void Bar() { }
            }
            """);
        var comments = CommentExtractor.ExtractComments(tree);
        Assert.AreEqual(1, comments.Count);
        Assert.AreEqual("TODO", comments[0].Tag);
        Assert.AreEqual("fix this", comments[0].Text);
    }

    [TestMethod]
    public void ExtractComments_HackComment_ExtractsCorrectly()
    {
        var tree = ParseCode("""
            class Foo
            {
                // HACK: temporary workaround
                void Bar() { }
            }
            """);
        var comments = CommentExtractor.ExtractComments(tree);
        Assert.AreEqual(1, comments.Count);
        Assert.AreEqual("HACK", comments[0].Tag);
        Assert.AreEqual("temporary workaround", comments[0].Text);
    }

    [TestMethod]
    public void ExtractComments_FixmeComment_ExtractsCorrectly()
    {
        var tree = ParseCode("""
            class Foo
            {
                // FIXME: off by one
                void Bar() { }
            }
            """);
        var comments = CommentExtractor.ExtractComments(tree);
        Assert.AreEqual(1, comments.Count);
        Assert.AreEqual("FIXME", comments[0].Tag);
    }

    [TestMethod]
    public void ExtractComments_LowercaseTodo_ExtractsAsUppercase()
    {
        var tree = ParseCode("""
            class Foo
            {
                // todo: lowercase works
                void Bar() { }
            }
            """);
        var comments = CommentExtractor.ExtractComments(tree);
        Assert.AreEqual(1, comments.Count);
        Assert.AreEqual("TODO", comments[0].Tag);
        Assert.AreEqual("lowercase works", comments[0].Text);
    }

    [TestMethod]
    public void ExtractComments_NormalComment_NotExtracted()
    {
        var tree = ParseCode("""
            class Foo
            {
                // This is a normal comment
                void Bar() { }
            }
            """);
        var comments = CommentExtractor.ExtractComments(tree);
        Assert.AreEqual(0, comments.Count);
    }

    [TestMethod]
    public void ExtractComments_MultiLineComment_ExtractsTodo()
    {
        var tree = ParseCode("""
            class Foo
            {
                /* TODO: multi-line thing */
                void Bar() { }
            }
            """);
        var comments = CommentExtractor.ExtractComments(tree);
        Assert.AreEqual(1, comments.Count);
        Assert.AreEqual("TODO", comments[0].Tag);
        Assert.AreEqual("multi-line thing", comments[0].Text);
    }

    [TestMethod]
    public void ExtractComments_MultipleTags_ReturnsAll()
    {
        var tree = ParseCode("""
            class Foo
            {
                // TODO: first task
                // FIXME: second issue
                // NOTE: important detail
                void Bar() { }
            }
            """);
        var comments = CommentExtractor.ExtractComments(tree);
        Assert.AreEqual(3, comments.Count);
        Assert.AreEqual("TODO", comments[0].Tag);
        Assert.AreEqual("FIXME", comments[1].Tag);
        Assert.AreEqual("NOTE", comments[2].Tag);
    }

    [TestMethod]
    public void ExtractComments_BugTag_Extracted()
    {
        var tree = ParseCode("""
            class Foo
            {
                // BUG: null reference when empty
                void Bar() { }
            }
            """);
        var comments = CommentExtractor.ExtractComments(tree);
        Assert.AreEqual(1, comments.Count);
        Assert.AreEqual("BUG", comments[0].Tag);
    }

    [TestMethod]
    public void ExtractComments_HasCorrectLineNumber()
    {
        var tree = ParseCode("""
            class Foo
            {
                void Bar()
                {
                    // TODO: line 5
                }
            }
            """);
        var comments = CommentExtractor.ExtractComments(tree);
        Assert.AreEqual(1, comments.Count);
        Assert.AreEqual(5, comments[0].Line);
    }
}
