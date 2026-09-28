using Sextant.Service;

namespace Sextant.ProcessStack.Activities.Tests;

/// <summary>
/// Pins <see cref="RepositoryUrlShape"/> to the service's SVC-5 <see cref="RepositoryUrlPolicy"/> with every
/// host allowed, so a URL the app accepts is one the service's shape rules accept (and with the same key).
/// </summary>
[TestClass]
public sealed class RepositoryUrlShapeParityTests
{
    private static readonly RepositoryUrlPolicy AnyHost = new([RepositoryUrlPolicy.Wildcard]);

    public static IEnumerable<object[]> Corpus =>
    [
        ["https://github.com/octo/repo"],
        ["https://github.com/octo/repo.git"],
        ["https://github.com/Octo/Repo.GIT"],
        ["HTTPS://GitHub.com/octo/repo"],
        ["https://github.com:443/octo/repo"],
        ["https://github.com:8443/octo/repo"],
        ["https://github.com:/octo/repo"],
        ["https://user@github.com/octo/repo"],
        ["https://user:token@github.com/octo/repo"],
        ["https://github.com/octo/repo?x=1"],
        ["https://github.com/octo/repo#readme"],
        ["https://github.com/octo/repo/"],
        ["https://github.com/octo/repo/tree/main"],
        ["https://github.com/octo"],
        ["https://github.com/"],
        ["https://github.com"],
        ["https://github.com//repo"],
        ["https://github.com/octo/repo.git.git"],
        ["https://github.com/octo/.git"],
        ["https://github.com/-octo/repo"],
        ["https://github.com/octo/-repo"],
        ["https://github.com/./repo"],
        ["https://github.com/../repo"],
        ["https://github.com/oc to/repo"],
        ["https://github.com/octo/re%20po"],
        ["https://github.com/octo/repo\\x"],
        ["https://github.com/octo/r\u00e9po"],
        ["https://github.com/octo/" + new string('r', 100)],
        ["https://github.com/octo/" + new string('r', 101)],
        ["https://github.com/octo/repo\n"],
        ["https://git\u212Aub.com/octo/repo"],
        ["https://gith\u00fcb.com/octo/repo"],
        ["https://localhost/octo/repo"],
        ["https://a.localhost/octo/repo"],
        ["https://github/octo/repo"],
        ["https://github.com./octo/repo"],
        ["https://-github.com/octo/repo"],
        ["https://github-.com/octo/repo"],
        ["https://git_hub.com/octo/repo"],
        ["https://127.0.0.1/octo/repo"],
        ["https://127.1/octo/repo"],
        ["https://0x7f.1/octo/repo"],
        ["https://example.0x/octo/repo"],
        ["https://example.123/octo/repo"],
        ["https://123.example/octo/repo"],
        ["https://[::1]/octo/repo"],
        ["https://ghe.example.com/octo/repo.git"],
        ["https://" + new string('a', 64) + ".com/octo/repo"],
        ["http://github.com/octo/repo"],
        ["ssh://git@github.com/octo/repo.git"],
        ["git@github.com:octo/repo.git"],
        ["file:///tmp/repo"],
        ["github.com/octo/repo"],
        ["https:/github.com/octo/repo"],
        [""],
        ["https://github.com/octo/" + new string('r', 2100)],
    ];

    [TestMethod]
    [DynamicData(nameof(Corpus))]
    public void Shape_verdict_matches_the_service_policy(string url)
    {
        var expected = AnyHost.Evaluate(url);
        var actual = RepositoryUrlShape.Evaluate(url);

        Assert.AreEqual(expected.Ok, actual.Ok, url);
        Assert.AreEqual(expected.Reason ?? string.Empty, actual.Reason, url);
        Assert.AreEqual(expected.Canonical ?? string.Empty, actual.Canonical, url);
        Assert.AreEqual(expected.Host ?? string.Empty, actual.Host, url);
        Assert.AreEqual(expected.Owner ?? string.Empty, actual.Owner, url);
        Assert.AreEqual(expected.Repo ?? string.Empty, actual.Repo, url);
    }

    [TestMethod]
    public void Null_is_refused_like_the_service()
    {
        Assert.AreEqual(AnyHost.Evaluate(null).Reason, RepositoryUrlShape.Evaluate(null).Reason);
    }

    [TestMethod]
    public void Reason_codes_are_the_service_codes()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                RepositoryUrlRejection.SchemeNotAllowed, RepositoryUrlRejection.UrlComponentNotAllowed,
                RepositoryUrlRejection.HostNotAllowed, RepositoryUrlRejection.PathNotAllowed,
            },
            new[]
            {
                RepositoryUrlShape.SchemeNotAllowed, RepositoryUrlShape.UrlComponentNotAllowed,
                RepositoryUrlShape.HostNotAllowed, RepositoryUrlShape.PathNotAllowed,
            });
        CollectionAssert.AreEqual(new[] { RepositoryUrlPolicy.MaxUrlLength }, new[] { RepositoryUrlShape.MaxUrlLength });
    }

    [TestMethod]
    public void Spelled_segments_keep_the_submitted_case_without_the_git_suffix()
    {
        var verdict = RepositoryUrlShape.Evaluate("https://GitHub.com/Octo/Repo.Git");

        Assert.IsTrue(verdict.Ok);
        Assert.AreEqual("github.com", verdict.Host);
        Assert.AreEqual("Octo", verdict.SpelledOwner);
        Assert.AreEqual("Repo", verdict.SpelledRepo);
        Assert.AreEqual("https://github.com/octo/repo", verdict.Canonical);
    }

    [TestMethod]
    public void A_case_sensitive_host_keeps_the_path_case_in_the_key()
    {
        var verdict = RepositoryUrlShape.Evaluate("https://git.example.com/Octo/Repo.git");

        Assert.IsTrue(verdict.Ok);
        Assert.AreEqual(AnyHost.Evaluate("https://git.example.com/Octo/Repo.git").Canonical, verdict.Canonical);
    }
}
