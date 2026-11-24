using System.Text.Json;
using Sextant.Store;
using Sextant.TestSupport;

namespace Sextant.Service.Tests;

[TestClass]
public sealed class ReplayAdmissionRetryTests
{
    private const string Repository = "https://github.com/acme/replay-retry";

    [TestMethod]
    public async Task FailedAdmission_RetriesOnClock_ThenSurvivesRestart()
    {
        await ExerciseRetry(stopBeforeRetry: false);
    }

    [TestMethod]
    public async Task ShutdownDuringBackoff_RetainsOldRowForRestart()
    {
        await ExerciseRetry(stopBeforeRetry: true);
    }

    // Issue #273 (review round 22, finding 1): a replayed admission that keeps failing retries without holding a writer
    // turn, so another repository's production still gets the writer and completes.
    [TestMethod]
    public async Task PersistentlyFailingReplayAdmission_DoesNotHoldTheWriter()
    {
        var dbPath = ServiceTestFixtures.NewDbPath();
        using var db = new IndexDatabase(dbPath);
        db.RunMigrations();
        var options = ServiceTestFixtures.NewOptions(dbPath);
        var clock = new RetryClock();
        Execute(db, """
            CREATE TRIGGER fail_stuck_refresh BEFORE INSERT ON branch_intents WHEN NEW.branch = 'stuck'
            BEGIN SELECT RAISE(ABORT, 'admission failed'); END;
            """);
        InsertQueued(db, options, 42, new EnsureSnapshotRequest
        {
            RepositoryRemoteUrl = Repository, CommitSha = "A", BranchName = "stuck"
        }, clock.GetUtcNow());

        SnapshotService? service = null;
        var worker = new FakeSnapshotWorker(db);
        try
        {
            service = SnapshotService.Start(options, worker, db, remoteDefaults: new Remote(), timeProvider: clock);
            service.EnableWriterTurnGuardForTesting();
            await clock.RetryScheduled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var other = await service.EnsureSnapshotAsync(new EnsureSnapshotRequest
            {
                RepositoryRemoteUrl = "https://github.com/acme/other", CommitSha = "O", BranchUpdate = BranchUpdateMode.None
            }).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(SnapshotJobStatus.Complete, other.Status, "the writer is not held by the retrying admission");
            Assert.AreEqual(1, worker.Calls);
            Assert.AreEqual(1L, Count(db, "queued_ensures"), "the failing replay is retained for its retry");
            Assert.AreEqual(0, service.WriterTraceViolationsForTesting);
        }
        finally
        {
            service?.Dispose();
            SqliteTestDatabase.Delete(dbPath, db);
        }
    }

    // Issue #273 (review round 22, finding 3): an expired queued ensure is dropped at startup, but the delete keeps
    // failing. The rescan, racing the pending drop, applies the same check and never restarts it; the drop lands on retry.
    [TestMethod]
    public async Task StartupDropWriteFailure_IsRetriedAndTheRescanNeverResurrectsIt()
    {
        var dbPath = ServiceTestFixtures.NewDbPath();
        using var db = new IndexDatabase(dbPath);
        db.RunMigrations();
        var options = ServiceTestFixtures.NewOptions(dbPath);
        var clock = new RetryClock();
        var request = new EnsureSnapshotRequest { RepositoryRemoteUrl = Repository, CommitSha = "A", BranchName = "main" };
        InsertQueued(db, options, 42, request, clock.GetUtcNow() - options.QueuedEnsureMaxAge - TimeSpan.FromHours(1));
        AdmitIntent(db, 42, request, clock.GetUtcNow());
        Execute(db, """
            CREATE TABLE fail_switch (on_off INTEGER);
            INSERT INTO fail_switch VALUES (1);
            CREATE TRIGGER fail_drop BEFORE DELETE ON queued_ensures WHEN EXISTS (SELECT 1 FROM fail_switch)
            BEGIN SELECT RAISE(ABORT, 'drop failed'); END;
            """);

        SnapshotService? service = null;
        var worker = new FakeSnapshotWorker(db);
        try
        {
            service = SnapshotService.Start(options, worker, db, remoteDefaults: new Remote(), timeProvider: clock);
            await clock.RetryScheduled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(1L, Count(db, "queued_ensures"), "the drop has not landed");
            service.RescanBranchIntentsForTesting();
            clock.Advance(TimeSpan.FromSeconds(60));
            service.RescanBranchIntentsForTesting();
            Assert.AreEqual(0, worker.Calls, "the rejected ensure is never restarted");
            Assert.AreEqual("pending", IntentOfQueued(db, 42)!.State);

            ExecuteOutsideService(dbPath, "DELETE FROM fail_switch;");
            clock.Advance(TimeSpan.FromSeconds(60));
            await WaitUntil(() => Count(db, "queued_ensures") == 0);
            Assert.AreEqual("expired", IntentOfQueued(db, 42)!.Outcome);
            Assert.AreEqual(0, worker.Calls);
        }
        finally
        {
            service?.Dispose();
            SqliteTestDatabase.Delete(dbPath, db);
        }
    }

