using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Mcp;

/// <summary>
/// Issue #228: a populated submodule is indexed into its provider's own snapshot, so a path under it has no rows in
/// the parent's snapshot. This finds the submodule that holds such a path from the parent's own recorded pins (the
/// provider URL and commit its <c>.gitmodules</c> and gitlinks already reveal to a reader of the parent), so the
/// caller gets a pointer instead of an empty answer.
/// </summary>
internal static class SubmodulePaths
{
    internal sealed record Hit(string ProviderRepositoryUrl, string ProviderCommitSha, string SubmodulePath, string ProviderRelativePath);

    /// <summary>The submodule of the read's parent project versions that holds the repository-relative
    /// <paramref name="path"/>, or null when it is under none (the innermost match wins).</summary>
    public static Hit? Find(SqliteConnection conn, SnapshotReadScope? scope, string path)
    {
        var relative = Normalize(path);
        if (relative.Length == 0 || Path.IsPathRooted(path.Trim()))
            return null;

        var projectIds = new SymbolStore(conn) { Scope = scope }.GetScopedProjectIds();
        Hit? best = null;
        foreach (var pin in new SnapshotDependencyStore(conn).GetSubmodulePins(projectIds))
        {
            var parentRoot = SourcePaths.DeriveRepoRoot(pin.ConsumerDiskPath, pin.ConsumerRepoRelativePath);
            var providerRoot = SourcePaths.DeriveRepoRoot(pin.ProviderDiskPath, pin.ProviderRepoRelativePath);
            if (parentRoot is null || providerRoot is null)
                continue;
            string submodulePath;
            try { submodulePath = Path.GetRelativePath(parentRoot, providerRoot).Replace('\\', '/'); }
            catch (ArgumentException) { continue; }
            if (submodulePath is "." or "" || submodulePath.StartsWith("..", StringComparison.Ordinal)
                || Path.IsPathRooted(submodulePath))
                continue;
            if (relative.Length <= submodulePath.Length + 1
                || !relative.StartsWith(submodulePath + "/", PathPresenter.PathComparison))
                continue;
            if (best is null || submodulePath.Length > best.SubmodulePath.Length)
                best = new Hit(pin.ProviderRepositoryUrl, pin.ProviderCommitSha, submodulePath, relative[(submodulePath.Length + 1)..]);
        }
        return best;
    }

    /// <summary>The pointer. A provider the caller may not read is never named (not even by URL or commit): only the
    /// submodule path, which the caller's own checkout already shows.</summary>
    public static string Message(string path, Hit hit, IReadAuthorizer authorizer) =>
        authorizer.AuthorizeRepository(0, hit.ProviderRepositoryUrl).Allowed
            ? Message(path, hit)
            : $"'{Normalize(path)}' is inside the submodule '{hit.SubmodulePath}', which is indexed as its own repository, " +
              "not in this one, so this repository has no rows for it.";

    private static string Message(string path, Hit hit) =>
        $"'{Normalize(path)}' is inside the submodule '{hit.SubmodulePath}', which this repository pins at " +
        $"{hit.ProviderRepositoryUrl} (commit {(hit.ProviderCommitSha.Length > 12 ? hit.ProviderCommitSha[..12] : hit.ProviderCommitSha)}). " +
        $"Its files are indexed under that repository, not here: ask for '{hit.ProviderRelativePath}' in {hit.ProviderRepositoryUrl}.";

    public static string Error(string path, Hit hit, IReadAuthorizer authorizer, SnapshotProvenance? provenance) =>
        ResponseBuilder.BuildError(ResponseBuilder.PathInSubmoduleCode, Message(path, hit, authorizer), provenance);

    private static string Normalize(string path)
    {
        var segments = new List<string>();
        foreach (var segment in path.Trim().Replace('\\', '/').Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
                continue;
            if (segment != "..")
                segments.Add(segment);
            else if (segments.Count > 0)
                segments.RemoveAt(segments.Count - 1);
            else
                return ""; // escapes the repository root: never inside a submodule
        }
        return string.Join('/', segments);
    }
}
