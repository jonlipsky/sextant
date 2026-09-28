namespace Sextant.ProcessStack.Activities;

/// <summary>
/// Builds the request bodies of the Sextant service's control plane (<c>POST /control/ensure</c> and
/// <c>POST /control/branches/retire</c>) in its snake_case wire format, as CLR maps a platform
/// <c>HttpRequest</c> node serializes. Absent optional fields are omitted, never sent as <c>null</c>.
/// </summary>
internal static class ServiceRequests
{
    /// <summary>The <c>branch_update</c> value that lets the branch guards move the pointer.</summary>
    public const string Advance = "advance";

    /// <summary>The <c>branch_update</c> value that publishes or attaches a snapshot without moving any pointer.</summary>
    public const string None = "none";

    /// <summary>
    /// An ensure body. With <paramref name="branchUpdate"/> <see cref="None"/>, the branch guards
    /// (<c>expected_head_commit</c>, <c>default_branch</c>) are left out, because the service ignores them.
    /// </summary>
    public static Dictionary<string, object?> Ensure(
        string repository, string commitSha, string branch, bool? isDefaultBranch, string? expectedHeadCommit,
        bool? forced, string branchUpdate)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["repository_remote_url"] = repository,
            ["commit_sha"] = commitSha,
        };
        if (branch.Length > 0)
            body["branch_name"] = branch;
        if (branchUpdate != None)
        {
            if (isDefaultBranch is bool isDefault)
                body["default_branch"] = isDefault;
            if (expectedHeadCommit is not null)
                body["expected_head_commit"] = expectedHeadCommit;
        }
        if (forced is bool wasForced)
            body["forced"] = wasForced;
        body["branch_update"] = branchUpdate;
        return body;
    }

    /// <summary>A retire body; <paramref name="expectedHeadCommit"/> <c>null</c> retires without a CAS.</summary>
    public static Dictionary<string, object?> Retire(string repository, string branch, string? expectedHeadCommit)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["repository"] = repository,
            ["branch"] = branch,
        };
        if (expectedHeadCommit is not null)
            body["expected_head_commit"] = expectedHeadCommit;
        return body;
    }

    /// <summary>The CAS value for a push's <c>before</c>: an all-zero SHA (a branch create) becomes <c>""</c>.</summary>
    public static string ExpectedHead(string before) => GitRefs.IsZeroSha(before) ? string.Empty : before;
}
