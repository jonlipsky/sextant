using ProcessStack.Abstractions;

namespace Sextant.ProcessStack.Activities;

/// <summary>
/// Decides what one GitHub repository event means for the Sextant service: ensure a snapshot (and, for a
/// branch push, advance the branch under the <c>expected_head_commit</c> CAS), retire a branch, or ignore it.
/// It emits the request bodies; it performs no I/O. <b>CAS mode only</b>: a push that can advance a branch
/// always carries <c>expected_head_commit</c> (its <c>before</c>, all zeros → <c>""</c>), and anything that
/// cannot be ordered (a PR, a push without a usable <c>before</c>) is sent with <c>branch_update: none</c>.
/// It never emits <c>branch_head_sequence</c>, so the service's <c>conflicting_branch_guards</c> refusal
/// cannot occur.
/// </summary>
[ActivityName(TypeName)]
[ActivityDescription("Plans the Sextant service call for a GitHub repository event: ensure (CAS-guarded), retire or ignore (pure computation).")]
public sealed class SextantPlanRepositoryChangeActivity : AbstractActivity
{
    /// <summary>The activity name flows use (<c>activity:SextantPlanRepositoryChange</c>).</summary>
    public const string TypeName = "SextantPlanRepositoryChange";

    public const string ActionEnsure = "ensure";
    public const string ActionRetire = "retire";
    public const string ActionIgnore = "ignore";

    public const string ReasonMissingRepository = "missing_repository";
    public const string ReasonUnsupportedEvent = "unsupported_event";
    public const string ReasonUnsupportedAction = "unsupported_action";
    public const string ReasonTag = "tag";
    public const string ReasonNotABranch = "not_a_branch";
    public const string ReasonMissingBranch = "missing_branch";
    public const string ReasonInvalidBranch = "invalid_branch";
    public const string ReasonInvalidAfter = "invalid_after";
    public const string ReasonMissingBefore = "missing_before";
    public const string ReasonHandledByPush = "handled_by_push";
    public const string ReasonPullRequestClosed = "pull_request_closed";
    public const string ReasonHeadRepositoryDeleted = "head_repository_deleted";
    public const string ReasonForkHead = "fork_head";
    public const string ReasonInvalidSha = "invalid_sha";

    private const string KindPush = "push";
    private const string KindCreate = "create";
    private const string KindDelete = "delete";
    private const string KindPullRequest = "pr";
    private const string PullRequestEvent = "pull_request";

    [ActivityInput("metadata", Description = "The PS-5 GitHub event metadata map (trigger metadata). The individual inputs below override its keys.")]
    public object? Metadata { get; set; }

    [ActivityInput("kind", Description = "push | create | delete | pr (pull_request). Derived from event when empty.")]
    public string? Kind { get; set; }

    [ActivityInput("event", Description = "The PS-5 event name (push, create, delete, pull_request or pull_request.<action>).")]
    public string? Event { get; set; }

    [ActivityInput("eventAction", Description = "The pull request action (opened, synchronize, reopened, closed); the metadata key is action.")]
    public string? EventAction { get; set; }

    [ActivityInput("cloneUrl", Description = "The repository's clone URL, sent verbatim.")]
    public string? CloneUrl { get; set; }

    [ActivityInput("defaultBranch", Description = "The repository's default branch; empty when unknown.")]
    public string? DefaultBranch { get; set; }

    [ActivityInput("ref", Description = "The pushed or deleted ref (refs/heads/… or refs/tags/…).")]
    public string? Ref { get; set; }

    [ActivityInput("refType", Description = "branch | tag | empty.")]
    public string? RefType { get; set; }

    [ActivityInput("branch", Description = "The short branch name; derived from ref when empty.")]
    public string? Branch { get; set; }

    [ActivityInput("before", Description = "The push's previous head commit (all zeros for a branch create).")]
    public string? Before { get; set; }

    [ActivityInput("after", Description = "The push's new head commit (all zeros for a branch delete).")]
    public string? After { get; set; }

    [ActivityInput("forced", Description = "Whether the push was forced (informational: audited, never bypasses the CAS).")]
    public object? Forced { get; set; }

    [ActivityInput("deleted", Description = "Whether the push deleted the ref.")]
    public object? Deleted { get; set; }

    [ActivityInput("baseSha", Description = "The pull request's base commit.")]
    public string? BaseSha { get; set; }

