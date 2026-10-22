using System.Diagnostics;

namespace Sextant.TestSupport;

/// <summary>
/// One temp directory per test run and assembly (issue #324): <c>&lt;temp&gt;/sxt-&lt;pid&gt;-&lt;random&gt;/</c> (via CreateTempSubdirectory).
/// <see cref="Initialize"/> creates it and points the process temp location (<c>TMPDIR</c> on Unix, <c>TMP</c>/<c>TEMP</c> on
/// Windows) at it, so every <c>Path.GetTempPath()</c> in the tests, and in the child processes they start, lands inside it.
/// <see cref="Cleanup"/> deletes it. Initialize also sweeps roots left by earlier runs whose process is gone, and takes a
/// snapshot of legacy-prefixed entries in the REAL temp root, which Cleanup compares to catch a test that bypasses the redirect.
/// </summary>
public static class TestTempRoot
{
    // Short on purpose: MSBuild node pipes and dotnet-diagnostic sockets live under the temp path, and a unix-domain
    // socket path is limited to ~104 chars on macOS (whose TMPDIR is already ~49).
    private const string RootPrefix = "sxt-";
    private const string OldRootPrefix = "sextant-tests-";
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);
    private static readonly TimeSpan SweepBudget = TimeSpan.FromSeconds(2);

    private static readonly string[] LegacyPrefixes =
    [
        "sextant_", "sextant-correctness-", "sextant-integration-", "no_git_", "slnx-", "elsewhere-",
    ];

    private static readonly object Gate = new();
    private static string? _root;
    private static bool _cleanedUp;
    private static string? _realTemp;
    private static DateTime _startedUtc;
    private static HashSet<string> _snapshot = new(StringComparer.Ordinal);
    private static string[] _savedEnv = [];

    private static readonly string[] EnvNames = ["TMPDIR", "TMP", "TEMP"];

    /// <summary>The run's root; initializes lazily if no lifecycle hook has run.</summary>
    public static string Root
    {
        get
        {
            Initialize();
            return _root ?? _realTemp!;
        }
    }

    /// <summary>The temp root as it was before the redirect.</summary>
    public static string RealTempRoot
    {
        get
        {
            Initialize();
            return _realTemp!;
        }
    }

    /// <summary>Creates a fresh, uniquely named subdirectory (a test's private directory) under the run root.</summary>
    public static string CreateDirectory(string prefix = "t")
    {
        var path = Path.Combine(Root, prefix + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>A unique, not yet created file path under the run root.</summary>
    public static string NewFilePath(string prefix = "f", string extension = "")
        => Path.Combine(Root, prefix + "_" + Guid.NewGuid().ToString("N") + extension);

    public static void Initialize()
    {
        lock (Gate)
        {
            if (_root != null || _cleanedUp)
                return;

            _realTemp = Path.GetTempPath(); // NOSONAR S5443: reads the shared temp path only to sweep stale run roots and snapshot legacy entries; the run root is created with CreateTempSubdirectory (unique, owner-only)
            _startedUtc = DateTime.UtcNow;
            _snapshot = TakeLegacySnapshot(_realTemp);
            SweepStale(_realTemp);

            // Atomic, unique, and owner-only (0700) on Unix; appends a random suffix to the prefix.
            var root = Directory.CreateTempSubdirectory($"{RootPrefix}{Environment.ProcessId}-").FullName;
            _root = root;

            _savedEnv = EnvNames.Select(Environment.GetEnvironmentVariable).ToArray()!;
            foreach (var name in EnvNames)
                Environment.SetEnvironmentVariable(name, root);
        }
    }

    /// <summary>
    /// Deletes the run root (best effort: SQLite pools are cleared and locked files retried briefly), restores the
    /// environment, and throws if a test created a legacy-prefixed entry directly in the real temp root.
    /// </summary>
    public static void Cleanup()
    {
        string? root, realTemp;
        lock (Gate)
        {
            root = _root;
            realTemp = _realTemp;
            if (root == null)
                return;

            for (var i = 0; i < EnvNames.Length; i++)
                Environment.SetEnvironmentVariable(EnvNames[i], _savedEnv[i]);
            _root = null;
            _cleanedUp = true;
        }

        DeleteTree(root, retry: true);

        var leaked = FindLeaks(realTemp!);
        if (leaked.Count > 0 && GuardIsStrict())
        {
            throw new InvalidOperationException(
                "Tests created entries directly in the temp root instead of under TestTempRoot (issue #324): "
                + string.Join(", ", leaked.Take(10)) + (leaked.Count > 10 ? $" (+{leaked.Count - 10} more)" : ""));
        }
        foreach (var name in leaked)
            Console.Error.WriteLine($"[TestTempRoot] leaked into temp root: {name}");
    }

    // The temp root is shared with other jobs and processes (older test binaries create the same prefixes), so the guard
    // only warns, everywhere, unless SEXTANT_TEST_TEMP_GUARD=strict.
    private static bool GuardIsStrict()
        => string.Equals(Environment.GetEnvironmentVariable("SEXTANT_TEST_TEMP_GUARD"), "strict", StringComparison.OrdinalIgnoreCase);

    private static List<string> FindLeaks(string realTemp)
    {
        var leaked = new List<string>();
        foreach (var path in SafeEntries(realTemp))
        {
            var name = Path.GetFileName(path);
            if (!IsLegacyName(name) || _snapshot.Contains(name))
                continue;
            // Only entries created after this run started, so older or concurrent runs cannot flake the check.
            var created = SafeCreationUtc(path);
            if (created >= _startedUtc)
                leaked.Add(name);
        }
        return leaked;
    }

    private static bool IsLegacyName(string name)
        => LegacyPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal));

    private static HashSet<string> TakeLegacySnapshot(string realTemp)
        => SafeEntries(realTemp).Select(Path.GetFileName).Where(n => n != null && IsLegacyName(n)).ToHashSet(StringComparer.Ordinal)!;

    private static IEnumerable<string> SafeEntries(string dir)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(dir).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static DateTime SafeCreationUtc(string path)
    {
        try
        {
            var created = Directory.Exists(path) ? Directory.GetCreationTimeUtc(path) : File.GetCreationTimeUtc(path);
            var modified = Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path);
            // Some filesystems report no birth time; fall back to the later of the two stamps.
            return created.Year < 1980 ? modified : created;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    private static void SweepStale(string realTemp)
    {
        var clock = Stopwatch.StartNew();
        foreach (var path in SafeEntries(realTemp))
        {
            if (clock.Elapsed > SweepBudget)
                return;
            var name = Path.GetFileName(path);
            var isOld = name.StartsWith(OldRootPrefix, StringComparison.Ordinal);
            if (!(isOld || name.StartsWith(RootPrefix, StringComparison.Ordinal)) || !Directory.Exists(path))
                continue;
            if (!TryParsePid(name, out var pid) || pid == Environment.ProcessId || IsAlive(pid))
                continue;
            if (DateTime.UtcNow - SafeCreationUtc(path) < StaleAfter)
                continue;
            // Another user's root (or one we cannot write) fails with a permission error: skipped, never retried.
            DeleteTree(path, retry: false);
        }
    }

    // Names: sxt-<pid>-<random suffix> (a 6-hex or CreateTempSubdirectory suffix), or the older sextant-tests-<assembly>-<pid>-<guid> (the assembly part has no '-').
    private static bool TryParsePid(string name, out int pid)
    {
        pid = 0;
        if (name.StartsWith(OldRootPrefix, StringComparison.Ordinal))
        {
            var old = name[OldRootPrefix.Length..].Split('-');
            return old.Length == 3 && int.TryParse(old[1], out pid) && pid > 0;
        }
        var parts = name[RootPrefix.Length..].Split('-');
        return parts.Length == 2 && parts[1].Length > 0 && int.TryParse(parts[0], out pid) && pid > 0;
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch
        {
            // Cannot tell (e.g. access denied): treat as alive.
            return true;
        }
    }

    // Reflection: not every test assembly references Microsoft.Data.Sqlite.
    private static void ClearSqlitePools()
    {
        try
        {
            Type.GetType("Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite")
                ?.GetMethod("ClearAllPools", Type.EmptyTypes)?.Invoke(null, null);
        }
        catch
        {
            // Best effort.
        }
    }

    private static void DeleteTree(string path, bool retry)
    {
        ClearSqlitePools();
        var attempts = retry ? 20 : 1;
        Exception? last = null;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                // Windows git objects are read-only; clear that and try again at once.
                if (ClearReadOnly(path))
                    continue;
                if (!retry)
                    return;
                ClearSqlitePools();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(50 * (attempt + 1));
            }
        }
        if (retry && last != null)
            Console.Error.WriteLine($"[TestTempRoot] could not delete {path}: {last.Message}");
    }

    // Returns true when at least one read-only attribute was cleared.
    private static bool ClearReadOnly(string path)
    {
        var cleared = false;
        try
        {
            foreach (var entry in new[] { path }.Concat(Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories)))
            {
                try
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReadOnly) == 0)
                        continue;
                    File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                    cleared = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Keep going; the delete retry reports what is left.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The tree could not be listed; the delete retry reports it.
        }
        return cleared;
    }
}
