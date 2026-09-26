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
/// <b>Crash safety.</b> The original bytes are journaled (atomically, fsynced) OUTSIDE every working tree
/// before any file is modified. <see cref="RecoverAll"/>/<see cref="Recover"/> replay a leftover journal on
/// the next run: a file is restored only when it still holds exactly the neutralized content; a file holding
/// the original or foreign content (e.g. someone re-checked-out the tree) is left alone.
/// </para>
/// <para>
/// In-place modification is safe because the service runs one worker at a time behind its write gate and
/// cross-process writer lease, and nothing else reads <c>global.json</c> from the checkout while a job runs.
/// </para>
/// </summary>
public sealed class SdkPinGuard
{
    private const string TempFileName = ".global.json.sextant-sdk-pin.tmp";

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

            var refusal = readError ?? Refusal(checkout, path, inside);
            byte[] neutralized = [];
            if (refusal is null && !GlobalJsonSdkPin.TryNeutralize(original!, out neutralized, out var neutralizeError))
                refusal = neutralizeError;

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
                WriteAtomically(entry.Path, neutralized, entry.UnixMode);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(Path.Combine(Path.GetDirectoryName(entry.Path)!, TempFileName));
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
            TryDelete(journalPath);

        return new SdkPinOverlay(this, checkout, applied.Count > 0 ? journalPath : null, applied, findings);
    }

    /// <summary>
    /// Replays every leftover journal under the configured <see cref="SdkPinOptions.JournalRoot"/> (a crash
    /// between neutralizing and restoring). Run at the start of every job, BEFORE any checkout is reused or
    /// inspected. Never throws. Returns the number of journals fully resolved.
    /// </summary>
    public int RecoverAll()
    {
        if (_options.JournalRoot is not { } root || !Directory.Exists(root))
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

    /// <summary>Replays the leftover journal for one checkout, if any. Never throws.</summary>
    public bool Recover(string checkoutDir)
    {
        var journal = JournalPathFor(Path.GetFullPath(checkoutDir));
        return !File.Exists(journal) || RecoverJournal(journal);
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
            TryDelete(journalPath);
        return null;
    }

    private List<(string Path, HostFxrSdkResolutionError Error)> FindFailingPins(IReadOnlyList<string> solutionPaths)
    {
        var pins = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in GlobalJsonLocator.EvaluationDirectories(solutionPaths))
        {
            if (GlobalJsonLocator.FindNearest(directory) is { } pin && seen.Add(pin))
                pins.Add(pin);
        }

        var failing = new List<(string, HostFxrSdkResolutionError)>();
        var failingSeen = new HashSet<string>(StringComparer.Ordinal);
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

        try
        {
            if (new FileInfo(path).LinkTarget is not null)
                return "the global.json is a symbolic link, so the service does not modify it";
            for (var dir = Path.GetDirectoryName(path); dir is not null && !PathEquals(dir, checkout); dir = Path.GetDirectoryName(dir))
            {
                if (new DirectoryInfo(dir).LinkTarget is not null)
                    return "a directory containing the global.json is a symbolic link, so the service does not modify it";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"the global.json could not be inspected ({ex.Message})";
        }

        return null;
    }

    private static SdkPinJournalEntry NewEntry(string path, byte[] original, byte[] neutralized)
    {
        int? mode = null;
        if (!OperatingSystem.IsWindows())
            mode = (int)File.GetUnixFileMode(path);

        return new SdkPinJournalEntry
        {
            Path = path,
            OriginalBase64 = Convert.ToBase64String(original),
            OriginalSha256 = Sha256(original),
            NeutralizedSha256 = Sha256(neutralized),
            LastWriteTimeUtcTicks = File.GetLastWriteTimeUtc(path).Ticks,
            UnixMode = mode
        };
    }

    // Restores one entry. Normal restore puts the committed bytes back unless the file now holds FOREIGN
    // content (neither neutralized nor original — someone else changed it; never clobber). Recovery after a
    // crash is stricter about a missing file: it is not recreated (the checkout may have been replaced).
    // Returns null on success, else the error.
    private string? RestoreEntry(SdkPinJournalEntry entry, bool recovering)
    {
        try
        {
            TryDelete(Path.Combine(Path.GetDirectoryName(entry.Path)!, TempFileName));
            var original = Convert.FromBase64String(entry.OriginalBase64);

            if (!File.Exists(entry.Path))
            {
                if (recovering)
                {
                    _log?.Invoke($"sdk-pin: '{entry.Path}' no longer exists; nothing to restore.");
                    return null;
                }
                WriteAtomically(entry.Path, original, entry.UnixMode);
                SetMetadata(entry);
                return null;
            }

            var current = Sha256(File.ReadAllBytes(entry.Path));
            if (string.Equals(current, entry.OriginalSha256, StringComparison.Ordinal))
            {
                SetMetadata(entry);
                return null;
            }

            if (!string.Equals(current, entry.NeutralizedSha256, StringComparison.Ordinal))
            {
                var message = $"'{entry.Path}' was changed by something else while its SDK pin was neutralized; it was left as-is";
                if (recovering)
                {
                    _log?.Invoke($"sdk-pin: {message}.");
                    return null;
                }
                return message;
            }

            WriteAtomically(entry.Path, original, entry.UnixMode);
            SetMetadata(entry);
            if (recovering)
                _log?.Invoke($"sdk-pin: recovered the committed '{entry.Path}' left neutralized by an interrupted job.");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return $"could not restore the committed '{entry.Path}': {ex.Message}";
        }
    }

    private bool RecoverJournal(string journalPath)
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

        var errors = new List<string>();
        foreach (var entry in journal?.Entries ?? [])
        {
            if (RestoreEntry(entry, recovering: true) is { } error)
                errors.Add(error);
        }

        if (errors.Count > 0)
        {
            foreach (var error in errors)
                _log?.Invoke($"sdk-pin: recovery: {error}");
            return false;
        }

        TryDelete(journalPath);
        return true;
    }

    private static void WriteJournal(string journalPath, SdkPinJournal journal)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        var temp = journalPath + ".tmp";
        WriteDurably(temp, JsonSerializer.SerializeToUtf8Bytes(journal, JournalJson));
        File.Move(temp, journalPath, overwrite: true);
    }

    private static void WriteAtomically(string path, byte[] content, int? unixMode)
    {
        var temp = Path.Combine(Path.GetDirectoryName(path)!, TempFileName);
        WriteDurably(temp, content);
        if (unixMode is { } mode && !OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, (UnixFileMode)mode);
        File.Move(temp, path, overwrite: true);
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
        string.Equals(
            Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
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
    public int Version { get; init; } = 1;
    public required string CheckoutDir { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public List<SdkPinJournalEntry> Entries { get; init; } = [];
}

internal sealed record SdkPinJournalEntry
{
    public required string Path { get; init; }
    public required string OriginalBase64 { get; init; }
    public required string OriginalSha256 { get; init; }
    public required string NeutralizedSha256 { get; init; }
    public long LastWriteTimeUtcTicks { get; init; }
    public int? UnixMode { get; init; }
}
