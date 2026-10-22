namespace Sextant.Service;

/// <summary>
/// Locates and removes one repository's rebuildable on-disk state (issue #291): its persistent checkout (submodule
/// checkouts live inside it), its bare git object store (submodule stores live inside it) and its NuGet package folder.
/// Every path is one <see cref="ServicePaths.RepoDirectoryName"/> segment directly under its volume root, a real
/// directory (a symlink or junction is refused, so a delete can never leave the volume), and removal is
/// rename-aside-then-delete: the rename (inside the caller's write gate) takes the directory out of every job's view
/// atomically, and the recursive delete can then run without holding the gate.
/// </summary>
internal sealed class RepositoryReclaimer(ServicePaths paths, Action<string>? log)
{
    internal const string AsidePrefix = ".reclaiming-";

    /// <summary>One directory to reclaim: its volume root and its full path.</summary>
    internal sealed record Target(string Kind, string Root, string Path);

    /// <summary>The state found for one repository: what exists, and what was refused by a path guard.</summary>
    internal sealed record Plan(IReadOnlyList<Target> Targets, IReadOnlyList<string> Refused);

    /// <summary>A directory renamed aside, to be measured and deleted.</summary>
    internal sealed record Aside(Target Target, string Path);

    private IEnumerable<(string Kind, string Root)> Roots()
    {
        yield return ("checkout", paths.CheckoutRoot);
        yield return ("git object store", paths.GitObjectStoreRoot);
        yield return ("package folder", paths.PackageCacheRoot);
        yield return ("restore state", paths.RestoreStateRoot);
    }

    /// <summary>The existing directories of <paramref name="repositoryRemoteUrl"/> that pass the guards.</summary>
    public Plan Find(string repositoryRemoteUrl)
    {
        var name = ServicePaths.RepoDirectoryName(repositoryRemoteUrl);
        var targets = new List<Target>();
        var refused = new List<string>();
        foreach (var (kind, root) in Roots())
        {
            var path = ChildOf(root, name);
            if (path is null)
            {
                refused.Add($"{kind}: not a direct child of its volume root");
                continue;
            }
            FileSystemInfo info = new DirectoryInfo(path);
            if (!info.Exists && !File.Exists(path))
                continue;
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null || File.Exists(path))
            {
                refused.Add($"{kind}: not a real directory (link or file)");
                continue;
            }
            targets.Add(new Target(kind, root, path));
        }
        return new Plan(targets, refused);
    }

    /// <summary>
    /// The full path of the directory named <paramref name="name"/> directly under <paramref name="root"/>, or null when
    /// the name is not one plain, non-hidden segment (so it cannot name the root, a parent or a nested path).
    /// </summary>
    internal static string? ChildOf(string root, string name)
    {
        if (name.Length == 0 || name[0] == '.' || name != Path.GetFileName(name)
            || name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            return null;
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(Path.Combine(fullRoot, name));
        var parent = Path.GetDirectoryName(full);
        return parent is not null && string.Equals(parent, fullRoot, StringComparison.Ordinal) ? full : null;
    }

    /// <summary>
    /// Renames the directory to a hidden sibling under the same root. Null when the rename failed (logged): the
    /// directory is then still in place and a later pass retries it.
    /// </summary>
    public Aside? MoveAside(Target target)
    {
        var aside = Path.Combine(target.Root, $"{AsidePrefix}{Guid.NewGuid():N}");
        try
        {
            Directory.Move(target.Path, aside);
            return new Aside(target, aside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"reclaim: could not move the {target.Kind} aside: {ex.GetType().Name}.");
            return null;
        }
    }

    /// <summary>The bytes of the regular files under the directory (links are not followed).</summary>
    public static long Measure(string directory)
    {
        try
        {
            return Restore.PackageCache.DirectorySize(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Deletes a directory moved aside; a failure is logged and left for the next pass's sweep.</summary>
    public bool Delete(Aside aside)
    {
        try
        {
            if (Directory.Exists(aside.Path))
                Directory.Delete(aside.Path, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"reclaim: could not delete the {aside.Target.Kind} ({ex.GetType().Name}); the next pass retries.");
            return false;
        }
    }

    /// <summary>
    /// Deletes the leftovers of an interrupted reclaim (a directory renamed aside and not yet deleted): the hidden
    /// siblings this class creates, real directories only.
    /// </summary>
    public void SweepInterrupted()
    {
        foreach (var (_, root) in Roots())
        {
            try
            {
                if (!Directory.Exists(root))
                    continue;
                foreach (var dir in Directory.EnumerateDirectories(root, AsidePrefix + "*"))
                {
                    var info = new DirectoryInfo(dir);
                    if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null)
                        continue;
                    try { Directory.Delete(dir, recursive: true); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* next pass */ }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* next pass */ }
        }
    }

    /// <summary>The package folder's <c>.usage</c> record, which goes with the folder.</summary>
    public static void DeleteUsageRecord(Target target)
    {
        try { File.Delete(target.Path.TrimEnd(Path.DirectorySeparatorChar) + ".usage"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* stale record is harmless */ }
    }
}
