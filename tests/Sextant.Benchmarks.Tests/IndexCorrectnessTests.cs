using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Indexer;
using Sextant.Store;

namespace Sextant.Benchmarks.Tests;

/// <summary>
/// Phase 4 acceptance tests: full indexing and any supported sequence of incremental edits must
/// converge on the same correct semantic SQLite state. These load real generated projects through
/// MSBuildWorkspace (a NuGet restore per corpus) so they are heavier than the unit tests, and each
/// owns an isolated corpus directory + database so edits never leak between tests.
/// </summary>
[TestClass]
public sealed class IndexCorrectnessTests
{
    // === Criterion 3/4/5: full == full-then-incremental ==================================

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FullAndIncremental_ConvergeToEquivalentCanonicalState(bool useDocumentExtractor)
    {
        var root = NewTempDir("diff");
        try
        {
            var slnx = CorpusGenerator.GenerateCorrectnessCorpus(root);
            Restore(slnx);

            // Path B (incremental): full-index the original state, then apply one edit and reindex it.
            var incrDir = NewTempDir("diff-incr");
            var fullDir = NewTempDir("diff-full");
            try
            {
                using var incrDb = new IndexDatabase(Path.Combine(incrDir, "index.db"));
                incrDb.RunMigrations();
                var solution0 = await SolutionLoader.LoadSolutionAsync(slnx);
                await new IndexOrchestrator(incrDb, useDocumentExtractor: useDocumentExtractor).IndexSolutionAsync(solution0);

                // Sentinel-stamp the independent projects (Lib2, MultiTarget) so that if the
                // incremental rebuild wrongly reprocessed them, their fresh last_indexed_at would
                // overwrite the sentinel. This proves the closure actually NARROWS the work to the
                // changed project and its dependents, not the whole solution.
                StampSentinel(incrDb.GetConnection(), "Lib2");
                StampSentinel(incrDb.GetConnection(), "MultiTarget");

                // Edit a Lib declaration that the App project consumes cross-project.
                var edited = Path.Combine(root, "Lib", "Overloads.cs");
                InsertBeforeLastBrace(edited, "    public int Triple(int x) => x * 3;\n");

                // The daemon reloads the workspace before reindexing; mirror that so the compilation
                // reflects the edit on disk.
                var solution1 = await SolutionLoader.LoadSolutionAsync(slnx);
                var followUps = await new IncrementalIndexer(incrDb, useDocumentExtractor: useDocumentExtractor).IndexChangedFilesAsync(solution1, [edited]);
                Assert.AreEqual(0, followUps.Count, "the closure rebuild leaves no re-enqueue set");

                // Narrowing: the independent projects must still carry the sentinel (untouched).
                Assert.AreEqual(1, SentinelCount(incrDb.GetConnection(), "Lib2"),
                    "Lib2 is not a dependent of the edited project and must not be reprocessed.");
                Assert.AreEqual(1, SentinelCount(incrDb.GetConnection(), "MultiTarget"),
                    "MultiTarget is independent and must not be reprocessed by an edit to Lib.");

                // Path A (full): index the final on-disk state from scratch.
                using var fullDb = new IndexDatabase(Path.Combine(fullDir, "index.db"));
                fullDb.RunMigrations();
                await new IndexOrchestrator(fullDb, useDocumentExtractor: useDocumentExtractor).IndexSolutionAsync(solution1);

                var fullDump = CanonicalIndexDump.Dump(fullDb.GetConnection());
                var incrDump = CanonicalIndexDump.Dump(incrDb.GetConnection());

                // Sanity: the edited symbol exists in both, so we are comparing non-trivial state.
                StringAssert.Contains(fullDump, "Triple", "the full index must contain the edited declaration");
                StringAssert.Contains(incrDump, "Triple", "the incremental index must contain the edited declaration");

                Assert.AreEqual(fullDump, incrDump,
                    "A full index and a full-then-incremental index must produce an equivalent canonical DB.");
            }
            finally
            {
                SqliteTestDatabase.DeleteDirectory(incrDir);
                SqliteTestDatabase.DeleteDirectory(fullDir);
            }
        }
        finally
        {
            SqliteTestDatabase.DeleteDirectory(root);
        }
    }

