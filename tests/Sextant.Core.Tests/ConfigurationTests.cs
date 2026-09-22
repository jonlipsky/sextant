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
            Assert.IsTrue(config.DocumentExtractor, "the document-oriented extractor is the shipping default");
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
    public void DocumentExtractor_DefaultsOnAndCanBeDisabled()
    {
        // Shipping default is on; the legacy extractor remains reachable as a fallback via json...
        File.WriteAllText(Path.Combine(_tempDir, "sextant.json"), """{ "document_extractor": false }""");
        var disabled = SextantConfiguration.Load(_tempDir);
        Assert.IsFalse(disabled.DocumentExtractor, "json can disable the extractor (legacy fallback)");

        File.WriteAllText(Path.Combine(_tempDir, "sextant.json"), """{ "document_extractor": true }""");
        Assert.IsTrue(SextantConfiguration.Load(_tempDir).DocumentExtractor);
    }

    [TestMethod]
    public void DocumentExtractor_EnvVarOverridesConfig()
    {
        // ...and via the env var, which takes precedence over the file.
        File.WriteAllText(Path.Combine(_tempDir, "sextant.json"), """{ "document_extractor": true }""");
        Environment.SetEnvironmentVariable("SEXTANT_DOCUMENT_EXTRACTOR", "false");
        try
        {
            Assert.IsFalse(SextantConfiguration.Load(_tempDir).DocumentExtractor,
                "SEXTANT_DOCUMENT_EXTRACTOR=false forces the legacy fallback even when json enables it");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SEXTANT_DOCUMENT_EXTRACTOR", null);
        }
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

    // === Phase 8: indexing profile, generated-source policy, and retention config ===================

    [TestMethod]
    public void IndexingProfile_DefaultsToStandard()
    {
        var config = SextantConfiguration.Load(_tempDir);
        Assert.AreEqual(IndexProfiles.Standard, config.IndexingProfile, "standard is the default profile");
        Assert.AreEqual(GeneratedSourcePolicies.Exclude, config.GeneratedSourcePolicy);
    }

    [TestMethod]
    public void IndexingProfile_LoadedFromJson()
    {
        File.WriteAllText(Path.Combine(_tempDir, "sextant.json"),
            """{ "indexing_profile": "core", "generated_source_policy": "exclude" }""");

        var config = SextantConfiguration.Load(_tempDir);
        Assert.AreEqual("core", config.IndexingProfile);
        Assert.AreEqual("exclude", config.GeneratedSourcePolicy);
    }

    [TestMethod]
    public void IndexingProfile_EnvVarOverridesConfig()
    {
        File.WriteAllText(Path.Combine(_tempDir, "sextant.json"), """{ "indexing_profile": "core" }""");
        Environment.SetEnvironmentVariable("SEXTANT_INDEXING_PROFILE", "deep");
        try
        {
            Assert.AreEqual("deep", SextantConfiguration.Load(_tempDir).IndexingProfile,
                "SEXTANT_INDEXING_PROFILE overrides the json profile");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SEXTANT_INDEXING_PROFILE", null);
        }
    }

    [TestMethod]
    public void Retention_DefaultsAreApplied()
    {
        var config = SextantConfiguration.Load(_tempDir);
        Assert.AreEqual(3, config.Retention.KeepCompleteGenerations);
        Assert.AreEqual(10, config.Retention.ApiSnapshotKeepCommits);
        Assert.IsTrue(config.Retention.PruneSupersededSourceBlobs);
    }

    [TestMethod]
    public void Retention_LoadedFromJson()
    {
        File.WriteAllText(Path.Combine(_tempDir, "sextant.json"), """
        {
            "retention": {
                "keep_complete_generations": 5,
                "api_snapshot_keep_commits": 25,
                "prune_superseded_source_blobs": false
            }
        }
        """);

        var config = SextantConfiguration.Load(_tempDir);
        Assert.AreEqual(5, config.Retention.KeepCompleteGenerations);
        Assert.AreEqual(25, config.Retention.ApiSnapshotKeepCommits);
        Assert.IsFalse(config.Retention.PruneSupersededSourceBlobs);
    }

    [TestMethod]
    public void Retention_EnvVarsOverrideConfig()
    {
        File.WriteAllText(Path.Combine(_tempDir, "sextant.json"),
            """{ "retention": { "keep_complete_generations": 5 } }""");
        Environment.SetEnvironmentVariable("SEXTANT_RETENTION_KEEP_GENERATIONS", "1");
        Environment.SetEnvironmentVariable("SEXTANT_RETENTION_API_KEEP_COMMITS", "2");
        Environment.SetEnvironmentVariable("SEXTANT_RETENTION_PRUNE_SOURCE_BLOBS", "false");
        try
        {
            var config = SextantConfiguration.Load(_tempDir);
            Assert.AreEqual(1, config.Retention.KeepCompleteGenerations);
            Assert.AreEqual(2, config.Retention.ApiSnapshotKeepCommits);
            Assert.IsFalse(config.Retention.PruneSupersededSourceBlobs);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SEXTANT_RETENTION_KEEP_GENERATIONS", null);
            Environment.SetEnvironmentVariable("SEXTANT_RETENTION_API_KEEP_COMMITS", null);
            Environment.SetEnvironmentVariable("SEXTANT_RETENTION_PRUNE_SOURCE_BLOBS", null);
        }
    }

    [TestMethod]
    public void Retention_PruneEnvVar_ParsesCaseInsensitively()
    {
        // A destructive default-on flag must not silently stay enabled for a mixed-case or padded
        // "false" spelling (review hardening).
        foreach (var off in new[] { "False", "FALSE", " false ", "Off", "No", "0" })
        {
            Environment.SetEnvironmentVariable("SEXTANT_RETENTION_PRUNE_SOURCE_BLOBS", off);
            try
            {
                var config = SextantConfiguration.Load(_tempDir);
                Assert.IsFalse(config.Retention.PruneSupersededSourceBlobs,
                    $"'{off}' must disable source-blob pruning");
            }
            finally
            {
                Environment.SetEnvironmentVariable("SEXTANT_RETENTION_PRUNE_SOURCE_BLOBS", null);
            }
        }
    }

    [TestMethod]
    public void Retention_NegativeKeepCounts_FallBackToDefaults()
    {
        // A stray negative keep-count is a misconfiguration, not a request to delete everything: it
        // normalizes to the safe default, while an explicit 0 (keep only the servable generation) is
        // preserved as a valid aggressive policy (review hardening).
        var negative = new RetentionPolicy
        {
            KeepCompleteGenerations = -1,
            ApiSnapshotKeepCommits = -5,
            PruneSupersededSourceBlobs = true
        }.Normalized();
        Assert.AreEqual(RetentionPolicy.DefaultKeepCompleteGenerations, negative.KeepCompleteGenerations);
        Assert.AreEqual(RetentionPolicy.DefaultApiSnapshotKeepCommits, negative.ApiSnapshotKeepCommits);

        var zero = new RetentionPolicy
        {
            KeepCompleteGenerations = 0,
            ApiSnapshotKeepCommits = 0
        }.Normalized();
        Assert.AreEqual(0, zero.KeepCompleteGenerations);
        Assert.AreEqual(0, zero.ApiSnapshotKeepCommits);
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
