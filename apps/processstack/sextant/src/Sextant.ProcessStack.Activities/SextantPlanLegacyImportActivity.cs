using System.Text.Json;
using ProcessStack.Abstractions;

namespace Sextant.ProcessStack.Activities;

/// <summary>
/// Plans the one-time import of a user's v1 watches (the <c>sextant.watched-repos</c> user memory) into
/// repository grants. Pure computation: the flow lists the memory, the caller's grants and the attempt
/// markers, and then checks and grants each planned entry itself.
/// <para>
/// The memory is the caller's own, but anything that can run the app can write it (a direct run of an
/// internal process is not refused, elevenworks/ProcessStack#3287), so every value is untrusted. Only a
/// github.com repository (<c>owner</c>/<c>repo</c> in the SVC-5 shape, and a <c>cloneUrl</c>, when present,
/// naming the same repository) on a valid branch is planned, and the flow still checks each one with the
/// workspace's GitHub connection before granting it.
/// </para>
/// <para>
/// A process cannot catch a failed GitHub check, so the flow marks an entry in the attempts map before it
/// checks it and clears the mark once the entry is granted. An entry marked <see cref="MaxAttempts"/> times
/// is unresolved: it is left out (and counted) unless <c>retryUnresolved</c> is set, so a stale entry (a
/// deleted repository) cannot fail every later import.
/// </para>
/// </summary>
[ActivityName(TypeName)]
[ActivityDescription("Plans the import of the caller's v1 watched-repos memory into repository grants: which entries to check and grant, and which are already granted, unreadable or unresolved (pure computation).")]
public sealed class SextantPlanLegacyImportActivity : AbstractActivity
{
    /// <summary>The activity name flows use (<c>activity:SextantPlanLegacyImport</c>).</summary>
    public const string TypeName = "SextantPlanLegacyImport";

    /// <summary>An entry marked this many times is unresolved.</summary>
    public const int MaxAttempts = 2;

    public const int DefaultMaxEntries = 200;
    public const int MaxEntriesLimit = 1000;

    // A stored count above this is clamped (the value is untrusted).
    private const int MaxStoredAttempts = 1000;

    private const string SlugPrefix = RepositoryReference.DefaultHost + "/";

    [ActivityInput("facts", Required = true, Description = "ListMyMemory's facts for scope sextant.watched-repos: memory key → the v1 value (camelCase JSON {owner, repo, branch, cloneUrl, addedAt}); null, \"\" or \"null\" is a tombstone.")]
    public object? Facts { get; set; }

    [ActivityInput("grants", Required = true, Description = "The GET /control/grants/self body ({grants: [...]}) or its grants array.")]
    public object? Grants { get; set; }

    [ActivityInput("attempts", Description = "The stored attempts map (JSON object text {slug: count}); anything else is read as empty.")]
    public object? Attempts { get; set; }

    [ActivityInput("retryUnresolved", Description = "True to plan unresolved entries again.", DefaultValue = false)]
    public object? RetryUnresolved { get; set; }

    [ActivityInput("maxEntries", Description = "At most this many entries are planned (default 200, at most 1000); the rest are counted in remaining.", DefaultValue = DefaultMaxEntries)]
    public object? MaxEntries { get; set; }

    [ActivityOutput("pending", Description = "The entries to check and grant, {owner, repo, branch, slug, mayBeHeld}: never-attempted ones first, then by slug. mayBeHeld is true when the caller holds a grant on the same repository (on another branch, or a default-branch grant the service has not resolved), so the entry may already be granted.")]
    public List<object?> Pending { get; set; } = [];

    // JSON text rather than a map, so the flow's script parses, edits and stores a plain JavaScript object.
    [ActivityOutput("attemptsJson", Description = "The attempts map pruned to the entries still to import, as JSON object text {slug: count} with the slugs in ordinal order.")]
    public string AttemptsJson { get; set; } = "{}";

