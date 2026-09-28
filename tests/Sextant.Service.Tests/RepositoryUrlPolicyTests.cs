namespace Sextant.Service.Tests;

/// <summary>
/// SVC-5: the pure SSRF policy the service applies to an UNTRUSTED top-level repository URL at
/// <c>POST /control/ensure</c> intake (and, later, grants/retire/the <c>repository</c> tool argument): https
/// only, no userinfo/query/fragment/non-443 port, a multi-label non-IP non-localhost DNS host on the allow-list,
/// a path of exactly <c>/{owner}/{repo}[.git]</c>, and an optional <c>host/owner</c> allow-list.
/// </summary>
[TestClass]
public class RepositoryUrlPolicyTests
{
    private static RepositoryUrlPolicy AnyHost => new([RepositoryUrlPolicy.Wildcard]);

    [TestMethod]
    [DataRow("https://github.com/org/app", "https://github.com/org/app", "org", "app")]
    [DataRow("https://github.com/org/app.git", "https://github.com/org/app", "org", "app")]
    [DataRow("https://github.com/org/app.GIT", "https://github.com/org/app", "org", "app")]
    [DataRow("HTTPS://GitHub.COM/Org/App", "https://github.com/org/app", "org", "app")]
    [DataRow("https://github.com:443/org/app", "https://github.com/org/app", "org", "app")]
    [DataRow("https://github.com/my.org/my_app-2", "https://github.com/my.org/my_app-2", "my.org", "my_app-2")]
    [DataRow("https://github.com/org/.github", "https://github.com/org/.github", "org", ".github")]
    public void AllowedGitHubUrl_IsCanonicalized(string url, string canonical, string owner, string repo)
    {
        var d = RepositoryUrlPolicy.Default.Evaluate(url);
        Assert.IsTrue(d.Ok, d.Reason);
        Assert.IsNull(d.Reason);
        Assert.AreEqual(canonical, d.Canonical);
        Assert.AreEqual("github.com", d.Host);
        Assert.AreEqual(owner, d.Owner);
        Assert.AreEqual(repo, d.Repo);
    }

    [TestMethod]
    public void CaseSensitiveHost_PreservesPathCase()
    {
        var policy = new RepositoryUrlPolicy(["git.example.com"]);
        var d = policy.Evaluate("https://GIT.example.com/Org/Repo.git");
        Assert.IsTrue(d.Ok, d.Reason);
        Assert.AreEqual("https://git.example.com/Org/Repo", d.Canonical,
            "a possibly case-sensitive host folds its name only (RemoteUrlIdentity)");
        Assert.AreEqual("git.example.com", d.Host);
        Assert.AreEqual("Org", d.Owner);
        Assert.AreEqual("Repo", d.Repo);
    }

