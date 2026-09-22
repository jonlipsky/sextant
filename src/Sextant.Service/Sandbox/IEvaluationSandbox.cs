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
    /// </summary>
    Task<T> RunAsync<T>(
        string checkoutDir, string scratchDir, Func<CancellationToken, Task<T>> evaluate, CancellationToken cancellationToken);
}

/// <summary>Thrown when untrusted evaluation exceeds a sandbox budget (time or memory) and is aborted.</summary>
public sealed class SandboxLimitExceededException(string message) : Exception(message);

/// <summary>Thrown, fail-closed, when a sandbox invariant cannot be satisfied (e.g. scratch outside the scratch root).</summary>
public sealed class SandboxViolationException(string message) : Exception(message);
