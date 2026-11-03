using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Sextant.Indexer;

namespace Sextant.Indexer.Tests;

/// <summary>
/// Issue #388: a phase compiles its projects in windows, each on its own solution, so dropping a window drops its
/// compilations; a single window (or none) leaves the source solution in use.
/// </summary>
[TestClass]
public sealed class CompilationWindowsTests
{
    [TestMethod]
    public void WindowsAreOffAtOrBelowTheWindowSize_AndTheSourceSolutionIsUsed()
    {
        using var workspace = new AdhocWorkspace();
        var (solution, ids) = Build(workspace, 3);

        var windows = new CompilationWindows(solution, ids, 3);
        var (used, project) = windows.Of(ids[1]);

        Assert.AreSame(solution, used);
        Assert.AreEqual(ids[1], project!.Id);
        Assert.IsTrue(windows.WhenReached(2).IsCompleted, "nothing to hold when windows are off");
    }

    [TestMethod]
    public async Task EachWindowCompilesOnItsOwnSolution_WithTheSameContent()
    {
        using var workspace = new AdhocWorkspace();
        var (solution, ids) = Build(workspace, 4);
        var windows = new CompilationWindows(solution, ids, 2);

        var (first, p0) = windows.Of(ids[0]);
        var (sameWindow, p1) = windows.Of(ids[1]);
        var (second, p2) = windows.Of(ids[2]);

        Assert.AreSame(first, sameWindow);
        Assert.AreNotSame(first, second);
        Assert.AreNotSame(solution, first);
        Assert.AreEqual(0, windows.WindowOf(ids[1]));
        Assert.AreEqual(1, windows.WindowOf(ids[2]));

        // The windowed project compiles to the same declarations as the source one, and shares no compilation with it.
        var source = (await solution.GetProject(ids[2])!.GetCompilationAsync())!;
        var windowed = (await p2!.GetCompilationAsync())!;
        Assert.AreNotSame(source, windowed);
        Assert.AreEqual(
            source.SyntaxTrees.Single().GetText().ToString(),
            windowed.SyntaxTrees.Single().GetText().ToString());
        CollectionAssert.AreEqual(
            source.GlobalNamespace.GetMembers().Select(m => m.Name).OrderBy(n => n).ToList(),
            windowed.GlobalNamespace.GetMembers().Select(m => m.Name).OrderBy(n => n).ToList());
        Assert.IsNotNull(p0);
        Assert.IsNotNull(p1);
    }

    [TestMethod]
    public void ReleasedWindowsAreRebuiltIfAskedForAgain()
    {
        using var workspace = new AdhocWorkspace();
        var (solution, ids) = Build(workspace, 4);
        var windows = new CompilationWindows(solution, ids, 2);

        var (first, _) = windows.Of(ids[0]);
        windows.ReleaseBefore(1);
        var (again, _) = windows.Of(ids[0]);

        Assert.AreNotSame(first, again);
    }

    [TestMethod]
    public async Task APrefetchIsHeldUntilThePhaseReachesItsWindow()
    {
        using var workspace = new AdhocWorkspace();
        var (solution, ids) = Build(workspace, 4);
        var windows = new CompilationWindows(solution, ids, 2);

        var held = windows.WhenReached(2);
        Assert.IsFalse(held.IsCompleted);

        windows.Reached(1);
        await held.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(windows.WhenReached(0).IsCompleted, "earlier windows are reached too");
    }

    [TestMethod]
    public void ADocumentWithoutAFilePath_FallsBackToTheSourceSolution()
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("P0", LanguageNames.CSharp);
        var solution = project.AddDocument("a.cs", SourceText.From("class A {}")).Project.Solution;
        var other = solution.AddProject("P1", "P1", LanguageNames.CSharp).Solution;
        var ids = other.Projects.Select(p => p.Id).ToList();

        var windows = new CompilationWindows(other, ids, 1);
        var (used, _) = windows.Of(ids[0]);

        Assert.AreSame(other, used, "a document that cannot be reloaded from disk keeps the source solution");
    }

    private static (Solution Solution, List<ProjectId> Ids) Build(AdhocWorkspace workspace, int count)
    {
        var dir = Path.Combine(Path.GetTempPath(), "sextant-windows-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var solution = workspace.CurrentSolution;
        var ids = new List<ProjectId>();
        for (var i = 0; i < count; i++)
        {
            var file = Path.Combine(dir, $"C{i}.cs");
            File.WriteAllText(file, $"namespace N{i} {{ public class C{i} {{ public int M() => {i}; }} }}");
            var projectId = ProjectId.CreateNewId($"P{i}");
            var info = ProjectInfo.Create(
                projectId, VersionStamp.Create(), $"P{i}", $"P{i}", LanguageNames.CSharp,
                filePath: Path.Combine(dir, $"P{i}.csproj"),
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
                documents:
                [
                    DocumentInfo.Create(
                        DocumentId.CreateNewId(projectId), $"C{i}.cs",
                        filePath: file,
                        loader: new FileTextLoader(file, null))
                ]);
            solution = solution.AddProject(info);
            ids.Add(projectId);
        }

        return (solution, ids);
    }
}
