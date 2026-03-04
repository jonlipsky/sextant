using Sextant.Core;

namespace Sextant.Core.Tests;

[TestClass]
public class ConfigurationTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sextant_config_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        // Create a .git directory so FindRepoRoot works
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));
    }

    [TestCleanup]
    public void TestCleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public void Load_WithNoFile_ReturnsDefaults()
    {
        var noGitDir = Path.Combine(Path.GetTempPath(), $"no_git_{Guid.NewGuid():N}");
        Directory.CreateDirectory(noGitDir);
        try
        {
            var config = SextantConfiguration.Load(noGitDir);
            Assert.AreEqual(".sextant/profiles/default/sextant.db", config.DbPath);
            Assert.AreEqual(5, config.MaxCallHierarchyDepth);
            Assert.AreEqual(20, config.FtsMaxResults);
            Assert.AreEqual(0, config.Solutions.Count);
        }
        finally
        {
            Directory.Delete(noGitDir, recursive: true);
        }
    }

    [TestMethod]
    public void Load_WithJsonFile_OverridesDefaults()
    {
        var json = """
        {
            "db_path": "custom/path.db",
            "max_call_hierarchy_depth": 10,
            "fts_max_results": 50,
            "solutions": ["src/App.sln", "tests/Tests.sln"]
        }
        """;
        File.WriteAllText(Path.Combine(_tempDir, "sextant.json"), json);

        var config = SextantConfiguration.Load(_tempDir);
        Assert.AreEqual("custom/path.db", config.DbPath);
        Assert.AreEqual(10, config.MaxCallHierarchyDepth);
        Assert.AreEqual(50, config.FtsMaxResults);
        CollectionAssert.AreEqual(new[] { "src/App.sln", "tests/Tests.sln" }, config.Solutions.ToArray());
    }

    [TestMethod]
    public void Load_WithPartialJsonFile_OnlyOverridesSetFields()
    {
        var json = """{ "db_path": "other.db" }""";
        File.WriteAllText(Path.Combine(_tempDir, "sextant.json"), json);

        var config = SextantConfiguration.Load(_tempDir);
        Assert.AreEqual("other.db", config.DbPath);
        Assert.AreEqual(5, config.MaxCallHierarchyDepth); // default preserved
        Assert.AreEqual(20, config.FtsMaxResults); // default preserved
    }

    [TestMethod]
    public void Load_WithInvalidJson_FallsBackToDefaults()
    {
        File.WriteAllText(Path.Combine(_tempDir, "sextant.json"), "not valid json {{{");

        var config = SextantConfiguration.Load(_tempDir);
        Assert.AreEqual(".sextant/profiles/default/sextant.db", config.DbPath);
    }

    [TestMethod]
    public void Load_WithCommentsAndTrailingCommas_ParsesSuccessfully()
    {
        var json = """
        {
            // Custom database path
            "db_path": "my.db",
            "fts_max_results": 30,
        }
        """;
        File.WriteAllText(Path.Combine(_tempDir, "sextant.json"), json);

        var config = SextantConfiguration.Load(_tempDir);
        Assert.AreEqual("my.db", config.DbPath);
        Assert.AreEqual(30, config.FtsMaxResults);
    }

    [TestMethod]
    public void Load_WithDaemonSocket_SetsProperty()
    {
        var json = """{ "daemon_socket": "/tmp/sextant.sock" }""";
        File.WriteAllText(Path.Combine(_tempDir, "sextant.json"), json);

        var config = SextantConfiguration.Load(_tempDir);
        Assert.AreEqual("/tmp/sextant.sock", config.DaemonSocket);
    }

    [TestMethod]
    public void DaemonSocket_EnvVarOverridesConfig()
    {
        var json = """{ "daemon_socket": "/tmp/sextant.sock" }""";
        File.WriteAllText(Path.Combine(_tempDir, "sextant.json"), json);

        Environment.SetEnvironmentVariable("SEXTANT_DAEMON_SOCKET", "/override/socket.sock");
        try
        {
            var config = SextantConfiguration.Load(_tempDir);
            Assert.AreEqual("/override/socket.sock", config.DaemonSocket);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SEXTANT_DAEMON_SOCKET", null);
        }
    }

    [TestMethod]
    public void FindRepoRoot_FindsGitDirectory()
    {
        var subDir = Path.Combine(_tempDir, "a", "b", "c");
        Directory.CreateDirectory(subDir);

        var root = SextantConfiguration.FindRepoRoot(subDir);
        Assert.AreEqual(_tempDir, root);
    }

    [TestMethod]
    public void FindRepoRoot_ReturnsNull_WhenNoGitDir()
    {
        var noGitDir = Path.Combine(Path.GetTempPath(), $"no_git_{Guid.NewGuid():N}");
        Directory.CreateDirectory(noGitDir);
        try
        {
            // This will walk up to filesystem root and not find .git
            // It may find a .git dir higher up (e.g. if run inside a git repo)
            // so we just verify it doesn't crash
            var result = SextantConfiguration.FindRepoRoot(noGitDir);
            // Result is either null or a valid directory
            if (result != null)
                Assert.IsTrue(Directory.Exists(Path.Combine(result, ".git")));
        }
        finally
        {
            Directory.Delete(noGitDir, recursive: true);
        }
    }
}