    [ActivityInput("baseRef", Description = "The pull request's base branch.")]
    public string? BaseRef { get; set; }

    [ActivityInput("headSha", Description = "The pull request's head commit.")]
    public string? HeadSha { get; set; }

    [ActivityInput("headRef", Description = "The pull request's head branch.")]
    public string? HeadRef { get; set; }

    [ActivityInput("headCloneUrl", Description = "The pull request head repository's clone URL; empty when that repository was deleted. A head in another repository (a fork) is not published (fork_head).")]
    public string? HeadCloneUrl { get; set; }

    [ActivityOutput("action", Description = "ensure | retire | ignore.")]
    public string Action { get; set; } = ActionIgnore;

    [ActivityOutput("ensureBody", Description = "The POST /control/ensure body when action is ensure (the first of ensureBodies); otherwise null.")]
    public Dictionary<string, object?>? EnsureBody { get; set; }

    [ActivityOutput("ensureBodies", Description = "Every ensure body to send, in order (a pull request yields base then head, the head only when it is in the base repository).")]
    public List<object?> EnsureBodies { get; set; } = [];

    [ActivityOutput("retireBody", Description = "The POST /control/branches/retire body when action is retire; otherwise null.")]
    public Dictionary<string, object?>? RetireBody { get; set; }

    [ActivityOutput("reason", Description = "Why the event is ignored or degraded (for example tag, missing_before); \"\" otherwise.")]
    public string Reason { get; set; } = string.Empty;

    [ActivityOutput("repository", Description = "The repository URL the bodies name; \"\" when none.")]
    public string Repository { get; set; } = string.Empty;

    [ActivityOutput("branchName", Description = "The branch the push or delete names; \"\" otherwise.")]
    public string BranchName { get; set; } = string.Empty;

    [ActivityOutput("isDefaultBranch", Description = "True when branch is the repository's known default branch.")]
    public bool IsDefaultBranch { get; set; }

    public SextantPlanRepositoryChangeActivity() => Name = TypeName;

    public override Task ExecuteAsync(ActivityContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var metadata = ActivityValues.AsMap(ActivityValues.Input(this, "metadata", Metadata));
        var change = new Change(this, metadata);
        Reset();
        switch (change.Kind)
        {
            case KindPush:
                PlanPush(change);
                break;
            case KindCreate:
                Ignore(ReasonHandledByPush);
                break;
            case KindDelete:
                PlanDelete(change);
                break;
            case KindPullRequest:
                PlanPullRequest(change);
                break;
            default:
                Ignore(ReasonUnsupportedEvent);
                break;
        }

        context.LogInfo(Reason.Length == 0 ? $"{TypeName}: {Action}." : $"{TypeName}: {Action} ({Reason}).");
        return Task.CompletedTask;
    }

    private void PlanPush(Change change)
    {
        if (!TryRepository(change.CloneUrl) || !TryBranch(change))
            return;

        if (change.Deleted)
        {
            // A delete push's `before` is the deleted head; retire under the CAS so a stale delete cannot
            // remove a branch a newer push re-created.
            var casHead = GitRefs.IsCommitSha(change.Before) && !GitRefs.IsZeroSha(change.Before) ? change.Before : null;
            Retire(casHead, casHead is null ? ReasonMissingBefore : string.Empty);
            return;
        }

        if (!GitRefs.IsCommitSha(change.After) || GitRefs.IsZeroSha(change.After))
        {
            Ignore(ReasonInvalidAfter);
            return;
        }

        bool? isDefault = change.DefaultBranch.Length > 0 ? IsDefaultBranch : null;
        if (GitRefs.IsCommitSha(change.Before))
        {
            Ensure(ServiceRequests.Ensure(
                Repository, change.After, BranchName, isDefault, ServiceRequests.ExpectedHead(change.Before),
                change.Forced, ServiceRequests.Advance));
            return;
        }

        // No usable `before`: the push cannot be ordered, so publish without moving the pointer and let the
        // reconcile advance the branch under its own CAS.
        Ensure(ServiceRequests.Ensure(
            Repository, change.After, BranchName, isDefault, null, change.Forced, ServiceRequests.None));
        Reason = ReasonMissingBefore;
    }

