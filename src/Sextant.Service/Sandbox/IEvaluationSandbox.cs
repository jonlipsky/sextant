namespace Sextant.Service.Sandbox;

/// <summary>
/// The seam the worker uses to run untrusted repository evaluation under enforced isolation (Phase 17,
/// criterion 2). Kept as an interface so the worker's untrusted region can be verified — in tests — to run
/// INSIDE the sandbox without spinning up a real MSBuild evaluation.
/// </summary>
public interface IEvaluationSandbox
{
    /// <summary>
    /// Runs <paramref name="evaluate"/> under the sandbox's resource + secret isolation. The passed token is
    /// linked to the sandbox's time/memory budget, so a well-behaved evaluation that honors cancellation is
    /// aborted when a budget is exceeded (surfaced as <see cref="SandboxLimitExceededException"/>).
    /// <paramref name="packagesDir"/> is the repository's persistent package folder (issue #272,
    /// <see cref="Restore.PackageCache"/>) the evaluation's <c>NUGET_PACKAGES</c> points at, or null for a folder in
    /// job scratch; anything but one repository's folder under <see cref="ServicePaths.PackageCacheRoot"/> is refused.
    /// </summary>
    Task<T> RunAsync<T>(
        string checkoutDir, string scratchDir, string? packagesDir, Func<CancellationToken, Task<T>> evaluate,
        CancellationToken cancellationToken);

    /// <summary>
    /// The wall-clock budget <see cref="RunAsync"/> enforces, or null when it enforces none. The worker plans its
    /// own phase deadlines inside it, so a large checkout degrades to a partial snapshot before the hard abort.
    /// </summary>
    TimeSpan? TimeBudget { get; }

    /// <summary>
    /// The policy token recorded on a job the sandbox aborted (<see cref="EvaluationBudgetPolicy.Token"/>), or null
    /// when the sandbox enforces no budget.
    /// </summary>
    string? BudgetPolicyToken { get; }
}

/// <summary>Thrown when untrusted evaluation exceeds a sandbox budget (time or memory) and is aborted.</summary>
public sealed class SandboxLimitExceededException(string message) : Exception(message)
{
    /// <summary>The budget that was exceeded: <c>time</c> or <c>memory</c>.</summary>
    public string? Kind { get; init; }

    /// <summary>The <see cref="EvaluationBudgetPolicy.Token"/> of the policy that aborted the run.</summary>
    public string? PolicyToken { get; init; }
}

/// <summary>Thrown, fail-closed, when a sandbox invariant cannot be satisfied (e.g. scratch outside the scratch root).</summary>
public sealed class SandboxViolationException(string message) : Exception(message);