    [TestMethod]
    // Scheme: https only (http, ssh, git, scp-like, file, no scheme at all).
    [DataRow("http://github.com/org/app", RepositoryUrlRejection.SchemeNotAllowed)]
    [DataRow("ssh://git@github.com/org/app.git", RepositoryUrlRejection.SchemeNotAllowed)]
    [DataRow("git://github.com/org/app.git", RepositoryUrlRejection.SchemeNotAllowed)]
    [DataRow("git@github.com:org/app.git", RepositoryUrlRejection.SchemeNotAllowed)]
    [DataRow("file:///srv/repos/app.git", RepositoryUrlRejection.SchemeNotAllowed)]
    [DataRow("ext::sh -c touch% /tmp/pwned", RepositoryUrlRejection.SchemeNotAllowed)]
    [DataRow("github.com/org/app", RepositoryUrlRejection.SchemeNotAllowed)]
    [DataRow("://github.com/org/app", RepositoryUrlRejection.SchemeNotAllowed)]
    [DataRow("", RepositoryUrlRejection.SchemeNotAllowed)]
    [DataRow(null, RepositoryUrlRejection.SchemeNotAllowed)]
    // URL components: userinfo, a port, a query, a fragment, whitespace/control/backslash.
    [DataRow("https://user@github.com/org/app", RepositoryUrlRejection.UrlComponentNotAllowed)]
    [DataRow("https://user:hunter2@github.com/org/app", RepositoryUrlRejection.UrlComponentNotAllowed)]
    [DataRow("https://github.com:8443/org/app", RepositoryUrlRejection.UrlComponentNotAllowed)]
    [DataRow("https://github.com:/org/app", RepositoryUrlRejection.UrlComponentNotAllowed)]
    [DataRow("https://github.com:0443/org/app", RepositoryUrlRejection.UrlComponentNotAllowed)]
    [DataRow("https://github.com/org/app?ref=main", RepositoryUrlRejection.UrlComponentNotAllowed)]
    [DataRow("https://github.com/org/app#readme", RepositoryUrlRejection.UrlComponentNotAllowed)]
    [DataRow("https://github.com/org/app ", RepositoryUrlRejection.UrlComponentNotAllowed)]
    [DataRow(" https://github.com/org/app", RepositoryUrlRejection.SchemeNotAllowed)]
    [DataRow("https://github.com/org/a pp", RepositoryUrlRejection.UrlComponentNotAllowed)]
    [DataRow("https://github.com/org/app\n", RepositoryUrlRejection.UrlComponentNotAllowed)]
    [DataRow("https://github.com\\org\\app", RepositoryUrlRejection.UrlComponentNotAllowed)]
    // Host shape: IP literals (v4 and the WHATWG numeric forms, v6), localhost, one label, a trailing dot.
    [DataRow("https://127.0.0.1/org/app", RepositoryUrlRejection.HostNotAllowed)]
    [DataRow("https://169.254.169.254/org/app", RepositoryUrlRejection.HostNotAllowed)]
    [DataRow("https://127.1/org/app", RepositoryUrlRejection.HostNotAllowed)]
    [DataRow("https://0x7f.1/org/app", RepositoryUrlRejection.HostNotAllowed)]
    [DataRow("https://example.0x7f/org/app", RepositoryUrlRejection.HostNotAllowed)]
    [DataRow("https://[::1]/org/app", RepositoryUrlRejection.HostNotAllowed)]
    [DataRow("https://[::ffff:127.0.0.1]:443/org/app", RepositoryUrlRejection.HostNotAllowed)]
    [DataRow("https://localhost/org/app", RepositoryUrlRejection.HostNotAllowed)]
    [DataRow("https://LOCALHOST/org/app", RepositoryUrlRejection.HostNotAllowed)]
    [DataRow("https://svc.localhost/org/app", RepositoryUrlRejection.HostNotAllowed)]
    [DataRow("https://intranet/org/app", RepositoryUrlRejection.HostNotAllowed)]
    [DataRow("https://github.com./org/app", RepositoryUrlRejection.HostNotAllowed)]
    [DataRow("https://-bad.example.com/org/app", RepositoryUrlRejection.HostNotAllowed)]
    [DataRow("https://git%68ub.com/org/app", RepositoryUrlRejection.HostNotAllowed)]
    [DataRow("https://gíthub.com/org/app", RepositoryUrlRejection.HostNotAllowed)]
    [DataRow("https:///org/app", RepositoryUrlRejection.HostNotAllowed)]
    // Path: exactly /owner/repo[.git] with safe segments.
    [DataRow("https://github.com", RepositoryUrlRejection.PathNotAllowed)]
    [DataRow("https://github.com/", RepositoryUrlRejection.PathNotAllowed)]
    [DataRow("https://github.com/org", RepositoryUrlRejection.PathNotAllowed)]
    [DataRow("https://github.com/org/app/", RepositoryUrlRejection.PathNotAllowed)]
    [DataRow("https://github.com/org/app/tree/main", RepositoryUrlRejection.PathNotAllowed)]
    [DataRow("https://github.com//org/app", RepositoryUrlRejection.PathNotAllowed)]
    [DataRow("https://github.com/-org/app", RepositoryUrlRejection.PathNotAllowed)]
    [DataRow("https://github.com/org/-app", RepositoryUrlRejection.PathNotAllowed)]
    [DataRow("https://github.com/org/--upload-pack=x", RepositoryUrlRejection.PathNotAllowed)]
    [DataRow("https://github.com/../app", RepositoryUrlRejection.PathNotAllowed)]
    [DataRow("https://github.com/org/..", RepositoryUrlRejection.PathNotAllowed)]
    [DataRow("https://github.com/./app", RepositoryUrlRejection.PathNotAllowed)]
    [DataRow("https://github.com/org/.git", RepositoryUrlRejection.PathNotAllowed)]
    [DataRow("https://github.com/org/...git", RepositoryUrlRejection.PathNotAllowed)]
    [DataRow("https://github.com/org/app.git.git", RepositoryUrlRejection.PathNotAllowed)]
    [DataRow("https://github.com/org/%2e%2e", RepositoryUrlRejection.PathNotAllowed)]
    [DataRow("https://github.com/org/app;x", RepositoryUrlRejection.PathNotAllowed)]
    [DataRow("https://github.com/org/ápp", RepositoryUrlRejection.PathNotAllowed)]
    // Host allow-list: off-list hosts are refused by default.
    [DataRow("https://gitlab.com/org/app", RepositoryUrlRejection.HostNotAllowed)]
    [DataRow("https://github.com.evil.example/org/app", RepositoryUrlRejection.HostNotAllowed)]
    [DataRow("https://api.github.com/org/app", RepositoryUrlRejection.HostNotAllowed)]
    public void UnsafeUrl_IsRefusedWithReason(string? url, string reason)
    {
        var d = RepositoryUrlPolicy.Default.Evaluate(url);
        Assert.IsFalse(d.Ok);
        Assert.AreEqual(reason, d.Reason);
        Assert.IsNull(d.Canonical);
        Assert.IsNull(d.Host);
        Assert.IsNull(d.Owner);
        Assert.IsNull(d.Repo);
    }

