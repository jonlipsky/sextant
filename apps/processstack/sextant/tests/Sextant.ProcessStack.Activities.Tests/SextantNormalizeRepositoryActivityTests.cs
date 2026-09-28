namespace Sextant.ProcessStack.Activities.Tests;

[TestClass]
public sealed class SextantNormalizeRepositoryActivityTests
{
    [TestMethod]
    public async Task Direct_properties_clone_url_is_kept_verbatim()
    {
        var activity = new SextantNormalizeRepositoryActivity { CloneUrl = "https://github.com/Octo/Repo.git" };

        await ActivityHarness.RunAsync(activity);

        Assert.IsTrue(activity.Accepted);
        Assert.AreEqual("https://github.com/Octo/Repo.git", activity.RemoteUrl);
        Assert.AreEqual("https://github.com/octo/repo", activity.RepositoryKey);
        Assert.AreEqual("github.com", activity.Host);
        Assert.AreEqual("Octo", activity.RepositoryOwner);
        Assert.AreEqual("Repo", activity.RepositoryName);
        Assert.AreEqual(string.Empty, activity.Reason);
    }

    [TestMethod]
    public async Task Definition_parameters_owner_and_repo_build_the_clone_url()
    {
        var activity = new SextantNormalizeRepositoryActivity()
            .WithParameters(("owner", "Octo"), ("repo", ActivityHarness.Json("\"Repo\"")));

        await ActivityHarness.RunAsync(activity);

        Assert.IsTrue(activity.Accepted);
        Assert.AreEqual("https://github.com/Octo/Repo.git", activity.RemoteUrl);
        Assert.AreEqual("https://github.com/octo/repo", activity.RepositoryKey);
    }

    [TestMethod]
    public async Task Definition_parameters_win_over_bound_properties()
    {
        var activity = new SextantNormalizeRepositoryActivity { CloneUrl = "https://github.com/other/thing.git" }
            .WithParameters(("cloneUrl", "https://github.com/octo/repo.git"));

        await ActivityHarness.RunAsync(activity);

        Assert.AreEqual("https://github.com/octo/repo.git", activity.RemoteUrl);
    }

    [TestMethod]
    public async Task Clone_url_wins_over_html_url_and_owner_repo()
    {
        var activity = new SextantNormalizeRepositoryActivity
        {
            CloneUrl = "https://github.com/a/b.git",
            HtmlUrl = "https://github.com/c/d",
            Owner = "e",
            Repo = "f",
        };

        await ActivityHarness.RunAsync(activity);

        Assert.AreEqual("https://github.com/a/b.git", activity.RemoteUrl);
    }

    [TestMethod]
    [DataRow("https://github.com/Octo/Repo", "https://github.com/Octo/Repo.git")]
    [DataRow("https://github.com/Octo/Repo/", "https://github.com/Octo/Repo.git")]
    [DataRow("https://github.com/Octo/Repo.git", "https://github.com/Octo/Repo.git")]
    public async Task Html_url_gets_the_git_suffix_of_a_clone_url(string htmlUrl, string expected)
    {
        var activity = new SextantNormalizeRepositoryActivity { HtmlUrl = htmlUrl };

        await ActivityHarness.RunAsync(activity);

        Assert.IsTrue(activity.Accepted);
        Assert.AreEqual(expected, activity.RemoteUrl);
    }

    [TestMethod]
    public async Task An_ssh_clone_url_becomes_https()
    {
        var activity = new SextantNormalizeRepositoryActivity { CloneUrl = "git@github.com:Octo/Repo.git" };

        await ActivityHarness.RunAsync(activity);

        Assert.IsTrue(activity.Accepted);
        Assert.AreEqual("https://github.com/Octo/Repo.git", activity.RemoteUrl);
        Assert.AreEqual("https://github.com/octo/repo", activity.RepositoryKey);
    }

    [TestMethod]
    [DataRow("", "octo/repo", "https://github.com/octo/repo.git")]
    [DataRow("octo", "repo.git", "https://github.com/octo/repo.git")]
    public async Task Owner_and_repo_accept_a_combined_repo_and_a_git_suffix(string owner, string repo, string expected)
    {
        var activity = new SextantNormalizeRepositoryActivity { Owner = owner, Repo = repo };

        await ActivityHarness.RunAsync(activity);

        Assert.AreEqual(expected, activity.RemoteUrl);
    }

    [TestMethod]
    public async Task A_default_host_names_the_host_for_owner_and_repo()
    {
        var activity = new SextantNormalizeRepositoryActivity { Owner = "Octo", Repo = "Repo", DefaultHost = "GHE.Example.com" };

        await ActivityHarness.RunAsync(activity);

        Assert.AreEqual("https://ghe.example.com/Octo/Repo.git", activity.RemoteUrl);
        Assert.AreEqual("https://ghe.example.com/Octo/Repo", activity.RepositoryKey);
        Assert.AreEqual("ghe.example.com", activity.Host);
    }

