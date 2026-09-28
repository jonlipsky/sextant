using System.Text.Json;
using Sextant.Service;

namespace Sextant.ProcessStack.Activities.Tests;

[TestClass]
public sealed class SextantPlanRepositoryChangeActivityTests
{
    private const string Repo = "https://github.com/Octo/Repo.git";
    private const string Fork = "https://github.com/fork/Repo.git";
    private const string Before = "1111111111111111111111111111111111111111";
    private const string After = "2222222222222222222222222222222222222222";
    private const string Zeros = "0000000000000000000000000000000000000000";
    private const string Mixed = "abcdefabcdefabcdefabcdefabcdefabcdefabcd";

    private static SextantPlanRepositoryChangeActivity Push(string before = Before, string after = After) => new()
    {
        Kind = "push",
        CloneUrl = Repo,
        DefaultBranch = "main",
        Ref = "refs/heads/feature/x",
        Before = before,
        After = after,
    };

    private static SextantPlanRepositoryChangeActivity PullRequest(string action = "opened") => new()
    {
        Event = "pull_request",
        EventAction = action,
        CloneUrl = Repo,
        DefaultBranch = "main",
        BaseSha = Before,
        BaseRef = "main",
        HeadSha = After,
        HeadRef = "feature/x",
        HeadCloneUrl = Fork,
    };

    private static EnsureSnapshotRequest AsEnsureRequest(object? body) =>
        JsonSerializer.Deserialize<EnsureSnapshotRequest>(JsonSerializer.Serialize(body), ServiceJson.Options)!;

    private static RetireBranchRequest AsRetireRequest(object? body) =>
        JsonSerializer.Deserialize<RetireBranchRequest>(JsonSerializer.Serialize(body), ServiceJson.Options)!;

    // ---- dual input paths ----

    [TestMethod]
    public async Task Direct_properties_push_advances_under_the_cas_on_before()
    {
        var activity = Push();
        activity.Forced = false;

        await ActivityHarness.RunAsync(activity);

        Assert.AreEqual("ensure", activity.Action);
        Assert.AreEqual(string.Empty, activity.Reason);
        Assert.AreEqual(Repo, activity.Repository);
        Assert.AreEqual("feature/x", activity.BranchName);
        Assert.IsFalse(activity.IsDefaultBranch);
        Assert.IsNull(activity.RetireBody);
        Assert.HasCount(1, activity.EnsureBodies);
        Assert.AreSame(activity.EnsureBody, activity.EnsureBodies[0]);

        var request = AsEnsureRequest(activity.EnsureBody);
        Assert.AreEqual(Repo, request.RepositoryRemoteUrl);
        Assert.AreEqual(After, request.CommitSha);
        Assert.AreEqual("feature/x", request.BranchName);
        Assert.IsFalse(request.IsDefaultBranch);
        Assert.AreEqual(Before, request.ExpectedHeadCommit);
        Assert.IsFalse(request.Forced);
        Assert.AreEqual("advance", request.BranchUpdate);
        Assert.IsNull(request.BranchHeadSequence);
        Assert.IsNull(request.BranchGuardProblem());
    }

    [TestMethod]
    public async Task Definition_parameters_metadata_json_plans_the_same_push()
    {
        var metadata = ActivityHarness.Json($$"""
            {"event":"push","cloneUrl":"{{Repo}}","defaultBranch":"main","ref":"refs/heads/main",
             "before":"{{Before}}","after":"{{After}}","forced":true,"deleted":false}
            """);
        var activity = new SextantPlanRepositoryChangeActivity().WithParameters(("metadata", metadata));

        await ActivityHarness.RunAsync(activity);

        Assert.AreEqual("ensure", activity.Action);
        Assert.AreEqual("main", activity.BranchName);
        Assert.IsTrue(activity.IsDefaultBranch);
        var request = AsEnsureRequest(activity.EnsureBody);
        Assert.IsTrue(request.IsDefaultBranch);
        Assert.IsTrue(request.Forced);
        Assert.AreEqual(Before, request.ExpectedHeadCommit);
    }

