namespace Sextant.Indexer.Tests;

/// <summary>
/// The static Windows-target scan that decides whether restore and load evaluate with <c>EnableWindowsTargeting=true</c>
/// (issue #293). A repository without a Windows target must get no property, so it restores exactly as before: the
/// property makes every project download <c>Microsoft.WindowsDesktop.App.Ref</c>, which a private-only feed lacks.
/// </summary>
[TestClass]
public sealed class EvaluationGlobalPropertiesTests
{
    private string _dir = null!;

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sextant-globals-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [TestMethod]
    public void PlainNetProjects_GetNoProperties()
    {
        var a = Project("A", "<TargetFramework>net10.0</TargetFramework>");
        var b = Project("B", "<TargetFrameworks>net8.0;net10.0</TargetFrameworks><UseWPF>false</UseWPF>");

        Assert.AreEqual(0, EvaluationGlobalProperties.For([a, b]).Count);
    }

    [TestMethod]
    [DataRow("<TargetFramework>net10.0-windows</TargetFramework>")]
    [DataRow("<TargetFramework>NET8.0-Windows10.0.19041.0</TargetFramework>")]
    [DataRow("<TargetFrameworks>net10.0;net10.0-windows10.0.19041.0</TargetFrameworks>")]
    [DataRow("<TargetFramework>$(NetVersion)-windows10.0.19041.0</TargetFramework>")]
    [DataRow("<TargetFramework>net10.0</TargetFramework><UseWPF>true</UseWPF>")]
    [DataRow("<TargetFramework>net10.0</TargetFramework><UseWinUI> True </UseWinUI>")]
    [DataRow("<TargetFramework>net10.0</TargetFramework><UseWindowsForms>true</UseWindowsForms>")]
    [DataRow("<TargetFramework>net10.0</TargetFramework><UseWPF Condition=\"'$(OS)' == 'Windows_NT'\">true</UseWPF>")]
    public void AWindowsMarkerInAProject_EnablesWindowsTargeting(string properties)
    {
        var plain = Project("Plain", "<TargetFramework>net10.0</TargetFramework>");
        var windows = Project("Win", properties);

        var globals = EvaluationGlobalProperties.For([plain, windows]);

        Assert.AreEqual(1, globals.Count);
        Assert.AreEqual(EvaluationGlobalProperties.EnableWindowsTargeting, globals[0].Key);
        Assert.AreEqual("true", globals[0].Value);
    }

    [TestMethod]
    public void AMarkerInTheNearestDirectoryBuildProps_Counts()
    {
        File.WriteAllText(Path.Combine(_dir, "Directory.Build.props"),
            "<Project><PropertyGroup><UseWPF>true</UseWPF></PropertyGroup></Project>");
        var project = Project("Nested", "<TargetFramework>net10.0</TargetFramework>");

        Assert.IsTrue(EvaluationGlobalProperties.TargetsWindows([project]));
    }

    [TestMethod]
    public void AMarkerInTheNearestDirectoryBuildTargets_Counts()
    {
        File.WriteAllText(Path.Combine(_dir, "Directory.Build.targets"),
            "<Project><PropertyGroup><TargetFramework>net10.0-windows</TargetFramework></PropertyGroup></Project>");
        var project = Project("Nested", "<OutputType>Library</OutputType>");

        Assert.IsTrue(EvaluationGlobalProperties.TargetsWindows([project]));
    }

    [TestMethod]
    public void AMarkerInAFartherDirectoryBuildProps_HiddenByANearerOne_DoesNotCount()
    {
        // MSBuild imports only the first Directory.Build.props found upward; the outer one is reached only if the
        // inner one imports it, which the scan does not follow (a documented limit).
        File.WriteAllText(Path.Combine(_dir, "Directory.Build.props"),
            "<Project><PropertyGroup><UseWPF>true</UseWPF></PropertyGroup></Project>");
        var inner = Path.Combine(_dir, "inner");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, "Directory.Build.props"), "<Project />");
        var dir = Path.Combine(inner, "Lib");
        Directory.CreateDirectory(dir);
        var project = Path.Combine(dir, "Lib.csproj");
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

        Assert.IsFalse(EvaluationGlobalProperties.TargetsWindows([project]));
    }

    [TestMethod]
    public void AMarkerInACommentOrAnotherWord_DoesNotCount()
    {
        var project = Project("Commented",
            "<TargetFramework>net10.0</TargetFramework><!-- was net10.0-windows with <UseWPF>true</UseWPF> -->" +
            "<Description>runs on windows-latest</Description>");

        Assert.IsFalse(EvaluationGlobalProperties.TargetsWindows([project]));
    }

    [TestMethod]
    public void MissingFiles_AndASolutionPath_AreHandled()
    {
        Assert.IsFalse(EvaluationGlobalProperties.TargetsWindows([Path.Combine(_dir, "missing", "Missing.csproj")]));

        Project("Win", "<TargetFramework>net10.0-windows</TargetFramework>");
        var slnx = Path.Combine(_dir, "All.slnx");
        File.WriteAllText(slnx, "<Solution><Project Path=\"Win/Win.csproj\" /></Solution>");
        Assert.AreEqual(1, EvaluationGlobalProperties.ForSolutionOrProject(slnx).Count);
        Assert.AreEqual(0, EvaluationGlobalProperties.ForSolutionOrProject(Path.Combine(_dir, "Missing.slnx")).Count);
    }

    private string Project(string name, string properties)
    {
        var dir = Path.Combine(_dir, name);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ".csproj");
        File.WriteAllText(path, $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>{properties}</PropertyGroup></Project>");
        return path;
    }
}
