using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sextant.Indexer;

namespace Sextant.Service;

/// <summary>
/// Recursive submodule provisioning for <see cref="CloningCheckoutProvider"/> (issue #125). Every
/// <c>.gitmodules</c> entry of the freshly-checked-out commit is populated at its PINNED gitlink commit, then
/// registered in its parent (<c>submodule.&lt;name&gt;.url</c> = the CLEAN url + <c>active</c>) and absorbed
/// under <c>.git/modules</c>, so <c>git submodule status --recursive</c> reports it initialized and clean
/// (the shape Phase-12 <see cref="SubmoduleDiscovery"/> needs to build a provider snapshot).
/// <para>
/// Deliberately NOT <c>git submodule update</c>: the untrusted <c>.gitmodules</c> is read from the object
/// database with includes disabled and only <c>path</c>/<c>url</c> are honoured, so keys such as
/// <c>update=!command</c>, <c>branch</c>, <c>shallow</c> or <c>include.path</c> can never execute or redirect
/// anything. Each URL goes through <see cref="SubmoduleUrlPolicy"/>; the token is sent (transiently, via
/// environment config) only to the top-level repository's host.
/// </para>
/// <para>
/// Failure policy: a PERMANENT per-submodule failure (url refused, auth/404, missing commit, bad entry)
/// leaves that submodule's directory empty and records a redacted outcome — the checkout still succeeds and
/// coverage reports it partial. A TRANSIENT failure throws <see cref="TransientProvisioningException"/>
/// (the existing bounded-retry policy), discarding the whole staged clone.
/// </para>
/// </summary>
public sealed partial class CloningCheckoutProvider
{
    /// <summary>Deepest submodule nesting provisioned (mirrors <see cref="CheckoutInventory"/>'s scan bound).</summary>
    internal const int MaxSubmoduleDepth = 8;

    /// <summary>Upper bound on submodules provisioned for one checkout (bounds work for a hostile repository).</summary>
    internal const int MaxSubmodules = 256;

    /// <summary>Upper bound on outcomes recorded in the marker (provisioned + skipped entries).</summary>
    private const int MaxRecordedOutcomes = 2 * MaxSubmodules;

    /// <summary>
    /// Version of the published checkout layout recorded in <see cref="MarkerFileName"/>. Layout 1 is the
    /// implicit pre-#125 layout (no marker, submodules never initialized); 2 adds recursive submodules.
    /// </summary>
    internal const int CheckoutLayoutVersion = 2;

    /// <summary>The provisioning marker, written inside the checkout's <c>.git</c> (never in the work tree).</summary>
    internal const string MarkerFileName = "sextant-checkout.json";

    private const int MaxMarkerBytes = 4 * 1024 * 1024;
    private const int MaxReasonLength = 300;

    private static readonly JsonSerializerOptions MarkerJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    /// <summary>The provisioning marker's shape.</summary>
    internal sealed record CheckoutMarker
    {
        public int Layout { get; init; }
        public IReadOnlyList<SubmoduleProvisioningOutcome> Submodules { get; init; } = [];
    }

    private sealed class SubmoduleWalk
    {
        public List<SubmoduleProvisioningOutcome> Outcomes { get; } = [];
        public int Visited { get; set; }
    }

    private readonly record struct GitmodulesEntry(string Name, string? Path, string? Url);

    /// <summary>
    /// Populates every submodule declared (recursively) by the checkout at <paramref name="checkoutRoot"/>,
    /// returning one outcome per declared entry in ordinal path order.
    /// </summary>
    private IReadOnlyList<SubmoduleProvisioningOutcome> ProvisionSubmodules(
        string checkoutRoot, string topCleanUrl, string? repositoryAuthority)
    {
        var walk = new SubmoduleWalk();
        ProvisionLevel(checkoutRoot, checkoutRoot, topCleanUrl, repositoryAuthority, depth: 1, walk);
        if (walk.Outcomes.Any(o => o.IsPopulated))
            AbsorbGitDirs(checkoutRoot);
        walk.Outcomes.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return walk.Outcomes;
    }

