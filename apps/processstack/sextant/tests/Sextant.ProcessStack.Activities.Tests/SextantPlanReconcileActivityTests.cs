using System.Text.Json;
using Sextant.Service;

namespace Sextant.ProcessStack.Activities.Tests;

[TestClass]
public sealed class SextantPlanReconcileActivityTests
{
    private const string RepoA = "https://github.com/octo/alpha.git";
    private const string RepoB = "https://github.com/octo/beta.git";
    private const string Old = "1111111111111111111111111111111111111111";
    private const string New = "2222222222222222222222222222222222222222";
    private const string Other = "3333333333333333333333333333333333333333";

    private static Dictionary<string, object?> Target(string repository, string branch, int? code, object? resolve = null) =>
        ActivityHarness.Map(("repository", repository), ("branch", branch), ("resolveStatusCode", code), ("resolve", resolve));

    private static Dictionary<string, object?> Resolve(string? commit, string branch, bool isDefault = false) =>
        ActivityHarness.Map(("status", "complete"), ("commit_sha", commit), ("branch", branch), ("is_default", isDefault));

    private static Dictionary<string, object?> Listing(string repository, string? defaultBranch, params (string Name, string Sha)[] branches) =>
        ActivityHarness.Map(
            ("repository", repository),
            ("defaultBranch", defaultBranch),
            ("branches", branches.Select(b => (object?)ActivityHarness.Map(("name", b.Name), ("sha", b.Sha))).ToList()));

    private static async Task<SextantPlanReconcileActivity> PlanAsync(
        IEnumerable<object?> targets, IEnumerable<object?> listings, object? maxEnsures = null, object? maxRetires = null,
        object? maxTargets = null)
    {
        var activity = new SextantPlanReconcileActivity
        {
            ServiceTargets = targets.ToList(),
            GithubBranches = listings.ToList(),
        };
        if (maxEnsures is not null)
            activity.MaxEnsures = maxEnsures;
        if (maxRetires is not null)
            activity.MaxRetires = maxRetires;
        if (maxTargets is not null)
            activity.MaxTargets = maxTargets;
        await ActivityHarness.RunAsync(activity);
        return activity;
    }

    private static string SkipReason(SextantPlanReconcileActivity activity, int index = 0) =>
        (string)((IDictionary<string, object?>)activity.SkippedTargets[index]!)["reason"]!;

    private static EnsureSnapshotRequest AsEnsureRequest(object? body) =>
        JsonSerializer.Deserialize<EnsureSnapshotRequest>(JsonSerializer.Serialize(body), ServiceJson.Options)!;

    private static RetireBranchRequest AsRetireRequest(object? body) =>
        JsonSerializer.Deserialize<RetireBranchRequest>(JsonSerializer.Serialize(body), ServiceJson.Options)!;

    // ---- dual input paths ----

    [TestMethod]
    public async Task Direct_properties_a_stale_default_branch_is_advanced_under_the_cas()
    {
        var activity = await PlanAsync(
            [Target(RepoA, string.Empty, 200, Resolve(Old, "main", isDefault: true))],
            [Listing(RepoA, "main", ("main", New))]);

        Assert.HasCount(1, activity.Ensures);
        Assert.IsEmpty(activity.Retires);
        Assert.IsFalse(activity.Truncated);
        Assert.AreEqual(0, activity.Skipped);
        var request = AsEnsureRequest(activity.Ensures[0]);
        Assert.AreEqual(RepoA, request.RepositoryRemoteUrl);
        Assert.AreEqual(New, request.CommitSha);
        Assert.AreEqual("main", request.BranchName);
        Assert.IsTrue(request.IsDefaultBranch);
        Assert.AreEqual(Old, request.ExpectedHeadCommit);
        Assert.AreEqual("advance", request.BranchUpdate);
        Assert.IsNull(request.BranchHeadSequence);
        Assert.IsNull(request.BranchGuardProblem());
    }