    // Issue #273 (review round 22, finding 3): recovery revalidates exactly as startup does. A queued ensure skipped at
    // startup for its retry deadline expires before that deadline; the rescan drops it instead of restarting it.
    [TestMethod]
    public async Task RecoveryAdmission_RevalidatesAgeBeforeRestarting()
    {
        var dbPath = ServiceTestFixtures.NewDbPath();
        using var db = new IndexDatabase(dbPath);
        db.RunMigrations();
        var options = ServiceTestFixtures.NewOptions(dbPath);
        var clock = new RetryClock();
        var request = new EnsureSnapshotRequest { RepositoryRemoteUrl = Repository, CommitSha = "Z", BranchName = "main" };
        InsertQueued(db, options, 42, request, clock.GetUtcNow() - options.QueuedEnsureMaxAge + TimeSpan.FromMinutes(1));
        var intent = AdmitIntent(db, 42, request, clock.GetUtcNow());
        new BranchIntentStore(db.GetConnection()).Defer(
            intent.Id, (clock.GetUtcNow() + TimeSpan.FromMinutes(2)).ToUnixTimeMilliseconds(), "test");

        SnapshotService? service = null;
        var worker = new FakeSnapshotWorker(db);
        try
        {
            service = SnapshotService.Start(options, worker, db, remoteDefaults: new Remote(), timeProvider: clock);
            Assert.AreEqual(1L, Count(db, "queued_ensures"), "startup deferred it to its retry deadline");
            clock.Advance(TimeSpan.FromMinutes(3));
            service.RescanBranchIntentsForTesting();
            await WaitUntil(() => Count(db, "queued_ensures") == 0);
            Assert.AreEqual("expired", IntentOfQueued(db, 42)!.Outcome);
            // Its following refresh may still converge the branch to the remote head (A); the expired ensure's own commit
            // (Z) is never produced.
            Assert.IsNull(service.GetStatusByIdentity(request.ToIdentity(
                options.DefaultConfigHash, options.DefaultCapabilityFingerprint,
                options.SdkPinIdentityComponent, options.RestoreIdentityComponent).Hash),
                "an expired queued ensure is never restarted by recovery");
        }
        finally
        {
            service?.Dispose();
            SqliteTestDatabase.Delete(dbPath, db);
        }
    }

    // ---- review round 25: the single queued-row owner ------------------------------------------------------------

    // ---- the branch queue is a strict FIFO -------------------------------------------------------------------------

    private sealed record ConvergenceScenario(string DbPath, IndexDatabase Db, RetryClock Clock, NoStateRemote Remote,
        SnapshotService Service, string Repository);

    private static ConvergenceScenario StartConvergence(string repository, FakeSnapshotWorker? worker = null,
        params string[] published)
    {
        var dbPath = ServiceTestFixtures.NewDbPath();
        var db = new IndexDatabase(dbPath);
        db.RunMigrations();
        var options = ServiceTestFixtures.NewOptions(dbPath);
        var clock = new RetryClock();
        foreach (var commit in published.Length == 0 ? ["A", "B"] : published)
            ServiceTestFixtures.PublishComplete(db, new EnsureSnapshotRequest { RepositoryRemoteUrl = repository, CommitSha = commit },
                recordCommit: true);
        var remote = new NoStateRemote { Head = "A" };
        var service = SnapshotService.Start(options, worker ?? new FakeSnapshotWorker(db), db, remoteDefaults: remote,
            timeProvider: clock);
        service.EnableWriterTurnGuardForTesting();
        return new ConvergenceScenario(dbPath, db, clock, remote, service, repository);
    }