    private void PlanDelete(Change change)
    {
        if (!TryRepository(change.CloneUrl) || !TryBranch(change))
            return;

        // A delete event carries no head commit; a newer re-create push is ordered by its own CAS.
        Retire(null, string.Empty);
    }

    private void PlanPullRequest(Change change)
    {
        switch (change.PullRequestAction)
        {
            case "opened" or "synchronize" or "reopened":
                break;
            case "closed":
                Ignore(ReasonPullRequestClosed);
                return;
            default:
                Ignore(ReasonUnsupportedAction);
                return;
        }
        if (!TryRepository(change.CloneUrl))
            return;

        var bodies = new List<Dictionary<string, object?>>();
        var reason = AddPullRequestSide(bodies, Repository, change.BaseSha, change.BaseRef);
        // A fork's head is code its author controls, and a trigger's ensure is the application's, which the
        // service neither grant-checks nor bounds; indexing it would run that code on the index worker. So
        // only a head in the base repository itself is published, under the base's spelling.
        var headReason = change.HeadCloneUrl.Length == 0
            ? ReasonHeadRepositoryDeleted
            : HeadRepositoryProblem(change.HeadCloneUrl, Repository);
        if (headReason.Length == 0)
            headReason = AddPullRequestSide(bodies, Repository, change.HeadSha, change.HeadRef);
        if (headReason.Length > 0)
            reason = headReason;

        if (bodies.Count == 0)
        {
            Ignore(reason);
            return;
        }
        Action = ActionEnsure;
        EnsureBody = bodies[0];
        EnsureBodies = [.. bodies];
        Reason = reason;
    }

    // Why the head repository is not published, or "": its URL must pass the shape check and name the base
    // repository (the same SVC-5 canonical key).
    private static string HeadRepositoryProblem(string headCloneUrl, string repository)
    {
        var verdict = RepositoryUrlShape.Evaluate(headCloneUrl);
        if (!verdict.Ok)
            return verdict.Reason;
        return string.Equals(verdict.Canonical, RepositoryReference.KeyFor(repository), StringComparison.Ordinal)
            ? string.Empty
            : ReasonForkHead;
    }

    // One side of a pull request, published with `branch_update: none` (no pointer moves); returns why the
    // side was skipped, or "". Identity ignores the branch name, so a side naming the same repository and
    // commit as an earlier one is not sent twice.
    private static string AddPullRequestSide(
        List<Dictionary<string, object?>> bodies, string repository, string sha, string reference)
    {
        var verdict = RepositoryUrlShape.Evaluate(repository);
        if (!verdict.Ok)
            return verdict.Reason;
        if (!GitRefs.IsCommitSha(sha) || GitRefs.IsZeroSha(sha))
            return ReasonInvalidSha;
        var duplicate = bodies.Exists(existing =>
            Equals(existing["repository_remote_url"], repository)
            && string.Equals((string?)existing["commit_sha"], sha, StringComparison.OrdinalIgnoreCase));
        if (!duplicate)
        {
            var branch = GitRefs.StripHeadsPrefix(reference);
            bodies.Add(ServiceRequests.Ensure(
                repository, sha, GitRefs.IsValidBranchName(branch) ? branch : string.Empty, null, null, null,
                ServiceRequests.None));
        }
        return string.Empty;
    }

    private bool TryRepository(string cloneUrl)
    {
        if (cloneUrl.Length == 0)
        {
            Ignore(ReasonMissingRepository);
            return false;
        }
        var verdict = RepositoryUrlShape.Evaluate(cloneUrl);
        if (!verdict.Ok)
        {
            Ignore(verdict.Reason);
            return false;
        }
        Repository = cloneUrl;
        return true;
    }

    private bool TryBranch(Change change)
    {
        if (change.RefType.Equals("tag", StringComparison.OrdinalIgnoreCase)
            || change.Ref.StartsWith(GitRefs.TagsPrefix, StringComparison.Ordinal))
        {
            Ignore(ReasonTag);
            return false;
        }
        if (change.RefType.Length > 0 && !change.RefType.Equals("branch", StringComparison.OrdinalIgnoreCase))
        {
            Ignore(ReasonNotABranch);
            return false;
        }

        var branch = change.Branch.Length > 0 ? change.Branch : GitRefs.StripHeadsPrefix(change.Ref);
        if (branch.Length == 0)
        {
            Ignore(ReasonMissingBranch);
            return false;
        }
        branch = GitRefs.StripHeadsPrefix(branch);
        if (!GitRefs.IsValidBranchName(branch))
        {
            Ignore(ReasonInvalidBranch);
            return false;
        }
        BranchName = branch;
        IsDefaultBranch = change.DefaultBranch.Length > 0
            && string.Equals(branch, change.DefaultBranch, StringComparison.Ordinal);
        return true;
    }

