using ProcessStack.Abstractions;

namespace Sextant.ProcessStack.Activities;

/// <summary>
/// Turns a repository reference from an event or a GitHub activity into the URL the Sextant service is
/// sent and the key flows compare repositories by. Pure computation over <c>Sextant.Core</c>'s
/// <see cref="Sextant.Core.GitRemoteNormalizer"/> and <see cref="Sextant.Core.RemoteUrlIdentity"/>.
/// </summary>
[ActivityName(TypeName)]
[ActivityDescription("Normalizes a repository reference to the Sextant service's URL spelling and policy key (pure computation).")]
public sealed class SextantNormalizeRepositoryActivity : AbstractActivity
{
    /// <summary>The activity name flows use (<c>activity:SextantNormalizeRepository</c>).</summary>
    public const string TypeName = "SextantNormalizeRepository";

    /// <summary>The reason when no input names a repository.</summary>
    public const string MissingRepository = "missing_repository";

    [ActivityInput("cloneUrl", Description = "The repository's clone URL (GitHub clone_url). Sent verbatim when it is https; an ssh remote becomes https. Wins over the other inputs.")]
    public string? CloneUrl { get; set; }

    [ActivityInput("htmlUrl", Description = "The repository's web URL (GitHub html_url); used with .git appended when cloneUrl is empty.")]
    public string? HtmlUrl { get; set; }

    [ActivityInput("owner", Description = "The repository owner; used with repo when neither URL is given.")]
    public string? Owner { get; set; }

    [ActivityInput("repo", Description = "The repository name (or owner/repo when owner is empty).")]
    public string? Repo { get; set; }

    [ActivityInput("defaultHost", Description = "The host for owner/repo (default github.com).", DefaultValue = RepositoryReference.DefaultHost)]
    public string? DefaultHost { get; set; } = RepositoryReference.DefaultHost;

    [ActivityInput("branch", Description = "Optional branch name to check with the chat parser's rule: a leading refs/heads/ is dropped, then git check-ref-format --branch. Independent of the URL verdict.")]
    public string? Branch { get; set; }

    [ActivityOutput("remoteUrl", Description = "The URL to send as repository_remote_url / repository; \"\" when not accepted.")]
    public string RemoteUrl { get; set; } = string.Empty;

    [ActivityOutput("repositoryKey", Description = "The service's SVC-5 policy key https://{host}/{owner}/{repo}, case-folded on case-insensitive hosts; \"\" when not accepted.")]
    public string RepositoryKey { get; set; } = string.Empty;

    [ActivityOutput("host", Description = "The lower-cased host; \"\" when not accepted.")]
    public string Host { get; set; } = string.Empty;

    [ActivityOutput("repositoryOwner", Description = "The owner as spelled in the URL; \"\" when not accepted.")]
    public string RepositoryOwner { get; set; } = string.Empty;

    [ActivityOutput("repositoryName", Description = "The repository name as spelled in the URL, without .git; \"\" when not accepted.")]
    public string RepositoryName { get; set; } = string.Empty;

    [ActivityOutput("accepted", Description = "True when the URL passes the service's SVC-5 shape rules (the service's host and owner allow-lists still apply).")]
    public bool Accepted { get; set; }

    [ActivityOutput("reason", Description = "Why the URL is not accepted: an SVC-5 reason code, or missing_repository; \"\" when accepted.")]
    public string Reason { get; set; } = string.Empty;

    [ActivityOutput("branchName", Description = "The checked branch name (without refs/heads/); \"\" when branch is empty or invalid.")]
    public string BranchName { get; set; } = string.Empty;

    [ActivityOutput("branchValid", Description = "True when branch is empty or a valid branch name; false when a branch was named and is invalid.")]
    public bool BranchValid { get; set; } = true;

    public SextantNormalizeRepositoryActivity() => Name = TypeName;

    public override Task ExecuteAsync(ActivityContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var remoteUrl = RemoteUrlFrom(
            ActivityValues.Text(ActivityValues.Input(this, "cloneUrl", CloneUrl)),
            ActivityValues.Text(ActivityValues.Input(this, "htmlUrl", HtmlUrl)),
            ActivityValues.Text(ActivityValues.Input(this, "owner", Owner)),
            ActivityValues.Text(ActivityValues.Input(this, "repo", Repo)),
            ActivityValues.Text(ActivityValues.Input(this, "defaultHost", DefaultHost)));

        var verdict = remoteUrl.Length == 0
            ? RepositoryUrlVerdict.Reject(MissingRepository)
            : RepositoryUrlShape.Evaluate(remoteUrl);
        Accepted = verdict.Ok;
        Reason = verdict.Reason;
        RemoteUrl = verdict.Ok ? remoteUrl : string.Empty;
        RepositoryKey = verdict.Canonical;
        Host = verdict.Host;
        RepositoryOwner = verdict.SpelledOwner;
        RepositoryName = verdict.SpelledRepo;

        var named = ActivityValues.Text(ActivityValues.Input(this, "branch", Branch));
        var branch = GitRefs.StripHeadsPrefix(named);
        BranchValid = named.Length == 0 || GitRefs.IsValidBranchName(branch);
        BranchName = BranchValid ? branch : string.Empty;

        // Never log the URL or the branch: a refused URL may carry credentials, and both are caller text.
        if (!verdict.Ok)
            context.LogWarning($"{TypeName}: repository not accepted ({verdict.Reason}).");
        if (!BranchValid)
            context.LogWarning($"{TypeName}: branch name not accepted.");
        return Task.CompletedTask;
    }

    private static string RemoteUrlFrom(string cloneUrl, string htmlUrl, string owner, string repo, string defaultHost)
    {
        if (cloneUrl.Length > 0)
            return RepositoryReference.ToCloneUrl(cloneUrl);
        if (htmlUrl.Length > 0)
            return CloneUrlFromHtmlUrl(htmlUrl);
        if (owner.Length == 0 && repo.Contains('/'))
        {
            var slash = repo.IndexOf('/');
            (owner, repo) = (repo[..slash], repo[(slash + 1)..]);
        }
        if (owner.Length == 0 || repo.Length == 0)
            return string.Empty;
        var host = defaultHost.Length > 0 ? defaultHost.ToLowerInvariant() : RepositoryReference.DefaultHost;
        return RepositoryReference.CloneUrlFor(host, owner, repo);
    }

    // GitHub's clone_url is its html_url plus ".git".
    private static string CloneUrlFromHtmlUrl(string htmlUrl)
    {
        var url = RepositoryReference.ToCloneUrl(htmlUrl);
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return url;
        url = url.TrimEnd('/');
        return url.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? url : url + ".git";
    }
}
