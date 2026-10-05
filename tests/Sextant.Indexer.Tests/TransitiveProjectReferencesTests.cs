using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Sextant.Indexer.Tests;

/// <summary>
/// An unrestored project only gets its DIRECT ProjectReferences from the design-time build (the SDK's transitive
/// closure comes from <c>obj/project.assets.json</c>), so a type it reaches through another project does not bind.
/// <see cref="TransitiveProjectReferences"/> closes that gap for unrestored projects and leaves restored ones alone.
/// </summary>
[TestClass]
public class TransitiveProjectReferencesTests
{
    private string _root = null!;

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sextant_closure_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private const string AppSource = """
        namespace App
        {
            public class Controller
            {
                public string Run(Core.IStore store) => store.Ensure("x");
            }
        }
        """;

    private sealed record Chain(Solution Solution, ProjectId Abs, ProjectId Core, ProjectId App);

    // Abs ← Core ← App: Core's method has an optional Abs-typed parameter, App references only Core.
    private Chain BuildChain(bool appHasAssets = false, ProjectId? absTwin = null)
    {
        var workspace = new AdhocWorkspace();
        var abs = ProjectId.CreateNewId("Abs");
        var core = ProjectId.CreateNewId("Core");
        var app = ProjectId.CreateNewId("App");
        var solution = workspace.CurrentSolution;
        solution = Add(solution, abs, "Abs", "Abs", "namespace Abs { public enum OwnerKind { User } }");
        solution = Add(solution, core, "Core", "Core",
            "namespace Core { public interface IStore { string Ensure(string n, Abs.OwnerKind k = Abs.OwnerKind.User); } }");
        solution = Add(solution, app, "App", "App", AppSource);
        if (absTwin != null)
            solution = Add(solution, absTwin, "Abs(net9.0)", "Abs", "namespace Abs { public enum OwnerKind { User } }",
                assemblyName: "Abs");
        solution = solution.AddProjectReference(core, new ProjectReference(abs))
            .AddProjectReference(app, new ProjectReference(core));
        if (absTwin != null)
            solution = solution.AddProjectReference(core, new ProjectReference(absTwin));
        if (appHasAssets)
        {
            Directory.CreateDirectory(Path.Combine(_root, "App", "obj"));
            File.WriteAllText(Path.Combine(_root, "App", "obj", "project.assets.json"), "{}");
        }
        return new Chain(solution, abs, core, app);
    }