    // === Criterion 1: initial indexing populates file_index for every file ===============

    [TestMethod]
    public async Task InitialIndex_PopulatesFileIndexForEveryIndexedFile()
    {
        var root = NewTempDir("fileindex");
        var dbDir = NewTempDir("fileindex-db");
        try
        {
            var slnx = CorpusGenerator.GenerateCorrectnessCorpus(root);
            Restore(slnx);

            using var db = new IndexDatabase(Path.Combine(dbDir, "index.db"));
            db.RunMigrations();
            var solution = await SolutionLoader.LoadSolutionAsync(slnx);
            await new IndexOrchestrator(db).IndexSolutionAsync(solution);

            var conn = db.GetConnection();
            var fileIndexRows = Scalar(conn, "SELECT COUNT(*) FROM file_index");
            Assert.IsTrue(fileIndexRows > 0, "file_index must be populated by the initial index (criterion 1).");

            // Every non-generated source symbol's file must have a file_index fingerprint row, and
            // every fingerprint must be a real content hash (not the empty string).
            var symbolFilesWithoutFingerprint = Scalar(conn, """
                SELECT COUNT(DISTINCT s.file_path)
                FROM symbols s
                WHERE NOT EXISTS (
                    SELECT 1 FROM file_index fi
                    WHERE fi.project_id = s.project_id AND fi.file_path = s.file_path)
                """);
            Assert.AreEqual(0, symbolFilesWithoutFingerprint,
                "Every indexed source file must have a file_index fingerprint row.");

            var emptyHashes = Scalar(conn, "SELECT COUNT(*) FROM file_index WHERE content_hash = '' OR content_hash IS NULL");
            Assert.AreEqual(0, emptyHashes, "Every file_index row must carry a real content hash.");
        }
        finally
        {
            SqliteTestDatabase.DeleteDirectory(root);
            SqliteTestDatabase.DeleteDirectory(dbDir);
        }
    }

    // === Criterion 2: unchanged restart schedules no reindex work ========================

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnchangedRestart_SchedulesNoSemanticReindexWork(bool useDocumentExtractor)
    {
        var root = NewTempDir("noop");
        var dbDir = NewTempDir("noop-db");
        try
        {
            var slnx = CorpusGenerator.GenerateCorrectnessCorpus(root);
            Restore(slnx);

            using var db = new IndexDatabase(Path.Combine(dbDir, "index.db"));
            db.RunMigrations();
            var solution = await SolutionLoader.LoadSolutionAsync(slnx);
            await new IndexOrchestrator(db, useDocumentExtractor: useDocumentExtractor).IndexSolutionAsync(solution);

            var conn = db.GetConnection();
            var runsBefore = Scalar(conn, "SELECT COUNT(*) FROM index_runs");
            var before = CanonicalIndexDump.Dump(conn);

            // Simulate a daemon restart re-examining every on-disk file with nothing changed.
            var allFiles = Directory
                .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                            && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                .ToList();
            var solution2 = await SolutionLoader.LoadSolutionAsync(slnx);
            await new IncrementalIndexer(db, useDocumentExtractor: useDocumentExtractor).IndexChangedFilesAsync(solution2, allFiles);

            var runsAfter = Scalar(conn, "SELECT COUNT(*) FROM index_runs");
            var after = CanonicalIndexDump.Dump(conn);

            Assert.AreEqual(runsBefore, runsAfter,
                "An unchanged restart must not open a new index run (fingerprint short-circuit).");
            Assert.AreEqual(before, after, "An unchanged restart must not mutate the semantic state.");
        }
        finally
        {
            SqliteTestDatabase.DeleteDirectory(root);
            SqliteTestDatabase.DeleteDirectory(dbDir);
        }
    }

    // === Criterion 6: file deletion invalidation =========================================