    [TestMethod]
    public void SegmentLength_IsBounded()
    {
        var max = new string('a', 100);
        Assert.IsTrue(RepositoryUrlPolicy.Default.Evaluate($"https://github.com/{max}/{max}.git").Ok);
        Assert.AreEqual(RepositoryUrlRejection.PathNotAllowed,
            RepositoryUrlPolicy.Default.Evaluate($"https://github.com/{max}a/app").Reason);
        Assert.AreEqual(RepositoryUrlRejection.PathNotAllowed,
            RepositoryUrlPolicy.Default.Evaluate($"https://github.com/org/{max}a").Reason);
    }

    [TestMethod]
    public void OverLongUrl_IsRefused()
    {
        var url = "https://github.com/org/" + new string('a', RepositoryUrlPolicy.MaxUrlLength);
        Assert.AreEqual(RepositoryUrlRejection.UrlComponentNotAllowed, RepositoryUrlPolicy.Default.Evaluate(url).Reason);
    }

    [TestMethod]
    public void Refusal_NeverEchoesTheUrl()
    {
        var d = RepositoryUrlPolicy.Default.Evaluate("https://x-access-token:hunter2@github.com/org/app");
        Assert.IsFalse(d.Ok);
        Assert.IsFalse(d.ToString().Contains("hunter2", StringComparison.Ordinal));
        Assert.IsFalse(d.ToString().Contains("github.com", StringComparison.Ordinal));
    }

    [TestMethod]
    public void FileUrl_IsAllowedOnlyUnderTheTestFlag()
    {
        const string url = "file:///tmp/sextant-fixture/app.git";
        Assert.AreEqual(RepositoryUrlRejection.SchemeNotAllowed, RepositoryUrlPolicy.Default.Evaluate(url).Reason);
        Assert.AreEqual(RepositoryUrlRejection.SchemeNotAllowed, AnyHost.Evaluate(url).Reason);

        var testing = new RepositoryUrlPolicy([RepositoryUrlPolicy.DefaultHost]) { AllowFileTransportForTesting = true };
        var d = testing.Evaluate(url);
        Assert.IsTrue(d.Ok);
        Assert.AreEqual("file:///tmp/sextant-fixture/app", d.Canonical);
        Assert.IsNull(d.Host);
        Assert.IsNull(d.Owner);
        Assert.IsNull(d.Repo);
        Assert.AreEqual(RepositoryUrlRejection.SchemeNotAllowed, testing.Evaluate("http://github.com/org/app").Reason,
            "the flag only admits file://, never any other non-https scheme");
    }

    [TestMethod]
    public void ExplicitHostList_AllowsOnlyListedHosts()
    {
        var policy = new RepositoryUrlPolicy([" GitLab.example.com ", "github.com", "gitlab.example.com"]);
        CollectionAssert.AreEqual(new[] { "gitlab.example.com", "github.com" }, policy.Hosts.ToArray());
        Assert.IsFalse(policy.AllowsAnyHost);
        Assert.IsTrue(policy.Evaluate("https://gitlab.example.com/grp/proj.git").Ok);
        Assert.IsTrue(policy.Evaluate("https://github.com/org/app").Ok);
        Assert.AreEqual(RepositoryUrlRejection.HostNotAllowed, policy.Evaluate("https://bitbucket.org/org/app").Reason);
        Assert.AreEqual(RepositoryUrlRejection.HostNotAllowed, policy.Evaluate("https://sub.gitlab.example.com/grp/proj").Reason,
            "a subdomain of a listed host is a different host");
    }

