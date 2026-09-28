using System.Diagnostics;
using Sextant.Core;
using Sextant.Service.Grants;
using Sextant.Service.Search;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #196: measures the per-call cost of <c>search_symbols</c> over large snapshots. A prefix that matches nothing
/// should cost O(log n) per snapshot rather than a scan of the whole snapshot. The numbers are written to the test
/// output. The test is gated behind <c>SEXTANT_RUN_PERF=1</c> and <c>[TestCategory("Performance")]</c> (the same
/// pattern as the other wall-clock tests), so seeding half a million symbols never slows or destabilizes the default
/// suite. It asserts only a loose ceiling, as a guard against a plan that scans again.
/// </summary>
[TestClass]
[TestCategory("Performance")]
public class SearchSymbolsPerformanceTests
{
    private const int LargeSymbols = 500_000;
    private const int LargeProjects = 25;
    private const int WideSnapshots = 20;
    private const int WideSymbols = 25_000;
    private const int Iterations = 15;

    private static readonly string[] Verbs =
    [
        "Get", "Set", "Create", "Handle", "On", "Is", "To", "Try", "Parse", "Build",
        "Load", "Save", "Find", "Read", "Write", "Update", "Delete", "Apply", "Resolve", "Validate"
    ];

    private static readonly string[] Nouns =
    [
        "Widget", "Gadget", "Order", "Customer", "Invoice", "Payment", "Session", "Token", "Request", "Response",
        "Snapshot", "Branch", "Commit", "Project", "Symbol", "Reference", "Grant", "Tenant", "Cursor", "Page",
        "Index", "Query", "Plan", "Result", "Error", "Option", "Setting", "Profile", "Account", "Address",
        "Message", "Channel", "Stream", "Buffer", "Handler", "Factory", "Builder", "Visitor", "Walker", "Reader",
        "Writer", "Parser", "Lexer", "Token", "Scope", "Binding", "Model", "View", "Controller", "Service"
    ];

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task PrefixSearch_CostIsBoundedByTheMatches_NotTheSnapshotSize()
    {
        if (Environment.GetEnvironmentVariable("SEXTANT_RUN_PERF") != "1")
            Assert.Inconclusive("Performance test skipped (set SEXTANT_RUN_PERF=1 to run).");

        var dbPath = ServiceTestFixtures.NewDbPath();
        var db = new IndexDatabase(dbPath);
        try
        {
            db.RunMigrations();
            var seed = Stopwatch.StartNew();
            Seed(db, "https://github.com/acme/large", "commit-large", LargeProjects, LargeSymbols / LargeProjects);
            for (var i = 0; i < WideSnapshots; i++)
                Seed(db, $"https://github.com/acme/wide{i:D2}", $"commit-wide{i}", 5, WideSymbols / 5);
            TestContext.WriteLine($"seeded {LargeSymbols + WideSnapshots * WideSymbols:N0} symbols in {seed.Elapsed.TotalSeconds:F1} s");

            var options = ServiceTestFixtures.NewOptions(dbPath) with { SearchMaxWidth = 50 };
            using var service = SnapshotService.Start(options, new FakeSnapshotWorker(db), db);
            var large = GrantServiceTests.User("tenant-a", "user-large");
            var wide = GrantServiceTests.User("tenant-a", "user-wide");
            await Grant(service, large, "https://github.com/acme/large");
            for (var i = 0; i < WideSnapshots; i++)
                await Grant(service, wide, $"https://github.com/acme/wide{i:D2}");

            var results = new List<(string Scenario, double Median, double P95, int Hits)>
            {
                Measure(service, large, "1x500k no-match 'Zqx'", Query("Zqx")),
                Measure(service, large, "1x500k narrow 'GetWidget12'", Query("GetWidget12")),
                Measure(service, large, "1x500k broad 'Get' limit 50", Query("Get")),
                Measure(service, large, "1x500k broad 'Get' limit 200", Query("Get", limit: 200)),
                Measure(service, large, "1x500k 'Get' kind=delegate (rare)", Query("Get", SymbolKind.Delegate)),
                Measure(service, wide, "20x25k no-match 'Zqx'", Query("Zqx")),
                Measure(service, wide, "20x25k broad 'Get' limit 200", Query("Get", limit: 200))
            };
            foreach (var (scenario, median, p95, hits) in results)
                TestContext.WriteLine($"{scenario,-40} median {median,8:F2} ms   p95 {p95,8:F2} ms   hits {hits}");

            var noMatch = results[0];
            Assert.IsTrue(noMatch.Median < 10, $"a no-match prefix over 500k symbols took {noMatch.Median:F2} ms (median)");
        }
        finally
        {
            SqliteTestDatabase.Delete(dbPath, db);
        }
    }

    private static SymbolSearchQuery Query(string prefix, SymbolKind? kind = null, int limit = SymbolSearchQuery.DefaultLimit) =>
        new() { NamePrefix = prefix, Kind = kind, Limit = limit };

    private (string, double, double, int) Measure(
        SnapshotService service, Sextant.Service.CallerIdentity.CallerPrincipal caller, string scenario, SymbolSearchQuery query)
    {
        var hits = 0;
        for (var i = 0; i < 3; i++)
            hits = service.SearchSymbols(caller, query, null).Symbols.Count;
        var samples = new List<double>();
        for (var i = 0; i < Iterations; i++)
        {
            var watch = Stopwatch.StartNew();
            service.SearchSymbols(caller, query, null);
            samples.Add(watch.Elapsed.TotalMilliseconds);
        }
        samples.Sort();
        return (scenario, samples[samples.Count / 2], samples[(int)Math.Ceiling(samples.Count * 0.95) - 1], hits);
    }