    private void ProvisionLevel(
        string root, string repoDir, string parentCleanUrl, string? repositoryAuthority, int depth, SubmoduleWalk walk)
    {
        var entries = ReadGitmodules(root, repoDir, walk);
        if (entries is null)
            return;

        foreach (var entry in entries
                     .Where(e => e.Path is not null)
                     .OrderBy(e => e.Path, StringComparer.Ordinal))
        {
            // Bound the recorded outcomes (and so the marker) for a hostile .gitmodules with huge fan-out;
            // anything beyond is still reported unpopulated by the coverage scan, just without a reason.
            if (walk.Outcomes.Count >= MaxRecordedOutcomes)
                return;
            var relative = CheckoutRelativeDisplay(root, repoDir, entry.Path!);
            if (depth > MaxSubmoduleDepth)
            {
                walk.Outcomes.Add(Outcome(relative, entry, SubmoduleProvisioningStatus.LimitExceeded,
                    reason: $"submodule nesting deeper than {MaxSubmoduleDepth} levels is not provisioned"));
                continue;
            }
            walk.Visited++;
            if (walk.Visited > MaxSubmodules)
            {
                walk.Outcomes.Add(Outcome(relative, entry, SubmoduleProvisioningStatus.LimitExceeded,
                    reason: $"more than {MaxSubmodules} submodules are declared; the rest are not provisioned"));
                continue;
            }

            var outcome = ProvisionOne(root, repoDir, entry, parentCleanUrl, repositoryAuthority);
            walk.Outcomes.Add(outcome);
            if (outcome.IsPopulated)
            {
                var subDir = Path.GetFullPath(Path.Combine(repoDir, entry.Path!));
                ProvisionLevel(root, subDir, outcome.Url!, repositoryAuthority, depth + 1, walk);
            }
        }
    }

    /// <summary>
    /// The <c>submodule.*</c> entries of <paramref name="repoDir"/>'s COMMITTED <c>.gitmodules</c> (read from
    /// the object database with includes disabled), or null when the repository declares no submodules. A
    /// <c>.gitmodules</c> git cannot parse records an <c>invalid_entry</c> outcome per path the lenient text
    /// scan can still see, so the gap is never silent.
    /// </summary>
    private List<GitmodulesEntry>? ReadGitmodules(string root, string repoDir, SubmoduleWalk walk)
    {
        var file = Path.Combine(repoDir, ".gitmodules");
        if (!File.Exists(file))
            return null;
        // git itself ignores a symlinked .gitmodules; never follow one out of the checkout.
        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            return null;

        var result = RunGit(repoDir, LocalGitEnvironment,
            "config", "--no-includes", "--null", "--list", "--blob", "HEAD:.gitmodules");
        if (!result.Ok)
        {
            if (result.Kind is GitFailureKind.Timeout or GitFailureKind.SpawnFailure)
                throw SubmoduleTransient("read .gitmodules", CheckoutRelativeDisplay(root, repoDir, "."), null, result);
            var reason = "the .gitmodules file could not be parsed: " + ShortReason(result);
            // Nothing below this level is populated yet, so the lenient scan returns exactly its entries.
            foreach (var declared in CheckoutInventory.FindDeclaredSubmodules(repoDir).Take(MaxRecordedOutcomes))
                walk.Outcomes.Add(Outcome(CheckoutRelativeDisplay(root, repoDir, declared.Path),
                    new GitmodulesEntry(declared.Path, declared.Path, null), SubmoduleProvisioningStatus.InvalidEntry,
                    reason: reason));
            _log?.Invoke($"Submodules of '{CheckoutRelativeDisplay(root, repoDir, ".")}' skipped: {reason}");
            return [];
        }
        return ParseGitmodulesConfig(result.Stdout);
    }

    /// <summary>
    /// Parses <c>git config --null --list</c> output (<c>key\nvalue\0</c> records) into per-name entries.
    /// Section and variable names are case-insensitive; the subsection (the submodule NAME) is case-sensitive
    /// and may itself contain dots. The last value of a key wins, as in git.
    /// </summary>
    internal static List<(string Name, string? Path, string? Url)> ParseGitmodulesConfigForTest(string output) =>
        ParseGitmodulesConfig(output).Select(e => (e.Name, e.Path, e.Url)).ToList();