    [TestMethod]
    public async Task Definition_parameters_individual_inputs_override_the_metadata_map()
    {
        var metadata = ActivityHarness.Map(
            ("event", "push"), ("cloneUrl", Repo), ("ref", "refs/heads/old"), ("before", Before), ("after", After));
        var activity = new SextantPlanRepositoryChangeActivity { Branch = "ignored-by-parameter" }
            .WithParameters(("metadata", metadata), ("branch", ActivityHarness.Json("\"release/2\"")));

        await ActivityHarness.RunAsync(activity);

        Assert.AreEqual("release/2", activity.BranchName);
        Assert.AreEqual("release/2", AsEnsureRequest(activity.EnsureBody).BranchName);
    }

    [TestMethod]
    public async Task Metadata_keys_are_matched_case_insensitively_and_properties_override_them()
    {
        var activity = new SextantPlanRepositoryChangeActivity
        {
            Metadata = ActivityHarness.Map(
                ("Event", "push"), ("CLONEURL", Fork), ("ref", "refs/heads/dev"), ("before", Before), ("after", After)),
            CloneUrl = Repo,
        };

        await ActivityHarness.RunAsync(activity);

        Assert.AreEqual("ensure", activity.Action);
        Assert.AreEqual(Repo, activity.Repository);
        Assert.AreEqual("dev", activity.BranchName);
    }

    [TestMethod]
    public async Task Metadata_as_json_text_is_accepted()
    {
        var activity = new SextantPlanRepositoryChangeActivity
        {
            Metadata = $$"""{"kind":"delete","cloneUrl":"{{Repo}}","ref":"topic","refType":"branch"}""",
        };

        await ActivityHarness.RunAsync(activity);

        Assert.AreEqual("retire", activity.Action);
        Assert.AreEqual("topic", activity.BranchName);
    }

    // ---- the SX-8 precedence table (SVC-6/7), CAS mode ----

    [TestMethod]
    public async Task Row_0_none_leaves_out_every_branch_guard()
    {
        var pr = PullRequest();
        var missingBefore = Push(before: string.Empty);
        missingBefore.Forced = true;
        await ActivityHarness.RunAsync(pr);
        await ActivityHarness.RunAsync(missingBefore);

        foreach (var body in pr.EnsureBodies.Concat(missingBefore.EnsureBodies))
        {
            var map = (IDictionary<string, object?>)body!;
            Assert.AreEqual("none", map["branch_update"]);
            Assert.IsFalse(map.ContainsKey("expected_head_commit"));
            Assert.IsFalse(map.ContainsKey("default_branch"));
            var request = AsEnsureRequest(body);
            Assert.IsTrue(request.SuppressesBranchUpdate);
            Assert.IsNull(request.BranchGuardProblem());
        }
        Assert.IsTrue(AsEnsureRequest(missingBefore.EnsureBody).Forced);
    }

    [TestMethod]
    public async Task Rows_1_and_3_no_body_ever_carries_a_sequence_so_the_guards_never_conflict()
    {
        foreach (var body in await EveryEmittedBodyAsync())
        {
            Assert.IsFalse(body.ContainsKey("branch_head_sequence"));
            var request = AsEnsureRequest(body);
            Assert.IsNull(request.BranchHeadSequence);
            Assert.IsNull(request.BranchGuardProblem());
        }
    }

    [TestMethod]
    public async Task Row_2_a_branch_create_expects_no_pointer()
    {
        var activity = Push(before: Zeros);

        await ActivityHarness.RunAsync(activity);

        Assert.AreEqual("ensure", activity.Action);
        var request = AsEnsureRequest(activity.EnsureBody);
        Assert.AreEqual("", request.ExpectedHeadCommit);
        Assert.AreEqual("advance", request.BranchUpdate);
        Assert.IsNull(request.BranchGuardProblem());
    }

    [TestMethod]
    public async Task Row_2_the_cas_value_is_the_push_before_verbatim()
    {
        var upper = Before.ToUpperInvariant().Replace('1', 'A');
        var activity = Push(before: upper);

        await ActivityHarness.RunAsync(activity);

        var cas = AsEnsureRequest(activity.EnsureBody).ExpectedHeadCommit;
        Assert.AreEqual(upper, cas);
    }

    [TestMethod]
    public async Task Row_4_no_body_advances_without_a_guard()
    {
        foreach (var body in await EveryEmittedBodyAsync())
        {
            var request = AsEnsureRequest(body);
            if (!request.SuppressesBranchUpdate)
                Assert.IsNotNull(request.ExpectedHeadCommit, JsonSerializer.Serialize(body));
        }
    }

