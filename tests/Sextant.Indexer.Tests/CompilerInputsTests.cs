using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Sextant.Indexer.Tests;

/// <summary>
/// <see cref="CompilerInputs"/> makes a loaded project's inputs match what <c>csc</c> sees (issue #294): one additional
/// document per path, and a visible diagnostic for an analyzer the indexer's Roslyn is too old to load.
/// </summary>
[TestClass]
public class CompilerInputsTests
{
    private string _root = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sextant_compiler_inputs_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [TestMethod]
    public void Normalize_KeepsOneAdditionalDocumentPerPath()
    {
        var (solution, projectId) = NewProject();
        var counter = Path.Combine(_root, "Counter.razor");
        var imports = Path.Combine(_root, "_Imports.razor");
        solution = AddAdditional(solution, projectId, counter);
        solution = AddAdditional(solution, projectId, imports);
        solution = AddAdditional(solution, projectId, counter);
        solution = AddAdditional(solution, projectId, Path.Combine(_root, ".", "_Imports.razor"));
        var messages = new List<string>();

        var normalized = CompilerInputs.Normalize(solution, messages.Add);

        var paths = normalized.GetProject(projectId)!.AdditionalDocuments.Select(d => d.FilePath).ToList();
        CollectionAssert.AreEqual(new[] { counter, imports }, paths, "the first document per full path stays, in order");
        Assert.AreEqual(1, messages.Count);
        StringAssert.Contains(messages[0], "Removed 2 duplicate additional file(s) from 1 project(s)");
    }

    [TestMethod]
    public void Normalize_ReturnsTheSameSolution_WhenNothingIsDuplicated()
    {
        var (solution, projectId) = NewProject();
        solution = AddAdditional(solution, projectId, Path.Combine(_root, "A.razor"));
        solution = AddAdditional(solution, projectId, Path.Combine(_root, "B.razor"));
        var messages = new List<string>();

        Assert.AreSame(solution, CompilerInputs.Normalize(solution, messages.Add));
        Assert.AreEqual(0, messages.Count);
    }

    [TestMethod]
    public void Normalize_ReportsAnAnalyzerBuiltForANewerCompiler_OncePerAssembly()
    {
        var newer = WriteAnalyzer("Newer.Generator", new Version(99, 0, 0, 0));
        var current = WriteAnalyzer("Current.Generator", typeof(Compilation).Assembly.GetName().Version!);
        var (solution, first) = NewProject("First");
        var second = ProjectId.CreateNewId("Second");
        solution = solution.AddProject(ProjectInfo.Create(second, VersionStamp.Create(), "Second", "Second", LanguageNames.CSharp));
        foreach (var id in new[] { first, second })
            solution = solution.WithProjectAnalyzerReferences(id, [Reference(newer), Reference(current)]);
        var messages = new List<string>();

        CompilerInputs.Normalize(solution, messages.Add);

        Assert.AreEqual(1, messages.Count, string.Join("\n", messages));
        StringAssert.Contains(messages[0], "Analyzer 'Newer.Generator.dll' needs Roslyn 99.0.0.0");
        StringAssert.Contains(messages[0], "cannot load in 2 project(s)");
    }

    [TestMethod]
    public void RequiredCompilerVersion_ReadsTheReferencedRoslyn_AndIgnoresUnreadableFiles()
    {
        Assert.AreEqual(new Version(5, 1, 2, 3), CompilerInputs.RequiredCompilerVersion(WriteAnalyzer("A", new Version(5, 1, 2, 3))));

        var notAnAssembly = Path.Combine(_root, "NotAnAssembly.dll");
        File.WriteAllText(notAnAssembly, "not a PE file");
        Assert.IsNull(CompilerInputs.RequiredCompilerVersion(notAnAssembly));
        Assert.IsNull(CompilerInputs.RequiredCompilerVersion(Path.Combine(_root, "Missing.dll")));
    }

    private static (Solution Solution, ProjectId Id) NewProject(string name = "Ui")
    {
        var id = ProjectId.CreateNewId(name);
        var solution = new AdhocWorkspace().CurrentSolution
            .AddProject(ProjectInfo.Create(id, VersionStamp.Create(), name, name, LanguageNames.CSharp));
        return (solution, id);
    }

    private static Solution AddAdditional(Solution solution, ProjectId projectId, string path) =>
        solution.AddAdditionalDocument(
            DocumentInfo.Create(DocumentId.CreateNewId(projectId), Path.GetFileName(path), filePath: path,
                loader: TextLoader.From(TextAndVersion.Create(SourceText.From("<p />"), VersionStamp.Create()))));

    private static AnalyzerFileReference Reference(string path) => new(path, new UnusedLoader());

    // An analyzer assembly that references a "Microsoft.CodeAnalysis" of the given version, as a generator built
    // against that compiler does.
    private string WriteAnalyzer(string name, Version roslyn)
    {
        var fakeRoslyn = CSharpCompilation.Create(
                "Microsoft.CodeAnalysis",
                [CSharpSyntaxTree.ParseText($"[assembly: System.Reflection.AssemblyVersion(\"{roslyn}\")] namespace Microsoft.CodeAnalysis {{ public class Api {{ }} }}")],
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var roslynImage = new MemoryStream();
        Assert.IsTrue(fakeRoslyn.Emit(roslynImage).Success);

        var analyzer = CSharpCompilation.Create(
            name,
            [CSharpSyntaxTree.ParseText("public class Generator { public Microsoft.CodeAnalysis.Api? Api; }")],
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromImage(roslynImage.ToArray())
            ],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var path = Path.Combine(_root, name + ".dll");
        var result = analyzer.Emit(path);
        Assert.IsTrue(result.Success, string.Join("\n", result.Diagnostics));
        return path;
    }

    private sealed class UnusedLoader : IAnalyzerAssemblyLoader
    {
        public void AddDependencyLocation(string fullPath) { }

        public Assembly LoadFromPath(string fullPath) =>
            throw new InvalidOperationException("the check reads metadata only and never loads the analyzer");
    }
}
