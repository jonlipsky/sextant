using Microsoft.CodeAnalysis;
using Sextant.Indexer;
using Sextant.Store;

namespace Sextant.Integration.Tests;

/// <summary>
/// Issue #294 through the REAL MSBuild loaders: code that uses Razor components, from the component library and from
/// a test project that lists its own <c>.razor</c> files again, must bind as it does in <c>dotnet build</c>. The
/// components exist only as Razor source generator output, so they bind only when the SDK's generator loads in the
/// indexer's Roslyn and is not broken by the duplicated additional files.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class GeneratedCodeBindingIntegrationTests : IDisposable
{
    private const string AcceptKey = "M:Ui.Tests.Mount.Accept(Microsoft.AspNetCore.Components.IComponent)";

    private readonly string _tempDir;

    public GeneratedCodeBindingIntegrationTests()
    {
        _ = IntegrationFixture.Instance; // MSBuildLocator registered before any Roslyn type loads
        _tempDir = Path.Combine(Path.GetTempPath(), $"sextant_generated_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => Sextant.TestSupport.SqliteTestDatabase.DeleteDirectory(_tempDir);

    [TestMethod]
    public async Task SolutionLoader_RazorComponentsBind_AndTheCallsUsingThemAreIndexedExactly()
    {
        var solutionPath = WriteCorpus();
        var diagnostics = new List<string>();

        var solution = await SolutionLoader.LoadSolutionAsync(solutionPath, diagnostics.Add);

        await AssertCompilesLikeTheBuild(solution, diagnostics);
        var dbPath = Path.Combine(_tempDir, "index.db");
        using var db = new IndexDatabase(dbPath);
        db.RunMigrations();
        await new IndexOrchestrator(db, useDocumentExtractor: true).IndexSolutionAsync(solution);
        var conn = db.GetConnection();
        var symbols = new SymbolStore(conn);
        var accept = symbols.GetBySymbolKeyInScope(AcceptKey).Single();
        var refs = new ReferenceStore(conn).GetBySymbolId(accept.Id);
        Assert.AreEqual(3, refs.Count, "each component passed to Accept is an indexed call site");
        Assert.IsFalse(refs.Any(r => r.IsCandidate), "and each bound exactly, so every component exists");

        // Generated code is bound against, not indexed: a component declared only in a .razor file has no symbol
        // row, while the code-behind partial of another one keeps its declarations.
        Assert.AreEqual(0, symbols.GetBySymbolKeyInScope("T:Ui.Counter").Count, "no row from generated source");
        Assert.AreEqual(1, symbols.GetBySymbolKeyInScope("M:Ui.Badge.Describe").Count, "the code-behind is indexed");
    }

    [TestMethod]
    public async Task MultiSolutionLoader_UnionOfTwoSolutions_RazorComponentsBind()
    {
        var root = Path.GetDirectoryName(WriteCorpus())!;
        var diagnostics = new List<string>();

        // Two solutions take the union path (one generated solution opened in one pass), not the single-solution one.
        var load = await MultiSolutionLoader.LoadAsync(
            [Path.Combine(root, "Ui.slnx"), Path.Combine(root, "Tests.slnx")], onDiagnostic: diagnostics.Add,
            scratchDirectory: Path.Combine(_tempDir, "scratch"));

        Assert.AreEqual(SolutionLoadModes.Union, load.LoadMode, string.Join("\n", diagnostics));
        await AssertCompilesLikeTheBuild(load.Solution, diagnostics);
    }

    [TestMethod]
    public async Task ProjectByProjectLoad_RazorComponentsBind()
    {
        var root = Path.GetDirectoryName(WriteCorpus())!;
        var diagnostics = new List<string>();

        var load = await SolutionLoader.LoadProjectsResilientlyAsync(
            [Path.Combine(root, "src", "Ui", "Ui.csproj"), Path.Combine(root, "tests", "Ui.Tests", "Ui.Tests.csproj")],
            diagnostics.Add);

        await AssertCompilesLikeTheBuild(load.Solution, diagnostics);
    }

    private static async Task AssertCompilesLikeTheBuild(Solution solution, List<string> diagnostics)
    {
        var tests = solution.Projects.Single(p => p.Name == "Ui.Tests");
        Assert.AreEqual(2, tests.AdditionalDocuments.Count(),
            "Wrapper.razor and _Imports.razor once each, though the project lists them twice: " +
            string.Join(", ", tests.AdditionalDocuments.Select(d => d.FilePath)));
        CollectionAssert.DoesNotContain(diagnostics.Select(d => d.Contains("needs Roslyn")).ToList(), true,
            "the SDK's Razor generator must load in the indexer's Roslyn: " + string.Join("\n", diagnostics));
        foreach (var name in new[] { "Ui", "Ui.Tests" })
        {
            var errors = (await solution.Projects.Single(p => p.Name == name).GetCompilationAsync())!.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error || d.Id == "CS8785").ToList();
            Assert.AreEqual(0, errors.Count, $"{name} compiles as in dotnet build: {string.Join("\n", errors)}");
        }
    }

    // Ui: a Razor class library with a component declared only in .razor (Counter) and one with a code-behind partial
    // that does not name its base type (Badge). Ui.Tests: a Razor test project that lists its .razor files again (as
    // ProcessStack.WebClient.Tests does) and passes every component to a method taking IComponent.
    private string WriteCorpus()
    {
        var root = Path.Combine(_tempDir, "repo");
        var ui = Path.Combine(root, "src", "Ui");
        WriteRazorProject(ui, "Ui", references: [], extraItems: string.Empty);
        Write(ui, "_Imports.razor", "@using Microsoft.AspNetCore.Components\n");
        Write(ui, "Counter.razor", "<p>@Start</p>\n@code {\n    [Parameter] public int Start { get; set; }\n}\n");
        Write(ui, "Badge.razor", "<span>@Describe()</span>\n");
        Write(ui, "Badge.razor.cs", "namespace Ui;\n\npublic partial class Badge\n{\n    public string Describe() => \"badge\";\n}\n");

        var tests = Path.Combine(root, "tests", "Ui.Tests");
        WriteRazorProject(tests, "Ui.Tests", references: ["../../src/Ui/Ui.csproj"],
            extraItems: "  <ItemGroup>\n    <RazorComponent Include=\"**\\*.razor\" />\n  </ItemGroup>\n");
        Write(tests, "_Imports.razor", "@using Microsoft.AspNetCore.Components\n");
        Write(tests, "Wrapper.razor", "@ChildContent\n@code {\n    [Parameter] public RenderFragment? ChildContent { get; set; }\n}\n");
        Write(tests, "Probe.cs", """
            using Microsoft.AspNetCore.Components;

            namespace Ui.Tests;

            public static class Mount
            {
                public static string Accept(IComponent component) => component.GetType().Name;
            }

            public class Probe
            {
                public string Counter() => Mount.Accept(new Ui.Counter { Start = 1 });
                public string Badge() => Mount.Accept(new Ui.Badge());
                public string Wrapped() => Mount.Accept(new Wrapper());
            }
            """);

        var solutionPath = Path.Combine(root, "Razor.slnx");
        File.WriteAllText(solutionPath, """
            <Solution>
              <Project Path="src/Ui/Ui.csproj" />
              <Project Path="tests/Ui.Tests/Ui.Tests.csproj" />
            </Solution>
            """);
        File.WriteAllText(Path.Combine(root, "Ui.slnx"), "<Solution>\n  <Project Path=\"src/Ui/Ui.csproj\" />\n</Solution>\n");
        File.WriteAllText(Path.Combine(root, "Tests.slnx"),
            "<Solution>\n  <Project Path=\"tests/Ui.Tests/Ui.Tests.csproj\" />\n</Solution>\n");
        GeneratedCorpusRestore.Restore(solutionPath);
        return solutionPath;
    }

    private static void WriteRazorProject(string dir, string name, string[] references, string extraItems)
    {
        var items = string.Concat(references.Select(r => $"    <ProjectReference Include=\"{r}\" />\n"));
        Write(dir, name + ".csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk.Razor">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <FrameworkReference Include="Microsoft.AspNetCore.App" />
            {items}  </ItemGroup>
            {extraItems}</Project>
            """);
    }

    private static void Write(string dir, string name, string content)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), content);
    }
}