    [TestMethod]
    public async Task Definition_parameters_json_inputs_plan_the_same_calls()
    {
        var targets = ActivityHarness.Json($$$"""
            [{"repository":"{{{RepoA}}}","branch":"","resolveStatusCode":200,
              "resolve":{"commit_sha":"{{{Old}}}","branch":"main","is_default":true}},
             {"repository":"{{{RepoA}}}","branch":"gone","resolveStatusCode":200,
              "resolve":{"commit_sha":"{{{Other}}}","branch":"gone","is_default":false}}]
            """);
        var listings = ActivityHarness.Json($$$"""
            [{"repository":"{{{RepoA}}}","defaultBranch":"main","branches":[{"name":"main","commit":{"sha":"{{{New}}}"}}]}]
            """);
        var activity = new SextantPlanReconcileActivity()
            .WithParameters(("serviceTargets", targets), ("githubBranches", listings), ("maxEnsures", "5"));

        await ActivityHarness.RunAsync(activity);

        Assert.HasCount(1, activity.Ensures);
        Assert.AreEqual(Old, AsEnsureRequest(activity.Ensures[0]).ExpectedHeadCommit);
        Assert.HasCount(1, activity.Retires);
        var retire = AsRetireRequest(activity.Retires[0]);
        Assert.AreEqual((RepoA, "gone", Other), (retire.Repository, retire.Branch, retire.ExpectedHeadCommit));
    }

    [TestMethod]
    public async Task Definition_parameters_accept_json_text()
    {
        var activity = new SextantPlanReconcileActivity().WithParameters(
            ("serviceTargets", $$"""[{"repository":"{{RepoA}}","branch":"dev","resolveStatusCode":404}]"""),
            ("githubBranches", $$"""[{"repository":"{{RepoA}}","defaultBranch":"main","branches":[{"name":"dev","sha":"{{New}}"}]}]"""));

        await ActivityHarness.RunAsync(activity);

        Assert.HasCount(1, activity.Ensures);
        Assert.AreEqual("", AsEnsureRequest(activity.Ensures[0]).ExpectedHeadCommit);
        Assert.IsFalse(AsEnsureRequest(activity.Ensures[0]).IsDefaultBranch);
    }

    // ---- a branch GitHub has ----

    [TestMethod]
    public async Task A_branch_at_the_github_head_is_up_to_date()
    {
        var activity = await PlanAsync(
            [Target(RepoA, "main", 200, Resolve(New.ToUpperInvariant(), "main"))],
            [Listing(RepoA, "main", ("main", New))]);

        Assert.AreEqual(1, activity.UpToDate);
        Assert.IsEmpty(activity.Ensures);
        Assert.AreEqual(0, activity.Skipped);
    }

    [TestMethod]
    public async Task A_branch_the_service_has_never_seen_is_created_under_an_empty_cas()
    {
        var activity = await PlanAsync([Target(RepoA, "dev", 404)], [Listing(RepoA, "main", ("dev", New))]);

        var request = AsEnsureRequest(activity.Ensures.Single());
        Assert.AreEqual("", request.ExpectedHeadCommit);
        Assert.AreEqual("dev", request.BranchName);
        Assert.IsFalse(request.IsDefaultBranch);
    }

    [TestMethod]
    public async Task An_unknown_default_leaves_default_branch_out()
    {
        var activity = await PlanAsync([Target(RepoA, "dev", 404)], [Listing(RepoA, null, ("dev", New))]);

        Assert.IsFalse(((IDictionary<string, object?>)activity.Ensures.Single()!).ContainsKey("default_branch"));
    }

    [TestMethod]
    [DataRow(500, SextantPlanReconcileActivity.ReasonResolveFailed)]
    [DataRow(401, SextantPlanReconcileActivity.ReasonResolveFailed)]
    [DataRow(null, SextantPlanReconcileActivity.ReasonResolveFailed)]
    [DataRow(200, SextantPlanReconcileActivity.ReasonResolveMissingCommit)]
    public async Task A_present_branch_without_a_usable_resolve_is_skipped(int? code, string reason)
    {
        var activity = await PlanAsync(
            [Target(RepoA, "main", code, code == 200 ? Resolve(null, "main") : null)],
            [Listing(RepoA, "main", ("main", New))]);

        Assert.IsEmpty(activity.Ensures);
        Assert.AreEqual(reason, SkipReason(activity));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("abc")]
    [DataRow("0000000000000000000000000000000000000000")]
    public async Task An_invalid_github_head_is_skipped(string head)
    {
        var activity = await PlanAsync([Target(RepoA, "main", 404)], [Listing(RepoA, "main", ("main", head))]);

        Assert.IsEmpty(activity.Ensures);
        Assert.AreEqual(SextantPlanReconcileActivity.ReasonInvalidHead, SkipReason(activity));
    }

