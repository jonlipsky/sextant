using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Sextant.Service.Host;
using Sextant.Store;
using static Sextant.Service.Tests.CallerAssertionHttpTests;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #198 (item 4): the #193 default-deny for user callers on <c>/control/*</c> (and the control token itself)
/// holds on a REAL Kestrel socket, whose request-target handling (percent-decoding, dot-segment removal, empty
/// segments, path parameters, case, absolute-form targets) the in-memory TestServer never runs. Each target is sent
/// byte-for-byte over TCP, because <see cref="HttpClient"/> would normalize it first. A target Kestrel normalizes
/// onto the retire route gets the same refusal as the route itself; every other one matches no route (404). None
/// ever retires the branch.
/// </summary>
[TestClass]
public class KestrelControlPathTests
{
    private const string Feature = "feature";

    /// <summary>Targets Kestrel normalizes onto <c>/control/branches/retire</c>, so the middleware sees the route.</summary>
    private static readonly string[] NormalizedOntoRetire =
    [
        "/control/branches/retire",
        "/control/branches/%2e%2e/branches/retire",
        "/control/branches/%2E%2E/branches/retire",
        "/control/x/../branches/retire",
        "/control/branches/./retire",
        "/%63ontrol/branches/retire",
        "/CONTROL/BRANCHES/RETIRE",
        "{absolute}/control/branches/retire"
    ];

    /// <summary>Targets that stay under <c>/control</c> but match no route: still behind the token and the user rule.</summary>
    private static readonly string[] UnroutedUnderControl =
    [
        "/control/branches/retire;x",
        "/control/branches//retire",
        "/control/branches/retire%2f"
    ];

    /// <summary>Targets outside <c>/control</c> after normalization: they match no route at all.</summary>
    private static readonly string[] Unrouted =
    [
        "//control/branches/retire",
        "/x/..%2fcontrol/branches/retire",
        "/control%2fbranches/retire"
    ];

    [TestMethod]
    public async Task UserCaller_OnARealSocket_NeverReachesRetire_HoweverTheTargetIsSpelled()
    {
        long featureSnapshot = 0;
        await using var host = await Harness.StartAsync(
            seed: db => featureSnapshot = GrantServiceTests.PublishOnBranch(db, Widgets, "commit-w2", Feature, isDefault: false),
            kestrel: true);
        var body = JsonSerializer.Serialize(new { repository = Widgets, branch = Feature, expected_head_commit = "commit-w2" });

        foreach (var target in NormalizedOntoRetire.Concat(UnroutedUnderControl))
        {
            var (status, text) = await SendRawAsync(host, target, ControlToken, host.UserAssertion(), body);
            Assert.AreEqual(403, status, $"{target}: {text}");
            StringAssert.Contains(text, CallerAssertionGate.NotAllowedCode, target);
        }
        foreach (var target in Unrouted)
        {
            var (status, text) = await SendRawAsync(host, target, ControlToken, host.UserAssertion(), body);
            Assert.AreEqual(404, status, $"{target}: {text}");
        }

        var head = host.Service.ResolveBranchHead(Widgets, Feature);
        Assert.IsNotNull(head, "the feature branch still resolves");
        Assert.AreEqual(featureSnapshot, head.Snapshot.Id, "the feature pointer did not move");
        var rows = host.Service.RecentAudit(action: AuditAction.Retire);
        Assert.AreEqual(NormalizedOntoRetire.Length, rows.Count, "each target that reached the route is audited as refused");
        Assert.IsTrue(rows.All(r => r.Outcome == AuditOutcome.Denied));
    }

    [TestMethod]
    public async Task NoControlToken_OnARealSocket_IsNeverServed_HoweverTheTargetIsSpelled()
    {
        long featureSnapshot = 0;
        await using var host = await Harness.StartAsync(
            seed: db => featureSnapshot = GrantServiceTests.PublishOnBranch(db, Widgets, "commit-w2", Feature, isDefault: false),
            kestrel: true);
        var body = JsonSerializer.Serialize(new { repository = Widgets, branch = Feature });

        foreach (var target in NormalizedOntoRetire.Concat(UnroutedUnderControl))
        {
            var (status, text) = await SendRawAsync(host, target, bearer: null, assertion: null, body);
            Assert.AreEqual(401, status, $"{target}: {text}");
        }
        foreach (var target in Unrouted)
        {
            var (status, text) = await SendRawAsync(host, target, bearer: null, assertion: null, body);
            Assert.AreEqual(404, status, $"{target}: {text}");
        }

        var head = host.Service.ResolveBranchHead(Widgets, Feature);
        Assert.IsNotNull(head, "the feature branch still resolves");
        Assert.AreEqual(featureSnapshot, head.Snapshot.Id, "the feature pointer did not move");
        Assert.AreEqual(0, host.Service.RecentAudit(action: AuditAction.Retire).Count, "nothing reached the retire route");
    }

    /// <summary>
    /// Sends one POST whose request target is written verbatim (<c>{absolute}</c> expands to the absolute-form
    /// origin) and returns the status code plus the raw response text (headers and body, possibly chunked).
    /// </summary>
    private static async Task<(int Status, string Text)> SendRawAsync(
        Harness host, string target, string? bearer, string? assertion, string body)
    {
        var address = host.ListenAddress ?? throw new InvalidOperationException("the harness was not started on Kestrel");
        var requestTarget = target.Replace("{absolute}", address.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal);
        var content = Encoding.UTF8.GetBytes(body);
        var head = new StringBuilder()
            .Append("POST ").Append(requestTarget).Append(" HTTP/1.1\r\n")
            .Append("Host: ").Append(address.Authority).Append("\r\n")
            .Append("Content-Type: application/json\r\n")
            .Append("Content-Length: ").Append(content.Length).Append("\r\n")
            .Append("Connection: close\r\n");
        if (bearer is not null)
            head.Append("Authorization: Bearer ").Append(bearer).Append("\r\n");
        if (assertion is not null)
            head.Append(Header).Append(": ").Append(assertion).Append("\r\n");
        head.Append("\r\n");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(address.Host, address.Port, timeout.Token);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), timeout.Token);
        await stream.WriteAsync(content, timeout.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = await reader.ReadToEndAsync(timeout.Token);
        var statusLine = text.Split("\r\n", 2)[0];
        var parts = statusLine.Split(' ', 3);
        Assert.IsTrue(parts.Length >= 2 && int.TryParse(parts[1], out _), $"{target}: unexpected status line '{statusLine}'");
        return (int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), text);
    }
}