    [TestMethod]
    public void WildcardHost_AllowsAnyShapeValidHost_ButStillEnforcesShape()
    {
        var policy = AnyHost;
        Assert.IsTrue(policy.AllowsAnyHost);
        Assert.AreEqual(0, policy.Hosts.Count);
        Assert.IsTrue(policy.Evaluate("https://git.internal.example/org/app").Ok);
        Assert.AreEqual(RepositoryUrlRejection.HostNotAllowed, policy.Evaluate("https://10.0.0.1/org/app").Reason);
        Assert.AreEqual(RepositoryUrlRejection.HostNotAllowed, policy.Evaluate("https://localhost/org/app").Reason);
        Assert.AreEqual(RepositoryUrlRejection.HostNotAllowed, policy.Evaluate("https://[fd00::1]/org/app").Reason);
        Assert.AreEqual(RepositoryUrlRejection.HostNotAllowed, policy.Evaluate("https://metadata/org/app").Reason);
        Assert.AreEqual(RepositoryUrlRejection.UrlComponentNotAllowed, policy.Evaluate("https://h.example:22/org/app").Reason);
    }

    [TestMethod]
    public void OwnerAllowList_MatchesHostAndOwner()
    {
        var policy = new RepositoryUrlPolicy(
            ["github.com", "git.example.com"],
            ["github.com/Acme", "git.example.com/Team"]);
        CollectionAssert.AreEqual(new[] { "github.com/acme", "git.example.com/Team" }, policy.Owners!.ToArray());

        Assert.IsTrue(policy.Evaluate("https://github.com/acme/widgets").Ok);
        Assert.IsTrue(policy.Evaluate("https://github.com/ACME/Widgets.git").Ok,
            "github.com owners are case-insensitive (RemoteUrlIdentity fold)");
        Assert.AreEqual(RepositoryUrlRejection.OwnerNotAllowed, policy.Evaluate("https://github.com/other/app").Reason);
        Assert.AreEqual(RepositoryUrlRejection.OwnerNotAllowed, policy.Evaluate("https://github.com/acme-evil/app").Reason);

        Assert.IsTrue(policy.Evaluate("https://git.example.com/Team/app").Ok);
        Assert.AreEqual(RepositoryUrlRejection.OwnerNotAllowed, policy.Evaluate("https://git.example.com/team/app").Reason,
            "a possibly case-sensitive host matches the owner exactly");
    }

    [TestMethod]
    public void OwnerAllowList_HostWildcard_AndUnlistedHost()
    {
        var policy = new RepositoryUrlPolicy(["github.com", "git.example.com"], ["git.example.com/*", "github.com/org"]);
        Assert.IsTrue(policy.Evaluate("https://git.example.com/anyone/app").Ok);
        Assert.IsTrue(policy.Evaluate("https://github.com/org/app").Ok);
        Assert.AreEqual(RepositoryUrlRejection.OwnerNotAllowed, policy.Evaluate("https://github.com/someone/app").Reason);

        var githubOnly = new RepositoryUrlPolicy(["github.com", "git.example.com"], ["github.com/org"]);
        Assert.AreEqual(RepositoryUrlRejection.OwnerNotAllowed, githubOnly.Evaluate("https://git.example.com/org/app").Reason,
            "once an owner allow-list is configured, a host it does not name admits no owner");
    }

    [TestMethod]
    public void OwnerAllowList_WithWildcardHosts_RestrictsToNamedHosts()
    {
        var policy = new RepositoryUrlPolicy([RepositoryUrlPolicy.Wildcard], ["git.example.com/org"]);
        Assert.IsTrue(policy.Evaluate("https://git.example.com/org/app").Ok);
        Assert.AreEqual(RepositoryUrlRejection.OwnerNotAllowed, policy.Evaluate("https://github.com/org/app").Reason);
    }

    [TestMethod]
    public void HostChecks_PrecedeOwnerChecks()
    {
        var policy = new RepositoryUrlPolicy(["github.com"], ["github.com/org"]);
        Assert.AreEqual(RepositoryUrlRejection.HostNotAllowed, policy.Evaluate("https://gitlab.com/org/app").Reason);
        Assert.AreEqual(RepositoryUrlRejection.PathNotAllowed, policy.Evaluate("https://github.com/org/app/x").Reason);
    }

    [TestMethod]
    [DataRow("https://github.com/org/app")]
    [DataRow("https://GitHub.com:443/Org/App.git")]
    [DataRow("https://git.example.com/Org/Repo.GIT")]
    [DataRow("https://git.example.com/org/re.po")]
    public void Canonicalization_IsIdempotent(string url)
    {
        var policy = new RepositoryUrlPolicy(["github.com", "git.example.com"]);
        var first = policy.Evaluate(url);
        Assert.IsTrue(first.Ok, first.Reason);
        var second = policy.Evaluate(first.Canonical);
        Assert.IsTrue(second.Ok, second.Reason);
        Assert.AreEqual(first, second, "evaluating the canonical form must reproduce the same decision");
        Assert.AreEqual(first.Canonical, Sextant.Core.RemoteUrlIdentity.Normalize(first.Canonical));
    }

