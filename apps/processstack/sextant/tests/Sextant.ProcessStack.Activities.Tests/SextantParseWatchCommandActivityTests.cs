namespace Sextant.ProcessStack.Activities.Tests;

[TestClass]
public sealed class SextantParseWatchCommandActivityTests
{
    private static async Task<SextantParseWatchCommandActivity> ParseAsync(string prompt, object? maxRepositories = null, string? defaultHost = null)
    {
        var activity = new SextantParseWatchCommandActivity { Prompt = prompt, MaxRepositories = maxRepositories };
        if (defaultHost is not null)
            activity.DefaultHost = defaultHost;
        await ActivityHarness.RunAsync(activity);
        return activity;
    }

    private static IDictionary<string, object?> Repository(SextantParseWatchCommandActivity activity, int index = 0) =>
        (IDictionary<string, object?>)activity.Repositories[index]!;

    private static string[] Keys(SextantParseWatchCommandActivity activity) =>
        activity.Repositories.Select(r => (string)((IDictionary<string, object?>)r!)["repositoryKey"]!).ToArray();

    // ---- dual input paths ----

    [TestMethod]
    public async Task Direct_properties_watch_a_repository_on_a_branch()
    {
        var activity = await ParseAsync("watch Octo/Repo on main");

        Assert.AreEqual("watch", activity.Verb);
        Assert.AreEqual("main", activity.Branch);
        Assert.IsEmpty(activity.Errors);
        Assert.HasCount(1, activity.Repositories);
        var repository = Repository(activity);
        Assert.AreEqual("Octo/Repo", repository["input"]);
        Assert.AreEqual("github.com", repository["host"]);
        Assert.AreEqual("Octo", repository["owner"]);
        Assert.AreEqual("Repo", repository["repo"]);
        Assert.AreEqual("https://github.com/octo/repo", repository["repositoryKey"]);
        Assert.AreEqual("https://github.com/Octo/Repo.git", repository["remoteUrl"]);
    }

    [TestMethod]
    public async Task Definition_parameters_prompt_host_and_limit()
    {
        var activity = new SextantParseWatchCommandActivity { Prompt = "help" }.WithParameters(
            ("prompt", ActivityHarness.Json("\"unwatch octo/a octo/b\"")),
            ("defaultHost", "GHE.Example.com"),
            ("maxRepositories", ActivityHarness.Json("1")));

        await ActivityHarness.RunAsync(activity);

        Assert.AreEqual("unwatch", activity.Verb);
        CollectionAssert.AreEqual(new[] { "https://ghe.example.com/octo/a" }, Keys(activity));
        Assert.AreEqual("https://ghe.example.com/octo/a.git", Repository(activity)["remoteUrl"]);
        Assert.HasCount(1, activity.Errors);
        Assert.Contains("At most 1 repositories", activity.Errors[0]);
    }

    // ---- verbs ----

    [TestMethod]
    [DataRow("watch octo/repo", "watch")]
    [DataRow("WATCH octo/repo", "watch")]
    [DataRow("track octo/repo", "watch")]
    [DataRow("start watching octo/repo", "watch")]
    [DataRow("unwatch octo/repo", "unwatch")]
    [DataRow("untrack octo/repo", "unwatch")]
    [DataRow("stop watching octo/repo", "unwatch")]
    [DataRow("Stop Tracking octo/repo", "unwatch")]
    public async Task Watch_and_unwatch_verbs(string prompt, string verb)
    {
        var activity = await ParseAsync(prompt);

        Assert.AreEqual(verb, activity.Verb);
        CollectionAssert.AreEqual(new[] { "https://github.com/octo/repo" }, Keys(activity));
        Assert.IsEmpty(activity.Errors);
    }

    [TestMethod]
    [DataRow("what am I watching?")]
    [DataRow("What am I tracking")]
    [DataRow("what repos am i watching")]
    [DataRow("list watched repos")]
    [DataRow("list my watched repositories")]
    [DataRow("list my watches")]
    [DataRow("show my watched repos")]
    [DataRow("show watched")]
    [DataRow("<@U0123> what am i watching?")]
    public async Task List_phrasings(string prompt)
    {
        var activity = await ParseAsync(prompt);

        Assert.AreEqual("list", activity.Verb);
        Assert.IsEmpty(activity.Repositories);
        Assert.IsEmpty(activity.Errors);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("?")]
    [DataRow("help")]
    [DataRow("hello there")]
    [DataRow("watching octo/repo")]
    [DataRow("please watch octo/repo")]
    [DataRow("@sextant")]
    public async Task Anything_else_is_help(string prompt)
    {
        var activity = await ParseAsync(prompt);

        Assert.AreEqual("help", activity.Verb);
        Assert.IsEmpty(activity.Repositories);
        Assert.IsEmpty(activity.Errors);
        Assert.AreEqual(string.Empty, activity.Branch);
    }

