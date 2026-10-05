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
/// short commit, a one-word coverage state, and a short, self-contained warning only when the answer may be
/// incomplete (<see cref="LeanSnapshot"/>).</item>
/// </list>
/// The local stdio/CLI surface never runs it, so its output is unchanged.
/// </summary>
public static class RemoteResponsePresenter
{
    /// <summary>
    /// The lean meta's warning for a snapshot that does not cover the whole checkout when its coverage record
    /// counts no gap (a partial snapshot without a recorded coverage row, or one whose gaps have no count).
    /// </summary>
    public const string PartialWarning =
        "Partial index: some projects or submodules were not indexed, so results may be incomplete.";

    /// <summary>The prefix of a partial warning that counts what is missing.</summary>
    public const string PartialWarningPrefix = "Partial index: ";

    /// <summary>The suffix of a partial warning that counts what is missing.</summary>
    public const string PartialWarningSuffix = ", so results may be incomplete.";

    /// <summary>
    /// The most characters a partial warning holds. It counts the gaps and names no project, so it stays a
    /// one-line caution on every result instead of a report.
    /// </summary>
    public const int MaxPartialWarningChars = 160;

    /// <summary>
    /// The lean meta's warning for a snapshot the running binary flags as incompatible: a newer schema, or a
    /// different analyzer version, toolchain or worker capability (<see cref="ReadCompatibility"/>).
    /// </summary>
    public const string IncompatibleWarning =
        "The index was built by a different indexer, toolchain or worker, so results may differ.";

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
    /// The partial warning for <paramref name="coverage"/>: it counts what is missing (projects that did not load
    /// or compile, submodules not checked out, solutions or parts of the checkout that could not be read) from the
    /// recorded coverage, so an agent can judge whether its question is affected, without naming a project or a
    /// tool. It holds at most <see cref="MaxPartialWarningChars"/> characters; a gap that does not fit is folded
    /// into "and other gaps". The generic <see cref="PartialWarning"/> is used when the record counts no gap.
    /// </summary>
    public static string PartialWarningFor(SnapshotCoverage? coverage)
    {
        var parts = coverage is null ? [] : CountedGaps(coverage);
        if (parts.Count == 0)
            return PartialWarning;

        const string andOthers = ", and other gaps";
        var text = PartialWarningPrefix + parts[0];
        for (var i = 1; i < parts.Count; i++)
        {
            var next = text + ", " + parts[i];
            var reserve = i == parts.Count - 1 ? 0 : andOthers.Length;
            if (next.Length + reserve + PartialWarningSuffix.Length > MaxPartialWarningChars)
            {
                text += andOthers;
                break;
            }
            text = next;
        }
        return text + PartialWarningSuffix;
    }

    private static List<string> CountedGaps(SnapshotCoverage coverage)
    {
        var parts = new List<string>();
        var skipped = Math.Max(coverage.ProjectsSkipped, 0);
        var degraded = Math.Max(coverage.Binding?.ProjectsDegraded ?? 0, 0);
        if (skipped > 0 || degraded > 0)
        {
            var failed = (long)skipped + degraded;
            var total = Math.Max((long)coverage.ProjectsDeclared, (long)coverage.ProjectsLoaded + skipped);
            var verb = skipped > 0 && degraded > 0 ? "did not load or compile"
                : skipped > 0 ? "did not load"
                : "did not compile";
            parts.Add(total >= failed
                ? $"{failed} of {total} projects {verb}"
                : $"{failed} {Plural(failed, "project", "projects")} {verb}");
        }
        if (coverage.SubmodulesUnpopulated > 0)
            parts.Add(coverage.SubmodulesDeclared >= coverage.SubmodulesUnpopulated
                ? $"{coverage.SubmodulesUnpopulated} of {coverage.SubmodulesDeclared} submodules were not checked out"
                : $"{coverage.SubmodulesUnpopulated} {Plural(coverage.SubmodulesUnpopulated, "submodule was", "submodules were")} not checked out");
        if (coverage.SolutionsSkipped > 0)
            parts.Add($"{coverage.SolutionsSkipped} configured {Plural(coverage.SolutionsSkipped, "solution", "solutions")} could not be used");
        if (coverage.ScanErrors > 0)
            parts.Add($"{coverage.ScanErrors} {Plural(coverage.ScanErrors, "part", "parts")} of the checkout could not be scanned");
        return parts;
    }

    private static string Plural(long count, string one, string many) => count == 1 ? one : many;

    // "https://github.com/org/app.git" → "github.com/org/app": the form the `repository` argument accepts.
    private static string DisplayRepository(string remoteUrl)
    {
        var normalized = GitRemoteNormalizer.Normalize(remoteUrl);
        var scheme = normalized.IndexOf("://", StringComparison.Ordinal);
        return scheme >= 0 ? normalized[(scheme + 3)..] : normalized;
    }
}
