using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sextant.Indexer;

namespace Sextant.Service.SdkPin;

/// <summary>Configuration for <see cref="SdkPinGuard"/> (issue #113).</summary>
public sealed record SdkPinOptions
{
    /// <summary>The journal directory name, a sibling of the checkouts (never inside a working tree).</summary>
    public const string JournalDirectoryName = ".sextant-sdk-pin";

    /// <summary>
    /// When true (the default) an unsatisfiable <c>global.json</c> SDK pin inside the checkout is temporarily
    /// neutralized so the checkout evaluates with an installed SDK. When false the pin is left alone and the
    /// load fails (or, per project, is skipped) with a typed <c>sdk_resolution_failed</c> diagnostic.
    /// </summary>
    public bool OverrideEnabled { get; init; } = true;

    /// <summary>
    /// The <see cref="Sextant.Core.SnapshotIdentity.SdkPinPolicy"/> component for a node whose override is
    /// disabled. Such a node publishes something different for a pinned commit (partial or failed rather than
    /// complete), so its snapshots must never share an identity with a default override-on node's.
    /// </summary>
    public const string StrictIdentityComponent = "strict";

    /// <summary>
    /// The snapshot-identity component for this policy: null for the default (override on, so the identity
    /// stays byte-identical to before issue #113), else <see cref="StrictIdentityComponent"/>. The service's
    /// request identity (<see cref="ServiceOptions.SdkPinIdentityComponent"/>) and the worker's published
    /// identity (<see cref="SdkPinGuard.IdentityComponent"/>) both derive from this one function, so the two
    /// cannot disagree for the same setting.
    /// </summary>
    public static string? IdentityComponentFor(bool overrideEnabled) => overrideEnabled ? null : StrictIdentityComponent;

    /// <summary>
    /// Where restore journals are written. Must be outside every checkout working tree, and its PARENT must
    /// contain the checkouts (recovery only replays a journal laid out that way; <see cref="SdkPinGuard.Apply"/>
    /// refuses to override otherwise). Null derives <c>&lt;parent of the checkout&gt;/.sextant-sdk-pin</c> — for
    /// a service checkout that is <c>&lt;CheckoutRoot&gt;/.sextant-sdk-pin</c>, the directory the host configures.
    /// </summary>
    public string? JournalRoot { get; init; }
}

/// <summary>
/// One <c>global.json</c> whose SDK pin hostfxr could not satisfy for the checkout being loaded, and what the
/// service did about it (issue #113).
/// </summary>
public sealed record SdkPinFinding
{
    /// <summary>The <c>global.json</c> relative to the checkout (forward slashes), or its absolute path when outside.</summary>
    public required string GlobalJsonPath { get; init; }

    /// <summary>The absolute path of the <c>global.json</c> (not surfaced; used for matching).</summary>
    [JsonIgnore]
    public required string FullPath { get; init; }

    /// <summary>True when the <c>global.json</c> lies inside the checkout.</summary>
    public bool InsideCheckout { get; init; }

    /// <summary>The pin's <c>sdk.version</c> (or hostfxr's reported requested version).</summary>
    public string? RequestedVersion { get; init; }

    /// <summary>The pin's <c>sdk.rollForward</c> policy, when present.</summary>
    public string? RollForward { get; init; }

    /// <summary>The SDK versions installed on this worker, newest first.</summary>
    public IReadOnlyList<string> InstalledSdks { get; init; } = [];

    /// <summary>The installed SDK hostfxr resolved once the pin was neutralized (null when not overridden).</summary>
    public string? ResolvedSdkVersion { get; init; }

    /// <summary>True when the pin was neutralized for the load (and restored afterwards).</summary>
    public bool OverrideApplied { get; init; }

    /// <summary>Why the pin was NOT overridden (null when it was).</summary>
    public string? NotOverriddenReason { get; init; }

    /// <summary>The pin as recorded in snapshot coverage provenance.</summary>
    public Sextant.Core.SdkPinOverride ToCoverageOverride() => new()
    {
        GlobalJsonPath = GlobalJsonPath,
        RequestedVersion = RequestedVersion,
        RollForward = RollForward,
        ResolvedSdkVersion = ResolvedSdkVersion,
        InstalledSdks = InstalledSdks
    };
}

/// <summary>
/// Keeps service indexing working when a repository's <c>global.json</c> pins a .NET SDK the worker does not
/// have (issue #113 — e.g. <c>"version": "10.0.300", "rollForward": "disable"</c> on a container with only
/// 10.0.401). Roslyn's BuildHost resolves its SDK through <c>hostfxr_resolve_sdk2</c>, which honors that pin
/// with no environment/API override, so the load dies before any project evaluates.
/// <para>
/// <b>Mechanism.</b> Before the load, <see cref="Apply"/> asks hostfxr (<see cref="ISdkResolutionProbe"/>)
/// whether each <c>global.json</c> governing the load resolves. Only a pin that FAILS is touched — a
/// resolvable pin is never modified, so behavior is unchanged for every repo that works today. A failing pin
/// inside the checkout has its <c>sdk</c> section removed IN PLACE (other sections such as
/// <c>msbuild-sdks</c> are kept) for the duration of the MSBuild load only, and the committed bytes,
/// last-write time and unix mode are restored immediately after the load, before any indexing. So the
/// published checkout never diverges from its commit, and <c>EvaluationFingerprint</c> — which hashes
/// <c>global.json</c> at index time — sees the committed content.
/// </para>
/// <para>
/// <b>Crash safety.</b> The original bytes are journaled (atomically; the file and, on Unix, its directory
/// are flushed) OUTSIDE every working tree before any file is modified. <see cref="RecoverAll"/>/<see cref="Recover"/>
/// replay a leftover journal on the next run, BEFORE the checkout is reused: a file still holding exactly
/// the neutralized content is restored; the original content needs nothing. A missing or foreign file FAILS
/// CLOSED (the journal is kept and the checkout is not indexed) unless the checkout's HEAD has moved to
/// another commit since, or the checkout is gone — then the difference is legitimate and the journal retires.
/// A journal is only replayed when it is confined to its own checkout on the checkout volume. The override
/// is only applied to a checkout whose git HEAD can be read — the journaled commit (not the content) is what
/// tells the neutralized tree apart from a re-provisioned one — and only to a <c>global.json</c> that git
/// confirms is exactly its committed content (<see cref="ICheckoutContentVerifier"/>), so the journal only
/// ever holds the commit's bytes. Each pin is verified on its own, against the repository that owns it
/// (issue #171): the checkout, or — for a pin inside a populated submodule — the innermost submodule, which
/// must be checked out at exactly the gitlink its parent's verified commit records. Such an entry also
/// journals the submodule's commit, and recovery restores it only while the submodule is still there. An
/// unverifiable pin is refused with its own reason and never stops another pin from being overridden.
/// </para>
/// <para>
/// In-place modification is safe because the service runs one worker at a time behind its write gate and
/// cross-process writer lease, and nothing else reads <c>global.json</c> from the checkout while a job runs.
/// </para>
/// </summary>
public sealed class SdkPinGuard
{
    // A neutralize/restore writes a uniquely-named sibling temp file (created exclusively, so a repository
    // file can never be overwritten or deleted) and renames it over global.json. Its name is journaled so a
    // crash mid-write can be cleaned up; recovery only ever deletes a temp file matching this exact shape.
    private const string TempFilePrefix = ".global.json.sextant-sdk-pin-";
    private const string TempFileSuffix = ".tmp";