    private static void StopConvergence(ConvergenceScenario c)
    {
        c.Remote.Release();
        c.Service.Dispose();
        SqliteTestDatabase.Delete(c.DbPath, c.Db);
    }

    private static Task<EnsureSnapshotResult> Create(ConvergenceScenario c, string branch) =>
        c.Service.EnsureSnapshotAsync(new EnsureSnapshotRequest
        {
            RepositoryRemoteUrl = c.Repository, CommitSha = "A", BranchName = branch, ExpectedHeadCommit = ""
        });

    private static Task WaitForRetryingRefresh(ConvergenceScenario c, string branch) => WaitUntil(() => CountWhere(c.Db,
        "branch_intents", $"kind = 'refresh' AND branch = '{branch}' AND state = 'pending' AND last_error = 'remote_state_unknown'") == 1);

    // Rule 3: a pending (retrying) refresh is never settled by a retirement admitted behind it: it runs first at its retry
    // time and converges the branch; the retirement (default branch) is then refused.
    [TestMethod]
    public async Task Convergence_PendingRefreshAheadOfARefusedRetirement_Converges()
    {
        var c = StartConvergence("https://github.com/acme/converge-pending");
        try
        {
            Assert.AreEqual(SnapshotJobStatus.Complete, (await Create(c, "main").WaitAsync(TimeSpan.FromSeconds(10))).Status);
            await WaitForRetryingRefresh(c, "main");
            c.Remote.Head = "B";
            c.Remote.StateKnown = true;

            var retire = c.Service.RetireBranchAsync(new RetireBranchRequest { Repository = c.Repository, Branch = "main" });
            await WaitUntil(() => CountWhere(c.Db, "branch_intents", "kind = 'retire'") == 1);
            c.Clock.Advance(TimeSpan.FromMinutes(2));
            Assert.AreEqual(BranchGuardReason.DefaultBranch, (await retire.WaitAsync(TimeSpan.FromSeconds(10))).Reason);
            await WaitUntil(() => HeadCommit(c.Db, c.Repository, "main") == "B");
        }
        finally
        {
            StopConvergence(c);
        }
    }

    // A feature-branch retirement whose CAS no longer matches is rejected; the retrying refresh ahead of it was never
    // superseded and converges the feature branch to the remote head. (A retirement that succeeds never recreates the
    // branch: covered by RetryingRefreshThenRetireAbsentBranchDoesNotRecreateIt.)
    [TestMethod]
    public async Task Convergence_FeatureRetirementRejectedByCas_Converges()
    {
        var c = StartConvergence("https://github.com/acme/converge-feature");
        try
        {
            Assert.AreEqual(SnapshotJobStatus.Complete, (await Create(c, "main").WaitAsync(TimeSpan.FromSeconds(10))).Status);
            Assert.AreEqual(SnapshotJobStatus.Complete, (await Create(c, "feature").WaitAsync(TimeSpan.FromSeconds(10))).Status);
            await WaitForRetryingRefresh(c, "feature");
            c.Remote.Head = "B";
            c.Remote.StateKnown = true;

            var retire = c.Service.RetireBranchAsync(new RetireBranchRequest
            {
                Repository = c.Repository, Branch = "feature", ExpectedHeadCommit = "Z"
            });
            await WaitUntil(() => CountWhere(c.Db, "branch_intents", "kind = 'retire'") == 1);
            c.Clock.Advance(TimeSpan.FromMinutes(2));
            var rejected = await retire.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(BranchGuardReason.HeadMismatch, rejected.Reason);
            await WaitUntil(() => HeadCommit(c.Db, c.Repository, "feature") == "B");
        }
        finally
        {
            StopConvergence(c);
        }
    }

    private static List<long> OpenRefreshIntentIds(IndexDatabase db)
    {
        using var conn = db.OpenReadConnection();
        return new BranchIntentStore(conn).ListUnsettled().Where(i => i.Kind == "refresh").Select(i => i.Id).ToList();
    }

    // A remote whose heads are known but whose default/head state can never be observed atomically.
    private sealed class NoStateRemote : IRemoteDefaultBranchResolver, IRemoteBranchHeadResolver
    {
        private (TaskCompletionSource Entered, ManualResetEventSlim Release)? _block;
        private readonly List<ManualResetEventSlim> _releases = [];
        public volatile string Head = "A";
        // Once known, the atomic state reports "main" as the default and Head as the branch head.
        public volatile bool StateKnown;
        // Intake head reads of a repository containing this text block until BlockerGate is set (to fill lookup slots).
        public string? BlockedRepository { get; set; }
        public ManualResetEventSlim BlockerGate { get; } = new();
        private int _blockedReads;
        public int BlockedReads => Volatile.Read(ref _blockedReads);

