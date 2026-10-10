using Sextant.Core;
using Sextant.Indexer;
using Sextant.Service.Sandbox;
using Sextant.Store;
using Sextant.TestSupport;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #245: a sandbox budget abort is recorded with the aborting policy, the service produces an identity
/// again when its recorded abort is stale (a pre-token abort such as prod job 187, or one under another policy),
/// and never loops on an abort under the current policy. Also pins the worker's time plan.
/// </summary>
[TestClass]
public class TimeBudgetRecoveryTests
{
    private const string LegacyTimeAbort = "untrusted repository evaluation exceeded its time budget and was aborted.";
    private const string LegacyMemoryAbort = "untrusted repository evaluation exceeded its memory budget and was aborted.";
    private const string CurrentToken = "v3;time=1800;memory=8589934592";

    private string? _dbPath;
    private IndexDatabase? _db;
    private SnapshotService? _service;
    private string? _dataRoot;

    [TestCleanup]
    public void Cleanup()
    {
        _service?.Dispose();
        if (_dbPath is not null)
            SqliteTestDatabase.Delete(_dbPath, _db);
        try { if (_dataRoot is not null && Directory.Exists(_dataRoot)) Directory.Delete(_dataRoot, recursive: true); } catch { }
    }

    // ---- the policy token ----------------------------------------------------------------------------

    [TestMethod]
    public void Token_OfTheEnforcedPolicy_NamesTheVersionAndBothBudgets()
    {
        Assert.AreEqual(CurrentToken, EvaluationBudgetPolicy.Token(SandboxPolicy.Enforced));
        Assert.AreEqual("v3;time=600;memory=1024", EvaluationBudgetPolicy.Token(
            SandboxPolicy.Enforced with { TimeBudget = TimeSpan.FromMinutes(10), MemoryBudgetBytes = 1024 }));
    }

    [TestMethod]
    public void Token_OfADisabledSandbox_IsNull() =>
        Assert.IsNull(EvaluationBudgetPolicy.Token(SandboxPolicy.Disabled));

    [TestMethod]
    [DataRow(false, null, LegacyTimeAbort, CurrentToken, true, DisplayName = "pre-token time abort (job 187) is stale")]
    [DataRow(false, null, LegacyMemoryAbort, CurrentToken, true, DisplayName = "pre-token memory abort is stale")]
    [DataRow(false, null, "no project could be loaded", CurrentToken, false, DisplayName = "an ordinary failure is not")]
    [DataRow(false, null, "provisioning failed after 3 attempt(s): " + LegacyTimeAbort, CurrentToken, false, DisplayName = "the message embedded in another is not")]
    [DataRow(false, null, LegacyTimeAbort + " (retry)", CurrentToken, false, DisplayName = "the message with a suffix is not")]
    [DataRow(false, null, null, CurrentToken, false, DisplayName = "no error is not")]
    [DataRow(true, CurrentToken, LegacyTimeAbort, CurrentToken, false, DisplayName = "an abort under the current policy is not")]
    [DataRow(true, "v1;time=1800;memory=8589934592", LegacyTimeAbort, CurrentToken, true, DisplayName = "an abort under an older version is stale")]
    [DataRow(true, "v2;time=1800;memory=8589934592", LegacyTimeAbort, CurrentToken, true, DisplayName = "an abort before the one-pass union load (#268) is stale")]
    [DataRow(true, "v3;time=600;memory=8589934592", LegacyTimeAbort, CurrentToken, true, DisplayName = "an abort under another budget is stale")]
    [DataRow(true, null, LegacyTimeAbort, CurrentToken, false, DisplayName = "an abort with no recorded token is not")]
    [DataRow(true, CurrentToken, LegacyTimeAbort, null, true, DisplayName = "an abort when the sandbox is now disabled is stale")]
    public void IsStaleAbort_RetriesOnlyAbortsUnderAnotherPolicy(
        bool exceeded, string? recorded, string? lastError, string? current, bool expected) =>
        Assert.AreEqual(expected, EvaluationBudgetPolicy.IsStaleAbort(exceeded, recorded, lastError, current));