    private static readonly JsonSerializerOptions JournalJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    private readonly SdkPinOptions _options;
    private readonly ISdkResolutionProbe _probe;
    private readonly ICheckoutContentVerifier _verifier;
    private readonly Action<string>? _log;

    /// <param name="options">The override toggle and journal root.</param>
    /// <param name="probe">hostfxr SDK resolution (the real one by default).</param>
    /// <param name="log">Operator log sink.</param>
    /// <param name="verifier">
    /// Proves a pin holds its committed content before it is overridden (git by default).
    /// </param>
    public SdkPinGuard(
        SdkPinOptions? options = null, ISdkResolutionProbe? probe = null, Action<string>? log = null,
        ICheckoutContentVerifier? verifier = null)
    {
        var configured = options ?? new SdkPinOptions();
        // Journal paths are compared against absolute paths during recovery, so a relative root is anchored once.
        _options = configured.JournalRoot is { } root
            ? configured with { JournalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) }
            : configured;
        _probe = probe ?? HostFxrSdkResolutionProbe.Instance;
        _verifier = verifier ?? GitCheckoutContentVerifier.Instance;
        _log = log;
    }

    public SdkPinOptions Options => _options;

    /// <summary>
    /// The <see cref="Sextant.Core.SnapshotIdentity.SdkPinPolicy"/> component that snapshots loaded through this
    /// guard are published under (issue #113): null by default, <see cref="SdkPinOptions.StrictIdentityComponent"/>
    /// when the override is disabled. It is taken from the SAME toggle that governs <see cref="Apply"/>, so the
    /// identity always states how the snapshot was actually built.
    /// </summary>
    public string? IdentityComponent => SdkPinOptions.IdentityComponentFor(_options.OverrideEnabled);

    /// <summary>The SDK versions installed on this worker, newest first (empty when unknown). Never throws.</summary>
    public IReadOnlyList<string> ListInstalledSdks() => SafeListInstalled();

    /// <summary>
    /// Detects every <c>global.json</c> governing the load of <paramref name="solutionPaths"/> whose SDK pin
    /// hostfxr cannot satisfy and — when enabled and safe — neutralizes it. With no failing pin this does no
    /// file I/O beyond locating <c>global.json</c> files and returns an overlay with no findings. The caller
    /// MUST call <see cref="SdkPinOverlay.Restore"/> (in a <c>finally</c>) as soon as the MSBuild load
    /// completes. Never throws for a pin problem; a failure is reported in the findings. Each failing pin is
    /// judged ON ITS OWN (issue #171): a refused pin carries its own reason and never stops another from being
    /// overridden, and a pin inside a populated submodule is verified against that submodule at its gitlink.
    /// </summary>
    public SdkPinOverlay Apply(string checkoutDir, IReadOnlyList<string> solutionPaths)
    {
        var checkout = Path.GetFullPath(checkoutDir);
        var failing = FindFailingPins(solutionPaths);
        if (failing.Count == 0)
            return new SdkPinOverlay(this, checkout, journalPath: null, [], []);

        IReadOnlyList<string>? installed = null;
        IReadOnlyList<string> Installed(HostFxrSdkResolutionError error)
        {
            installed ??= SafeListInstalled();
            return installed.Count > 0 ? installed : error.InstalledSdks;
        }

        var findings = new List<SdkPinFinding>();
        var candidates = new List<(SdkPinFinding Finding, SdkPinJournalEntry Entry, byte[] Neutralized)>();
        foreach (var (path, error) in failing)
        {
            var inside = IsContained(checkout, path);
            byte[]? original = null;
            string? readError = null;
            try
            {
                original = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                readError = $"global.json could not be read ({ex.Message})";
            }

            var pin = original is not null && GlobalJsonSdkPin.TryRead(original, out var read, out _) ? read : default;
            var finding = new SdkPinFinding
            {
                GlobalJsonPath = DisplayPath(checkout, path, inside),
                FullPath = path,
                InsideCheckout = inside,
                RequestedVersion = pin.Version ?? error.RequestedVersion,
                RollForward = pin.RollForward,
                InstalledSdks = Installed(error)
            };

            var refusal = readError
                ?? Refusal(checkout, path, inside)
                ?? (error.IsMissingSdk
                    ? null
                    : "hostfxr failed to resolve an SDK for a reason other than a missing SDK version, so the pin is not overridden");
            byte[] neutralized = [];
            if (refusal is null && !GlobalJsonSdkPin.TryNeutralize(original!, out neutralized, out var neutralizeError))
                refusal = neutralizeError;
            if (refusal is null && pin.Version is null)
                refusal = "the global.json's \"sdk\" section does not pin a version, so the service does not override it";
            if (refusal is null && !GlobalJsonSdkPin.IsSdkVersion(pin.Version))
                refusal = $"the global.json pins \"{pin.Version}\", which is not a well-formed .NET SDK version " +
                    "(major.minor.patch with a feature band of at least 100), so the service does not override it";

            SdkPinJournalEntry? entry = null;
            if (refusal is null)
            {
                try
                {
                    entry = NewEntry(path, original!, neutralized);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    refusal = $"the global.json could not be inspected ({ex.Message})";
                }
            }

            if (refusal is not null || entry is null)
            {
                findings.Add(finding with { NotOverriddenReason = refusal });
                continue;
            }

            candidates.Add((finding, entry, neutralized));
        }

        if (candidates.Count == 0)
            return new SdkPinOverlay(this, checkout, journalPath: null, [], findings);

        // Journal FIRST (atomic + fsynced, outside the working tree) so a crash after any file is modified
        // can always be repaired by the next run.
        var journalPath = JournalPathFor(checkout);
        var head = CheckoutHead.TryRead(checkout);
        var unrecoverable = JournalLayoutProblem(checkout, journalPath)
            ?? (head is null
                ? "the checkout's git HEAD commit cannot be read, so recovery could not tell it from a re-provisioned checkout"
                : null);
        if (unrecoverable is not null)
        {
            // Recovery would refuse this journal, so a crash could never be repaired: do not modify anything.
            var reason = $"the SDK-pin restore journal could not be replayed safely after a crash ({unrecoverable}), so the checkout was not modified";
            findings.AddRange(candidates.Select(c => c.Finding with { NotOverriddenReason = reason }));
            return new SdkPinOverlay(this, checkout, journalPath: null, [], findings);
        }

        // The journal must hold the COMMIT's bytes: a same-commit recovery then only ever restores committed
        // content, never a local edit or an untracked file onto a tree re-provisioned at the same commit. Each pin
        // is verified ON ITS OWN, against the repository that owns it — the checkout, or the populated submodule
        // containing it (issue #171) — so an unverifiable pin is refused with its own reason and never keeps any
        // other pin from being overridden.
        var verified = new List<(SdkPinFinding Finding, SdkPinJournalEntry Entry, byte[] Neutralized)>();
        foreach (var (finding, entry, neutralized) in candidates)
        {
            var uncommitted = VerifyCandidate(checkout, head!, entry, out var owner);
            if (uncommitted is not null)
            {
                findings.Add(finding with
                {
                    NotOverriddenReason = $"the global.json is not verifiably the checkout's committed content ({uncommitted}), so the service does not override it"
                });
                continue;
            }
            verified.Add((finding, owner is null ? entry : entry with { WorkTree = owner.Dir, WorkTreeHead = owner.Head }, neutralized));
        }
        if (verified.Count == 0)
            return new SdkPinOverlay(this, checkout, journalPath: null, [], findings);

        try
        {
            var entries = verified.Select(c => c.Entry).ToList();
            WriteJournal(journalPath, new SdkPinJournal
            {
                // A journal naming a submodule's commit is unreadable to a binary from before issue #171, so it
                // fails closed there rather than being replayed without the submodule check.
                Version = entries.Any(e => e.WorkTree is not null) ? SdkPinJournal.SubmoduleVersion : SdkPinJournal.CurrentVersion,
                CheckoutDir = checkout,
                Head = head,
                CreatedUtc = DateTimeOffset.UtcNow,
                Entries = entries
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var reason = $"the SDK-pin restore journal could not be written ({ex.Message}), so the checkout was not modified";
            findings.AddRange(verified.Select(c => c.Finding with { NotOverriddenReason = reason }));
            return new SdkPinOverlay(this, checkout, journalPath: null, [], findings);
        }

        var applied = new List<SdkPinJournalEntry>();
        foreach (var (finding, entry, neutralized) in verified)
        {
            try
            {
                WriteAtomically(entry.Path, entry.TempPath, neutralized, entry.UnixMode);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                findings.Add(finding with { NotOverriddenReason = $"global.json could not be rewritten ({ex.Message})" });
                continue;
            }

            // The "SDK used": what hostfxr now resolves for this global.json's directory. If the pin is STILL
            // unsatisfiable (e.g. a parent pin outside the checkout also fails), neutralizing it bought
            // nothing — put the committed bytes back now so the override is only ever reported when it worked.
            var reprobe = SafeProbe(Path.GetDirectoryName(entry.Path)!);
            if (reprobe is { Resolved: false })
            {
                var restoreError = RestoreEntry(entry, checkout, volume: null, recovering: false);
                findings.Add(finding with
                {
                    NotOverriddenReason =
                        "neutralizing the pin did not make an installed SDK resolvable" +
                        (reprobe.Error!.GlobalJsonPath is { } other ? $" (hostfxr still honors '{DisplayPath(checkout, other, IsContained(checkout, other))}')" : string.Empty) +
                        (restoreError is null ? string.Empty : $"; restoring it failed: {restoreError}")
                });
                if (restoreError is not null)
                    applied.Add(entry);
                continue;
            }

            applied.Add(entry);
            findings.Add(finding with { OverrideApplied = true, ResolvedSdkVersion = reprobe?.ResolvedSdkVersion });
            _log?.Invoke(
                $"sdk-pin: neutralized unsatisfiable SDK pin in '{finding.GlobalJsonPath}' (requested " +
                $"{finding.RequestedVersion ?? "?"}, rollForward {finding.RollForward ?? "default"}); evaluating with " +
                $"installed SDK {reprobe?.ResolvedSdkVersion ?? "?"}. The committed file is restored after the load.");
        }

        if (applied.Count == 0)
            RetireJournal(journalPath);

        return new SdkPinOverlay(this, checkout, applied.Count > 0 ? journalPath : null, applied, findings);
    }

    /// <summary>
    /// Replays every leftover journal under the configured <see cref="SdkPinOptions.JournalRoot"/> (a crash
    /// between neutralizing and restoring). Run at the start of every job, BEFORE any checkout is reused or
    /// inspected. Never throws, and one bad journal never stops the others from being replayed. Returns the
    /// number of journals fully resolved. A journal directory that cannot be read is only logged here: the
    /// per-checkout <see cref="Recover"/> gate then refuses to index a checkout whose journal it cannot rule out.
    /// </summary>
    public int RecoverAll()
    {
        if (_options.JournalRoot is not { } root)
            return 0;
        var rootPresence = PresenceOf(root, out var rootError);
        if (rootPresence == Presence.Unknown)
            _log?.Invoke($"sdk-pin: could not access the restore-journal directory '{root}' ({rootError}).");
        if (rootPresence != Presence.Present)
            return 0;

        var resolved = 0;
        string[] journals;
        try
        {
            journals = Directory.GetFiles(root, "*.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"sdk-pin: could not enumerate restore journals in '{root}': {ex.Message}");
            return 0;
        }

        foreach (var journal in journals.OrderBy(j => j, StringComparer.Ordinal))
        {
            if (RecoverJournal(journal))
                resolved++;
        }
        return resolved;
    }

    /// <summary>
    /// Replays the leftover journal for one checkout, if any. Never throws. False — the checkout must not be
    /// indexed — when a journal exists and could not be replayed, or when its presence cannot be ruled out
    /// (the journal directory is unreadable): an access failure is never mistaken for "no journal".
    /// </summary>
    public bool Recover(string checkoutDir)
    {
        string journal;
        try
        {
            journal = JournalPathFor(Path.GetFullPath(checkoutDir));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            _log?.Invoke($"sdk-pin: could not locate the restore journal for '{checkoutDir}' ({ex.Message}).");
            return false;
        }

        var presence = PresenceOf(journal, out var error);
        if (presence == Presence.Absent)
            return true;
        if (presence == Presence.Unknown)
        {
            _log?.Invoke(
                $"sdk-pin: could not determine whether '{journal}' exists ({error}); checkout '{checkoutDir}' is " +
                "not indexed until the restore-journal directory is accessible.");
            return false;
        }
        return RecoverJournal(journal);
    }

    /// <summary>The journal file for <paramref name="checkoutDir"/> (internal for tests).</summary>
    internal string JournalPathFor(string checkoutDir)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(checkoutDir));
        var root = _options.JournalRoot
            ?? Path.Combine(Path.GetDirectoryName(full) ?? full, SdkPinOptions.JournalDirectoryName);
        var leaf = new string(Path.GetFileName(full).Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        var hash = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(full)))[..12];
        return Path.Combine(root, $"{(leaf.Length > 0 ? leaf : "checkout")}-{hash}.json");
    }

    internal string? RestoreAll(string checkout, IReadOnlyList<SdkPinJournalEntry> entries, string? journalPath)
    {
        var errors = new List<string>();
        foreach (var entry in entries)
        {
            var error = RestoreEntry(entry, checkout, volume: null, recovering: false);
            if (error is not null)
                errors.Add(error);
        }

        if (errors.Count > 0)
        {
            // Keep the journal so the next run can still repair the checkout.
            foreach (var error in errors)
                _log?.Invoke($"sdk-pin: {error}");
            return string.Join("; ", errors);
        }

        if (journalPath is not null)
            RetireJournal(journalPath);
        return null;
    }

    private List<(string Path, HostFxrSdkResolutionError Error)> FindFailingPins(IReadOnlyList<string> solutionPaths)
    {
        var pins = new List<string>();
        var seen = new HashSet<string>(GlobalJsonLocator.PathComparer);
        foreach (var directory in GlobalJsonLocator.EvaluationDirectories(solutionPaths))
        {
            if (GlobalJsonLocator.FindNearest(directory) is { } pin && seen.Add(pin))
                pins.Add(pin);
        }

        var failing = new List<(string, HostFxrSdkResolutionError)>();
        var failingSeen = new HashSet<string>(GlobalJsonLocator.PathComparer);
        foreach (var pin in pins)
        {
            if (SafeProbe(Path.GetDirectoryName(pin)!) is not { Resolved: false } result)
                continue;

            // hostfxr names the file it honored; prefer it when it exists (it is authoritative).
            var honored = result.Error!.GlobalJsonPath is { } reported && File.Exists(reported)
                ? Path.GetFullPath(reported)
                : pin;
            if (failingSeen.Add(honored))
                failing.Add((honored, result.Error));
        }
        return failing;
    }

    // The repository that owns a pin: its work tree and the commit it is checked out at.
    private sealed record PinOwner(string Dir, string Head);

    // Issue #171: a pin is verified against the repository that owns it. That is the checkout itself, or, for a
    // global.json inside a populated submodule, the INNERMOST submodule containing it, reached through a chain
    // of gitlinks: each submodule must be checked out at exactly the commit its parent's verified commit pins,
    // so the file is still the superproject commit's content. `submodule` is set only in the submodule case;
    // the journal records it so recovery can tell a submodule that moved since from one modified in place.
    private string? VerifyCandidate(string checkout, string head, SdkPinJournalEntry entry, out PinOwner? submodule)
    {
        submodule = null;
        var chain = EnclosingSubmodules(checkout, entry.Path, out var problem);
        if (problem is not null)
            return problem;

        var owner = new PinOwner(checkout, head);
        foreach (var dir in chain)
        {
            var dirHead = CheckoutHead.TryRead(dir);
            if (dirHead is null)
                return $"the git HEAD of the submodule '{DisplayPath(checkout, dir, inside: true)}' that contains it cannot be read";
            if (VerifyGitlink(owner, dir, dirHead) is { } gitlink)
                return gitlink;
            owner = new PinOwner(dir, dirHead);
        }

        if (VerifyCommitted(owner.Dir, owner.Head, [new CheckoutFileContent(entry.Path, Convert.FromBase64String(entry.OriginalBase64))]) is { } content)
            return content;
        if (chain.Count > 0)
            submodule = owner;
        return null;
    }

    // Every directory from the global.json's own directory up to (excluding) the checkout root that is its own
    // git work tree — has a `.git` entry — outermost first. In a service checkout only a populated submodule
    // does; VerifyGitlink proves each is a submodule of its parent.
    private static List<string> EnclosingSubmodules(string checkout, string path, out string? problem)
    {
        problem = null;
        var trees = new List<string>();
        for (var dir = Path.GetDirectoryName(path); dir is not null && IsContained(checkout, dir); dir = Path.GetDirectoryName(dir))
        {
            var presence = PresenceOf(Path.Combine(dir, ".git"), out var error);
            if (presence == Presence.Unknown)
            {
                problem = $"whether '{DisplayPath(checkout, dir, inside: true)}' is a submodule could not be determined ({error})";
                return trees;
            }
            if (presence == Presence.Present)
                trees.Add(dir);
        }
        trees.Reverse();
        return trees;
    }

    private string? VerifyGitlink(PinOwner parent, string submoduleDir, string submoduleHead)
    {
        try
        {
            return _verifier.GitlinkProblem(parent.Dir, parent.Head, submoduleDir, submoduleHead);
        }
        catch (Exception ex)
        {
            return $"verification failed ({ex.GetType().Name}: {ex.Message})";
        }
    }

    private string? VerifyCommitted(string checkout, string head, IReadOnlyList<CheckoutFileContent> files)
    {
        try
        {
            return _verifier.Problem(checkout, head, files);
        }
        catch (Exception ex)
        {
            return $"verification failed ({ex.GetType().Name}: {ex.Message})";
        }
    }

    private SdkResolutionProbeResult? SafeProbe(string directory)
    {
        try
        {
            return _probe.Probe(directory);
        }
        catch (Exception ex)
        {
            // An unavailable probe must never block indexing: behave exactly as before #113.
            _log?.Invoke($"sdk-pin: SDK resolution probe failed for '{directory}' ({ex.GetType().Name}: {ex.Message}); not overriding.");
            return null;
        }
    }

    private IReadOnlyList<string> SafeListInstalled()
    {
        try
        {
            return _probe.ListInstalledSdks();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"sdk-pin: could not list installed SDKs ({ex.GetType().Name}: {ex.Message}).");
            return [];
        }
    }

    private string? Refusal(string checkout, string path, bool inside)
    {
        if (!_options.OverrideEnabled)
            return "the service's SDK-pin override is disabled (SEXTANT_SERVICE_SDK_PIN_OVERRIDE=false)";
        if (!inside)
            return "the global.json is outside the checkout, so the service does not modify it";
        if (IsUnderGitMetadata(checkout, path))
            return "the global.json is inside git metadata (.git), so the service does not modify it";
        return LinkRefusal(checkout, path);
    }

    // Git's own directories (.git, including an absorbed submodule's .git/modules/<name>) are never written.
    private static bool IsUnderGitMetadata(string checkout, string path) =>
        Path.GetRelativePath(checkout, path)
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .SkipLast(1)
            .Any(segment => string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase));

    // The service never writes through a symbolic link (or junction): not the global.json itself, nor any
    // directory from it up to and including the checkout root — nor, when recovering, any directory between
    // the checkout and the checkout volume (<paramref name="volume"/>, exclusive). Any of them could redirect
    // the write outside the checkout. A path that does not exist is not a link.
    private static string? LinkRefusal(string checkout, string path, string? volume = null)
    {
        try
        {
            if (IsLink(new FileInfo(path)))
                return "the global.json is a symbolic link, so the service does not modify it";
            for (var dir = Path.GetDirectoryName(path); dir is not null; dir = Path.GetDirectoryName(dir))
            {
                if (volume is not null && PathEquals(dir, volume))
                    break;
                if (IsLink(new DirectoryInfo(dir)))
                    return IsContained(checkout, dir)
                        ? "a directory containing the global.json is a symbolic link, so the service does not modify it"
                        : "the checkout directory is a symbolic link or junction, so the service does not modify it";
                if (volume is null && PathEquals(dir, checkout))
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"the global.json could not be inspected ({ex.Message})";
        }

        return null;
    }

    // LinkTarget throws for a missing path on Unix; a missing path is simply not a link.
    private static bool IsLink(FileSystemInfo info)
    {
        try
        {
            return info.LinkTarget is not null;
        }
        catch (IOException) when (!info.Exists)
        {
            return false;
        }
    }

    private static SdkPinJournalEntry NewEntry(string path, byte[] original, byte[] neutralized)
    {
        int? mode = null;
        if (!OperatingSystem.IsWindows())
            mode = (int)File.GetUnixFileMode(path);

        return new SdkPinJournalEntry
        {
            Path = path,
            TempPath = Path.Combine(Path.GetDirectoryName(path)!, $"{TempFilePrefix}{Guid.NewGuid():N}{TempFileSuffix}"),
            OriginalBase64 = Convert.ToBase64String(original),
            OriginalSha256 = Sha256(original),
            NeutralizedSha256 = Sha256(neutralized),
            LastWriteTimeUtcTicks = File.GetLastWriteTimeUtc(path).Ticks,
            UnixMode = mode
        };
    }

    // True only for the exact temp-file shape NewEntry generates, beside the entry's global.json — so a
    // tampered journal can never make recovery delete an arbitrary file.
    private static bool IsOwnTempPath(string globalJsonPath, string? tempPath)
    {
        if (string.IsNullOrEmpty(tempPath)
            || !PathEquals(Path.GetDirectoryName(Path.GetFullPath(tempPath)) ?? string.Empty,
                Path.GetDirectoryName(Path.GetFullPath(globalJsonPath)) ?? string.Empty))
            return false;
        var name = Path.GetFileName(tempPath);
        if (!name.StartsWith(TempFilePrefix, StringComparison.Ordinal) || !name.EndsWith(TempFileSuffix, StringComparison.Ordinal))
            return false;
        var token = name[TempFilePrefix.Length..^TempFileSuffix.Length];
        return token.Length == 32 && token.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
    }

    // Restores one entry. Normal restore puts the committed bytes back unless the file now holds FOREIGN
    // content (neither neutralized nor original — someone else changed it; never clobber). Recovery after a
    // crash never recreates a missing file and FAILS CLOSED on a missing or foreign file, keeping the journal
    // so the checkout is not indexed. (A checkout that has since moved to another commit never reaches this:
    // RecoverJournal retires its journal without writing anything.) The link check is repeated right before
    // any write because the MSBuild load ran in between. That narrows, but cannot close, the window in which a
    // directory could be swapped for a link. Evaluated code already holds the worker's filesystem authority (see
    // EvaluationSandbox; OS-hard isolation is #76), so a swap gains it nothing it could not do directly.
    // Returns null on success, else the error.
    private string? RestoreEntry(SdkPinJournalEntry entry, string checkout, string? volume, bool recovering)
    {
        if (LinkRefusal(checkout, entry.Path, volume) is { } refusal)
            return $"'{entry.Path}' was not restored: {refusal}";
        try
        {
            if (IsOwnTempPath(entry.Path, entry.TempPath))
                TryDelete(entry.TempPath);
            var original = Convert.FromBase64String(entry.OriginalBase64);

            if (!File.Exists(entry.Path))
            {
                if (recovering)
                    return $"'{entry.Path}' is missing although the checkout is still at the commit whose SDK pin was neutralized; it was not recreated";
                WriteRestored(entry, original);
                return null;
            }

            var current = Sha256(File.ReadAllBytes(entry.Path));
            if (string.Equals(current, entry.OriginalSha256, StringComparison.Ordinal))
            {
                SetMetadata(entry);
                return null;
            }

            if (!string.Equals(current, entry.NeutralizedSha256, StringComparison.Ordinal))
                return $"'{entry.Path}' was changed by something else while its SDK pin was neutralized; it was left as-is";

            WriteRestored(entry, original);
            if (recovering)
                _log?.Invoke($"sdk-pin: recovered the committed '{entry.Path}' left neutralized by an interrupted job.");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
            return $"could not restore the committed '{entry.Path}': {ex.Message}";
        }
    }

    // A per-journal exception boundary: an unexpected failure keeps the journal (so its checkout stays blocked,
    // surfacing as a requeued sdk_pin_restore_failed) and never stops RecoverAll replaying the other journals.
    private bool RecoverJournal(string journalPath)
    {
        try
        {
            return RecoverJournalCore(journalPath);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"sdk-pin: recovering restore journal '{journalPath}' failed ({ex.GetType().Name}: {ex.Message}); leaving it for inspection.");
            return false;
        }
    }

    private bool RecoverJournalCore(string journalPath)
    {
        SdkPinJournal? journal;
        try
        {
            journal = JsonSerializer.Deserialize<SdkPinJournal>(File.ReadAllBytes(journalPath), JournalJson);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _log?.Invoke($"sdk-pin: restore journal '{journalPath}' is unreadable ({ex.Message}); leaving it for inspection.");
            return false;
        }

        if (JournalProblem(journalPath, journal) is { } problem)
        {
            _log?.Invoke($"sdk-pin: restore journal '{journalPath}' is not valid ({problem}); leaving it for inspection.");
            return false;
        }

        var checkout = Path.GetFullPath(journal!.CheckoutDir);
        var checkoutPresence = PresenceOf(checkout, out var checkoutError);
        if (checkoutPresence == Presence.Unknown)
        {
            _log?.Invoke($"sdk-pin: could not access checkout '{checkout}' ({checkoutError}); keeping its restore journal.");
            return false;
        }
        if (checkoutPresence == Presence.Absent)
        {
            // The checkout is gone (deleted by an operator): a later clone is fresh, so there is nothing to repair.
            _log?.Invoke($"sdk-pin: checkout '{checkout}' no longer exists; retiring its restore journal.");
            RetireJournal(journalPath);
            return true;
        }

        // The journaled commit is what proves the tree is still the one whose pin was neutralized (content alone
        // cannot: a re-provisioned tree may hold the neutralized bytes), so Apply never journals without it.
        var head = CheckoutHead.TryRead(checkout);
        if (head is null)
        {
            _log?.Invoke(
                $"sdk-pin: cannot confirm checkout '{checkout}' is still at commit {journal.Head} (its HEAD is unreadable); " +
                $"keeping the restore journal '{journalPath}'.");
            return false;
        }
        if (!string.Equals(journal.Head, head, StringComparison.OrdinalIgnoreCase))
        {
            // The checkout was re-provisioned at another commit (the cloning provider replaces the whole
            // tree), so every journaled path — even a temp-file path, or a global.json whose bytes happen to
            // equal the neutralized form — now belongs to THAT commit. Never write or delete anything in it.
            _log?.Invoke($"sdk-pin: checkout '{checkout}' moved from {journal.Head} to {head}; retiring its restore journal without touching the checkout.");
            RetireJournal(journalPath);
            return true;
        }

        var errors = new List<string>();
        var volume = JournalVolume(journalPath);
        // Issue #171: a submodule entry is only restored while its submodule is still at the journaled commit; a
        // submodule that has moved (or is no longer populated, with its global.json gone) holds nothing of the
        // neutralized tree, so its entry is retired without writing anything.
        var current = new List<SdkPinJournalEntry>();
        foreach (var entry in journal.Entries)
        {
            var state = SubmoduleEntryState(entry, out var submoduleProblem);
            if (state == EntryState.Current)
                current.Add(entry);
            else if (state == EntryState.Unconfirmed)
                errors.Add(submoduleProblem!);
        }

        // Links are checked against the CURRENT tree only now that it is known to be the journaled commit (a
        // moved checkout's links belong to the new commit and must never block retiring the journal). All
        // entries are checked before any is written, and RestoreEntry re-checks each right before its write.
        foreach (var entry in current)
        {
            if (LinkRefusal(checkout, Path.GetFullPath(entry.Path), volume) is { } refusal)
                errors.Add($"'{entry.Path}' was not restored: {refusal}");
        }
        if (errors.Count == 0)
        {
            foreach (var entry in current)
            {
                if (RestoreEntry(entry, checkout, volume, recovering: true) is { } error)
                    errors.Add(error);
            }
        }

        if (errors.Count > 0)
        {
            foreach (var error in errors)
                _log?.Invoke($"sdk-pin: recovery: {error}.");
            _log?.Invoke(
                $"sdk-pin: checkout '{checkout}' is not indexed until an operator restores its committed global.json " +
                $"(or deletes the checkout so it is re-cloned); the restore journal '{journalPath}' is kept and retires itself then.");
            return false;
        }

        RetireJournal(journalPath);
        return true;
    }

    private enum EntryState { Current, Moved, Unconfirmed }

    // Whether a journal entry still belongs to the tree whose pin was neutralized. A checkout-owned entry always
    // does (the checkout's HEAD was already confirmed); a submodule entry only while the submodule is still at the
    // journaled commit. A submodule whose HEAD cannot be read — or that is no longer a work tree although its
    // global.json remains — FAILS CLOSED, exactly like the checkout's own unreadable HEAD.
    private EntryState SubmoduleEntryState(SdkPinJournalEntry entry, out string? problem)
    {
        problem = null;
        if (entry.WorkTree is null)
            return EntryState.Current;

        var workTree = Path.GetFullPath(entry.WorkTree);
        var git = PresenceOf(Path.Combine(workTree, ".git"), out var error);
        if (git == Presence.Absent)
        {
            var file = PresenceOf(Path.GetFullPath(entry.Path), out error);
            if (file == Presence.Absent)
            {
                _log?.Invoke($"sdk-pin: submodule '{workTree}' is no longer populated; retiring its restore-journal entry without touching the checkout.");
                return EntryState.Moved;
            }
            problem = file == Presence.Unknown
                ? $"could not access '{entry.Path}' ({error}); it was not restored"
                : $"'{entry.Path}' was not restored: its submodule '{workTree}' is no longer a git work tree, so its commit cannot be confirmed";
            return EntryState.Unconfirmed;
        }
        if (git == Presence.Unknown)
        {
            problem = $"could not access the submodule '{workTree}' ({error}); '{entry.Path}' was not restored";
            return EntryState.Unconfirmed;
        }

        var head = CheckoutHead.TryRead(workTree);
        if (head is null)
        {
            problem = $"cannot confirm the submodule '{workTree}' is still at commit {entry.WorkTreeHead} (its HEAD is unreadable); '{entry.Path}' was not restored";
            return EntryState.Unconfirmed;
        }
        if (!string.Equals(head, entry.WorkTreeHead, StringComparison.OrdinalIgnoreCase))
        {
            _log?.Invoke($"sdk-pin: submodule '{workTree}' moved from {entry.WorkTreeHead} to {head}; retiring its restore-journal entry without touching it.");
            return EntryState.Moved;
        }
        return EntryState.Current;
    }

    // A journal is only replayed when it is well-formed and confined: it must be the journal for the checkout
    // it names, laid out as Apply writes it (see JournalLayoutProblem), and every entry must be a distinct
    // global.json lexically inside that checkout whose journaled bytes match their hash and whose metadata is
    // in range. This is STRUCTURAL validation only (total: any malformed shape is a problem string); symlinks
    // in the current tree are checked just before a same-commit restore writes, never before retiring.
    private string? JournalProblem(string journalPath, SdkPinJournal? journal)
    {
        try
        {
            return JournalProblemCore(journalPath, journal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return $"it names an invalid path ({ex.Message})";
        }
    }

    private string? JournalProblemCore(string journalPath, SdkPinJournal? journal)
    {
        if (journal is null)
            return "it is empty";
        if (journal.Version is not (SdkPinJournal.CurrentVersion or SdkPinJournal.SubmoduleVersion))
            return $"unsupported journal version {journal.Version}";
        if (journal.Head is null)
            return "it records no checkout commit";
        if (!CheckoutHead.IsObjectId(journal.Head))
            return "its recorded HEAD is not a commit id";
        if (string.IsNullOrWhiteSpace(journal.CheckoutDir) || !Path.IsPathFullyQualified(journal.CheckoutDir))
            return "it names no absolute checkout path";

        var checkout = Path.TrimEndingDirectorySeparator(Path.GetFullPath(journal.CheckoutDir));
        if (!PathEquals(JournalPathFor(checkout), Path.GetFullPath(journalPath)))
            return "it is not the journal of the checkout it names";
        if (JournalLayoutProblem(checkout, journalPath) is { } layoutProblem)
            return layoutProblem;
        if (journal.Entries is not { Count: > 0 } entries)
            return "it lists no entries";

        var paths = new HashSet<string>(GlobalJsonLocator.PathComparer);
        var temps = new HashSet<string>(GlobalJsonLocator.PathComparer);
        foreach (var entry in entries)
        {
            if (EntryProblem(checkout, entry) is { } problem)
                return problem;
            if (!paths.Add(Path.GetFullPath(entry.Path)) || !temps.Add(Path.GetFullPath(entry.TempPath)))
                return "it lists the same file twice";
        }
        // A version-1 journal predates submodule entries; one that names a submodule was not written by Apply.
        if (journal.Version == SdkPinJournal.CurrentVersion && entries.Any(e => e.WorkTree is not null))
            return "a version-1 journal lists a submodule entry";
        return null;
    }

    // Recovery only replays a journal that lies OUTSIDE its checkout, in a journal directory whose parent (the
    // checkout volume) contains the checkout — e.g. <CheckoutRoot>/.sextant-sdk-pin. Apply refuses to write a
    // journal recovery would reject, so a crash is always repairable.
    private static string? JournalLayoutProblem(string checkout, string journalPath)
    {
        if (IsContained(checkout, Path.GetFullPath(journalPath)))
            return "the journal would be inside the checkout's working tree";
        if (JournalVolume(journalPath) is not { } volume || !IsContained(volume, checkout))
            return "the checkout is not under the journal directory's parent (the checkout volume)";
        return null;
    }

    private static string? JournalVolume(string journalPath) =>
        Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(journalPath)));

    private static string? EntryProblem(string checkout, SdkPinJournalEntry? entry)
    {
        if (entry is null)
            return "an entry is empty";
        if (string.IsNullOrEmpty(entry.Path) || !Path.IsPathFullyQualified(entry.Path)
            || !string.Equals(Path.GetFileName(entry.Path), GlobalJsonLocator.FileName, StringComparison.Ordinal)
            || !IsContained(checkout, Path.GetFullPath(entry.Path)))
            return "an entry is not a global.json inside the checkout";
        if (!IsOwnTempPath(entry.Path, entry.TempPath))
            return "an entry names an unexpected temporary file";
        if (!IsSha256(entry.OriginalSha256) || !IsSha256(entry.NeutralizedSha256))
            return "an entry's checksums are malformed";
        if (entry.OriginalBase64 is null)
            return "an entry has no journaled content";
        try
        {
            if (!string.Equals(Sha256(Convert.FromBase64String(entry.OriginalBase64)), entry.OriginalSha256, StringComparison.Ordinal))
                return "an entry's journaled content does not match its checksum";
        }
        catch (FormatException)
        {
            return "an entry's journaled content is not valid base64";
        }
        if (entry.LastWriteTimeUtcTicks is < 0 or > MaxTicks)
            return "an entry's timestamp is out of range";
        if (entry.UnixMode is < 0 or > MaxUnixMode)
            return "an entry's file mode is out of range";
        if (IsUnderGitMetadata(checkout, Path.GetFullPath(entry.Path)))
            return "an entry lies inside git metadata (.git)";
        return SubmoduleEntryProblem(checkout, entry);
    }

    // A submodule entry names both its work tree and that work tree's commit, and the work tree lies inside the
    // checkout and contains the entry's global.json.
    private static string? SubmoduleEntryProblem(string checkout, SdkPinJournalEntry entry)
    {
        if (entry.WorkTree is null && entry.WorkTreeHead is null)
            return null;
        if (entry.WorkTree is null || entry.WorkTreeHead is null)
            return "an entry records its submodule incompletely";
        if (!Path.IsPathFullyQualified(entry.WorkTree))
            return "an entry's submodule is not an absolute path";
        var workTree = Path.GetFullPath(entry.WorkTree);
        if (!IsContained(checkout, workTree) || !IsContained(workTree, Path.GetFullPath(entry.Path))
            || IsUnderGitMetadata(checkout, Path.Combine(workTree, GlobalJsonLocator.FileName)))
            return "an entry's submodule is not inside the checkout, or does not contain its global.json";
        if (!CheckoutHead.IsObjectId(entry.WorkTreeHead))
            return "an entry's submodule HEAD is not a commit id";
        return null;
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private const long MaxTicks = 3155378975999999999; // DateTime.MaxValue.Ticks
    private const int MaxUnixMode = 0xFFF; // every UnixFileMode flag (07777)

    private enum Presence { Absent, Present, Unknown }

    // Distinguishes a path that is confirmed absent from one whose existence cannot be determined (an access
    // or I/O failure): File.Exists/Directory.Exists report both as "false", which must never count as "no journal".
    private static Presence PresenceOf(string path, out string? error)
    {
        error = null;
        try
        {
            _ = File.GetAttributes(path);
            return Presence.Present;
        }
        catch (FileNotFoundException)
        {
            return Presence.Absent;
        }
        catch (DirectoryNotFoundException)
        {
            return Presence.Absent;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return Presence.Unknown;
        }
    }

    private static void WriteJournal(string journalPath, SdkPinJournal journal)
    {
        var directory = Path.GetDirectoryName(journalPath)!;
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            // A new journal directory is only durable once its own parent entry is flushed.
            if (Path.GetDirectoryName(directory) is { } parent)
                _ = DurableFlush.TryFlush(parent);
        }
        var temp = journalPath + ".tmp";
        WriteDurably(temp, JsonSerializer.SerializeToUtf8Bytes(journal, JournalJson));
        File.Move(temp, journalPath, overwrite: true);
        // The journal must be durable BEFORE global.json is touched, so flush the rename too.
        _ = DurableFlush.TryFlush(directory);
    }

    // Deletes a journal whose checkout needs no more repair, and flushes the deletion — only after the
    // restored file (and its metadata) was flushed, so a power loss never keeps the deletion but loses the restore.
    private void RetireJournal(string journalPath)
    {
        TryDelete(journalPath);
        if (Path.GetDirectoryName(journalPath) is { } directory)
            _ = DurableFlush.TryFlush(directory);
    }

    private static void WriteRestored(SdkPinJournalEntry entry, byte[] original)
    {
        WriteAtomically(entry.Path, entry.TempPath, original, entry.UnixMode);
        SetMetadata(entry);
    }

    private static void WriteAtomically(string path, string temp, byte[] content, int? unixMode)
    {
        var created = false;
        try
        {
            // CreateNew: never overwrite (or later delete) a file this guard did not create.
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            if (unixMode is { } mode && !OperatingSystem.IsWindows())
                File.SetUnixFileMode(temp, (UnixFileMode)mode);
            File.Move(temp, path, overwrite: true);
            created = false;
        }
        finally
        {
            if (created)
                DeleteQuietly(temp);
        }
        _ = DurableFlush.TryFlush(Path.GetDirectoryName(path)!);
    }

    private static void WriteDurably(string path, byte[] content)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(content);
        stream.Flush(flushToDisk: true);
    }

    private static void SetMetadata(SdkPinJournalEntry entry)
    {
        if (entry.UnixMode is { } mode && !OperatingSystem.IsWindows())
            File.SetUnixFileMode(entry.Path, (UnixFileMode)mode);
        File.SetLastWriteTimeUtc(entry.Path, new DateTime(entry.LastWriteTimeUtcTicks, DateTimeKind.Utc));
        // Mode and mtime are inode metadata: flush them too before the journal that repairs them is retired.
        _ = DurableFlush.TryFlush(entry.Path);
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"sdk-pin: could not delete '{path}': {ex.Message}");
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a stray temp file is journaled and removed by the next restore/recovery.
        }
    }

    private static string Sha256(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    private static string DisplayPath(string checkout, string path, bool inside) =>
        inside ? Path.GetRelativePath(checkout, path).Replace('\\', '/') : path;

    private static bool IsContained(string root, string candidate)
    {
        var rel = Path.GetRelativePath(root, candidate);
        return rel != "."
            && rel != ".."
            && !rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(rel);
    }

    private static bool PathEquals(string a, string b) =>
        GlobalJsonLocator.PathComparer.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b));
}