    [TestMethod]
    public async Task Nothing_to_normalize_is_missing_repository()
    {
        var activity = new SextantNormalizeRepositoryActivity { Owner = "octo" };

        await ActivityHarness.RunAsync(activity);

        Assert.IsFalse(activity.Accepted);
        Assert.AreEqual(SextantNormalizeRepositoryActivity.MissingRepository, activity.Reason);
        Assert.AreEqual(string.Empty, activity.RemoteUrl);
        Assert.AreEqual(string.Empty, activity.RepositoryKey);
    }

    [TestMethod]
    [DataRow("http://github.com/octo/repo.git", RepositoryUrlShape.SchemeNotAllowed)]
    [DataRow("https://github.com:8443/octo/repo.git", RepositoryUrlShape.UrlComponentNotAllowed)]
    [DataRow("https://127.0.0.1/octo/repo.git", RepositoryUrlShape.HostNotAllowed)]
    [DataRow("https://github.com/octo/repo/tree/main", RepositoryUrlShape.PathNotAllowed)]
    public async Task A_url_the_service_would_refuse_is_not_accepted(string cloneUrl, string reason)
    {
        var activity = new SextantNormalizeRepositoryActivity { CloneUrl = cloneUrl };

        await ActivityHarness.RunAsync(activity);

        Assert.IsFalse(activity.Accepted);
        Assert.AreEqual(reason, activity.Reason);
        Assert.AreEqual(string.Empty, activity.RemoteUrl);
    }

    [TestMethod]
    public async Task A_refused_url_with_credentials_is_never_logged_or_returned()
    {
        var activity = new SextantNormalizeRepositoryActivity { CloneUrl = "https://x:hunter2@github.com/octo/repo.git" };

        var logs = await ActivityHarness.RunAsync(activity);

        Assert.IsFalse(activity.Accepted);
        Assert.AreEqual(RepositoryUrlShape.UrlComponentNotAllowed, activity.Reason);
        Assert.IsFalse(logs.Any(line => line.Contains("hunter2", StringComparison.Ordinal)));
        Assert.AreEqual(string.Empty, activity.RemoteUrl + activity.RepositoryKey);
    }

    [TestMethod]
    public async Task Rerunning_an_activity_instance_does_not_keep_stale_outputs()
    {
        var activity = new SextantNormalizeRepositoryActivity { CloneUrl = "https://github.com/octo/repo.git" };
        await ActivityHarness.RunAsync(activity);

        activity.CloneUrl = "http://github.com/octo/repo.git";
        await ActivityHarness.RunAsync(activity);

        Assert.IsFalse(activity.Accepted);
        Assert.AreEqual(string.Empty, activity.Host);
        Assert.AreEqual(string.Empty, activity.RepositoryOwner);
    }

    [TestMethod]
    [DataRow(null, true, "")]
    [DataRow("", true, "")]
    [DataRow("  ", true, "")]
    [DataRow("main", true, "main")]
    [DataRow(" feature/x ", true, "feature/x")]
    [DataRow("refs/heads/release/1.0", true, "release/1.0")]
    [DataRow("main..x", false, "")]
    [DataRow("-main", false, "")]
    [DataRow("HEAD", false, "")]
    [DataRow("a b", false, "")]
    [DataRow("refs/heads/", false, "")]
    public async Task A_branch_is_checked_with_the_chat_parser_rule(string? branch, bool valid, string name)
    {
        var activity = new SextantNormalizeRepositoryActivity { CloneUrl = "https://github.com/octo/repo.git", Branch = branch };

        var logs = await ActivityHarness.RunAsync(activity);

        Assert.AreEqual(valid, activity.BranchValid);
        Assert.AreEqual(name, activity.BranchName);
        Assert.IsTrue(activity.Accepted, "the branch verdict is independent of the URL verdict");
        if (!valid)
            Assert.IsFalse(logs.Any(line => line.Contains(branch!.Trim(), StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task A_branch_from_definition_parameters_is_checked()
    {
        var activity = new SextantNormalizeRepositoryActivity()
            .WithParameters(("cloneUrl", "https://github.com/octo/repo.git"), ("branch", "feature..x"));

        await ActivityHarness.RunAsync(activity);

        Assert.IsFalse(activity.BranchValid);
        Assert.AreEqual(string.Empty, activity.BranchName);
    }

    [TestMethod]
    public async Task Rerunning_resets_the_branch_verdict()
    {
        var activity = new SextantNormalizeRepositoryActivity { CloneUrl = "https://github.com/octo/repo.git", Branch = "main..x" };
        await ActivityHarness.RunAsync(activity);

        activity.Branch = null;
        await ActivityHarness.RunAsync(activity);

        Assert.IsTrue(activity.BranchValid);
        Assert.AreEqual(string.Empty, activity.BranchName);
    }
}
