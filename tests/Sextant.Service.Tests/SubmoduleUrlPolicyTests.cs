namespace Sextant.Service.Tests;

/// <summary>
/// Issue #125: the pure, git-free policy that turns an UNTRUSTED <c>.gitmodules</c> URL into the URL the
/// service may fetch (and whether the checkout token may be sent to it), plus the small parsing/validation
/// helpers the recursive submodule provisioner relies on. Includes the two URL shapes the real
/// elevenworks/monorepo uses (plain absolute https, and https with a username-only userinfo).
/// </summary>
[TestClass]
public class SubmoduleUrlPolicyTests
{
    private const string Parent = "https://github.com/elevenworks/monorepo.git";
    private const string RepoHost = "github.com";
    private static readonly IReadOnlySet<string> NoHosts = new HashSet<string>();

    private static SubmoduleUrlDecision Resolve(
        string? url, string parent = Parent, string? repoHost = RepoHost, IReadOnlySet<string>? hosts = null,
        bool allowFile = false) =>
        SubmoduleUrlPolicy.Resolve(url, parent, repoHost, hosts ?? NoHosts, allowFile);

    [TestMethod]
    public void AbsoluteHttpsOnRepositoryHost_IsAllowedWithToken()
    {
        // elevenworks/monorepo: `libs/MixAndMatch` → https://github.com/elevenworks/MixAndMatch.git
        var d = Resolve("https://github.com/elevenworks/MixAndMatch.git");
        Assert.IsTrue(d.Allowed);
        Assert.AreEqual("https://github.com/elevenworks/MixAndMatch.git", d.CleanUrl);
        Assert.AreEqual("github.com", d.Authority);
        Assert.IsTrue(d.SendToken, "a same-host submodule is fetched with the service's transient auth");
    }

    [TestMethod]
    public void UsernameOnlyUserInfo_IsStripped()
    {
        // elevenworks/monorepo: `External/Gcodes` → https://jonlipsky@github.com/jonlipsky/gcodes.git
        var d = Resolve("https://jonlipsky@github.com/jonlipsky/gcodes.git");
        Assert.IsTrue(d.Allowed);
        Assert.AreEqual("https://github.com/jonlipsky/gcodes.git", d.CleanUrl, "a bare username is not a secret; strip it");
        Assert.IsTrue(d.SendToken);
    }

    [TestMethod]
    [DataRow("https://user:hunter2@github.com/o/r.git")]
    [DataRow("https://x-access-token:hunter2@github.com/o/r.git")]
    [DataRow("https://:hunter2@github.com/o/r.git")]
    [DataRow("ssh://git:hunter2@github.com/o/r.git")]
    public void PasswordOrTokenUserInfo_IsRefusedWithoutEchoingIt(string url)
    {
        var d = Resolve(url);
        Assert.IsFalse(d.Allowed);
        Assert.IsNull(d.CleanUrl);
        Assert.IsFalse(d.Refusal!.Contains("hunter2", StringComparison.Ordinal), "a refusal must never echo the secret");
    }

    [TestMethod]
    public void DefaultPortAndHostCase_AreNormalized()
    {
        var d = Resolve("https://GitHub.COM:443/o/r.git");
        Assert.IsTrue(d.Allowed);
        Assert.AreEqual("https://github.com/o/r.git", d.CleanUrl);
        Assert.IsTrue(d.SendToken);
    }

    [TestMethod]
    public void DifferentPortOnRepositoryHost_IsADifferentAuthority()
    {
        Assert.IsFalse(Resolve("https://github.com:8443/o/r.git").Allowed,
            "the token is scoped to the exact repository authority, not every port of the host");
    }

    [TestMethod]
    [DataRow("../MixAndMatch.git", "https://github.com/elevenworks/MixAndMatch.git")]
    [DataRow("../../other/X.git", "https://github.com/other/X.git")]
    [DataRow("./sub.git", "https://github.com/elevenworks/monorepo.git/sub.git")]
    [DataRow("../a/./b.git", "https://github.com/elevenworks/a/b.git")]
    public void RelativeUrl_ResolvesAgainstCleanParentUrl(string relative, string expected)
    {
        var d = Resolve(relative);
        Assert.IsTrue(d.Allowed, d.Refusal);
        Assert.AreEqual(expected, d.CleanUrl);
        Assert.IsTrue(d.SendToken, "a relative url stays on the parent's host");
    }