/// <summary>
/// The pins neutralized by one <see cref="SdkPinGuard.Apply"/>. <see cref="Restore"/> puts the committed
/// bytes back; it is idempotent and never throws, and a failure is reported in <see cref="RestoreError"/>
/// (the journal is kept so the next run can repair the checkout).
/// </summary>
public sealed class SdkPinOverlay
{
    private readonly SdkPinGuard _guard;
    private readonly string? _journalPath;
    private readonly IReadOnlyList<SdkPinJournalEntry> _applied;
    private bool _restored;

    internal SdkPinOverlay(
        SdkPinGuard guard, string checkoutDir, string? journalPath,
        IReadOnlyList<SdkPinJournalEntry> applied, IReadOnlyList<SdkPinFinding> findings)
    {
        _guard = guard;
        CheckoutDir = checkoutDir;
        _journalPath = journalPath;
        _applied = applied;
        Findings = findings;
        _restored = applied.Count == 0;
    }

    public string CheckoutDir { get; }

    /// <summary>Every unsatisfiable pin found for the load, overridden or not.</summary>
    public IReadOnlyList<SdkPinFinding> Findings { get; }

    /// <summary>The findings whose pin was neutralized for the load.</summary>
    public IEnumerable<SdkPinFinding> Overridden => Findings.Where(f => f.OverrideApplied);

