using System.Text.Json;
using Sextant.Service;
using Sextant.Store;

namespace Sextant.ProcessStack.Activities.Tests;

[TestClass]
public sealed class SextantInterpretEnsureResultActivityTests
{
    private static string Wire(EnsureSnapshotResult result) => JsonSerializer.Serialize(result, ServiceJson.Options);

    private static EnsureSnapshotResult Result(
        string status, bool attached = false, bool? branchAdvanced = true, long? snapshotId = 7, string? reason = null) => new()
    {
        JobId = 42,
        IdentityHash = "abc123",
        Status = status,
        SnapshotId = snapshotId,
        Attached = attached,
        BranchAdvanced = branchAdvanced,
        Reason = reason,
    };

    private static async Task<SextantInterpretEnsureResultActivity> InterpretAsync(
        object? statusCode, object? body, string branchUpdate = "advance", string operation = "ensure")
    {
        var activity = new SextantInterpretEnsureResultActivity
        {
            StatusCode = statusCode, Body = body, BranchUpdate = branchUpdate, Operation = operation,
        };
        await ActivityHarness.RunAsync(activity);
        return activity;
    }

    // ---- dual input paths ----

    [TestMethod]
    public async Task Direct_properties_terminal_ensure_is_ok()
    {
        var activity = await InterpretAsync(200, Wire(Result(SnapshotJobStatus.Complete)));

        Assert.AreEqual("ok", activity.Outcome);
        Assert.AreEqual(string.Empty, activity.Reason);
        Assert.AreEqual(42L, activity.JobId);
        Assert.AreEqual("abc123", activity.IdentityHash);
        Assert.AreEqual("complete", activity.JobStatus);
        Assert.IsTrue(activity.Terminal);
        Assert.IsTrue(activity.Published);
        Assert.AreEqual(7L, activity.SnapshotId);
        Assert.IsFalse(activity.Attached);
        Assert.IsTrue(activity.BranchAdvanced);
        Assert.IsFalse(activity.RetryAdvised);
        Assert.IsNull(activity.Retired);
    }

    [TestMethod]
    public async Task Definition_parameters_parsed_json_body_and_numeric_status()
    {
        var activity = new SextantInterpretEnsureResultActivity { StatusCode = 500 }.WithParameters(
            ("statusCode", ActivityHarness.Json("202")),
            ("body", ActivityHarness.Json(Wire(Result(SnapshotJobStatus.Running, branchAdvanced: null, snapshotId: null)))),
            ("branchUpdate", "none"));

        await ActivityHarness.RunAsync(activity);

        Assert.AreEqual("ok", activity.Outcome);
        Assert.AreEqual("running", activity.JobStatus);
        Assert.IsFalse(activity.Terminal);
        Assert.IsFalse(activity.Published);
        Assert.IsNull(activity.SnapshotId);
        Assert.IsNull(activity.BranchAdvanced);
        Assert.IsFalse(activity.RetryAdvised);
    }

    [TestMethod]
    public async Task Definition_parameters_accept_a_status_string_and_a_map_body()
    {
        var activity = new SextantInterpretEnsureResultActivity().WithParameters(
            ("statusCode", "409"),
            ("body", ActivityHarness.Map(("status", "rejected"), ("reason", "head_mismatch"))),
            ("operation", "retire"));

        await ActivityHarness.RunAsync(activity);

        Assert.AreEqual("head_mismatch", activity.Outcome);
        Assert.AreEqual("head_mismatch", activity.Reason);
    }

    // ---- 2xx ensure ----

    [TestMethod]
    [DataRow("queued", false, false)]
    [DataRow("running", false, false)]
    [DataRow("complete", true, true)]
    [DataRow("partial", true, true)]
    [DataRow("failed", true, false)]
    [DataRow("unsupported", true, false)]
    [DataRow("cancelled", true, false)]
    public async Task Every_job_status_maps_to_terminal_and_published(string status, bool terminal, bool published)
    {
        var code = SnapshotJobStatus.IsTerminal(status) ? 200 : 202;

        var activity = await InterpretAsync(code, Wire(Result(status, reason: published ? null : "why")));

        Assert.AreEqual("ok", activity.Outcome);
        Assert.AreEqual(status, activity.JobStatus);
        Assert.AreEqual(terminal, activity.Terminal);
        Assert.AreEqual(published, activity.Published);
        Assert.AreEqual(published ? string.Empty : "why", activity.Reason);
    }

    [TestMethod]
    public async Task An_attach_is_its_own_outcome()
    {
        var activity = await InterpretAsync(200, Wire(Result(SnapshotJobStatus.Complete, attached: true)));

        Assert.AreEqual("attached", activity.Outcome);
        Assert.IsTrue(activity.Attached);
    }

    [TestMethod]
    public async Task A_declined_advance_advises_one_retry()
    {
        var activity = await InterpretAsync(200, Wire(Result(SnapshotJobStatus.Complete, attached: true, branchAdvanced: false)));

        Assert.IsFalse(activity.BranchAdvanced);
        Assert.IsTrue(activity.RetryAdvised);
    }

