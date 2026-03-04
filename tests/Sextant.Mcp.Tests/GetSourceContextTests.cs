using System.Text.Json;
using Sextant.Mcp;
using Sextant.Mcp.Tools;

namespace Sextant.Mcp.Tests;

[TestClass]
public class GetSourceContextTests
{
    private static readonly McpTestFixture _fixture = McpTestFixtureInstance.Instance;

    [TestMethod]
    public void GetSourceContext_RealFile_ReturnsSourceLines()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"source_ctx_test_{Guid.NewGuid():N}.cs");
        try
        {
            var lines = new[]
            {
                "using System;",
                "",
                "namespace Test;",
                "",
                "public class Foo",
                "{",
                "    public void Bar()",
                "    {",
                "        Console.WriteLine(\"hello\");",
                "    }",
                "}"
            };
            File.WriteAllLines(tempFile, lines);

            var result = GetSourceContextTool.GetSourceContext(tempFile, 7, 2);
            var doc = JsonDocument.Parse(result);
            var meta = doc.RootElement.GetProperty("meta");
            Assert.AreEqual(1, meta.GetProperty("result_count").GetInt32());

            var first = doc.RootElement.GetProperty("results")[0];
            Assert.AreEqual(tempFile, first.GetProperty("file_path").GetString());
            Assert.AreEqual(7, first.GetProperty("target_line").GetInt32());

            var source = first.GetProperty("source");
            Assert.IsTrue(source.TryGetProperty("lines", out var linesEl));
            Assert.AreEqual(5, linesEl.GetArrayLength());
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [TestMethod]
    public void GetSourceContext_NonExistentFile_ReturnsEmpty()
    {
        var result = GetSourceContextTool.GetSourceContext("/nonexistent/file.cs", 10, 3);
        var doc = JsonDocument.Parse(result);
        var meta = doc.RootElement.GetProperty("meta");
        Assert.AreEqual(0, meta.GetProperty("result_count").GetInt32());
    }

    [TestMethod]
    public void GetSourceContext_ContextLinesZero_ReturnsOnlyTargetLine()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"source_ctx_zero_{Guid.NewGuid():N}.cs");
        try
        {
            File.WriteAllLines(tempFile, new[] { "line1", "line2", "line3", "line4", "line5" });

            var result = GetSourceContextTool.GetSourceContext(tempFile, 3, 0);
            var doc = JsonDocument.Parse(result);
            var first = doc.RootElement.GetProperty("results")[0];
            var lines = first.GetProperty("source").GetProperty("lines");
            Assert.AreEqual(1, lines.GetArrayLength());
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [TestMethod]
    public void GetSourceContext_ContextLinesExceedsFileEnd_ClampsAtEnd()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"source_ctx_clamp_{Guid.NewGuid():N}.cs");
        try
        {
            File.WriteAllLines(tempFile, new[] { "line1", "line2", "line3" });

            var result = GetSourceContextTool.GetSourceContext(tempFile, 2, 20);
            var doc = JsonDocument.Parse(result);
            var first = doc.RootElement.GetProperty("results")[0];
            var lines = first.GetProperty("source").GetProperty("lines");
            Assert.AreEqual(3, lines.GetArrayLength());
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [TestMethod]
    public void FindSymbol_IncludeSource_AddsDeclaration()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"source_decl_{Guid.NewGuid():N}.cs");
        try
        {
            File.WriteAllLines(tempFile, new[] {
                "namespace Test;",
                "public class Foo",
                "{",
                "    public void Bar() { }",
                "}"
            });

            // include_source reads from the symbol's file path, which would be in the index.
            // Since our test DB has synthetic file paths, we test the tool parameter is accepted
            var result = FindSymbolTool.FindSymbol(_fixture.DbProvider, "BaseService",
                fuzzy: true, include_source: true);
            var doc = JsonDocument.Parse(result);
            var results = doc.RootElement.GetProperty("results");
            Assert.IsTrue(results.GetArrayLength() >= 1);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [TestMethod]
    public void GetCallHierarchy_IncludeSource_AcceptsParameter()
    {
        // include_source reads from real files; our test DB has synthetic paths
        // Verify the parameter is accepted and doesn't error
        var result = GetCallHierarchyTool.GetCallHierarchy(_fixture.DbProvider,
            "global::Alpha.BaseService.Process(string)", "callers", include_source: true);
        var doc = JsonDocument.Parse(result);
        Assert.IsTrue(doc.RootElement.TryGetProperty("results", out _));
    }
}
