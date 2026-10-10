using System.Text.Json;

namespace Sextant.Service.Restore;

/// <summary>
/// Per-repository NuGet package folders kept between jobs (issue #272), so a later commit of a repository restores
/// from its own warm folder instead of downloading every package into job scratch again.
/// <para>
/// One folder per repository (<see cref="ServicePaths.RepoDirectoryName"/> under
/// <see cref="ServicePaths.PackageCacheRoot"/>), never one global folder. A repository's own <c>nuget.config</c>
/// decides which feed serves a package id, and NuGet trusts a package folder by id and version, so a shared folder
/// would let one repository's feed plant a package another repository's restore then uses. Two spellings of the
/// same remote share a folder, exactly as they share a checkout.
/// </para>
/// <para>
/// Bounded: after each job the folder it used is measured, and while all folders together exceed
/// <see cref="MaxBytes"/> the least recently used folder is evicted whole (other repositories first, the folder the
/// job used last). An evicted folder is renamed aside before it is deleted, so NuGet never sees a half-deleted
/// package as installed. Sizes and last-use times are recorded in <c>&lt;folder&gt;.usage</c> files beside the
/// folders, written only by the service.
/// </para>
/// <para>
/// Only restore writes packages here: the sandbox points <c>NUGET_PACKAGES</c> at the job's folder for the
/// evaluation (the in-process load reads it; it does not restore). Like the rest of the sandbox this is defense in
/// depth, not a boundary against repository code running in the service account (#76).
/// </para>
/// </summary>
public sealed class PackageCache
{
    /// <summary>The default bound on all package folders together (<c>SEXTANT_SERVICE_PACKAGE_CACHE_MAX_MB</c>): 10 GiB.</summary>
    public const long DefaultMaxBytes = 10L * 1024 * 1024 * 1024;

    private const string UsageSuffix = ".usage";
    private const string EvictingPrefix = ".evicting-";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly ServicePaths _paths;
    private readonly Action<string>? _log;
    private readonly TimeProvider _clock;

    /// <param name="paths">The service volumes; the folders live under <see cref="ServicePaths.PackageCacheRoot"/>.</param>
    /// <param name="maxBytes">The bound on all folders together; zero or less disables the cache (each job then
    /// restores into its own scratch, as before #272).</param>
    /// <param name="log">Optional log sink.</param>
    /// <param name="clock">The clock last-use times are read from; tests drive it by hand.</param>
    public PackageCache(ServicePaths paths, long maxBytes, Action<string>? log = null, TimeProvider? clock = null)
    {
        _paths = paths;
        MaxBytes = maxBytes > 0 ? maxBytes : 0;
        _log = log;
        _clock = clock ?? TimeProvider.System;
        if (Enabled)
        {
            Directory.CreateDirectory(Root);
            SweepInterruptedEvictions();
        }
    }

    /// <summary>True when jobs restore into per-repository folders that outlive them.</summary>
    public bool Enabled => MaxBytes > 0;

    /// <summary>The bound on all package folders together, or zero when the cache is disabled.</summary>
    public long MaxBytes { get; }

    private string Root => _paths.PackageCacheRoot;