    [TestMethod]
    public async Task Forced_is_informational_and_never_replaces_the_cas()
    {
        var activity = Push();
        activity.Forced = "true";

        await ActivityHarness.RunAsync(activity);

        var request = AsEnsureRequest(activity.EnsureBody);
        Assert.IsTrue(request.Forced);
        Assert.AreEqual(Before, request.ExpectedHeadCommit);
        Assert.AreEqual("advance", request.BranchUpdate);
    }

    [TestMethod]
    public async Task An_unparseable_forced_is_left_out()
    {
        var activity = Push();
        activity.Forced = "maybe";

        await ActivityHarness.RunAsync(activity);

        Assert.IsFalse(activity.EnsureBody!.ContainsKey("forced"));
    }

    [TestMethod]
    public async Task An_unknown_default_branch_leaves_default_branch_out()
    {
        var activity = Push();
        activity.DefaultBranch = string.Empty;

        await ActivityHarness.RunAsync(activity);

        Assert.IsFalse(activity.EnsureBody!.ContainsKey("default_branch"));
        Assert.IsFalse(activity.IsDefaultBranch);
        Assert.IsNull(AsEnsureRequest(activity.EnsureBody).IsDefaultBranch);
    }

    [TestMethod]
    public async Task A_push_to_the_default_branch_says_so_and_a_qualified_default_is_accepted()
    {
        var activity = Push();
        activity.Ref = "refs/heads/main";
        activity.DefaultBranch = "refs/heads/main";

        await ActivityHarness.RunAsync(activity);

        Assert.IsTrue(activity.IsDefaultBranch);
        Assert.IsTrue(AsEnsureRequest(activity.EnsureBody).IsDefaultBranch);
    }

    [TestMethod]
    public async Task A_push_without_before_publishes_without_moving_the_pointer()
    {
        var activity = Push(before: "not-a-sha");

        await ActivityHarness.RunAsync(activity);

        Assert.AreEqual("ensure", activity.Action);
        Assert.AreEqual(SextantPlanRepositoryChangeActivity.ReasonMissingBefore, activity.Reason);
        Assert.AreEqual("none", AsEnsureRequest(activity.EnsureBody).BranchUpdate);
        Assert.AreEqual("feature/x", AsEnsureRequest(activity.EnsureBody).BranchName);
    }

    // ---- retire ----

    [TestMethod]
    public async Task A_delete_push_retires_under_the_cas_on_before()
    {
        var activity = Push(after: Zeros);

        await ActivityHarness.RunAsync(activity);

        Assert.AreEqual("retire", activity.Action);
        Assert.AreEqual(string.Empty, activity.Reason);
        Assert.IsNull(activity.EnsureBody);
        Assert.IsEmpty(activity.EnsureBodies);
        var request = AsRetireRequest(activity.RetireBody);
        Assert.AreEqual(Repo, request.Repository);
        Assert.AreEqual("feature/x", request.Branch);
        Assert.AreEqual(Before, request.ExpectedHeadCommit);
    }

    [TestMethod]
    public async Task The_deleted_flag_wins_over_the_after_sha()
    {
        var activity = Push();
        activity.Deleted = ActivityHarness.Json("true");

        await ActivityHarness.RunAsync(activity);

        Assert.AreEqual("retire", activity.Action);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(Zeros)]
    [DataRow("abc")]
    public async Task A_delete_push_without_a_usable_before_retires_without_a_cas(string before)
    {
        var activity = Push(before: before, after: Zeros);

        await ActivityHarness.RunAsync(activity);

        Assert.AreEqual("retire", activity.Action);
        Assert.AreEqual(SextantPlanRepositoryChangeActivity.ReasonMissingBefore, activity.Reason);
        Assert.IsFalse(activity.RetireBody!.ContainsKey("expected_head_commit"));
        Assert.IsNull(AsRetireRequest(activity.RetireBody).ExpectedHeadCommit);
    }

    [TestMethod]
    public async Task A_delete_event_retires_without_a_cas()
    {
        var activity = new SextantPlanRepositoryChangeActivity
        {
            Event = "delete", CloneUrl = Repo, Ref = "feature/x", RefType = "branch",
        };

        await ActivityHarness.RunAsync(activity);

        Assert.AreEqual("retire", activity.Action);
        var request = AsRetireRequest(activity.RetireBody);
        Assert.AreEqual("feature/x", request.Branch);
        Assert.IsNull(request.ExpectedHeadCommit);
    }

