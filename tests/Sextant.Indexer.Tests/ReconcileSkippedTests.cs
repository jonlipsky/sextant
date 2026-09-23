using Microsoft.CodeAnalysis;
using Sextant.Indexer;

namespace Sextant.Indexer.Tests;

/// <summary>
/// Pins the accuracy of <see cref="SolutionLoader.ReconcileSkipped"/> — the core of the issue #90
/// completeness diagnostic. A skipped project must be reported when (and only when) it genuinely failed
/// to load: an empty stub named by a failure, or a project absent from the solution. A valid empty
/// project must NOT be reported skipped, a failure that only mentions a shared file name must NOT be
/// misattributed to the wrong sibling, and a typed owning-project path must attribute exactly.
/// </summary>
[TestClass]
public sealed class ReconcileSkippedTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "sextant-reconcile");

    [TestMethod]
    public void PresentProjectWithDocuments_IsNotSkipped_EvenWhenAFailureNamesIt()
    {
        var a = Abs("A", "A.csproj");
        var solution = SolutionWith((a, withDocument: true));
        var failures = new[] { new SolutionLoader.LoadFailure(null, null, $"warning about {a}") };

        var skipped = SolutionLoader.ReconcileSkipped([a], solution, failures, [], null);

        Assert.AreEqual(0, skipped.Count,
            "a project that loaded real content must not be reported skipped just because a diagnostic mentions it");
    }

    [TestMethod]
    public void EmptyStubNamedByFullPathFailure_IsSkipped()
    {
        var a = Abs("A", "A.csproj");
        var solution = SolutionWith((a, withDocument: false));
        var failures = new[] { new SolutionLoader.LoadFailure(null, null, $"Msbuild failed when processing the file '{a}'") };

        var skipped = SolutionLoader.ReconcileSkipped([a], solution, failures, [], null);

        Assert.AreEqual(1, skipped.Count);
        Assert.AreEqual(Path.GetFullPath(a), Path.GetFullPath(skipped[0].ProjectPath));
        StringAssert.Contains(skipped[0].Reason, "Msbuild failed");
    }

    [TestMethod]
    public void ValidEmptyProject_WithNoFailure_IsNotSkipped()
    {
        var a = Abs("A", "A.csproj");
        var solution = SolutionWith((a, withDocument: false));

        var skipped = SolutionLoader.ReconcileSkipped(
            [a], solution, Array.Empty<SolutionLoader.LoadFailure>(), [], null);

        Assert.AreEqual(0, skipped.Count,
            "a legitimately empty project with no failure diagnostic must not be reported skipped");
    }

    [TestMethod]
    public void ProjectAbsentFromSolution_IsSkipped()
    {
        var a = Abs("A", "A.csproj");
        var b = Abs("B", "B.csproj");
        var solution = SolutionWith((a, withDocument: true)); // b never loaded

        var skipped = SolutionLoader.ReconcileSkipped(
            [a, b], solution, Array.Empty<SolutionLoader.LoadFailure>(), [], null);

        Assert.AreEqual(1, skipped.Count);
        Assert.AreEqual(Path.GetFullPath(b), Path.GetFullPath(skipped[0].ProjectPath));
    }

    [TestMethod]
    public void FailureNamingOnlyASharedFileName_IsNotMisattributedToASibling()
    {
        // Two projects share the file name Build.csproj; a failure that mentions only that file name
        // cannot be pinned to one of them, so neither may be reported skipped (refusing the ambiguous
        // file-name fallback), rather than blaming the wrong sibling.
        var one = Abs("one", "Build.csproj");
        var two = Abs("two", "Build.csproj");
        var solution = SolutionWith((one, withDocument: false), (two, withDocument: false));
        var failures = new[] { new SolutionLoader.LoadFailure(null, null, "The project file Build.csproj could not be loaded") };

        var skipped = SolutionLoader.ReconcileSkipped([one, two], solution, failures, [], null);

        Assert.AreEqual(0, skipped.Count,
            "a diagnostic naming only a shared file name must not be misattributed to a sibling project");
    }

    [TestMethod]
    public void TypedOwningProjectPath_AttributesExactly_AcrossSharedFileNames()
    {
        // Same shared-file-name shape, but this time the failure carries the typed owning-project path,
        // so exactly the failing sibling is reported skipped — not both, not the wrong one.
        var one = Abs("one", "Build.csproj");
        var two = Abs("two", "Build.csproj");
        var solution = SolutionWith((one, withDocument: false), (two, withDocument: false));
        var failures = new[]
        {
            new SolutionLoader.LoadFailure(null, Path.GetFullPath(one), "The project file could not be loaded")
        };

        var skipped = SolutionLoader.ReconcileSkipped([one, two], solution, failures, [], null);

        Assert.AreEqual(1, skipped.Count);
        Assert.AreEqual(Path.GetFullPath(one), Path.GetFullPath(skipped[0].ProjectPath));
    }

    [TestMethod]
    public void MultiTargetedProject_OneVariantLoaded_IsNotSkipped_EvenWhenAFailureNamesIt()
    {
        // A multi-targeted project appears once per TFM under the SAME file path. If one target variant
        // loaded content, the project indexed and must not be reported skipped — regardless of the order
        // Roslyn lists the variants or a failure diagnostic naming the empty variant.
        var lib = Abs("Lib", "Lib.csproj");
        var solution = SolutionWith((lib, withDocument: false), (lib, withDocument: true));
        var failures = new[] { new SolutionLoader.LoadFailure(null, Path.GetFullPath(lib), "one target failed") };

        var skipped = SolutionLoader.ReconcileSkipped([lib], solution, failures, [], null);

        Assert.AreEqual(0, skipped.Count,
            "a multi-targeted project with at least one loaded variant must not be reported skipped");
    }

    [TestMethod]
    public void MultiTargetedProject_AllVariantsEmptyAndFailed_IsSkipped()
    {
        var lib = Abs("Lib", "Lib.csproj");
        var solution = SolutionWith((lib, withDocument: false), (lib, withDocument: false));
        var failures = new[] { new SolutionLoader.LoadFailure(null, Path.GetFullPath(lib), "all targets failed to load") };

        var skipped = SolutionLoader.ReconcileSkipped([lib], solution, failures, [], null);

        Assert.AreEqual(1, skipped.Count);
        Assert.AreEqual(Path.GetFullPath(lib), Path.GetFullPath(skipped[0].ProjectPath));
    }

    [TestMethod]
    public void TypedProjectId_ResolvedLate_AttributesAnEmptyStub()
    {
        // The failure carries only a ProjectId (path unresolved at event time); ReconcileSkipped must
        // resolve it against the final solution and attribute the empty stub as skipped.
        var a = Abs("A", "A.csproj");
        var (solution, projectId) = SolutionWithIds((a, withDocument: false));
        var failures = new[] { new SolutionLoader.LoadFailure(projectId, null, "The project file could not be loaded") };

        var skipped = SolutionLoader.ReconcileSkipped([a], solution, failures, [], null);

        Assert.AreEqual(1, skipped.Count);
        Assert.AreEqual(Path.GetFullPath(a), Path.GetFullPath(skipped[0].ProjectPath));
    }

    [TestMethod]
    public void TypedFailureForOneProject_MentioningASibling_DoesNotSkipTheSibling()
    {
        // Project A's diagnostic is typed to A but its MESSAGE also names B.csproj (e.g. a missing
        // reference). B loaded as a valid empty project with no failure of its own, so it must NOT be
        // marked skipped merely because A's message mentions it — a typed failure is attributable only to
        // its own owning project, never to a sibling via message substring.
        var a = Abs("A", "A.csproj");
        var b = Abs("B", "B.csproj");
        var solution = SolutionWith((a, withDocument: true), (b, withDocument: false));
        var failures = new[]
        {
            new SolutionLoader.LoadFailure(null, Path.GetFullPath(a),
                "Project A.csproj could not resolve a reference to B.csproj")
        };

        var skipped = SolutionLoader.ReconcileSkipped([a, b], solution, failures, [], null);

        Assert.AreEqual(0, skipped.Count,
            "a typed failure owned by A must not mark sibling B skipped merely by naming it in the message");
    }

    private static string Abs(string dir, string file) => Path.Combine(Root, dir, file);

    private static Solution SolutionWith(params (string Path, bool WithDocument)[] projects) =>
        BuildSolution(projects).Solution;

    private static (Solution Solution, ProjectId FirstId) SolutionWithIds(
        params (string Path, bool WithDocument)[] projects)
    {
        var built = BuildSolution(projects);
        return (built.Solution, built.ProjectIds[0]);
    }

    private static (Solution Solution, List<ProjectId> ProjectIds) BuildSolution(
        (string Path, bool WithDocument)[] projects)
    {
        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution;
        var ids = new List<ProjectId>();
        foreach (var (path, withDocument) in projects)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var projectId = ProjectId.CreateNewId();
            ids.Add(projectId);
            var documents = withDocument
                ? new[]
                {
                    DocumentInfo.Create(
                        DocumentId.CreateNewId(projectId), "Class1.cs",
                        filePath: Path.Combine(Path.GetDirectoryName(path)!, "Class1.cs"))
                }
                : Array.Empty<DocumentInfo>();
            var info = ProjectInfo.Create(
                projectId, VersionStamp.Create(), name, name, LanguageNames.CSharp,
                filePath: path, documents: documents);
            solution = solution.AddProject(info);
        }
        return (solution, ids);
    }
}