    [TestMethod]
    public async Task A_changed_github_default_is_created_and_promoted_under_an_empty_cas()
    {
        // The service's default is `develop`; GitHub's is now `main`, resolved by name: 404, so the ensure
        // creates the pointer and marks it default.
        var activity = await PlanAsync([Target(RepoA, string.Empty, 404)], [Listing(RepoA, "main", ("main", New))]);

        var request = AsEnsureRequest(activity.Ensures.Single());
        Assert.AreEqual(("main", New, ""), (request.BranchName, request.CommitSha, request.ExpectedHeadCommit));
        Assert.IsTrue(request.IsDefaultBranch);
        Assert.AreEqual("advance", request.BranchUpdate);
    }

    [TestMethod]
    public async Task A_resolve_answered_for_another_branch_is_skipped()
    {
        // The flow resolved without a branch: the service's default is `trunk`, GitHub's is `main`, so the
        // resolve says nothing about main.
        var activity = await PlanAsync(
            [Target(RepoA, string.Empty, 200, Resolve(Old, "trunk", isDefault: true))],
            [Listing(RepoA, "main", ("main", New))]);

        Assert.IsEmpty(activity.Ensures);
        Assert.AreEqual(SextantPlanReconcileActivity.ReasonResolveBranchMismatch, SkipReason(activity));
    }

    // ---- a branch GitHub no longer has ----

    [TestMethod]
    public async Task A_deleted_branch_is_retired_under_the_cas_on_the_service_head()
    {
        var activity = await PlanAsync(
            [Target(RepoA, "refs/heads/gone", 200, Resolve(Old, "gone"))],
            [Listing(RepoA, "main", ("main", New))]);

        var retire = AsRetireRequest(activity.Retires.Single());
        Assert.AreEqual(RepoA, retire.Repository);
        Assert.AreEqual("gone", retire.Branch);
        Assert.AreEqual(Old, retire.ExpectedHeadCommit);
        Assert.IsEmpty(activity.Ensures);
    }

    [TestMethod]
    public async Task An_absent_default_target_is_never_retired()
    {
        var activity = await PlanAsync(
            [Target(RepoA, string.Empty, 200, Resolve(Old, "main", isDefault: true))],
            [Listing(RepoA, "main", ("dev", New))]);

        Assert.IsEmpty(activity.Retires);
        Assert.AreEqual(SextantPlanReconcileActivity.ReasonDefaultBranchAbsent, SkipReason(activity));
    }

    [TestMethod]
    [DataRow(true, "other")]
    [DataRow(false, "main")]
    public async Task An_absent_branch_the_service_or_github_calls_default_is_never_retired(bool serviceDefault, string githubDefault)
    {
        var activity = await PlanAsync(
            [Target(RepoA, "main", 200, Resolve(Old, "main", isDefault: serviceDefault))],
            [Listing(RepoA, githubDefault, ("dev", New))]);

        Assert.IsEmpty(activity.Retires);
        Assert.AreEqual(SextantPlanReconcileActivity.ReasonDefaultBranch, SkipReason(activity));
    }

    [TestMethod]
    [DataRow("truncated", true)]
    [DataRow("complete", false)]
    public async Task An_incomplete_listing_never_retires(string flag, bool value)
    {
        var listing = Listing(RepoA, "main", ("main", New));
        listing[flag] = value;

        var activity = await PlanAsync([Target(RepoA, "gone", 200, Resolve(Old, "gone"))], [listing]);

        Assert.IsEmpty(activity.Retires);
        Assert.AreEqual(SextantPlanReconcileActivity.ReasonBranchListingIncomplete, SkipReason(activity));
    }

    [TestMethod]
    [DataRow(404, SextantPlanReconcileActivity.ReasonBranchAbsent)]
    [DataRow(503, SextantPlanReconcileActivity.ReasonResolveFailed)]
    [DataRow(null, SextantPlanReconcileActivity.ReasonResolveFailed)]
    public async Task An_absent_branch_without_a_service_head_is_skipped(int? code, string reason)
    {
        var activity = await PlanAsync([Target(RepoA, "gone", code)], [Listing(RepoA, "main", ("main", New))]);

        Assert.IsEmpty(activity.Retires);
        Assert.AreEqual(reason, SkipReason(activity));
    }

