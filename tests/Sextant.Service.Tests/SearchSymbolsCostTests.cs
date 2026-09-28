using System.Text;
using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Service.CallerIdentity;
using Sextant.Service.Grants;
using Sextant.Service.Search;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #196: what one <c>search_symbols</c> call may cost. Each snapshot page is an index seek over the prefix's
/// NOCASE key range (migration 025), the call returns at most <see cref="ServiceOptions.SearchMaxHits"/> symbols in all,
/// the caller's repositories are found by one indexed query over its own keys, and the request's cancellation stops
/// the call. These tests drive <see cref="SnapshotService.SearchSymbols"/> directly; the HTTP boundary and the
/// security invariants are <see cref="SearchSymbolsHttpTests"/>.
/// </summary>
[TestClass]
public class SearchSymbolsCostTests
{
    private const string Widgets = "https://github.com/acme/widgets";
    private const string Gadgets = "https://github.com/acme/gadgets";
    private const string Gizmos = "https://github.com/acme/gizmos";

    private static readonly CallerPrincipal Caller = GrantServiceTests.User("tenant-a", "user-1");

    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SnapshotService? _service;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = ServiceTestFixtures.NewDbPath();
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
    }

    [TestCleanup]
    public void TestCleanup()
    {
        _service?.Dispose();
        SqliteTestDatabase.Delete(_dbPath, _db);
    }

    // ==== the migration re-indexes nothing ==========================================================

    [TestMethod]
    public void EnsureIdentity_FoldsTheSnapshotSchema_WhichMigration025DoesNotAdvance()
    {
        var identity = ServiceTestFixtures.Request().ToIdentity();

        Assert.AreEqual(IndexDatabase.SnapshotSchemaVersion, identity.SchemaVersion);
        Assert.IsTrue(identity.SchemaVersion < IndexDatabase.LatestSchemaVersion, "025 is identity-neutral");
    }

    // ==== query plans ===============================================================================

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public void PageQuery_SeeksThePrefixRange_WithoutAScanOrASort(bool unboundedRange, bool analyzed)
    {
        if (analyzed)
            AnalyzeASeededCatalog();

        var plan = Plan(SnapshotService.SearchPageSql,
            ("@snapshot", 1L), ("@project", 0L), ("@from", "get"), ("@after", 0L), ("@lo", "get"),
            ("@hi", unboundedRange ? Array.Empty<byte>() : "geu"), ("@pattern", "get%"), ("@take", 51));

        AssertNoScanOrSort(plan);
        AssertHasStep(plan, "SEARCH sp USING ", "(snapshot_id=? AND project_id>?)");
        AssertHasStep(plan, "SEARCH s USING INDEX ix_symbols_project_name_nocase (project_id=? AND display_name>? AND display_name<?)");
        AssertHasStep(plan, "SEARCH p USING INTEGER PRIMARY KEY (rowid=?)");
        AssertHasStep(plan, "SEARCH lp USING INTEGER PRIMARY KEY (rowid=?)");
    }

    [TestMethod]
    public void ResumeQuery_IsAPointLookup()
    {
        var plan = Plan(SnapshotService.SearchResumeSql, ("@snapshot", 1L), ("@after", 7L), ("@lo", "get"), ("@hi", "geu"));

        AssertNoScanOrSort(plan);
        AssertHasStep(plan, "SEARCH s USING INTEGER PRIMARY KEY (rowid=?)");
        AssertHasStep(plan, "SEARCH sp USING ", "(snapshot_id=? AND project_id=?)");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RepositoryQuery_SeeksEachRange_WithoutReadingTheCatalog(bool analyzed)
    {
        if (analyzed)
            AnalyzeASeededCatalog();

        var plan = Plan(SnapshotService.SearchRepositoriesSql,
            ("@ranges", """[["https://github.com/acme/widgets","https://github.com/acme/widgett"]]"""));

        Assert.IsFalse(plan.Any(p => p.StartsWith("SCAN r", StringComparison.Ordinal)), string.Join("\n", plan));
        Assert.IsFalse(plan.Any(p => p.Contains("TEMP B-TREE", StringComparison.Ordinal)), string.Join("\n", plan));
        AssertHasStep(plan, "SEARCH r USING INDEX ix_repositories_remote_url_nocase (remote_url>? AND remote_url<?)");
    }

    // ==== the page query returns exactly the prefix's matches ======================================

    [TestMethod]
    [DataRow("get")]
    [DataRow("GET")]
    [DataRow("g")]
    [DataRow("Ge")]
    [DataRow("GetA")]
    [DataRow("Get_")]
    [DataRow("Get%")]
    [DataRow("Get\\")]
    [DataRow("Get@")]
    [DataRow("Get[")]
    [DataRow("get`")]
    [DataRow("GetZ")]
    [DataRow("Getä")]
    [DataRow("GetÄ")]
    [DataRow("\U0001D53Eet")]
    [DataRow("\U0010FFFF")]
    [DataRow("Zzz")]
    public void Walk_ReturnsExactlyThePrefixMatches_AtEveryLimit(string prefix)
    {
        // Several projects per snapshot, names equal but for ASCII case, names repeated, LIKE wildcards and the escape,
        // the characters around the ASCII letters, non-ASCII letters (which neither NOCASE nor LIKE fold), a character
        // outside the BMP and the last scalar value: the walk returns each match once, whatever the page size.
        string[] first =
        [
            "Get", "get", "GET", "GetA", "getB", "Get_x", "GetXx", "Get%y", "Get\\z", "Getä", "GetÄ", "Gez", "Geu", "Gdz",
            "Ge", "G", "Get@", "Get[", "GetZ", "Get{", "get`", "Getter", "\U0001D53Eet", "\U0001D53Eetter"
        ];
        string[] second = ["GETA", "geta", "GetA", "Get", "Get", "Get", "Gettable", "Get\u00A0", "Zz", "\U0010FFFFx", "\U0010FFFF"];
        var service = Start();
        var hashes = new[]
        {
            Seed(Widgets, "commit-w", Names(first), [], Names(second)),
            Seed(Gadgets, "commit-g", Names(second), Names(first))
        };
        Grant(service, Widgets);
        Grant(service, Gadgets);
        var expected = hashes
            .SelectMany(hash => first.Concat(second).Where(name => StartsWithIgnoringAsciiCase(name, prefix)).Select(name => (hash, name)))
            .OrderBy(e => e.hash, StringComparer.Ordinal).ThenBy(e => e.name, StringComparer.Ordinal)
            .ToList();

        foreach (var limit in new[] { 1, 2, 3, 7, SymbolSearchQuery.MaxLimit })
        {
            var hits = Walk(service, prefix, limit).SelectMany(p => p.Symbols).ToList();
            Assert.AreEqual(hits.Count, hits.Select(h => (h.IdentityHash, h.SymbolKey)).Distinct().Count(),
                $"limit {limit}: no symbol is returned twice");
            CollectionAssert.AreEqual(expected,
                hits.Select(h => (h.IdentityHash, h.Name))
                    .OrderBy(e => e.IdentityHash, StringComparer.Ordinal).ThenBy(e => e.Name, StringComparer.Ordinal)
                    .ToList(),
                $"limit {limit}: every match and nothing else");
        }
    }

    [TestMethod]
    public void EachSnapshot_IsPagedInIndexOrder_AcrossItsProjects()
    {
        var service = Start();
        Seed(Widgets, "commit-w", Names("TypeB", "typeA", "TypeC"), [], Names("TypeA", "Typea"));
        Grant(service, Widgets);

        var pages = Walk(service, "type", limit: 2);

        // (project, NOCASE name, id): the first project's three, then the second project's two, two per page. Each page
        // is then merged in its documented (name, identity hash, id) order.
        var names = pages.Select(p => string.Join(",", p.Symbols.Select(s => s.Name))).ToList();
        CollectionAssert.AreEqual(new[] { "TypeB,typeA", "TypeA,TypeC", "Typea" }, names, string.Join(" | ", names));
    }

    // ==== the per-call total-hit cap ================================================================

    [TestMethod]
    [DataRow(10, 100)]
    [DataRow(100, 100)]
    [DataRow(150, 150)]
    [DataRow(100_000, 180)]
    public void TotalHits_AreCappedPerCall_AndSharedByTheSnapshots(int maxHits, int expectedFirstPage)
    {
        // Three snapshots of 60 matches read with the largest limit: without the cap one page would carry all 180. The
        // cap is clamped to 100..5000, so every snapshot a page reads still gets its share.
        var service = Start(o => o with { SearchMaxHits = maxHits });
        var hashes = new[] { Widgets, Gadgets, Gizmos }
            .Select(r => Seed(r, "commit-" + r[^4..], Names(Enumerable.Range(0, 60).Select(i => $"Match{i:D2}").ToArray())))
            .ToList();
        foreach (var repository in new[] { Widgets, Gadgets, Gizmos })
            Grant(service, repository);

        var pages = Walk(service, "Match", SymbolSearchQuery.MaxLimit);

        Assert.AreEqual(expectedFirstPage, pages[0].Symbols.Count);
        var share = Math.Clamp(maxHits, ServiceOptions.SearchMaxHitsFloor, ServiceOptions.SearchMaxHitsCeiling) / 3;
        foreach (var hash in hashes)
            Assert.IsTrue(pages[0].Symbols.Count(s => s.IdentityHash == hash) >= Math.Min(share, 60), "each snapshot gets its share");
        Assert.IsTrue(pages.All(p => p.Symbols.Count <= Math.Max(expectedFirstPage, 100)), "no page is over the cap");
        Assert.IsTrue(pages.All(p => p.Truncated.Count == 0), "a capped snapshot was still searched: it is not truncated");
        var hits = pages.SelectMany(p => p.Symbols).ToList();
        Assert.AreEqual(180, hits.Count, "every match is returned");
        Assert.AreEqual(180, hits.Select(h => (h.IdentityHash, h.SymbolKey)).Distinct().Count(), "no match is returned twice");
    }

    [TestMethod]
    public void DefaultCap_BoundsAPageBelowWidthTimesLimit()
    {
        var service = Start();
        foreach (var i in Enumerable.Range(0, 5))
        {
            var repository = $"https://github.com/acme/repo{i}";
            Seed(repository, $"commit-{i}", Names(Enumerable.Range(0, 200).Select(n => $"Match{n:D3}").ToArray()));
            Grant(service, repository);
        }

        var page = Search(service, "Match", SymbolSearchQuery.MaxLimit);

        Assert.AreEqual(ServiceOptions.DefaultSearchMaxHits, page.Symbols.Count, "5 snapshots x 200 is capped at the default 500");
        Assert.IsTrue(page.Symbols.GroupBy(s => s.IdentityHash).All(g => g.Count() == 100), "shared evenly");
        Assert.IsNotNull(page.Next);
    }

    [TestMethod]
    public void KindFilter_ExaminesABoundedNumberOfRows_AndStillFindsEveryMatch()
    {
        // A rare kind under a broad prefix: each page examines at most its allowance of rows, so a page can come back
        // empty with a cursor that resumes after the last row it examined, and the walk still finds every match once.
        var service = Start();
        service.SearchKindExamineRowsPerCall = 4;
        var delegates = new HashSet<int> { 3, 11, 17 };
        var hash = Seed(Widgets, "commit-w", Enumerable.Range(0, 20)
            .Select(i => ($"Get{i:D2}", delegates.Contains(i) ? SymbolKind.Delegate : SymbolKind.Method))
            .ToArray());
        Grant(service, Widgets);
        var examined = 0;
        service.SearchRowReadHook = (_, _) => examined++;

        var pages = new List<SymbolSearchOutcome>();
        var perPage = new List<int>();
        foreach (var page in Pages(service, "Get", limit: 1, SymbolKind.Delegate))
        {
            pages.Add(page);
            perPage.Add(examined);
            examined = 0;
        }

        CollectionAssert.AreEqual(new[] { "Get03", "Get11", "Get17" }, pages.SelectMany(p => p.Symbols.Select(s => s.Name)).ToList());
        Assert.IsTrue(perPage.All(n => n <= 4), $"rows examined per page: {string.Join(", ", perPage)}");
        Assert.IsTrue(pages.Exists(p => p.Symbols.Count == 0 && p.Next is not null), "a page that ran out of rows to examine resumes");
        Assert.IsTrue(pages.SelectMany(p => p.Symbols).All(s => s.IdentityHash == hash && s.Kind == "delegate"));
    }

    [TestMethod]
    public void KindFilter_BoundsTheRowsExaminedPerCall_AcrossEverySnapshotItReads()
    {
        // Three snapshots with a rare kind and the largest limit: the hit budget barely shrinks, so a per-snapshot
        // allowance of `share + 1` would let each snapshot examine all 40 rows. The examine budget is shared instead.
        var service = Start();
        service.SearchKindExamineRowsPerCall = 30;
        foreach (var repository in new[] { Widgets, Gadgets, Gizmos })
        {
            Seed(repository, "commit-" + repository[^4..], Enumerable.Range(0, 40)
                .Select(i => ($"Get{i:D2}", i == 37 ? SymbolKind.Delegate : SymbolKind.Method))
                .ToArray());
            Grant(service, repository);
        }
        var examined = 0;
        service.SearchRowReadHook = (_, _) => examined++;

        var pages = new List<SymbolSearchOutcome>();
        var perPage = new List<int>();
        foreach (var page in Pages(service, "Get", SymbolSearchQuery.MaxLimit, SymbolKind.Delegate))
        {
            pages.Add(page);
            perPage.Add(examined);
            examined = 0;
        }

        Assert.IsTrue(perPage.All(n => n <= 30), $"rows examined per page: {string.Join(", ", perPage)}");
        var hits = pages.SelectMany(p => p.Symbols).ToList();
        Assert.AreEqual(3, hits.Count, "every match is found");
        Assert.AreEqual(3, hits.Select(h => h.IdentityHash).Distinct().Count(), "once per snapshot");
        Assert.IsTrue(hits.All(h => h.Name == "Get37" && h.Kind == "delegate"));
    }

    // ==== the caller's repositories =================================================================

    [TestMethod]
    [DataRow("https://GitHub.com/Acme/Widgets.git")]
    [DataRow("https://github.com:443/acme/widgets")]
    [DataRow("HTTPS://GITHUB.COM:443/ACME/WIDGETS")]
    [DataRow("https://github.com/acme/widgets/")]
    [DataRow("https://github.com/acme/widgets.git ")]
    public void CatalogSpellingOfAGrantedRepository_IsFound(string catalogSpelling)
    {
        var service = Start();
        var hash = Seed(catalogSpelling, "commit-w", Names("TypeA"));
        Grant(service, Widgets);

        var page = Search(service, "Type");

        Assert.AreEqual(hash, page.Symbols.Single().IdentityHash);
        Assert.AreEqual(Widgets, page.Symbols.Single().Repository, "the grant's spelling labels the result");
        Assert.AreEqual(0, page.Pending.Count);
    }

    [TestMethod]
    public void RepositoriesSharingTheKeysPrefix_AreNotTheGrantedOne()
    {
        var service = Start();
        Seed(Widgets + "-extra", "commit-x", Names("TypeX"));
        Seed(Widgets + "x", "commit-y", Names("TypeY"));
        Seed("https://github.com/acme/widget", "commit-z", Names("TypeZ"));
        Grant(service, Widgets);

        var page = Search(service, "Type");

        Assert.AreEqual(0, page.Symbols.Count);
        CollectionAssert.AreEqual(new[] { Widgets }, page.Pending.Select(p => p.Repository).ToList(),
            "the granted repository is not in the catalog yet");
    }

    [TestMethod]
    public void TheLowestIdCatalogRowOfAKey_IsSearched()
    {
        // Two catalog rows with the same grant key (the catalog dedups by RemoteUrlIdentity, which keeps an explicit
        // default port): the lowest id is the repository, as for every other grant lookup.
        var service = Start();
        var first = Seed(Widgets, "commit-a", Names("TypeA"));
        Seed("https://github.com:443/acme/widgets", "commit-b", Names("TypeB"));
        Grant(service, Widgets);

        var page = Search(service, "Type");

        Assert.AreEqual(first, page.Symbols.Single().IdentityHash);
    }

    [TestMethod]
    public void ProviderRepository_IsNotASearchTarget()
    {
        var service = Start();
        ServiceTestFixtures.PublishComplete(_db, ServiceTestFixtures.Request(repo: Widgets, commit: "commit-p"), isProvider: true);
        Grant(service, Widgets);

        var page = Search(service, "Type");

        Assert.AreEqual(0, page.Symbols.Count);
        Assert.AreEqual(1, page.Pending.Count);
    }

    // ==== cancellation ==============================================================================

    [TestMethod]
    public void CancelledBeforeTheCall_ReadsNothing()
    {
        var service = StartWithThreeSnapshots();
        var reads = new List<string>();
        service.SearchSnapshotReadHook = reads.Add;
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() => Search(service, "Type", cancellationToken: cancelled.Token));

        Assert.AreEqual(0, reads.Count);
    }

    [TestMethod]
    public void CancelledBetweenSnapshots_ReadsNoFurtherSnapshot()
    {
        var service = StartWithThreeSnapshots();
        using var cancel = new CancellationTokenSource();
        var reads = new List<string>();
        service.SearchSnapshotReadHook = hash =>
        {
            reads.Add(hash);
            cancel.Cancel();
        };

        Assert.ThrowsExactly<OperationCanceledException>(() => Search(service, "Type", cancellationToken: cancel.Token));

        Assert.AreEqual(1, reads.Count, "the call stopped before the next snapshot");
        AssertTheServiceStillSearches(service);
    }

    [TestMethod]
    public void CancelledWhileAStatementRuns_InterruptsIt()
    {
        var service = StartWithThreeSnapshots();
        using var cancel = new CancellationTokenSource();
        var rows = 0;
        service.SearchRowReadHook = (_, _) =>
        {
            rows++;
            cancel.Cancel();
        };

        var ex = Assert.ThrowsExactly<OperationCanceledException>(() => Search(service, "Type", cancellationToken: cancel.Token));

        // The row loop does not check the token: only SQLite's interrupt can have stopped the statement.
        Assert.AreEqual(1, rows, "the statement stopped at its next row");
        Assert.IsInstanceOfType<SqliteException>(ex.InnerException);
        Assert.AreEqual(9 /* SQLITE_INTERRUPT */, ((SqliteException)ex.InnerException!).SqliteErrorCode);
        Assert.AreEqual(cancel.Token, ex.CancellationToken);
        AssertTheServiceStillSearches(service);
    }

    [TestMethod]
    public void ACancellationAfterTheCall_DoesNotReachTheNextOne()
    {
        var service = StartWithThreeSnapshots();
        using var cancel = new CancellationTokenSource();

        var page = Search(service, "Type", cancellationToken: cancel.Token);
        cancel.Cancel();

        Assert.AreEqual(9, page.Symbols.Count);
        AssertTheServiceStillSearches(service);
    }

    // ==== helpers ===================================================================================

    private SnapshotService Start(Func<ServiceOptions, ServiceOptions>? configure = null)
    {
        var options = ServiceTestFixtures.NewOptions(_dbPath);
        if (configure is not null)
            options = configure(options);
        return _service = SnapshotService.Start(options, new FakeSnapshotWorker(_db), _db);
    }

    // Widgets, Gadgets and Gizmos, each with three matches of "Type", all granted.
    private SnapshotService StartWithThreeSnapshots()
    {
        var service = Start();
        foreach (var repository in new[] { Widgets, Gadgets, Gizmos })
            Seed(repository, "commit-" + repository[^4..], Names("TypeA", "TypeB", "TypeC"));
        foreach (var repository in new[] { Widgets, Gadgets, Gizmos })
            Grant(service, repository);
        return service;
    }

    // After an interrupted call the pooled read connection is back outside any transaction: a later call starts its
    // own read transaction, sees a grant made since, and returns every match.
    private void AssertTheServiceStillSearches(SnapshotService service)
    {
        service.SearchSnapshotReadHook = null;
        service.SearchRowReadHook = null;
        const string late = "https://github.com/acme/late";
        Seed(late, "commit-late", Names("TypeL"));
        Grant(service, late);
        for (var i = 0; i < 3; i++)
            Assert.AreEqual(10, Search(service, "Type").Symbols.Count, "every match, including the new grant's");
        RevokeLate(service, late);
    }

    private static void RevokeLate(SnapshotService service, string url) =>
        service.DeleteGrantsAsync(Caller, GrantScope.Self, RepositoryGrantKey.Of(url), "").GetAwaiter().GetResult();

    private static void Grant(SnapshotService service, string url) =>
        service.PutGrantAsync(Caller, GrantScope.Self, url, RepositoryGrantKey.Of(url), "").GetAwaiter().GetResult();

    private static SymbolSearchOutcome Search(
        SnapshotService service, string prefix, int limit = SymbolSearchQuery.DefaultLimit, SymbolKind? kind = null,
        SymbolSearchCursorState? resume = null, CancellationToken cancellationToken = default) =>
        service.SearchSymbols(Caller, new SymbolSearchQuery { NamePrefix = prefix, Limit = limit, Kind = kind }, resume, cancellationToken);

    private static List<SymbolSearchOutcome> Walk(SnapshotService service, string prefix, int limit, SymbolKind? kind = null) =>
        Pages(service, prefix, limit, kind).ToList();

    // Every page from the start, each resumed from the previous page's cursor as the tool would: encoded, then decoded.
    private static IEnumerable<SymbolSearchOutcome> Pages(SnapshotService service, string prefix, int limit, SymbolKind? kind = null)
    {
        var binding = SymbolSearchCursor.Binding(Caller, prefix, kind?.ToString().ToLowerInvariant(), null, null);
        SymbolSearchCursorState? resume = null;
        for (var i = 0; i < 1000; i++)
        {
            var page = Search(service, prefix, limit, kind, resume);
            Assert.IsFalse(page.NoTargets);
            yield return page;
            if (page.Next is null)
                yield break;
            var cursor = SymbolSearchCursor.Encode(page.Next, binding);
            Assert.IsTrue(SymbolSearchCursor.TryDecode(cursor, binding, ServiceOptions.SearchMaxWidthCeiling, out resume));
        }
        Assert.Fail("the search did not terminate");
    }

    private static (string Name, SymbolKind Kind)[] Names(params string[] names) =>
        names.Select(n => (n, SymbolKind.Class)).ToArray();

    // The tool's matching rule: a literal prefix, case-insensitive for the ASCII letters only.
    private static bool StartsWithIgnoringAsciiCase(string name, string prefix) =>
        FoldAscii(name).StartsWith(FoldAscii(prefix), StringComparison.Ordinal);

    private static string FoldAscii(string text) =>
        string.Create(text.Length, text, (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
                span[i] = source[i] is >= 'A' and <= 'Z' ? (char)(source[i] + ('a' - 'A')) : source[i];
        });

    // Publishes `repository` at `commit` as the complete head of its default branch `main`, with one project per entry of
    // `projects` holding its symbols (after the fixture's own empty project). Returns the snapshot's identity hash.
    private string Seed(string repository, string commit, params (string Name, SymbolKind Kind)[][] projects)
    {
        var request = ServiceTestFixtures.Request(repo: repository, commit: commit);
        var snapshotId = ServiceTestFixtures.PublishComplete(_db, request, symbolCount: 0);
        var conn = _db.GetConnection();
        var snapshots = new SnapshotStore(conn);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var repositoryId = snapshots.GetById(snapshotId)!.RepositoryId;
        snapshots.SetBranchPointer(snapshots.EnsureBranch(repositoryId, "main", true, now), snapshotId, now);

        Exec(conn, "BEGIN IMMEDIATE;");
        for (var p = 0; p < projects.Length; p++)
        {
            var projectId = Insert(conn, """
                INSERT INTO projects (canonical_id, git_remote_url, repo_relative_path, last_indexed_at, snapshot_id)
                VALUES (@c, @g, @p, 0, @snap) RETURNING id;
                """, ("@c", $"{repository}@{commit}_p{p}"), ("@g", repository), ("@p", $"src/P{p}/P{p}.csproj"), ("@snap", snapshotId));
            snapshots.MapProject(snapshotId, projectId);
            var fileId = Insert(conn, "INSERT INTO files (project_id, repo_relative_path) VALUES (@p, 'src/All.cs') RETURNING id;",
                ("@p", projectId));
            var fileVersionId = Insert(conn,
                "INSERT INTO file_versions (file_id, content_hash, last_indexed_at) VALUES (@f, @h, 0) RETURNING id;",
                ("@f", fileId), ("@h", Encoding.UTF8.GetBytes($"{commit}:{p}")));

            using var symbol = conn.CreateCommand();
            symbol.CommandText = """
                INSERT INTO symbols
                    (project_id, symbol_key, fully_qualified_name, display_name, kind, accessibility,
                     file_version_id, line_start, line_end, last_indexed_at)
                VALUES (@p, @key, @fqn, @name, @kind, 0, @fv, 1, 10, 0);
                """;
            symbol.Parameters.AddWithValue("@p", projectId);
            symbol.Parameters.AddWithValue("@fv", fileVersionId);
            var key = symbol.Parameters.Add("@key", SqliteType.Text);
            var fqn = symbol.Parameters.Add("@fqn", SqliteType.Text);
            var name = symbol.Parameters.Add("@name", SqliteType.Text);
            var kind = symbol.Parameters.Add("@kind", SqliteType.Integer);
            for (var i = 0; i < projects[p].Length; i++)
            {
                key.Value = $"k:{projectId}:{i}";
                fqn.Value = $"global::P{p}.{projects[p][i].Name}";
                name.Value = projects[p][i].Name;
                kind.Value = (int)projects[p][i].Kind;
                symbol.ExecuteNonQuery();
            }
        }
        Exec(conn, "COMMIT;");
        return request.ToIdentity().Hash;
    }

    private void AnalyzeASeededCatalog()
    {
        for (var i = 0; i < 20; i++)
            Seed($"https://github.com/acme/repo{i:D2}", $"commit-{i}",
                Names(Enumerable.Range(0, 50).Select(n => $"{(n % 2 == 0 ? "Get" : "Set")}Thing{n}").ToArray()),
                Names("Other"));
        Exec(_db.GetConnection(), "ANALYZE;");
    }

    // The EXPLAIN QUERY PLAN steps of `sql`, bound as the service binds it, on a connection of their own.
    private List<string> Plan(string sql, params (string Name, object Value)[] parameters)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        var steps = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            steps.Add(reader.GetString(3));
        return steps;
    }

    private static void AssertNoScanOrSort(List<string> plan)
    {
        var text = string.Join("\n", plan);
        Assert.IsFalse(plan.Any(p => p.StartsWith("SCAN", StringComparison.Ordinal)), "no table is scanned:\n" + text);
        Assert.IsFalse(plan.Any(p => p.Contains("TEMP B-TREE", StringComparison.Ordinal)), "nothing is sorted:\n" + text);
    }

    private static void AssertHasStep(List<string> plan, string start, string? end = null) =>
        Assert.IsTrue(plan.Any(p => p.StartsWith(start, StringComparison.Ordinal) && (end is null || p.EndsWith(end, StringComparison.Ordinal))),
            $"expected a step `{start}…{end}` in:\n{string.Join("\n", plan)}");

    private static long Insert(SqliteConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        return (long)cmd.ExecuteScalar()!;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
