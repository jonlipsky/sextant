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
/// Enforcement boundary — read this before hosting untrusted code. This sandbox is DEFENSE IN DEPTH, NOT a
/// hard security boundary. The untrusted work (MSBuild evaluation: imported targets, SDK resolvers, inline
/// <c>UsingTask</c>/<c>Exec</c> tasks) runs IN-PROCESS, so a hostile project CAN still: read/write arbitrary
/// filesystem paths the worker user can reach, spawn child processes, open network sockets, and ignore the
/// cooperative cancellation (a tight native loop never observes the token). The memory ceiling is a
/// watchdog-driven cooperative abort, not an OS hard cap; the offline posture is best-effort environment,
/// not a kernel network block. What it DOES buy: a bounded time/memory budget that stops a runaway or
/// merely-greedy evaluation, secret scrubbing so credentials are not in the evaluation's environment, and
/// fail-closed scratch confinement so a published snapshot can never be corrupted by evaluation.
/// <para>
/// Therefore: DO NOT host untrusted third-party repositories in multi-tenant production on this in-process
/// tier. It is adequate for local/single-node use and for explicitly-onboarded, trusted PILOT repositories.
/// True OS-hard isolation (job object / cgroup + rlimits + network namespace / sandbox-exec, over an
/// out-of-process evaluator) is tracked as issue #76 and is a documented precondition for untrusted
/// multi-tenant production (wired into the security runbook + pilot exit criteria). When the sandbox itself
/// cannot honor its confinement invariant (scratch not under the scratch root) it FAILS CLOSED — it refuses
/// to evaluate rather than silently running unconfined.
/// </para>
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
