using System.Text.Json.Nodes;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Mcp;

/// <summary>
/// Presents and matches source paths for one MCP request (issue #145). The store reconstructs ABSOLUTE
/// paths from each project's <c>disk_path</c> (a worker checkout on the service, e.g.
/// <c>/data/service/checkouts/app-…/src/X.cs</c>). On the remote surface (<see cref="IsRemote"/>) every
/// response is rewritten so no checkout path leaves the service: a path is shown relative to the OUTERMOST
/// known checkout root, so a submodule file reads <c>external/lib/src/X.cs</c>, exactly as in the caller's
/// own clone. Inputs on both surfaces accept that repository-relative form; the remote surface refuses
/// absolute paths (<see cref="AbsolutePathRefused"/>), while the local surface keeps accepting them.
/// Roots are read lazily, once per request, from every project row's <c>disk_path</c>.
/// </summary>
public sealed class PathPresenter
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly IndexDatabase? _db;
    private IReadOnlyList<string>? _roots;

    public PathPresenter(IndexDatabase? db, bool isRemote)
    {
        _db = db;
        IsRemote = isRemote;
    }

    /// <summary>True on the service's remote MCP surface: outputs are repository-relative, absolute inputs refused.</summary>
    public bool IsRemote { get; }

    /// <summary>A presenter for a context with no database (local, pass-through).</summary>
    public static PathPresenter Local { get; } = new(null, isRemote: false);

    /// <summary>Why an absolute path input is refused on the remote surface (it never echoes the path).</summary>
    public static string AbsolutePathMessage(string parameter) =>
        $"'{parameter}' must be a repository-relative path such as 'src/App/Foo.cs' (as it appears in your clone); " +
        "absolute paths are not accepted.";

    /// <summary>The <c>invalid_argument</c> response for an absolute path input on the remote surface.</summary>
    public static string AbsolutePathRefused(string parameter, SnapshotProvenance? provenance = null) =>
        ResponseBuilder.BuildError(ResponseBuilder.InvalidArgumentCode, AbsolutePathMessage(parameter), provenance);

    /// <summary>True when <paramref name="input"/> is an absolute path that this surface refuses.</summary>
    public bool Refuses(string? input) => IsRemote && input is not null && IsAbsolute(Normalize(input));

    /// <summary>
    /// The repository-relative form of a stored (usually absolute) source path: the path below the outermost
    /// known checkout root, '/'-separated. A relative path passes through ('/'-normalized). An absolute path
    /// under no known root is reduced to its file name so no server path can leak.
    /// </summary>
    public string ToRelative(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;
        if (!IsAbsolute(path))
            return path.Replace('\\', '/');
        foreach (var root in Roots)
        {
            if (path.Length > root.Length + 1 && path.StartsWith(root, PathComparison) && IsSeparator(path[root.Length]))
                return path[(root.Length + 1)..].Replace('\\', '/');
        }
        return Path.GetFileName(path.Replace('\\', '/').TrimEnd('/'));
    }

    /// <summary>The path as this surface shows it: repository-relative when remote, unchanged locally.</summary>
    public string? Present(string? path) => IsRemote && path is not null ? ToRelative(path) : path;

    /// <summary>
    /// Whether a stored path matches a caller's path: the repository-relative form on both surfaces, plus
    /// the exact absolute path on the local surface (the remote surface refuses absolute inputs upstream).
    /// </summary>
    public bool Matches(string? stored, string input)
    {
        if (string.IsNullOrEmpty(stored))
            return false;
        var normalized = Normalize(input);
        if (IsAbsolute(normalized))
            return !IsRemote && string.Equals(stored.Replace('\\', '/'), normalized, PathComparison);
        return string.Equals(ToRelative(stored), normalized, PathComparison);
    }

    /// <summary>
    /// Every whole-segment suffix of a repository-relative input (<c>a/b/c.cs</c>, <c>b/c.cs</c>, <c>c.cs</c>):
    /// the stored <c>files.repo_relative_path</c> candidates for it, since a submodule's files are stored
    /// relative to the submodule's own root. Callers confirm each hit with <see cref="Matches"/>.
    /// </summary>
    public static IReadOnlyList<string> StoredCandidates(string input)
    {
        var normalized = Normalize(input);
        var candidates = new List<string>();
        var start = 0;
        while (start < normalized.Length)
        {
            candidates.Add(normalized[start..]);
            var next = normalized.IndexOf('/', start);
            if (next < 0) break;
            start = next + 1;
        }
        return candidates;
    }

    /// <summary>The checkout roots (shortest first), read once per request.</summary>
    public IReadOnlyList<string> Roots => _roots ??= ReadRoots(_db);

    /// <summary>
    /// Rewrites a serialized response so no checkout path survives (remote surface only): path-valued
    /// properties become repository-relative (<see cref="ToRelative"/>), a property name that is itself a path
    /// (a summary's per-file count) is presented the same way, and any other string or property name that
    /// embeds a checkout root has the root removed.
    /// </summary>
    public JsonNode? Redact(JsonNode? node, string? key = null)
    {
        switch (node)
        {
            case JsonObject obj:
                var entries = obj.ToList();
                obj.Clear();
                foreach (var (name, value) in entries)
                {
                    var presented = IsAbsolute(name) ? ToRelative(name) : RedactText(name);
                    var redacted = Redact(value, name);
                    // Two server paths outside every checkout can reduce to one file name: keep both counts.
                    if (obj[presented] is JsonValue existing && existing.TryGetValue<int>(out var prior)
                        && redacted is JsonValue added && added.TryGetValue<int>(out var count))
                        redacted = JsonValue.Create(prior + count);
                    obj[presented] = redacted;
                }
                return obj;
            case JsonArray array:
                var items = array.ToList();
                array.Clear();
                foreach (var item in items)
                    array.Add(Redact(item, key));
                return array;
            case JsonValue value when value.TryGetValue<string>(out var text):
                return JsonValue.Create(IsPathValue(key, text) ? ToRelative(text) : RedactText(text));
            default:
                return node;
        }
    }

    /// <summary>
    /// Removes every checkout root from free text: a string that starts with a root is a path and is shown
    /// repository-relative; elsewhere a root is just cut out. Text without a root is returned unchanged.
    /// </summary>
    public string RedactText(string text)
    {
        if (text.Length == 0 || Roots.Count == 0)
            return text;
        foreach (var root in Roots)
        {
            if (text.Length > root.Length + 1 && text.StartsWith(root, PathComparison) && IsSeparator(text[root.Length]))
                return text[(root.Length + 1)..].Replace('\\', '/');
        }
        foreach (var root in Roots)
        {
            if (text.Contains(root, PathComparison))
                text = text.Replace(root + "/", "", PathComparison).Replace(root + "\\", "", PathComparison)
                    .Replace(root, ".", PathComparison);
        }
        return text;
    }

    private static bool IsPathKey(string key) =>
        key is "file_path" or "call_site_file" || key.EndsWith("_file_path", StringComparison.Ordinal);

    // find_references grouped by file puts the file path in group_key (a project or kind key is never absolute).
    private static bool IsPathValue(string? key, string text) =>
        key is not null && (IsPathKey(key) || (key == "group_key" && IsAbsolute(text)));

    private static bool IsSeparator(char c) => c is '/' or '\\';

    private static string Normalize(string input)
    {
        var path = input.Trim().Replace('\\', '/');
        while (path.StartsWith("./", StringComparison.Ordinal))
            path = path[2..];
        return path;
    }

    /// <summary>
    /// True for a rooted path on this OS or in the other OS's shape (a drive letter or a leading slash), so a
    /// Windows-shaped path sent to a Linux service is still recognised as absolute.
    /// </summary>
    public static bool IsAbsolute(string path) =>
        Path.IsPathRooted(path) || path.StartsWith('/') || (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':');

    private static IReadOnlyList<string> ReadRoots(IndexDatabase? db)
    {
        if (db == null)
            return [];
        using var conn = db.OpenReadConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT disk_path, repo_relative_path FROM projects WHERE disk_path IS NOT NULL;";
        var roots = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var root = SourcePaths.DeriveRepoRoot(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
            // A filesystem root ("/", "C:\") would make every path "relative"; never treat it as a checkout.
            if (root is { Length: > 3 })
                roots.Add(root.TrimEnd('/', '\\'));
        }
        return roots.OrderBy(r => r.Length).ThenBy(r => r, StringComparer.Ordinal).ToList();
    }
}
