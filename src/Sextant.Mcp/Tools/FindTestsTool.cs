using System.ComponentModel;
using Sextant.Core;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

public static class FindTestsTool
{
    [McpServerTool(Name = "find_tests"),
     Description("Find test methods, optionally filtered to tests that reference a specific production symbol.")]
    public static string FindTests(
        DatabaseProvider dbProvider,
        [Description("FQN of a production symbol to find tests for. Omit to list all test methods.")]
        string? for_symbol = null,
        [Description("Test framework filter: 'xunit', 'nunit', 'mstest', or 'all' (default)")]
        string framework = "all",
        [Description("Maximum results (default 50)")]
        int max_results = 50)
    {
        var db = dbProvider.GetDatabase();
        if (db == null)
            return ResponseBuilder.BuildEmpty("No index database found.");

        var conn = db.GetConnection();
        var symbolStore = new SymbolStore(conn);
        var referenceStore = new ReferenceStore(conn);

        var testAttributes = GetTestAttributes(framework);

        var allTestMethods = new List<SymbolInfo>();
        foreach (var attr in testAttributes)
            allTestMethods.AddRange(symbolStore.GetByAttribute(attr));

        // Deduplicate by ID
        allTestMethods = allTestMethods
            .GroupBy(t => t.Id)
            .Select(g => g.First())
            .ToList();

        var testMethods = allTestMethods;

        if (for_symbol != null)
        {
            var targetSymbol = symbolStore.GetByFqn(for_symbol);
            if (targetSymbol == null)
                return ResponseBuilder.BuildEmpty("Symbol not found.");

            var refs = referenceStore.GetBySymbolId(targetSymbol.Id);
            var testFiles = testMethods.Select(t => t.FilePath).ToHashSet();
            var refsInTestFiles = refs.Where(r => testFiles.Contains(r.FilePath)).ToList();

            // Match references to test methods by file + line range
            var matchedTests = testMethods.Where(tm =>
                refsInTestFiles.Any(r =>
                    r.FilePath == tm.FilePath &&
                    r.Line >= tm.LineStart &&
                    r.Line <= tm.LineEnd))
                .ToList();

            // Fallback: naming convention matching
            if (matchedTests.Count == 0)
            {
                var targetName = ExtractSimpleName(for_symbol);
                matchedTests = allTestMethods.Where(t =>
                    t.DisplayName.Contains(targetName, StringComparison.OrdinalIgnoreCase) ||
                    GetContainingTypeName(t.FullyQualifiedName).Contains(targetName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            testMethods = matchedTests;
        }

        testMethods = testMethods.Take(max_results).ToList();

        var results = testMethods.Select(t => (object)new
        {
            fully_qualified_name = t.FullyQualifiedName,
            display_name = t.DisplayName,
            test_framework = DetectFramework(t.Attributes),
            test_class = GetContainingTypeName(t.FullyQualifiedName),
            file_path = t.FilePath,
            line_start = t.LineStart,
            line_end = t.LineEnd,
            references_target = for_symbol != null
        }).ToList();

        var freshness = testMethods.Count > 0 ? testMethods.Min(t => t.LastIndexedAt) : 0;
        return ResponseBuilder.Build(results, freshness);
    }

    private static List<string> GetTestAttributes(string framework)
    {
        return framework.ToLowerInvariant() switch
        {
            "xunit" => [
                "global::Xunit.FactAttribute",
                "global::Xunit.TheoryAttribute"
            ],
            "nunit" => [
                "global::NUnit.Framework.TestAttribute",
                "global::NUnit.Framework.TestCaseAttribute"
            ],
            "mstest" => [
                "global::Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute"
            ],
            _ => [
                "global::Xunit.FactAttribute",
                "global::Xunit.TheoryAttribute",
                "global::NUnit.Framework.TestAttribute",
                "global::NUnit.Framework.TestCaseAttribute",
                "global::Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute"
            ]
        };
    }

    private static string ExtractSimpleName(string fqn)
    {
        var parenIdx = fqn.IndexOf('(');
        var nameOnly = parenIdx >= 0 ? fqn[..parenIdx] : fqn;
        var lastDot = nameOnly.LastIndexOf('.');
        return lastDot >= 0 ? nameOnly[(lastDot + 1)..] : nameOnly;
    }

    private static string GetContainingTypeName(string fqn)
    {
        var parenIdx = fqn.IndexOf('(');
        var nameOnly = parenIdx >= 0 ? fqn[..parenIdx] : fqn;
        var lastDot = nameOnly.LastIndexOf('.');
        if (lastDot < 0) return nameOnly;
        var typeFqn = nameOnly[..lastDot];
        var typeLastDot = typeFqn.LastIndexOf('.');
        return typeLastDot >= 0 ? typeFqn[(typeLastDot + 1)..] : typeFqn;
    }

    private static string DetectFramework(string? attributes)
    {
        if (attributes == null) return "unknown";
        if (attributes.Contains("Xunit")) return "xunit";
        if (attributes.Contains("NUnit")) return "nunit";
        if (attributes.Contains("Microsoft.VisualStudio.TestTools")) return "mstest";
        return "unknown";
    }
}
