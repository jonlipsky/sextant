using ProcessStack.Abstractions;

namespace Sextant.ProcessStack.Activities;

/// <summary>
/// Classifies the response of a Sextant service control call (<c>POST /control/ensure</c>, or
/// <c>POST /control/branches/retire</c> with <c>operation: retire</c>) made by a platform <c>HttpRequest</c>
/// node with <c>failOnErrorStatus: false</c>, into one outcome a flow can branch on. Pure computation.
/// </summary>
[ActivityName(TypeName)]
[ActivityDescription("Classifies a Sextant service ensure (or retire) response into an outcome, and advises a retry when a CAS-guarded ensure did not advance its branch (pure computation).")]
public sealed class SextantInterpretEnsureResultActivity : AbstractActivity
{
    /// <summary>The activity name flows use (<c>activity:SextantInterpretEnsureResult</c>).</summary>
    public const string TypeName = "SextantInterpretEnsureResult";

    /// <summary>2xx: the service accepted the request (the job itself may still have failed; see <c>jobStatus</c>).</summary>
    public const string OutcomeOk = "ok";

    /// <summary>2xx: the request attached to an existing job or published snapshot instead of producing one.</summary>
    public const string OutcomeAttached = "attached";

    /// <summary>400 (a refused URL, a malformed guard or body), or a 409 other than <c>head_mismatch</c>.</summary>
    public const string OutcomeRejected = "rejected";

    /// <summary>No response, 408, 429 or 5xx: transient; the nightly reconcile heals it.</summary>
    public const string OutcomeUnavailable = "unavailable";

    /// <summary>409 <c>head_mismatch</c>: a retire's CAS failed because a newer push re-created the branch.</summary>
    public const string OutcomeHeadMismatch = "head_mismatch";

    /// <summary>403 <c>not_granted</c>: the caller has no grant for the repository.</summary>
    public const string OutcomeNotGranted = "not_granted";

    /// <summary>401, or a 403 other than <c>not_granted</c>: the control token or caller assertion was refused.</summary>
    public const string OutcomeUnauthorized = "unauthorized";

    /// <summary>Any other status (404, 415, 3xx, …) or a 2xx body that is not a service result.</summary>
    public const string OutcomeError = "error";

    /// <summary>The <c>reason</c> of a 2xx response whose body is not a service result.</summary>
    public const string ReasonInvalidResponse = "invalid_response";

    /// <summary>The <c>reason</c> when no HTTP status is known (the request never got a response).</summary>
    public const string ReasonNoResponse = "no_response";

    private const string OperationRetire = "retire";
    private const string NotGranted = "not_granted";
    private const string HeadMismatch = "head_mismatch";

    [ActivityInput("statusCode", Required = true, Description = "The HTTP status of the control call (0 or empty when it got no response).")]
    public object? StatusCode { get; set; }

    [ActivityInput("body", Description = "The response body: JSON text, a parsed JSON object or a map.")]
    public object? Body { get; set; }

    [ActivityInput("branchUpdate", Description = "The branch_update the ensure sent (advance or none); a none ensure never advises a retry.", DefaultValue = ServiceRequests.Advance)]
    public string? BranchUpdate { get; set; } = ServiceRequests.Advance;

    [ActivityInput("operation", Description = "ensure (default) or retire: which control call produced the response.", DefaultValue = "ensure")]
    public string? Operation { get; set; } = "ensure";

    [ActivityOutput("outcome", Description = "ok | attached | rejected | unavailable | head_mismatch | not_granted | unauthorized | error.")]
    public string Outcome { get; set; } = OutcomeError;

    [ActivityOutput("reason", Description = "The body's reason (or error) code, else http_<status>; for a 2xx ensure, the job's recorded reason or \"\".")]
    public string Reason { get; set; } = string.Empty;

    [ActivityOutput("jobId", Description = "The ensure job id (2xx ensure only).")]
    public long? JobId { get; set; }

    [ActivityOutput("identityHash", Description = "The snapshot identity hash (2xx ensure only); \"\" otherwise.")]
    public string IdentityHash { get; set; } = string.Empty;

    [ActivityOutput("jobStatus", Description = "The job status: queued | running | complete | partial | failed | unsupported | cancelled; \"\" when not a 2xx ensure.")]
    public string JobStatus { get; set; } = string.Empty;

    [ActivityOutput("terminal", Description = "True when jobStatus is terminal (poll /control/status/{jobId} otherwise).")]
    public bool Terminal { get; set; }

    [ActivityOutput("published", Description = "True when the job published its snapshot (complete or partial).")]
    public bool Published { get; set; }

    [ActivityOutput("snapshotId", Description = "The published snapshot id, when the service reported one.")]
    public long? SnapshotId { get; set; }

    [ActivityOutput("attached", Description = "True when the ensure attached to an existing job or snapshot.")]
    public bool Attached { get; set; }

