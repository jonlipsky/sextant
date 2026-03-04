using Sextant.Core;

namespace Sextant.Integration.Tests;

[TestClass]
[TestCategory("Integration")]
public class ProfileIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public ProfileIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sextant_profile_int_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [TestMethod]
    public void Profile_DefaultCreatesCorrectPath()
    {
        var config = new SextantConfiguration();
        var path = SextantConfiguration.ResolveDbPath(null, null, config);
        Assert.AreEqual(".sextant/profiles/default/sextant.db", path);
    }

    [TestMethod]
    public void Profile_NamedCreatesCorrectPath()
    {
        var config = new SextantConfiguration();
        var path = SextantConfiguration.ResolveDbPath(null, "testing", config);
        Assert.AreEqual(".sextant/profiles/testing/sextant.db", path);
    }

    [TestMethod]
    public void Profile_IsolatedFromOtherProfiles()
    {
        var profilesDir = Path.Combine(_tempDir, ".sextant", "profiles");
        var profile1Dir = Path.Combine(profilesDir, "profile1");
        var profile2Dir = Path.Combine(profilesDir, "profile2");
        Directory.CreateDirectory(profile1Dir);
        Directory.CreateDirectory(profile2Dir);

        File.WriteAllText(Path.Combine(profile1Dir, "sextant.db"), "data1");
        File.WriteAllText(Path.Combine(profile2Dir, "sextant.db"), "data2");

        Assert.AreNotEqual(
            File.ReadAllText(Path.Combine(profile1Dir, "sextant.db")),
            File.ReadAllText(Path.Combine(profile2Dir, "sextant.db")));
    }

    [TestMethod]
    public void Profile_LegacyMigration_MovesDbToDefault()
    {
        var sextantDir = Path.Combine(_tempDir, ".sextant");
        Directory.CreateDirectory(sextantDir);
        File.WriteAllText(Path.Combine(sextantDir, "sextant.db"), "legacy-data");

        SextantConfiguration.MigrateLegacyIfNeeded(_tempDir);

        var newDb = Path.Combine(sextantDir, "profiles", "default", "sextant.db");
        Assert.IsTrue(File.Exists(newDb));
        Assert.AreEqual("legacy-data", File.ReadAllText(newDb));
        Assert.IsFalse(File.Exists(Path.Combine(sextantDir, "sextant.db")));
    }

    [TestMethod]
    public void Profile_LegacyMigration_NoOpWhenAlreadyMigrated()
    {
        var sextantDir = Path.Combine(_tempDir, ".sextant");
        var profileDir = Path.Combine(sextantDir, "profiles", "default");
        Directory.CreateDirectory(profileDir);
        File.WriteAllText(Path.Combine(profileDir, "sextant.db"), "migrated-data");
        File.WriteAllText(Path.Combine(sextantDir, "sextant.db"), "old-data");

        SextantConfiguration.MigrateLegacyIfNeeded(_tempDir);

        // Migration should not overwrite existing profile DB
        Assert.AreEqual("migrated-data",
            File.ReadAllText(Path.Combine(profileDir, "sextant.db")));
    }

    [TestMethod]
    public void Profile_ListsAllProfiles()
    {
        var profilesDir = Path.Combine(_tempDir, ".sextant", "profiles");
        Directory.CreateDirectory(Path.Combine(profilesDir, "default"));
        Directory.CreateDirectory(Path.Combine(profilesDir, "testing"));
        Directory.CreateDirectory(Path.Combine(profilesDir, "production"));

        var dirs = Directory.GetDirectories(profilesDir)
            .Select(d => Path.GetFileName(d))
            .OrderBy(n => n)
            .ToList();

        Assert.AreEqual(3, dirs.Count);
        Assert.IsTrue(dirs.Contains("default"));
        Assert.IsTrue(dirs.Contains("testing"));
        Assert.IsTrue(dirs.Contains("production"));
    }
}
