using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sextant.ProcessStack.Activities.Tests;

[TestClass]
public sealed class SextantPlanLegacyImportActivityTests
{
    private const string AlphaUrl = "https://github.com/octo/alpha.git";

    private static string V1(string owner, string repo, string branch, string? cloneUrl = null) =>
        JsonSerializer.Serialize(new
        {
            owner,
            repo,
            branch,
            cloneUrl = cloneUrl ?? $"https://github.com/{owner}/{repo}.git",
            addedAt = "2026-01-01T00:00:00Z",
        });

    private static Dictionary<string, object?> Grant(string repository, string branch, string? resolvedBranch = null) =>
        ActivityHarness.Map(
            ("repository", repository),
            ("branch", branch),
            ("source", "self"),
            ("status", ActivityHarness.Map(("resolved_branch", resolvedBranch), ("snapshot_status", "complete"))));

    private static object GrantsBody(params Dictionary<string, object?>[] grants) =>
        ActivityHarness.Map(("grants", grants.Cast<object?>().ToList()), ("result_count", grants.Length));

    private static async Task<SextantPlanLegacyImportActivity> PlanAsync(
        object? facts, object? grants = null, object? attempts = null, object? retry = null, object? maxEntries = null)
    {
        var activity = new SextantPlanLegacyImportActivity
        {
            Facts = facts,
            Grants = grants ?? GrantsBody(),
            Attempts = attempts,
        };
        if (retry is not null)
            activity.RetryUnresolved = retry;
        if (maxEntries is not null)
            activity.MaxEntries = maxEntries;
        await ActivityHarness.RunAsync(activity);
        return activity;
    }

    private static List<string> Slugs(SextantPlanLegacyImportActivity activity) =>
        activity.Pending.Select(p => (string)((IDictionary<string, object?>)p!)["slug"]!).ToList();

    private static string Field(SextantPlanLegacyImportActivity activity, int index, string name) =>
        (string)((IDictionary<string, object?>)activity.Pending[index]!)[name]!;

    // ---- the plan ----

    [TestMethod]
    public async Task Live_entries_are_planned_in_slug_order_and_tombstones_are_not_counted()
    {
        var facts = ActivityHarness.Map(
            ("github.com/octo/beta@main", V1("octo", "beta", "main")),
            ("github.com/octo/alpha@feature/x", V1("Octo", "Alpha", "feature/x")),
            ("github.com/octo/gone@main", "null"),
            ("github.com/octo/gone@dev", null),
            ("github.com/octo/blank@dev", "  "));

        var activity = await PlanAsync(facts);

        CollectionAssert.AreEqual(
            new[] { "github.com/octo/alpha@feature/x", "github.com/octo/beta@main" }, Slugs(activity));
        Assert.AreEqual("Octo", Field(activity, 0, "owner"));
        Assert.AreEqual("Alpha", Field(activity, 0, "repo"));
        Assert.AreEqual("feature/x", Field(activity, 0, "branch"));
        Assert.AreEqual(3, activity.Tombstones);
        Assert.AreEqual(0, activity.Unreadable);
        Assert.AreEqual(0, activity.AlreadyImported);
        Assert.AreEqual(0, activity.Unresolved);
        Assert.AreEqual(0, activity.Remaining);
        Assert.AreEqual("{}", activity.AttemptsJson);
        Assert.AreEqual(SextantPlanLegacyImportActivity.MaxAttempts, activity.MaxAttemptsOutput);
    }

    [TestMethod]
    public async Task Json_facts_keep_keys_that_differ_only_by_branch_case()
    {
        // The memory store's keys are case-sensitive, and so is a branch: @Main and @main are two watches.
        var facts = ActivityHarness.Json($$"""
            {"github.com/octo/alpha@Main": {{JsonSerializer.Serialize(V1("octo", "alpha", "Main"))}},
             "github.com/octo/alpha@main": {{JsonSerializer.Serialize(V1("octo", "alpha", "main"))}}}
            """);

        var activity = await PlanAsync(facts);

        CollectionAssert.AreEqual(
            new[] { "github.com/octo/alpha@Main", "github.com/octo/alpha@main" }, Slugs(activity));
    }