    [TestMethod]
    [DataRow((string?)null)]
    [DataRow("abc")]
    [DataRow("0000000000000000000000000000000000000000")]
    public async Task An_absent_branch_whose_resolve_has_no_commit_is_skipped(string? commit)
    {
        var activity = await PlanAsync(
            [Target(RepoA, "gone", 200, Resolve(commit, "gone"))], [Listing(RepoA, "main", ("main", New))]);

        Assert.IsEmpty(activity.Retires);
        Assert.AreEqual(SextantPlanReconcileActivity.ReasonResolveMissingCommit, SkipReason(activity));
    }

    // ---- targets that cannot be planned ----

    [TestMethod]
    public async Task Unplannable_targets_are_skipped_with_their_reason()
    {
        var unlisted = ActivityHarness.Map(("repository", RepoB), ("defaultBranch", "main"));
        var activity = await PlanAsync(
            [
                Target(string.Empty, "main", 404),
                Target(RepoA, "bad..name", 404),
                Target(RepoB, "main", 404),
                Target("https://github.com/octo/gamma.git", "main", 404),
                Target("https://github.com/octo/delta.git", string.Empty, 404),
            ],
            [unlisted, Listing("https://github.com/octo/delta.git", null, ("main", New))]);

        Assert.IsEmpty(activity.Ensures);
        Assert.AreEqual(5, activity.Skipped);
        var reasons = activity.SkippedTargets
            .Cast<IDictionary<string, object?>>()
            .ToDictionary(s => $"{s["repository"]}|{s["branch"]}", s => (string)s["reason"]!);
        Assert.AreEqual(SextantPlanReconcileActivity.ReasonMissingRepository, reasons["|main"]);
        Assert.AreEqual(SextantPlanReconcileActivity.ReasonInvalidBranch, reasons[$"{RepoA}|bad..name"]);
        Assert.AreEqual(SextantPlanReconcileActivity.ReasonRepositoryNotListed, reasons[$"{RepoB}|main"]);
        Assert.AreEqual(SextantPlanReconcileActivity.ReasonRepositoryNotListed, reasons["https://github.com/octo/gamma.git|main"]);
        Assert.AreEqual(SextantPlanReconcileActivity.ReasonDefaultBranchUnknown, reasons["https://github.com/octo/delta.git|"]);
    }

    [TestMethod]
    public async Task Spellings_of_one_repository_and_branch_are_planned_once()
    {
        var activity = await PlanAsync(
            [
                Target("https://github.com/Octo/Alpha", "main", 404),
                Target(RepoA, "main", 404),
                Target(RepoA, string.Empty, 404),
            ],
            [Listing("https://github.com/OCTO/alpha/", "main", ("main", New))]);

        Assert.HasCount(1, activity.Ensures);
        Assert.AreEqual(2, activity.Skipped);
        Assert.IsTrue(activity.SkippedTargets.Cast<IDictionary<string, object?>>()
            .All(s => (string)s["reason"]! == SextantPlanReconcileActivity.ReasonDuplicate && (string)s["branch"]! == "main"));
    }

    // ---- listing forms ----

    [TestMethod]
    public async Task Flat_listing_records_merge_per_repository()
    {
        var activity = await PlanAsync(
            [Target(RepoA, string.Empty, 404), Target(RepoA, "gone", 200, Resolve(Old, "gone"))],
            [
                ActivityHarness.Map(("repository", "git@github.com:octo/alpha.git"), ("name", "main"), ("sha", New), ("isDefault", true)),
                ActivityHarness.Map(("repository", RepoA), ("name", "dev"), ("commit", ActivityHarness.Map(("sha", Other)))),
            ]);

        var ensure = AsEnsureRequest(activity.Ensures.Single());
        Assert.AreEqual(("main", New, true), (ensure.BranchName, ensure.CommitSha, ensure.IsDefaultBranch));
        Assert.AreEqual("gone", AsRetireRequest(activity.Retires.Single()).Branch);
    }

