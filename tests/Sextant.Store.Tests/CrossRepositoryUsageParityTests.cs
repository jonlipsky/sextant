using System.Globalization;
using Microsoft.Data.Sqlite;
using Sextant.Core;

namespace Sextant.Store.Tests;

/// <summary>
/// Issue #160 result parity: the target-driven, forced-join-order usage and candidate queries must return
/// exactly the rows, in exactly the order, of the pre-#160 edge-driven SQL (embedded below as the oracle).
/// The catalog is small but covers the shapes that could diverge: superseded/pending/complete consumer
/// generations, several branches on one snapshot, a commit shared by two consumer repositories, a
/// multi-TFM provider, a second provider repository defining the same key, a same-FQN symbol with a
/// different key, references to a non-pinned provider version and from a project without an edge,
/// duplicate occurrences at one location, and call-edge rows beside their pure references.
/// </summary>
[TestClass]
public sealed class CrossRepositoryUsageParityTests
{
    private const string ProviderP = "https://github.com/org/mix-p";
    private const string ProviderQ = "https://github.com/org/mix-q";
    private const string ConsumerA = "https://github.com/org/app-a";
    private const string ConsumerB = "https://github.com/org/app-b";
    private const string ConsumerC = "https://github.com/org/app-c";
    private const string ConsumerD = "https://github.com/org/app-d";
    private const string Key = "T:Mix.ImageButton";
    private const string Key2 = "M:Mix.ImageButton.Click";
    private const string SameFqnOtherKey = "T:Mix.ImageButton`1";

    private static readonly string[] Keys = [Key, Key2, SameFqnOtherKey, "T:Mix.Missing"];

    private static readonly (string name, CrossRepoUsageScope scope)[] Scopes =
    [
        ("default", CrossRepoUsageScope.DefaultHeads),
        ("branch main", new CrossRepoUsageScope { Branch = "main" }),
        ("branch release", new CrossRepoUsageScope { Branch = "release" }),
        ("branch feature", new CrossRepoUsageScope { Branch = "feature" }),
        ("branch missing", new CrossRepoUsageScope { Branch = "missing" }),
        ("commit a1", new CrossRepoUsageScope { ConsumerCommitSha = "a1" }),
        ("commit a2", new CrossRepoUsageScope { ConsumerCommitSha = "a2" }),
        ("commit shared", new CrossRepoUsageScope { ConsumerCommitSha = "shared" }),
        ("commit missing", new CrossRepoUsageScope { ConsumerCommitSha = "missing" })
    ];

    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SqliteConnection _conn = null!;
    private SnapshotStore _snapshots = null!;
    private readonly Dictionary<string, long> _repos = [];

