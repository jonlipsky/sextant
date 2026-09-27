namespace Sextant.Service;

/// <summary>
/// How the cloning checkout provider handled ONE submodule declared by a checkout's <c>.gitmodules</c>
/// (issue #125). Recorded in the checkout's provisioning marker (<c>.git/sextant-checkout.json</c>) and
/// surfaced on <see cref="CheckoutResolution.SubmoduleProvisioning"/>, so an unpopulated submodule's coverage
/// gap carries a concrete, credential-free reason instead of a generic "not populated".
/// </summary>
public sealed record SubmoduleProvisioningOutcome
{
    /// <summary>The submodule path relative to the checkout root, using <c>/</c> separators.</summary>
    public required string Path { get; init; }

    /// <summary>The submodule's <c>.gitmodules</c> name (sanitized for display).</summary>
    public required string Name { get; init; }

    /// <summary>One of the <see cref="SubmoduleProvisioningStatus"/> values.</summary>
    public required string Status { get; init; }

    /// <summary>The clean (credential-free) URL that was fetched or refused-after-resolution; null when none.</summary>
    public string? Url { get; init; }

    /// <summary>The pinned gitlink commit, when one was found.</summary>
    public string? Commit { get; init; }

    /// <summary>A short, token-redacted explanation for any status other than <c>populated</c>.</summary>
    public string? Reason { get; init; }

    /// <summary>True when the submodule was checked out at its gitlink commit.</summary>
    public bool IsPopulated => Status == SubmoduleProvisioningStatus.Populated;
}

/// <summary>The stable wire values of <see cref="SubmoduleProvisioningOutcome.Status"/>.</summary>
public static class SubmoduleProvisioningStatus
{
    /// <summary>Checked out at the gitlink commit and registered in its parent.</summary>
    public const string Populated = "populated";

    /// <summary>The <c>.gitmodules</c> URL was refused by the host/transport policy.</summary>
    public const string UrlRefused = "url_refused";

    /// <summary>The fetch failed permanently (repository not found, authentication refused, …).</summary>
    public const string FetchFailed = "fetch_failed";

    /// <summary>The fetch succeeded but the gitlink commit could not be checked out (missing commit).</summary>
    public const string CheckoutFailed = "checkout_failed";

    /// <summary>The <c>.gitmodules</c> entry is unsafe or unusable (bad name/path, non-empty directory, no url).</summary>
    public const string InvalidEntry = "invalid_entry";

    /// <summary>The <c>.gitmodules</c> entry has no gitlink at its path in the parent commit.</summary>
    public const string NoGitlink = "no_gitlink";

    /// <summary>Skipped because the nesting depth or total submodule limit was reached.</summary>
    public const string LimitExceeded = "limit_exceeded";

    /// <summary>A short human label for a status (used in coverage reasons).</summary>
    public static string Describe(string status) => status switch
    {
        Populated => "populated",
        UrlRefused => "url refused",
        FetchFailed => "fetch failed",
        CheckoutFailed => "pinned commit not found",
        InvalidEntry => "invalid .gitmodules entry",
        NoGitlink => "no gitlink",
        LimitExceeded => "submodule limit reached",
        _ => status
    };
}