    [TestMethod]
    public async Task DeletedFile_RemovesItsSymbolsOnIncremental()
    {
        var root = NewTempDir("delete");
        var dbDir = NewTempDir("delete-db");
        try
        {
            var slnx = CorpusGenerator.GenerateCorrectnessCorpus(root);
            Restore(slnx);

            using var db = new IndexDatabase(Path.Combine(dbDir, "index.db"));
            db.RunMigrations();
            var solution = await SolutionLoader.LoadSolutionAsync(slnx);
            await new IndexOrchestrator(db).IndexSolutionAsync(solution);

            var conn = db.GetConnection();
            Assert.IsTrue(SymbolExists(conn, "Describer"), "precondition: Describer is indexed before deletion.");

            var deleted = Path.Combine(root, "Lib", "Anonymous.cs");
            File.Delete(deleted);

            var solution2 = await SolutionLoader.LoadSolutionAsync(slnx);
            await new IncrementalIndexer(db).IndexChangedFilesAsync(solution2, [deleted]);

            Assert.IsFalse(SymbolExists(conn, "Describer"),
                "A deleted file's symbols must be purged on the next incremental pass.");
            var staleFileIndex = Scalar(conn, "SELECT COUNT(*) FROM file_index WHERE file_path = $p",
                ("$p", deleted));
            Assert.AreEqual(0, staleFileIndex, "A deleted file must leave no stale file_index fingerprint.");
        }
        finally
        {
            SqliteTestDatabase.DeleteDirectory(root);
            SqliteTestDatabase.DeleteDirectory(dbDir);
        }
    }

    // === Criterion 6: config/evaluation change invalidation ==============================

    [TestMethod]
    public async Task ProjectConfigChange_EscalatesReindexEvenWithNoSourceEdit()
    {
        var root = NewTempDir("config");
        var dbDir = NewTempDir("config-db");
        try
        {
            var slnx = CorpusGenerator.GenerateCorrectnessCorpus(root);
            Restore(slnx);

            using var db = new IndexDatabase(Path.Combine(dbDir, "index.db"));
            db.RunMigrations();
            var solution = await SolutionLoader.LoadSolutionAsync(slnx);
            await new IndexOrchestrator(db).IndexSolutionAsync(solution);

            var conn = db.GetConnection();
            StampSentinel(conn, "Lib");

            // A config change with no .cs byte changed: define a constant in the csproj. The evaluation
            // fingerprint must detect it and escalate a reindex of that project.
            var csproj = Path.Combine(root, "Lib", "Lib.csproj");
            var text = await File.ReadAllTextAsync(csproj);
            text = text.Replace("</PropertyGroup>",
                "    <DefineConstants>$(DefineConstants);EXTRA</DefineConstants>\n  </PropertyGroup>");
            await File.WriteAllTextAsync(csproj, text);

            // No changed .cs paths at all — the escalation must come purely from the fingerprint diff.
            var solution2 = await SolutionLoader.LoadSolutionAsync(slnx);
            await new IncrementalIndexer(db).IndexChangedFilesAsync(solution2, []);

            Assert.AreEqual(0, SentinelCount(conn, "Lib"),
                "A csproj/evaluation change must escalate a reindex of the project even with no source edit.");
        }
        finally
        {
            SqliteTestDatabase.DeleteDirectory(root);
            SqliteTestDatabase.DeleteDirectory(dbDir);
        }
    }

