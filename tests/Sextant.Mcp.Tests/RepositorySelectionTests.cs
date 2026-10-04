using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sextant.Core;
using Sextant.Mcp;
using Sextant.Mcp.Tools;
using Sextant.Store;

namespace Sextant.Mcp.Tests;

/// <summary>
/// The request-level repository selector (<see cref="DatabaseProvider.RequestedRepository"/>) and the
/// fail-closed selection hook (<see cref="DatabaseProvider.RequireRepositorySelection"/>). Both default to
/// off, so a provider with no selector reads the unselected default exactly as before (for a
/// multi-repository catalog that is <see cref="SnapshotReadScope.Unscoped"/>). With the hook on, a read that
/// names no repository fails with <c>repository_required</c>, whose bytes depend only on the request. A
/// named repository with no complete snapshot never widens to the unselected default.
/// </summary>
[TestClass]
public class RepositorySelectionTests
{
    private const string RepoA = "https://github.com/org/a";
    private const string RepoB = "https://github.com/org/b";
    private const string SharedFqn = "global::Shared.T";
    private const string FeatureBranch = "feature/x";

    private string _dbPath = "";
    private IndexDatabase _db = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_reposel_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        var conn = _db.GetConnection();
        var store = new SnapshotStore(conn);
        SeedSelectedRepo(conn, store, RepoA, "logical_A");
        SeedSelectedRepo(conn, store, RepoB, "logical_B");
    }

    [TestCleanup]
    public void Cleanup() => SqliteTestDatabase.Delete(_dbPath, _db);

    [TestMethod]
    public void Defaults_NameNoRepository_AndRequireNoSelection()
    {
        using var provider = new DatabaseProvider(_dbPath);

        Assert.IsNull(provider.RequestedRepository(), "no selector by default");
        Assert.IsNull(provider.RequestedBranch(), "no branch selector by default");
        Assert.IsFalse(provider.RequireRepositorySelection(), "a selection is not required by default");
    }

    [TestMethod]
    public void NamedBranch_PinsThatBranchSnapshot()
    {
        SeedFeatureBranch();
        using var provider = new DatabaseProvider(_dbPath)
        {
            RequestedRepository = () => RepoA,
            RequestedBranch = () => FeatureBranch
        };

        var json = FindShared(provider);

        Assert.AreEqual(1, Meta(json).GetProperty("result_count").GetInt32());
        StringAssert.Contains(json, "logical_A_feature", "the named branch's snapshot is read");
        Assert.IsFalse(json.Contains("\"logical_A\""), "not the default branch's snapshot");
        Assert.IsFalse(json.Contains("logical_B"));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("  ")]
    public void BlankBranch_ReadsTheDefaultBranch(string? branch)
    {
        SeedFeatureBranch();
        using var provider = new DatabaseProvider(_dbPath)
        {
            RequestedRepository = () => RepoA,
            RequestedBranch = () => branch
        };

        var json = FindShared(provider);

        StringAssert.Contains(json, "\"logical_A\"");
        Assert.IsFalse(json.Contains("logical_A_feature"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NamedBranch_WithoutRepository_FailsWithRepositoryRequired(bool requireSelection)
    {
        // A branch only means something inside a repository, so it never falls back to an unscoped read, even
        // when the host does not require a selection.
        using var provider = new DatabaseProvider(_dbPath)
        {
            RequestedBranch = () => "main",
            RequireRepositorySelection = () => requireSelection
        };

        Assert.IsFalse(provider.TryBeginRead(out _, out _, out var failure));
        Assert.AreEqual(ResponseBuilder.RepositoryRequiredCode,
            Meta(failure).GetProperty("error").GetProperty("code").GetString());
        Assert.AreSame(SnapshotReadScope.DenyAll,
            FederatedReadContext.Resolve(_db, requestedBranch: () => "main").Scope,
            "a caller that ignores the gate still reads nothing");
    }

    [TestMethod]
    [DataRow("no-such-branch")]
    [DataRow("Main", DisplayName = "branch names match exactly")]
    public void UnresolvedNamedBranch_NeverWidens_AndSaysWhy(string branch)
    {
        using var provider = new DatabaseProvider(_dbPath)
        {
            RequestedRepository = () => RepoA,
            RequestedBranch = () => branch
        };

        Assert.IsFalse(provider.TryBeginRead(out _, out _, out var failure));

        var root = JsonDocument.Parse(failure).RootElement;
        Assert.AreEqual(0, root.GetProperty("meta").GetProperty("result_count").GetInt32());
        StringAssert.Contains(root.GetProperty("message").GetString(), "requested repository branch");
        Assert.IsFalse(failure.Contains(branch), "the requested branch is not echoed");

        var resolved = FederatedReadContext.Resolve(
            _db, requestedRepository: () => RepoA, requestedBranch: () => branch);
        Assert.IsTrue(resolved.SelectionUnresolved);
        Assert.IsTrue(resolved.BranchRequested);
        Assert.AreSame(SnapshotReadScope.DenyAll, resolved.Scope);
    }

    [TestMethod]
    public void UnresolvedNamedBranch_UnderEnforcement_IsTheUniformNotFound()
    {
        using var provider = new DatabaseProvider(_dbPath, new AllowingEnforcingAuthorizer())
        {
            RequestedRepository = () => RepoA,
            RequestedBranch = () => "no-such-branch"
        };

        Assert.IsFalse(provider.TryBeginRead(out _, out _, out var failure));
        Assert.AreEqual(Normalize(ResponseBuilder.BuildNotFound()), Normalize(failure),
            "an enforcing path never distinguishes an unknown branch from a denial");
    }

    [TestMethod]
    public void BranchOfAnotherRepository_IsNotSelected()
    {
        SeedFeatureBranch();
        using var provider = new DatabaseProvider(_dbPath)
        {
            RequestedRepository = () => RepoB,
            RequestedBranch = () => FeatureBranch
        };

        Assert.IsFalse(provider.TryBeginRead(out _, out _, out _), "the branch belongs to repository A only");
    }

    private void SeedFeatureBranch() =>
        SeedSelectedRepo(_db.GetConnection(), new SnapshotStore(_db.GetConnection()), RepoA, "logical_A_feature",
            commit: "c2", branchName: FeatureBranch, isDefault: false);

    [TestMethod]
    public void NoSelector_MultiRepositoryCatalog_ReadsUnscoped_AcrossEveryRepository()
    {
        using var provider = new DatabaseProvider(_dbPath);

        Assert.IsTrue(provider.TryBeginRead(out _, out var context, out _));
        Assert.AreSame(SnapshotReadScope.Unscoped, context.Scope,
            "with no selector a multi-repository read stays unscoped, as before the hook existed");

        var meta = Meta(FindShared(provider));
        Assert.AreEqual(2, meta.GetProperty("ambiguous_match_count").GetInt32(),
            "the unscoped read sees the symbol in BOTH repositories");
    }

    [TestMethod]
    public void NamedRepository_PinsThatRepository()
    {
        using var provider = new DatabaseProvider(_dbPath) { RequestedRepository = () => RepoB };

        var json = FindShared(provider);

        var meta = Meta(json);
        Assert.IsFalse(meta.TryGetProperty("ambiguous", out _), "only the named repository's symbol is visible");
        Assert.AreEqual(1, meta.GetProperty("result_count").GetInt32());
        StringAssert.Contains(json, "logical_B");
        Assert.IsFalse(json.Contains("logical_A"), "the other repository is scoped out");
    }

    [TestMethod]
    public void RequireSelection_NoSelector_FailsWithRepositoryRequired()
    {
        using var provider = new DatabaseProvider(_dbPath) { RequireRepositorySelection = () => true };

        Assert.IsFalse(provider.TryBeginRead(out _, out _, out var failure));

        var meta = Meta(failure);
        Assert.AreEqual(ResponseBuilder.RepositoryRequiredCode, meta.GetProperty("error").GetProperty("code").GetString());
        Assert.AreEqual(0, meta.GetProperty("result_count").GetInt32());
        Assert.IsFalse(failure.Contains(RepoA) || failure.Contains(RepoB), "the error names no repository");
        Assert.AreEqual(Normalize(failure), Normalize(FindShared(provider)), "every tool returns the same error");
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void RequireSelection_BlankSelector_FailsWithRepositoryRequired(string? selector)
    {
        using var provider = new DatabaseProvider(_dbPath)
        {
            RequestedRepository = () => selector,
            RequireRepositorySelection = () => true
        };

        Assert.IsFalse(provider.TryBeginRead(out _, out _, out var failure));
        Assert.AreEqual(ResponseBuilder.RepositoryRequiredCode,
            Meta(failure).GetProperty("error").GetProperty("code").GetString());
    }

    [TestMethod]
    public void RequireSelection_ErrorDependsOnlyOnTheRequest_NotOnTheCatalog()
    {
        // The same request gets the same bytes against a populated catalog, a missing database, and under an
        // enforcing authorizer, so the error cannot reveal whether the service is provisioned or what it holds.
        using var populated = new DatabaseProvider(_dbPath) { RequireRepositorySelection = () => true };
        using var missing = new DatabaseProvider(
            Path.Combine(Path.GetTempPath(), $"sextant_missing_{Guid.NewGuid():N}.db")) { RequireRepositorySelection = () => true };
        using var enforced = new DatabaseProvider(_dbPath, new DenyingEnforcingAuthorizer())
        {
            RequireRepositorySelection = () => true
        };

        Assert.IsFalse(populated.TryBeginRead(out _, out _, out var fromPopulated));
        Assert.IsFalse(missing.TryBeginRead(out _, out _, out var fromMissing));
        Assert.IsFalse(enforced.TryBeginRead(out _, out _, out var fromEnforced));

        Assert.AreEqual(Normalize(fromPopulated), Normalize(fromMissing));
        Assert.AreEqual(Normalize(fromPopulated), Normalize(fromEnforced));
    }

    [TestMethod]
    public void RequireSelection_NamedRepository_IsServed()
    {
        using var provider = new DatabaseProvider(_dbPath)
        {
            RequestedRepository = () => RepoA,
            RequireRepositorySelection = () => true
        };

        var json = FindShared(provider);

        Assert.IsFalse(Meta(json).TryGetProperty("error", out _), "a named repository satisfies the requirement");
        StringAssert.Contains(json, "logical_A");
        Assert.IsFalse(json.Contains("logical_B"));
    }

    [TestMethod]
    public void UnresolvedNamedRepository_NeverWidensToTheUnselectedDefault()
    {
        // Naming a repository with no complete snapshot must not satisfy the requirement and then read every
        // repository through the unscoped fallback.
        using var provider = new DatabaseProvider(_dbPath)
        {
            RequestedRepository = () => "https://github.com/org/not-indexed",
            RequireRepositorySelection = () => true
        };

        Assert.IsFalse(provider.TryBeginRead(out _, out _, out var failure));

        var root = JsonDocument.Parse(failure).RootElement;
        Assert.AreEqual(0, root.GetProperty("meta").GetProperty("result_count").GetInt32());
        Assert.IsFalse(root.GetProperty("meta").TryGetProperty("error", out _));
        StringAssert.Contains(root.GetProperty("message").GetString(), "requested repository",
            "the permissive path says why it read nothing");
        Assert.IsFalse(failure.Contains("not-indexed"), "the requested repository is not echoed");

        var resolved = FederatedReadContext.Resolve(_db, requestedRepository: () => "https://github.com/org/not-indexed");
        Assert.IsTrue(resolved.SelectionUnresolved);
        Assert.AreSame(SnapshotReadScope.DenyAll, resolved.Scope, "a caller that ignores the gate still reads nothing");
    }

    [TestMethod]
    public void UnresolvedNamedRepository_UnderEnforcement_IsTheUniformNotFound()
    {
        using var provider = new DatabaseProvider(_dbPath, new AllowingEnforcingAuthorizer())
        {
            RequestedRepository = () => "https://github.com/org/not-indexed"
        };

        Assert.IsFalse(provider.TryBeginRead(out _, out _, out var failure));
        Assert.AreEqual(Normalize(ResponseBuilder.BuildNotFound()), Normalize(failure),
            "an enforcing path never distinguishes an unresolved repository from a denial");
    }

    [TestMethod]
    public void LocalStdioHost_ProviderKeepsTheDefaults_AndReadsUnscoped()
    {
        // The local stdio MCP server never wires a selector or the requirement, so its reads are unchanged.
        using var host = McpServerSetup.CreateMcpHost([], _dbPath).Build();
        var provider = host.Services.GetRequiredService<DatabaseProvider>();

        Assert.IsNull(provider.RequestedRepository());
        Assert.IsNull(provider.RequestedBranch());
        Assert.IsFalse(provider.RequireRepositorySelection());
        Assert.IsTrue(provider.TryBeginRead(out _, out var context, out _));
        Assert.AreSame(SnapshotReadScope.Unscoped, context.Scope);
        Assert.AreEqual(2, Meta(FindShared(provider)).GetProperty("ambiguous_match_count").GetInt32());
    }

    [TestMethod]
    public void LocalStdioHost_RegistersOnlyTheToolErrorFilter()
    {
        // The per-call selection filters (SVC-2) belong to the service's /mcp only: the local stdio server's
        // tools/list stays byte-identical, with no reserved selector arguments. Its one call-tool filter is the
        // tool-error marker (isError on a meta.error result), which every host registers.
        using var host = McpServerSetup.CreateMcpHost([], _dbPath).Build();
        var filters = host.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<ModelContextProtocol.Server.McpServerOptions>>()
            .Value.Filters.Request;

        Assert.AreEqual(0, filters.ListToolsFilters.Count);
        Assert.AreEqual(1, filters.CallToolFilters.Count);
    }

    [TestMethod]
    public void LocalStdioHost_RegistersNoServiceOnlyTools()
    {
        // list_repositories (SVC-4) and search_symbols (SVC-F) live in Sextant.Service: the local stdio server only
        // registers Sextant.Mcp's tools, so its tools/list is unchanged by them.
        using var host = McpServerSetup.CreateMcpHost([], _dbPath).Build();
        var names = host.Services.GetServices<ModelContextProtocol.Server.McpServerTool>()
            .Select(t => t.ProtocolTool.Name)
            .ToList();

        Assert.IsTrue(names.Contains("find_symbol"), string.Join(", ", names));
        CollectionAssert.DoesNotContain(names, "list_repositories");
        CollectionAssert.DoesNotContain(names, "search_symbols");
    }

    [TestMethod]
    public async Task LocalHttpHost_ProviderKeepsTheDefaults()
    {
        await using var app = McpServerSetup.CreateHttpMcpHost([], port: 0, dbPath: _dbPath);
        var provider = app.Services.GetRequiredService<DatabaseProvider>();

        Assert.IsNull(provider.RequestedRepository());
        Assert.IsNull(provider.RequestedBranch());
        Assert.IsFalse(provider.RequireRepositorySelection());
    }

    private static string FindShared(DatabaseProvider provider) =>
        FindSymbolTool.FindSymbol(provider, SharedFqn).GetAwaiter().GetResult();

    private static JsonElement Meta(string json) => JsonDocument.Parse(json).RootElement.GetProperty("meta");

    private static string Normalize(string json) =>
        System.Text.RegularExpressions.Regex.Replace(json, "\"queried_at\":\\s*\\d+", "\"queried_at\":0");

    private static void SeedSelectedRepo(
        Microsoft.Data.Sqlite.SqliteConnection conn, SnapshotStore store, string url, string canonical,
        string commit = "c1", string branchName = "main", bool isDefault = true)
    {
        var repoId = store.EnsureRepository(url, now: 1);
        var commitId = store.EnsureCommit(repoId, commit, "t1", now: 1);
        var logical = store.EnsureLogicalProject(repoId, canonical, "src/P/P.csproj", "net10.0", now: 1);
        var identity = new SnapshotIdentity
        {
            RepositoryRemoteUrl = url, CommitSha = commit, TreeSha = "t1",
            SchemaVersion = IndexDatabase.SnapshotSchemaVersion,
            AnalyzerVersion = IndexConfigurationHash.AnalyzerVersion,
            ConfigHash = "cfg", ToolchainFingerprint = ToolchainFingerprint.Current
        };
        var snapId = store.BeginPending(identity, repoId, commitId, runId: null, now: 1).id;
        var project = new ProjectIdentity
        {
            CanonicalId = canonical, GitRemoteUrl = url, RepoRelativePath = "src/P/P.csproj", TargetFramework = "net10.0"
        };
        var projId = new ProjectStore(conn).UpsertSnapshotProject(project, snapId, logical, 1);
        store.MapProject(snapId, projId);
        new SymbolStore(conn).Insert(new SymbolInfo
        {
            ProjectId = projId,
            SymbolKey = "T:Shared.T", FullyQualifiedName = SharedFqn,
            DisplayName = "T", Kind = SymbolKind.Class, Accessibility = Accessibility.Public,
            FilePath = "src/P/T.cs", LineStart = 1, LineEnd = 2, LastIndexedAt = 1
        });
        store.MarkComplete(snapId, publishedAt: 1);
        var branch = store.EnsureBranch(repoId, branchName, isDefault, now: 1);
        store.SetBranchPointer(branch, snapId, now: 1);
    }

    private sealed class DenyingEnforcingAuthorizer : IReadAuthorizer
    {
        public bool IsEnforcing => true;
        public ReadAuthorization Authorize(SnapshotRow? selected) => ReadAuthorization.Deny("denied");
        public ReadAuthorization AuthorizeRepository(long repositoryId, string remoteUrl) => ReadAuthorization.Deny("denied");
    }

    /// <summary>Enforcing but allows everything, even a null selection, to isolate the unresolved-selection gate.</summary>
    private sealed class AllowingEnforcingAuthorizer : IReadAuthorizer
    {
        public bool IsEnforcing => true;
        public ReadAuthorization Authorize(SnapshotRow? selected) => ReadAuthorization.Allow;
        public ReadAuthorization AuthorizeRepository(long repositoryId, string remoteUrl) => ReadAuthorization.Allow;
    }
}
