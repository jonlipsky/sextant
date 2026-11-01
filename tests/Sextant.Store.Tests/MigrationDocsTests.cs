using System.Text.RegularExpressions;

namespace Sextant.Store.Tests;

/// <summary>
/// Issue #300: CLAUDE.md and docs/schema.md state the latest migration and <c>LatestSchemaVersion</c>;
/// they fail here when a migration is added without updating them.
/// </summary>
[TestClass]
public class MigrationDocsTests
{
    [TestMethod]
    public void ClaudeMd_And_SchemaDoc_NameTheLatestMigration()
    {
        var root = RepoRoot();
        var latest = IndexDatabase.LatestSchemaVersion;
        var file = Directory.GetFiles(Path.Combine(root, "src", "Sextant.Store", "Migrations"), $"{latest:000}_*.sql").Single();
        var name = Path.GetFileNameWithoutExtension(file);

        var claude = File.ReadAllText(Path.Combine(root, "CLAUDE.md"));
        StringAssert.Contains(claude, $"last on disk is `{name}`");
        StringAssert.Contains(claude, $"`LatestSchemaVersion` is **{latest}**");

        var schema = File.ReadAllText(Path.Combine(root, "docs", "schema.md"));
        StringAssert.Contains(schema, $"Migrations `012` through `{latest:000}`");
        StringAssert.Contains(schema, $"`LatestSchemaVersion` is **{latest}**");
        StringAssert.Contains(schema, $"(`012`–`{latest:000}`)");
        foreach (var m in Directory.GetFiles(Path.Combine(root, "src", "Sextant.Store", "Migrations"), "*.sql"))
        {
            var n = Path.GetFileName(m);
            if (int.Parse(n[..3]) >= 12)
                StringAssert.Contains(schema, $"`{n}`", $"docs/schema.md lists migration {n}");
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Sextant.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