    // === Criterion 4/6: removing a project reference converges with a fresh full index ===

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RemovedProjectReference_IncrementalConvergesWithFullReindex(bool useDocumentExtractor)
    {
        var root = NewTempDir("refdrop");
        try
        {
            var slnx = CorpusGenerator.GenerateCorrectnessCorpus(root);
            Restore(slnx);

            var incrDir = NewTempDir("refdrop-incr");
            var fullDir = NewTempDir("refdrop-full");
            try
            {
                // Path B (incremental): full-index the original App -> Lib state, then drop the reference.
                using var incrDb = new IndexDatabase(Path.Combine(incrDir, "index.db"));
                incrDb.RunMigrations();
                var solution0 = await SolutionLoader.LoadSolutionAsync(slnx);
                await new IndexOrchestrator(incrDb, useDocumentExtractor: useDocumentExtractor).IndexSolutionAsync(solution0);

                Assert.IsTrue(CrossProjectReferenceExists(incrDb.GetConnection(), "App", "Lib"),
                    "precondition: App must have cross-project references into Lib before the edit.");

                // Remove App's project reference to Lib AND its use of Lib types (so it still compiles).
                // Only App's fingerprint/source change, so the new-graph closure would rebuild App alone
                // and leave Lib's inbound reference rows (owned by Lib's symbols) stale — the union with
                // the previous index's cross-project connectivity is what pulls Lib back in.
                var appCsproj = Path.Combine(root, "App", "App.csproj");
                await File.WriteAllTextAsync(appCsproj, """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <TargetFramework>net10.0</TargetFramework>
                        <LangVersion>latest</LangVersion>
                        <Nullable>enable</Nullable>
                        <ImplicitUsings>enable</ImplicitUsings>
                        <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
                      </PropertyGroup>
                    </Project>
                    """.ReplaceLineEndings("\n"));
                var appRunner = Path.Combine(root, "App", "Runner.cs");
                await File.WriteAllTextAsync(appRunner, """
                    namespace App;

                    public class Runner
                    {
                        public int Run() => 42;
                    }
                    """.ReplaceLineEndings("\n"));

                Restore(slnx);
                var solution1 = await SolutionLoader.LoadSolutionAsync(slnx);
                await new IncrementalIndexer(incrDb, useDocumentExtractor: useDocumentExtractor).IndexChangedFilesAsync(solution1, [appCsproj, appRunner]);

                // Path A (full): index the final on-disk state from scratch.
                using var fullDb = new IndexDatabase(Path.Combine(fullDir, "index.db"));
                fullDb.RunMigrations();
                await new IndexOrchestrator(fullDb, useDocumentExtractor: useDocumentExtractor).IndexSolutionAsync(solution1);

                // The now-removed cross-project references must be gone in BOTH databases.
                Assert.IsFalse(CrossProjectReferenceExists(fullDb.GetConnection(), "App", "Lib"),
                    "a fresh full index of the post-edit state has no App -> Lib references (baseline).");
                Assert.IsFalse(CrossProjectReferenceExists(incrDb.GetConnection(), "App", "Lib"),
                    "the incremental rebuild must purge App's removed cross-project references into Lib.");

                var fullDump = CanonicalIndexDump.Dump(fullDb.GetConnection());
                var incrDump = CanonicalIndexDump.Dump(incrDb.GetConnection());
                Assert.AreEqual(fullDump, incrDump,
                    "Removing a project reference incrementally must converge with a fresh full index.");
            }
            finally
            {
                SqliteTestDatabase.DeleteDirectory(incrDir);
                SqliteTestDatabase.DeleteDirectory(fullDir);
            }
        }
        finally
        {
            SqliteTestDatabase.DeleteDirectory(root);
        }
    }