    [TestMethod]
    public async Task A_listing_with_unlisted_branches_is_unreachable()
    {
        var listing = ActivityHarness.Map(("repository", RepoA), ("defaultBranch", "main"), ("branches", null));

        var activity = await PlanAsync([Target(RepoA, "gone", 200, Resolve(Old, "gone"))], [listing]);

        Assert.IsEmpty(activity.Retires);
        Assert.AreEqual(SextantPlanReconcileActivity.ReasonRepositoryNotListed, SkipReason(activity));
    }

    [TestMethod]
    public async Task One_record_per_target_can_serve_as_both_inputs()
    {
        var record = Target(RepoA, "dev", 200, Resolve(Old, "dev"));
        record["defaultBranch"] = "main";
        record["branches"] = new List<object?> { ActivityHarness.Map(("name", "dev"), ("commitSha", New)) };

        var activity = await PlanAsync([record], [record]);

        Assert.AreEqual(Old, AsEnsureRequest(activity.Ensures.Single()).ExpectedHeadCommit);
    }

    [TestMethod]
    public async Task Target_aliases_are_accepted()
    {
        var target = ActivityHarness.Map(
            ("repository_remote_url", RepoA), ("branch", "dev"), ("statusCode", "200"),
            ("resolveBody", $$"""{"commit_sha":"{{Old}}","branch":"dev"}"""));

        var activity = await PlanAsync([target], [Listing(RepoA, "main", ("dev", New))]);

        Assert.AreEqual(Old, AsEnsureRequest(activity.Ensures.Single()).ExpectedHeadCommit);
    }

    // ---- limits and determinism ----

    [TestMethod]
    public async Task Ensures_beyond_the_limit_are_left_for_the_next_run()
    {
        var targets = Enumerable.Range(0, 3).Select(i => (object?)Target(RepoA, $"b{i}", 404)).ToList();
        var listing = Listing(RepoA, "main", ("b0", New), ("b1", New), ("b2", New));

        var activity = await PlanAsync(targets, [listing], maxEnsures: 2);

        CollectionAssert.AreEqual(new[] { "b0", "b1" }, activity.Ensures.Select(e => AsEnsureRequest(e).BranchName).ToArray());
        Assert.IsTrue(activity.Truncated);
        Assert.AreEqual(SextantPlanReconcileActivity.ReasonEnsureLimit, SkipReason(activity));
        Assert.AreEqual(0, activity.TruncatedTargets);
    }

    [TestMethod]
    public async Task Retires_beyond_the_limit_are_left_for_the_next_run()
    {
        var targets = Enumerable.Range(0, 3).Select(i => (object?)Target(RepoA, $"g{i}", 200, Resolve(Old, $"g{i}"))).ToList();

        var activity = await PlanAsync(targets, [Listing(RepoA, "main", ("main", New))], maxRetires: "1");

        Assert.AreEqual("g0", AsRetireRequest(activity.Retires.Single()).Branch);
        Assert.IsTrue(activity.Truncated);
        Assert.AreEqual(2, activity.Skipped);
        Assert.AreEqual(SextantPlanReconcileActivity.ReasonRetireLimit, SkipReason(activity, 1));
    }

    [TestMethod]
    public async Task Targets_beyond_the_limit_are_counted_not_planned()
    {
        var targets = Enumerable.Range(0, 5).Select(i => (object?)Target(RepoA, $"b{i}", 404)).ToList();

        var activity = await PlanAsync(targets, [Listing(RepoA, "main", ("b0", New), ("b4", New))], maxTargets: 2);

        Assert.AreEqual(3, activity.TruncatedTargets);
        Assert.IsTrue(activity.Truncated);
        Assert.HasCount(1, activity.Ensures);
        Assert.AreEqual("b0", AsEnsureRequest(activity.Ensures[0]).BranchName);
        Assert.AreEqual(1, activity.Skipped);
    }

    [TestMethod]
    public async Task A_zero_limit_plans_nothing()
    {
        var activity = await PlanAsync([Target(RepoA, "dev", 404)], [Listing(RepoA, "main", ("dev", New))], maxEnsures: 0);

        Assert.IsEmpty(activity.Ensures);
        Assert.IsTrue(activity.Truncated);
    }