    [TestMethod]
    [DataRow("@sextant watch octo/repo")]
    [DataRow("<@U0123ABC> watch octo/repo.")]
    [DataRow("<@U0123ABC> @sextant   watch\tocto/repo!")]
    [DataRow("  watch   octo/repo  ")]
    public async Task Mentions_whitespace_and_punctuation_are_ignored(string prompt)
    {
        var activity = await ParseAsync(prompt);

        Assert.AreEqual("watch", activity.Verb);
        CollectionAssert.AreEqual(new[] { "https://github.com/octo/repo" }, Keys(activity));
        Assert.IsEmpty(activity.Errors);
    }

    // ---- branches ----

    [TestMethod]
    [DataRow("watch octo/repo", "")]
    [DataRow("watch octo/repo on develop", "develop")]
    [DataRow("watch octo/repo on branch release/1.2", "release/1.2")]
    [DataRow("watch octo/repo ON Feature/X", "Feature/X")]
    [DataRow("watch octo/repo on refs/heads/main", "main")]
    [DataRow("watch octo/repo on `main`", "main")]
    [DataRow("watch octo/repo on \"main\".", "main")]
    public async Task Branch_forms(string prompt, string branch)
    {
        var activity = await ParseAsync(prompt);

        Assert.AreEqual(branch, activity.Branch);
        Assert.IsFalse(activity.BranchInvalid);
        Assert.HasCount(1, activity.Repositories);
        Assert.IsEmpty(activity.Errors);
    }

    [TestMethod]
    public async Task Only_a_spaced_on_marks_the_branch()
    {
        var activity = await ParseAsync("watch octo/on on dev");

        CollectionAssert.AreEqual(new[] { "https://github.com/octo/on" }, Keys(activity));
        Assert.AreEqual("dev", activity.Branch);
        Assert.IsEmpty(activity.Errors);
    }

    [TestMethod]
    public async Task On_branch_wins_over_a_later_on()
    {
        var activity = await ParseAsync("watch octo/repo on branch hands-on");

        Assert.AreEqual("hands-on", activity.Branch);
        Assert.IsEmpty(activity.Errors);
    }

    [TestMethod]
    [DataRow("watch octo/repo on bad..name", "'bad..name' is not a valid branch name.")]
    [DataRow("watch octo/repo on HEAD", "'HEAD' is not a valid branch name.")]
    [DataRow("watch octo/repo on", "Name the branch after \"on\".")]
    [DataRow("watch octo/repo on branch", "Name the branch after \"on\".")]
    [DataRow("watch octo/repo on a b", "'a b' is not a valid branch name.")]
    [DataRow("stop watching octo/repo on bad..name", "'bad..name' is not a valid branch name.")]
    [DataRow("stop watching octo/repo on", "Name the branch after \"on\".")]
    public async Task An_invalid_branch_is_an_error(string prompt, string error)
    {
        var activity = await ParseAsync(prompt);

        // The branch stays "", so the flow must refuse on branchInvalid: "" alone would mean the default
        // branch (watch) or every branch (unwatch).
        Assert.AreEqual(string.Empty, activity.Branch);
        Assert.IsTrue(activity.BranchInvalid);
        CollectionAssert.AreEqual(new[] { error }, activity.Errors);
        Assert.HasCount(1, activity.Repositories);
    }

    [TestMethod]
    public async Task An_invalid_repository_does_not_mark_the_branch_invalid()
    {
        var activity = await ParseAsync("stop watching octo/repo not-a-repo");

        Assert.IsFalse(activity.BranchInvalid);
        Assert.HasCount(1, activity.Repositories);
        Assert.HasCount(1, activity.Errors);
    }

    // ---- repositories ----