    [TestMethod]
    [DataRow("../../../x.git")]
    [DataRow("../../../../etc/passwd")]
    [DataRow("..//x.git")]
    public void RelativeUrl_ClimbingPastTheHost_IsRefused(string relative) =>
        Assert.IsFalse(Resolve(relative).Allowed);

    [TestMethod]
    public void RelativeUrl_OfANestedSubmodule_ResolvesAgainstItsOwnParent()
    {
        // A nested submodule's relative url is relative to ITS parent (the intermediate submodule's clean url).
        var d = Resolve("../deep.git", parent: "https://github.com/org/mid.git");
        Assert.AreEqual("https://github.com/org/deep.git", d.CleanUrl);
    }

    [TestMethod]
    [DataRow("git@github.com:elevenworks/MixAndMatch.git", "https://github.com/elevenworks/MixAndMatch.git")]
    [DataRow("github.com:elevenworks/MixAndMatch.git", "https://github.com/elevenworks/MixAndMatch.git")]
    [DataRow("ssh://git@github.com/elevenworks/MixAndMatch.git", "https://github.com/elevenworks/MixAndMatch.git")]
    [DataRow("ssh://git@github.com:22/elevenworks/MixAndMatch.git", "https://github.com/elevenworks/MixAndMatch.git")]
    [DataRow("git+ssh://GitHub.com/o/r.git", "https://github.com/o/r.git")]
    [DataRow("ssh+git://github.com/o/r", "https://github.com/o/r")]
    public void SshAndScpUrlsOnRepositoryHost_AreRewrittenToHttps(string url, string expected)
    {
        var d = Resolve(url);
        Assert.IsTrue(d.Allowed, d.Refusal);
        Assert.AreEqual(expected, d.CleanUrl);
        Assert.AreEqual("github.com", d.Authority);
        Assert.IsTrue(d.SendToken);
    }

    [TestMethod]
    public void ScpUrl_RewritesToTheRepositoryHttpsAuthorityIncludingItsPort()
    {
        var d = Resolve("git@git.example.com:org/lib.git", parent: "https://git.example.com:8443/org/app.git",
            repoHost: "git.example.com:8443");
        Assert.AreEqual("https://git.example.com:8443/org/lib.git", d.CleanUrl);
        Assert.IsTrue(d.SendToken);
    }

    [TestMethod]
    [DataRow("git@gitlab.com:o/r.git")]
    [DataRow("ssh://git@bitbucket.org/o/r.git")]
    public void SshUrlOnAnotherHost_IsRefused(string url)
    {
        var d = Resolve(url);
        Assert.IsFalse(d.Allowed);
        StringAssert.Contains(d.Refusal, "not the repository's https host");
    }

    [TestMethod]
    public void SshUrl_IsRefusedWhenTheRepositoryIsNotHttps() =>
        Assert.IsFalse(Resolve("git@github.com:o/r.git", parent: "git@github.com:o/app.git", repoHost: null).Allowed,
            "with no https repository authority there is no host to rewrite an ssh url onto");

    [TestMethod]
    public void OtherHttpsHost_IsRefusedUnlessAllowlisted_AndThenFetchedAnonymously()
    {
        var refused = Resolve("https://gitlab.example.com/o/r.git");
        Assert.IsFalse(refused.Allowed);
        StringAssert.Contains(refused.Refusal, "SEXTANT_SERVICE_SUBMODULE_HOSTS");

        var allowed = Resolve("https://gitlab.example.com/o/r.git", hosts: new HashSet<string> { "gitlab.example.com" });
        Assert.IsTrue(allowed.Allowed);
        Assert.AreEqual("https://gitlab.example.com/o/r.git", allowed.CleanUrl);
        Assert.IsFalse(allowed.SendToken, "the token NEVER leaves the repository's own host");
    }

    [TestMethod]
    public void RepositoryHostlessParent_OnlyAllowlistedHttpsHostsAreFetched()
    {
        Assert.IsFalse(Resolve("https://github.com/o/r.git", parent: "git@github.com:o/app.git", repoHost: null).Allowed);
        var d = Resolve("https://github.com/o/r.git", parent: "git@github.com:o/app.git", repoHost: null,
            hosts: new HashSet<string> { "github.com" });
        Assert.IsTrue(d.Allowed);
        Assert.IsFalse(d.SendToken);
    }