    [ActivityOutput("maxAttempts", Description = "An entry marked this many times is unresolved.")]
    public int MaxAttemptsOutput { get; set; } = MaxAttempts;

    [ActivityOutput("alreadyImported", Description = "How many entries the caller's grants already cover.")]
    public int AlreadyImported { get; set; }

    [ActivityOutput("unresolved", Description = "How many entries were left out because earlier checks did not finish.")]
    public int Unresolved { get; set; }

    [ActivityOutput("unreadable", Description = "How many live entries cannot be imported: not JSON, not on github.com, or no valid owner, repository or branch.")]
    public int Unreadable { get; set; }

    [ActivityOutput("remaining", Description = "How many entries maxEntries left out; a later run plans them.")]
    public int Remaining { get; set; }

    [ActivityOutput("duplicates", Description = "How many live entries name a watch an earlier entry already names.")]
    public int Duplicates { get; set; }

    [ActivityOutput("tombstones", Description = "How many entries are tombstones (not counted anywhere else).")]
    public int Tombstones { get; set; }

    public SextantPlanLegacyImportActivity() => Name = TypeName;

    public override Task ExecuteAsync(ActivityContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var facts = ActivityValues.Pairs(ActivityValues.RawInput(this, "facts", Facts));
        var (held, heldKeys) = HeldGrants(ActivityValues.Input(this, "grants", Grants));
        var attempts = ParseAttempts(ActivityValues.RawInput(this, "attempts", Attempts));
        var retry = ActivityValues.AsBool(ActivityValues.Input(this, "retryUnresolved", RetryUnresolved)) == true;
        var maxEntries = ActivityValues.Limit(
            ActivityValues.Input(this, "maxEntries", MaxEntries), DefaultMaxEntries, MaxEntriesLimit);

        Pending = [];
        AttemptsJson = "{}";
        var kept = new SortedDictionary<string, int>(StringComparer.Ordinal);
        MaxAttemptsOutput = MaxAttempts;
        AlreadyImported = Unresolved = Unreadable = Remaining = Duplicates = Tombstones = 0;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<(Entry Entry, int Count)>();
        foreach (var (_, value) in facts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsTombstone(value))
            {
                Tombstones++;
                continue;
            }
            if (Entry.TryParse(value) is not { } entry)
            {
                Unreadable++;
                continue;
            }
            if (!seen.Add(entry.Slug))
            {
                Duplicates++;
                continue;
            }
            if (held.Contains((entry.Key, entry.Branch)))
            {
                AlreadyImported++;
                continue;
            }
            var count = attempts.GetValueOrDefault(entry.Slug);
            if (count > 0)
                kept[entry.Slug] = count;
            if (count >= MaxAttempts && !retry)
            {
                Unresolved++;
                continue;
            }
            candidates.Add((entry, count));
        }

        var ordered = candidates
            .OrderBy(c => c.Count)
            .ThenBy(c => c.Entry.Slug, StringComparer.Ordinal)
            .ToList();
        foreach (var (entry, _) in ordered.Take(maxEntries))
            Pending.Add(entry.ToMap(mayBeHeld: heldKeys.Contains(entry.Key)));
        Remaining = Math.Max(0, ordered.Count - maxEntries);
        AttemptsJson = JsonSerializer.Serialize(kept);