    /// <summary>Non-null when restoring the committed <c>global.json</c> content failed.</summary>
    public string? RestoreError { get; private set; }

    /// <summary>Restores every neutralized <c>global.json</c>. Idempotent; never throws.</summary>
    public void Restore()
    {
        if (_restored)
            return;
        RestoreError = _guard.RestoreAll(CheckoutDir, _applied, _journalPath);
        _restored = RestoreError is null;
    }
}

internal sealed record SdkPinJournal
{
    public const int CurrentVersion = 1;

    /// <summary>
    /// The version of a journal with at least one <see cref="SdkPinJournalEntry.WorkTree"/> entry (issue #171):
    /// a binary that predates submodule entries refuses it (fails closed) instead of replaying it unchecked.
    /// </summary>
    public const int SubmoduleVersion = 2;

    public int Version { get; init; } = CurrentVersion;
    public required string CheckoutDir { get; init; }

    /// <summary>
    /// The commit the checkout's HEAD pointed at when the pin was neutralized. Always written by
    /// <see cref="SdkPinGuard.Apply"/>; a journal without it is rejected by recovery.
    /// </summary>
    public string? Head { get; init; }

    public DateTimeOffset CreatedUtc { get; init; }
    public List<SdkPinJournalEntry> Entries { get; init; } = [];
}

internal sealed record SdkPinJournalEntry
{
    public required string Path { get; init; }

    /// <summary>The uniquely-named sibling temp file used to rewrite <see cref="Path"/> atomically.</summary>
    public required string TempPath { get; init; }

    public required string OriginalBase64 { get; init; }
    public required string OriginalSha256 { get; init; }
    public required string NeutralizedSha256 { get; init; }
    public long LastWriteTimeUtcTicks { get; init; }
    public int? UnixMode { get; init; }

    /// <summary>
    /// For a <c>global.json</c> inside a populated submodule (issue #171): the innermost submodule's work tree.
    /// Null for a file owned by the checkout itself.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkTree { get; init; }

    /// <summary>The commit <see cref="WorkTree"/> was checked out at when the pin was neutralized.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkTreeHead { get; init; }
}
