namespace Sextant.Core.Tests;

[TestClass]
public class GitExecutableTests
{
    private static readonly string GitName = OperatingSystem.IsWindows() ? "git.exe" : "git";

    private string _root = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "sextant-gitexe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Path.IsPathRooted(_root) && Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string FakeGit(string directoryName)
    {
        var dir = Path.Combine(_root, directoryName);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, GitName);
        File.WriteAllText(file, string.Empty);
        return file;
    }

    [TestMethod]
    public void ConfiguredPath_WinsOverPath()
    {
        var configured = FakeGit("configured");
        var onPath = FakeGit("onpath");

        Assert.AreEqual(configured, GitExecutable.Resolve(configured, Path.GetDirectoryName(onPath)));
    }

    [TestMethod]
    public void ConfiguredPath_MayBeQuoted()
    {
        var configured = FakeGit("configured");

        Assert.AreEqual(configured, GitExecutable.Resolve("\"" + configured + "\"", null));
    }

    [TestMethod]
    public void QuotedPathEntry_IsTrimmed()
    {
        var git = FakeGit("quoted");

        Assert.AreEqual(git, GitExecutable.Resolve(null, "\"" + Path.GetDirectoryName(git) + "\""));
    }

    [TestMethod]
    public void RelativePathEntry_IsSkipped()
    {
        var git = FakeGit("abs");
        var path = string.Join(Path.PathSeparator, "relative-dir", Path.GetDirectoryName(git));

        Assert.AreEqual(git, GitExecutable.Resolve(null, path));
    }

    [TestMethod]
    public void MissingConfiguredFile_FallsBackToPath()
    {
        var git = FakeGit("onpath");

        Assert.AreEqual(git, GitExecutable.Resolve(Path.Combine(_root, "nope", GitName), Path.GetDirectoryName(git)));
    }

    [TestMethod]
    public void NothingFound_FallsBackToBareGit()
    {
        Assert.AreEqual("git", GitExecutable.Resolve(null, Path.Combine(_root, "empty")));
        Assert.AreEqual("git", GitExecutable.Resolve(null, null));
    }
}