        context.LogInfo($"{TypeName}: {Pending.Count} to import, {AlreadyImported} already imported, {Unresolved} unresolved, "
            + $"{Unreadable} unreadable, {Remaining} remaining, {Duplicates} duplicate(s), {Tombstones} tombstone(s).");
        return Task.CompletedTask;
    }

    private static bool IsTombstone(object? value) => value switch
    {
        null => true,
        string text => text.Trim() is "" or "null",
        _ => false,
    };

    // Every (repository key, branch) the caller's grants cover: a grant's own branch, and for a default-branch
    // grant (branch "") the branch the service resolved it to. Also every repository key with any grant: an
    // entry on such a repository may map to a grant the pairs cannot show (an unresolved default branch).
    private static (HashSet<(string Key, string Branch)> Held, HashSet<string> Keys) HeldGrants(object? value)
    {
        var held = new HashSet<(string, string)>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var grants = ActivityValues.AsList(value) ?? ActivityValues.AsList(ActivityValues.Get(ActivityValues.AsMap(value), "grants"));
        foreach (var item in grants ?? [])
        {
            var grant = ActivityValues.AsMap(item);
            var key = RepositoryReference.KeyFor(ActivityValues.Text(ActivityValues.Get(grant, "repository")));
            if (grant is null || key.Length == 0)
                continue;
            keys.Add(key);
            var branch = ActivityValues.Text(ActivityValues.Get(grant, "branch"));
            if (branch.Length > 0)
            {
                held.Add((key, branch));
                continue;
            }
            var resolved = ActivityValues.Text(
                ActivityValues.Get(ActivityValues.AsMap(ActivityValues.Get(grant, "status")), "resolved_branch"));
            if (resolved.Length > 0)
                held.Add((key, resolved));
        }
        return (held, keys);
    }

    // The stored {slug: count} map; a non-object, or an entry that is not a whole number from 1, is dropped.
    private static Dictionary<string, int> ParseAttempts(object? value)
    {
        var attempts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (slug, raw) in ActivityValues.Pairs(value))
        {
            if (ActivityValues.AsLong(raw) is long count && count > 0)
                attempts[slug] = (int)Math.Min(count, MaxStoredAttempts);
        }
        return attempts;
    }

    // One readable v1 entry. The slug is v1's key shape on github.com: owner and repository lower-cased, the
    // branch as stored (trimmed).
    private sealed record Entry(string Owner, string Repo, string Branch, string Key, string Slug)
    {
        public static Entry? TryParse(object? value)
        {
            var map = ActivityValues.AsMap(value);
            if (map is null)
                return null;

            // Each must be one path segment in the SVC-5 shape, so neither can name another host or path.
            var owner = ActivityValues.Text(ActivityValues.Get(map, "owner"));
            var repo = ActivityValues.Text(ActivityValues.Get(map, "repo"));
            if (owner.Length == 0 || repo.Length == 0)
                return null;
            var verdict = RepositoryUrlShape.Evaluate(
                RepositoryReference.CloneUrlFor(RepositoryReference.DefaultHost, owner, repo));
            if (!verdict.Ok)
                return null;

            // v1 recorded the URL it indexed. It must name this same repository on github.com in the SVC-5
            // shape (an ssh remote is read as https); anything else is not imported.
            var cloneUrl = ActivityValues.Text(ActivityValues.Get(map, "cloneUrl"));
            if (cloneUrl.Length > 0 && !NamesSameRepository(cloneUrl, verdict.Canonical))
                return null;

            var branch = ActivityValues.Text(ActivityValues.Get(map, "branch"));
            if (branch.StartsWith(GitRefs.HeadsPrefix, StringComparison.Ordinal) || !GitRefs.IsValidBranchName(branch))
                return null;

            return new Entry(
                verdict.SpelledOwner, verdict.SpelledRepo, branch, verdict.Canonical,
                $"{SlugPrefix}{verdict.SpelledOwner.ToLowerInvariant()}/{verdict.SpelledRepo.ToLowerInvariant()}@{branch}");
        }

        private static bool NamesSameRepository(string cloneUrl, string canonical)
        {
            var recorded = RepositoryUrlShape.Evaluate(RepositoryReference.ToCloneUrl(cloneUrl));
            return recorded.Ok && recorded.Canonical == canonical;
        }

        public Dictionary<string, object?> ToMap(bool mayBeHeld)
        {
            var map = ActivityValues.NewMap();
            map["owner"] = Owner;
            map["repo"] = Repo;
            map["branch"] = Branch;
            map["slug"] = Slug;
            map["mayBeHeld"] = mayBeHeld;
            return map;
        }
    }
}
