using System.Text.Json;

namespace Sextant.Service.Restore;

/// <summary>
/// The projects that failed to restore on their own, per solution, remembered for one repository (issue #408). Unlike the
/// restore state it is NOT keyed by the inputs fingerprint: it survives a new commit, because a project that cannot
/// restore (a platform-specific one, a missing workload) usually still cannot. It is only a hint for grouping: the next
/// job restores every other project of the solution in one call and still TRIES the remembered ones in a second call,
/// so a project that was fixed heals itself and nothing is ever left out because it was remembered.
/// </summary>
internal sealed class KnownBadProjects
{
    internal const string FileName = "restore-known-bad.json";
    internal const string FormatVersion = "1";
    private const long MaxBytes = 1024 * 1024;
    private const int MaxSolutions = 64;
    private const int MaxProjectsPerSolution = 512;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly Dictionary<string, SortedSet<string>> _bySolution;
    private readonly object _gate = new();

    private KnownBadProjects(Dictionary<string, SortedSet<string>> bySolution) => _bySolution = bySolution;

    /// <summary>True once <see cref="Set"/> changed what is remembered.</summary>
    public bool Dirty { get; private set; }

    /// <summary>Loads the repository's remembered projects; empty when there are none or the file cannot be read.</summary>
    public static KnownBadProjects Load(string stateDirectory)
    {
        var map = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        try
        {
            var path = Path.Combine(stateDirectory, FileName);
            if (File.Exists(path) && new FileInfo(path).Length <= MaxBytes
                && JsonSerializer.Deserialize<Stored>(File.ReadAllText(path), Json) is { } stored && stored.Version == FormatVersion)
            {
                foreach (var (solution, projects) in stored.Solutions.Take(MaxSolutions))
                    map[solution] = new SortedSet<string>(projects.Take(MaxProjectsPerSolution), StringComparer.Ordinal);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            map.Clear();
        }
        return new KnownBadProjects(map);
    }

    /// <summary>The checkout-relative projects remembered for a checkout-relative solution path.</summary>
    public IReadOnlySet<string> For(string solution)
    {
        lock (_gate)
            return _bySolution.TryGetValue(solution, out var set) ? new HashSet<string>(set, StringComparer.Ordinal) : new HashSet<string>();
    }

    /// <summary>Replaces what is remembered for <paramref name="solution"/>; an empty set forgets it.</summary>
    public void Set(string solution, IEnumerable<string> projects)
    {
        var next = new SortedSet<string>(projects.Take(MaxProjectsPerSolution), StringComparer.Ordinal);
        lock (_gate)
        {
            var had = _bySolution.TryGetValue(solution, out var current);
            if (next.Count == 0)
            {
                if (had)
                {
                    _bySolution.Remove(solution);
                    Dirty = true;
                }
                return;
            }
            if (had && current!.SetEquals(next))
                return;
            if (!had && _bySolution.Count >= MaxSolutions)
                return;
            _bySolution[solution] = next;
            Dirty = true;
        }
    }

    /// <summary>Writes the file when something changed. Never throws.</summary>
    public void Save(string stateDirectory, Action<string>? log)
    {
        string? temp = null;
        try
        {
            Stored stored;
            lock (_gate)
            {
                if (!Dirty)
                    return;
                stored = new Stored { Version = FormatVersion, Solutions = _bySolution.ToDictionary(p => p.Key, p => p.Value.ToList()) };
            }
            Directory.CreateDirectory(stateDirectory);
            temp = Path.Combine(stateDirectory, $".known-bad-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temp, JsonSerializer.Serialize(stored, Json));
            File.Move(temp, Path.Combine(stateDirectory, FileName), overwrite: true);
            temp = null;
            lock (_gate)
                Dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"package restore known-bad projects could not be saved: {ex.GetType().Name}.");
        }
        finally
        {
            if (temp is not null)
                try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* left behind */ }
        }
    }

    private sealed class Stored
    {
        public string Version { get; set; } = string.Empty;
        public Dictionary<string, List<string>> Solutions { get; set; } = [];
    }
}