        public (TaskCompletionSource Entered, ManualResetEventSlim Release) BlockNextStateRead()
        {
            var block = (new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), new ManualResetEventSlim());
            lock (_releases)
            {
                _releases.Add(block.Item2);
                _block = block;
            }
            return block;
        }

        public void Release()
        {
            BlockerGate.Set();
            lock (_releases)
                foreach (var release in _releases)
                    release.Set();
        }

        public string? ResolveDefaultBranch(string repositoryRemoteUrl) => "main";
        public string? ResolveDefaultBranch(string repositoryRemoteUrl, CancellationToken cancellationToken) => "main";
        public string? ResolveBranchHead(string repositoryRemoteUrl, string branch, CancellationToken cancellationToken = default) => Head;
        public RemoteBranchHeadObservation? ObserveBranchHead(
            string repositoryRemoteUrl, string branch, CancellationToken cancellationToken = default)
        {
            if (BlockedRepository is { } blocked && repositoryRemoteUrl.Contains(blocked, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _blockedReads);
                BlockerGate.Wait(TimeSpan.FromSeconds(20));
            }
            return new(Head);
        }

        public RemoteBranchStateObservation? ObserveBranchState(
            string repositoryRemoteUrl, string branch, CancellationToken cancellationToken = default)
        {
            (TaskCompletionSource Entered, ManualResetEventSlim Release)? block;
            lock (_releases)
            {
                block = _block;
                _block = null;
            }
            if (block is { } armed)
            {
                armed.Entered.TrySetResult();
                armed.Release.Wait(TimeSpan.FromSeconds(20));
                return null;
            }
            return StateKnown ? new RemoteBranchStateObservation("main", Head) : null;
        }
    }

    private static long CountWhere(IndexDatabase db, string table, string where)
    {
        using var conn = db.OpenReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {where}";
        return (long)cmd.ExecuteScalar()!;
    }

    private static void InsertQueued(
        IndexDatabase db, ServiceOptions options, long seq, EnsureSnapshotRequest request, DateTimeOffset admittedAt,
        bool restrictsImplicitDefault = false, string? callerTenant = null, string? callerSubject = null)
    {
        using var cmd = db.GetConnection().CreateCommand();
        cmd.CommandText = """
            INSERT INTO queued_ensures(seq, identity_hash, request_json, restricts_implicit_default, caller_tenant,
                                       caller_subject, admitted_at)
            VALUES (@seq, @hash, @json, @restricts, @ctenant, @csubject, @at);
            """;
        cmd.Parameters.AddWithValue("@ctenant", (object?)callerTenant ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@csubject", (object?)callerSubject ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@restricts", restrictsImplicitDefault ? 1 : 0);
        cmd.Parameters.AddWithValue("@seq", seq);
        cmd.Parameters.AddWithValue("@hash", request.ToIdentity(
            options.DefaultConfigHash, options.DefaultCapabilityFingerprint,
            options.SdkPinIdentityComponent, options.RestoreIdentityComponent).Hash);
        cmd.Parameters.AddWithValue("@json", JsonSerializer.Serialize(request, ServiceJson.Options));
        cmd.Parameters.AddWithValue("@at", admittedAt.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }

    private static BranchIntent AdmitIntent(IndexDatabase db, long seq, EnsureSnapshotRequest request, DateTimeOffset at)
    {
        var conn = db.GetConnection();
        var repository = new SnapshotStore(conn).EnsureRepository(request.RepositoryRemoteUrl, at.ToUnixTimeMilliseconds());
        return new BranchIntentStore(conn).Admit(repository, request.BranchName ?? "main", "ensure", seq, null,
            JsonSerializer.Serialize(request, ServiceJson.Options), at.ToUnixTimeMilliseconds());
    }

    private static BranchIntent? IntentOfQueued(IndexDatabase db, long seq)
    {
        using var conn = db.OpenReadConnection();
        return new BranchIntentStore(conn).GetByQueuedEnsureId(seq);
    }

    private static string? HeadCommit(IndexDatabase db, string repository, string branch)
    {
        using var conn = db.OpenReadConnection();
        var snapshots = new SnapshotStore(conn);
        return snapshots.GetRepositoryId(repository) is long repo
               && snapshots.GetBranchId(repo, branch) is long branchId
               && snapshots.GetBranchSnapshotId(branchId) is long snapshotId
            ? snapshots.GetCommitSha(snapshots.GetById(snapshotId)!.CommitId)
            : null;
    }

    private sealed class UnknownHeadRemote : IRemoteDefaultBranchResolver, IRemoteBranchHeadResolver
    {
        public RemoteBranchHeadObservation? ObserveBranchHead(
            string repositoryRemoteUrl, string branch, CancellationToken cancellationToken = default) =>
            ResolveBranchHead(repositoryRemoteUrl, branch, cancellationToken) is { } observed && !string.IsNullOrWhiteSpace(observed)
                ? new RemoteBranchHeadObservation(observed)
                : null;

        public string? ResolveDefaultBranch(string repositoryRemoteUrl, CancellationToken cancellationToken) =>
            ResolveDefaultBranch(repositoryRemoteUrl);

        public string? ResolveDefaultBranch(string repositoryRemoteUrl) => "main";
        public string? ResolveBranchHead(string repositoryRemoteUrl, string branch, CancellationToken cancellationToken = default) => null;
        public RemoteBranchStateObservation? ObserveBranchState(string repositoryRemoteUrl, string branch, CancellationToken cancellationToken = default) => null;
    }

    private static async Task ExerciseRetry(bool stopBeforeRetry)
    {
        var dbPath = ServiceTestFixtures.NewDbPath();
        using var db = new IndexDatabase(dbPath);
        db.RunMigrations();
        var options = ServiceTestFixtures.NewOptions(dbPath);
        var clock = new RetryClock();
        var request = new EnsureSnapshotRequest
        {
            RepositoryRemoteUrl = Repository, CommitSha = "A", BranchName = "main", IsDefaultBranch = true
        };
        // The row was admitted while convergence was off, so it has no intent yet: replay admits one, and fails here.
        Execute(db, """
            CREATE TRIGGER fail_refresh_admission BEFORE INSERT ON branch_intents WHEN NEW.queued_ensure_id = 42
            BEGIN SELECT RAISE(ABORT, 'admission failed'); END;
            """);
        using (var cmd = db.GetConnection().CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO queued_ensures(seq, identity_hash, request_json, admitted_at)
                VALUES (42, @hash, @json, @at);
                """;
            cmd.Parameters.AddWithValue("@hash", request.ToIdentity(
                options.DefaultConfigHash, options.DefaultCapabilityFingerprint,
                options.SdkPinIdentityComponent, options.RestoreIdentityComponent).Hash);
            cmd.Parameters.AddWithValue("@json", JsonSerializer.Serialize(request, ServiceJson.Options));
            cmd.Parameters.AddWithValue("@at", clock.GetUtcNow().ToUnixTimeMilliseconds());
            cmd.ExecuteNonQuery();
        }

        SnapshotService? service = null;
        var worker = new FakeSnapshotWorker(db) { UseGate = true };
        try
        {
            service = SnapshotService.Start(options, worker, db, remoteDefaults: new Remote(), timeProvider: clock);
            await clock.RetryScheduled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(1L, Count(db, "queued_ensures"));
            Assert.AreEqual(0L, Count(db, "branch_intents"));
            Assert.AreEqual(0, worker.Calls);
            Execute(db, "DROP TRIGGER fail_refresh_admission;");

            if (!stopBeforeRetry)
            {
                clock.Advance(TimeSpan.FromMilliseconds(999));
                Assert.AreEqual(0, worker.Calls);
                clock.Advance(TimeSpan.FromMilliseconds(1));
                await WaitUntil(() => worker.Calls == 1);
                Assert.AreEqual(1L, Count(db, "queued_ensures"));
                Assert.AreEqual(2L, Count(db, "branch_intents"), "its intent and its own following refresh were materialized");
            }

            service.Dispose();
            service = null;
            Assert.AreEqual(1L, Count(db, "queued_ensures"));
            var restartedWorker = new FakeSnapshotWorker(db) { UseGate = true };
            service = SnapshotService.Start(options, restartedWorker, db, remoteDefaults: new Remote(), timeProvider: clock);
            await WaitUntil(() => restartedWorker.Calls == 1);
            Assert.AreEqual(1L, Count(db, "queued_ensures"));
            restartedWorker.Gate.TrySetResult();
            await WaitUntil(() => Count(db, "queued_ensures") == 0);
        }
        finally
        {
            worker.Gate.TrySetResult();
            service?.Dispose();
            SqliteTestDatabase.Delete(dbPath, db);
        }
    }

    // A test mutation while the service runs, on its own connection (never the service's guarded writer connection).
    private static void ExecuteOutsideService(string dbPath, string sql)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void Execute(IndexDatabase db, string sql)
    {
        using var cmd = db.GetConnection().CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Count(IndexDatabase db, string table)
    {
        using var conn = db.OpenReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)cmd.ExecuteScalar()!;
    }

    private static void AssertQueuedEnsureIntentOrder(
        IndexDatabase db, long[] sequences, string cataloged, string uncataloged)
    {
        using var connection = db.OpenReadConnection();
        var intents = new BranchIntentStore(connection).ListUnsettled()
            .Where(intent => intent.Kind == "ensure" && intent.QueuedEnsureId is not null)
            .OrderBy(intent => intent.Id)
            .ToArray();
        CollectionAssert.AreEqual(sequences, intents.Select(intent => intent.Id).ToArray());
        CollectionAssert.AreEqual(sequences, intents.Select(intent => intent.QueuedEnsureId!.Value).ToArray());
        Assert.AreEqual("main", intents[0].Branch);
        Assert.AreEqual("main", intents[1].Branch, "a null branch name replays as main");
        Assert.AreEqual("main", intents[2].Branch);
        Assert.AreEqual(intents[0].RepositoryId, intents[2].RepositoryId,
            "equivalent catalog URLs keep one repository identity");
        var snapshotStore = new SnapshotStore(connection);
        Assert.AreEqual(intents[0].RepositoryId, snapshotStore.GetRepositoryId(cataloged));
        Assert.AreEqual(intents[1].RepositoryId, snapshotStore.GetRepositoryId(uncataloged));
    }

    private static long CountQueuedEnsureIntents(IndexDatabase db)
    {
        using var connection = db.OpenReadConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM branch_intents WHERE queued_ensure_id IS NOT NULL;";
        return (long)command.ExecuteScalar()!;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private sealed class Remote : IRemoteDefaultBranchResolver, IRemoteBranchHeadResolver
    {
        public RemoteBranchHeadObservation? ObserveBranchHead(
            string repositoryRemoteUrl, string branch, CancellationToken cancellationToken = default) =>
            ResolveBranchHead(repositoryRemoteUrl, branch, cancellationToken) is { } observed && !string.IsNullOrWhiteSpace(observed)
                ? new RemoteBranchHeadObservation(observed)
                : null;

        public string? ResolveDefaultBranch(string repositoryRemoteUrl, CancellationToken cancellationToken) =>
            ResolveDefaultBranch(repositoryRemoteUrl);

        public string? ResolveDefaultBranch(string repositoryRemoteUrl) => "main";
        public string? ResolveBranchHead(string repositoryRemoteUrl, string branch, CancellationToken cancellationToken = default) => "A";
        public RemoteBranchStateObservation? ObserveBranchState(string repositoryRemoteUrl, string branch, CancellationToken cancellationToken = default) => new("main", "A");
    }

    private sealed class RetryClock : TimeProvider
    {
        private readonly ManualClock _clock = new(DateTimeOffset.UtcNow);
        public TaskCompletionSource RetryScheduled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override DateTimeOffset GetUtcNow() => _clock.GetUtcNow();
        public void Advance(TimeSpan by) => _clock.Advance(by);
        private readonly System.Collections.Concurrent.ConcurrentQueue<TimeSpan> _oneShots = new();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _clock.CreateTimer(callback, state, dueTime, period);
            if (period == Timeout.InfiniteTimeSpan)
                _oneShots.Enqueue(dueTime);
            if (dueTime == TimeSpan.FromSeconds(1) && period == Timeout.InfiniteTimeSpan)
                RetryScheduled.TrySetResult();
            return timer;
        }

        // How many one-shot timers with this due time were created (an owner's backoff: 1 s, 2 s, 4 s, ...).
        public int OneShots(TimeSpan due) => _oneShots.Count(d => d == due);
    }
}