    [TestMethod]
    public async Task A_create_event_is_left_to_its_push()
    {
        var activity = new SextantPlanRepositoryChangeActivity { Event = "create", CloneUrl = Repo, Ref = "x", RefType = "branch" };

        await ActivityHarness.RunAsync(activity);

        Assert.AreEqual("ignore", activity.Action);
        Assert.AreEqual(SextantPlanRepositoryChangeActivity.ReasonHandledByPush, activity.Reason);
    }

    // ---- ignore reasons ----

    [TestMethod]
    [DataRow("refType", "tag", SextantPlanRepositoryChangeActivity.ReasonTag)]
    [DataRow("ref", "refs/tags/v1.0", SextantPlanRepositoryChangeActivity.ReasonTag)]
    [DataRow("refType", "repository", SextantPlanRepositoryChangeActivity.ReasonNotABranch)]
    [DataRow("ref", "", SextantPlanRepositoryChangeActivity.ReasonMissingBranch)]
    [DataRow("ref", "refs/heads/", SextantPlanRepositoryChangeActivity.ReasonMissingBranch)]
    [DataRow("ref", "refs/heads/bad..name", SextantPlanRepositoryChangeActivity.ReasonInvalidBranch)]
    [DataRow("ref", "refs/heads/HEAD", SextantPlanRepositoryChangeActivity.ReasonInvalidBranch)]
    [DataRow("after", "", SextantPlanRepositoryChangeActivity.ReasonInvalidAfter)]
    [DataRow("after", "abc123", SextantPlanRepositoryChangeActivity.ReasonInvalidAfter)]
    [DataRow("cloneUrl", "", SextantPlanRepositoryChangeActivity.ReasonMissingRepository)]
    [DataRow("cloneUrl", "http://github.com/octo/repo.git", RepositoryUrlShape.SchemeNotAllowed)]
    [DataRow("cloneUrl", "https://x:secret@github.com/octo/repo.git", RepositoryUrlShape.UrlComponentNotAllowed)]
    [DataRow("cloneUrl", "https://localhost/octo/repo.git", RepositoryUrlShape.HostNotAllowed)]
    [DataRow("cloneUrl", "https://github.com/octo", RepositoryUrlShape.PathNotAllowed)]
    public async Task A_push_the_service_cannot_use_is_ignored_with_its_reason(string input, string value, string reason)
    {
        // A non-null Definition.Parameters entry wins over the property, even when it is "".
        var activity = Push().WithParameters((input, value));

        var logs = await ActivityHarness.RunAsync(activity);

        Assert.AreEqual("ignore", activity.Action);
        Assert.AreEqual(reason, activity.Reason);
        Assert.IsNull(activity.EnsureBody);
        Assert.IsNull(activity.RetireBody);
        Assert.IsEmpty(activity.EnsureBodies);
        Assert.IsFalse(logs.Any(line => line.Contains("secret", StringComparison.Ordinal)));
        Assert.IsFalse(logs.Any(line => line.Contains("github.com", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("issues")]
    [DataRow("workflow_run")]
    public async Task An_unsupported_event_is_ignored(string eventName)
    {
        var activity = new SextantPlanRepositoryChangeActivity { Event = eventName, CloneUrl = Repo };

        await ActivityHarness.RunAsync(activity);

        Assert.AreEqual("ignore", activity.Action);
        Assert.AreEqual(SextantPlanRepositoryChangeActivity.ReasonUnsupportedEvent, activity.Reason);
    }

    [TestMethod]
    public async Task Kind_wins_over_event()
    {
        var activity = Push();
        activity.Kind = "DELETE";
        activity.Event = "push";

        await ActivityHarness.RunAsync(activity);

        Assert.AreEqual("retire", activity.Action);
        Assert.IsNull(AsRetireRequest(activity.RetireBody).ExpectedHeadCommit);
    }

    // ---- pull requests ----

    [TestMethod]
    [DataRow("opened")]
    [DataRow("synchronize")]
    [DataRow("reopened")]
    public async Task A_pull_request_publishes_base_then_head_without_moving_pointers(string action)
    {
        var activity = PullRequest(action);

        await ActivityHarness.RunAsync(activity);

        Assert.AreEqual("ensure", activity.Action);
        Assert.AreEqual(string.Empty, activity.Reason);
        Assert.HasCount(2, activity.EnsureBodies);
        Assert.AreSame(activity.EnsureBody, activity.EnsureBodies[0]);
        var baseSide = AsEnsureRequest(activity.EnsureBodies[0]);
        var headSide = AsEnsureRequest(activity.EnsureBodies[1]);
        Assert.AreEqual((Repo, Before, "main"), (baseSide.RepositoryRemoteUrl, baseSide.CommitSha, baseSide.BranchName));
        Assert.AreEqual((Fork, After, "feature/x"), (headSide.RepositoryRemoteUrl, headSide.CommitSha, headSide.BranchName));
        foreach (var request in new[] { baseSide, headSide })
        {
            Assert.AreEqual("none", request.BranchUpdate);
            Assert.IsNull(request.ExpectedHeadCommit);
            Assert.IsNull(request.IsDefaultBranch);
            Assert.IsNull(request.Forced);
        }
    }

    [TestMethod]
    public async Task Definition_parameters_pull_request_event_suffix_names_the_action()
    {
        var metadata = ActivityHarness.Json($$"""
            {"event":"pull_request.synchronize","cloneUrl":"{{Repo}}","baseSha":"{{Before}}","baseRef":"main",
             "headSha":"{{After}}","headRef":"refs/heads/topic","headCloneUrl":"{{Repo}}"}
            """);
        var activity = new SextantPlanRepositoryChangeActivity().WithParameters(("metadata", metadata));

        await ActivityHarness.RunAsync(activity);

        Assert.AreEqual("ensure", activity.Action);
        Assert.HasCount(2, activity.EnsureBodies);
        Assert.AreEqual("topic", AsEnsureRequest(activity.EnsureBodies[1]).BranchName);
    }

    [TestMethod]
    public async Task A_pull_request_from_a_deleted_head_repository_publishes_the_base_only()
    {
        var activity = PullRequest();
        activity.HeadCloneUrl = string.Empty;

        await ActivityHarness.RunAsync(activity);

        Assert.AreEqual("ensure", activity.Action);
        Assert.AreEqual(SextantPlanRepositoryChangeActivity.ReasonHeadRepositoryDeleted, activity.Reason);
        Assert.HasCount(1, activity.EnsureBodies);
        Assert.AreEqual(Before, AsEnsureRequest(activity.EnsureBody).CommitSha);
    }

    [TestMethod]
    public async Task A_pull_request_side_with_a_bad_sha_or_url_is_skipped_with_its_reason()
    {
        var badHead = PullRequest();
        badHead.HeadSha = Zeros;
        var badHeadUrl = PullRequest();
        badHeadUrl.HeadCloneUrl = "git://github.com/fork/repo.git";
        var badBase = PullRequest();
        badBase.BaseSha = "short";

        await ActivityHarness.RunAsync(badHead);
        await ActivityHarness.RunAsync(badHeadUrl);
        await ActivityHarness.RunAsync(badBase);

        Assert.AreEqual(SextantPlanRepositoryChangeActivity.ReasonInvalidSha, badHead.Reason);
        Assert.AreEqual(Before, AsEnsureRequest(badHead.EnsureBody).CommitSha);
        Assert.AreEqual(RepositoryUrlShape.SchemeNotAllowed, badHeadUrl.Reason);
        Assert.HasCount(1, badHeadUrl.EnsureBodies);
        Assert.AreEqual(SextantPlanRepositoryChangeActivity.ReasonInvalidSha, badBase.Reason);
        Assert.AreEqual(After, AsEnsureRequest(badBase.EnsureBody).CommitSha);
    }

    [TestMethod]
    public async Task A_pull_request_with_no_usable_side_is_ignored()
    {
        var activity = PullRequest();
        activity.BaseSha = string.Empty;
        activity.HeadCloneUrl = string.Empty;

        await ActivityHarness.RunAsync(activity);

        Assert.AreEqual("ignore", activity.Action);
        Assert.AreEqual(SextantPlanRepositoryChangeActivity.ReasonHeadRepositoryDeleted, activity.Reason);
        Assert.IsEmpty(activity.EnsureBodies);
    }

    [TestMethod]
    public async Task A_pull_request_side_repeating_an_earlier_identity_is_sent_once()
    {
        var activity = PullRequest();
        activity.BaseSha = Mixed;
        activity.HeadCloneUrl = Repo;
        activity.HeadSha = Mixed.ToUpperInvariant();

        await ActivityHarness.RunAsync(activity);

        Assert.HasCount(1, activity.EnsureBodies);
        Assert.AreEqual("main", AsEnsureRequest(activity.EnsureBody).BranchName);
    }

    [TestMethod]
    public async Task An_invalid_pull_request_branch_name_is_left_out_but_the_side_is_published()
    {
        var activity = PullRequest();
        activity.HeadRef = "bad..ref";

        await ActivityHarness.RunAsync(activity);

        Assert.HasCount(2, activity.EnsureBodies);
        Assert.IsFalse(((IDictionary<string, object?>)activity.EnsureBodies[1]!).ContainsKey("branch_name"));
    }

    [TestMethod]
    [DataRow("closed", SextantPlanRepositoryChangeActivity.ReasonPullRequestClosed)]
    [DataRow("edited", SextantPlanRepositoryChangeActivity.ReasonUnsupportedAction)]
    [DataRow("", SextantPlanRepositoryChangeActivity.ReasonUnsupportedAction)]
    public async Task Other_pull_request_actions_are_ignored(string action, string reason)
    {
        var activity = PullRequest(action);

        await ActivityHarness.RunAsync(activity);

        Assert.AreEqual("ignore", activity.Action);
        Assert.AreEqual(reason, activity.Reason);
    }

    [TestMethod]
    public async Task A_pull_request_on_a_refused_base_repository_is_ignored()
    {
        var activity = PullRequest();
        activity.CloneUrl = "https://github.com:22/octo/repo.git";

        await ActivityHarness.RunAsync(activity);

        Assert.AreEqual("ignore", activity.Action);
        Assert.AreEqual(RepositoryUrlShape.UrlComponentNotAllowed, activity.Reason);
    }

    // ---- misc ----

    [TestMethod]
    public async Task Rerunning_resets_every_output()
    {
        var activity = Push();
        await ActivityHarness.RunAsync(activity);
        Assert.AreEqual("ensure", activity.Action);

        activity.Kind = "issues";
        await ActivityHarness.RunAsync(activity);

        Assert.AreEqual("ignore", activity.Action);
        Assert.IsNull(activity.EnsureBody);
        Assert.IsEmpty(activity.EnsureBodies);
        Assert.AreEqual(string.Empty, activity.Repository);
        Assert.AreEqual(string.Empty, activity.BranchName);
        Assert.IsFalse(activity.IsDefaultBranch);
    }

    [TestMethod]
    public async Task Logs_name_the_decision_only()
    {
        var logs = await ActivityHarness.RunAsync(Push(before: string.Empty));

        Assert.HasCount(1, logs);
        Assert.Contains("ensure (missing_before)", logs[0]);
        Assert.DoesNotContain("github", logs[0]);
        Assert.DoesNotContain(After, logs[0]);
    }

    [TestMethod]
    public void Branch_update_values_are_the_service_values()
    {
        CollectionAssert.AreEqual(
            new[] { BranchUpdateMode.Advance, BranchUpdateMode.None },
            new[] { ServiceRequests.Advance, ServiceRequests.None });
    }

    // Every ensure body the activity emits over a spread of events, for the table-wide invariants.
    private static async Task<List<IDictionary<string, object?>>> EveryEmittedBodyAsync()
    {
        var scenarios = new List<SextantPlanRepositoryChangeActivity>
        {
            Push(), Push(before: Zeros), Push(before: string.Empty), Push(before: "x"),
            PullRequest(), PullRequest("synchronize"), PullRequest("reopened"),
        };
        var main = Push();
        main.Ref = "refs/heads/main";
        main.Forced = true;
        scenarios.Add(main);
        var noDefault = Push(before: Zeros);
        noDefault.DefaultBranch = null;
        scenarios.Add(noDefault);

        var bodies = new List<IDictionary<string, object?>>();
        foreach (var scenario in scenarios)
        {
            await ActivityHarness.RunAsync(scenario);
            bodies.AddRange(scenario.EnsureBodies.Cast<IDictionary<string, object?>>());
        }
        Assert.HasCount(12, bodies);
        return bodies;
    }
}