    [TestMethod]
    [DataRow("http://github.com/o/r.git")]
    [DataRow("git://github.com/o/r.git")]
    [DataRow("ftp://github.com/o/r.git")]
    [DataRow("ext::sh")]
    [DataRow("fd::17")]
    [DataRow("file:///srv/git/secret.git")]
    [DataRow("/srv/git/secret.git")]
    [DataRow("C:/repos/secret")]
    [DataRow("C:\\repos\\secret")]
    [DataRow("-uhelp")]
    [DataRow("--upload-pack=touch")]
    [DataRow(" https://github.com/o/r.git")]
    [DataRow("https://github.com/o/r.git\nhttps://evil.example/x")]
    [DataRow("https://github.com/o/r.git%0a")]
    [DataRow("https://github.com/o/r.git%00")]
    [DataRow("https://github.com/o/r.git?x=1")]
    [DataRow("https://github.com/o/r.git#frag")]
    [DataRow("https://github.com/../x.git")]
    [DataRow("https://github.com/o/./r.git")]
    [DataRow("https://github.com")]
    [DataRow("https://github.com/")]
    [DataRow("https://[::1]/o/r.git")]
    [DataRow("https://github.com:0/o/r.git")]
    [DataRow("https://github.com:99999/o/r.git")]
    [DataRow("https://gith_ub.com/o/r.git")]
    [DataRow("")]
    [DataRow(null)]
    public void DisallowedShapes_AreRefused(string? url)
    {
        var d = Resolve(url);
        Assert.IsFalse(d.Allowed, $"'{url}' must be refused");
        Assert.IsFalse(string.IsNullOrEmpty(d.Refusal));
    }

    [TestMethod]
    public void OverlongUrl_IsRefused() =>
        Assert.IsFalse(Resolve("https://github.com/" + new string('a', SubmoduleUrlPolicy.MaxUrlLength)).Allowed);

    [TestMethod]
    public void FileTransport_IsAllowedOnlyUnderTheTestFlag()
    {
        const string fileParent = "file:///tmp/fixtures/app";
        Assert.IsFalse(Resolve("file:///tmp/fixtures/lib", parent: fileParent, repoHost: null).Allowed);
        var d = Resolve("../lib", parent: fileParent, repoHost: null, allowFile: true);
        Assert.IsTrue(d.Allowed, d.Refusal);
        Assert.AreEqual("file:///tmp/fixtures/lib", d.CleanUrl);
        Assert.IsFalse(d.SendToken);
    }

    [TestMethod]
    [DataRow("GitHub.COM", "github.com")]
    [DataRow("git.example.com:443", "git.example.com")]
    [DataRow("git.example.com:8443", "git.example.com:8443")]
    [DataRow("git.example.com:08443", "git.example.com:8443")]
    [DataRow("10.0.0.5", "10.0.0.5")]
    [DataRow("https://github.com", null)]
    [DataRow("github.com/org", null)]
    [DataRow("user@github.com", null)]
    [DataRow("github.com:", null)]
    [DataRow("github.com:0", null)]
    [DataRow("github.com:65536", null)]
    [DataRow("-github.com", null)]
    [DataRow("[::1]", null)]
    [DataRow("", null)]
    public void NormalizeAuthority_AcceptsOnlyPlainHostAndPort(string input, string? expected) =>
        Assert.AreEqual(expected, SubmoduleUrlPolicy.NormalizeAuthority(input));

    [TestMethod]
    [DataRow("https://github.com/o/r.git", "github.com")]
    [DataRow("HTTPS://GitHub.com:8443/o/r.git", "github.com:8443")]
    [DataRow("https://user@github.com/o/r.git", null)]
    [DataRow("http://github.com/o/r.git", null)]
    [DataRow("git@github.com:o/r.git", null)]
    [DataRow("file:///tmp/r", null)]
    public void HttpsAuthority_IsTheOnlyHostTheTokenMayReach(string url, string? expected) =>
        Assert.AreEqual(expected, SubmoduleUrlPolicy.HttpsAuthority(url));

