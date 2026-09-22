using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sextant.Core.Platform;

/// <summary>
/// One contributed logical project/TFM in a <see cref="ContributionManifest"/> (Phase 16). A CI/client
/// contribution is produced per logical project version by the environment that could actually build it,
/// and carries the capability that built it plus the git-content fingerprints the server hash-verifies
/// against provider Git content before publication (CRITICAL 1: a contribution is an untrusted supply-chain
/// input). Assembly (acceptance criterion 4) combines only mutually-compatible project versions into ONE
/// repository snapshot, so each entry records its own <see cref="CapabilityFingerprint"/> — a Windows entry
/// and a macOS entry can share one snapshot while still recording which capability produced each.
/// </summary>
public sealed record ContributionProjectEntry
{
    /// <summary>The commit-invariant logical canonical id (git-remote|repo-relative-path|tfm hash).</summary>
    public required string CanonicalId { get; init; }

    /// <summary>The project's repository-relative path (never absolute).</summary>
    public required string RepoRelativePath { get; init; }

    /// <summary>The evaluated target framework of this project version (null for a single-TFM project).</summary>
    public string? TargetFramework { get; init; }

    /// <summary>The Phase-15 capability fingerprint of the worker that built THIS project version.</summary>
    public required string CapabilityFingerprint { get; init; }

    /// <summary>Per-source-file fingerprints (repo-relative-path@git-blob-hash) the server hash-verifies.</summary>
    public IReadOnlyList<string> SourceFingerprints { get; init; } = [];

    /// <summary>Imported build-input fingerprints (Directory.Build.*, targets/props) the server verifies.</summary>
    public IReadOnlyList<string> ImportFingerprints { get; init; } = [];

    /// <summary>Referenced assembly/reference-pack fingerprints declared for compatibility gating.</summary>
    public IReadOnlyList<string> ReferenceFingerprints { get; init; } = [];

    /// <summary>The logical project-version keys this project version references (the project graph).</summary>
    public IReadOnlyList<string> ReferencedProjectVersionKeys { get; init; } = [];

    /// <summary>Declared completeness of this project version: <c>complete</c>/<c>partial</c>/<c>unsupported</c>.</summary>
    public string Completeness { get; init; } = "complete";

    /// <summary>Structured workspace diagnostics captured during evaluation (surfaced on rejection).</summary>
    public IReadOnlyList<string> WorkspaceDiagnostics { get; init; } = [];
}

/// <summary>Per-payload-section counts + content hashes (the compact semantic tables) for verification.</summary>
public sealed record ContributionPayloadSection
{
    public required string Name { get; init; }
    public required long Count { get; init; }
    public required string ContentHash { get; init; }
}

/// <summary>
/// The deterministic contribution manifest (Phase 16) that accompanies a client/CI semantic payload. It
/// implements the manifest contract in <c>platform-indexing.md</c>: tenant + repository identity, exact
/// commit/tree, the per-project/TFM project versions and their git-content fingerprints, the schema /
/// analyzer / CLI / toolchain / capability versions, the config/profile hash, producer + execution
/// provenance, and per-section counts/hashes. The server folds the identity fields into the SAME Phase-9
/// <see cref="SnapshotIdentity"/> a native worker would publish under, so a contribution attaches to the
/// ONE repository snapshot for that committed state (assembly), while the whole artifact is content-addressed
/// for idempotent, no-op re-upload.
///
/// It is ProcessStack- and service-AGNOSTIC (it lives in core), like <see cref="SnapshotIdentity"/> and
/// <see cref="WorkerCapability"/>: the client produces it and the service consumes/validates it, but core
/// never depends on the service (asserted by <c>ArchitectureBoundaryTests</c>).
/// </summary>
public sealed record ContributionManifest
{
    /// <summary>Manifest format version.</summary>
    public string Version { get; init; } = "1";

    /// <summary>The ProcessStack/GitHub tenant identity the contributor authenticated under.</summary>
    public required string Tenant { get; init; }

    /// <summary>The normalized repository remote URL (or a <c>local://</c> identity).</summary>
    public required string RepositoryRemoteUrl { get; init; }

