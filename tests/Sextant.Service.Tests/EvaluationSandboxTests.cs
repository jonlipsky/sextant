using Sextant.Core;
using Sextant.Core.Platform;
using Sextant.Service;
using Sextant.Service.Sandbox;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Phase 17 / criterion 2 — enforced resource + secret isolation for untrusted repository evaluation. These
/// are hermetic: they drive <see cref="EvaluationSandbox"/> with a trivial evaluate delegate (no real
/// MSBuild), assert budgets abort the evaluation, secrets are scrubbed and restored, toolchain scratch is
/// redirected into the per-job scratch, scratch outside the scratch root is refused fail-closed, and — via a
/// spy sandbox — that <see cref="LocalIndexerSnapshotWorker"/> routes its untrusted region THROUGH the
/// sandbox.
/// </summary>
[TestClass]
public class EvaluationSandboxTests
{
    private string _dataRoot = string.Empty;
    private ServicePaths _paths = null!;
    private string _scratch = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), $"sextant_sbx_{Guid.NewGuid():N}");
        _paths = new ServicePaths(ServiceVolumes.Rooted(_dataRoot));
        _scratch = _paths.AllocateScratch("job");
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { if (Directory.Exists(_dataRoot)) Directory.Delete(_dataRoot, recursive: true); } catch { }
    }

    [TestMethod]
    public async Task Disabled_RunsEvaluateDirectly_WithNoEnvironmentMutation()
    {
        Environment.SetEnvironmentVariable("SEXTANT_TEST_SECRET_TOKEN", "shhh");
        try
        {
            var sandbox = new EvaluationSandbox(SandboxPolicy.Disabled, _paths);
            string? seen = "unset";
            var result = await sandbox.RunAsync(_dataRoot, _scratch, _ =>
            {
                seen = Environment.GetEnvironmentVariable("SEXTANT_TEST_SECRET_TOKEN");
                return Task.FromResult(42);
            }, CancellationToken.None);

            Assert.AreEqual(42, result);
            Assert.AreEqual("shhh", seen, "a disabled sandbox must not scrub secrets (byte-identical passthrough).");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SEXTANT_TEST_SECRET_TOKEN", null);
        }
    }

    [TestMethod]
    public async Task Enforced_ScrubsSecrets_ForTheEvaluation_AndRestoresAfter()
    {
        Environment.SetEnvironmentVariable("SEXTANT_TEST_SECRET_TOKEN", "shhh");
        try
        {
            var sandbox = new EvaluationSandbox(SandboxPolicy.Enforced with { TimeBudget = TimeSpan.Zero }, _paths);
            string? insideSecret = "unset";
            await sandbox.RunAsync(_dataRoot, _scratch, _ =>
            {
                insideSecret = Environment.GetEnvironmentVariable("SEXTANT_TEST_SECRET_TOKEN");
                return Task.FromResult(0);
            }, CancellationToken.None);

            Assert.IsNull(insideSecret, "a secret-named variable must be scrubbed inside the sandbox.");
            Assert.AreEqual("shhh", Environment.GetEnvironmentVariable("SEXTANT_TEST_SECRET_TOKEN"),
                "the secret must be restored after the sandbox scope disposes.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SEXTANT_TEST_SECRET_TOKEN", null);
        }
    }

    [TestMethod]
    public async Task Enforced_RedirectsToolchainScratch_IntoPerJobScratch()
    {
        var sandbox = new EvaluationSandbox(SandboxPolicy.Enforced with { TimeBudget = TimeSpan.Zero }, _paths);
        string? temp = null, nuget = null;
        await sandbox.RunAsync(_dataRoot, _scratch, _ =>
        {
            temp = Environment.GetEnvironmentVariable("TEMP");
            nuget = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
            return Task.FromResult(0);
        }, CancellationToken.None);

        Assert.IsNotNull(temp);
        Assert.IsNotNull(nuget);
        Assert.IsTrue(_paths.IsScratch(temp!), "TEMP must be redirected under the scratch root.");
        Assert.IsTrue(_paths.IsScratch(nuget!), "NUGET_PACKAGES must be redirected under the scratch root.");
    }

    [TestMethod]
    public async Task Enforced_TimeBudgetExceeded_AbortsWithLimitException()
    {
        var policy = SandboxPolicy.Enforced with
        {
            TimeBudget = TimeSpan.FromMilliseconds(50),
            MemoryBudgetBytes = 0 // isolate the time budget
        };
        var sandbox = new EvaluationSandbox(policy, _paths);

        await Assert.ThrowsExactlyAsync<SandboxLimitExceededException>(async () =>
            await sandbox.RunAsync(_dataRoot, _scratch,
                async token => { await Task.Delay(TimeSpan.FromSeconds(30), token); return 0; },
                CancellationToken.None));
    }

    [TestMethod]
    public async Task Enforced_MemoryBudgetExceeded_AbortsWithLimitException()
    {
        // A 1-byte ceiling is exceeded by the test host's working set on the first sample, so the watchdog
        // trips deterministically and cancels the (otherwise long) evaluation.
        var policy = SandboxPolicy.Enforced with
        {
            TimeBudget = TimeSpan.FromMinutes(5),
            MemoryBudgetBytes = 1,
            SampleInterval = TimeSpan.FromMilliseconds(10)
        };
        var sandbox = new EvaluationSandbox(policy, _paths);

        var ex = await Assert.ThrowsExactlyAsync<SandboxLimitExceededException>(async () =>
            await sandbox.RunAsync(_dataRoot, _scratch,
                async token => { await Task.Delay(TimeSpan.FromSeconds(30), token); return 0; },
                CancellationToken.None));
        StringAssert.Contains(ex.Message, "memory");
    }

    [TestMethod]
    public async Task Enforced_ScratchOutsideScratchRoot_FailsClosed()
    {
        var sandbox = new EvaluationSandbox(SandboxPolicy.Enforced, _paths);
        var outside = Path.Combine(_paths.CheckoutRoot, "not-scratch"); // a persistent volume, not scratch

        await Assert.ThrowsExactlyAsync<SandboxViolationException>(async () =>
            await sandbox.RunAsync(_dataRoot, outside, _ => Task.FromResult(0), CancellationToken.None));
    }

    [TestMethod]
    public async Task CallerCancellation_PropagatesAsOperationCanceled_NotLimitException()
    {
        var sandbox = new EvaluationSandbox(SandboxPolicy.Enforced with { MemoryBudgetBytes = 0 }, _paths);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () =>
            await sandbox.RunAsync(_dataRoot, _scratch,
                async token => { await Task.Delay(TimeSpan.FromSeconds(30), token); return 0; },
                cts.Token));
    }

    [TestMethod]
    public async Task Worker_RoutesUntrustedEvaluation_ThroughTheSandbox()
    {
        // A spy sandbox proves the worker's untrusted region is wrapped: it returns a sentinel WITHOUT
        // invoking evaluate, so a real MSBuild load never happens, and the worker surfaces the sentinel.
        var spy = new SpySandbox();
        var config = new SextantConfiguration();
        using var db = new IndexDatabase(Path.Combine(_dataRoot, "catalog.db"), IndexWriteOptions.Default);
        db.RunMigrations();

        var worker = new LocalIndexerSnapshotWorker(
            db, config, new StubCheckoutProvider(_scratch), capability: null, sandbox: spy);

        var request = new EnsureSnapshotRequest
        {
            RepositoryRemoteUrl = "https://example/repo",
            CommitSha = new string('a', 40),
            TreeSha = new string('b', 40)
        };
        var result = await worker.ProduceAsync(request, "identity-hash", _scratch, CancellationToken.None);

        Assert.IsTrue(spy.WasInvoked, "the worker must route its untrusted evaluation through the sandbox.");
        Assert.AreEqual(SnapshotJobStatus.Failed, result.Status);
        StringAssert.Contains(result.Error ?? string.Empty, "sandbox-intercepted");
    }

    private sealed class SpySandbox : IEvaluationSandbox
    {
        public bool WasInvoked { get; private set; }

        public Task<T> RunAsync<T>(
            string checkoutDir, string scratchDir, Func<CancellationToken, Task<T>> evaluate, CancellationToken cancellationToken)
        {
            WasInvoked = true;
            // Do NOT invoke evaluate — proving the untrusted work is inside the sandbox boundary.
            object result = SnapshotWorkResult.Failed("sandbox-intercepted");
            return Task.FromResult((T)result);
        }
    }

    private sealed class StubCheckoutProvider(string checkoutDir) : ICheckoutProvider
    {
        public bool TryResolve(EnsureSnapshotRequest request, out string resolvedCheckoutDir, out string solutionPath)
        {
            resolvedCheckoutDir = checkoutDir;
            solutionPath = Path.Combine(checkoutDir, "Solution.slnx");
            return true;
        }
    }
}
