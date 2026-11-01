using Microsoft.Data.Sqlite;
using Sextant.Service.Grants;
using Sextant.Store;

namespace Sextant.Service.Observability;

/// <summary>
/// Index freshness of each tracked default branch (issue #355): how long the indexed head has differed from the remote
/// head. The remote head comes from <see cref="Record"/>, which the service's freshness observer calls once per
/// observation interval; the indexed head and the lag are computed at every collection, so a finished index reads 0
/// at once and a lagging one grows with the clock between observations.
/// <para>
/// A repository whose remote could not be read on its last observation, or has not been read for
/// <c>staleAfter</c>, is reported <see cref="DefaultBranchFreshness.Unknown"/>, never as 0. A repository with no
/// observation yet is absent until the first pass. A branch whose pointer has no usable complete snapshot is observed
/// like any other (its indexed head is null, so it lags). A repository whose current remote head was evaluated and
/// holds no supported code is not reported: it can never catch up. That fact is resolved once per observation by the
/// observer (<paramref name="noSupportedCode"/>), never per collection. Resolution of the lag is the observation interval.
/// </para>
/// </summary>
public sealed class DefaultBranchFreshnessTracker(TimeProvider time, TimeSpan staleAfter)
{
    private sealed class State
    {
        public string? RemoteHead;
        public long LastSuccessMs;
        public bool LastFailed;
        public bool NoSupportedCode;
        public long? DifferingSinceMs;
    }

    /// <summary>The longest repository label a metric carries.</summary>
    public const int MaxLabelLength = 256;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);

    /// <summary>
    /// The repository label of a metric: the grant key reduced to its scheme, host and path, at most
    /// <see cref="MaxLabelLength"/> characters. Everything up to the LAST <c>@</c> is dropped as userinfo (a password
    /// may itself hold <c>@</c>, <c>/</c>, <c>?</c> or <c>#</c>), then any query and fragment, so a legacy catalog row
    /// that holds a credential in its URL can never put one into a metric. Where the remainder is ambiguous it errs
    /// toward dropping text, never toward keeping it.
    /// </summary>
    public static string LabelOf(string? repositoryUrl)
    {
        var label = RepositoryGrantKey.Of(repositoryUrl);
        var scheme = string.Empty;
        var separator = label.IndexOf("://", StringComparison.Ordinal);
        if (separator >= 0)
        {
            scheme = label[..(separator + 3)];
            label = label[(separator + 3)..];
        }
        label = label[(label.LastIndexOf('@') + 1)..];
        var cut = label.IndexOfAny(['?', '#']);
        if (cut >= 0)
            label = label[..cut];
        label = scheme + label;
        return label.Length <= MaxLabelLength ? label : label[..MaxLabelLength];
    }

    /// <summary>
    /// Records one observation of a repository's default-branch head. <paramref name="indexedHead"/> is null when the
    /// branch has no usable complete snapshot; <paramref name="remoteHead"/> is null when unreadable;
    /// <paramref name="noSupportedCode"/> says the remote head was evaluated and holds no supported code.
    /// </summary>
    public void Record(string repositoryUrl, string? indexedHead, string? remoteHead, bool noSupportedCode = false)
    {
        var key = LabelOf(repositoryUrl);
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        lock (_gate)
        {
            if (!_states.TryGetValue(key, out var state))
                _states[key] = state = new State();
            if (remoteHead is null)
            {
                state.LastFailed = true;
                return;
            }
            state.LastFailed = false;
            state.LastSuccessMs = now;
            state.RemoteHead = remoteHead;
            state.NoSupportedCode = noSupportedCode;
            if (SameCommit(indexedHead, remoteHead))
                state.DifferingSinceMs = null;
            else
                state.DifferingSinceMs ??= now;
        }
    }

    /// <summary>
    /// The freshness of every tracked default branch that has been observed, ordered by repository, one entry per
    /// label. Rows that share a label (spellings of one repository) keep the larger lag; unknown wins only when no row
    /// has a lag. <paramref name="visibleRepositoryKeys"/> restricts the result to repositories whose
    /// <see cref="RepositoryGrantKey"/> is in the set (a caller's grants); null means every repository.
    /// </summary>
    public IReadOnlyList<DefaultBranchFreshness> Evaluate(
        SqliteConnection connection, IReadOnlySet<string>? visibleRepositoryKeys = null)
    {
        var heads = new SnapshotStore(connection).ListDefaultBranchHeads()
            .Where(h => RepositoryGrantKey.IsVisible(visibleRepositoryKeys, h.RepositoryRemoteUrl))
            .ToList();
        if (heads.Count == 0)
            return [];
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var result = new List<DefaultBranchFreshness>();
        foreach (var head in heads)
        {
            var key = LabelOf(head.RepositoryRemoteUrl);
            bool unknown, noCode;
            string? remoteHead;
            long? since;
            lock (_gate)
            {
                if (!_states.TryGetValue(key, out var state))
                    continue;
                if (state.RemoteHead is { } seen && !state.LastFailed && SameCommit(head.CommitSha, seen))
                    state.DifferingSinceMs = null;
                else if (state.RemoteHead is not null && !state.LastFailed)
                    state.DifferingSinceMs ??= now;
                remoteHead = state.RemoteHead;
                since = state.DifferingSinceMs;
                noCode = state.NoSupportedCode;
                var stale = now - state.LastSuccessMs > staleAfter.TotalMilliseconds;
                unknown = state.LastFailed || remoteHead is null || stale;
                if (noCode && !stale && !SameCommit(head.CommitSha, remoteHead))
                    continue;
            }

            if (unknown)
            {
                result.Add(new DefaultBranchFreshness { Repository = key, Unknown = true });
                continue;
            }
            result.Add(new DefaultBranchFreshness
            {
                Repository = key,
                LagSeconds = SameCommit(head.CommitSha, remoteHead!) ? 0
                    : since is long s ? Math.Max(0, (now - s) / 1000.0) : 0
            });
        }
        return result
            .GroupBy(f => f.Repository, StringComparer.Ordinal)
            .Select(g => g.Where(f => !f.Unknown).OrderByDescending(f => f.LagSeconds).FirstOrDefault() ?? g.First())
            .OrderBy(f => f.Repository, StringComparer.Ordinal)
            .ToList();
    }

    private static bool SameCommit(string? a, string? b) => a is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