    private void Ensure(Dictionary<string, object?> body)
    {
        Action = ActionEnsure;
        EnsureBody = body;
        EnsureBodies = [body];
    }

    private void Retire(string? expectedHeadCommit, string reason)
    {
        Action = ActionRetire;
        RetireBody = ServiceRequests.Retire(Repository, BranchName, expectedHeadCommit);
        Reason = reason;
    }

    private void Ignore(string reason)
    {
        Action = ActionIgnore;
        EnsureBody = null;
        EnsureBodies = [];
        RetireBody = null;
        Reason = reason;
    }

    private void Reset()
    {
        Ignore(string.Empty);
        Repository = string.Empty;
        BranchName = string.Empty;
        IsDefaultBranch = false;
    }

    // The event's fields, each from its input when set, else from the metadata map.
    private sealed class Change
    {
        public Change(SextantPlanRepositoryChangeActivity activity, IReadOnlyDictionary<string, object?>? metadata)
        {
            string Field(string name, object? property, string metadataKey) =>
                First(ActivityValues.Text(ActivityValues.Input(activity, name, property)),
                    ActivityValues.Text(ActivityValues.Get(metadata, metadataKey)));
            object? Raw(string name, object? property) =>
                ActivityValues.Input(activity, name, property) ?? ActivityValues.Get(metadata, name);

            var eventName = Field("event", activity.Event, "event").ToLowerInvariant();
            var action = Field("eventAction", activity.EventAction, "action").ToLowerInvariant();
            if (eventName.StartsWith(PullRequestEvent + ".", StringComparison.Ordinal) && action.Length == 0)
                action = eventName[(PullRequestEvent.Length + 1)..];
            Kind = NormalizeKind(Field("kind", activity.Kind, "kind").ToLowerInvariant(), eventName);
            PullRequestAction = action;
            CloneUrl = Field("cloneUrl", activity.CloneUrl, "cloneUrl");
            DefaultBranch = GitRefs.StripHeadsPrefix(Field("defaultBranch", activity.DefaultBranch, "defaultBranch"));
            Ref = Field("ref", activity.Ref, "ref");
            RefType = Field("refType", activity.RefType, "refType");
            Branch = Field("branch", activity.Branch, "branch");
            Before = Field("before", activity.Before, "before");
            After = Field("after", activity.After, "after");
            Forced = ActivityValues.AsBool(Raw("forced", activity.Forced));
            Deleted = ActivityValues.AsBool(Raw("deleted", activity.Deleted)) ?? GitRefs.IsZeroSha(After);
            BaseSha = Field("baseSha", activity.BaseSha, "baseSha");
            BaseRef = Field("baseRef", activity.BaseRef, "baseRef");
            HeadSha = Field("headSha", activity.HeadSha, "headSha");
            HeadRef = Field("headRef", activity.HeadRef, "headRef");
            HeadCloneUrl = Field("headCloneUrl", activity.HeadCloneUrl, "headCloneUrl");
        }

        public string Kind { get; }
        public string PullRequestAction { get; }
        public string CloneUrl { get; }
        public string DefaultBranch { get; }
        public string Ref { get; }
        public string RefType { get; }
        public string Branch { get; }
        public string Before { get; }
        public string After { get; }
        public bool? Forced { get; }
        public bool Deleted { get; }
        public string BaseSha { get; }
        public string BaseRef { get; }
        public string HeadSha { get; }
        public string HeadRef { get; }
        public string HeadCloneUrl { get; }

        private static string First(string preferred, string fallback) => preferred.Length > 0 ? preferred : fallback;

        private static string NormalizeKind(string kind, string eventName)
        {
            var value = kind.Length > 0 ? kind : eventName;
            if (value is PullRequestEvent or KindPullRequest || value.StartsWith(PullRequestEvent + ".", StringComparison.Ordinal))
                return KindPullRequest;
            return value;
        }
    }
}
