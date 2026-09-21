using System.Diagnostics;

namespace Sextant.Service.Sandbox;

/// <summary>
/// Runs untrusted repository evaluation under enforced resource + secret isolation (Phase 17, acceptance
/// criterion 2). Every service worker evaluation of an untrusted checkout — private or public — goes through
/// here. It enforces:
/// <list type="bullet">
///   <item>a wall-clock TIME budget (linked cancellation — a cooperative evaluation is aborted on breach);</item>
///   <item>a MEMORY ceiling (a watchdog samples the process working set and cancels on breach);</item>
///   <item>SECRET isolation + FILESYSTEM redirection + OFFLINE toolchain via <see cref="SandboxedEnvironmentScope"/>;</item>
///   <item>FAIL-CLOSED filesystem confinement: the per-job scratch MUST be under the scratch root, else it refuses to run.</item>
/// </list>
/// Enforcement boundary (documented honestly): the untrusted work runs IN-PROCESS, so the memory ceiling is
/// a cooperative abort, not an OS hard cap, and the offline posture is best-effort env, not a kernel network
/// block. The OS-hard ceiling (job object / rlimit) and a true network namespace require an out-of-process
/// evaluator and are a tracked hardening follow-up; this class bounds a runaway/hostile evaluation and denies
/// secret access today without destabilizing the service or the byte-identical local path.
/// </summary>
public sealed class EvaluationSandbox(SandboxPolicy policy, ServicePaths paths, Action<string>? log = null) : IEvaluationSandbox
{
    public SandboxPolicy Policy => policy;

    public async Task<T> RunAsync<T>(
        string checkoutDir, string scratchDir, Func<CancellationToken, Task<T>> evaluate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evaluate);

        if (!policy.Enabled)
            return await evaluate(cancellationToken).ConfigureAwait(false);

        // FAIL-CLOSED filesystem confinement: untrusted evaluation may only WRITE into per-job scratch, which
        // is separate from and unreachable by the persistent volumes (a published snapshot can never be
        // corrupted by evaluation). If scratch is not genuinely under the scratch root we refuse to run.
        if (string.IsNullOrEmpty(scratchDir) || !paths.IsScratch(scratchDir))
            throw new SandboxViolationException(
                $"refusing to evaluate: sandbox scratch '{scratchDir}' is not under the service scratch root.");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (policy.TimeBudget > TimeSpan.Zero)
            linked.CancelAfter(policy.TimeBudget);

        var breach = SandboxBreach.None;
        using var watchdog = StartMemoryWatchdog(linked, () => breach = SandboxBreach.Memory);
        using var environment = new SandboxedEnvironmentScope(policy, scratchDir);

        try
        {
            return await evaluate(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The sandbox — not the caller — cancelled: a budget was exceeded. Distinguish memory (watchdog
            // tripped) from time (deadline elapsed) for the diagnostic; either way the job fails, never
            // publishes, and is retryable.
            var kind = breach == SandboxBreach.Memory ? "memory" : "time";
            log?.Invoke($"sandbox aborted untrusted evaluation of '{checkoutDir}': {kind} budget exceeded.");
            throw new SandboxLimitExceededException(
                $"untrusted repository evaluation exceeded its {kind} budget and was aborted.");
        }
    }

    private IDisposable StartMemoryWatchdog(CancellationTokenSource linked, Action onBreach)
    {
        if (policy.MemoryBudgetBytes <= 0)
            return NoopDisposable.Instance;

        var interval = policy.SampleInterval > TimeSpan.Zero ? policy.SampleInterval : TimeSpan.FromSeconds(2);
        var timer = new Timer(_ =>
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                if (process.WorkingSet64 >= policy.MemoryBudgetBytes)
                {
                    onBreach();
                    linked.Cancel();
                }
            }
            catch
            {
                // Sampling failure must not crash the watchdog thread; the time budget still bounds the run.
            }
        }, null, interval, interval);
        return timer;
    }

    private enum SandboxBreach { None, Memory }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();
        public void Dispose() { }
    }
}