    [TestMethod]
    [DataRow("https://h/org/app.git", "../lib.git", "https://h/org/lib.git")]
    [DataRow("https://h/org/app.git", "./lib.git", "https://h/org/app.git/lib.git")]
    [DataRow("https://h/org/app.git", "../lib.git/", "https://h/org/lib.git")]
    [DataRow("https://h", "../x", null)]
    [DataRow("https://h/org", "../../x", null)]
    [DataRow("not-a-url", "../x", null)]
    public void ResolveRelative_FollowsGitSemantics(string parent, string relative, string? expected) =>
        Assert.AreEqual(expected, SubmoduleUrlPolicy.ResolveRelative(parent, relative));

    [TestMethod]
    public void ParseSubmoduleHosts_NormalizesAndDedups()
    {
        CollectionAssert.AreEqual(
            new[] { "gitlab.example.com", "git.example.com:8443" },
            ServiceOptions.ParseSubmoduleHosts(" gitlab.example.com, GIT.example.com:8443 ,gitlab.example.com:443,").ToArray());
        Assert.AreEqual(0, ServiceOptions.ParseSubmoduleHosts(null).Count);
    }

    [TestMethod]
    [DataRow("https://gitlab.example.com")]
    [DataRow("gitlab.example.com/org")]
    [DataRow("user:pw@gitlab.example.com")]
    public void ParseSubmoduleHosts_InvalidEntry_FailsClosed(string value) =>
        Assert.ThrowsExactly<InvalidOperationException>(() => ServiceOptions.ParseSubmoduleHosts(value));

    [TestMethod]
    public void Constructor_InvalidSubmoduleHost_Throws()
    {
        var dataRoot = ServiceTestFixtures.NewDataRoot();
        try
        {
            var paths = new ServicePaths(ServiceVolumes.Rooted(dataRoot));
            Assert.ThrowsExactly<ArgumentException>(() => new CloningCheckoutProvider(
                new PersistentVolumeCheckoutProvider(paths), paths, submoduleHosts: ["https://bad"]));
        }
        finally
        {
            try { if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true); }
            catch { /* best-effort temp cleanup */ }
        }
    }
}

