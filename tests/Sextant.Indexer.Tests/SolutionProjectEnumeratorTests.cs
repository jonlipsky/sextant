using Sextant.Indexer;

namespace Sextant.Indexer.Tests;

/// <summary>
/// Parsing tests for <see cref="SolutionProjectEnumerator"/> — the declared-project lister a resilient
/// load uses to open projects individually (issue #90). Pure text parsing, no MSBuild.
/// </summary>
[TestClass]
public sealed class SolutionProjectEnumeratorTests
{
    private string _dir = null!;

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sextant-enum-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [TestMethod]
    public void ClassicSln_ReturnsProjectPaths_ExcludingSolutionFolders()
    {
        var sln = Path.Combine(_dir, "App.sln");
        File.WriteAllText(sln,
            "Microsoft Visual Studio Solution File, Format Version 12.00\n" +
            "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Lib\", \"Lib\\Lib.csproj\", \"{11111111-1111-1111-1111-111111111111}\"\n" +
            "EndProject\n" +
            "Project(\"{2150E333-8FDC-42A3-9474-1A3956D46DE8}\") = \"SolutionItems\", \"SolutionItems\", \"{22222222-2222-2222-2222-222222222222}\"\n" +
            "EndProject\n" +
            "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"App\", \"App\\App.csproj\", \"{33333333-3333-3333-3333-333333333333}\"\n" +
            "EndProject\n");

        var result = SolutionProjectEnumerator.Enumerate(sln);

        CollectionAssert.AreEqual(
            new[] { Path.Combine(_dir, "Lib", "Lib.csproj"), Path.Combine(_dir, "App", "App.csproj") },
            result.ToArray());
    }

    [TestMethod]
    public void Slnx_ReturnsProjectPaths_IncludingNestedFolders()
    {
        var slnx = Path.Combine(_dir, "App.slnx");
        File.WriteAllText(slnx,
            "<Solution>\n" +
            "  <Project Path=\"Lib/Lib.csproj\" />\n" +
            "  <Folder Name=\"/Group/\">\n" +
            "    <Project Path=\"App/App.csproj\" />\n" +
            "  </Folder>\n" +
            "</Solution>\n");

        var result = SolutionProjectEnumerator.Enumerate(slnx);

        CollectionAssert.AreEqual(
            new[] { Path.Combine(_dir, "Lib", "Lib.csproj"), Path.Combine(_dir, "App", "App.csproj") },
            result.ToArray());
    }

    [TestMethod]
    public void FiltersUnrecognizedProjectTypesAndDeduplicates()
    {
        var slnx = Path.Combine(_dir, "App.slnx");
        File.WriteAllText(slnx,
            "<Solution>\n" +
            "  <Project Path=\"Lib/Lib.csproj\" />\n" +
            "  <Project Path=\"Shared/Shared.shproj\" />\n" +
            "  <Project Path=\"Lib/Lib.csproj\" />\n" +
            "</Solution>\n");

        var result = SolutionProjectEnumerator.Enumerate(slnx);

        Assert.AreEqual(1, result.Count, "shproj is excluded and the duplicate csproj is collapsed");
        Assert.AreEqual(Path.Combine(_dir, "Lib", "Lib.csproj"), result[0]);
    }

    [TestMethod]
    public void ClassicSln_UsesPathField_WhenDisplayNameAlsoEndsInProjectExtension()
    {
        // Regression: the display name itself ends in ".csproj". An extension SCAN would pick the display
        // name ("Widget.csproj") and resolve it against the solution dir — the WRONG location. The path is
        // the second positional token ("src\Widget\Widget.csproj"), which is what must be returned.
        var sln = Path.Combine(_dir, "App.sln");
        File.WriteAllText(sln,
            "Microsoft Visual Studio Solution File, Format Version 12.00\n" +
            "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Widget.csproj\", \"src\\Widget\\Widget.csproj\", \"{11111111-1111-1111-1111-111111111111}\"\n" +
            "EndProject\n");

        var result = SolutionProjectEnumerator.Enumerate(sln);

        CollectionAssert.AreEqual(
            new[] { Path.Combine(_dir, "src", "Widget", "Widget.csproj") },
            result.ToArray());
    }

    [TestMethod]
    public void UnreadableOrUnknownSolution_ReturnsEmpty_WithoutThrowing()
    {
        var missing = Path.Combine(_dir, "does-not-exist.sln");
        Assert.AreEqual(0, SolutionProjectEnumerator.Enumerate(missing).Count);

        var weird = Path.Combine(_dir, "App.weird");
        File.WriteAllText(weird, "not a solution");
        Assert.AreEqual(0, SolutionProjectEnumerator.Enumerate(weird).Count);
    }
}