    private static List<GitmodulesEntry> ParseGitmodulesConfig(string output)
    {
        const string section = "submodule.";
        var order = new List<string>();
        var paths = new Dictionary<string, string?>(StringComparer.Ordinal);
        var urls = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var record in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var newline = record.IndexOf('\n');
            var key = newline >= 0 ? record[..newline] : record;
            var value = newline >= 0 ? record[(newline + 1)..] : null;
            if (!key.StartsWith(section, StringComparison.OrdinalIgnoreCase))
                continue;
            var lastDot = key.LastIndexOf('.');
            if (lastDot <= section.Length)
                continue;
            var name = key[section.Length..lastDot];
            var variable = key[(lastDot + 1)..].ToLowerInvariant();
            if (variable is not ("path" or "url"))
                continue;
            if (!paths.ContainsKey(name) && !urls.ContainsKey(name))
                order.Add(name);
            if (variable == "path")
                paths[name] = value;
            else
                urls[name] = value;
        }

        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<GitmodulesEntry>();
        foreach (var name in order)
        {
            paths.TryGetValue(name, out var path);
            urls.TryGetValue(name, out var url);
            // git keys a submodule by path: a second name claiming an already-claimed path is ignored.
            if (path is not null && !seenPaths.Add(path))
                continue;
            entries.Add(new GitmodulesEntry(name, path, url));
        }
        return entries;
    }

    /// <summary>
    /// Provisions ONE submodule: validate the entry, find its gitlink, resolve its URL through the policy,
    /// then init → remote add (clean url) → shallow fetch of the pinned commit (full-fetch fallback) →
    /// detached checkout → verify HEAD → register in the parent. Permanent failures return a non-populated
    /// outcome (after removing any partial git dir); transient failures throw.
    /// </summary>
    private SubmoduleProvisioningOutcome ProvisionOne(
        string root, string repoDir, GitmodulesEntry entry, string parentCleanUrl, string? repositoryAuthority)
    {
        var path = entry.Path!;
        var display = CheckoutRelativeDisplay(root, repoDir, path);

        if (!IsSafeSubmoduleName(entry.Name))
            return Outcome(display, entry, SubmoduleProvisioningStatus.InvalidEntry,
                reason: "the submodule name is not a safe relative name");
        if (!IsSafeSubmodulePath(path))
            return Outcome(display, entry, SubmoduleProvisioningStatus.InvalidEntry,
                reason: "the submodule path is not a safe relative path");

        var subDir = SafeFullPath(repoDir, path);
        if (subDir is null || !IsContainedIn(repoDir, subDir) || string.Equals(subDir, repoDir, StringComparison.Ordinal)
            || !IsContainedIn(root, subDir))
            return Outcome(display, entry, SubmoduleProvisioningStatus.InvalidEntry,
                reason: "the submodule path escapes its repository");
        if (TraversesReparsePoint(repoDir, path))
            return Outcome(display, entry, SubmoduleProvisioningStatus.InvalidEntry,
                reason: "the submodule path traverses a symbolic link or junction");

        var gitlink = ReadGitlink(repoDir, path, display);
        if (gitlink is null)
            return Outcome(display, entry, SubmoduleProvisioningStatus.NoGitlink,
                reason: "the parent commit has no gitlink at this path");

        var decision = SubmoduleUrlPolicy.Resolve(
            entry.Url, parentCleanUrl, repositoryAuthority, _submoduleHosts, AllowFileTransportForTesting);
        if (!decision.Allowed)
            return Outcome(display, entry, SubmoduleProvisioningStatus.UrlRefused, commit: gitlink,
                reason: decision.Refusal);
        var cleanUrl = decision.CleanUrl!;
        var logUrl = SanitizeUrlForLog(cleanUrl);

        if (!Directory.Exists(subDir))
            Directory.CreateDirectory(subDir);
        else if (Directory.EnumerateFileSystemEntries(subDir).Any())
            return Outcome(display, entry, SubmoduleProvisioningStatus.InvalidEntry, url: cleanUrl, commit: gitlink,
                reason: "the submodule directory is not empty");

        var env = SubmoduleFetchEnvironment(decision.SendToken ? decision.Authority : null);

        var init = RunGit(subDir, env, "init", "--quiet");
        if (!init.Ok)
            return FailSubmodule(GitStage.Init, "init", subDir, display, entry, cleanUrl, gitlink, init);
        var remote = RunGit(subDir, env, "remote", "add", "origin", "--end-of-options", cleanUrl);
        if (!remote.Ok)
            return FailSubmodule(GitStage.RemoteAdd, "remote add", subDir, display, entry, cleanUrl, gitlink, remote);

        // Same shape as the top-level: shallow fetch of the EXACT pinned commit, falling back to a full
        // branch fetch when the server rejects fetch-by-sha; deterministic only if BOTH attempts are.
        var shallow = RunGit(subDir, env, "fetch", "--depth", "1", "--no-tags", "--end-of-options", "origin", gitlink);
        if (!shallow.Ok)
        {
            var full = RunGit(subDir, env, "fetch", "--no-tags", "--end-of-options", "origin");
            if (!full.Ok)
            {
                var shallowTransient = IsTransientGitFailure(GitStage.Fetch, shallow.Kind, shallow.Stderr);
                var fullTransient = IsTransientGitFailure(GitStage.Fetch, full.Kind, full.Stderr);
                if (shallowTransient || fullTransient)
                {
                    RemoveSubmoduleGitDir(subDir, display);
                    throw SubmoduleTransient("fetch", display, logUrl, fullTransient ? full : shallow);
                }
                RemoveSubmoduleGitDirOrThrow(subDir, display);
                var reason = ShortReason(full) is { Length: > 0 } r ? r : ShortReason(shallow);
                _log?.Invoke($"Submodule '{display}' ({logUrl}) not populated: fetch failed: {reason}");
                return Outcome(display, entry, SubmoduleProvisioningStatus.FetchFailed, url: cleanUrl, commit: gitlink,
                    reason: reason);
            }
        }

        // The gitlink sha is a validated hex object id (ReadGitlink), so it cannot be parsed as an option.
        var checkout = RunGit(subDir, env, "checkout", "--detach", "--quiet", gitlink);
        if (!checkout.Ok)
            return FailSubmodule(GitStage.Checkout, "checkout", subDir, display, entry, cleanUrl, gitlink, checkout,
                SubmoduleProvisioningStatus.CheckoutFailed);

        var rev = RunGit(subDir, env, "rev-parse", "HEAD");
        if (!rev.Ok)
            return FailSubmodule(GitStage.RevParse, "rev-parse", subDir, display, entry, cleanUrl, gitlink, rev,
                SubmoduleProvisioningStatus.CheckoutFailed);
        if (!string.Equals(rev.Stdout.Trim(), gitlink, StringComparison.OrdinalIgnoreCase))
        {
            RemoveSubmoduleGitDirOrThrow(subDir, display);
            return Outcome(display, entry, SubmoduleProvisioningStatus.CheckoutFailed, url: cleanUrl, commit: gitlink,
                reason: "the checked-out HEAD does not match the pinned commit");
        }

        // Register in the parent so `git submodule status` reports it initialized (not `-`). The url is the
        // CLEAN url; the name was validated (no control characters, no traversal) above.
        var registerUrl = RunGit(repoDir, LocalGitEnvironment, "config", $"submodule.{entry.Name}.url", cleanUrl);
        var registerActive = registerUrl.Ok
            ? RunGit(repoDir, LocalGitEnvironment, "config", $"submodule.{entry.Name}.active", "true")
            : registerUrl;
        if (!registerActive.Ok)
        {
            RemoveSubmoduleGitDir(subDir, display);
            throw SubmoduleTransient("config", display, logUrl, registerActive);
        }

        return Outcome(display, entry, SubmoduleProvisioningStatus.Populated, url: cleanUrl, commit: gitlink);
    }

    /// <summary>
    /// Maps a failed single-invocation submodule step: TRANSIENT → throw (after removing the partial git dir);
    /// DETERMINISTIC → remove the partial git dir and return a non-populated outcome.
    /// </summary>
    private SubmoduleProvisioningOutcome FailSubmodule(
        GitStage stage, string step, string subDir, string display, GitmodulesEntry entry, string cleanUrl,
        string gitlink, GitResult result, string status = SubmoduleProvisioningStatus.FetchFailed)
    {
        if (IsTransientGitFailure(stage, result.Kind, result.Stderr))
        {
            RemoveSubmoduleGitDir(subDir, display);
            throw SubmoduleTransient(step, display, SanitizeUrlForLog(cleanUrl), result);
        }
        RemoveSubmoduleGitDirOrThrow(subDir, display);
        var reason = $"git {step} failed: {ShortReason(result)}";
        _log?.Invoke($"Submodule '{display}' ({SanitizeUrlForLog(cleanUrl)}) not populated: {reason}");
        return Outcome(display, entry, status, url: cleanUrl, commit: gitlink, reason: reason);
    }

    /// <summary>
    /// The pinned commit of the gitlink at <paramref name="path"/> in <paramref name="repoDir"/>'s HEAD, or null
    /// when that path holds no gitlink. Literal pathspecs so a crafted path (e.g. <c>:(glob)…</c>) is never
    /// interpreted as pathspec magic.
    /// </summary>
    private string? ReadGitlink(string repoDir, string path, string display)
    {
        var result = RunGit(repoDir, LocalGitEnvironment with { LiteralPathspecs = true },
            "ls-tree", "-z", "HEAD", "--", path);
        if (!result.Ok)
        {
            if (result.Kind is GitFailureKind.Timeout)
                throw SubmoduleTransient("ls-tree", display, null, result);
            return null;
        }
        foreach (var record in result.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = record.IndexOf('\t');
            if (tab < 0 || !string.Equals(record[(tab + 1)..], path, StringComparison.Ordinal))
                continue;
            var fields = record[..tab].Split(' ');
            if (fields.Length == 3 && fields[0] == "160000" && fields[1] == "commit"
                && fields[2].Length is 40 or 64 && IsHexObjectId(fields[2]))
                return fields[2].ToLowerInvariant();
        }
        return null;
    }

    /// <summary>
    /// Moves every populated submodule's embedded git dir under the superproject's <c>.git/modules</c>
    /// (git's canonical layout; relative gitdir/worktree links survive the atomic publish rename). Best-effort:
    /// the embedded layout is equally valid for <c>git submodule status</c>, so a failure is only logged.
    /// </summary>
    private void AbsorbGitDirs(string checkoutRoot)
    {
        var result = RunGit(checkoutRoot, LocalGitEnvironment, "submodule", "absorbgitdirs");
        if (!result.Ok)
            _log?.Invoke($"Submodule git dirs were left embedded (absorbgitdirs failed): {ShortReason(result)}");
    }

    /// <summary>Removes a failed submodule's partial git dir so the directory stays an UNPOPULATED submodule.</summary>
    private static bool RemoveSubmoduleGitDir(string subDir, string display)
    {
        _ = display;
        var dotGit = Path.Combine(subDir, ".git");
        TryDeleteDirectory(dotGit);
        return !Directory.Exists(dotGit) && !File.Exists(dotGit);
    }

    /// <summary>
    /// Like <see cref="RemoveSubmoduleGitDir"/>, but FAIL CLOSED: a partial git dir that cannot be removed
    /// would make the coverage scan count an empty submodule as populated, so it aborts the whole clone as
    /// transient (retried) instead.
    /// </summary>
    private void RemoveSubmoduleGitDirOrThrow(string subDir, string display)
    {
        if (!RemoveSubmoduleGitDir(subDir, display))
            throw new TransientProvisioningException(
                $"could not remove the partial git directory of failed submodule '{display}'");
    }

    private TransientProvisioningException SubmoduleTransient(string step, string display, string? logUrl, GitResult result)
    {
        var reason = result.Kind switch
        {
            GitFailureKind.Timeout => "timed out",
            GitFailureKind.SpawnFailure => "git could not be started",
            _ => ShortReason(result) is { Length: > 0 } s ? s : "no stderr"
        };
        var target = logUrl is null ? $"'{display}'" : $"'{display}' ({logUrl})";
        var message = $"git {step} failed transiently for submodule {target}: {reason}";
        _log?.Invoke($"Clone will retry — {message}");
        return new TransientProvisioningException(message);
    }

    /// <summary>First line of a failed git step's token-redacted stderr, bounded for diagnostics.</summary>
    private string ShortReason(GitResult result)
    {
        var line = result.Kind switch
        {
            GitFailureKind.Timeout => "timed out",
            GitFailureKind.SpawnFailure => "git could not be started",
            _ => FirstLine(Redact(result.Stderr))
        };
        if (line.StartsWith("fatal: ", StringComparison.Ordinal))
            line = line["fatal: ".Length..];
        return Truncate(SanitizeForDisplay(line), MaxReasonLength);
    }

    private SubmoduleProvisioningOutcome Outcome(
        string display, GitmodulesEntry entry, string status, string? url = null, string? commit = null, string? reason = null) => new()
    {
        Path = display,
        Name = Truncate(SanitizeForDisplay(entry.Name), 200),
        Status = status,
        Url = url,
        Commit = commit,
        Reason = reason is null ? null : Truncate(SanitizeForDisplay(Redact(reason)), MaxReasonLength)
    };

    /// <summary>
    /// The checkout-relative display path of a submodule, normalized exactly like
    /// <see cref="CheckoutInventory.FindDeclaredSubmodules"/> (full path → relative to the checkout root, <c>/</c>
    /// separators) so an outcome can be matched to the coverage scan's entry.
    /// </summary>
    private static string CheckoutRelativeDisplay(string root, string repoDir, string path)
    {
        try
        {
            var full = Path.GetFullPath(Path.Combine(repoDir, path));
            var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
            return Truncate(SanitizeForDisplay(relative), 400);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Truncate(SanitizeForDisplay(path), 400);
        }
    }

    /// <summary>git's <c>check_submodule_name</c> rule: non-empty, relative, no <c>..</c> component, no control chars.</summary>
    internal static bool IsSafeSubmoduleName(string name)
    {
        if (name.Length is 0 or > 1024 || name.Any(char.IsControl))
            return false;
        if (name[0] is '/' or '\\' || Path.IsPathRooted(name))
            return false;
        return !name.Split('/', '\\').Any(s => s == "..");
    }

    /// <summary>A submodule path must be relative, free of control/backslash chars and of <c>.</c>/<c>..</c>/<c>.git</c> components.</summary>
    internal static bool IsSafeSubmodulePath(string path)
    {
        if (path.Length is 0 or > 1024 || path.Any(c => char.IsControl(c) || c == '\\'))
            return false;
        if (path[0] == '/' || path[0] == '-' || Path.IsPathRooted(path))
            return false;
        foreach (var segment in path.TrimEnd('/').Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".."
                || segment.Equals(".git", StringComparison.OrdinalIgnoreCase)
                || segment.Contains(':'))
                return false;
        }
        return true;
    }

    // Every EXISTING component between the repository and the submodule must be a real directory, so a
    // crafted tree cannot route a submodule (and its fetched content) through a symlink/junction elsewhere.
    private static bool TraversesReparsePoint(string repoDir, string path)
    {
        var current = repoDir;
        foreach (var segment in path.TrimEnd('/').Split('/'))
        {
            current = Path.Combine(current, segment);
            try
            {
                if (!Directory.Exists(current) && !File.Exists(current))
                    return false;
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return true;
            }
        }
        return false;
    }

    private static string? SafeFullPath(string baseDir, string relative)
    {
        try
        {
            return Path.GetFullPath(Path.Combine(baseDir, relative));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    // ---- provisioning marker ----------------------------------------------------------------------

    private static void WriteMarker(string checkoutDir, IReadOnlyList<SubmoduleProvisioningOutcome> outcomes)
    {
        var marker = new CheckoutMarker { Layout = CheckoutLayoutVersion, Submodules = outcomes };
        File.WriteAllText(Path.Combine(checkoutDir, ".git", MarkerFileName),
            JsonSerializer.Serialize(marker, MarkerJson), new UTF8Encoding(false));
    }

    /// <summary>Reads a checkout's provisioning marker; null when absent, unreadable, or malformed.</summary>
    internal static CheckoutMarker? ReadMarker(string checkoutDir)
    {
        var file = Path.Combine(checkoutDir, ".git", MarkerFileName);
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > MaxMarkerBytes)
                return null;
            return JsonSerializer.Deserialize<CheckoutMarker>(File.ReadAllText(file), MarkerJson);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when a cached checkout that is ALREADY at the requested commit must nevertheless be re-provisioned:
    /// it predates recursive submodule provisioning (no marker / an older layout) and declares at least one
    /// submodule that is not populated. A checkout with a current marker — even one whose submodules failed
    /// permanently — or with no unpopulated submodules is reused as-is.
    /// </summary>
    private static bool NeedsSubmoduleUpgrade(string checkoutDir)
    {
        if (ReadMarker(checkoutDir) is { Layout: >= CheckoutLayoutVersion })
            return false;
        return CheckoutInventory.FindDeclaredSubmodules(checkoutDir).Any(s => !s.Populated);
    }

    /// <summary>Attaches the checkout's recorded submodule outcomes (if any) to a located resolution.</summary>
    private static CheckoutResolution WithProvisioning(CheckoutResolution resolution) =>
        ReadMarker(resolution.CheckoutDir) is { } marker
            ? resolution with { SubmoduleProvisioning = marker.Submodules }
            : resolution;

    // ---- credential scrub + fail-closed verification ------------------------------------------------

    /// <summary>
    /// Before publish: deletes every <c>FETCH_HEAD</c> in every git dir of the staged checkout (the top-level
    /// <c>.git</c> incl. <c>.git/modules/**</c>, plus any still-embedded submodule git dir) and — when a token
    /// is configured — scans every remaining non-object file of those git dirs for the token in any form
    /// (raw, or the base64 Basic credential). Returns false (FAIL CLOSED: never publish) when a record cannot
    /// be scrubbed or a credential is found; <paramref name="finding"/> names the file, never the secret.
    /// </summary>
    internal bool ScrubAndVerifyGitDirs(
        string checkoutDir, IReadOnlyList<SubmoduleProvisioningOutcome> outcomes, out string? finding)
    {
        finding = null;
        var gitDirs = new List<string> { Path.Combine(checkoutDir, ".git") };
        foreach (var outcome in outcomes.Where(o => o.IsPopulated))
            gitDirs.Add(Path.Combine(checkoutDir, outcome.Path.Replace('/', Path.DirectorySeparatorChar), ".git"));

        var needles = CredentialNeedles();
        foreach (var gitDir in gitDirs)
        {
            if (File.Exists(gitDir))
            {
                if (needles.Count > 0 && FileContainsAny(gitDir, needles))
                {
                    finding = Path.GetRelativePath(checkoutDir, gitDir).Replace('\\', '/');
                    return false;
                }
                continue;
            }
            if (!Directory.Exists(gitDir))
                continue;

            foreach (var file in EnumerateGitMetadataFiles(gitDir))
            {
                if (string.Equals(Path.GetFileName(file), "FETCH_HEAD", StringComparison.Ordinal))
                {
                    try { File.Delete(file); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* checked below */ }
                    if (File.Exists(file))
                    {
                        finding = Path.GetRelativePath(checkoutDir, file).Replace('\\', '/');
                        return false;
                    }
                    continue;
                }
                if (needles.Count > 0 && FileContainsAny(file, needles))
                {
                    finding = Path.GetRelativePath(checkoutDir, file).Replace('\\', '/');
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// Every file under a git dir except object stores (<c>objects/</c> directly under a git dir — repository
    /// CONTENT, not metadata). A directory is only pruned as an object store when its parent is itself a git
    /// dir (has <c>HEAD</c>), so a submodule NAMED <c>objects</c> under <c>.git/modules</c> is still scanned.
    /// Reparse points are never followed.
    /// </summary>
    private static IEnumerable<string> EnumerateGitMetadataFiles(string gitDir)
    {
        var stack = new Stack<string>();
        stack.Push(gitDir);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var file in Directory.GetFiles(dir))
                yield return file;
            var isGitDir = File.Exists(Path.Combine(dir, "HEAD"));
            foreach (var sub in Directory.GetDirectories(dir))
            {
                if (isGitDir && string.Equals(Path.GetFileName(sub), "objects", StringComparison.Ordinal))
                    continue;
                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0)
                    continue;
                stack.Push(sub);
            }
        }
    }

    private List<byte[]> CredentialNeedles()
    {
        var needles = new List<byte[]>();
        if (_token is null)
            return needles;
        needles.Add(Encoding.UTF8.GetBytes(_token));
        needles.Add(Encoding.UTF8.GetBytes(_tokenBasicCredential!));
        needles.Add(Encoding.UTF8.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes(_token))));
        return needles;
    }

    /// <summary>Streams <paramref name="file"/> looking for any needle (chunk overlap so a match spanning a boundary is found).</summary>
    internal static bool FileContainsAny(string file, IReadOnlyList<byte[]> needles)
    {
        var overlap = needles.Max(n => n.Length) - 1;
        var buffer = new byte[64 * 1024 + overlap];
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var carried = 0;
        int read;
        while ((read = stream.Read(buffer, carried, buffer.Length - carried)) > 0)
        {
            var span = buffer.AsSpan(0, carried + read);
            foreach (var needle in needles)
                if (span.IndexOf(needle) >= 0)
                    return true;
            carried = Math.Min(overlap, span.Length);
            span[^carried..].CopyTo(buffer);
        }
        return false;
    }

    private static string SanitizeForDisplay(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
            sb.Append(char.IsControl(c) ? '?' : c);
        return sb.ToString();
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
