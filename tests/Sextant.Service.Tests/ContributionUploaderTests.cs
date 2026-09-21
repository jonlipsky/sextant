using System.Net;
using Sextant.Service.Contributions;

namespace Sextant.Service.Tests;

/// <summary>
/// The service-optional fail-safe of <see cref="ContributionUploader"/> (acceptance criterion 5): an
/// unreachable or erroring index service NEVER throws — it yields a non-blocking
/// <see cref="ContributionUploadOutcome.ServiceUnavailable"/>, so a normal developer/CI build stays green
/// and only an explicit required-CI policy (the CLI <c>--require</c> flag) can make it fatal. Fully
/// hermetic: a stub <see cref="HttpMessageHandler"/> models each transport failure, no real network.
/// </summary>
[TestClass]
public class ContributionUploaderTests
{
    private static ContributionArtifact Artifact() =>
        ContributionTestFixtures.BuildArtifact(
            "https://github.com/octo/app", "commit-aaaa", "linux-x64|net8.0",
            [new PayloadProjectSpec("src/App/App.csproj", "net8.0")]);

    private static ContributionUploadOptions Options() => new() { ServiceUrl = new Uri("https://index.example.com") };

    [TestMethod]
    public async Task Connection_failure_is_non_blocking_service_unavailable()
    {
        using var client = new HttpClient(new ThrowingHandler(new HttpRequestException("connection refused")));

        var outcome = await ContributionUploader.UploadAsync(client, Artifact(), Options());

        Assert.IsTrue(outcome.ServiceUnavailable);
        Assert.IsFalse(outcome.Accepted);
        StringAssert.Contains(outcome.Message, "unreachable");
    }

    [TestMethod]
    public async Task Timeout_is_non_blocking_service_unavailable()
    {
        using var client = new HttpClient(new ThrowingHandler(new TaskCanceledException("timed out")));

        var outcome = await ContributionUploader.UploadAsync(client, Artifact(), Options());

        Assert.IsTrue(outcome.ServiceUnavailable);
        Assert.IsFalse(outcome.Accepted);
    }

    [TestMethod]
    public async Task Server_error_5xx_is_treated_as_service_unavailable_not_a_rejection()
    {
        using var client = new HttpClient(new StatusHandler(HttpStatusCode.InternalServerError));

        var outcome = await ContributionUploader.UploadAsync(client, Artifact(), Options());

        Assert.IsTrue(outcome.ServiceUnavailable, "a 5xx is transient, never a supply-chain rejection");
        Assert.IsFalse(outcome.Accepted);
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("") });
    }
}
