using ProcessStack.Abstractions;

namespace Sextant.ProcessStack.Activities;

/// <summary>
/// Diffs the Sextant service's watched branches against GitHub and plans the CAS-guarded calls that bring
/// the service in line: an ensure for every branch whose GitHub head the service does not point at (or that
/// is GitHub's default branch but not the service's default yet, #199), and a retire for every non-default
/// branch GitHub no longer has. Deterministic (sorted, de-duplicated, capped)
/// and pure: the flow gathers the inputs and sends the planned bodies.
/// <para>
/// Every CAS is the commit the service pointed at when resolved, so the flow must resolve every target
/// <b>before</b> it lists GitHub's branches (a default target with GitHub's default branch name, read first).
/// A push the service processes after the resolve then fails the CAS (the plan only attaches); with the
/// listing first, a push processed between the two would pass it, and the ensure would roll the branch back
/// to the stale listing head (or the retire remove a branch the push re-created).
/// </para>
/// </summary>
[ActivityName(TypeName)]
[ActivityDescription("Plans the CAS-guarded ensures and retires that reconcile the Sextant service's branch pointers with GitHub (pure computation).")]
public sealed class SextantPlanReconcileActivity : AbstractActivity
{
    /// <summary>The activity name flows use (<c>activity:SextantPlanReconcile</c>).</summary>
    public const string TypeName = "SextantPlanReconcile";

    public const int DefaultMaxTargets = 1000;
    public const int DefaultMaxEnsures = 100;
    public const int DefaultMaxRetires = 20;
    public const int MaxTargetsLimit = 10_000;
    public const int MaxEnsuresLimit = 1000;
    public const int MaxRetiresLimit = 1000;

    public const string ReasonMissingRepository = "missing_repository";
    public const string ReasonInvalidBranch = "invalid_branch";
    public const string ReasonRepositoryNotListed = "repository_not_listed";
    public const string ReasonDefaultBranchUnknown = "default_branch_unknown";
    public const string ReasonDuplicate = "duplicate";
    public const string ReasonInvalidHead = "invalid_head";
    public const string ReasonResolveFailed = "resolve_failed";
    public const string ReasonResolveBranchMismatch = "resolve_branch_mismatch";
    public const string ReasonResolveMissingCommit = "resolve_missing_commit";
    public const string ReasonDefaultBranchAbsent = "default_branch_absent";
    public const string ReasonDefaultBranch = "default_branch";
    public const string ReasonBranchListingIncomplete = "branch_listing_incomplete";
    public const string ReasonBranchAbsent = "branch_absent";
    public const string ReasonEnsureLimit = "ensure_limit";
    public const string ReasonRetireLimit = "retire_limit";

    [ActivityInput("serviceTargets", Required = true, Description = "The watched (repository, branch) targets, each with its /control/resolve result: {repository, branch ('' = default), resolveStatusCode, resolve}. Resolve every target BEFORE listing githubBranches (the CAS only guards against pushes processed after the resolve), a default target with GitHub's default branch name.")]
    public object? ServiceTargets { get; set; }

    [ActivityInput("githubBranches", Required = true, Description = "GitHub's branches: per repository {repository, defaultBranch, branches:[{name, sha}], truncated?} or flat {repository, name, sha, isDefault?}. A repository without a branches list is treated as unreachable; one marked truncated:true or complete:false is never retired from.")]
    public object? GithubBranches { get; set; }

    [ActivityInput("maxTargets", Description = "At most this many targets are planned (default 1000, at most 10000).", DefaultValue = DefaultMaxTargets)]
    public object? MaxTargets { get; set; }

    [ActivityInput("maxEnsures", Description = "At most this many ensures are planned (default 100, at most 1000).", DefaultValue = DefaultMaxEnsures)]
    public object? MaxEnsures { get; set; }

    [ActivityInput("maxRetires", Description = "At most this many retires are planned (default 20, at most 1000).", DefaultValue = DefaultMaxRetires)]
    public object? MaxRetires { get; set; }

    [ActivityOutput("ensures", Description = "The POST /control/ensure bodies to send (CAS-guarded advance; default_branch is whether the branch is GitHub's default, which is how the service's default is set).")]
    public List<object?> Ensures { get; set; } = [];