/// <summary>Unit tests for the recursive submodule provisioner's parsing, validation and scrub helpers.</summary>
[TestClass]
public class SubmoduleProvisioningHelperTests
{
    private readonly List<string> _tempDirs = [];

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var dir in _tempDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* best-effort temp cleanup */ }
        }
    }

    [TestMethod]
    public void ParseGitmodulesConfig_HonoursOnlyPathAndUrl_LastValueWins_FirstPathClaimWins()
    {
        const string output =
            "submodule.a.path\nlibs/a\0" +
            "submodule.a.url\nhttps://h/old.git\0" +
            "submodule.a.url\nhttps://h/a.git\0" +
            "submodule.a.update\n!rm -rf /\0" +       // never honoured (no command execution)
            "submodule.b.c.path\nlibs/bc\0" +          // a name may itself contain dots
            "submodule.b.c.url\n../bc.git\0" +
            "SUBMODULE.D.PATH\nlibs/d\0" +             // section/variable case-insensitive, name case-sensitive
            "submodule.dup.path\nlibs/a\0" +           // a second claim on an already-claimed path is ignored
            "submodule.dup.url\nhttps://h/evil.git\0" +
            "core.bare\nfalse\0";

        var entries = CloningCheckoutProvider.ParseGitmodulesConfigForTest(output);

        CollectionAssert.AreEqual(new[] { "a", "b.c", "D" }, entries.Select(e => e.Name).ToArray());
        Assert.AreEqual(("a", "libs/a", "https://h/a.git"), entries[0]);
        Assert.AreEqual(("b.c", "libs/bc", "../bc.git"), entries[1]);
        Assert.AreEqual(("D", "libs/d", (string?)null), entries[2]);
    }

    [TestMethod]
    [DataRow("libs/a", true)]
    [DataRow("MixAndMatch", true)]
    [DataRow("External/Gcodes", true)]
    [DataRow("", false)]
    [DataRow("/abs", false)]
    [DataRow("../escape", false)]
    [DataRow("a/../../b", false)]
    [DataRow("a\nb", false)]
    public void IsSafeSubmoduleName_FollowsGitsCheckSubmoduleName(string name, bool expected) =>
        Assert.AreEqual(expected, CloningCheckoutProvider.IsSafeSubmoduleName(name));

    [TestMethod]
    [DataRow("libs/a", true)]
    [DataRow("libs/a/", true)]
    [DataRow("External/Gcodes", true)]
    [DataRow("", false)]
    [DataRow(".", false)]
    [DataRow("../x", false)]
    [DataRow("a/../b", false)]
    [DataRow("/abs", false)]
    [DataRow("-x", false)]
    [DataRow("a//b", false)]
    [DataRow("a\\b", false)]
    [DataRow("a/.git", false)]
    [DataRow("a/.GIT/hooks", false)]
    [DataRow("C:x", false)]
    [DataRow("a\tb", false)]
    public void IsSafeSubmodulePath_RefusesTraversalAndGitDirs(string path, bool expected) =>
        Assert.AreEqual(expected, CloningCheckoutProvider.IsSafeSubmodulePath(path));

    [TestMethod]
    [DataRow("fatal: remote error: upload-pack: not our ref 1111111111111111111111111111111111111111", true)]
    [DataRow("fatal: transport 'file' not allowed", true)]
    [DataRow("fatal: transport 'ext' not allowed", true)]
    [DataRow("fatal: unable to access 'https://h/': Connection reset by peer", false)]
    public void MatchesDeterministicGitError_SubmoduleFailureShapes(string stderr, bool expected) =>
        Assert.AreEqual(expected, CloningCheckoutProvider.MatchesDeterministicGitError(stderr));

    [TestMethod]
    public void ApplyEnvironmentConfig_AppendsAfterInheritedEntries()
    {
        var env = new Dictionary<string, string?>
        {
            ["GIT_CONFIG_COUNT"] = "2",
            ["GIT_CONFIG_KEY_0"] = "http.proxy",
            ["GIT_CONFIG_VALUE_0"] = "http://proxy:3128",
            ["GIT_CONFIG_KEY_1"] = "credential.helper",
            ["GIT_CONFIG_VALUE_1"] = "store",
        };
        CloningCheckoutProvider.ApplyEnvironmentConfig(env, [new("credential.helper", ""), new("core.askPass", "")]);

        Assert.AreEqual("4", env["GIT_CONFIG_COUNT"]);
        Assert.AreEqual("http.proxy", env["GIT_CONFIG_KEY_0"], "an operator's own env config is preserved");
        Assert.AreEqual("credential.helper", env["GIT_CONFIG_KEY_2"], "ours come LAST so they win / reset");
        Assert.AreEqual(string.Empty, env["GIT_CONFIG_VALUE_2"]);
        Assert.AreEqual("core.askPass", env["GIT_CONFIG_KEY_3"]);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("garbage")]
    [DataRow("-1")]
    [DataRow("5000")]
    public void ApplyEnvironmentConfig_UnusableInheritedCount_StartsAtZero(string? inherited)
    {
        var env = new Dictionary<string, string?>();
        if (inherited is not null)
            env["GIT_CONFIG_COUNT"] = inherited;
        CloningCheckoutProvider.ApplyEnvironmentConfig(env, [new("a.b", "c")]);
        Assert.AreEqual("1", env["GIT_CONFIG_COUNT"]);
        Assert.AreEqual("a.b", env["GIT_CONFIG_KEY_0"]);
    }

    [TestMethod]
    public void FileContainsAny_FindsANeedleSpanningTheReadChunkBoundary()
    {
        var dir = Temp();
        var file = Path.Combine(dir, "blob");
        var needle = "SENTINEL-TOKEN"u8.ToArray();
        // The scanner reads (64 KiB + longest-needle − 1) bytes first; start the needle 5 bytes before that
        // first read ends so it straddles two reads and is only found via the carried overlap.
        var content = new byte[(64 * 1024) + needle.Length - 1 - 5];
        Array.Fill(content, (byte)'a');
        File.WriteAllBytes(file, [.. content, .. needle, .. "tail"u8.ToArray()]);

        Assert.IsTrue(CloningCheckoutProvider.FileContainsAny(file, [needle]));
        Assert.IsFalse(CloningCheckoutProvider.FileContainsAny(file, ["NOT-THERE"u8.ToArray()]));
    }

    [TestMethod]
    public void ScrubAndVerify_TokenInASubmoduleGitDirConfig_FailsClosed()
    {
        const string token = "SENTINEL-TOKEN-4f1c2b";
        var basic = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("x-access-token:" + token));
        var provider = NewProvider(token);
        var checkout = NewStagedCheckout();
        // A (hypothetical) leak of the Basic credential into an absorbed submodule's config.
        var moduleDir = Path.Combine(checkout, ".git", "modules", "lib");
        Directory.CreateDirectory(moduleDir);
        File.WriteAllText(Path.Combine(moduleDir, "HEAD"), "0123456789012345678901234567890123456789\n");
        File.WriteAllText(Path.Combine(moduleDir, "config"),
            $"[http \"https://github.com/\"]\n\textraheader = AUTHORIZATION: basic {basic}\n");

        Assert.IsFalse(provider.ScrubAndVerifyGitDirs(checkout, out var finding),
            "a credential anywhere under .git/modules must refuse publish");
        Assert.AreEqual(".git/modules/lib/config", finding);
        Assert.IsFalse(finding!.Contains(token, StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ScrubAndVerify_TokenInAnEmbeddedSubmoduleGitDir_FailsClosed(bool rawToken)
    {
        const string token = "SENTINEL-TOKEN-4f1c2b";
        var provider = NewProvider(token);
        var checkout = NewStagedCheckout();
        // The embedded git dir is DISCOVERED by walking the work tree (never rebuilt from recorded outcome
        // paths, which are display-sanitized/truncated), so even an unrecorded, deeply nested one is scanned.
        var embedded = Path.Combine(checkout, "libs", "a", "vendor", "deep", ".git");
        Directory.CreateDirectory(embedded);
        File.WriteAllText(Path.Combine(embedded, "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(embedded, "packed-refs"),
            rawToken ? $"# {token}\n" : $"# {Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(token))}\n");

        Assert.IsFalse(provider.ScrubAndVerifyGitDirs(checkout, out var finding));
        Assert.AreEqual("libs/a/vendor/deep/.git/packed-refs", finding);
    }

    [TestMethod]
    public void ScrubAndVerify_TokenInAGitfile_FailsClosed()
    {
        const string token = "SENTINEL-TOKEN-4f1c2b";
        var provider = NewProvider(token);
        var checkout = NewStagedCheckout();
        var sub = Path.Combine(checkout, "libs", "b");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, ".git"), $"gitdir: ../../.git/modules/b # {token}\n");

        Assert.IsFalse(provider.ScrubAndVerifyGitDirs(checkout, out var finding));
        Assert.AreEqual("libs/b/.git", finding);
    }

    [TestMethod]
    public void ScrubAndVerify_DeletesEveryFetchHead_AndPassesWhenClean()
    {
        var provider = NewProvider("SENTINEL-TOKEN-4f1c2b");
        var checkout = NewStagedCheckout();
        var top = Path.Combine(checkout, ".git", "FETCH_HEAD");
        File.WriteAllText(top, "x\n");
        var moduleDir = Path.Combine(checkout, ".git", "modules", "lib", "modules", "deep");
        Directory.CreateDirectory(moduleDir);
        File.WriteAllText(Path.Combine(moduleDir, "HEAD"), "0123456789012345678901234567890123456789\n");
        var nested = Path.Combine(moduleDir, "FETCH_HEAD");
        File.WriteAllText(nested, "y\n");
        // Object CONTENT is not scanned (only git metadata) — a blob that happens to hold the token is repo data.
        var objects = Path.Combine(checkout, ".git", "objects", "ab");
        Directory.CreateDirectory(objects);
        File.WriteAllText(Path.Combine(objects, "cdef"), "SENTINEL-TOKEN-4f1c2b");

        Assert.IsTrue(provider.ScrubAndVerifyGitDirs(checkout, out var finding), finding);
        Assert.IsFalse(File.Exists(top));
        Assert.IsFalse(File.Exists(nested), "a nested submodule git dir's FETCH_HEAD is scrubbed too");
    }

    private CloningCheckoutProvider NewProvider(string token)
    {
        var dataRoot = ServiceTestFixtures.NewDataRoot();
        _tempDirs.Add(dataRoot);
        var paths = new ServicePaths(ServiceVolumes.Rooted(dataRoot));
        return new CloningCheckoutProvider(new PersistentVolumeCheckoutProvider(paths), paths, token: token);
    }

    private string NewStagedCheckout()
    {
        var checkout = Temp();
        var gitDir = Path.Combine(checkout, ".git");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "0123456789012345678901234567890123456789\n");
        File.WriteAllText(Path.Combine(gitDir, "config"), "[remote \"origin\"]\n\turl = https://github.com/o/r.git\n");
        return checkout;
    }

    private string Temp()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sextant_subhelp_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }
}
