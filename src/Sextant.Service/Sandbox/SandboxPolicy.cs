namespace Sextant.Service.Sandbox;

/// <summary>
/// The enforced limits the <see cref="EvaluationSandbox"/> applies around untrusted repository evaluation
/// (Phase 17, acceptance criterion 2). MSBuild project evaluation is an UNTRUSTED execution boundary — even
/// for a private repo it can run arbitrary imported targets / SDK resolvers / inline tasks — so the worker
/// path always evaluates under these limits. The defaults are a safe production posture; a deployment can
/// widen or narrow them, and tests use tiny budgets to exercise the enforcement deterministically.
/// </summary>
public sealed record SandboxPolicy
{
    /// <summary>
    /// Master switch. Enforced (true) on the service worker path. The single-node local CLI/daemon path does
    /// NOT run this worker, so leaving the sandbox unwired there keeps local operation byte-identical.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Wall-clock budget for one evaluation; exceeding it aborts the job (retryable), never publishes.</summary>
    public TimeSpan TimeBudget { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Process working-set ceiling sampled by the watchdog; exceeding it aborts the evaluation cooperatively.
    /// Zero disables the memory watchdog. This is a cooperative abort (the untrusted work is in-process), not
    /// an OS hard cap — the OS-enforced ceiling (job object / rlimit over an out-of-process evaluator) is
    /// tracked as issue #76; this bounds a runaway evaluation without taking the service down. See
    /// <see cref="EvaluationSandbox"/> for the full defense-in-depth-not-a-hard-boundary caveat.
    /// </summary>
    public long MemoryBudgetBytes { get; init; } = 8L * 1024 * 1024 * 1024;

    /// <summary>When false (default) the sandbox sets offline/no-telemetry env for the evaluation.</summary>
    public bool AllowNetwork { get; init; }

    /// <summary>When true (default) secret-bearing environment variables are scrubbed for the evaluation.</summary>
    public bool ScrubSecrets { get; init; } = true;

    /// <summary>How often the memory watchdog samples the process working set.</summary>
    public TimeSpan SampleInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The enforced production posture (network denied, secrets scrubbed, bounded time + memory).</summary>
    public static SandboxPolicy Enforced { get; } = new();

    /// <summary>An explicit opt-out (used only where evaluation is already trusted — never the untrusted worker path).</summary>
    public static SandboxPolicy Disabled { get; } = new() { Enabled = false };
}
