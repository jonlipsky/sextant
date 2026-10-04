using Sextant.Core;
using Sextant.Store;

namespace Sextant.Mcp.Tests;

[TestClass]
public class SymbolResolverNameCapTests
{
    [TestMethod]
    public void ATypeWithAVeryCommonName_StillResolves_WhenTheNameMatchesMoreRowsThanTheLookupReads()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sextant_name_cap_{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new IndexDatabase(path))
            {
                db.RunMigrations();
                var conn = db.GetConnection();
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var projectId = new ProjectStore(conn).Insert(new ProjectIdentity
                {
                    CanonicalId = "proj_cap_000001", GitRemoteUrl = "https://github.com/test/cap",
                    RepoRelativePath = "src/Cap/Cap.csproj"
                }, now);
                var symbols = new SymbolStore(conn);
                Execute(conn, "BEGIN IMMEDIATE;");
                // More `Options` members than the name lookup reads, all inserted before the type itself.
                for (var i = 0; i < 2100; i++)
                {
                    symbols.Insert(new SymbolInfo
                    {
                        ProjectId = projectId, SymbolKey = $"P:Cap.Settings{i}.Options", FullyQualifiedName = "Options",
                        DisplayName = "Options", Kind = SymbolKind.Property, Accessibility = Accessibility.Public,
                        FilePath = $"src/Cap/Settings{i}.cs", LineStart = 3, LineEnd = 3, LastIndexedAt = now
                    });
                }
                symbols.Insert(new SymbolInfo
                {
                    ProjectId = projectId, SymbolKey = "T:Cap.Configuration.Options",
                    FullyQualifiedName = "global::Cap.Configuration.Options", DisplayName = "Options",
                    Kind = SymbolKind.Class, Accessibility = Accessibility.Public, FilePath = "src/Cap/Options.cs",
                    LineStart = 1, LineEnd = 9, LastIndexedAt = now
                });
                Execute(conn, "COMMIT;");

                foreach (var name in new[] { "Cap.Configuration.Options", "Configuration.Options", "Options" })
                {
                    var lookup = SymbolResolver.Lookup(new SymbolStore(conn), new ProjectStore(conn), name);

                    Assert.AreEqual(SymbolLookupStatus.Resolved, lookup.Status, name);
                    Assert.AreEqual("T:Cap.Configuration.Options", lookup.Symbol!.SymbolKey, name);
                }
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
                File.Delete(file);
        }
    }

    private static void Execute(Microsoft.Data.Sqlite.SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