    [TestMethod]
    [DataRow("none")]
    [DataRow("NONE")]
    public async Task A_none_ensure_never_advises_a_retry(string branchUpdate)
    {
        var activity = await InterpretAsync(200, Wire(Result(SnapshotJobStatus.Complete, branchAdvanced: false)), branchUpdate);

        Assert.IsFalse(activity.BranchAdvanced);
        Assert.IsFalse(activity.RetryAdvised);
    }

    [TestMethod]
    public async Task A_partial_snapshot_reports_its_coverage_reason()
    {
        var activity = await InterpretAsync(200, Wire(Result(SnapshotJobStatus.Partial, reason: "project_skipped")));

        Assert.AreEqual("ok", activity.Outcome);
        Assert.AreEqual("project_skipped", activity.Reason);
        Assert.IsTrue(activity.Published);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("{}")]
    [DataRow("""{"job_id":1}""")]
    [DataRow("""{"status":"complete"}""")]
    [DataRow("""{"job_id":"x","status":"complete"}""")]
    [DataRow("not json")]
    [DataRow("[1,2]")]
    public async Task A_2xx_body_that_is_not_an_ensure_result_is_an_error(string body)
    {
        var activity = await InterpretAsync(200, body);

        Assert.AreEqual("error", activity.Outcome);
        Assert.AreEqual(SextantInterpretEnsureResultActivity.ReasonInvalidResponse, activity.Reason);
        Assert.IsNull(activity.JobId);
    }

    // ---- 2xx retire ----

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task A_retire_reports_whether_the_branch_was_deleted(bool retired)
    {
        var activity = await InterpretAsync(200, JsonSerializer.Serialize(new { retired }, ServiceJson.Options), operation: "retire");

        Assert.AreEqual("ok", activity.Outcome);
        Assert.AreEqual(retired, activity.Retired);
        Assert.IsNull(activity.JobId);
        Assert.AreEqual(string.Empty, activity.JobStatus);
    }

    [TestMethod]
    public async Task A_retire_2xx_without_retired_is_an_error()
    {
        var activity = await InterpretAsync(200, "{}", operation: "Retire");

        Assert.AreEqual("error", activity.Outcome);
        Assert.AreEqual(SextantInterpretEnsureResultActivity.ReasonInvalidResponse, activity.Reason);
    }

    // ---- every refusal the control endpoints return ----

    [TestMethod]
    [DataRow(RepositoryUrlRejection.SchemeNotAllowed)]
    [DataRow(RepositoryUrlRejection.UrlComponentNotAllowed)]
    [DataRow(RepositoryUrlRejection.HostNotAllowed)]
    [DataRow(RepositoryUrlRejection.PathNotAllowed)]
    [DataRow(RepositoryUrlRejection.OwnerNotAllowed)]
    [DataRow(BranchGuardReason.ConflictingBranchGuards)]
    [DataRow(BranchGuardReason.InvalidBranchUpdate)]
    [DataRow(BranchGuardReason.BranchRequired)]
    public async Task A_400_refusal_is_rejected_with_its_reason(string reason)
    {
        var activity = await InterpretAsync(400, JsonSerializer.Serialize(new { status = "rejected", reason }, ServiceJson.Options));

        Assert.AreEqual("rejected", activity.Outcome);
        Assert.AreEqual(reason, activity.Reason);
        Assert.IsFalse(activity.RetryAdvised);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("""{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":"Bad Request","status":400}""")]
    public async Task A_400_from_body_binding_is_rejected_as_http_400(string body)
    {
        var activity = await InterpretAsync(400, body);

        Assert.AreEqual("rejected", activity.Outcome);
        Assert.AreEqual("http_400", activity.Reason);
    }

    [TestMethod]
    [DataRow("Unauthorized", "http_401")]
    [DataRow("""{"error":"invalid_caller_assertion"}""", "invalid_caller_assertion")]
    [DataRow("""{"error":"assertion_not_allowed"}""", "assertion_not_allowed")]
    public async Task A_401_is_unauthorized(string body, string reason)
    {
        var activity = await InterpretAsync(401, body);

        Assert.AreEqual("unauthorized", activity.Outcome);
        Assert.AreEqual(reason, activity.Reason);
    }

    [TestMethod]
    [DataRow("""{"error":"caller_not_allowed"}""", "unauthorized", "caller_not_allowed")]
    [DataRow("""{"error":"not_granted"}""", "not_granted", "not_granted")]
    [DataRow("""{"status":"rejected","reason":"not_granted"}""", "not_granted", "not_granted")]
    [DataRow("", "unauthorized", "http_403")]
    public async Task A_403_is_not_granted_only_for_a_missing_grant(string body, string outcome, string reason)
    {
        var activity = await InterpretAsync(403, body);

        Assert.AreEqual(outcome, activity.Outcome);
        Assert.AreEqual(reason, activity.Reason);
    }

