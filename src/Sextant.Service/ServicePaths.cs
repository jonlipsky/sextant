namespace Sextant.Service;

/// <summary>
/// Materializes and guards the service's on-disk roots (<see cref="ServiceVolumes"/>). It creates the
/// persistent checkout/artifact/cache volumes and the ephemeral worker-scratch root, and — the load-
/// bearing invariant — asserts the scratch root is NOT nested inside any persistent volume and vice
/// versa. Worker scratch is freely deletable between jobs; a published snapshot's durable data (catalog
/// DB + artifact volume) must be UNREACHABLE from scratch cleanup, so a botched cleanup can never delete
/// published data (acceptance criterion 3). Scratch directories are allocated PER JOB under the scratch
/// root and can only be released through <see cref="ReleaseScratch"/>, which refuses any path outside it.
/// </summary>
public sealed class ServicePaths
{
    private readonly string _checkoutRoot;
    private readonly string _artifactRoot;
    private readonly string _cacheRoot;
    private readonly string _scratchRoot;

    public string CheckoutRoot => _checkoutRoot;
    public string ArtifactRoot => _artifactRoot;
    public string CacheRoot => _cacheRoot;
    public string ScratchRoot => _scratchRoot;

    public ServicePaths(ServiceVolumes volumes)
    {
        _checkoutRoot = NormalizeFull(volumes.CheckoutRoot);
        _artifactRoot = NormalizeFull(volumes.ArtifactRoot);
        _cacheRoot = NormalizeFull(volumes.CacheRoot);
        _scratchRoot = NormalizeFull(volumes.ScratchRoot);

        AssertSeparate(nameof(volumes.ScratchRoot), _scratchRoot, nameof(volumes.CheckoutRoot), _checkoutRoot);
        AssertSeparate(nameof(volumes.ScratchRoot), _scratchRoot, nameof(volumes.ArtifactRoot), _artifactRoot);
        AssertSeparate(nameof(volumes.ScratchRoot), _scratchRoot, nameof(volumes.CacheRoot), _cacheRoot);

        Directory.CreateDirectory(_checkoutRoot);
        Directory.CreateDirectory(_artifactRoot);
        Directory.CreateDirectory(_cacheRoot);
        Directory.CreateDirectory(_scratchRoot);
    }

    /// <summary>
    /// The sanitized single-segment directory name a repository's checkout lives under, on the checkout
    /// volume. This is the ONE canonical repo-url → directory mapping, shared by the checkout provider
    /// (which locates the checkout to index) and the Git-content provider (#68, which must look in the SAME
    /// directory to verify blobs), so the two can never disagree. It strips a trailing <c>.git</c>, replaces
    /// invalid filename characters, and trims separators/dots so <c>.</c>/<c>..</c> traversal collapses to a
    /// safe default.
    /// </summary>
    public static string RepoDirectoryName(string repositoryRemoteUrl)
    {
        var trimmed = (repositoryRemoteUrl ?? string.Empty).TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        var name = lastSlash >= 0 ? trimmed[(lastSlash + 1)..] : trimmed;
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        var chars = name.Select(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c).ToArray();
        var safe = new string(chars).Trim('_', '.');
        return safe.Length == 0 ? "repo" : safe;
    }

    /// <summary>Allocates a fresh, empty per-job scratch directory under the scratch root.</summary>
    public string AllocateScratch(string jobLabel)
    {
        var safe = Sanitize(jobLabel);
        var dir = Path.Combine(_scratchRoot, $"{safe}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Deletes a worker scratch directory — but ONLY if it is genuinely under the scratch root. Any path
    /// that resolves outside the scratch root (a persistent volume, the catalog DB directory, or an
    /// attempted <c>..</c> escape) is refused with <see cref="InvalidOperationException"/> so scratch
    /// cleanup can never reach published data (acceptance criterion 3). Missing directories are a no-op.
    /// </summary>
    public void ReleaseScratch(string scratchPath)
    {
        var full = NormalizeFull(scratchPath);
        if (!IsUnder(full, _scratchRoot))
            throw new InvalidOperationException(
                $"Refusing to delete '{full}': worker scratch cleanup is confined to the scratch root '{_scratchRoot}' " +
                "and must never touch a persistent volume or published snapshot.");

        if (Directory.Exists(full))
            Directory.Delete(full, recursive: true);
    }

    /// <summary>True when <paramref name="path"/> lies inside a persistent (non-scratch) service volume.</summary>
    public bool IsPersistent(string path)
    {
        var full = NormalizeFull(path);
        return IsUnder(full, _checkoutRoot) || IsUnder(full, _artifactRoot) || IsUnder(full, _cacheRoot);
    }

    private static void AssertSeparate(string aName, string a, string bName, string b)
    {
        if (IsUnder(a, b) || IsUnder(b, a))
            throw new InvalidOperationException(
                $"{aName} ('{a}') and {bName} ('{b}') must be on separate paths; worker scratch cannot be nested " +
                "with a persistent volume or a published snapshot could be deleted by scratch cleanup.");
    }

    // True when child is equal to, or nested within, parent — compared on normalized full paths with a
    // trailing separator so "/data/scratch2" is NOT considered under "/data/scratch".
    private static bool IsUnder(string child, string parent)
    {
        var p = parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var c = child.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return c.Equals(p, PathComparison) || c.StartsWith(p, PathComparison);
    }

    private static string NormalizeFull(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string Sanitize(string label)
    {
        var chars = label.Select(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c).ToArray();
        var s = new string(chars).Trim('_');
        return s.Length == 0 ? "job" : s[..Math.Min(s.Length, 40)];
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
