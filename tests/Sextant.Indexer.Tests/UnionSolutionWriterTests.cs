using Microsoft.CodeAnalysis;
using Sextant.Indexer;

namespace Sextant.Indexer.Tests;

/// <summary>
/// Issue #268: the multi-solution union loads in one pass over a solution generated outside the checkout, next to a
/// copy of the selected solutions' global.json, for every project that evaluates there exactly as it would opened
/// alone (issue #113's model: same SDK, same msbuild-sdks it uses, no $(SolutionDir)); the rest, and whatever
/// references them, are held back to be opened individually.
/// </summary>
[TestClass]
public sealed class UnionSolutionWriterTests
{
    private string _root = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sextant_union_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void TestCleanup() => Directory.Delete(_root, recursive: true);

    [TestMethod]
    public void Render_ListsTheUnionInOrder_RelativeToTheSolution_WithForwardSlashes()
    {
        var xml = UnionSolutionWriter.Render(_root,
            [Path.Combine(_root, "src", "B", "B.csproj"), Path.Combine(_root, "src", "A & Co", "A.csproj")]);

        Assert.AreEqual(
            "<Solution>\n" +
            "  <Project Path=\"src/B/B.csproj\" />\n" +
            "  <Project Path=\"src/A &amp; Co/A.csproj\" />\n" +
            "</Solution>\n",
            xml);
    }

    [TestMethod]
    public void Write_PutsTheSolutionNextToACopyOfTheSharedGlobalJson()
    {
        var globalJson = Write("repo/global.json", "{ \"sdk\": { \"version\": \"10.0.100\" } }");
        var project = Touch("repo", "src", "A", "A.csproj");
        var scratch = Path.Combine(_root, "scratch", "union");

        var solution = UnionSolutionWriter.Write(scratch, [project], globalJson);

        Assert.AreEqual(scratch, Path.GetDirectoryName(solution));
        Assert.AreEqual(File.ReadAllText(globalJson), File.ReadAllText(Path.Combine(scratch, "global.json")),
            "the generated solution resolves the same SDK and msbuild-sdks as the selected solutions");
        StringAssert.Contains(File.ReadAllText(solution), "../../repo/src/A/A.csproj");
    }

    [TestMethod]
    public void Write_WithNoSharedGlobalJson_WritesAnEmptyOne_SoNothingAboveScratchApplies()
    {
        var scratch = Path.Combine(_root, "scratch");
        Write("global.json", "{ \"sdk\": { \"version\": \"9.0.100\" } }"); // above scratch, but not the solutions'

        UnionSolutionWriter.Write(scratch, [Touch("repo", "A.csproj")], globalJson: null);

        Assert.AreEqual("{}\n", File.ReadAllText(Path.Combine(scratch, "global.json")));
    }

    [TestMethod]
    public void Partition_PutsEveryProjectInOnePass_WhenAllShareTheSolutionsGlobalJson()
    {
        var shared = Touch("global.json");
        var projects = new[] { Touch("src", "A", "A.csproj"), Touch("tools", "B", "B.csproj") };

        var partition = UnionSolutionWriter.Partition([Touch("A.slnx"), Touch("tools", "B.slnx")], projects, new Sdks("10.0.100"));

        Assert.IsTrue(partition.CanLoadInOnePass, partition.Reason);
        Assert.AreEqual(shared, partition.GlobalJson);
        CollectionAssert.AreEqual(projects, partition.OnePass.ToArray());
        Assert.IsEmpty(partition.Individually);
    }

    [TestMethod]
    public void Partition_KeepsAProjectInOnePass_WhenItsOwnGlobalJsonResolvesTheSameSdk_AndPinsNoSdkItUses()
    {
        // ProcessStack's submodule shape: the nested global.json pins only msbuild-sdks these projects do not use.
        Write("global.json", "{ \"sdk\": { \"version\": \"10.0.100\", \"rollForward\": \"latestFeature\" } }");
        Write("sub/global.json", "{ \"msbuild-sdks\": { \"MSBuild.Sdk.Extras\": \"3.0.44\" } }");
        var lib = Write("sub/src/Lib/Lib.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var partition = UnionSolutionWriter.Partition(
            [Touch("A.slnx"), Touch("B.slnx")], [Touch("src", "A", "A.csproj"), lib], new Sdks("10.0.401"));

        Assert.HasCount(2, partition.OnePass, partition.Reason);
        Assert.IsEmpty(partition.Individually);
    }

