using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Sextant.Store;

namespace Sextant.Service.Contributions;

/// <summary>Options controlling how a captured artifact is uploaded to the index service.</summary>
public sealed record ContributionUploadOptions
{
    /// <summary>The service base URL (e.g. <c>https://index.example.com</c>).</summary>
    public required Uri ServiceUrl { get; init; }

    /// <summary>The contributor bearer token (control-plane auth + authorizer principal).</summary>
    public string? Token { get; init; }

    /// <summary>Whether this upload FINALIZES (publishes) the assembly snapshot (default true).</summary>
    public bool Finalize { get; init; } = true;

    /// <summary>The branch to advance to the published snapshot (optional).</summary>
    public string? BranchName { get; init; }

    /// <summary>Whether <see cref="BranchName"/> is the repository's default branch.</summary>
    public bool IsDefaultBranch { get; init; }
}

/// <summary>The disposition of an upload attempt, distinguishing a service-unavailable outcome from a reject.</summary>
public sealed record ContributionUploadOutcome
{
    /// <summary>True when the service accepted (published/assembling/duplicate) the contribution.</summary>
    public required bool Accepted { get; init; }

    /// <summary>True when the service could not be reached at all (network/timeouts) — never a build failure.</summary>
    public bool ServiceUnavailable { get; init; }

    /// <summary>The parsed service result, when the service responded.</summary>
    public IngestContributionResult? Result { get; init; }

    /// <summary>A human-readable summary (result message or the unavailability reason).</summary>
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Uploads a content-addressed contribution artifact to the index service's <c>/control/contribute</c>
/// endpoint (Phase 16). Upload is OPT-IN and NEVER required for a normal build (acceptance criterion 5):
/// a service that is unreachable yields <see cref="ContributionUploadOutcome.ServiceUnavailable"/> rather
/// than throwing, so the caller decides whether that is fatal (only a repository that explicitly opts into
/// a required-CI policy makes it blocking). The artifact is content-addressed, so re-uploading the same
/// artifact is a server-side no-op (criterion 2). Resumable/chunked transfer is a tracked follow-up; this
/// posts the artifact as one request body.
/// </summary>
public static class ContributionUploader
{
    public static async Task<ContributionUploadOutcome> UploadAsync(
        HttpClient client, ContributionArtifact artifact, ContributionUploadOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(options);

        var query = $"?finalize={(options.Finalize ? "true" : "false")}";
        if (!string.IsNullOrWhiteSpace(options.BranchName))
            query += $"&branch={Uri.EscapeDataString(options.BranchName)}&default_branch={(options.IsDefaultBranch ? "true" : "false")}";

        var requestUri = new Uri(options.ServiceUrl, "/control/contribute" + query);
        using var content = new ByteArrayContent(artifact.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var message = new HttpRequestMessage(HttpMethod.Post, requestUri) { Content = content };
        if (!string.IsNullOrWhiteSpace(options.Token))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return Unavailable($"the index service at {options.ServiceUrl} is unreachable: {ex.Message}");
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable($"the index service at {options.ServiceUrl} timed out: {ex.Message}");
        }

        using (response)
        {
            // A 5xx is treated as service-unavailable (transient), never a supply-chain rejection.
            if ((int)response.StatusCode >= 500)
                return Unavailable($"the index service returned {(int)response.StatusCode}.");

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var result = TryParse(body);
            var accepted = response.StatusCode == HttpStatusCode.OK && result is { Accepted: true };
            return new ContributionUploadOutcome
            {
                Accepted = accepted,
                Result = result,
                Message = result?.Message ?? result?.Status ?? $"HTTP {(int)response.StatusCode}"
            };
        }
    }

    private static IngestContributionResult? TryParse(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            return JsonSerializer.Deserialize<IngestContributionResult>(body, ServiceJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ContributionUploadOutcome Unavailable(string message) => new()
    {
        Accepted = false,
        ServiceUnavailable = true,
        Message = message
    };
}