    [TestMethod]
    [DataRow(null, 100)]
    [DataRow("", 100)]
    [DataRow("x", 100)]
    [DataRow(-1, 100)]
    [DataRow(0, 0)]
    [DataRow(7, 7)]
    [DataRow("7", 7)]
    [DataRow(1000, 1000)]
    [DataRow(1001, 1000)]
    [DataRow(long.MaxValue, 1000)]
    public void Limits_default_and_clamp(object? requested, int expected)
    {
        Assert.AreEqual(expected, ActivityValues.Limit(requested, SextantPlanReconcileActivity.DefaultMaxEnsures, SextantPlanReconcileActivity.MaxEnsuresLimit));
    }

    [TestMethod]
    public void The_limit_defaults_are_the_documented_ones()
    {
        var activity = new SextantPlanReconcileActivity();

        Assert.AreEqual(1000, ActivityValues.Limit(activity.MaxTargets, SextantPlanReconcileActivity.DefaultMaxTargets, SextantPlanReconcileActivity.MaxTargetsLimit));
        Assert.AreEqual((1000, 100, 20), (SextantPlanReconcileActivity.DefaultMaxTargets, SextantPlanReconcileActivity.DefaultMaxEnsures, SextantPlanReconcileActivity.DefaultMaxRetires));
        Assert.AreEqual((10_000, 1000, 1000), (SextantPlanReconcileActivity.MaxTargetsLimit, SextantPlanReconcileActivity.MaxEnsuresLimit, SextantPlanReconcileActivity.MaxRetiresLimit));
    }

    [TestMethod]
    public async Task The_plan_does_not_depend_on_input_order()
    {
        var targets = new List<object?>
        {
            Target(RepoB, "main", 200, Resolve(Old, "main", isDefault: true)),
            Target(RepoA, "dev", 404),
            Target(RepoA, "gone", 200, Resolve(Other, "gone")),
            Target(RepoA, string.Empty, 200, Resolve(New, "main", isDefault: true)),
            Target("https://github.com/octo/Alpha", "dev", 404),
            Target(RepoB, "old", 200, Resolve(Old, "old")),
        };
        var listings = new List<object?>
        {
            Listing(RepoA, "main", ("main", New), ("dev", New)),
            Listing(RepoB, "main", ("main", New)),
        };

        var expected = Snapshot(await PlanAsync(targets, listings, maxRetires: 1));
        for (var seed = 1; seed <= 5; seed++)
        {
            var random = new Random(seed);
            var shuffledTargets = targets.OrderBy(_ => random.Next()).ToList();
            var shuffledListings = listings.OrderBy(_ => random.Next()).ToList();
            Assert.AreEqual(expected, Snapshot(await PlanAsync(shuffledTargets, shuffledListings, maxRetires: 1)), $"seed {seed}");
        }
        StringAssertContainsAll(expected, "\"ensures\"", "retire_limit", "duplicate");
    }

    [TestMethod]
    public async Task Rerunning_replaces_the_previous_plan()
    {
        var activity = await PlanAsync([Target(RepoA, "dev", 404)], [Listing(RepoA, "main", ("dev", New))]);
        Assert.HasCount(1, activity.Ensures);

        activity.ServiceTargets = new List<object?>();
        await ActivityHarness.RunAsync(activity);

        Assert.IsEmpty(activity.Ensures);
        Assert.IsEmpty(activity.SkippedTargets);
        Assert.AreEqual(0, activity.UpToDate);
        Assert.IsFalse(activity.Truncated);
    }

    [TestMethod]
    public async Task Logs_carry_counts_only()
    {
        var activity = new SextantPlanReconcileActivity
        {
            ServiceTargets = new List<object?> { Target(RepoA, "dev", 404) },
            GithubBranches = new List<object?> { Listing(RepoA, "main", ("dev", New)) },
        };

        var logs = await ActivityHarness.RunAsync(activity);

        Assert.HasCount(1, logs);
        Assert.Contains("1 ensure(s), 0 retire(s), 0 up to date, 0 skipped", logs[0]);
        Assert.DoesNotContain("github", logs[0]);
    }

    private static string Snapshot(SextantPlanReconcileActivity activity) => JsonSerializer.Serialize(new
    {
        ensures = activity.Ensures,
        retires = activity.Retires,
        skipped = activity.SkippedTargets,
        activity.UpToDate,
        activity.Truncated,
        activity.TruncatedTargets,
    });

    private static void StringAssertContainsAll(string value, params string[] parts)
    {
        foreach (var part in parts)
            Assert.Contains(part, value);
    }
}