    /// <summary>The exact commit SHA the contribution was produced at.</summary>
    public required string CommitSha { get; init; }

    /// <summary>The commit's tree SHA (part of snapshot identity; verified against provider Git content).</summary>
    public string? TreeSha { get; init; }

    /// <summary>The on-disk store schema version the payload was produced under.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>The extraction-logic (analyzer) version the payload was produced under.</summary>
    public required string AnalyzerVersion { get; init; }

    /// <summary>The Sextant CLI version that produced the contribution (provenance).</summary>
    public required string CliVersion { get; init; }

    /// <summary>The Roslyn version used (provenance; optional).</summary>
    public string? RoslynVersion { get; init; }

    /// <summary>The MSBuild version used (provenance; optional).</summary>
    public string? MsBuildVersion { get; init; }

    /// <summary>The .NET SDK version used (provenance; optional).</summary>
    public string? SdkVersion { get; init; }

    /// <summary>The installed optional workload ids used (provenance; optional).</summary>
    public IReadOnlyList<string> Workloads { get; init; } = [];

    /// <summary>The Phase-8 profile/feature configuration hash the payload was produced under.</summary>
    public string? ConfigHash { get; init; }

    /// <summary>The runtime toolchain fingerprint that produced the payload (<see cref="ToolchainFingerprint.Current"/>).</summary>
    public required string ToolchainFingerprint { get; init; }

    /// <summary>The producing NODE's default worker-capability fingerprint (the assembly-compat capability).</summary>
    public required string CapabilityFingerprint { get; init; }

    /// <summary>
    /// The identity hash the client published the payload snapshot under, inside its own payload catalog.
    /// The importer resolves the payload's complete snapshot by this hash. Distinct from
    /// <see cref="ToSnapshotIdentity"/> (the capability-less assembly target on the server).
    /// </summary>
    public required string PayloadSnapshotIdentityHash { get; init; }

    /// <summary>
    /// Whether the producing working tree was dirty. A publishable committed contribution MUST be clean
    /// (<c>false</c>); the server rejects a dirty contribution (acceptance criterion 3/6) and the client
    /// never uploads a dirty tree without a separate explicit opt-in.
    /// </summary>
    public bool WorkingTreeDirty { get; init; }

    /// <summary>The contributed project versions (per logical project/TFM).</summary>
    public IReadOnlyList<ContributionProjectEntry> Projects { get; init; } = [];

    /// <summary>Per-section counts + content hashes for the compact semantic payload tables.</summary>
    public IReadOnlyList<ContributionPayloadSection> PayloadSections { get; init; } = [];

    /// <summary>The producer identity (machine/CI runner/user) for provenance.</summary>
    public required string Producer { get; init; }

    /// <summary>Free-form execution provenance (CI run URL, pipeline id, etc.).</summary>
    public string? ExecutionProvenance { get; init; }

    /// <summary>Creation timestamp (unix ms).</summary>
    public long CreatedAtUnixMs { get; init; }

    /// <summary>Optional signature/attestation over the canonical manifest (added after the base protocol).</summary>
    public string? Signature { get; init; }

    /// <summary>
    /// The Phase-9 snapshot identity this contribution targets on the server. It is deliberately
    /// capability-LESS **and toolchain-LESS** at the snapshot level: an ASSEMBLED repository snapshot
    /// combines project versions built by DIFFERENT environments — a Windows runner and a macOS runner
    /// (acceptance criterion 4) — whose <see cref="ToolchainFingerprint"/> necessarily differ (OS +
    /// architecture are folded into it), so including the toolchain would give each environment a DIFFERENT
    /// assembly identity and they could never converge on ONE pending snapshot. The compatibility that
    /// actually matters for assembling extracted semantics — schema version, analyzer (extraction-logic)
    /// version, and the profile/config hash — IS in the identity and is independently verified per
    /// contribution, and each contribution's full toolchain + per-project capability are recorded in
    /// provenance. Phase-15 single-worker capability-in-identity is unaffected: that path folds capability
    /// into the identity of a whole-snapshot single-worker build; this assembly path records capability and
    /// toolchain per contributed project version instead.
    /// </summary>
    public SnapshotIdentity ToSnapshotIdentity() => new()
    {
        RepositoryRemoteUrl = RepositoryRemoteUrl,
        CommitSha = CommitSha,
        TreeSha = TreeSha,
        SchemaVersion = SchemaVersion,
        AnalyzerVersion = AnalyzerVersion,
        ConfigHash = ConfigHash,
        ToolchainFingerprint = string.Empty,
        CapabilityFingerprint = null
    };