    [ActivityOutput("retires", Description = "The POST /control/branches/retire bodies to send (CAS-guarded).")]
    public List<object?> Retires { get; set; } = [];

    [ActivityOutput("truncated", Description = "True when a limit cut the plan short; the next run continues.")]
    public bool Truncated { get; set; }

    [ActivityOutput("truncatedTargets", Description = "How many targets maxTargets left out.")]
    public int TruncatedTargets { get; set; }

    [ActivityOutput("upToDate", Description = "How many targets already point at GitHub's head (GitHub's default must also be the service's default: one resolved with is_default:false is promoted by an ensure whose CAS is its current commit).")]
    public int UpToDate { get; set; }

    [ActivityOutput("skipped", Description = "How many planned targets produced no call (see skippedTargets).")]
    public int Skipped { get; set; }

    [ActivityOutput("skippedTargets", Description = "{repository, branch, reason} for every skipped target.")]
    public List<object?> SkippedTargets { get; set; } = [];

    public SextantPlanReconcileActivity() => Name = TypeName;

    public override Task ExecuteAsync(ActivityContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var listings = GitHubListing.Parse(ActivityValues.AsList(ActivityValues.Input(this, "githubBranches", GithubBranches)));
        var targets = Target.Parse(ActivityValues.AsList(ActivityValues.Input(this, "serviceTargets", ServiceTargets)));
        var maxTargets = ActivityValues.Limit(ActivityValues.Input(this, "maxTargets", MaxTargets), DefaultMaxTargets, MaxTargetsLimit);
        var maxEnsures = ActivityValues.Limit(ActivityValues.Input(this, "maxEnsures", MaxEnsures), DefaultMaxEnsures, MaxEnsuresLimit);
        var maxRetires = ActivityValues.Limit(ActivityValues.Input(this, "maxRetires", MaxRetires), DefaultMaxRetires, MaxRetiresLimit);

        Ensures = [];
        Retires = [];
        SkippedTargets = [];
        UpToDate = 0;
        TruncatedTargets = Math.Max(0, targets.Count - maxTargets);
        Truncated = TruncatedTargets > 0;

        var seen = new HashSet<(string Key, string Branch)>();
        foreach (var target in targets.Take(maxTargets))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Plan(target, listings, seen, maxEnsures, maxRetires);
        }
        Skipped = SkippedTargets.Count;

