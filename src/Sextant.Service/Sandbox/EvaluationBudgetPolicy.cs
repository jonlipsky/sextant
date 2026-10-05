using System.Globalization;
using System.Text.RegularExpressions;

namespace Sextant.Service.Sandbox;

/// <summary>
/// Decides when a job the sandbox aborted for exceeding a budget may be retried (issue #245). An aborted job is
/// terminal, and the service reuses a terminal failed job for its identity (#153), so without this a budget
/// failure would be returned forever, even after the worker learned to stay inside the budget. The worker
/// records the <see cref="Token"/> of the policy that aborted the run; the service treats the failure as stale
/// (and runs the job again) only when the current token differs. A run under the same policy is never retried,
/// so a repository that cannot fit is retried at most once per policy change, and nothing else re-indexes.
/// </summary>
public static partial class EvaluationBudgetPolicy
{
    /// <summary>
    /// The version of the budget handling. Bump it whenever the worker's degradation changes in a way that can
    /// let a previously aborted repository finish, so each recorded abort is retried once.
    /// Version 1 was the implicit pre-token behavior: a hard abort with no phase deadlines.
    /// </summary>
    public const int Version = 2;

    /// <summary>The error diagnostic the worker records on a job the sandbox aborted.</summary>
    public const string ExceededCode = "evaluation_budget_exceeded";

    /// <summary>The info diagnostic carrying the aborting policy's <see cref="Token"/> as its message.</summary>
    public const string PolicyCode = "evaluation_budget_policy";

    /// <summary>
    /// The token of <paramref name="policy"/>: the handling version plus the enforced budgets, or null when the
    /// sandbox is disabled (it then enforces no budget).
    /// </summary>
    public static string? Token(SandboxPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.Enabled)
            return null;
        var seconds = policy.TimeBudget > TimeSpan.Zero ? (long)policy.TimeBudget.TotalSeconds : 0;
        var memory = Math.Max(0, policy.MemoryBudgetBytes);
        return string.Create(CultureInfo.InvariantCulture, $"v{Version};time={seconds};memory={memory}");
    }

    /// <summary>
    /// True when a failed job is a budget abort that the current policy should retry: it recorded a policy token
    /// other than <paramref name="currentToken"/>, or it predates tokens (no <see cref="ExceededCode"/> diagnostic)
    /// and its last error is the sandbox's abort message. An abort recorded without a token is never retried,
    /// because nothing shows the policy changed.
    /// </summary>
    public static bool IsStaleAbort(
        bool hasExceededDiagnostic, string? recordedToken, string? lastError, string? currentToken) =>
        hasExceededDiagnostic
            ? recordedToken is not null && !string.Equals(recordedToken, currentToken, StringComparison.Ordinal)
            : lastError is not null && LegacyAbortMessage().IsMatch(lastError);

    [GeneratedRegex(@"\Auntrusted repository evaluation exceeded its (time|memory) budget and was aborted\.\z", RegexOptions.CultureInvariant)]
    private static partial Regex LegacyAbortMessage();
}
