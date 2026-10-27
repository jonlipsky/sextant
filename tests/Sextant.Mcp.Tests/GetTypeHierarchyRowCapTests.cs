using Sextant.Core;
using Sextant.Mcp.Tools;
using Sextant.Store;

namespace Sextant.Mcp.Tests;

/// <summary>Finding 2: <c>get_type_hierarchy</c> stops collecting at a row cap and says so.</summary>
[TestClass]
public class GetTypeHierarchyRowCapTests
{
    private const int Implementers = 60;
    private static DatabaseProvider _provider = null!;

    [ClassInitialize]
    public static void Seed(TestContext _)
    {
        var db = new IndexDatabase(TestTempRoot.NewFilePath("sextant_hierarchy_cap", ".db"));
        db.RunMigrations();
        var conn = db.GetConnection();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var projectId = new ProjectStore(conn).Insert(new ProjectIdentity
        {
            CanonicalId = "proj_wide_000001",
            GitRemoteUrl = "https://github.com/test/wide",
            RepoRelativePath = "src/Wide/Wide.csproj"
        }, now);

        var symbols = new SymbolStore(conn);
        long Add(string name, SymbolKind kind) => symbols.Insert(new SymbolInfo
        {
            ProjectId = projectId, SymbolKey = $"T:Ns.{name}", FullyQualifiedName = $"global::Ns.{name}",
            DisplayName = name, Kind = kind, Accessibility = Accessibility.Public,
            FilePath = "src/Wide/Wide.cs", LineStart = 1, LineEnd = 2, LastIndexedAt = now
        });

        var iface = Add("IWide", SymbolKind.Interface);
        var rels = new RelationshipStore(conn);
        for (var i = 0; i < Implementers; i++)
        {
            var impl = Add($"Impl{i:D3}", SymbolKind.Class);
            rels.Insert(new RelationshipInfo
            {
                FromSymbolId = impl, ToSymbolId = iface, Kind = RelationshipKind.Implements, LastIndexedAt = now
            });
        }

        _provider = new DatabaseProvider(db.DbPath) { MaxResponseChars = 1000 };
    }

    [TestMethod]
    public void AWideHierarchy_StopsAtTheRowCap_AndReportsTruncation()
    {
        var json = GetTypeHierarchyTool.GetTypeHierarchy(_provider, "Ns.IWide", "down");

        StringAssert.Contains(json, "Stopped collecting at 20 rows");
        StringAssert.Contains(json, "get_implementors");
    }

    [TestMethod]
    public void ANarrowHierarchy_ReportsNoCapMessage()
    {
        var json = GetTypeHierarchyTool.GetTypeHierarchy(_provider, "Ns.Impl000", "down");

        Assert.IsFalse(json.Contains("Stopped collecting"));
    }
}
