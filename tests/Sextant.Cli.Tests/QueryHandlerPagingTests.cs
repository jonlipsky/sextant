using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sextant.Cli.Handlers;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Cli.Tests;

/// <summary>
/// <c>get-type-members</c> and <c>get-index-status</c> are paged tools now, so <c>sextant query</c> must pass its
/// <c>--limit</c>/<c>--cursor</c> through: otherwise a long answer prints one page and a cursor nobody can use.
/// </summary>
[TestClass]
[DoNotParallelize]
public class QueryHandlerPagingTests
{
    private const string TypeFqn = "global::App.Widgets.Widget";
    private string _dbPath = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_cli_paging_{Guid.NewGuid():N}.db");
        using var db = new IndexDatabase(_dbPath);
        db.RunMigrations();
        var conn = db.GetConnection();
        var projects = new ProjectStore(conn);
        var symbols = new SymbolStore(conn);
        long? first = null;
        foreach (var path in new[] { "src/App/App.csproj", "src/Lib/Lib.csproj" })
        {
            var id = projects.Insert(new ProjectIdentity
            {
                CanonicalId = $"{Path.GetFileNameWithoutExtension(path)}0123456789",
                GitRemoteUrl = "https://github.com/org/app",
                RepoRelativePath = path
            }, 1000);
            first ??= id;
        }

        symbols.Insert(Symbol(first!.Value, TypeFqn, "Widget", SymbolKind.Class, 1, lineEnd: 100));
        for (var i = 0; i < 3; i++)
            symbols.Insert(Symbol(first.Value, $"{TypeFqn}.Run{i}()", $"Run{i}", SymbolKind.Method, 10 + i));
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            File.Delete(_dbPath + suffix);
    }

    [TestMethod]
    public void GetTypeMembers_PassesLimitAndCursor()
    {
        var page1 = Query("get-type-members", TypeFqn, "--limit", "2");
        Assert.AreEqual(2, page1.GetProperty("results").GetArrayLength());
        Assert.AreEqual(3, page1.GetProperty("meta").GetProperty("total").GetInt32());
        var cursor = page1.GetProperty("meta").GetProperty("next_cursor").GetString()!;

        var page2 = Query("get-type-members", TypeFqn, "--limit", "2", "--cursor", cursor);
        Assert.AreEqual("Run2", page2.GetProperty("results")[0].GetProperty("display_name").GetString());
        Assert.IsFalse(page2.GetProperty("meta").TryGetProperty("next_cursor", out _));
    }

    [TestMethod]
    public void GetIndexStatus_PassesLimitAndCursor()
    {
        var page1 = Query("get-index-status", "--limit", "1");
        Assert.AreEqual("src/App/App.csproj", page1.GetProperty("results")[0].GetProperty("repo_relative_path").GetString());
        Assert.AreEqual(2, page1.GetProperty("index").GetProperty("totals").GetProperty("projects").GetInt32());
        var cursor = page1.GetProperty("meta").GetProperty("next_cursor").GetString()!;

        var page2 = Query("get-index-status", "--limit", "1", "--cursor", cursor);
        Assert.AreEqual("src/Lib/Lib.csproj", page2.GetProperty("results")[0].GetProperty("repo_relative_path").GetString());
    }

    private JsonElement Query(string tool, params string[] args)
    {
        var original = Console.Out;
        using var output = new StringWriter();
        Console.SetOut(output);
        try
        {
            Assert.AreEqual(0, QueryHandler.Run(tool, args, _dbPath, profile: null));
        }
        finally
        {
            Console.SetOut(original);
        }
        return JsonDocument.Parse(output.ToString()).RootElement.Clone();
    }

    private static SymbolInfo Symbol(long projectId, string fqn, string name, SymbolKind kind, int line, int? lineEnd = null) => new()
    {
        ProjectId = projectId,
        SymbolKey = fqn,
        FullyQualifiedName = fqn,
        DisplayName = name,
        Kind = kind,
        Accessibility = Accessibility.Public,
        FilePath = "src/App/Widget.cs",
        LineStart = line,
        LineEnd = lineEnd ?? line,
        LastIndexedAt = 1000
    };
}
