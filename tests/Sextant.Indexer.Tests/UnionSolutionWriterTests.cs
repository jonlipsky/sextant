using Sextant.Indexer;

namespace Sextant.Indexer.Tests;

/// <summary>
/// Issue #268: the multi-solution union loads in one pass over a generated solution only where that evaluates every
/// project with the same SDK as each real solution and each project opened alone would (one shared global.json), and
/// the generated solution lists the union in order, relative to itself.
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
    public void Write_CreatesAUniqueHiddenSolution_InTheDirectory()
    {
        var project = Touch("src", "A", "A.csproj");

        var first = UnionSolutionWriter.Write(_root, [project]);
        var second = UnionSolutionWriter.Write(_root, [project]);

        Assert.AreEqual(_root, Path.GetDirectoryName(first));
        StringAssert.StartsWith(Path.GetFileName(first), ".sextant-union-");
        StringAssert.EndsWith(first, ".slnx");
        Assert.AreNotEqual(first, second, "concurrent loads never share a generated file");
        StringAssert.Contains(File.ReadAllText(first), "src/A/A.csproj");
    }

    [TestMethod]
    public void Partition_PutsEveryProjectInOnePass_WhenAllShareTheSolutionsGlobalJson()
    {
        Touch("global.json");
        var projects = new[] { Touch("src", "A", "A.csproj"), Touch("tools", "B", "B.csproj") };

        var partition = UnionSolutionWriter.Partition([Touch("A.slnx"), Touch("tools", "B.slnx")], projects, new Sdks("10.0.100"));

        Assert.AreEqual(_root, partition.Directory, "the selected solutions' common directory");
        CollectionAssert.AreEqual(projects, partition.OnePass.ToArray());
        Assert.IsEmpty(partition.Individually);
    }

    [TestMethod]
    public void Partition_WithNoGlobalJsonAnywhere_PutsEveryProjectInOnePass()
    {
        var partition = UnionSolutionWriter.Partition(
            [Touch("one", "A.slnx"), Touch("two", "B.slnx")], [Touch("one", "A.csproj"), Touch("two", "B.csproj")],
            new Sdks("10.0.100"));

        Assert.AreEqual(_root, partition.Directory);
        Assert.HasCount(2, partition.OnePass);
    }

    [TestMethod]
    public void Partition_KeepsAProjectInOnePass_WhenItsOwnGlobalJsonResolvesTheSameSdk_AndPinsNoSdkItUses()
    {
        // ProcessStack's submodule shape: the nested global.json pins only msbuild-sdks these projects do not use.
        Write("global.json", "{ \"sdk\": { \"version\": \"10.0.100\", \"rollForward\": \"latestFeature\" } }");
        Write("sub/global.json", "{ \"msbuild-sdks\": { \"MSBuild.Sdk.Extras\": \"3.0.44\" } }");
        var lib = Write("sub/src/Lib/Lib.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var partition = UnionSolutionWriter.Partition(
            [Touch("A.slnx"), Touch("sub", "Sub.sln")], [Touch("src", "A", "A.csproj"), lib], new Sdks("10.0.401"));

        Assert.HasCount(2, partition.OnePass, partition.Reason);
        Assert.IsEmpty(partition.Individually);
    }

    [TestMethod]
    public void Partition_OpensAProjectThatUsesAnMsBuildSdkItsGlobalJsonPinsDifferently_Individually()
    {
        // The Xamarin heads of the same submodule: opened alone they resolve MSBuild.Sdk.Extras from their own pin,
        // which a solution elsewhere would not see.
        Write("sub/global.json", "{ \"msbuild-sdks\": { \"MSBuild.Sdk.Extras\": \"3.0.44\" } }");
        var head = Write("sub/Head/Head.csproj", "<Project Sdk=\"MSBuild.Sdk.Extras\" />");
        var app = Touch("src", "A", "A.csproj");

        var partition = UnionSolutionWriter.Partition([Touch("A.slnx"), Touch("sub", "Sub.sln")], [head, app], new Sdks("10.0.100"));

        CollectionAssert.AreEqual(new[] { app }, partition.OnePass.ToArray());
        CollectionAssert.AreEqual(new[] { head }, partition.Individually.ToArray());
        StringAssert.Contains(partition.Reason, "MSBuild.Sdk.Extras");
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

        var partition = UnionSolutionWriter.Partition(
            [Touch("A.slnx"), Touch("sub", "Sub.sln")], [app, host, sample, head], new Sdks("10.0.100"));

        CollectionAssert.AreEqual(new[] { app }, partition.OnePass.ToArray());
        CollectionAssert.AreEqual(new[] { host, sample, head }, partition.Individually.ToArray(), "union order is kept");
    }

    [TestMethod]
    public void Partition_OpensAProjectWhoseOwnGlobalJsonResolvesAnotherSdk_Individually()
    {
        Write("global.json", "{ \"sdk\": { \"version\": \"10.0.100\" } }");
        Write("src/Pinned/global.json", "{ \"sdk\": { \"version\": \"9.0.100\" } }");
        var pinned = Write("src/Pinned/Pinned.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var probe = new Sdks("10.0.100") { [Path.Combine(_root, "src", "Pinned", "global.json")] = "9.0.100" };

        var partition = UnionSolutionWriter.Partition(
            [Touch("A.slnx"), Touch("B.slnx")], [Touch("src", "A", "A.csproj"), pinned], probe);

        CollectionAssert.AreEqual(new[] { pinned }, partition.Individually.ToArray(), "opened alone, it builds with another SDK");
        StringAssert.Contains(partition.Reason, "Pinned.csproj");
        StringAssert.Contains(partition.Reason, "different SDK");
    }

    [TestMethod]
    public void Partition_OpensAProjectWhoseOwnPinIsUnsatisfiable_Individually()
    {
        // Issue #113 isolates (or overrides) that pin on the per-project open; a solution would hide it.
        Write("src/Pinned/global.json", "{ \"sdk\": { \"version\": \"10.0.999\", \"rollForward\": \"disable\" } }");
        var pinned = Write("src/Pinned/Pinned.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var probe = new Sdks("10.0.100") { [Path.Combine(_root, "src", "Pinned", "global.json")] = null };

        var partition = UnionSolutionWriter.Partition([Touch("A.slnx"), Touch("B.slnx")], [pinned], probe);

        Assert.IsNull(partition.Directory, "nothing is left to load in one pass");
        CollectionAssert.AreEqual(new[] { pinned }, partition.Individually.ToArray());
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

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
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

    private string Touch(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }
}