    [TestMethod]
    public void Partition_HoldsBackAProjectThatUsesAnMsBuildSdkItsGlobalJsonPinsDifferently_EvenThroughAnImport()
    {
        Write("sub/global.json", "{ \"msbuild-sdks\": { \"MSBuild.Sdk.Extras\": \"3.0.44\" } }");
        Write("sub/eng/Common.props", "<Project><Sdk Name=\"MSBuild.Sdk.Extras\" /></Project>");
        Write("sub/Directory.Build.props", "<Project><Import Project=\"$(MSBuildThisFileDirectory)eng/Common.props\" /></Project>");
        var head = Write("sub/Head/Head.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var app = Touch("src", "A", "A.csproj");

        var partition = UnionSolutionWriter.Partition([Touch("A.slnx"), Touch("B.slnx")], [head, app], new Sdks("10.0.100"));

        CollectionAssert.AreEqual(new[] { app }, partition.OnePass.ToArray());
        CollectionAssert.AreEqual(new[] { head }, partition.Individually.ToArray());
        StringAssert.Contains(partition.Reason, "MSBuild.Sdk.Extras");
    }

    [TestMethod]
    public void Partition_HoldsBackAProjectWhoseEvaluationReadsSolutionDir()
    {
        // A solution load sets $(SolutionDir) (to scratch, here); opening the project alone leaves it unset.
        Write("tests/Directory.Build.props",
            "<Project><Import Project=\"$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))\" /></Project>");
        Write("Directory.Build.props", "<Project><PropertyGroup><Out>$(SolutionDir)artifacts</Out></PropertyGroup></Project>");
        var test = Touch("tests", "T", "T.csproj");

        var partition = UnionSolutionWriter.Partition([Touch("A.slnx"), Touch("B.slnx")], [test], new Sdks("10.0.100"));

        Assert.IsFalse(partition.CanLoadInOnePass);
        StringAssert.Contains(partition.Reason, "$(SolutionDir)");
    }

    [TestMethod]
    public void Partition_ResolvesGetPathOfFileAbove_AndHoldsBackAnImportItCannotResolve()
    {
        Write("tests/Directory.Build.props",
            "<Project><Import Project=\"$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))\" /></Project>");
        Write("Directory.Build.props", "<Project />");
        var resolvable = Touch("tests", "T", "T.csproj");
        var dynamic = Write("samples/S/S.csproj", "<Project><Import Project=\"$(SomeProperty)\" /></Project>");

        var partition = UnionSolutionWriter.Partition([Touch("A.slnx"), Touch("B.slnx")], [resolvable, dynamic], new Sdks("10.0.100"));

        CollectionAssert.AreEqual(new[] { resolvable }, partition.OnePass.ToArray(), partition.Reason);
        CollectionAssert.AreEqual(new[] { dynamic }, partition.Individually.ToArray());
        StringAssert.Contains(partition.Reason, "cannot be resolved statically");
    }

    [TestMethod]
    public void Partition_HoldsBackAProjectThatReferencesAHeldBackOne_Transitively()
    {
        // A legacy sample with no SDK name of its own still references a held-back head: loading it in the generated
        // solution would evaluate that head there too.
        Write("sub/global.json", "{ \"msbuild-sdks\": { \"MSBuild.Sdk.Extras\": \"3.0.44\" } }");
        var head = Write("sub/Head/Head.csproj", "<Project Sdk=\"MSBuild.Sdk.Extras\" />");
        var sample = Write("sub/Sample/Sample.csproj",
            "<Project><ItemGroup><ProjectReference Include=\"..\\Head\\Head.csproj\" /></ItemGroup></Project>");
        var host = Write("sub/Host/Host.csproj",
            "<Project><ItemGroup><ProjectReference Include=\"../Sample/Sample.csproj\" Condition=\"'$(X)' == ''\" /></ItemGroup></Project>");
        var app = Touch("src", "A", "A.csproj");

        var partition = UnionSolutionWriter.Partition([Touch("A.slnx"), Touch("B.slnx")], [app, host, sample, head], new Sdks("10.0.100"));

        CollectionAssert.AreEqual(new[] { app }, partition.OnePass.ToArray());
        CollectionAssert.AreEqual(new[] { host, sample, head }, partition.Individually.ToArray(), "union order is kept");
    }

    [TestMethod]
    public void Partition_HoldsBackAProjectWhoseOwnGlobalJsonResolvesAnotherSdk_OrFailsToResolve()
    {
        Write("global.json", "{ \"sdk\": { \"version\": \"10.0.100\" } }");
        Write("src/Old/global.json", "{ \"sdk\": { \"version\": \"9.0.100\" } }");
        Write("src/Pinned/global.json", "{ \"sdk\": { \"version\": \"10.0.999\", \"rollForward\": \"disable\" } }");
        var old = Write("src/Old/Old.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var pinned = Write("src/Pinned/Pinned.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var probe = new Sdks("10.0.100")
        {
            [Path.Combine(_root, "src", "Old", "global.json")] = "9.0.100",
            [Path.Combine(_root, "src", "Pinned", "global.json")] = null // unsatisfiable: #113 isolates it per project
        };

        var partition = UnionSolutionWriter.Partition(
            [Touch("A.slnx"), Touch("B.slnx")], [Touch("src", "A", "A.csproj"), old, pinned], probe);

        CollectionAssert.AreEqual(new[] { old, pinned }, partition.Individually.ToArray());
        StringAssert.Contains(partition.Reason, "Old.csproj");
        StringAssert.Contains(partition.Reason, "different SDK");
    }

    [TestMethod]
    public void Partition_UsesTheCommonDirectorysGlobalJson_AndJudgesASolutionsOwnPinProjectByProject()
    {
        // ProcessStack's shape: the submodule's own solutions sit under their own global.json. Their projects are judged
        // one by one against the common directory's, exactly as opening each would resolve its own.
        var root = Write("global.json", "{ \"sdk\": { \"version\": \"10.0.100\" } }");
        Write("sub/global.json", "{ \"msbuild-sdks\": { \"MSBuild.Sdk.Extras\": \"3.0.44\" } }");
        var lib = Write("sub/src/Lib/Lib.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var head = Write("sub/src/Head/Head.csproj", "<Project Sdk=\"MSBuild.Sdk.Extras\" />");

        var partition = UnionSolutionWriter.Partition(
            [Touch("App.slnx"), Touch("sub", "Sub.sln")], [Touch("src", "A", "A.csproj"), lib, head], new Sdks("10.0.100"));

        Assert.AreEqual(root, partition.GlobalJson);
        Assert.HasCount(2, partition.OnePass);
        CollectionAssert.AreEqual(new[] { head }, partition.Individually.ToArray());
    }

    [TestMethod]
    public void Partition_LoadsNothingInOnePass_WhenTheSharedGlobalJsonIsLocationDependent()
    {
        Write("global.json", "{ \"sdk\": { \"version\": \"10.0.100\", \"paths\": [\".dotnet\"] } }");

        var partition = UnionSolutionWriter.Partition([Touch("A.slnx"), Touch("B.slnx")], [Touch("A.csproj")], new Sdks("10.0.100"));

        Assert.IsFalse(partition.CanLoadInOnePass, "a copy of a global.json with relative SDK paths would resolve elsewhere");
        StringAssert.Contains(partition.Reason, "paths");
    }

    [TestMethod]
    public void CommonDirectory_IsTheDeepestSharedAncestor()
    {
        Assert.AreEqual(
            Path.Combine(_root, "a"),
            UnionSolutionWriter.CommonDirectory(
                [Path.Combine(_root, "a", "b"), Path.Combine(_root, "a", "c", "d"), Path.Combine(_root, "a")]));
        Assert.AreEqual(
            _root,
            UnionSolutionWriter.CommonDirectory([Path.Combine(_root, "ab"), Path.Combine(_root, "a")]),
            "a shared name prefix is not a shared directory");
    }

    [TestMethod]
    public void WithoutSkippedStubs_RemovesUnreferencedSkippedStubs_Repeatedly_AndKeepsAReferencedOne()
    {
        // A (stub) -> B (stub): removing A leaves B unreferenced, so it goes too. D (stub) is referenced by C (loaded).
        var workspace = new AdhocWorkspace();
        var solution = workspace.CurrentSolution;
        ProjectId Add(string name, bool withDocument)
        {
            var id = ProjectId.CreateNewId();
            solution = solution.AddProject(ProjectInfo.Create(id, VersionStamp.Default, name, name, LanguageNames.CSharp,
                filePath: Path.Combine(_root, name, name + ".csproj")));
            if (withDocument)
                solution = solution.AddDocument(DocumentId.CreateNewId(id), "C.cs", "class C {}");
            return id;
        }
        var b = Add("B", false);
        var a = Add("A", false);
        var d = Add("D", false);
        var c = Add("C", true);
        solution = solution.AddProjectReference(a, new ProjectReference(b)).AddProjectReference(c, new ProjectReference(d));
        var skipped = new[] { "A", "B", "D" }.Select(n => new SkippedProject(Path.Combine(_root, n, n + ".csproj"), "failed")).ToList();

        var result = SolutionLoader.WithoutSkippedStubs(solution, skipped);

        CollectionAssert.AreEquivalent(new[] { c, d }, result.ProjectIds.ToArray());
    }

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private string Touch(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, path.EndsWith("global.json", StringComparison.Ordinal) ? "{}" : "<Project />");
        return path;
    }

    // Resolves the SDK hostfxr would for a directory from its nearest global.json: an entry for that file, else the
    // default. A null entry is an unresolvable pin.
    private sealed class Sdks(string defaultSdk) : Dictionary<string, string?>, ISdkResolutionProbe
    {
        public SdkResolutionProbeResult Probe(string workingDirectory) =>
            GlobalJsonLocator.FindNearest(workingDirectory) is { } file && TryGetValue(file, out var version)
                ? new SdkResolutionProbeResult { ResolvedSdkVersion = version }
                : new SdkResolutionProbeResult { ResolvedSdkVersion = defaultSdk };

        public IReadOnlyList<string> ListInstalledSdks() => [defaultSdk];
    }
}