    // ---- the time plan ---------------------------------------------------------------------------------

    [TestMethod]
    public void Plan_SplitsAThirtyMinuteBudget()
    {
        var clock = new ManualClock();
        var start = clock.GetUtcNow();
        var plan = EvaluationTimePlan.Begin(clock, TimeSpan.FromMinutes(30));

        Assert.AreEqual(TimeSpan.FromSeconds(360), plan.RestoreLimit);
        Assert.AreEqual(start + TimeSpan.FromSeconds(1620), plan.ExtractionDeadline);
        // Prod's restore took its old 300 s cap: the load may then run until +894 s (45% of the 1320 s left).
        Assert.AreEqual(start + TimeSpan.FromSeconds(894), plan.LoadDeadline(start + TimeSpan.FromSeconds(300)).At);
        Assert.AreEqual("time budget: 30 min; restore up to 360s, extraction until +1620s.", plan.Describe());
    }

    [TestMethod]
    public void Plan_LoadDeadline_AfterTheExtractionDeadline_IsTheRestoreEnd()
    {
        var clock = new ManualClock();
        var plan = EvaluationTimePlan.Begin(clock, TimeSpan.FromMinutes(30));
        var late = plan.ExtractionDeadline + TimeSpan.FromSeconds(5);

        var deadline = plan.LoadDeadline(late);

        Assert.AreEqual(late, deadline.At);
        Assert.AreSame(clock, deadline.Clock);
    }

    [TestMethod]
    public void Plan_ForIndexing_HandsTheOrchestratorItsShare()
    {
        var clock = new ManualClock();
        var start = clock.GetUtcNow();
        var plan = EvaluationTimePlan.Begin(clock, TimeSpan.FromMinutes(30));
        var notLoaded = new[] { "/c/b/B.csproj" };

        var budget = plan.ForIndexing("/c", notLoaded);

        Assert.AreSame(clock, budget.Clock);
        Assert.AreEqual(TimeSpan.FromMinutes(30), budget.Budget);
        Assert.AreEqual(plan.ExtractionDeadline, budget.ExtractionDeadline);
        Assert.AreEqual("/c", budget.CheckoutRoot);
        CollectionAssert.AreEqual(notLoaded, budget.NotLoaded.ToArray());
        // Symbols may use 40% of the 720 s left when they start at +900 s.
        Assert.AreEqual(start + TimeSpan.FromSeconds(1188), budget.SymbolDeadline(start + TimeSpan.FromSeconds(900)));
    }