    [TestMethod]
    public async Task Several_repositories_separated_by_commas_spaces_and_and()
    {
        var activity = await ParseAsync("watch octo/a, octo/b and octo/c & octo/d octo/e on main");

        CollectionAssert.AreEqual(
            new[]
            {
                "https://github.com/octo/a", "https://github.com/octo/b", "https://github.com/octo/c",
                "https://github.com/octo/d", "https://github.com/octo/e",
            },
            Keys(activity));
        Assert.AreEqual("main", activity.Branch);
    }

    [TestMethod]
    [DataRow("watch <https://github.com/Octo/Repo|github.com/Octo/Repo>")]
    [DataRow("watch <http://github.com/Octo/Repo|github.com/Octo/Repo>")]
    [DataRow("watch <https://github.com/Octo/Repo>")]
    [DataRow("watch https://github.com/Octo/Repo.git")]
    [DataRow("watch github.com/Octo/Repo")]
    [DataRow("watch git@github.com:Octo/Repo.git")]
    [DataRow("watch (Octo/Repo)")]
    [DataRow("watch 'Octo/Repo'")]
    public async Task Repository_reference_forms(string prompt)
    {
        var activity = await ParseAsync(prompt);

        Assert.IsEmpty(activity.Errors);
        Assert.AreEqual("https://github.com/Octo/Repo.git", Repository(activity)["remoteUrl"]);
        Assert.AreEqual("https://github.com/octo/repo", Repository(activity)["repositoryKey"]);
    }

    [TestMethod]
    public async Task A_slack_auto_link_is_what_was_typed()
    {
        var activity = await ParseAsync("watch <http://ghe.example.com/Team/Tool|ghe.example.com/Team/Tool> on main");

        Assert.IsEmpty(activity.Errors);
        Assert.AreEqual("ghe.example.com/Team/Tool", Repository(activity)["input"]);
        Assert.AreEqual("https://ghe.example.com/Team/Tool", Repository(activity)["repositoryKey"]);
        Assert.AreEqual("main", activity.Branch);
    }

    [TestMethod]
    public async Task A_labelled_slack_link_names_its_url_even_with_spaces_or_on_in_the_label()
    {
        var activity = await ParseAsync("watch <https://github.com/octo/real|the octo/other repo on dev> on main");

        Assert.IsEmpty(activity.Errors);
        CollectionAssert.AreEqual(new[] { "https://github.com/octo/real" }, Keys(activity));
        Assert.AreEqual("main", activity.Branch);
    }

    [TestMethod]
    public async Task A_plain_http_slack_link_is_still_refused()
    {
        var activity = await ParseAsync("watch <http://github.com/octo/repo>");

        Assert.IsEmpty(activity.Repositories);
        CollectionAssert.AreEqual(new[] { "Only https repository URLs are supported." }, activity.Errors);
    }

    [TestMethod]
    public async Task A_slack_escaped_ampersand_separates_repositories()
    {
        var activity = await ParseAsync("watch octo/a &amp; octo/b");

        Assert.IsEmpty(activity.Errors);
        CollectionAssert.AreEqual(new[] { "https://github.com/octo/a", "https://github.com/octo/b" }, Keys(activity));
    }

    [TestMethod]
    public async Task A_host_qualified_reference_keeps_its_host()
    {
        var activity = await ParseAsync("watch ghe.example.com/Team/Tool");

        Assert.AreEqual("ghe.example.com", Repository(activity)["host"]);
        Assert.AreEqual("https://ghe.example.com/Team/Tool", Repository(activity)["repositoryKey"]);
    }

    [TestMethod]
    public async Task Duplicate_spellings_are_named_once()
    {
        var activity = await ParseAsync("watch octo/repo Octo/Repo https://github.com/octo/repo.git octo/repo");

        CollectionAssert.AreEqual(new[] { "https://github.com/octo/repo" }, Keys(activity));
        Assert.AreEqual("octo/repo", Repository(activity)["input"]);
        Assert.IsEmpty(activity.Errors);
    }

    [TestMethod]
    public async Task Repositories_beyond_the_limit_are_left_out_with_one_error()
    {
        var names = string.Join(' ', Enumerable.Range(0, 12).Select(i => $"octo/r{i}"));

        var activity = await ParseAsync($"watch {names}");

        Assert.HasCount(SextantParseWatchCommandActivity.DefaultMaxRepositories, activity.Repositories);
        CollectionAssert.AreEqual(new[] { "At most 10 repositories per command; the rest were left out." }, activity.Errors);
    }