    [ActivityOutput("branchAdvanced", Description = "The service's branch_advanced: true when this request moved the branch pointer, false when it declined or the pointer was already there, null when not reported.")]
    public bool? BranchAdvanced { get; set; }

    [ActivityOutput("retryAdvised", Description = "True when an advance ensure reported branch_advanced:false: re-send it once (a cheap reuse) so out-of-order pushes converge. Bound the retries: false also means the pointer was already there.")]
    public bool RetryAdvised { get; set; }

    [ActivityOutput("retired", Description = "The retire result (retire 2xx only): true when the branch was deleted, false when it was absent.")]
    public bool? Retired { get; set; }

    public SextantInterpretEnsureResultActivity() => Name = TypeName;

    public override Task ExecuteAsync(ActivityContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var code = ActivityValues.AsLong(ActivityValues.Input(this, "statusCode", StatusCode)) ?? 0;
        var body = ActivityValues.AsMap(ActivityValues.Input(this, "body", Body));
        var branchUpdate = ActivityValues.Text(ActivityValues.Input(this, "branchUpdate", BranchUpdate));
        var isRetire = ActivityValues.Text(ActivityValues.Input(this, "operation", Operation))
            .Equals(OperationRetire, StringComparison.OrdinalIgnoreCase);

        Reset();
        var bodyReason = BodyReason(body);
        if (code is >= 200 and < 300)
        {
            if (isRetire)
                InterpretRetire(body);
            else
                InterpretEnsure(body, branchUpdate);
        }
        else
        {
            Outcome = ClassifyFailure(code, bodyReason);
            Reason = bodyReason.Length > 0 ? bodyReason : code == 0 ? ReasonNoResponse : $"http_{code}";
        }

        // Log codes only: a response body is never echoed.
        context.LogInfo($"{TypeName}: {Outcome} ({(Reason.Length > 0 ? Reason : "http_" + code)}).");
        return Task.CompletedTask;
    }

    private void InterpretEnsure(IReadOnlyDictionary<string, object?>? body, string branchUpdate)
    {
        var jobId = ActivityValues.AsLong(ActivityValues.Get(body, "job_id"));
        var status = ActivityValues.Text(ActivityValues.Get(body, "status")).ToLowerInvariant();
        if (jobId is null || status.Length == 0)
        {
            Outcome = OutcomeError;
            Reason = ReasonInvalidResponse;
            return;
        }

        JobId = jobId;
        JobStatus = status;
        IdentityHash = ActivityValues.Text(ActivityValues.Get(body, "identity_hash"));
        Terminal = JobStatuses.IsTerminal(status);
        Published = status is JobStatuses.Complete or JobStatuses.Partial;
        SnapshotId = ActivityValues.AsLong(ActivityValues.Get(body, "snapshot_id"));
        Attached = ActivityValues.AsBool(ActivityValues.Get(body, "attached")) == true;
        BranchAdvanced = ActivityValues.AsBool(ActivityValues.Get(body, "branch_advanced"));
        RetryAdvised = BranchAdvanced == false
            && !branchUpdate.Equals(ServiceRequests.None, StringComparison.OrdinalIgnoreCase);
        Outcome = Attached ? OutcomeAttached : OutcomeOk;
        Reason = ActivityValues.Text(ActivityValues.Get(body, "reason"));
    }

    private void InterpretRetire(IReadOnlyDictionary<string, object?>? body)
    {
        var retired = ActivityValues.AsBool(ActivityValues.Get(body, "retired"));
        if (retired is null)
        {
            Outcome = OutcomeError;
            Reason = ReasonInvalidResponse;
            return;
        }
        Retired = retired;
        Outcome = OutcomeOk;
    }

    private static string ClassifyFailure(long code, string reason) => code switch
    {
        400 => OutcomeRejected,
        401 => OutcomeUnauthorized,
        403 => reason == NotGranted ? OutcomeNotGranted : OutcomeUnauthorized,
        409 => reason == HeadMismatch ? OutcomeHeadMismatch : OutcomeRejected,
        0 or 408 or 429 or (>= 500 and < 600) => OutcomeUnavailable,
        _ => OutcomeError,
    };

    // The service's refusal bodies carry `reason` (`{"status":"rejected"|"unavailable","reason":…}`); the
    // caller-assertion gate's carry `error`.
    private static string BodyReason(IReadOnlyDictionary<string, object?>? body)
    {
        var reason = ActivityValues.Text(ActivityValues.Get(body, "reason"));
        return reason.Length > 0 ? reason : ActivityValues.Text(ActivityValues.Get(body, "error"));
    }

    private void Reset()
    {
        Outcome = OutcomeError;
        Reason = string.Empty;
        JobId = null;
        IdentityHash = string.Empty;
        JobStatus = string.Empty;
        Terminal = false;
        Published = false;
        SnapshotId = null;
        Attached = false;
        BranchAdvanced = null;
        RetryAdvised = false;
        Retired = null;
    }
}