    private static Task Grant(SnapshotService service, Sextant.Service.CallerIdentity.CallerPrincipal caller, string url) =>
        service.PutGrantAsync(caller, GrantScope.Self, url, RepositoryGrantKey.Of(url), "");

    // One complete default-branch snapshot of `projects` projects with `perProject` symbols each, written in one
    // transaction with a reused insert command. About 1 in 1000 symbols is a delegate.
    private static void Seed(IndexDatabase db, string repository, string commit, int projects, int perProject)
    {
        var snapshotId = ServiceTestFixtures.PublishComplete(db, ServiceTestFixtures.Request(repo: repository, commit: commit), symbolCount: 0);
        var conn = db.GetConnection();
        var snapshots = new SnapshotStore(conn);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        snapshots.SetBranchPointer(snapshots.EnsureBranch(snapshots.GetRepositoryId(repository)!.Value, "main", true, now), snapshotId, now);

        Exec(conn, "BEGIN IMMEDIATE;");
        using var project = conn.CreateCommand();
        project.CommandText = """
            INSERT INTO projects (canonical_id, git_remote_url, repo_relative_path, last_indexed_at, snapshot_id)
            VALUES (@c, @g, @p, 0, @snap) RETURNING id;
            """;
        var canonical = project.Parameters.Add("@c", Microsoft.Data.Sqlite.SqliteType.Text);
        project.Parameters.AddWithValue("@g", repository);
        var path = project.Parameters.Add("@p", Microsoft.Data.Sqlite.SqliteType.Text);
        project.Parameters.AddWithValue("@snap", snapshotId);

        using var file = conn.CreateCommand();
        file.CommandText = """
            INSERT INTO files (project_id, repo_relative_path) VALUES (@p, 'src/All.cs') RETURNING id;
            """;
        var fileProject = file.Parameters.Add("@p", Microsoft.Data.Sqlite.SqliteType.Integer);

        using var version = conn.CreateCommand();
        version.CommandText = "INSERT INTO file_versions (file_id, content_hash, last_indexed_at) VALUES (@f, @h, 0) RETURNING id;";
        var versionFile = version.Parameters.Add("@f", Microsoft.Data.Sqlite.SqliteType.Integer);
        var versionHash = version.Parameters.Add("@h", Microsoft.Data.Sqlite.SqliteType.Blob);

        using var symbol = conn.CreateCommand();
        symbol.CommandText = """
            INSERT INTO symbols
                (project_id, symbol_key, fully_qualified_name, display_name, kind, accessibility,
                 file_version_id, line_start, line_end, last_indexed_at)
            VALUES (@p, @key, @fqn, @name, @kind, 0, @fv, 1, 10, 0);
            """;
        var symbolProject = symbol.Parameters.Add("@p", Microsoft.Data.Sqlite.SqliteType.Integer);
        var key = symbol.Parameters.Add("@key", Microsoft.Data.Sqlite.SqliteType.Text);
        var fqn = symbol.Parameters.Add("@fqn", Microsoft.Data.Sqlite.SqliteType.Text);
        var name = symbol.Parameters.Add("@name", Microsoft.Data.Sqlite.SqliteType.Text);
        var kind = symbol.Parameters.Add("@kind", Microsoft.Data.Sqlite.SqliteType.Integer);
        var fileVersion = symbol.Parameters.Add("@fv", Microsoft.Data.Sqlite.SqliteType.Integer);

        var random = new Random(StableSeed($"{repository}|{projects}|{perProject}"));
        for (var p = 0; p < projects; p++)
        {
            canonical.Value = $"{commit}_p{p}";
            path.Value = $"src/P{p}/P{p}.csproj";
            var projectId = (long)project.ExecuteScalar()!;
            snapshots.MapProject(snapshotId, projectId);
            fileProject.Value = projectId;
            var fileId = (long)file.ExecuteScalar()!;
            versionFile.Value = fileId;
            versionHash.Value = Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray();
            var fileVersionId = (long)version.ExecuteScalar()!;

            symbolProject.Value = projectId;
            fileVersion.Value = fileVersionId;
            for (var i = 0; i < perProject; i++)
            {
                var display = $"{Verbs[random.Next(Verbs.Length)]}{Nouns[random.Next(Nouns.Length)]}{random.Next(1000)}";
                key.Value = $"k:{p}:{i}";
                fqn.Value = $"global::P{p}.{display}";
                name.Value = display;
                kind.Value = random.Next(1000) == 0 ? (int)SymbolKind.Delegate : (int)SymbolKind.Method;
                symbol.ExecuteNonQuery();
            }
        }
        Exec(conn, "COMMIT;");
    }

    // string.GetHashCode and HashCode are randomized per process; the synthetic names must be the same on every run.
    private static int StableSeed(string text)
    {
        var hash = 17;
        foreach (var c in text)
            hash = unchecked(hash * 31 + c);
        return hash;
    }

    private static void Exec(Microsoft.Data.Sqlite.SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