    [TestMethod]
    public async Task Facts_arrive_as_json_text_a_json_node_or_definition_parameters()
    {
        var text = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["github.com/octo/alpha@Main"] = V1("octo", "alpha", "Main"),
            ["github.com/octo/alpha@main"] = V1("octo", "alpha", "main"),
        });
        var node = JsonNode.Parse(text)!;

        var fromText = await PlanAsync(text);
        var fromNode = await PlanAsync(node);
        var fromParameters = new SextantPlanLegacyImportActivity().WithParameters(
            ("facts", ActivityHarness.Json(text)),
            ("grants", ActivityHarness.Json("""{"grants":[]}""")),
            ("attempts", """{"github.com/octo/alpha@main":1}"""));
        await ActivityHarness.RunAsync(fromParameters);

        CollectionAssert.AreEqual(new[] { "github.com/octo/alpha@Main", "github.com/octo/alpha@main" }, Slugs(fromText));
        CollectionAssert.AreEqual(Slugs(fromText), Slugs(fromNode));
        CollectionAssert.AreEqual(new[] { "github.com/octo/alpha@Main", "github.com/octo/alpha@main" }, Slugs(fromParameters));
        Assert.AreEqual("""{"github.com/octo/alpha@main":1}""", fromParameters.AttemptsJson);
    }

    [TestMethod]
    public async Task An_authored_expression_parameter_falls_back_to_the_bound_property()
    {
        var activity = new SextantPlanLegacyImportActivity
        {
            Facts = ActivityHarness.Map(("k", V1("octo", "alpha", "main"))),
            Grants = GrantsBody(),
        }.WithParameters(("facts", "= facts"), ("grants", "= grantsJson"));

        await ActivityHarness.RunAsync(activity);

        CollectionAssert.AreEqual(new[] { "github.com/octo/alpha@main" }, Slugs(activity));
    }

    [TestMethod]
    public async Task Entries_naming_the_same_watch_are_planned_once()
    {
        var facts = ActivityHarness.Map(
            ("github.com/octo/alpha@main", V1("octo", "alpha", "main")),
            ("GitHub.com/Octo/Alpha.git@main", V1("OCTO", "alpha.git", " main ", "https://GitHub.com/Octo/Alpha")));

        var activity = await PlanAsync(facts);

        CollectionAssert.AreEqual(new[] { "github.com/octo/alpha@main" }, Slugs(activity));
        Assert.AreEqual(1, activity.Duplicates);
    }

    // ---- untrusted values ----

    [TestMethod]
    [DataRow("not json")]
    [DataRow("[1,2]")]
    [DataRow("42")]
    [DataRow("""{"owner":"octo","branch":"main"}""")]
    [DataRow("""{"repo":"alpha","branch":"main"}""")]
    [DataRow("""{"owner":"octo","repo":"alpha"}""")]
    [DataRow("""{"owner":"octo","repo":"alpha","branch":""}""")]
    [DataRow("""{"owner":"octo","repo":"alpha","branch":"a..b"}""")]
    [DataRow("""{"owner":"octo","repo":"alpha","branch":"refs/heads/main"}""")]
    [DataRow("""{"owner":"octo","repo":"alpha","branch":"-x"}""")]
    [DataRow("""{"owner":"evil.example/octo","repo":"alpha","branch":"main"}""")]
    [DataRow("""{"owner":"octo","repo":"alpha/extra","branch":"main"}""")]
    [DataRow("""{"owner":"git@evil.example:octo","repo":"alpha","branch":"main"}""")]
    [DataRow("""{"owner":"..","repo":"alpha","branch":"main"}""")]
    [DataRow("""{"owner":"octo","repo":"alpha","branch":"main","cloneUrl":"https://evil.example/octo/alpha.git"}""")]
    [DataRow("""{"owner":"octo","repo":"alpha","branch":"main","cloneUrl":"https://github.com/octo/other.git"}""")]
    [DataRow("""{"owner":"octo","repo":"alpha","branch":"main","cloneUrl":"git@evil.example:octo/alpha.git"}""")]
    [DataRow("""{"owner":"octo","repo":"alpha","branch":"main","cloneUrl":"https://github.com:8443/octo/alpha.git"}""")]
    public async Task An_unreadable_or_foreign_entry_is_counted_and_never_planned(string value)
    {
        var activity = await PlanAsync(ActivityHarness.Map(("k", value), ("ok", V1("octo", "beta", "main"))));

        CollectionAssert.AreEqual(new[] { "github.com/octo/beta@main" }, Slugs(activity));
        Assert.AreEqual(1, activity.Unreadable);
        Assert.AreEqual(0, activity.Tombstones);
    }

    [TestMethod]
    public async Task An_ssh_clone_url_for_the_same_repository_is_accepted()
    {
        var activity = await PlanAsync(ActivityHarness.Map(
            ("k", V1("octo", "alpha", "main", "git@github.com:octo/alpha.git")),
            ("m", """{"Owner":"octo","Repo":"beta","Branch":"main"}""")));

        CollectionAssert.AreEqual(new[] { "github.com/octo/alpha@main", "github.com/octo/beta@main" }, Slugs(activity));
        Assert.AreEqual(0, activity.Unreadable);
    }

    // ---- already imported ----

    [TestMethod]
    public async Task A_grant_on_the_branch_or_a_default_grant_resolving_to_it_covers_the_entry()
    {
        var facts = ActivityHarness.Map(
            ("a", V1("octo", "alpha", "main")),
            ("b", V1("octo", "alpha", "dev")),
            ("c", V1("octo", "beta", "main")),
            ("d", V1("octo", "gamma", "main")),
            ("e", V1("octo", "delta", "Main")));
        var grants = GrantsBody(
            Grant("https://github.com/Octo/Alpha", "", "main"),
            Grant("https://github.com/octo/beta.git", "main"),
            Grant("https://github.com/octo/gamma.git", ""),
            Grant("https://github.com/octo/delta.git", "main"));

        var activity = await PlanAsync(facts, grants);

        CollectionAssert.AreEqual(
            new[] { "github.com/octo/alpha@dev", "github.com/octo/delta@Main", "github.com/octo/gamma@main" },
            Slugs(activity));
        Assert.AreEqual(2, activity.AlreadyImported);
    }

    [TestMethod]
    public async Task The_grants_array_alone_is_accepted()
    {
        var activity = await PlanAsync(
            ActivityHarness.Map(("a", V1("octo", "alpha", "main"))),
            ActivityHarness.Json($$"""[{"repository":"{{AlphaUrl}}","branch":"main"}]"""));

        Assert.IsEmpty(activity.Pending);
        Assert.AreEqual(1, activity.AlreadyImported);
    }

    // ---- attempts ----

    [TestMethod]
    public async Task Marked_entries_go_last_and_unresolved_ones_are_left_out_unless_retried()
    {
        var facts = ActivityHarness.Map(
            ("a", V1("octo", "alpha", "main")),
            ("b", V1("octo", "beta", "main")),
            ("c", V1("octo", "gamma", "main")),
            ("d", V1("octo", "delta", "main")));
        var attempts = """
            {"github.com/octo/alpha@main":1,"github.com/octo/beta@main":2,"github.com/octo/delta@main":"x",
             "github.com/octo/gone@main":1,"github.com/octo/gamma@main":-3}
            """;

        var planned = await PlanAsync(facts, attempts: attempts);
        var retried = await PlanAsync(facts, attempts: attempts, retry: "true");

        CollectionAssert.AreEqual(
            new[] { "github.com/octo/delta@main", "github.com/octo/gamma@main", "github.com/octo/alpha@main" },
            Slugs(planned));
        Assert.AreEqual(1, planned.Unresolved);
        // Pruned to the entries still to import, in ordinal slug order.
        Assert.AreEqual("""{"github.com/octo/alpha@main":1,"github.com/octo/beta@main":2}""", planned.AttemptsJson);

        CollectionAssert.AreEqual(
            new[] { "github.com/octo/delta@main", "github.com/octo/gamma@main", "github.com/octo/alpha@main", "github.com/octo/beta@main" },
            Slugs(retried));
        Assert.AreEqual(0, retried.Unresolved);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("garbage")]
    [DataRow("[1]")]
    [DataRow("""{"github.com/octo/alpha@main":{"n":5}}""")]
    public async Task Unusable_attempts_read_as_empty(string? attempts)
    {
        var activity = await PlanAsync(ActivityHarness.Map(("a", V1("octo", "alpha", "main"))), attempts: attempts);

        CollectionAssert.AreEqual(new[] { "github.com/octo/alpha@main" }, Slugs(activity));
        Assert.AreEqual("{}", activity.AttemptsJson);
        Assert.AreEqual(0, activity.Unresolved);
    }

    [TestMethod]
    public async Task Attempts_of_imported_or_unreadable_entries_are_pruned()
    {
        var activity = await PlanAsync(
            ActivityHarness.Map(("a", V1("octo", "alpha", "main")), ("b", "not json")),
            GrantsBody(Grant(AlphaUrl, "main")),
            """{"github.com/octo/alpha@main":2}""");

        Assert.IsEmpty(activity.Pending);
        Assert.AreEqual("{}", activity.AttemptsJson);
        Assert.AreEqual(1, activity.AlreadyImported);
        Assert.AreEqual(1, activity.Unreadable);
    }

    // ---- limits ----

    [TestMethod]
    public async Task Max_entries_caps_the_plan_and_counts_the_rest()
    {
        var facts = ActivityHarness.Map(
            ("a", V1("octo", "alpha", "main")),
            ("b", V1("octo", "beta", "main")),
            ("c", V1("octo", "gamma", "main")));

        var activity = await PlanAsync(facts, maxEntries: "2");

        CollectionAssert.AreEqual(new[] { "github.com/octo/alpha@main", "github.com/octo/beta@main" }, Slugs(activity));
        Assert.AreEqual(1, activity.Remaining);
    }

    [TestMethod]
    public async Task No_facts_plan_nothing()
    {
        var activity = await PlanAsync(null, grants: "not json");

        Assert.IsEmpty(activity.Pending);
        Assert.AreEqual(0, activity.Unreadable + activity.Tombstones + activity.AlreadyImported);
    }

    [TestMethod]
    public async Task The_log_line_carries_counts_only()
    {
        var activity = new SextantPlanLegacyImportActivity
        {
            Facts = ActivityHarness.Map(("k", V1("secret-owner", "secret-repo", "secret-branch"))),
            Grants = GrantsBody(),
        };

        var logs = await ActivityHarness.RunAsync(activity);

        Assert.HasCount(1, logs);
        Assert.DoesNotContain("secret", logs[0]);
    }
}
