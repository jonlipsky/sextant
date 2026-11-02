using Sextant.Core;

namespace Sextant.Indexer;

/// <summary>
/// Discovery of the submodules of a checkout the worker provisioned, checked against what the checkout declares on disk.
/// <see cref="SubmoduleDiscovery.DiscoverAsync"/> reports a git failure as "no submodules", and a parent that believes
/// it has none registers every submodule project as its own (extracted, and counted in its binding health) instead of
/// attributing it to the provider. A checkout whose <c>.gitmodules</c> declares a populated submodule discovery did not
/// return is therefore never indexed (issue #387).
/// </summary>
public static class CheckoutSubmoduleDiscovery
{
    private const int Attempts = 3;

    /// <summary>
    /// Runs <paramref name="discover"/> until it returns every populated submodule the checkout declares (at most
    /// <see cref="Attempts"/> times), and throws <see cref="SubmoduleDiscoveryException"/> when it never does.
    /// </summary>
    internal static async Task<List<SubmoduleInfo>> DiscoverVerifiedAsync(
        Func<string, Action<GitCallFailure>?, Task<List<SubmoduleInfo>>> discover, string repoRoot, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> missing = [];
        var failures = new List<GitCallFailure>();
        var attemptFailures = new List<GitCallFailure>();
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            attemptFailures.Clear();
            var found = await discover(repoRoot, attemptFailures.Add).ConfigureAwait(false);
            missing = MissingPopulated(repoRoot, found);
            if (missing.Count == 0)
                return found;
            // A populated submodule discovery skipped without any git failure (no origin remote, a stale
            // .gitmodules entry, an unparsed status line) is the previous behaviour: no retry, no refusal.
            failures = attemptFailures.Where(f => f.SubmodulePath is null || missing.Contains(f.SubmodulePath)).ToList();
            if (failures.Count == 0)
                return found;
            if (attempt < Attempts)
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken).ConfigureAwait(false);
        }

        var cause = string.Join("; ", failures.Take(3).Select(f => f.ToString()));
        throw new SubmoduleDiscoveryException(
            $"submodule discovery returned no result for {missing.Count} populated submodule(s) the checkout declares " +
            $"({string.Join(", ", missing.Take(5))}) after {Attempts} attempts ({cause}); indexing was refused so their " +
            "projects are not attributed to the repository itself.",
            missing);
    }

    /// <summary>The populated submodules <paramref name="repoRoot"/> declares that <paramref name="found"/> does not contain.</summary>
    internal static IReadOnlyList<string> MissingPopulated(string repoRoot, IReadOnlyList<SubmoduleInfo> found)
    {
        var known = found.Select(s => s.Path.Replace('\\', '/').Trim('/')).ToHashSet(StringComparer.Ordinal);
        return CheckoutInventory.FindDeclaredSubmodules(repoRoot)
            .Where(d => d.Populated && !known.Contains(d.Path))
            .Select(d => d.Path)
            .ToList();
    }
}

/// <summary>Submodule discovery of a checkout missed a submodule the checkout has populated (issue #387).</summary>
public sealed class SubmoduleDiscoveryException(string message, IReadOnlyList<string> missing) : Exception(message)
{
    public IReadOnlyList<string> Missing { get; } = missing;
}
