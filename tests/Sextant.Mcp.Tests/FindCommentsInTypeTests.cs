using System.Text.Json;
using Sextant.Core;
using Sextant.Mcp;
using Sextant.Mcp.Tools;
using Sextant.Store;

namespace Sextant.Mcp.Tests;

/// <summary>Issue #221: <c>in_symbol</c> on a type also returns the comments of its members and nested types.</summary>
[TestClass]
public class FindCommentsInTypeTests
{
    private static string _dbPath = "";
    private static DatabaseProvider _provider = null!;

    [ClassInitialize]
    public static void Seed(TestContext _)
    {
        _dbPath = TestTempRoot.NewFilePath("sextant_comments_in_type", ".db");
        var db = new IndexDatabase(_dbPath);
        db.RunMigrations();
        var conn = db.GetConnection();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var projectId = new ProjectStore(conn).Insert(new ProjectIdentity
        {
            CanonicalId = "proj_widgets_000001",
            GitRemoteUrl = "https://github.com/test/widgets",
            RepoRelativePath = "src/Widgets/Widgets.csproj"
        }, now);

        var symbols = new SymbolStore(conn);
        long Add(string key, string name, SymbolKind kind, string fqn) => symbols.Insert(new SymbolInfo
        {
            ProjectId = projectId, SymbolKey = key, FullyQualifiedName = fqn, DisplayName = name, Kind = kind,
            Accessibility = Accessibility.Public, FilePath = "src/Widgets/Widget.cs", LineStart = 1, LineEnd = 50,
            LastIndexedAt = now
        });

        var widget = Add("T:Ns.Widget", "Widget", SymbolKind.Class, "global::Ns.Widget");
        var run = Add("M:Ns.Widget.Run", "Run", SymbolKind.Method, "global::Ns.Widget.Run()");
        var inner = Add("T:Ns.Widget.Inner", "Inner", SymbolKind.Class, "global::Ns.Widget.Inner");
        var go = Add("M:Ns.Widget.Inner.Go", "Go", SymbolKind.Method, "global::Ns.Widget.Inner.Go()");
        var sibling = Add("T:Ns.Widgets", "Widgets", SymbolKind.Class, "global::Ns.Widgets");
        var other = Add("M:Ns.Widgets.Stop", "Stop", SymbolKind.Method, "global::Ns.Widgets.Stop()");

        var comments = new CommentStore(conn);
        comments.Insert(projectId, "src/Widgets/Widget.cs", 3, "TODO", "on the type", widget, now);
        comments.Insert(projectId, "src/Widgets/Widget.cs", 7, "TODO", "in a method", run, now);
        comments.Insert(projectId, "src/Widgets/Widget.cs", 12, "FIXME", "in a nested type", inner, now);
        comments.Insert(projectId, "src/Widgets/Widget.cs", 15, "HACK", "in a nested method", go, now);
        comments.Insert(projectId, "src/Widgets/Widgets.cs", 4, "TODO", "in a sibling type", sibling, now);
        comments.Insert(projectId, "src/Widgets/Widgets.cs", 8, "TODO", "in a sibling method", other, now);

        _provider = new DatabaseProvider(_dbPath);
    }

    private static List<string> Texts(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("results").EnumerateArray()
            .Select(r => r.GetProperty("text").GetString()!).ToList();

    [TestMethod]
    public void InSymbol_OnAType_ReturnsItsMembersAndNestedTypesComments_NotASiblingsWithTheSamePrefix()
    {
        var texts = Texts(FindCommentsTool.FindComments(_provider, in_symbol: "Ns.Widget"));

        CollectionAssert.AreEquivalent(
            new[] { "on the type", "in a method", "in a nested type", "in a nested method" }, texts);
    }

    [TestMethod]
    public void InSymbol_OnAMethod_ReturnsOnlyThatMethodsComments()
    {
        var texts = Texts(FindCommentsTool.FindComments(_provider, in_symbol: "Ns.Widget.Run"));

        CollectionAssert.AreEqual(new[] { "in a method" }, texts);
    }

    [TestMethod]
    public void InSymbol_OnAType_StillHonoursTheTagFilter()
    {
        var texts = Texts(FindCommentsTool.FindComments(_provider, tag: "HACK", in_symbol: "Ns.Widget"));

        CollectionAssert.AreEqual(new[] { "in a nested method" }, texts);
    }
}