    [TestMethod]
    public async Task The_repository_limit_is_clamped()
    {
        var names = string.Join(' ', Enumerable.Range(0, 60).Select(i => $"octo/r{i}"));

        var activity = await ParseAsync($"watch {names}", maxRepositories: 500);

        Assert.HasCount(SextantParseWatchCommandActivity.MaxRepositoriesLimit, activity.Repositories);
        Assert.HasCount(1, activity.Errors);
    }

    [TestMethod]
    [DataRow("watch")]
    [DataRow("unwatch")]
    [DataRow("stop watching")]
    [DataRow("watch on main")]
    [DataRow("watch and , &")]
    public async Task A_command_without_a_repository_asks_for_one(string prompt)
    {
        var activity = await ParseAsync(prompt);

        Assert.AreNotEqual("help", activity.Verb);
        Assert.IsEmpty(activity.Repositories);
        CollectionAssert.AreEqual(new[] { $"Name a repository, for example: {activity.Verb} owner/repo on main." }, activity.Errors);
    }

    [TestMethod]
    public async Task An_http_url_is_refused()
    {
        var activity = await ParseAsync("watch http://github.com/octo/repo");

        Assert.IsEmpty(activity.Repositories);
        CollectionAssert.AreEqual(new[] { "Only https repository URLs are supported." }, activity.Errors);
    }

    [TestMethod]
    [DataRow("watch https://x:hunter2@github.com/octo/repo", "A repository URL must not carry credentials, a port, a query or a fragment.")]
    [DataRow("watch https://github.com:8443/octo/repo", "A repository URL must not carry credentials, a port, a query or a fragment.")]
    [DataRow("watch hunter2@evil", "That reference is not a repository; use owner/repo, host/owner/repo or an https URL.")]
    public async Task A_reference_that_may_carry_credentials_is_never_echoed(string prompt, string error)
    {
        var activity = await ParseAsync(prompt);

        Assert.IsEmpty(activity.Repositories);
        CollectionAssert.AreEqual(new[] { error }, activity.Errors);
    }

    [TestMethod]
    public async Task Other_bad_references_are_echoed_truncated()
    {
        var longName = new string('x', 80);

        var activity = await ParseAsync($"watch nope a/b/c {longName} octo/ok");

        CollectionAssert.AreEqual(new[] { "https://github.com/octo/ok" }, Keys(activity));
        CollectionAssert.AreEqual(
            new[]
            {
                "'nope' is not a repository; use owner/repo, host/owner/repo or an https URL.",
                "'a/b/c' is not a repository; use owner/repo, host/owner/repo or an https URL.",
                $"'{new string('x', 64)}…' is not a repository; use owner/repo, host/owner/repo or an https URL.",
            },
            activity.Errors);
    }

    [TestMethod]
    public async Task Errors_are_capped()
    {
        var bad = string.Join(' ', Enumerable.Range(0, 15).Select(i => $"bad{i}"));

        var activity = await ParseAsync($"watch {bad} on bad..branch");

        Assert.HasCount(SextantParseWatchCommandActivity.MaxErrors, activity.Errors);
        Assert.AreEqual("'bad..branch' is not a valid branch name.", activity.Errors[0]);
    }

    [TestMethod]
    public async Task Rerunning_resets_the_outputs()
    {
        var activity = await ParseAsync("watch octo/repo on dev");

        activity.Prompt = "help";
        await ActivityHarness.RunAsync(activity);

        Assert.AreEqual("help", activity.Verb);
        Assert.IsEmpty(activity.Repositories);
        Assert.AreEqual(string.Empty, activity.Branch);
        Assert.IsEmpty(activity.Errors);
    }

    [TestMethod]
    public async Task Logs_never_echo_the_message()
    {
        var activity = new SextantParseWatchCommandActivity { Prompt = "watch octo/secret-project on main" };

        var logs = await ActivityHarness.RunAsync(activity);

        Assert.HasCount(1, logs);
        Assert.Contains("watch, 1 repository, 0 error(s)", logs[0]);
        Assert.DoesNotContain("secret-project", logs[0]);
    }
}
