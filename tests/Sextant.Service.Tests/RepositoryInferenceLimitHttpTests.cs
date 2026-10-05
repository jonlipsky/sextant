using System.Text.Json;
using System.Text.RegularExpressions;
using Sextant.Store;
using static Sextant.Service.Tests.CallerAssertionHttpTests;

namespace Sextant.Service.Tests;

/// <summary>
/// The limit of repository inference: a caller that can read 25 repositories is inferred after one probe per
/// repository; a caller that can read 26 is never probed and gets the ordinary <c>repository_required</c>, which lists
/// its first 20 repositories in name order and says how many more there are. Only <c>acme/repo-01</c> holds the
/// symbol, and it comes first in every order, so any probe at all would find it. The limits are written as numbers,
/// not read from <see cref="Sextant.Service.Host.RepositoryInferenceFilter.MaxRepositories"/>, so moving the limit
/// fails these tests.
/// </summary>
[TestClass]
public class RepositoryInferenceLimitHttpTests
{
    private const string AtTheLimit = "user-25";
    private const string PastTheLimit = "user-26";
    private const string Arguments = """{"name":"Holder.Only.Target"}""";

    private static Harness _host = null!;
    private static string _root = "";

    [ClassInitialize]
    public static async Task StartAsync(TestContext _)
    {
        _root = Path.Combine(Path.GetTempPath(), "sextant-inference-limit-" + Guid.NewGuid().ToString("N"));
        _host = await Harness.StartAsync(
            configure: o => o with { RequireRepositorySelection = true },
            seed: db =>
            {
                RepositoryInferenceHttpTests.Index(db, _root, Url(1), "commit-01", HolderSources);
                for (var i = 2; i <= 26; i++)
                    PublishDefaultBranch(db, Url(i), $"commit-{i:D2}");
            });
        for (var i = 1; i <= 26; i++)
        {
            if (i <= 25)
                await GrantAsync(AtTheLimit, Url(i));
            await GrantAsync(PastTheLimit, Url(i));
        }
    }

    [ClassCleanup]
    public static async Task StopAsync()
    {
        await _host.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [TestMethod]
    public async Task TwentyFiveReadableRepositories_AreEachProbedOnce_AndTheHolderIsInferred()
    {
        var before = _host.InferenceProbes();

        var call = await CallAsync(AtTheLimit);

        Assert.IsFalse(call.IsError, call.Body.ToString());
        var snapshot = call.Body.GetProperty("meta").GetProperty("snapshot");
        Assert.AreEqual("github.com/acme/repo-01", snapshot.GetProperty("repository").GetString(), call.Body.ToString());
        Assert.AreEqual("inferred", snapshot.GetProperty("repository_selection").GetString(), call.Body.ToString());
        Assert.AreEqual(25, _host.InferenceProbes() - before, "each readable repository is probed once");
    }

    [TestMethod]
    public async Task TwentySixReadableRepositories_AreNeverProbed_AndTheCallIsTheOrdinaryRepositoryRequired()
    {
        var before = _host.InferenceProbes();

        var call = await CallAsync(PastTheLimit);
        var again = await CallAsync(PastTheLimit);

        Assert.AreEqual(0, _host.InferenceProbes() - before, "a caller past the limit is never probed");
        Assert.IsTrue(call.IsError, call.Body.ToString());
        Assert.AreEqual("repository_required", ErrorCode(call.Body));
        Assert.AreEqual(0, call.Body.GetProperty("results").GetArrayLength());
        Assert.IsFalse(call.Body.ToString().Contains("inferred", StringComparison.Ordinal), call.Body.ToString());

        var message = call.Body.GetProperty("message").GetString()!;
        Assert.IsFalse(message.Contains("This symbol is in", StringComparison.Ordinal), message);
        var listed = Regex.Matches(message, "'acme/repo-(\\d\\d)'").Select(m => int.Parse(m.Groups[1].Value)).ToList();
        CollectionAssert.AreEqual(Enumerable.Range(1, 20).ToList(), listed, message);
        StringAssert.Contains(message, " and 6 more (call list_repositories for all).");
        Assert.AreEqual(WithoutTimestamp(call.Body), WithoutTimestamp(again.Body), "the answer is the same every time");
    }

    private static string Url(int repository) => $"https://github.com/acme/repo-{repository:D2}";

    private static Task<ToolCall> CallAsync(string sub) =>
        _host.CallAsync("find_symbol", Arguments, DelegateToken, _host.UserAssertion(sub: sub));

    private static async Task GrantAsync(string sub, string repository)
    {
        using var response = await _host.ControlAsync(HttpMethod.Put, "/control/grants/self", ControlToken,
            _host.UserAssertion(sub: sub), JsonSerializer.Serialize(new { repository }));
        Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    // A complete snapshot that does not hold the symbol, published as the repository's default branch.
    private static void PublishDefaultBranch(IndexDatabase db, string url, string commit)
    {
        var snapshotId = ServiceTestFixtures.PublishComplete(db, ServiceTestFixtures.Request(repo: url, commit: commit), symbolCount: 1);
        var snapshots = new SnapshotStore(db.GetConnection());
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        snapshots.SetBranchPointer(snapshots.EnsureBranch(snapshots.GetRepositoryId(url)!.Value, "main", true, now), snapshotId, now);
    }

    private static readonly (string Path, string Source)[] HolderSources =
    [
        ("Target.cs", """
            namespace Holder.Only
            {
                public class Target { }
            }
            """)
    ];
}
