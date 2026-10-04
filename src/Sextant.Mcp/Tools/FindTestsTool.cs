using System.ComponentModel;
using Sextant.Core;
using Sextant.Store;
using ModelContextProtocol.Server;

namespace Sextant.Mcp.Tools;

[McpServerToolType]
public static class FindTestsTool
{
    [McpServerTool(Name = "find_tests"),
     Description("Test methods, optionally only those exercising a symbol. Use instead of grepping test folders.")]
    public static string FindTests(
        DatabaseProvider dbProvider,
        [Description("Symbol the tests exercise.")]
        string? for_symbol = null,
        [Description("xunit, nunit, mstest or all.")]
        string framework = "all",
        [Description(ToolText.Limit)] int? limit = null,
        [Description(ToolText.Cursor)] string? cursor = null)
    {
        if (!dbProvider.TryBeginRead(out var db, out var readContext, out var authError))
            return authError;

        if (!CapabilityGate.Ensure(db, IndexFeature.TestIndexing, "test_indexing", out var unavailable,
                readContext.SelectedSnapshotId, dbProvider.Authorizer.IsEnforcing))
            return unavailable;
        if (!Paging.TryBegin("find_tests", limit, cursor, readContext, out var page, out var cursorError,
                for_symbol, framework))
            return cursorError;

        using var conn = db.OpenReadConnection();
        var snapshotScope = readContext.Scope;
        var symbolStore = new SymbolStore(conn) { Scope = snapshotScope };
        var referenceStore = new ReferenceStore(conn) { Scope = snapshotScope };
        var projectStore = new ProjectStore(conn) { Scope = readContext.Scope };

        var frameworkName = string.IsNullOrWhiteSpace(framework) ? "all" : framework.Trim().ToLowerInvariant();
        if (frameworkName is not ("all" or "xunit" or "nunit" or "mstest"))
            return ResponseBuilder.BuildError(ResponseBuilder.InvalidArgumentCode,
                $"Unknown framework '{framework}'. Use 'xunit', 'nunit', 'mstest' or 'all'.", readContext.Provenance);

        var testAttributes = GetTestAttributes(frameworkName);

        var allTestMethods = new List<SymbolInfo>();
        foreach (var attr in testAttributes)
            allTestMethods.AddRange(symbolStore.GetByAttribute(attr));

        // Deduplicate by ID
        allTestMethods = allTestMethods
            .GroupBy(t => t.Id)
            .Select(g => g.First())
            .ToList();

        var testMethods = allTestMethods;
        SymbolAmbiguity? ambiguity = null;
        string? message = null;
        var namer = new SymbolNamer(symbolStore);
        var referencing = false;

        if (for_symbol != null)
        {
            var lookup = SymbolResolver.Lookup(symbolStore, projectStore, for_symbol);
            if (lookup.Status != SymbolLookupStatus.Resolved)
                return SymbolResolver.ErrorResponse(symbolStore, projectStore, lookup, readContext.Provenance);
            var targetSymbol = lookup.Symbol!;
            ambiguity = lookup.Ambiguity;

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
            referencing = matchedTests.Count > 0;

            var target = SymbolResolver.Describe(namer, targetSymbol);
            // Fallback: naming convention matching
            if (matchedTests.Count == 0)
            {
                var targetName = targetSymbol.Kind == SymbolKind.Constructor
                    ? namer.ContainingType(targetSymbol)?.DisplayName ?? targetSymbol.DisplayName
                    : targetSymbol.DisplayName;
                matchedTests = allTestMethods.Where(t =>
                    t.DisplayName.Contains(targetName, StringComparison.OrdinalIgnoreCase) ||
                    TestClassName(namer, t).Contains(targetName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                message = matchedTests.Count > 0
                    ? $"No test method references {target}; these tests are matched by name only."
                    : $"No test method references {target}, and none is named after it.";
            }

            message = ResponseBuilder.JoinMessages(SymbolResolver.ResolutionNote(symbolStore, lookup), message);
            testMethods = matchedTests;
        }
        else if (testMethods.Count == 0)
        {
            message = frameworkName == "all"
                ? "No test methods were found (xUnit [Fact]/[Theory], NUnit [Test]/[TestCase], MSTest [TestMethod])."
                : $"No {frameworkName} test methods were found.";
        }

        testMethods = testMethods
            .OrderBy(t => t.FilePath, StringComparer.Ordinal).ThenBy(t => t.LineStart).ThenBy(t => t.Id)
            .ToList();
        object? summary = page.IsTruncatedFirstPage(testMethods.Count)
            ? new
            {
                ByClass = Paging.CountBy(testMethods, t => TestClassName(namer, t)),
                ByFile = Paging.CountBy(testMethods, t => t.FilePath)
            }
            : null;

        var results = page.Slice(testMethods).Select(t => (object)new
        {
            fully_qualified_name = namer.QualifiedName(t),
            display_name = t.DisplayName,
            test_framework = DetectFramework(t.Attributes),
            test_class = TestClassName(namer, t),
            file_path = t.FilePath,
            line_start = t.LineStart,
            line_end = t.LineEnd,
            references_target = referencing
        }).ToList();

        var freshness = testMethods.Count > 0 ? testMethods.Min(t => t.LastIndexedAt) : 0;
        return ResponseBuilder.BuildPage(results, testMethods.Count, page, freshness, ambiguity, readContext.Provenance, summary,
            message: message);
    }

    // The simple name of the class declaring a test method: from its documentation-ID key, else (an index from before
    // documentation-ID keys) its fully qualified name.
    private static string TestClassName(SymbolNamer namer, SymbolInfo test) =>
        namer.ContainingType(test)?.DisplayName ?? GetContainingTypeName(test.FullyQualifiedName);

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