    [TestMethod]
    public void Plan_RefusesANonPositiveBudget() =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => EvaluationTimePlan.Begin(new ManualClock(), TimeSpan.Zero));

    // ---- the real sandbox -------------------------------------------------------------------------------

    [TestMethod]
    public void Sandbox_ExposesTheBudgetAndToken_OnlyWhenItEnforcesOne()
    {
        var paths = NewPaths();

        var enforced = new EvaluationSandbox(SandboxPolicy.Enforced, paths);
        Assert.AreEqual(TimeSpan.FromMinutes(30), enforced.TimeBudget);
        Assert.AreEqual(CurrentToken, enforced.BudgetPolicyToken);

        var disabled = new EvaluationSandbox(SandboxPolicy.Disabled, paths);
        Assert.IsNull(disabled.TimeBudget);
        Assert.IsNull(disabled.BudgetPolicyToken);

        var noTimeBudget = new EvaluationSandbox(SandboxPolicy.Enforced with { TimeBudget = TimeSpan.Zero }, paths);
        Assert.IsNull(noTimeBudget.TimeBudget);
    }

    [TestMethod]
    public async Task Sandbox_TimeAbort_KeepsTheMessage_AndCarriesKindAndToken()
    {
        var paths = NewPaths();
        var policy = SandboxPolicy.Enforced with { TimeBudget = TimeSpan.FromMilliseconds(50), MemoryBudgetBytes = 0 };
        var sandbox = new EvaluationSandbox(policy, paths);

        var ex = await Assert.ThrowsExactlyAsync<SandboxLimitExceededException>(async () =>
            await sandbox.RunAsync(paths.CheckoutRoot, paths.AllocateScratch("job"), null,
                async token => { await Task.Delay(TimeSpan.FromSeconds(30), token); return 0; },
                CancellationToken.None));

        Assert.AreEqual(LegacyTimeAbort, ex.Message, "the message is unchanged, so a pre-token record is still recognized.");
        Assert.AreEqual("time", ex.Kind);
        Assert.AreEqual(EvaluationBudgetPolicy.Token(policy), ex.PolicyToken);
    }

    // ---- the worker records the aborting policy ------------------------------------------------------------

    [TestMethod]
    public async Task Worker_Abort_RecordsTheExceededErrorAndTheAbortingPolicy()
    {
        var result = await ProduceWith(new AbortingSandbox(exceptionToken: "v3;time=60;memory=0", sandboxToken: CurrentToken));

        Assert.AreEqual(SnapshotJobStatus.Failed, result.Status);
        Assert.AreEqual(LegacyTimeAbort, result.Error);
        Assert.AreEqual(2, result.Projects.Count);
        var exceeded = result.Projects.Single(p => p.Code == EvaluationBudgetPolicy.ExceededCode);
        Assert.AreEqual(JobDiagnosticSeverity.Error, exceeded.Severity);
        Assert.AreEqual(LegacyTimeAbort, exceeded.Message);
        var policy = result.Projects.Single(p => p.Code == EvaluationBudgetPolicy.PolicyCode);
        Assert.AreEqual(JobDiagnosticSeverity.Info, policy.Severity);
        Assert.AreEqual("v3;time=60;memory=0", policy.Message, "the exception's own token wins: it is the policy that aborted.");
    }

    [TestMethod]
    public async Task Worker_Abort_WithNoTokenOnTheException_FallsBackToTheSandboxToken()
    {
        var result = await ProduceWith(new AbortingSandbox(exceptionToken: null, sandboxToken: CurrentToken));

        Assert.AreEqual(CurrentToken, result.Projects.Single(p => p.Code == EvaluationBudgetPolicy.PolicyCode).Message);
    }

    [TestMethod]
    public async Task Worker_Abort_WithNoTokenAnywhere_RecordsOnlyTheExceededError()
    {
        var result = await ProduceWith(new AbortingSandbox(exceptionToken: null, sandboxToken: null));

        Assert.AreEqual(EvaluationBudgetPolicy.ExceededCode, result.Projects.Single().Code);
    }

    // ---- the service produces a stale abort again --------------------------------------------------------

    [TestMethod]
    public async Task Service_RetriesJob187_APreTokenTimeAbort_OnTheSameJob_ThenReusesTheResult()
    {
        var request = ServiceTestFixtures.Request();
        var jobId = SeedFailedJob(request, LegacyTimeAbort, diagnostics: []);
        var worker = StartService(db => new FakeSnapshotWorker(db));

        var first = await _service!.EnsureSnapshotAsync(request);

        Assert.AreEqual(jobId, first.JobId, "the identity's one durable job is produced again, not forked.");
        Assert.AreEqual(SnapshotJobStatus.Complete, first.Status);
        Assert.AreEqual(1, worker.Calls);
        Assert.IsTrue(first.Attached, "the job existed.");

        var second = await _service.EnsureSnapshotAsync(request);
        Assert.AreEqual(jobId, second.JobId);
        Assert.AreEqual(SnapshotJobStatus.Complete, second.Status);
        Assert.AreEqual(1, worker.Calls, "the new result is reused.");
    }

    [TestMethod]
    public async Task Service_RetriesAStaleAbort_ForANonBlockingEnsure()
    {
        var request = ServiceTestFixtures.Request();
        var jobId = SeedFailedJob(request, LegacyTimeAbort, diagnostics: []);
        var worker = StartService(db => new FakeSnapshotWorker(db) { UseGate = true });

        var pending = await _service!.BeginEnsureSnapshotAsync(request);

        Assert.AreEqual(jobId, pending.JobId);
        Assert.IsFalse(SnapshotJobStatus.IsTerminal(pending.Status),
            $"a stale abort is reported pending while it is produced again, never as its old failure (was {pending.Status}).");

        worker.Gate.SetResult();
        var settled = await _service.EnsureSnapshotAsync(request);
        Assert.AreEqual(jobId, settled.JobId);
        Assert.AreEqual(SnapshotJobStatus.Complete, settled.Status);
        Assert.AreEqual(1, worker.Calls);
    }

    [TestMethod]
    public async Task Service_RetriesAnAbortUnderAnOlderPolicyOnce_ThenReusesAnAbortUnderTheCurrentOne()
    {
        var request = ServiceTestFixtures.Request();
        var jobId = SeedFailedJob(request, LegacyTimeAbort, AbortDiagnostics("v1;time=1800;memory=8589934592"));
        var worker = StartService(db => new FakeSnapshotWorker(db, (_, _) => SnapshotWorkResult.Failed(
            LegacyTimeAbort,
            LocalIndexerSnapshotWorker.BudgetExceededDiagnostics(
                new SandboxLimitExceededException(LegacyTimeAbort) { Kind = "time", PolicyToken = CurrentToken },
                sandbox: null))));

        var first = await _service!.EnsureSnapshotAsync(request);

        Assert.AreEqual(jobId, first.JobId);
        Assert.AreEqual(SnapshotJobStatus.Failed, first.Status);
        Assert.AreEqual(1, worker.Calls, "an abort under an older policy is produced again.");
        Assert.AreEqual(CurrentToken, _service.GetStatus(jobId)!.Diagnostics
            .Single(d => d.Code == EvaluationBudgetPolicy.PolicyCode).Message, "the new abort records the current policy.");

        var second = await _service.EnsureSnapshotAsync(request);
        Assert.AreEqual(SnapshotJobStatus.Failed, second.Status);
        Assert.AreEqual(1, worker.Calls, "an abort under the current policy is reused: no retry loop.");
    }

    [TestMethod]
    public async Task Service_ReusesAnAbortUnderTheCurrentPolicy()
    {
        var request = ServiceTestFixtures.Request();
        SeedFailedJob(request, LegacyTimeAbort, AbortDiagnostics(CurrentToken));
        var worker = StartService(db => new FakeSnapshotWorker(db));

        var result = await _service!.EnsureSnapshotAsync(request);

        Assert.AreEqual(SnapshotJobStatus.Failed, result.Status);
        Assert.AreEqual(0, worker.Calls);
    }

    [TestMethod]
    public async Task Service_ReusesAnAbortThatRecordedNoPolicy()
    {
        var request = ServiceTestFixtures.Request();
        SeedFailedJob(request, LegacyTimeAbort, AbortDiagnostics(token: null));
        var worker = StartService(db => new FakeSnapshotWorker(db));

        var result = await _service!.EnsureSnapshotAsync(request);

        Assert.AreEqual(SnapshotJobStatus.Failed, result.Status);
        Assert.AreEqual(0, worker.Calls, "nothing shows the policy changed, so the abort stands.");
    }

    [TestMethod]
    [DataRow("no project across the 1 selected solution(s) could be loaded on this worker.")]
    [DataRow("provisioning failed after 5 attempt(s): " + LegacyTimeAbort)]
    public async Task Service_ReusesEveryOtherFailure(string lastError)
    {
        var request = ServiceTestFixtures.Request();
        SeedFailedJob(request, lastError, diagnostics: []);
        var worker = StartService(db => new FakeSnapshotWorker(db));

        var result = await _service!.EnsureSnapshotAsync(request);

        Assert.AreEqual(SnapshotJobStatus.Failed, result.Status);
        Assert.AreEqual(0, worker.Calls, "#153: a failed identity is not re-indexed by a later ensure.");
    }

    // ---- helpers ----------------------------------------------------------------------------------------

    private ServicePaths NewPaths()
    {
        _dataRoot ??= ServiceTestFixtures.NewDataRoot();
        return new ServicePaths(ServiceVolumes.Rooted(_dataRoot));
    }

    private async Task<SnapshotWorkResult> ProduceWith(IEvaluationSandbox sandbox)
    {
        var paths = NewPaths();
        var scratch = paths.AllocateScratch("job");
        _dbPath = ServiceTestFixtures.NewDbPath();
        _db = new IndexDatabase(_dbPath, IndexWriteOptions.Default);
        _db.RunMigrations();
        var worker = new LocalIndexerSnapshotWorker(
            _db, new SextantConfiguration(), new StubCheckoutProvider(scratch), capability: null, sandbox: sandbox);
        return await worker.ProduceAsync(
            new EnsureSnapshotRequest { RepositoryRemoteUrl = "https://example/repo", CommitSha = new string('a', 40) },
            "identity-hash", scratch, CancellationToken.None);
    }

    // Records a failed job for the request's identity in a previous service process, the way prod job 187 was left.
    private long SeedFailedJob(EnsureSnapshotRequest request, string lastError, IReadOnlyList<ProjectOutcome> diagnostics)
    {
        _dbPath = ServiceTestFixtures.NewDbPath();
        using var db = new IndexDatabase(_dbPath);
        db.RunMigrations();
        var jobs = new SnapshotJobStore(db.GetConnection());
        var (job, _) = jobs.EnsureJob(request.ToIdentity().Hash, request.RepositoryRemoteUrl, request.CommitSha, request.BranchName);
        jobs.MarkRunning(job.Id, "previous-process");
        jobs.MarkResult(job.Id, SnapshotJobStatus.Failed, null, lastError);
        jobs.ReplaceDiagnostics(job.Id, diagnostics.Select(d => d.ToDiagnostic(job.Id)));
        return job.Id;
    }

    // Restarts the service on the seeded catalog.
    private FakeSnapshotWorker StartService(Func<IndexDatabase, FakeSnapshotWorker> create)
    {
        _db = new IndexDatabase(_dbPath!);
        _db.RunMigrations();
        var worker = create(_db);
        _service = SnapshotService.Start(ServiceTestFixtures.NewOptions(_dbPath!), worker, _db);
        return worker;
    }

    private static IReadOnlyList<ProjectOutcome> AbortDiagnostics(string? token) =>
        LocalIndexerSnapshotWorker.BudgetExceededDiagnostics(
            new SandboxLimitExceededException(LegacyTimeAbort) { Kind = "time", PolicyToken = token }, sandbox: null);

    private sealed class AbortingSandbox(string? exceptionToken, string? sandboxToken) : IEvaluationSandbox
    {
        public TimeSpan? TimeBudget => TimeSpan.FromMinutes(30);

        public string? BudgetPolicyToken => sandboxToken;

        public Task<T> RunAsync<T>(
            string checkoutDir, string scratchDir, string? packagesDir, Func<CancellationToken, Task<T>> evaluate, CancellationToken cancellationToken) =>
            throw new SandboxLimitExceededException(LegacyTimeAbort) { Kind = "time", PolicyToken = exceptionToken };
    }

    private sealed class StubCheckoutProvider(string checkoutDir) : ICheckoutProvider
    {
        public bool TryResolve(EnsureSnapshotRequest request, out CheckoutResolution resolution)
        {
            resolution = new CheckoutResolution
            {
                CheckoutDir = checkoutDir,
                SelectedSolutions = [Path.Combine(checkoutDir, "Solution.slnx")],
                Source = SolutionSelectionSource.DefaultUnion
            };
            return true;
        }
    }
}