    /// <summary>
    /// The package folder of <paramref name="repositoryRemoteUrl"/>, created when missing and marked used now, or
    /// null when the cache is disabled or the folder cannot be prepared (a full or read-only cache volume): the job
    /// then restores into a cold folder in its scratch, as with the cache disabled. Never throws.
    /// </summary>
    public string? Acquire(string repositoryRemoteUrl)
    {
        if (!Enabled)
            return null;
        var dir = Path.Combine(Root, ServicePaths.RepoDirectoryName(repositoryRemoteUrl));
        try
        {
            Directory.CreateDirectory(dir);
            var previous = ReadUsage(dir);
            WriteUsage(dir, new Usage { LastUsed = _clock.GetUtcNow(), Bytes = previous?.Bytes ?? 0 });
            return dir;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"package cache: could not prepare the package folder ({ex.GetType().Name}); restoring into job scratch.");
            return null;
        }
    }

    /// <summary>
    /// Records the size of the folder a job used (<paramref name="packagesDir"/>, from <see cref="Acquire"/>) and
    /// evicts least recently used folders until the cache fits <see cref="MaxBytes"/>. Never throws: a cache that
    /// cannot be measured or trimmed is logged and left for the next job.
    /// </summary>
    public void Release(string? packagesDir)
    {
        if (!Enabled || packagesDir is null)
            return;
        try
        {
            if (Directory.Exists(packagesDir))
                WriteUsage(packagesDir, new Usage { LastUsed = _clock.GetUtcNow(), Bytes = DirectorySize(packagesDir) });
            Trim(packagesDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"package cache: could not record or trim the cache: {ex.GetType().Name}.");
        }
    }

    /// <summary>The total bytes the package folders were last measured at.</summary>
    public long TotalBytes() => Entries().Sum(e => e.Bytes);

    /// <summary>
    /// Evicts whole folders, least recently used first, until the recorded total fits <see cref="MaxBytes"/>.
    /// <paramref name="current"/> (the folder the job just used) goes last. Returns the number evicted.
    /// </summary>
    public int Trim(string? current = null)
    {
        if (!Enabled || !Directory.Exists(Root))
            return 0;
        var currentName = current is null ? null : Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(current)));
        var entries = Entries()
            .OrderBy(e => string.Equals(e.Name, currentName, StringComparison.Ordinal) ? 1 : 0)
            .ThenBy(e => e.LastUsed)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToList();
        var total = entries.Sum(e => e.Bytes);
        var evicted = 0;
        foreach (var entry in entries)
        {
            if (total <= MaxBytes)
                break;
            if (!Evict(entry.Name))
                continue;
            total -= entry.Bytes;
            evicted++;
            _log?.Invoke(
                $"package cache: evicted '{entry.Name}' ({entry.Bytes / (1024 * 1024)} MiB, last used {entry.LastUsed:u}) " +
                $"to stay under {MaxBytes / (1024 * 1024)} MiB" +
                (string.Equals(entry.Name, currentName, StringComparison.Ordinal)
                    ? "; this repository's packages alone exceed the bound, so its next restore is cold"
                    : string.Empty) + ".");
        }
        return evicted;
    }

    /// <summary>One repository's folder as last recorded.</summary>
    internal sealed record Entry(string Name, DateTimeOffset LastUsed, long Bytes);

    internal IReadOnlyList<Entry> Entries()
    {
        var entries = new List<Entry>();
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith('.'))
                continue;
            var usage = ReadUsage(dir);
            if (usage is null)
            {
                // A folder with no (or an unreadable) record: measure it once and treat it as least recently used.
                usage = new Usage { LastUsed = DateTimeOffset.MinValue, Bytes = DirectorySize(dir) };
                WriteUsage(dir, usage);
            }
            entries.Add(new Entry(name, usage.LastUsed, usage.Bytes));
        }
        return entries;
    }

    private bool Evict(string name)
    {
        var dir = Path.Combine(Root, name);
        var aside = Path.Combine(Root, $"{EvictingPrefix}{Guid.NewGuid():N}");
        try
        {
            Directory.Move(dir, aside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"package cache: could not evict '{name}': {ex.GetType().Name}.");
            return false;
        }
        TryDelete(UsagePath(dir));
        TryDeleteDirectory(aside);
        return true;
    }

    // An eviction interrupted between the rename and the delete leaves a renamed folder no restore uses.
    private void SweepInterruptedEvictions()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Root, EvictingPrefix + "*"))
                TryDeleteDirectory(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* next startup */ }
    }

    private sealed record Usage
    {
        public DateTimeOffset LastUsed { get; init; }
        public long Bytes { get; init; }
    }

    private static string UsagePath(string dir) => Path.TrimEndingDirectorySeparator(dir) + UsageSuffix;

    private static Usage? ReadUsage(string dir)
    {
        try
        {
            var file = UsagePath(dir);
            return File.Exists(file) ? JsonSerializer.Deserialize<Usage>(File.ReadAllText(file), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void WriteUsage(string dir, Usage usage)
    {
        var file = UsagePath(dir);
        var temp = file + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(usage, Json));
        File.Move(temp, file, overwrite: true);
    }

    /// <summary>The bytes of every regular file under <paramref name="dir"/>, not following links.</summary>
    internal static long DirectorySize(string dir)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true
        };
        long total = 0;
        foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*", options))
            total += file.Length;
        return total;
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* next trim */ }
    }

    private void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"package cache: could not delete '{Path.GetFileName(dir)}': {ex.GetType().Name}.");
        }
    }
}