    [TestMethod]
    public async Task The_service_s_not_granted_refusal_is_not_granted()
    {
        // SVC-4's ensure refusal for a user caller without a grant, built from the service's own constant.
        var body = JsonSerializer.Serialize(new { status = "rejected", reason = Sextant.Service.Grants.GrantReason.NotGranted }, ServiceJson.Options);

        var activity = await InterpretAsync(403, body);

        Assert.AreEqual(SextantInterpretEnsureResultActivity.OutcomeNotGranted, activity.Outcome);
        Assert.AreEqual(Sextant.Service.Grants.GrantReason.NotGranted, activity.Reason);
    }

    [TestMethod]
    [DataRow(BranchGuardReason.HeadMismatch, "head_mismatch")]
    [DataRow(BranchGuardReason.DefaultBranch, "rejected")]
    public async Task A_409_retire_refusal_is_classified(string reason, string outcome)
    {
        var activity = await InterpretAsync(409, JsonSerializer.Serialize(new { status = "rejected", reason }, ServiceJson.Options), operation: "retire");

        Assert.AreEqual(outcome, activity.Outcome);
        Assert.AreEqual(reason, activity.Reason);
        Assert.IsNull(activity.Retired);
    }

    [TestMethod]
    public async Task A_503_is_unavailable_with_the_service_reason()
    {
        const string reason = "the index service stopped this ensure before it completed (e.g. it is shutting down); retry the ensure";

        var activity = await InterpretAsync(503, JsonSerializer.Serialize(new { status = "unavailable", reason }, ServiceJson.Options));

        Assert.AreEqual("unavailable", activity.Outcome);
        Assert.AreEqual(reason, activity.Reason);
    }

    [TestMethod]
    [DataRow(0, "no_response")]
    [DataRow(408, "http_408")]
    [DataRow(429, "http_429")]
    [DataRow(500, "http_500")]
    [DataRow(502, "http_502")]
    [DataRow(504, "http_504")]
    [DataRow(599, "http_599")]
    public async Task Transient_codes_are_unavailable(int code, string reason)
    {
        var activity = await InterpretAsync(code, null);

        Assert.AreEqual("unavailable", activity.Outcome);
        Assert.AreEqual(reason, activity.Reason);
    }

    [TestMethod]
    [DataRow((string?)null)]
    [DataRow("")]
    [DataRow("abc")]
    public async Task A_missing_status_is_no_response(string? code)
    {
        var activity = await InterpretAsync(code, null);

        Assert.AreEqual("unavailable", activity.Outcome);
        Assert.AreEqual("no_response", activity.Reason);
    }

    [TestMethod]
    [DataRow(404)]
    [DataRow(405)]
    [DataRow(415)]
    [DataRow(422)]
    [DataRow(301)]
    [DataRow(600)]
    public async Task Other_codes_are_errors(int code)
    {
        var activity = await InterpretAsync(code, string.Empty);

        Assert.AreEqual("error", activity.Outcome);
        Assert.AreEqual($"http_{code}", activity.Reason);
        Assert.IsNull(activity.JobId);
    }

    [TestMethod]
    public async Task A_failure_after_a_success_resets_the_outputs()
    {
        var activity = await InterpretAsync(200, Wire(Result(SnapshotJobStatus.Complete, branchAdvanced: false)));
        Assert.IsTrue(activity.RetryAdvised);

        activity.StatusCode = 503;
        activity.Body = null;
        await ActivityHarness.RunAsync(activity);

        Assert.AreEqual("unavailable", activity.Outcome);
        Assert.IsNull(activity.JobId);
        Assert.IsNull(activity.BranchAdvanced);
        Assert.IsFalse(activity.RetryAdvised);
        Assert.IsFalse(activity.Terminal);
        Assert.AreEqual(string.Empty, activity.IdentityHash);
    }

    [TestMethod]
    public async Task Logs_never_echo_the_body()
    {
        var activity = new SextantInterpretEnsureResultActivity { StatusCode = 400, Body = """{"status":"rejected","reason":"host_not_allowed","secret":"hunter2"}""" };

        var logs = await ActivityHarness.RunAsync(activity);

        Assert.HasCount(1, logs);
        Assert.Contains("rejected (host_not_allowed)", logs[0]);
        Assert.DoesNotContain("hunter2", logs[0]);
    }

    [TestMethod]
    public void Job_statuses_are_the_service_statuses()
    {
        var statuses = new[]
        {
            (JobStatuses.Queued, SnapshotJobStatus.Queued), (JobStatuses.Running, SnapshotJobStatus.Running),
            (JobStatuses.Complete, SnapshotJobStatus.Complete), (JobStatuses.Partial, SnapshotJobStatus.Partial),
            (JobStatuses.Failed, SnapshotJobStatus.Failed), (JobStatuses.Unsupported, SnapshotJobStatus.Unsupported),
            (JobStatuses.Cancelled, SnapshotJobStatus.Cancelled),
        };
        foreach (var (mine, service) in statuses)
        {
            Assert.AreEqual(service, mine);
            Assert.AreEqual(SnapshotJobStatus.IsTerminal(service), JobStatuses.IsTerminal(mine), mine);
        }
        Assert.IsFalse(JobStatuses.IsTerminal("unknown"));
        Assert.AreEqual(SnapshotJobStatus.IsTerminal("unknown"), JobStatuses.IsTerminal("unknown"));
    }
}