    private Solution Add(Solution solution, ProjectId id, string name, string dir, string source, string? assemblyName = null)
    {
        var projectDir = Path.Combine(_root, dir);
        Directory.CreateDirectory(projectDir);
        var projectPath = Path.Combine(projectDir, dir + ".csproj");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        return solution
            .AddProject(ProjectInfo.Create(id, VersionStamp.Default, name, assemblyName ?? name, LanguageNames.CSharp,
                filePath: projectPath,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]))
            .AddDocument(DocumentId.CreateNewId(id), name + ".cs", SourceText.From(source),
                filePath: Path.Combine(projectDir, name + ".cs"));
    }

    private static async Task<IReadOnlyList<Diagnostic>> Errors(Solution solution, ProjectId id) =>
        (await solution.GetProject(id)!.GetCompilationAsync())!.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();

    [TestMethod]
    public async Task UnrestoredProject_GetsItsTransitiveReferences_AndItsCallsBind()
    {
        var chain = BuildChain();
        Assert.IsTrue((await Errors(chain.Solution, chain.App)).Any(d => d.Id == "CS0012"),
            "without the closure App cannot bind the call (the incident's shape)");
        var messages = new List<string>();

        var closed = TransitiveProjectReferences.Close(chain.Solution, messages.Add);

        CollectionAssert.AreEquivalent(new[] { chain.Core, chain.Abs },
            closed.GetProject(chain.App)!.ProjectReferences.Select(r => r.ProjectId).ToArray());
        Assert.AreEqual(0, (await Errors(closed, chain.App)).Count);
        Assert.AreEqual(
            "Added 1 transitive project reference(s) to 1 unrestored project(s) so types reached through their " +
            "project references bind.", messages.Single());
    }

    [TestMethod]
    public void RestoredProject_IsLeftExactlyAsLoaded()
    {
        var chain = BuildChain(appHasAssets: true);

        var closed = TransitiveProjectReferences.Close(chain.Solution);

        Assert.AreEqual(1, closed.GetProject(chain.App)!.ProjectReferences.Count(),
            "a restored project already has the SDK's own closure, which honours PrivateAssets");
    }

    [TestMethod]
    public void HealthySolution_IsReturnedUnchanged()
    {
        var chain = BuildChain();
        var restoredEverywhere = TransitiveProjectReferences.Close(chain.Solution, _ => false, onDiagnostic: null);
        Assert.AreSame(chain.Solution, restoredEverywhere);
    }

    [TestMethod]
    public void MultiTargetedDependency_IsAddedOnce()
    {
        // Core references both target-framework variants of Abs (two Roslyn projects, one project file). Adding
        // both to App would give its compilation two assemblies of the same identity.
        var chain = BuildChain(absTwin: ProjectId.CreateNewId("Abs(net9.0)"));

        var closed = TransitiveProjectReferences.Close(chain.Solution, _ => true, onDiagnostic: null);

        var references = closed.GetProject(chain.App)!.ProjectReferences.Select(r => r.ProjectId).ToList();
        Assert.AreEqual(2, references.Count, "Core plus exactly one Abs variant");
        Assert.AreEqual(chain.Abs, references.Single(r => r != chain.Core), "the first reachable variant, in reference order");
    }

    [TestMethod]
    public void DependencyAlreadyPresentAsMetadata_IsNotAddedAgain()
    {
        var chain = BuildChain();
        var absImage = Path.Combine(_root, "Abs.dll");
        var absCompilation = CSharpCompilation.Create("Abs",
            [CSharpSyntaxTree.ParseText("namespace Abs { public enum OwnerKind { User } }")],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.IsTrue(absCompilation.Emit(absImage).Success);
        var solution = chain.Solution.AddMetadataReference(chain.App, MetadataReference.CreateFromFile(absImage));

        var closed = TransitiveProjectReferences.Close(solution, _ => true, onDiagnostic: null);

        Assert.AreEqual(1, closed.GetProject(chain.App)!.ProjectReferences.Count(),
            "an assembly of the same name is already referenced, so adding the project would duplicate its identity");
    }

    [TestMethod]
    public async Task ProjectWithADanglingReference_IsStillClosed_AndKeepsTheDanglingReference()
    {
        // The prod shape (TouchDraw2ProForiOS, Valuosity.Reports): App keeps a reference to a project the workspace
        // does not contain. Roslyn's AddProjectReferences resolves every existing reference and threw
        // "Unexpected null - file SolutionState.cs line 363", so App was left without its closure.
        var chain = BuildChain();
        var ghost = ProjectId.CreateNewId("Ghost");
        var solution = Add(chain.Solution, ghost, "Ghost", "Ghost", "namespace Ghost { public class G { } }")
            .AddProjectReference(chain.App, new ProjectReference(ghost))
            .RemoveProject(ghost);
        Assert.IsTrue(solution.GetProject(chain.App)!.AllProjectReferences.Any(r => r.ProjectId == ghost),
            "the fixture really has a dangling reference");
        var messages = new List<string>();

        var closed = TransitiveProjectReferences.Close(solution, _ => true, messages.Add);

        var app = closed.GetProject(chain.App)!;
        CollectionAssert.AreEquivalent(new[] { chain.Core, chain.Abs }, app.ProjectReferences.Select(r => r.ProjectId).ToArray());
        Assert.IsTrue(app.AllProjectReferences.Any(r => r.ProjectId == ghost), "the dangling reference is kept as loaded");
        Assert.AreEqual(0, (await Errors(closed, chain.App)).Count);
        Assert.IsFalse(messages.Any(m => m.StartsWith("Could not add", StringComparison.Ordinal)), string.Join(" | ", messages));
    }

    [TestMethod]
    public void DeepChain_ClosesEveryLevel_InBreadthFirstOrder()
    {
        var workspace = new AdhocWorkspace();
        var ids = Enumerable.Range(0, 4).Select(i => ProjectId.CreateNewId($"P{i}")).ToArray();
        var solution = workspace.CurrentSolution;
        for (var i = 0; i < ids.Length; i++)
            solution = Add(solution, ids[i], $"P{i}", $"P{i}", $"namespace P{i} {{ public class C{i} {{ }} }}");
        for (var i = 0; i < ids.Length - 1; i++)
            solution = solution.AddProjectReference(ids[i], new ProjectReference(ids[i + 1]));

        var closed = TransitiveProjectReferences.Close(solution, _ => true, onDiagnostic: null);

        CollectionAssert.AreEqual(new[] { ids[1], ids[2], ids[3] },
            closed.GetProject(ids[0])!.ProjectReferences.Select(r => r.ProjectId).ToArray());
        CollectionAssert.AreEqual(new[] { ids[2], ids[3] },
            closed.GetProject(ids[1])!.ProjectReferences.Select(r => r.ProjectId).ToArray());
        Assert.AreEqual(0, closed.GetProject(ids[3])!.ProjectReferences.Count());
    }
}