    private static readonly JsonSerializerOptions CanonicalJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false
    };

    /// <summary>
    /// The deterministic canonical bytes of this manifest (UTF-8 JSON in declared property order). Two
    /// logically-equal manifests serialize identically, so an artifact's content address is stable and a
    /// re-upload dedupes (acceptance criterion 2).
    /// </summary>
    public byte[] CanonicalBytes() => JsonSerializer.SerializeToUtf8Bytes(this, CanonicalJson);

    /// <summary>A stable SHA-256 (hex) over the canonical manifest bytes.</summary>
    [JsonIgnore]
    public string ManifestHash => Convert.ToHexStringLower(SHA256.HashData(CanonicalBytes()));

    /// <summary>Parses a manifest from its canonical UTF-8 JSON bytes.</summary>
    public static ContributionManifest FromBytes(ReadOnlySpan<byte> bytes)
    {
        var manifest = JsonSerializer.Deserialize<ContributionManifest>(bytes, CanonicalJson)
            ?? throw new FormatException("The contribution manifest could not be parsed.");
        manifest.ValidateShape();
        return manifest;
    }

    // A hand-crafted or tampered manifest can set a required collection (or a collection ENTRY) to explicit
    // JSON null: STJ's `required` enforces PRESENCE only, not non-null, and an explicit `null` overrides a
    // property initializer. Left unchecked, a null Projects list, a null project entry, or a null fingerprint
    // collection would NRE deep inside authorization / git-content verification / import as an unstructured
    // 500. Reject a malformed SHAPE here at the parse boundary so it becomes a structured MalformedArtifact
    // rejection instead (acceptance criterion 3: rejected with a structured reason, never a server fault).
    private void ValidateShape()
    {
        // Only the v1 wire format is understood. An unknown (future or attacker-chosen) version must NOT be
        // silently interpreted with v1 semantics, so pin the exact supported version at the parse boundary.
        if (!string.Equals(Version, "1", StringComparison.Ordinal))
            throw Malformed($"version '{Version}' (only contribution manifest version '1' is supported)");
        if (Workloads is null) throw Malformed("workloads");
        if (Projects is null) throw Malformed("projects");
        if (PayloadSections is null) throw Malformed("payload_sections");
        if (Workloads.Any(w => w is null)) throw Malformed("workloads[] entry");
        if (Projects.Any(p => p is null)) throw Malformed("projects[] entry");
        if (PayloadSections.Any(s => s is null)) throw Malformed("payload_sections[] entry");

        foreach (var p in Projects)
        {
            if (p.SourceFingerprints is null) throw Malformed($"projects['{p.CanonicalId}'].source_fingerprints");
            if (p.ImportFingerprints is null) throw Malformed($"projects['{p.CanonicalId}'].import_fingerprints");
            if (p.ReferenceFingerprints is null) throw Malformed($"projects['{p.CanonicalId}'].reference_fingerprints");
            if (p.ReferencedProjectVersionKeys is null) throw Malformed($"projects['{p.CanonicalId}'].referenced_project_version_keys");
            if (p.WorkspaceDiagnostics is null) throw Malformed($"projects['{p.CanonicalId}'].workspace_diagnostics");
            if (p.SourceFingerprints.Any(s => s is null)
                || p.ImportFingerprints.Any(s => s is null)
                || p.ReferenceFingerprints.Any(s => s is null)
                || p.ReferencedProjectVersionKeys.Any(s => s is null)
                || p.WorkspaceDiagnostics.Any(s => s is null))
                throw Malformed($"projects['{p.CanonicalId}'] contains a null collection entry");
        }
    }

    private static FormatException Malformed(string field) =>
        new($"The contribution manifest has a malformed '{field}' (null where a value is required).");
}
