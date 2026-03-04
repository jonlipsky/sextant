using Sextant.Core;

namespace Sextant.Core.Tests;

[TestClass]
public class ProfileResolutionTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sextant_profile_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void TestCleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [TestMethod]
    public void ResolveDbPath_NoOverrides_ReturnsDefaultProfile()
    {
        var config = new SextantConfiguration();
        var result = SextantConfiguration.ResolveDbPath(null, null, config);
        Assert.AreEqual(".sextant/profiles/default/sextant.db", result);
    }

    [TestMethod]
    public void ResolveDbPath_ProfileOverride_ReturnsNamedProfile()
    {
        var config = new SextantConfiguration();
        var result = SextantConfiguration.ResolveDbPath(null, "testing", config);
        Assert.AreEqual(".sextant/profiles/testing/sextant.db", result);
    }

    [TestMethod]
    public void ResolveDbPath_ExplicitDb_BypassesProfile()
    {
        var config = new SextantConfiguration();
        var result = SextantConfiguration.ResolveDbPath("/explicit/path.db", null, config);
        Assert.AreEqual("/explicit/path.db", result);
    }

    [TestMethod]
    public void ResolveDbPath_ExplicitDbWithProfile_ExplicitWins()
    {
        var config = new SextantConfiguration();
        var result = SextantConfiguration.ResolveDbPath("/explicit/path.db", "testing", config);
        Assert.AreEqual("/explicit/path.db", result);
    }

    [TestMethod]
    public void ResolveDbPath_EnvVar_UsedWhenNoFlagProvided()
    {
        var originalValue = Environment.GetEnvironmentVariable("SEXTANT_PROFILE");
        try
        {
            Environment.SetEnvironmentVariable("SEXTANT_PROFILE", "from-env");
            var config = new SextantConfiguration();
            var result = SextantConfiguration.ResolveDbPath(null, null, config);
            Assert.AreEqual(".sextant/profiles/from-env/sextant.db", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SEXTANT_PROFILE", originalValue);
        }
    }

    [TestMethod]
    public void ResolveDbPath_ConfigProfile_UsedAsFallback()
    {
        var originalValue = Environment.GetEnvironmentVariable("SEXTANT_PROFILE");
        try
        {
            Environment.SetEnvironmentVariable("SEXTANT_PROFILE", null);
            var config = new SextantConfiguration { Profile = "from-config" };
            var result = SextantConfiguration.ResolveDbPath(null, null, config);
            Assert.AreEqual(".sextant/profiles/from-config/sextant.db", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SEXTANT_PROFILE", originalValue);
        }
    }

    [TestMethod]
    public void MigrateLegacy_MovesDbToDefault()
    {
        // Set up legacy structure
        var sextantDir = Path.Combine(_tempDir, ".sextant");
        Directory.CreateDirectory(sextantDir);
        var legacyDb = Path.Combine(sextantDir, "sextant.db");
        File.WriteAllText(legacyDb, "test-db-content");

        SextantConfiguration.MigrateLegacyIfNeeded(_tempDir);

        var newDb = Path.Combine(sextantDir, "profiles", "default", "sextant.db");
        Assert.IsTrue(File.Exists(newDb));
        Assert.IsFalse(File.Exists(legacyDb));
        Assert.AreEqual("test-db-content", File.ReadAllText(newDb));
    }

    [TestMethod]
    public void MigrateLegacy_MovesLogsAlongsideDb()
    {
        var sextantDir = Path.Combine(_tempDir, ".sextant");
        Directory.CreateDirectory(sextantDir);
        File.WriteAllText(Path.Combine(sextantDir, "sextant.db"), "db");

        var logsDir = Path.Combine(sextantDir, "logs");
        Directory.CreateDirectory(logsDir);
        File.WriteAllText(Path.Combine(logsDir, "test.log"), "log-content");

        SextantConfiguration.MigrateLegacyIfNeeded(_tempDir);

        var newLogs = Path.Combine(sextantDir, "profiles", "default", "logs");
        Assert.IsTrue(Directory.Exists(newLogs));
        Assert.IsTrue(File.Exists(Path.Combine(newLogs, "test.log")));
        Assert.IsFalse(Directory.Exists(logsDir));
    }

    [TestMethod]
    public void MigrateLegacy_NoOpWhenAlreadyMigrated()
    {
        var sextantDir = Path.Combine(_tempDir, ".sextant");
        var profileDir = Path.Combine(sextantDir, "profiles", "default");
        Directory.CreateDirectory(profileDir);
        File.WriteAllText(Path.Combine(profileDir, "sextant.db"), "new-db");

        // Also put a legacy DB there (shouldn't be touched)
        File.WriteAllText(Path.Combine(sextantDir, "sextant.db"), "old-db");

        SextantConfiguration.MigrateLegacyIfNeeded(_tempDir);

        // Legacy DB should still be there since new DB already exists
        Assert.IsTrue(File.Exists(Path.Combine(sextantDir, "sextant.db")));
        Assert.AreEqual("new-db", File.ReadAllText(Path.Combine(profileDir, "sextant.db")));
    }

    [TestMethod]
    public void MigrateLegacy_NoOpWhenNoLegacyDb()
    {
        var sextantDir = Path.Combine(_tempDir, ".sextant");
        Directory.CreateDirectory(sextantDir);

        // No legacy DB exists - should not throw
        SextantConfiguration.MigrateLegacyIfNeeded(_tempDir);

        Assert.IsFalse(Directory.Exists(Path.Combine(sextantDir, "profiles")));
    }

    [DataTestMethod]
    [DataRow("testing")]
    [DataRow("customer-a")]
    [DataRow("my_profile")]
    [DataRow("Profile123")]
    public void ValidateProfileName_AcceptsValidNames(string name)
    {
        SextantConfiguration.ValidateProfileName(name);
    }

    [DataTestMethod]
    [DataRow("../escape")]
    [DataRow("has spaces")]
    [DataRow("path/slash")]
    [DataRow("back\\slash")]
    [DataRow("")]
    public void ValidateProfileName_RejectsInvalidNames(string name)
    {
        Assert.ThrowsExactly<ArgumentException>(() => SextantConfiguration.ValidateProfileName(name));
    }
}