        context.LogInfo($"{TypeName}: {Ensures.Count} ensure(s), {Retires.Count} retire(s), {UpToDate} up to date, "
            + $"{Skipped} skipped{(Truncated ? ", truncated" : string.Empty)}.");
        return Task.CompletedTask;
    }

    private void Plan(
        Target target, IReadOnlyDictionary<string, GitHubListing> listings, HashSet<(string, string)> seen,
        int maxEnsures, int maxRetires)
    {
        if (target.Key.Length == 0)
        {
            Skip(target, target.Branch, ReasonMissingRepository);
            return;
        }
        if (target.Branch.Length > 0 && !GitRefs.IsValidBranchName(target.Branch))
        {
            Skip(target, target.Branch, ReasonInvalidBranch);
            return;
        }
        if (!listings.TryGetValue(target.Key, out var listing) || !listing.Listed)
        {
            Skip(target, target.Branch, ReasonRepositoryNotListed);
            return;
        }

        var branch = target.Branch.Length > 0 ? target.Branch : listing.DefaultBranch;
        if (branch.Length == 0)
        {
            Skip(target, target.Branch, ReasonDefaultBranchUnknown);
            return;
        }
        if (!seen.Add((target.Key, branch)))
        {
            Skip(target, branch, ReasonDuplicate);
            return;
        }

        var resolvedBranch = ActivityValues.Text(ActivityValues.Get(target.Resolve, "branch"));
        if (target.ResolveStatusCode == 200 && resolvedBranch.Length > 0
            && !string.Equals(resolvedBranch, branch, StringComparison.Ordinal))
        {
            // The resolve answered for another branch (GitHub's default changed between reading its name and
            // listing, or the flow resolved without a branch): its commit says nothing about this branch.
            Skip(target, branch, ReasonResolveBranchMismatch);
            return;
        }

        if (listing.Branches.TryGetValue(branch, out var head))
            PlanPresent(target, listing, branch, head, maxEnsures);
        else
            PlanAbsent(target, listing, branch, maxRetires);
    }

    // GitHub has the branch at `head`: advance the service to it under a CAS on what the service points at.
    private void PlanPresent(Target target, GitHubListing listing, string branch, string head, int maxEnsures)
    {
        if (!GitRefs.IsCommitSha(head) || GitRefs.IsZeroSha(head))
        {
            Skip(target, branch, ReasonInvalidHead);
            return;
        }

        var isGitHubDefault = listing.DefaultBranch.Length > 0
            && string.Equals(branch, listing.DefaultBranch, StringComparison.Ordinal);
        string expected;
        switch (target.ResolveStatusCode)
        {
            case 200:
                var commit = ActivityValues.Text(ActivityValues.Get(target.Resolve, "commit_sha"));
                if (commit.Length == 0)
                {
                    Skip(target, branch, ReasonResolveMissingCommit);
                    return;
                }
                if (string.Equals(commit, head, StringComparison.OrdinalIgnoreCase))
                {
                    // At GitHub's head but not the service's default although it is GitHub's (a user watched it
                    // first, and a user never claims the default: #199). The CAS on the current commit moves
                    // no pointer; default_branch: true only promotes it.
                    if (!isGitHubDefault || ActivityValues.AsBool(ActivityValues.Get(target.Resolve, "is_default")) != false)
                    {
                        UpToDate++;
                        return;
                    }
                }
                expected = commit;
                break;
            case 404:
                expected = string.Empty;
                break;
            default:
                Skip(target, branch, ReasonResolveFailed);
                return;
        }

        if (Ensures.Count >= maxEnsures)
        {
            Truncated = true;
            Skip(target, branch, ReasonEnsureLimit);
            return;
        }
        bool? isDefault = listing.DefaultBranch.Length > 0 ? isGitHubDefault : null;
        Ensures.Add(ServiceRequests.Ensure(
            target.Repository, head, branch, isDefault, expected, null, ServiceRequests.Advance));
    }

    // GitHub no longer has the branch: retire it under a CAS on the commit the service pointed at when
    // resolved (before the listing), so a branch re-created since then (its push moved the pointer) is kept.
    // The default is never retired (the service refuses it too).
    private void PlanAbsent(Target target, GitHubListing listing, string branch, int maxRetires)
    {
        if (target.Branch.Length == 0)
        {
            Skip(target, branch, ReasonDefaultBranchAbsent);
            return;
        }
        if (!listing.Complete)
        {
            Skip(target, branch, ReasonBranchListingIncomplete);
            return;
        }
        switch (target.ResolveStatusCode)
        {
            case 200:
                break;
            case 404:
                Skip(target, branch, ReasonBranchAbsent);
                return;
            default:
                Skip(target, branch, ReasonResolveFailed);
                return;
        }
        if (ActivityValues.AsBool(ActivityValues.Get(target.Resolve, "is_default")) == true
            || string.Equals(branch, listing.DefaultBranch, StringComparison.Ordinal))
        {
            Skip(target, branch, ReasonDefaultBranch);
            return;
        }
        var commit = ActivityValues.Text(ActivityValues.Get(target.Resolve, "commit_sha"));
        if (!GitRefs.IsCommitSha(commit) || GitRefs.IsZeroSha(commit))
        {
            Skip(target, branch, ReasonResolveMissingCommit);
            return;
        }
        if (Retires.Count >= maxRetires)
        {
            Truncated = true;
            Skip(target, branch, ReasonRetireLimit);
            return;
        }
        Retires.Add(ServiceRequests.Retire(target.Repository, branch, commit));
    }

    private void Skip(Target target, string branch, string reason)
    {
        var entry = ActivityValues.NewMap();
        entry["repository"] = target.Repository;
        entry["branch"] = branch;
        entry["reason"] = reason;
        SkippedTargets.Add(entry);
    }

    // One watched target with its resolve result.
    private sealed record Target(
        string Repository, string Key, string Branch, long? ResolveStatusCode,
        IReadOnlyDictionary<string, object?>? Resolve)
    {
        // Sorted by (key, branch, repository) so the plan, its caps and its de-duplication are deterministic
        // whatever order the targets were gathered in.
        public static List<Target> Parse(IReadOnlyList<object?>? items)
        {
            var targets = new List<Target>();
            foreach (var item in items ?? [])
            {
                var map = ActivityValues.AsMap(item);
                if (map is null)
                    continue;
                var repository = ActivityValues.Text(ActivityValues.Get(map, "repository", "repository_remote_url"));
                targets.Add(new Target(
                    repository,
                    RepositoryReference.KeyFor(repository),
                    GitRefs.StripHeadsPrefix(ActivityValues.Text(ActivityValues.Get(map, "branch"))),
                    ActivityValues.AsLong(ActivityValues.Get(map, "resolveStatusCode", "statusCode")),
                    ActivityValues.AsMap(ActivityValues.Get(map, "resolve", "resolveBody", "body"))));
            }
            return
            [
                .. targets
                    .OrderBy(t => t.Key, StringComparer.Ordinal)
                    .ThenBy(t => t.Branch, StringComparer.Ordinal)
                    .ThenBy(t => t.Repository, StringComparer.Ordinal),
            ];
        }
    }

    // GitHub's view of one repository, merged across every entry that names it.
    private sealed class GitHubListing
    {
        public string DefaultBranch { get; private set; } = string.Empty;

        public bool Listed { get; private set; }

        public bool Complete { get; private set; } = true;

        public Dictionary<string, string> Branches { get; } = new(StringComparer.Ordinal);

        public static Dictionary<string, GitHubListing> Parse(IReadOnlyList<object?>? items)
        {
            var listings = new Dictionary<string, GitHubListing>(StringComparer.Ordinal);
            foreach (var item in items ?? [])
            {
                var map = ActivityValues.AsMap(item);
                var key = RepositoryReference.KeyFor(
                    ActivityValues.Text(ActivityValues.Get(map, "repository", "cloneUrl", "repository_remote_url")));
                if (map is null || key.Length == 0)
                    continue;
                if (!listings.TryGetValue(key, out var listing))
                    listings[key] = listing = new GitHubListing();
                listing.Add(map);
            }
            return listings;
        }

        private void Add(IReadOnlyDictionary<string, object?> map)
        {
            SetDefault(ActivityValues.Text(ActivityValues.Get(map, "defaultBranch", "default_branch")));
            if (ActivityValues.AsBool(ActivityValues.Get(map, "truncated")) == true
                || ActivityValues.AsBool(ActivityValues.Get(map, "complete")) == false)
                Complete = false;
            if (map.ContainsKey("branches"))
            {
                if (ActivityValues.AsList(map["branches"]) is not { } branches)
                    return;
                Listed = true;
                foreach (var branch in branches)
                    AddBranch(ActivityValues.AsMap(branch));
                return;
            }
            if (AddBranch(map) is { } name)
            {
                Listed = true;
                if (ActivityValues.AsBool(ActivityValues.Get(map, "isDefault", "is_default")) == true)
                    SetDefault(name);
            }
        }

        private string? AddBranch(IReadOnlyDictionary<string, object?>? branch)
        {
            var name = GitRefs.StripHeadsPrefix(ActivityValues.Text(ActivityValues.Get(branch, "name", "branch")));
            if (name.Length == 0)
                return null;
            var sha = ActivityValues.Text(ActivityValues.Get(branch, "sha", "commitSha", "commit_sha"));
            if (sha.Length == 0)
                sha = ActivityValues.Text(ActivityValues.Get(ActivityValues.AsMap(ActivityValues.Get(branch, "commit")), "sha"));
            Branches.TryAdd(name, sha);
            return name;
        }

        private void SetDefault(string branch)
        {
            if (DefaultBranch.Length == 0 && branch.Length > 0)
                DefaultBranch = GitRefs.StripHeadsPrefix(branch);
        }
    }
}
