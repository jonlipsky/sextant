using System.Text.Json;
using Sextant.Core;
using Sextant.Mcp;
using Sextant.Mcp.Tools;
using Sextant.Store;

namespace Sextant.Mcp.Tests;

/// <summary>
/// Phase 8, acceptance criterion 3: capability-aware MCP query tools return a STRUCTURED
/// feature-unavailable response (in the existing <c>meta</c> envelope) when the data they need was not
/// indexed under the active profile — never a crash or a silently-empty result. A generation that
/// built the feature lets the same tool proceed.
/// </summary>
[TestClass]
public class FeatureUnavailableTests
{
    private readonly List<(string path, IndexDatabase db, DatabaseProvider provider)> _open = new();

    [TestCleanup]
    public void TestCleanup()
    {
        foreach (var (path, db, provider) in _open)
        {
            provider.Dispose();
            SqliteTestDatabase.Delete(path, db);
        }
        _open.Clear();
    }

    private DatabaseProvider Seed(string profile, IndexFeature features)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sextant_cap_{Guid.NewGuid():N}.db");
        var db = new IndexDatabase(path);
        db.RunMigrations();

        var conn = db.GetConnection();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var projectStore = new ProjectStore(conn);
        var projectId = projectStore.Insert(new ProjectIdentity
        {
            CanonicalId = "proj_cap_0123456789",
            GitRemoteUrl = "https://github.com/org/repo",
            RepoRelativePath = "src/App/App.csproj"
        }, now);

        // At least one symbol so the database is servable (Phase-7 readiness gate).
        new SymbolStore(conn).Insert(new SymbolInfo
        {
            ProjectId = projectId,
            SymbolKey = "global::App.Widget",
            FullyQualifiedName = "global::App.Widget",
            DisplayName = "Widget",
            Kind = SymbolKind.Class,
            Accessibility = Accessibility.Public,
            FilePath = "src/App/Widget.cs",
            LineStart = 1, LineEnd = 10,
            LastIndexedAt = now
        });

        // A COMPLETE generation recording the profile's feature set drives the capability gate.
        var runStore = new IndexRunStore(conn);
        var runId = runStore.BeginRun("full", now,
            IndexConfigurationHash.Compute(profile, features, GeneratedSourcePolicies.Exclude),
            profile, (long)features);
        runStore.MarkComplete(runId, now, 1);

        var provider = new DatabaseProvider(path);
        _open.Add((path, db, provider));
        return provider;
    }

    private static bool TryGetFeatureUnavailable(string json, out string feature, out string requiredProfile, out string? activeProfile)
    {
        feature = requiredProfile = string.Empty;
        activeProfile = null;
        using var doc = JsonDocument.Parse(json);
        var meta = doc.RootElement.GetProperty("meta");
        if (!meta.TryGetProperty("feature_unavailable", out var fu))
            return false;

        feature = fu.GetProperty("feature").GetString()!;
        requiredProfile = fu.GetProperty("required_profile").GetString()!;
        activeProfile = fu.TryGetProperty("active_profile", out var ap) ? ap.GetString() : null;
        Assert.IsFalse(string.IsNullOrEmpty(fu.GetProperty("message").GetString()),
            "a feature-unavailable response carries an actionable message");
        // The result set is empty, not a partial/garbage payload.
        Assert.AreEqual(0, doc.RootElement.GetProperty("results").GetArrayLength());
        return true;
    }

    private static bool HasFeatureUnavailable(string json)
        => TryGetFeatureUnavailable(json, out _, out _, out _);

    // === Core profile: every optional-feature tool reports feature-unavailable ======================

    [TestMethod]
    public void CoreProfile_GatesAllOptionalFeatureTools()
    {
        var db = Seed(IndexProfiles.Core, IndexFeature.Core);

        AssertUnavailable(FindCommentsTool.FindComments(db), "comments", "standard");
        AssertUnavailable(SemanticSearchTool.SemanticSearch(db, "widget"), "documentation_search", "standard");
        AssertUnavailable(FindTestsTool.FindTests(db), "test_indexing", "standard");
        AssertUnavailable(TraceValueTool.TraceValue(db, "global::App.Widget.M", "origins"), "dataflow", "deep");
    }

    private static void AssertUnavailable(string json, string expectedFeature, string expectedRequiredProfile)
    {
        Assert.IsTrue(TryGetFeatureUnavailable(json, out var feature, out var required, out var active),
            $"expected a feature_unavailable response for '{expectedFeature}'");
        Assert.AreEqual(expectedFeature, feature);
        Assert.AreEqual(expectedRequiredProfile, required, "the minimum profile that would enable the feature");
        Assert.AreEqual("core", active, "the active profile is surfaced so the agent knows what to re-index at");
    }

    // === Standard profile: comments/doc-search/tests proceed; only deep-only dataflow is gated ======

    [TestMethod]
    public void StandardProfile_AllowsStandardTools_ButGatesDataflow()
    {
        var db = Seed(IndexProfiles.Standard, IndexFeature.Standard);

        Assert.IsFalse(HasFeatureUnavailable(FindCommentsTool.FindComments(db)), "comments is a standard feature");
        Assert.IsFalse(HasFeatureUnavailable(SemanticSearchTool.SemanticSearch(db, "widget")), "doc search is a standard feature");
        Assert.IsFalse(HasFeatureUnavailable(FindTestsTool.FindTests(db)), "test indexing is a standard feature");

        Assert.IsTrue(HasFeatureUnavailable(TraceValueTool.TraceValue(db, "global::App.Widget.M", "origins")),
            "dataflow is deep-only, so it is gated under the standard profile");
    }

    // === Deep profile: nothing is gated ============================================================

    [TestMethod]
    public void DeepProfile_AllowsEveryTool()
    {
        var db = Seed(IndexProfiles.Deep, IndexFeature.Deep);

        Assert.IsFalse(HasFeatureUnavailable(FindCommentsTool.FindComments(db)));
        Assert.IsFalse(HasFeatureUnavailable(SemanticSearchTool.SemanticSearch(db, "widget")));
        Assert.IsFalse(HasFeatureUnavailable(FindTestsTool.FindTests(db)));
        Assert.IsFalse(HasFeatureUnavailable(TraceValueTool.TraceValue(db, "global::App.Widget.M", "origins")),
            "the deep profile builds dataflow, so trace_value proceeds (and returns a normal empty/normal result)");
    }
}