    // === Multi-TFM: incremental edit preserves per-TFM variants (no data loss) ============

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MultiTargetProject_IncrementalEdit_PreservesPerTfmVariantsWithNoDataLoss(bool useDocumentExtractor)
    {
        var root = NewTempDir("mtfm");
        var dbDir = NewTempDir("mtfm-db");
        try
        {
            var slnx = CorpusGenerator.GenerateCorrectnessCorpus(root);
            Restore(slnx);

            using var db = new IndexDatabase(Path.Combine(dbDir, "index.db"));
            db.RunMigrations();
            var solution = await SolutionLoader.LoadSolutionAsync(slnx);
            await new IndexOrchestrator(db, useDocumentExtractor: useDocumentExtractor).IndexSolutionAsync(solution);

            var conn = db.GetConnection();
            var (net10, netStd) = MultiTargetProjectIds(conn);

            // Baseline (full path, already proven elsewhere): conditional member only under net10.0.
            Assert.IsTrue(SymbolExistsInProject(conn, net10, "JoinModern"));
            Assert.IsFalse(SymbolExistsInProject(conn, netStd, "JoinModern"));

            // Edit the shared, linked Formatter.cs (owned by BOTH per-TFM projects) — add a member
            // present under every framework.
            var formatter = Path.Combine(root, "MultiTarget", "Formatter.cs");
            InsertBeforeLastBrace(formatter, "    public string JoinReverse(string a, string b) => Join(b, a);\n");

            var solution2 = await SolutionLoader.LoadSolutionAsync(slnx);
            await new IncrementalIndexer(db, useDocumentExtractor: useDocumentExtractor).IndexChangedFilesAsync(solution2, [formatter]);

            // Re-resolve ids (a rebuild re-inserts project rows, so ids may change).
            (net10, netStd) = MultiTargetProjectIds(conn);

            // The new shared member is present under BOTH frameworks.
            Assert.IsTrue(SymbolExistsInProject(conn, net10, "JoinReverse"),
                "The added shared member must be present under net10.0 after the incremental edit.");
            Assert.IsTrue(SymbolExistsInProject(conn, netStd, "JoinReverse"),
                "The added shared member must be present under netstandard2.0 after the incremental edit.");

            // No cross-TFM interference: the #if NET10_0 member is still ONLY under net10.0, and the
            // pre-existing shared member survives under both — reprocessing one TFM did not drop the
            // other TFM's variant.
            Assert.IsTrue(SymbolExistsInProject(conn, net10, "JoinModern"),
                "The conditional member must survive under net10.0 after the incremental edit.");
            Assert.IsFalse(SymbolExistsInProject(conn, netStd, "JoinModern"),
                "The conditional member must remain absent under netstandard2.0 (no cross-TFM leak).");
            Assert.IsTrue(SymbolExistsInProject(conn, net10, "Join"));
            Assert.IsTrue(SymbolExistsInProject(conn, netStd, "Join"));
        }
        finally
        {
            SqliteTestDatabase.DeleteDirectory(root);
            SqliteTestDatabase.DeleteDirectory(dbDir);
        }
    }

    // === Criterion 7: historical API-surface snapshots survive a working-index rebuild ====

    [TestMethod]
    public async Task HistoricalApiSurfaceSnapshots_SurviveWorkingIndexRebuild()
    {
        var root = NewTempDir("apisurf");
        var dbDir = NewTempDir("apisurf-db");
        try
        {
            var slnx = CorpusGenerator.GenerateCorrectnessCorpus(root);
            Restore(slnx);

            using var db = new IndexDatabase(Path.Combine(dbDir, "index.db"));
            db.RunMigrations();
            var solution = await SolutionLoader.LoadSolutionAsync(slnx);
            await new IndexOrchestrator(db).IndexSolutionAsync(solution);

            var conn = db.GetConnection();
            var projectId = Scalar(conn, "SELECT id FROM projects LIMIT 1");

            // Simulate a snapshot captured at an earlier commit. It is self-contained (stable key +
            // fqn + accessibility inline) and points at no live symbol, exactly as an old commit's
            // snapshot would after its symbols were replaced.
            using (var insert = conn.CreateCommand())
            {
                insert.CommandText = """
                    INSERT INTO api_surface_snapshots
                        (project_id, symbol_id, symbol_key, fully_qualified_name, accessibility, signature_hash, captured_at, git_commit)
                    VALUES ($p, NULL, 'HIST:Key', 'global::Lib.Historical', 'public', 'deadbeef', 1, 'historical-commit-sha')
                    """;
                insert.Parameters.AddWithValue("$p", projectId);
                insert.ExecuteNonQuery();
            }

            var before = Scalar(conn, "SELECT COUNT(*) FROM api_surface_snapshots WHERE git_commit = 'historical-commit-sha'");
            Assert.AreEqual(1, before, "precondition: the historical snapshot exists before the rebuild.");

            // Rebuild the working index from scratch — this deletes and re-inserts every symbol row.
            var solution2 = await SolutionLoader.LoadSolutionAsync(slnx);
            await new IndexOrchestrator(db).IndexSolutionAsync(solution2);

            var after = Scalar(conn, "SELECT COUNT(*) FROM api_surface_snapshots WHERE git_commit = 'historical-commit-sha'");
            Assert.AreEqual(1, after,
                "Historical commit snapshots must NOT be cascade-deleted when the working index is rebuilt (criterion 7).");
        }
        finally
        {
            SqliteTestDatabase.DeleteDirectory(root);
            SqliteTestDatabase.DeleteDirectory(dbDir);
        }
    }

