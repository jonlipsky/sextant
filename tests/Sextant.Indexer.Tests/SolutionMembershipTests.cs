using Microsoft.CodeAnalysis;

namespace Sextant.Indexer.Tests;

/// <summary>
/// Issue #124: a multi-solution (union) workspace has no solution file of its own, so the orchestrator
/// derives each selected solution's <c>solution → project</c> mapping from the solution's DECLARED projects
/// plus the loaded project-reference graph (<see cref="IndexOrchestrator.ComputeSolutionMembership"/>).
/// Hermetic: a synthetic <see cref="AdhocWorkspace"/> solution, no MSBuild.
/// </summary>
[TestClass]
public sealed class SolutionMembershipTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "sextant_membership");

    private static string P(string relative) => Path.GetFullPath(Path.Combine(Root, relative));

    [TestMethod]
    public void DeclaredProjects_MapAllTheirTfmVariants_AndSharedProjectsMapToEverySolution()
    {
        using var workspace = new AdhocWorkspace();
        var s = workspace.CurrentSolution;
        (s, var coreNet) = Add(s, "Core/Core.csproj", "Core(net10.0)");
        (s, var coreStd) = Add(s, "Core/Core.csproj", "Core(netstandard2.0)");
        (s, var app) = Add(s, "App/App.csproj", "App", coreNet);
        (s, var tool) = Add(s, "Tools/Tool.csproj", "Tool", coreNet);
        var ids = Ids(coreNet, 10, coreStd, 11, app, 20, tool, 30);

        var membership = IndexOrchestrator.ComputeSolutionMembership(s,
        [
            new SolutionMembership(P("App.slnx"), [P("Core/Core.csproj"), P("App/App.csproj")]),
            new SolutionMembership(P("Tools.slnx"), [P("Core/Core.csproj"), P("Tools/Tool.csproj")])
        ], ids);

        Assert.AreEqual(2, membership.Count);
        Assert.AreEqual(P("App.slnx"), membership[0].SolutionPath);
        CollectionAssert.AreEqual(new long[] { 10, 11, 20 }, membership[0].ProjectIds.ToArray(),
            "a declared multi-targeted project maps EVERY TFM variant");
        CollectionAssert.AreEqual(new long[] { 10, 11, 30 }, membership[1].ProjectIds.ToArray(),
            "the shared project maps to each solution that declares it; no solution gets another's projects");
    }

    [TestMethod]
    public void ProjectReferenceOnlyProjects_AreIncludedTransitively_PerTfm()
    {
        // App.slnx declares ONLY App; App → Lib → Core are pulled in by ProjectReference and are declared by
        // no solution. The single-solution path maps every workspace project (MSBuild loads references), so
        // the union path must too — else adding an unrelated solution would shrink App.slnx's scope.
        using var workspace = new AdhocWorkspace();
        var s = workspace.CurrentSolution;
        (s, var coreNet) = Add(s, "Core/Core.csproj", "Core(net10.0)");
        (s, var coreStd) = Add(s, "Core/Core.csproj", "Core(netstandard2.0)");
        (s, var lib) = Add(s, "Lib/Lib.csproj", "Lib", coreNet);
        (s, var app) = Add(s, "App/App.csproj", "App", lib);
        (s, var other) = Add(s, "Other/Other.csproj", "Other");
        var ids = Ids(coreNet, 10, coreStd, 11, lib, 15, app, 20, other, 40);

        var membership = IndexOrchestrator.ComputeSolutionMembership(s,
        [
            new SolutionMembership(P("App.slnx"), [P("App/App.csproj")]),
            new SolutionMembership(P("Other.slnx"), [P("Other/Other.csproj")])
        ], ids);

        CollectionAssert.AreEqual(new long[] { 10, 15, 20 }, membership[0].ProjectIds.ToArray(),
            "the transitive reference closure is mapped, following the Roslyn graph so only the REFERENCED " +
            "TFM variant (net10.0) is included — not the unreferenced netstandard2.0 variant");
        CollectionAssert.AreEqual(new long[] { 40 }, membership[1].ProjectIds.ToArray());
    }

    [TestMethod]
    public void SolutionWithNoLoadedProject_IsStillReported_WithNoMappings()
    {
        // A selected head none of whose declared projects loaded must still be recorded (so a solution: scope
        // over it resolves to a KNOWN empty solution and fails closed), just with no project ids.
        using var workspace = new AdhocWorkspace();
        var s = workspace.CurrentSolution;
        (s, var app) = Add(s, "App/App.csproj", "App");
        (s, _) = Add(s, "Stub/Stub.csproj", "Stub");

        var membership = IndexOrchestrator.ComputeSolutionMembership(s,
        [
            new SolutionMembership(P("App.slnx"), [P("App/App.csproj")]),
            new SolutionMembership(P("Mobile-ios.slnx"), [P("Mobile.iOS/Mobile.iOS.csproj")]),
            new SolutionMembership(P("Stub.slnx"), [P("Stub/Stub.csproj")])
        ], Ids(app, 20));

        Assert.AreEqual(3, membership.Count);
        CollectionAssert.AreEqual(new long[] { 20 }, membership[0].ProjectIds.ToArray());
        Assert.AreEqual(0, membership[1].ProjectIds.Count, "a head that never loaded maps to nothing");
        Assert.AreEqual(0, membership[2].ProjectIds.Count, "a project with no registered id is never mapped");
    }

    [TestMethod]
    public void DeclaredPaths_AreMatchedAfterFullPathNormalization()
    {
        using var workspace = new AdhocWorkspace();
        var s = workspace.CurrentSolution;
        (s, var app) = Add(s, "App/App.csproj", "App");

        var messy = Path.Combine(Root, "Build", "..", "App", ".", "App.csproj");
        var membership = IndexOrchestrator.ComputeSolutionMembership(s,
            [new SolutionMembership(P("App.slnx"), [messy])], Ids(app, 20));

        CollectionAssert.AreEqual(new long[] { 20 }, membership[0].ProjectIds.ToArray());
    }

    private static (Solution, ProjectId) Add(Solution solution, string relativePath, string name, params ProjectId[] references)
    {
        var id = ProjectId.CreateNewId();
        var info = ProjectInfo.Create(
            id, VersionStamp.Create(), name, name, LanguageNames.CSharp,
            filePath: P(relativePath),
            projectReferences: references.Select(r => new ProjectReference(r)));
        return (solution.AddProject(info), id);
    }

    private static Dictionary<ProjectId, long> Ids(params object[] pairs)
    {
        var map = new Dictionary<ProjectId, long>();
        for (var i = 0; i < pairs.Length; i += 2)
            map[(ProjectId)pairs[i]] = Convert.ToInt64(pairs[i + 1]);
        return map;
    }
}
