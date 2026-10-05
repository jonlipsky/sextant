using System.Text.Json;
using System.Text.Json.Nodes;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Mcp;

/// <summary>
/// The remote MCP surface's response post-pass, applied by the service host to every tool result so no tool
/// can opt out (issue #145):
/// <list type="bullet">
/// <item>Source paths become repository-relative and no checkout path survives (<see cref="PathPresenter.Redact"/>).</item>
/// <item><c>meta.snapshot</c> becomes the lean block an agent needs to trust the answer — repository, branch,
/// short commit, a one-word coverage state, and a warning only when the answer may be incomplete
/// (<see cref="LeanSnapshot"/>). The full provenance stays available from <c>get_index_status</c>
/// (<c>index.snapshot</c>).</item>
/// </list>
/// The local stdio/CLI surface never runs it, so its output is unchanged.
/// </summary>
public static class RemoteResponsePresenter
{
    /// <summary>
    /// The lean meta's warning for a snapshot that does not cover the whole checkout when its coverage record
    /// gives no reason (a partial snapshot without a recorded coverage row).
    /// </summary>
    public const string PartialWarning =
        "Partial index: some projects or submodules were not indexed, so results may be incomplete. " +
        "Call get_index_status for details.";

    /// <summary>The prefix of a partial warning that names what is missing.</summary>
    public const string PartialWarningPrefix = "Partial index: ";

    /// <summary>The suffix of a partial warning that names what is missing.</summary>
    public const string PartialWarningSuffix = " Call get_index_status for details.";

    /// <summary>The cap on the coverage reasons quoted in a partial warning, in characters.</summary>
    public const int MaxWarningReasonChars = 600;

    /// <summary>The lean meta's warning for a snapshot built by an incompatible indexer.</summary>
    public const string IncompatibleWarning =
        "The index was built by a different Sextant version, so results may differ. Call get_index_status for details.";

    /// <summary>The lean meta's warning for a snapshot that includes uncommitted working-tree changes.</summary>
    public const string DirtyWarning = "The index includes uncommitted working-tree changes.";

    private const int ShortCommitLength = 12;

    /// <summary>
    /// Presents one tool result for the remote surface. <paramref name="context"/> is the read the tool was
    /// admitted with (null when it never read, e.g. a request-shaped error); <paramref name="paths"/> knows the
    /// checkout roots to strip. A result that is not a JSON object only has its checkout paths removed.
    /// </summary>
    public static string Present(string text, FederatedReadContext? context, PathPresenter paths)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return paths.RedactText(text);
        }

        // #219's symbol/argument errors carry the read's provenance too; those get the lean block as well.
        if (node is JsonObject { } root && root["meta"] is JsonObject meta
            && (meta["error"] is null || meta["snapshot"] is not null)
            && context?.Provenance is { } provenance)
            meta["snapshot"] = LeanSnapshot(context, provenance);

        return paths.Redact(node)?.ToJsonString(ResponseBuilder.NodeWriteOptions) ?? text;
    }

    /// <summary>
    /// The lean <c>meta.snapshot</c>: repository, branch, short commit, coverage, a warning when needed, and
    /// <c>repository_selection: "implicit"</c> when the host chose the repository because the request named none.
    /// </summary>
    public static JsonObject LeanSnapshot(FederatedReadContext context, SnapshotProvenance provenance)
    {
        var lean = new JsonObject();
        if (context.RepositoryUrl is { Length: > 0 } url)
            lean["repository"] = DisplayRepository(url);
        if (provenance.RepositorySelection is { Length: > 0 } selection)
            lean["repository_selection"] = selection;
        if (context.BranchName is { Length: > 0 } branch)
            lean["branch"] = branch;
        if (provenance.BaseCommit is { Length: > 0 } commit)
            lean["commit"] = commit.Length > ShortCommitLength ? commit[..ShortCommitLength] : commit;

        var partial = provenance.Completeness != SnapshotStatus.Complete;
        lean["coverage"] = partial ? SnapshotStatus.Partial : SnapshotStatus.Complete;

        var warnings = new List<string>();
        if (partial)
            warnings.Add(PartialWarningFor(provenance.Coverage));
        if (!provenance.Compatible)
            warnings.Add(IncompatibleWarning);
        if (provenance.Dirty)
            warnings.Add(DirtyWarning);
        if (warnings.Count > 0)
            lean["warning"] = string.Join(" ", warnings);
        return lean;
    }

    /// <summary>
    /// The partial warning for <paramref name="coverage"/>: its recorded reasons, so an agent learns WHAT is
    /// missing (which projects did not compile, which submodule is absent) and can judge whether its question is
    /// affected, instead of a generic caution that reads as "distrust every answer". Quoted reasons are capped at
    /// <see cref="MaxWarningReasonChars"/>; the generic <see cref="PartialWarning"/> is used when no reason was
    /// recorded.
    /// </summary>
    public static string PartialWarningFor(SnapshotCoverage? coverage)
    {
        var reasons = coverage?.Reasons.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).ToList();
        if (reasons is not { Count: > 0 })
            return PartialWarning;

        var text = string.Join(" ", reasons.Select(r => r.EndsWith('.') ? r : r + "."));
        if (text.Length > MaxWarningReasonChars)
        {
            var cut = text.LastIndexOf(' ', MaxWarningReasonChars - 1);
            text = text[..(cut > 0 ? cut : MaxWarningReasonChars - 1)].TrimEnd(',', ';', ' ') + " ...";
        }
        return PartialWarningPrefix + text + PartialWarningSuffix;
    }

    // "https://github.com/org/app.git" → "github.com/org/app": the form the `repository` argument accepts.
    private static string DisplayRepository(string remoteUrl)
    {
        var normalized = GitRemoteNormalizer.Normalize(remoteUrl);
        var scheme = normalized.IndexOf("://", StringComparison.Ordinal);
        return scheme >= 0 ? normalized[(scheme + 3)..] : normalized;
    }
}