    // === helpers =========================================================================

    private static (long net10, long netStd) MultiTargetProjectIds(SqliteConnection conn)
    {
        var net10 = Scalar(conn, """
            SELECT id FROM projects
            WHERE repo_relative_path LIKE '%MultiTarget%' AND target_framework = 'net10.0'
            """);
        var netStd = Scalar(conn, """
            SELECT id FROM projects
            WHERE repo_relative_path LIKE '%MultiTarget%' AND target_framework = 'netstandard2.0'
            """);
        return (net10, netStd);
    }

    /// <summary>Overwrites a project's symbol fingerprints with a sentinel so a later reprocess is detectable.</summary>
    private static void StampSentinel(SqliteConnection conn, string projectPathFragment)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE symbols SET last_indexed_at = 1
            WHERE project_id IN (SELECT id FROM projects WHERE repo_relative_path LIKE $frag)
            """;
        cmd.Parameters.AddWithValue("$frag", "%" + projectPathFragment + "%");
        cmd.ExecuteNonQuery();
    }

    /// <summary>1 if the sentinel survived (project untouched); 0 if the project was reprocessed.</summary>
    private static long SentinelCount(SqliteConnection conn, string projectPathFragment)
    {
        return Scalar(conn, """
            SELECT CASE WHEN COUNT(*) > 0 AND MIN(last_indexed_at) = 1 AND MAX(last_indexed_at) = 1
                        THEN 1 ELSE 0 END
            FROM symbols
            WHERE project_id IN (SELECT id FROM projects WHERE repo_relative_path LIKE $frag)
            """, ("$frag", "%" + projectPathFragment + "%"));
    }

    private static bool SymbolExists(SqliteConnection conn, string displayName) =>
        Scalar(conn, "SELECT COUNT(*) FROM symbols WHERE display_name = $n", ("$n", displayName)) > 0;

    private static bool SymbolExistsInProject(SqliteConnection conn, long projectId, string displayName) =>
        Scalar(conn, "SELECT COUNT(*) FROM symbols WHERE project_id = $p AND display_name = $n",
            ("$p", projectId), ("$n", displayName)) > 0;

    /// <summary>
    /// True when a cross-project reference (usage site in a different project than the referenced
    /// symbol's owning project) exists from the consumer project to the dependency project, matched by
    /// repo-relative-path fragment. Used to prove a removed project reference purges its stale rows.
    /// </summary>
    private static bool CrossProjectReferenceExists(SqliteConnection conn, string consumerFrag, string dependencyFrag) =>
        Scalar(conn, """
            SELECT COUNT(*)
            FROM "references" r
            JOIN symbols s ON s.id = r.symbol_id
            JOIN projects pc ON pc.id = r.in_project_id
            JOIN projects pd ON pd.id = s.project_id
            WHERE r.in_project_id != s.project_id
              AND pc.repo_relative_path LIKE $c
              AND pd.repo_relative_path LIKE $d
            """, ("$c", "%" + consumerFrag + "%"), ("$d", "%" + dependencyFrag + "%")) > 0;

    private static long Scalar(SqliteConnection conn, string sql, params (string name, object value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

    private static void InsertBeforeLastBrace(string filePath, string insertion)
    {
        var text = File.ReadAllText(filePath);
        var lastBrace = text.LastIndexOf('}');
        Assert.IsTrue(lastBrace >= 0, $"unexpected source layout in {filePath}");
        File.WriteAllText(filePath, text[..lastBrace] + insertion + text[lastBrace..]);
    }

    private static string NewTempDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sextant-correctness-{tag}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Restore(string solutionPath)
    {
        var psi = new ProcessStartInfo("dotnet", $"restore \"{solutionPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        stdoutTask.GetAwaiter().GetResult();
        process.WaitForExit();
        if (process.ExitCode != 0)
            Assert.Inconclusive($"restore of the generated corpus failed (exit {process.ExitCode}): {stderr}");
    }
}