    [TestMethod]
    public void Canonicalization_AgreesWithRemoteUrlIdentity()
    {
        // Two spellings the catalog already treats as one repository map to one policy key.
        var a = RepositoryUrlPolicy.Default.Evaluate("https://github.com/org/app.git");
        var b = RepositoryUrlPolicy.Default.Evaluate("https://GITHUB.com/ORG/APP");
        Assert.AreEqual(a.Canonical, b.Canonical);
        Assert.AreEqual(Sextant.Core.RemoteUrlIdentity.Normalize("https://github.com/org/app.git"), a.Canonical);
    }

    [TestMethod]
    [DataRow("https://github.com")]
    [DataRow("github.com:443")]
    [DataRow("github.com/org")]
    [DataRow("user@github.com")]
    [DataRow("127.0.0.1")]
    [DataRow("[::1]")]
    [DataRow("localhost")]
    [DataRow("intranet")]
    [DataRow("github.com.")]
    [DataRow("*.github.com")]
    [DataRow("")]
    public void Constructor_MalformedHostEntry_Throws(string host) =>
        Assert.ThrowsExactly<FormatException>(() => new RepositoryUrlPolicy(["github.com", host]));

    [TestMethod]
    public void Constructor_EmptyHostList_Throws() =>
        Assert.ThrowsExactly<FormatException>(() => new RepositoryUrlPolicy([]));

    [TestMethod]
    [DataRow("github.com")]
    [DataRow("github.com/")]
    [DataRow("/org")]
    [DataRow("github.com/org/app")]
    [DataRow("github.com/-org")]
    [DataRow("github.com/..")]
    [DataRow("*/org")]
    [DataRow("https://github.com/org")]
    [DataRow("gitlab.com/org")]
    public void Constructor_MalformedOwnerEntry_Throws(string owner) =>
        Assert.ThrowsExactly<FormatException>(() => new RepositoryUrlPolicy(["github.com"], [owner]));

    [TestMethod]
    public void Constructor_EmptyOwnerList_Throws() =>
        Assert.ThrowsExactly<FormatException>(() => new RepositoryUrlPolicy(["github.com"], []));

    [TestMethod]
    public void ParseRepositoryUrlPolicy_UnsetIsGitHubOnly()
    {
        var policy = ServiceOptions.ParseRepositoryUrlPolicy(null, null);
        Assert.AreSame(RepositoryUrlPolicy.Default, policy);
        CollectionAssert.AreEqual(new[] { "github.com" }, policy.Hosts.ToArray());
        Assert.IsNull(policy.Owners);
        Assert.IsFalse(policy.AllowsAnyHost);
    }

    [TestMethod]
    public void ParseRepositoryUrlPolicy_ParsesLists()
    {
        var policy = ServiceOptions.ParseRepositoryUrlPolicy(" github.com, GitLab.Example.com ,", "github.com/org, gitlab.example.com/*");
        CollectionAssert.AreEqual(new[] { "github.com", "gitlab.example.com" }, policy.Hosts.ToArray());
        CollectionAssert.AreEqual(new[] { "github.com/org", "gitlab.example.com/*" }, policy.Owners!.ToArray());

        var ownersOnly = ServiceOptions.ParseRepositoryUrlPolicy(null, "github.com/org");
        CollectionAssert.AreEqual(new[] { "github.com" }, ownersOnly.Hosts.ToArray(), "unset hosts keep the github.com default");
        CollectionAssert.AreEqual(new[] { "github.com/org" }, ownersOnly.Owners!.ToArray());

        Assert.IsTrue(ServiceOptions.ParseRepositoryUrlPolicy("*", null).AllowsAnyHost);
    }

    [TestMethod]
    [DataRow("https://github.com", null)]
    [DataRow("github.com:8443", null)]
    [DataRow("10.0.0.1", null)]
    [DataRow("localhost", null)]
    [DataRow(",", null)]
    [DataRow(" ", null)]
    [DataRow("github.com", "github.com")]
    [DataRow("github.com", "gitlab.com/org")]
    [DataRow("github.com", ",")]
    [DataRow(null, "gitlab.com/org")]
    public void ParseRepositoryUrlPolicy_Malformed_FailsClosed(string? hosts, string? owners)
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => ServiceOptions.ParseRepositoryUrlPolicy(hosts, owners));
        StringAssert.Contains(ex.Message, "SEXTANT_SERVICE_REPOSITORY_");
        StringAssert.Contains(ex.Message, "fail closed");
    }
}