    [TestInitialize]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_xrepo_parity_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _conn = _db.GetConnection();
        _snapshots = new SnapshotStore(_conn);
        Seed();
    }

    [TestCleanup]
    public void Cleanup() => SqliteTestDatabase.Delete(_dbPath, _db);

    [TestMethod]
    public void UsageRows_MatchThePreFixQuery_RowForRow()
    {
        var nonEmpty = AssertUsageParity();
        Assert.IsTrue(nonEmpty >= 40, $"the parity matrix must exercise real rows, not just empty results ({nonEmpty} non-empty cases)");
    }

    [TestMethod]
    public void UsageRows_MatchThePreFixQuery_WithStatistics()
    {
        Exec("ANALYZE;");
        AssertUsageParity();
    }

    [TestMethod]
    public void CandidateRepositories_MatchThePreFixQuery()
    {
        var nonEmpty = 0;
        foreach (var provider in new[] { ProviderP, ProviderQ })
        foreach (var key in Keys)
        foreach (var (scopeName, scope) in Scopes)
        {
            var store = new SnapshotDependencyStore(_conn);
            using var actual = store.CreateCandidateConsumerRepositoriesCommand(_repos[provider], key, scope);
            using var oracle = OracleCommand("SELECT DISTINCT crepo.id, crepo.remote_url", scope, "", "", actual);
            var expected = Rows(oracle).Order(StringComparer.Ordinal).ToList();
            CollectionAssert.AreEqual(expected, Rows(actual).Order(StringComparer.Ordinal).ToList(),
                $"candidates provider={provider} key={key} scope={scopeName}");
            if (expected.Count > 0)
                nonEmpty++;
        }
        Assert.IsTrue(nonEmpty >= 10, $"the candidate matrix must exercise real rows ({nonEmpty} non-empty cases)");
    }

    [TestMethod]
    public void CatalogShapes_AreExercised()
    {
        // Guards the seed itself, so a future edit cannot quietly hollow out the parity matrix.
        var store = new SnapshotDependencyStore(_conn);
        var heads = store.FindCrossRepositoryUsages(ProviderP, Key, CrossRepoUsageScope.DefaultHeads, null);
        Assert.IsTrue(heads.GroupBy(u => (u.ConsumerRepositoryUrl, u.FilePath, u.Line, u.Column)).Any(g => g.Count() > 1),
            "duplicate occurrences at one location must both be returned");
        CollectionAssert.AreEquivalent(new[] { ConsumerA, ConsumerB, ConsumerD },
            heads.Select(u => u.ConsumerRepositoryUrl).Distinct().ToArray(), "default heads: A, B and D (C is pending)");
        Assert.IsFalse(heads.Any(u => u.FilePath == "src/A3/Gamma.cs"), "a project without an edge to the provider contributes nothing");
        Assert.IsFalse(heads.Any(u => u.Line is 20 or 21), "non-pinned provider versions and unpinned provider projects are excluded");

        var shared = store.FindCrossRepositoryUsages(ProviderP, Key, new CrossRepoUsageScope { ConsumerCommitSha = "shared" }, null);
        CollectionAssert.AreEquivalent(new[] { "dev", "main" }, shared.Select(u => u.ConsumerBranch).Distinct().ToArray(),
            "a commit-scope snapshot with two branch pointers yields one row per branch");
        Assert.IsTrue(shared.All(u => u.ConsumerRepositoryUrl == ConsumerD), "the pending snapshot sharing the commit is excluded");

        var feature = store.FindCrossRepositoryUsages(ProviderP, Key, new CrossRepoUsageScope { Branch = "feature" }, null);
        Assert.IsTrue(feature.Count > 0 && feature.All(u => u.ConsumerCommitSha == "a1"), "a superseded generation is served by its branch");
    }

    [TestMethod]
    public void UsageCommand_EmptyAuthorizationSet_FailsClosed()
    {
        // The builder must never treat an empty authorized set as allow-all, even if a caller skips the
        // early deny-all return in FindCrossRepositoryUsages.
        var store = new SnapshotDependencyStore(_conn);
        foreach (var (scopeName, scope) in Scopes)
        {
            using var allowAll = store.CreateCrossRepositoryUsagesCommand(_repos[ProviderP], Key, scope, null);
            using var denyAll = store.CreateCrossRepositoryUsagesCommand(_repos[ProviderP], Key, scope, Array.Empty<long>());
            Assert.AreEqual(0, Rows(denyAll).Count, $"empty authorization set must match nothing (scope={scopeName})");
            if (scope == CrossRepoUsageScope.DefaultHeads)
                Assert.IsTrue(Rows(allowAll).Count > 0, "the null (allow-all) control must return rows");
        }
        Assert.AreEqual(0, store.FindCrossRepositoryUsages(ProviderP, Key, CrossRepoUsageScope.DefaultHeads, []).Count);
    }

    private int AssertUsageParity()
    {
        long[]?[] filters =
        [
            null,
            [_repos[ConsumerA]],
            [_repos[ConsumerA], _repos[ConsumerC]],
            [_repos[ConsumerB]],
            [_repos[ConsumerD], _repos[ConsumerB]],
            [999_999]
        ];

        var nonEmpty = 0;
        foreach (var provider in new[] { ProviderP, ProviderQ })
        foreach (var key in Keys)
        foreach (var (scopeName, scope) in Scopes)
        foreach (var filter in filters)
        {
            var store = new SnapshotDependencyStore(_conn);
            using var actual = store.CreateCrossRepositoryUsagesCommand(_repos[provider], key, scope, filter);
            var authFilter = filter is null ? "" : $" AND crepo.id IN ({string.Join(",", filter)})";
            using var oracle = OracleCommand(
                """
                SELECT crepo.remote_url, cb.name, cc.commit_sha, clp.canonical_id,
                       cf.repo_relative_path, o.line, o.col, o.kind, d.provider_commit_sha, d.submodule_dirty
                """,
                scope, authFilter,
                "ORDER BY crepo.remote_url, clp.canonical_id, cf.repo_relative_path, o.line, o.col, o.id, cb.name",
                actual);
            var expected = Rows(oracle);
            CollectionAssert.AreEqual(expected, Rows(actual),
                $"usages provider={provider} key={key} scope={scopeName} filter={(filter is null ? "null" : string.Join(",", filter))}");
            if (expected.Count > 0)
                nonEmpty++;
        }
        return nonEmpty;
    }

    /// <summary>The pre-#160 edge-driven FROM/WHERE, verbatim, with the planner free to order the joins.
    /// Parameters are copied from the command under test, so both run with identical bindings.</summary>
    private SqliteCommand OracleCommand(string select, CrossRepoUsageScope scope, string authFilter, string orderBy, SqliteCommand bindingsFrom)
    {
        var scopeJoin = scope.ConsumerCommitSha != null
            ? """
              JOIN commits cc ON cc.id = cs.commit_id AND cc.commit_sha = @consumer_commit
              LEFT JOIN branches cb ON cb.snapshot_id = d.consumer_snapshot_id
              """
            : $"""
              JOIN branches cb ON cb.snapshot_id = d.consumer_snapshot_id {(scope.Branch != null ? "AND cb.name = @branch" : "AND cb.is_default = 1")}
              LEFT JOIN commits cc ON cc.id = cs.commit_id
              """;
        var cmd = _conn.CreateCommand();
        cmd.CommandText = $"""
            {select}
            FROM snapshot_dependencies d
            JOIN repositories prepo ON prepo.id = d.provider_repository_id AND prepo.id = @provider_repo_id
            JOIN symbols psym ON psym.project_id = d.provider_project_id AND psym.symbol_key = @symbol_key
            JOIN occurrences o ON o.target_symbol_id = psym.id AND o.in_project_id = d.consumer_project_id
                AND o.source_symbol_id IS NULL
            JOIN snapshots cs ON cs.id = d.consumer_snapshot_id
            JOIN projects cp ON cp.id = d.consumer_project_id
            JOIN logical_projects clp ON clp.id = cp.logical_project_id
            JOIN repositories crepo ON crepo.id = clp.repository_id
            JOIN file_versions cfv ON cfv.id = o.file_version_id
            JOIN files cf ON cf.id = cfv.file_id
            {scopeJoin}
            WHERE cs.status IN (@published_complete, @published_superseded){authFilter}
            {orderBy};
            """;
        foreach (SqliteParameter p in bindingsFrom.Parameters)
            cmd.Parameters.AddWithValue(p.ParameterName, p.Value);
        return cmd;
    }

    private static List<string> Rows(SqliteCommand cmd)
    {
        var rows = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var cells = new string[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
                cells[i] = reader.IsDBNull(i) ? "<null>" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)!;
            rows.Add(string.Join(" | ", cells));
        }
        return rows;
    }

    // ==== the catalog ===========================================================================

    private readonly record struct Snapshot(long Id, long RepoId);

    private void Seed()
    {
        // Provider P: two pinned commits; p2 carries a multi-TFM project. Provider Q defines the same key.
        var p1 = Provider(ProviderP, "p1");
        var pCore1 = Project(p1, ProviderP, "mix_core", "lib/Core/Core.csproj");
        var pUi1 = Project(p1, ProviderP, "mix_ui", "lib/Ui/Ui.csproj");
        var p2 = Provider(ProviderP, "p2");
        var pCore2 = Project(p2, ProviderP, "mix_core", "lib/Core/Core.csproj");
        var pCore2Net8 = Project(p2, ProviderP, "mix_core_net8", "lib/Core/Core.csproj", "net8.0");
        var pUi2 = Project(p2, ProviderP, "mix_ui", "lib/Ui/Ui.csproj");
        var q1 = Provider(ProviderQ, "q1");
        var qCore = Project(q1, ProviderQ, "q_core", "lib/Q/Q.csproj");

        var kCore1 = Symbol(pCore1, Key);
        var kCore2 = Symbol(pCore2, Key);
        var kCore2Net8 = Symbol(pCore2Net8, Key);
        var k2Core2 = Symbol(pCore2, Key2);
        var kUi2 = Symbol(pUi2, Key);
        var otherKeyUi1 = Symbol(pUi1, SameFqnOtherKey);
        var otherKeyUi2 = Symbol(pUi2, SameFqnOtherKey);
        var kQ = Symbol(qCore, Key);
        foreach (var s in new[] { p1, p2, q1 })
            _snapshots.MarkComplete(s.Id, publishedAt: 1);

        // Consumer A: a superseded generation (feature branch) and the current head (main + release).
        var a1 = Consumer(ConsumerA, "a1");
        var aOld = Project(a1, ConsumerA, "a_app", "src/A1/A1.csproj");
        var aOldUse = FileVersion(aOld, "src/A1/Use.cs");
        Edge(a1, aOld, p1, pCore1, "p1");
        Ref(aOld, kCore1, aOldUse, 10, 1);
        Ref(aOld, kCore1, aOldUse, 11, 4, ReferenceKind.TypeRef);
        _snapshots.MarkComplete(a1.Id, publishedAt: 1);
        Exec($"UPDATE snapshots SET status = '{SnapshotStatus.Superseded}' WHERE id = {a1.Id};");

        var a2 = Consumer(ConsumerA, "a2");
        var aApp = Project(a2, ConsumerA, "a_app", "src/A1/A1.csproj");
        var aLib = Project(a2, ConsumerA, "a_lib", "src/A2/A2.csproj");
        var aTool = Project(a2, ConsumerA, "a_tool", "src/A3/A3.csproj");
        Edge(a2, aApp, p2, pCore2, "p2");
        Edge(a2, aApp, p2, pCore2Net8, "p2");
        Edge(a2, aApp, q1, qCore, "q1");
        Edge(a2, aLib, p2, pUi2, "p2", dirty: true);
        Edge(a2, aTool, q1, qCore, "q1");
        var appUse = FileVersion(aApp, "src/A1/Use.cs");
        var appAlpha = FileVersion(aApp, "src/A1/Alpha.cs");
        var libBeta = FileVersion(aLib, "src/A2/Beta.cs");
        var toolGamma = FileVersion(aTool, "src/A3/Gamma.cs");
        var caller = Symbol(aApp, "M:A.App.Run");
        Ref(aApp, kCore2, appUse, 5, 3);
        Ref(aApp, kCore2, appUse, 5, 3);
        Call(aApp, kCore2, caller, appUse, 5, 3);
        Ref(aApp, kCore2, appUse, 5, 1);
        Ref(aApp, kCore2, appAlpha, 9, 2, ReferenceKind.Inheritance);
        Ref(aApp, kCore2Net8, appUse, 7, 1);
        Ref(aApp, k2Core2, appUse, 8, 6);
        Call(aApp, k2Core2, caller, appUse, 8, 6);
        Ref(aApp, kCore1, appUse, 20, 1);
        Ref(aApp, kUi2, appUse, 21, 1);
        Ref(aApp, kQ, appUse, 22, 1);
        Ref(aApp, otherKeyUi2, appAlpha, 23, 1);
        Ref(aLib, kUi2, libBeta, 1, 1, ReferenceKind.TypeRef);
        Ref(aLib, otherKeyUi2, libBeta, 2, 1);
        Ref(aTool, kCore2, toolGamma, 2, 2);
        Ref(aTool, kQ, toolGamma, 3, 2);
        _snapshots.MarkComplete(a2.Id, publishedAt: 2);
        Point(a2, "main", isDefault: true);
        Point(a2, "release", isDefault: false);
        Point(a1, "feature", isDefault: false);

        // Consumer B pins the older provider commit, dirty, and references the same-FQN other-key symbol.
        var b1 = Consumer(ConsumerB, "b1");
        var bApp = Project(b1, ConsumerB, "b_app", "src/B/B.csproj");
        var bUse = FileVersion(bApp, "src/B/Use.cs");
        Edge(b1, bApp, p1, pCore1, "p1", dirty: true);
        Edge(b1, bApp, p1, pUi1, "p1", dirty: true);
        Ref(bApp, kCore1, bUse, 3, 4);
        Ref(bApp, otherKeyUi1, bUse, 4, 4);
        _snapshots.MarkComplete(b1.Id, publishedAt: 1);
        Point(b1, "main", isDefault: true);

        // Consumer C: a still-PENDING snapshot at commit "shared" (never served).
        var c1 = Consumer(ConsumerC, "shared");
        var cApp = Project(c1, ConsumerC, "c_app", "src/C/C.csproj");
        var cUse = FileVersion(cApp, "src/C/Use.cs");
        Edge(c1, cApp, p2, pCore2, "p2");
        Ref(cApp, kCore2, cUse, 1, 1);

        // Consumer D: a complete snapshot at the same commit sha, pointed at by two branches.
        var d1 = Consumer(ConsumerD, "shared");
        var dApp = Project(d1, ConsumerD, "d_app", "src/D/D.csproj");
        var dLib = Project(d1, ConsumerD, "d_lib", "src/D/Lib/Lib.csproj");
        Edge(d1, dApp, p2, pCore2, "p2");
        Edge(d1, dLib, p2, pCore2Net8, "p2");
        var dUse = FileVersion(dApp, "src/D/Use.cs");
        var dLibUse = FileVersion(dLib, "src/D/Lib/Use.cs");
        Ref(dApp, kCore2, dUse, 4, 1);
        Ref(dApp, kCore2, dUse, 2, 9);
        Ref(dLib, kCore2Net8, dLibUse, 4, 1);
        Ref(dLib, kCore2, dLibUse, 6, 1);
        _snapshots.MarkComplete(d1.Id, publishedAt: 1);
        Point(d1, "main", isDefault: true);
        Point(d1, "dev", isDefault: false);
    }

    private Snapshot Provider(string url, string commitSha)
    {
        var repoId = _snapshots.EnsureProviderRepository(url, now: 1);
        _repos[url] = repoId;
        var commitId = _snapshots.EnsureCommit(repoId, commitSha, null, now: 1);
        var (id, _, _) = _snapshots.BeginPending(Identity(url, commitSha, provider: true), repoId, commitId, null, now: 1, isProvider: true);
        return new Snapshot(id, repoId);
    }

    private Snapshot Consumer(string url, string commitSha)
    {
        var repoId = _snapshots.EnsureRepository(url, now: 1);
        _repos[url] = repoId;
        var commitId = _snapshots.EnsureCommit(repoId, commitSha, $"tree_{commitSha}", now: 1);
        var (id, _, _) = _snapshots.BeginPending(Identity(url, commitSha, provider: false), repoId, commitId, null, now: 1);
        return new Snapshot(id, repoId);
    }

    private long Project(Snapshot snapshot, string url, string canonical, string relPath, string tfm = "net10.0")
    {
        var logicalId = _snapshots.EnsureLogicalProject(snapshot.RepoId, canonical, relPath, tfm, now: 1);
        var projectId = new ProjectStore(_conn).UpsertSnapshotProject(new ProjectIdentity
        {
            CanonicalId = canonical,
            GitRemoteUrl = url,
            RepoRelativePath = relPath,
            TargetFramework = tfm
        }, snapshot.Id, logicalId, lastIndexedAt: 1);
        _snapshots.MapProject(snapshot.Id, projectId);
        return projectId;
    }

    private void Point(Snapshot snapshot, string branch, bool isDefault) =>
        _snapshots.SetBranchPointer(_snapshots.EnsureBranch(snapshot.RepoId, branch, isDefault, now: 2), snapshot.Id, now: 3);

    private void Edge(Snapshot consumer, long consumerProjectId, Snapshot provider, long providerProjectId, string providerCommit, bool dirty = false) =>
        new SnapshotDependencyStore(_conn).Insert(new SnapshotDependencyEdge
        {
            ConsumerSnapshotId = consumer.Id,
            ConsumerProjectId = consumerProjectId,
            ProviderSnapshotId = provider.Id,
            ProviderProjectId = providerProjectId,
            ProviderRepositoryId = provider.RepoId,
            ProviderCommitSha = providerCommit,
            ReferenceKind = "submodule_ref",
            SubmoduleDirty = dirty,
            CreatedAt = 1
        });

    private long Symbol(long projectId, string key) =>
        Insert("""
            INSERT INTO symbols (project_id, symbol_key, fully_qualified_name, display_name, kind, accessibility, line_start, line_end, last_indexed_at)
            VALUES (@a, @b, 'global::Mix.ImageButton', 'ImageButton', 0, 0, 1, 1, 1) RETURNING id;
            """, projectId, key);

    private long FileVersion(long projectId, string path)
    {
        var fileId = Insert("INSERT INTO files (project_id, repo_relative_path) VALUES (@a, @b) RETURNING id;", projectId, path);
        return Insert("INSERT INTO file_versions (file_id, content_hash, last_indexed_at) VALUES (@a, CAST(@b AS BLOB), 1) RETURNING id;",
            fileId, $"hash_{projectId}_{path}");
    }

    private void Ref(long inProjectId, long targetSymbolId, long fileVersionId, int line, int col, ReferenceKind kind = ReferenceKind.Invocation) =>
        Occurrence(inProjectId, targetSymbolId, null, fileVersionId, line, col, kind);

    private void Call(long inProjectId, long targetSymbolId, long sourceSymbolId, long fileVersionId, int line, int col) =>
        Occurrence(inProjectId, targetSymbolId, sourceSymbolId, fileVersionId, line, col, ReferenceKind.Invocation);

    private void Occurrence(long inProjectId, long targetSymbolId, long? sourceSymbolId, long fileVersionId, int line, int col, ReferenceKind kind)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO occurrences (in_project_id, target_symbol_id, source_symbol_id, file_version_id, line, col, kind, flags)
            VALUES (@p, @t, @s, @fv, @line, @col, @kind, 0);
            """;
        cmd.Parameters.AddWithValue("@p", inProjectId);
        cmd.Parameters.AddWithValue("@t", targetSymbolId);
        cmd.Parameters.AddWithValue("@s", (object?)sourceSymbolId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@fv", fileVersionId);
        cmd.Parameters.AddWithValue("@line", line);
        cmd.Parameters.AddWithValue("@col", col);
        cmd.Parameters.AddWithValue("@kind", (int)kind);
        cmd.ExecuteNonQuery();
    }

    private long Insert(string sql, object a, object b)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@a", a);
        cmd.Parameters.AddWithValue("@b", b);
        return (long)cmd.ExecuteScalar()!;
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static SnapshotIdentity Identity(string url, string commitSha, bool provider) => new()
    {
        RepositoryRemoteUrl = url,
        CommitSha = commitSha,
        TreeSha = provider ? null : $"tree_{commitSha}",
        SchemaVersion = IndexDatabase.SnapshotSchemaVersion,
        AnalyzerVersion = IndexConfigurationHash.AnalyzerVersion,
        ConfigHash = "cfg",
        ToolchainFingerprint = "tc",
        IsOverlay = false
    };
}
