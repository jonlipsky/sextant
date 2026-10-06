using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Store.Tests;

/// <summary>
/// Phase 8, acceptance criteria 4 &amp; 5: retention garbage-collects superseded generations, trims API
/// history, and prunes orphan source blobs, while a read-only dry-run reports eligibility
/// and the protected set — always including the currently-servable
/// generation — is never deleted.
/// </summary>
[TestClass]
public class RetentionServiceTests
{
    private string _dbPath = null!;
    private IndexDatabase _db = null!;
    private SqliteConnection _conn = null!;
    private long _projectId;
    private long _servableRunId;

    [TestInitialize]
    public void TestInitialize()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"sextant_retention_{Guid.NewGuid():N}.db");
        _db = new IndexDatabase(_dbPath);
        _db.RunMigrations();
        _conn = _db.GetConnection();

        var projectStore = new ProjectStore(_conn);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _projectId = projectStore.Insert(new ProjectIdentity
        {
            CanonicalId = "proj_ret_0123456789",
            GitRemoteUrl = "https://github.com/org/repo",
            RepoRelativePath = "src/App/App.csproj"
        }, now);

        SeedGenerations(6, now);
        SeedApiSnapshots(commits: 40, rowsPerCommit: 15);
        SeedOrphanFileVersions(200, now);
        _referencedFileVersionId = SeedReferencedFileVersion(now);
    }

    [TestCleanup]
    public void TestCleanup() => SqliteTestDatabase.Delete(_dbPath, _db);

    private long _referencedFileVersionId;

    // Six complete generations; the newest (highest id) is the currently-servable one.
    private void SeedGenerations(int count, long now)
    {
        var runStore = new IndexRunStore(_conn);
        for (var i = 0; i < count; i++)
        {
            var id = runStore.BeginRun("full", now + i,
                IndexProfileDescriptor.Full.ConfigurationHash, IndexProfiles.Deep, (long)IndexFeature.Deep);
            runStore.MarkComplete(id, now + i, 1);
            _servableRunId = id;
        }
    }

    private void SeedApiSnapshots(int commits, int rowsPerCommit)
    {
        InTransaction(() =>
        {
            for (var c = 0; c < commits; c++)
            {
                var commit = $"commit_{c:D4}_{new string((char)('a' + (c % 26)), 8)}";
                var capturedAt = 1_000L + c;
                for (var r = 0; r < rowsPerCommit; r++)
                {
                    using var cmd = _conn.CreateCommand();
                    cmd.CommandText = """
                        INSERT INTO api_surface_snapshots
                            (project_id, symbol_id, symbol_key, fully_qualified_name, accessibility, signature_hash, captured_at, git_commit)
                        VALUES (@p, NULL, @k, @fqn, 'public', @sig, @cap, @commit);
                        """;
                    cmd.Parameters.AddWithValue("@p", _projectId);
                    cmd.Parameters.AddWithValue("@k", $"key_{c}_{r}_{new string('x', 40)}");
                    cmd.Parameters.AddWithValue("@fqn", $"global::App.Namespace.Type{c}.Member{r}({new string('y', 200)})");
                    cmd.Parameters.AddWithValue("@sig", new string('z', 64));
                    cmd.Parameters.AddWithValue("@cap", capturedAt);
                    cmd.Parameters.AddWithValue("@commit", commit);
                    cmd.ExecuteNonQuery();
                }
            }
        });
    }

    private void SeedOrphanFileVersions(int count, long now, int start = 0)
    {
        InTransaction(() =>
        {
            for (var i = start; i < start + count; i++)
            {
                long fileId;
                using (var fileCmd = _conn.CreateCommand())
                {
                    fileCmd.CommandText =
                        "INSERT INTO files (project_id, repo_relative_path) VALUES (@p, @path) RETURNING id;";
                    fileCmd.Parameters.AddWithValue("@p", _projectId);
                    fileCmd.Parameters.AddWithValue("@path", $"src/Orphan/File{i}.cs");
                    fileId = (long)fileCmd.ExecuteScalar()!;
                }

                using var fvCmd = _conn.CreateCommand();
                fvCmd.CommandText = """
                    INSERT INTO file_versions (file_id, content_hash, last_indexed_at)
                    VALUES (@f, @hash, @now);
                    """;
                fvCmd.Parameters.AddWithValue("@f", fileId);
                fvCmd.Parameters.AddWithValue("@hash", NewHash(i));
                fvCmd.Parameters.AddWithValue("@now", now);
                fvCmd.ExecuteNonQuery();
            }
        });
    }

    // A file version referenced by a live symbol must never be pruned.
    private long SeedReferencedFileVersion(long now)
    {
        long fileId;
        using (var fileCmd = _conn.CreateCommand())
        {
            fileCmd.CommandText =
                "INSERT INTO files (project_id, repo_relative_path) VALUES (@p, 'src/App/Live.cs') RETURNING id;";
            fileCmd.Parameters.AddWithValue("@p", _projectId);
            fileId = (long)fileCmd.ExecuteScalar()!;
        }

        long fileVersionId;
        using (var fvCmd = _conn.CreateCommand())
        {
            fvCmd.CommandText = """
                INSERT INTO file_versions (file_id, content_hash, last_indexed_at)
                VALUES (@f, @hash, @now) RETURNING id;
                """;
            fvCmd.Parameters.AddWithValue("@f", fileId);
            fvCmd.Parameters.AddWithValue("@hash", NewHash(9999));
            fvCmd.Parameters.AddWithValue("@now", now);
            fileVersionId = (long)fvCmd.ExecuteScalar()!;
        }

        using var symCmd = _conn.CreateCommand();
        symCmd.CommandText = """
            INSERT INTO symbols
                (project_id, symbol_key, fully_qualified_name, display_name, kind, accessibility,
                 file_version_id, line_start, line_end, last_indexed_at)
            VALUES (@p, 'global::App.Live', 'global::App.Live', 'Live', 0, 0, @fv, 1, 10, @now);
            """;
        symCmd.Parameters.AddWithValue("@p", _projectId);
        symCmd.Parameters.AddWithValue("@fv", fileVersionId);
        symCmd.Parameters.AddWithValue("@now", now);
        symCmd.ExecuteNonQuery();
        return fileVersionId;
    }

    private static byte[] NewHash(int seed)
    {
        var bytes = new byte[32];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)((seed + i) & 0xFF);
        return bytes;
    }

    private void InTransaction(Action action)
    {
        Exec("BEGIN;");
        try
        {
            action();
            Exec("COMMIT;");
        }
        catch
        {
            Exec("ROLLBACK;");
            throw;
        }
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private long ScalarLong(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    // === Criterion 4: dry-run reports what execution would do, without modifying the database ======

    [TestMethod]
    public void Plan_ReportsDeletionsAndReclaimedBytes_WithoutModifyingDatabase()
    {
        var policy = new RetentionPolicy { KeepCompleteGenerations = 3, ApiSnapshotKeepCommits = 10 };
        var service = new RetentionService(_conn, policy);

        var runsBefore = ScalarLong("SELECT COUNT(*) FROM index_runs;");
        var snapshotsBefore = ScalarLong("SELECT COUNT(*) FROM api_surface_snapshots;");
        var fileVersionsBefore = ScalarLong("SELECT COUNT(*) FROM file_versions;");

        var report = service.Plan();

        Assert.IsTrue(report.DryRun, "Plan() is a dry run");
        Assert.AreEqual(3, report.Deleted.Count, "6 complete runs, keep 3 ⇒ 3 superseded generations deleted");
        Assert.IsFalse(report.ReclaimedBytesKnown, "a read-only plan cannot measure cascade/freelist deltas");
        Assert.AreEqual(0L, report.ReclaimedBytes);
        Assert.IsTrue(report.ApiSnapshotsDeleted > 0, "commits beyond the keep window are reported deletable");
        Assert.IsTrue(report.FileVersionsDeleted > 0, "orphan source blobs are reported prunable");

        Assert.AreEqual(runsBefore, ScalarLong("SELECT COUNT(*) FROM index_runs;"), "Plan must not delete runs");
        Assert.AreEqual(snapshotsBefore, ScalarLong("SELECT COUNT(*) FROM api_surface_snapshots;"), "Plan must not delete snapshots");
        Assert.AreEqual(fileVersionsBefore, ScalarLong("SELECT COUNT(*) FROM file_versions;"), "Plan must not delete blobs");
    }

    [TestMethod]
    public void Execute_DeletesSupersededDataAndReportsReclaimedBytes()
    {
        var policy = new RetentionPolicy { KeepCompleteGenerations = 3, ApiSnapshotKeepCommits = 10 };
        var service = new RetentionService(_conn, policy);
        var plan = service.Plan();

        var report = service.Execute();

        Assert.IsFalse(report.DryRun, "Execute() is not a dry run");
        Assert.AreEqual(plan.Deleted.Count, report.Deleted.Count, "execution deletes exactly what the plan reported");
        Assert.AreEqual(3, ScalarLong("SELECT COUNT(*) FROM index_runs;"), "3 generations retained (incl. servable)");
        Assert.IsTrue(report.ReclaimedBytes > 0, "execution reports the bytes it reclaimed");

        // API history trimmed to the keep window (10 commits remain).
        Assert.AreEqual(10, ScalarLong("SELECT COUNT(DISTINCT git_commit) FROM api_surface_snapshots;"),
            "API history is trimmed to ApiSnapshotKeepCommits");

        // Orphan blobs pruned; the referenced blob survives.
        Assert.AreEqual(1, ScalarLong("SELECT COUNT(*) FROM file_versions;"),
            "only the live-symbol-referenced file version remains");
        Assert.AreEqual(1, ScalarLong($"SELECT COUNT(*) FROM file_versions WHERE id = {_referencedFileVersionId};"),
            "a source blob referenced by a live symbol is never pruned");
    }

    // === Criterion 5: the protected set — always incl. the servable generation — is never deleted ==

    [TestMethod]
    public void Execute_NeverDeletesServableGeneration_EvenWithKeepZero()
    {
        var policy = new RetentionPolicy { KeepCompleteGenerations = 0, ApiSnapshotKeepCommits = 0 };
        var service = new RetentionService(_conn, policy);

        var report = service.Execute();

        var servableStillPresent = ScalarLong($"SELECT COUNT(*) FROM index_runs WHERE id = {_servableRunId};");
        Assert.AreEqual(1, servableStillPresent, "the currently-servable generation must survive even with keep=0");
        Assert.IsTrue(report.Protected.Any(g => g.Id == _servableRunId),
            "the servable generation is reported as protected");

        var lastComplete = new IndexRunStore(_conn).GetLastCompleteRun();
        Assert.IsNotNull(lastComplete, "the servable pointer must still resolve (Phase-7 rebuild gate not tripped)");
        Assert.AreEqual(_servableRunId, lastComplete!.Id);
    }

    [TestMethod]
    public void Execute_HonorsCustomProviderProtection()
    {
        // With keep=0 every non-servable generation is normally deletable; a provider that protects a
        // specific generation must spare it (the Phase-9/10/12 forward-compatible protected-set hook).
        long protectedRunId = _servableRunId - 2;
        var provider = new FixedGenerationProtection(protectedRunId);
        var policy = new RetentionPolicy { KeepCompleteGenerations = 0, ApiSnapshotKeepCommits = 10 };
        var service = new RetentionService(_conn, policy,
            new IRetentionProtectionProvider[] { new LastCompleteRunProtection(), provider });

        var report = service.Execute();

        Assert.AreEqual(1, ScalarLong($"SELECT COUNT(*) FROM index_runs WHERE id = {protectedRunId};"),
            "a provider-protected generation is retained despite keep=0");
        Assert.IsTrue(report.Protected.Any(g => g.Id == protectedRunId),
            "the provider-protected generation is reported as protected");
    }

    [TestMethod]
    public void Execute_HonorsCommitProtection_ForApiHistory()
    {
        // A provider protecting a specific commit keeps its snapshots even outside the keep window.
        var oldestCommit = "commit_0000_" + new string('a', 8);
        var provider = new FixedCommitProtection(oldestCommit);
        var policy = new RetentionPolicy { KeepCompleteGenerations = 3, ApiSnapshotKeepCommits = 10 };
        var service = new RetentionService(_conn, policy,
            new IRetentionProtectionProvider[] { new LastCompleteRunProtection(), provider });

        service.Execute();

        Assert.IsTrue(
            ScalarLong($"SELECT COUNT(*) FROM api_surface_snapshots WHERE git_commit = '{oldestCommit}';") > 0,
            "a provider-protected commit's snapshots survive even beyond the keep window");
    }

    [TestMethod]
    public void Execute_ApiHistoryKeepWindow_IsDeterministicUnderCapturedAtTies()
    {
        // Two commits captured at the SAME instant sit at the keep-window boundary. Without a stable
        // secondary sort key their kept/deleted classification is nondeterministic; the tiebreaker
        // (git_commit DESC) makes the lexicographically-greater commit the deterministic survivor.
        const long tieCapturedAt = 100_000L; // above every fixture commit, so the pair is the newest
        InsertApiRow("tie_commit_aaaa", tieCapturedAt);
        InsertApiRow("tie_commit_bbbb", tieCapturedAt);

        // Keep generations untouched (>6) and isolate the API-history behavior: keep exactly 1 commit.
        var policy = new RetentionPolicy { KeepCompleteGenerations = 10, ApiSnapshotKeepCommits = 1 };
        new RetentionService(_conn, policy).Execute();

        Assert.AreEqual(1, ScalarLong("SELECT COUNT(DISTINCT git_commit) FROM api_surface_snapshots;"),
            "exactly one commit is retained");
        Assert.AreEqual(1,
            ScalarLong("SELECT COUNT(*) FROM api_surface_snapshots WHERE git_commit = 'tie_commit_bbbb';"),
            "the lexicographically-greater tied commit is the deterministic survivor");
        Assert.AreEqual(0,
            ScalarLong("SELECT COUNT(*) FROM api_surface_snapshots WHERE git_commit = 'tie_commit_aaaa';"),
            "the lexicographically-lesser tied commit is deleted");
    }

    private void InsertApiRow(string commit, long capturedAt)
    {
        InTransaction(() =>
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO api_surface_snapshots
                    (project_id, symbol_id, symbol_key, fully_qualified_name, accessibility, signature_hash, captured_at, git_commit)
                VALUES (@p, NULL, @k, @fqn, 'public', @sig, @cap, @commit);
                """;
            cmd.Parameters.AddWithValue("@p", _projectId);
            cmd.Parameters.AddWithValue("@k", $"key_{commit}");
            cmd.Parameters.AddWithValue("@fqn", $"global::App.{commit}");
            cmd.Parameters.AddWithValue("@sig", new string('z', 64));
            cmd.Parameters.AddWithValue("@cap", capturedAt);
            cmd.Parameters.AddWithValue("@commit", commit);
            cmd.ExecuteNonQuery();
        });
    }

    private sealed class FixedGenerationProtection(long runId) : IRetentionProtectionProvider
    {
        public string Name => "test-fixed-generation";
        public void Contribute(SqliteConnection connection, RetentionProtectionBuilder builder)
            => builder.ProtectGeneration(runId, "protected by test provider");
    }

    private sealed class FixedCommitProtection(string commit) : IRetentionProtectionProvider
    {
        public string Name => "test-fixed-commit";
        public void Contribute(SqliteConnection connection, RetentionProtectionBuilder builder)
            => builder.ProtectCommit(commit, "protected by test provider");
    }

    [TestMethod]
    public void Plan_OnReadOnlyConnection_DoesNotWritePagesOrTakeWriterLock()
    {
        using var reader = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = _dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        reader.Open();
        var walBefore = _db.WalBytes;
        var plain = new RetentionService(reader, new RetentionPolicy()).Plan();
        Assert.IsFalse(plain.MoreRemaining);
        Assert.AreEqual(walBefore, _db.WalBytes, "planning writes no WAL pages");
        var admissions = new ActionProtection(() =>
        {
            // A separate writer can publish while planning owns its read transaction.
            Exec("BEGIN IMMEDIATE;");
            var runs = new IndexRunStore(_conn);
            var run = runs.BeginRun("full", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            runs.MarkComplete(run, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 1);
            Exec("COMMIT;");
        });
        var report = new RetentionService(reader, new RetentionPolicy(), [admissions]).Plan();
        Assert.IsFalse(report.MoreRemaining);
        Assert.AreEqual(7L, ScalarLong("SELECT COUNT(*) FROM index_runs;"), "index publish committed during the plan");
        using var changes = reader.CreateCommand();
        changes.CommandText = "SELECT total_changes();";
        Assert.AreEqual(0L, Convert.ToInt64(changes.ExecuteScalar()));
    }

    [TestMethod]
    public void ExecuteBatch_WhenAnotherWriterOwnsLock_ReturnsBoundedBusyWithoutDeleting()
    {
        using var blocker = _db.OpenReadConnection();
        using var cmd = blocker.CreateCommand();
        cmd.CommandText = "BEGIN IMMEDIATE;";
        cmd.ExecuteNonQuery();
        try
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            var report = new RetentionService(_conn, new RetentionPolicy()).ExecuteBatch();
            Assert.AreEqual("sqlite_busy", report.StopReason);
            Assert.IsTrue(report.MoreRemaining);
            Assert.AreEqual(0, report.Deleted.Count);
            Assert.IsTrue(started.Elapsed < TimeSpan.FromSeconds(4));
            Assert.AreEqual(6L, ScalarLong("SELECT COUNT(*) FROM index_runs;"));
        }
        finally
        {
            cmd.CommandText = "ROLLBACK;";
            cmd.ExecuteNonQuery();
        }
        Assert.IsFalse(new RetentionService(_conn, new RetentionPolicy()).Execute().MoreRemaining);
    }

    [TestMethod]
    public void ExecuteBatch_InterruptsSqliteVm_AndRollsBackEarlierDeletes()
    {
        var before = ScalarLong("SELECT COUNT(*) FROM file_versions;");
        var service = new RetentionService(_conn, new RetentionPolicy());
        var report = service.ExecuteBatch(timeLimit: TimeSpan.FromMilliseconds(100), beforeCommit: () =>
        {
            using var slow = _conn.CreateCommand();
            slow.CommandText = """
                WITH RECURSIVE n(i) AS (VALUES(1) UNION ALL SELECT i+1 FROM n WHERE i < 1000000000)
                SELECT SUM(i) FROM n;
                """;
            slow.ExecuteScalar();
        });
        Assert.AreEqual("time_budget", report.StopReason);
        Assert.IsTrue(report.MoreRemaining);
        Assert.AreEqual(before, ScalarLong("SELECT COUNT(*) FROM file_versions;"), "interrupted batch rolls back");
        Assert.AreEqual(6L, ScalarLong("SELECT COUNT(*) FROM index_runs;"));
        Assert.IsFalse(service.Execute().MoreRemaining, "progress handler and transaction are cleaned up");
    }

    [TestMethod]
    public void ExecuteBatch_CancellationRollsBackAndPropagates()
    {
        using var cancelled = new CancellationTokenSource();
        var service = new RetentionService(_conn, new RetentionPolicy());
        Assert.ThrowsExactly<OperationCanceledException>(() => service.ExecuteBatch(cancelled.Token,
            beforeCommit: () => { cancelled.Cancel(); cancelled.Token.ThrowIfCancellationRequested(); }));
        Assert.AreEqual(6L, ScalarLong("SELECT COUNT(*) FROM index_runs;"));
        Assert.IsFalse(service.Execute().MoreRemaining);
    }

    [TestMethod]
    public void RepeatedBatches_Converge_AndDoNotVacuum()
    {
        // VACUUM changes schema_version; reusable pages remain on the freelist instead of shrinking the DB.
        var schemaBefore = ScalarLong("PRAGMA schema_version;");
        SeedOrphanFileVersions(RetentionService.BatchRowLimit * 2, 1, start: 200);
        var service = new RetentionService(_conn, new RetentionPolicy { KeepCompleteGenerations = 3, ApiSnapshotKeepCommits = 10 });
        var planned = service.Plan();
        var total = new RetentionReport { DryRun = false };
        var passes = 0;
        do
        {
            var batch = service.ExecuteBatch();
            Assert.IsNull(batch.StopReason);
            Assert.IsTrue(batch.FileVersionsDeleted <= RetentionService.BatchRowLimit);
            Assert.IsTrue(batch.ApiSnapshotsDeleted <= RetentionService.BatchRowLimit);
            total = RetentionService.Combine(total, batch);
            Assert.IsTrue(++passes < 100);
        } while (total.MoreRemaining);
        Assert.IsTrue(passes > 1);
        Assert.AreEqual(planned.Deleted.Count, total.Deleted.Count);
        Assert.AreEqual(planned.ApiSnapshotsDeleted, total.ApiSnapshotsDeleted);
        Assert.AreEqual(planned.FileVersionsDeleted, total.FileVersionsDeleted);
        Assert.AreEqual(1L, ScalarLong("SELECT COUNT(*) FROM file_versions;"));
        Assert.AreEqual(schemaBefore, ScalarLong("PRAGMA schema_version;"), "no full VACUUM");
        Assert.IsTrue(ScalarLong("PRAGMA freelist_count;") > 0, "freed pages are reusable, not compacted");
    }

    private sealed class ActionProtection(Action action) : IRetentionProtectionProvider
    {
        public string Name => "test-action";
        public void Contribute(SqliteConnection connection, RetentionProtectionBuilder builder) => action();
    }
}
