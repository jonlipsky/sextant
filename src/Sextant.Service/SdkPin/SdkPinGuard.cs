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
    /// Where restore journals are written. Must be outside every checkout working tree. Null derives
    /// <c>&lt;parent of the checkout&gt;/.sextant-sdk-pin</c> — for a service checkout that is
    /// <c>&lt;CheckoutRoot&gt;/.sextant-sdk-pin</c>, the same directory the host configures explicitly.
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
/// A journal is only replayed when it is confined to its own checkout on the checkout volume.
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
    private readonly Action<string>? _log;

    public SdkPinGuard(SdkPinOptions? options = null, ISdkResolutionProbe? probe = null, Action<string>? log = null)
    {
        _options = options ?? new SdkPinOptions();
        _probe = probe ?? HostFxrSdkResolutionProbe.Instance;
        _log = log;
    }

    public SdkPinOptions Options => _options;

    /// <summary>The SDK versions installed on this worker, newest first (empty when unknown). Never throws.</summary>
    public IReadOnlyList<string> ListInstalledSdks() => SafeListInstalled();

    /// <summary>
    /// Detects every <c>global.json</c> governing the load of <paramref name="solutionPaths"/> whose SDK pin
    /// hostfxr cannot satisfy and — when enabled and safe — neutralizes it. With no failing pin this does no
    /// file I/O beyond locating <c>global.json</c> files and returns an overlay with no findings. The caller
    /// MUST call <see cref="SdkPinOverlay.Restore"/> (in a <c>finally</c>) as soon as the MSBuild load
    /// completes. Never throws for a pin problem; a failure is reported in the findings.
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
        try
        {
            WriteJournal(journalPath, new SdkPinJournal
            {
                CheckoutDir = checkout,
                Head = CheckoutHead.TryRead(checkout),
                CreatedUtc = DateTimeOffset.UtcNow,
                Entries = candidates.Select(c => c.Entry).ToList()
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var reason = $"the SDK-pin restore journal could not be written ({ex.Message}), so the checkout was not modified";
            findings.AddRange(candidates.Select(c => c.Finding with { NotOverriddenReason = reason }));
            return new SdkPinOverlay(this, checkout, journalPath: null, [], findings);
        }

        var applied = new List<SdkPinJournalEntry>();
        foreach (var (finding, entry, neutralized) in candidates)
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
                var restoreError = RestoreEntry(entry, recovering: false);
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

    internal string? RestoreAll(IReadOnlyList<SdkPinJournalEntry> entries, string? journalPath)
    {
        var errors = new List<string>();
        foreach (var entry in entries)
        {
            var error = RestoreEntry(entry, recovering: false);
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
        return LinkRefusal(checkout, path);
    }

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
    // RecoverJournal retires its journal without writing anything.) Returns null on success, else the error.
    private string? RestoreEntry(SdkPinJournalEntry entry, bool recovering)
    {
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

        if (journal.Head is not null)
        {
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
                // tree), so every journaled path now holds THAT commit's content — even one whose bytes happen to
                // equal the neutralized form. Never write to it: remove only this guard's own temp files.
                foreach (var entry in journal.Entries)
                    TryDelete(entry.TempPath);
                _log?.Invoke($"sdk-pin: checkout '{checkout}' moved from {journal.Head} to {head}; retiring its restore journal without touching the checkout.");
                RetireJournal(journalPath);
                return true;
            }
        }

        var errors = new List<string>();
        foreach (var entry in journal.Entries)
        {
            if (RestoreEntry(entry, recovering: true) is { } error)
                errors.Add(error);
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

    // A journal is only replayed when it is well-formed and confined: it must be the journal for the checkout
    // it names, that checkout must lie on the checkout volume the journal directory belongs to, and every
    // entry must be a distinct, non-symlinked global.json inside that checkout whose journaled bytes match
    // their hash and whose metadata is in range. Validation is total: any malformed shape is a problem string.
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
        if (journal.Version != SdkPinJournal.CurrentVersion)
            return $"unsupported journal version {journal.Version}";
        if (journal.Head is not null && !CheckoutHead.IsObjectId(journal.Head))
            return "its recorded HEAD is not a commit id";
        if (string.IsNullOrWhiteSpace(journal.CheckoutDir) || !Path.IsPathFullyQualified(journal.CheckoutDir))
            return "it names no absolute checkout path";

        var checkout = Path.TrimEndingDirectorySeparator(Path.GetFullPath(journal.CheckoutDir));
        if (!PathEquals(JournalPathFor(checkout), Path.GetFullPath(journalPath)))
            return "it is not the journal of the checkout it names";
        var volume = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(journalPath)));
        if (volume is null || !IsContained(volume, checkout))
            return "the checkout it names is outside the checkout volume";
        if (journal.Entries is not { Count: > 0 } entries)
            return "it lists no entries";

        var paths = new HashSet<string>(GlobalJsonLocator.PathComparer);
        var temps = new HashSet<string>(GlobalJsonLocator.PathComparer);
        foreach (var entry in entries)
        {
            if (EntryProblem(checkout, volume, entry) is { } problem)
                return problem;
            if (!paths.Add(Path.GetFullPath(entry.Path)) || !temps.Add(Path.GetFullPath(entry.TempPath)))
                return "it lists the same file twice";
        }
        return null;
    }

    private static string? EntryProblem(string checkout, string volume, SdkPinJournalEntry? entry)
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
        return LinkRefusal(checkout, Path.GetFullPath(entry.Path), volume);
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
        RestoreError = _guard.RestoreAll(_applied, _journalPath);
        _restored = RestoreError is null;
    }
}

internal sealed record SdkPinJournal
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public required string CheckoutDir { get; init; }

    /// <summary>The commit the checkout's HEAD pointed at when the pin was neutralized (null when unknown).</summary>
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
}
