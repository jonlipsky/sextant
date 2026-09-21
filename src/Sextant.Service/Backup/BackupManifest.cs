namespace Sextant.Service.Backup;

/// <summary>
/// The manifest written alongside a service backup (criterion 6). It records the backup format version,
/// when it was taken, the catalog SCHEMA version it was taken at (so a restore can refuse to load a
/// backup produced by a NEWER build than the restoring code), the relative locations of the backed-up
/// catalog and immutable-artifact volume, a configuration fingerprint, and — importantly — a
/// CREDENTIALS BOUNDARY note. Secrets (control/query/contribute tokens, LLM API keys) are NEVER written
/// into a backup; the manifest instead lists the environment variables an operator must re-provide on
/// restore, so the backup is safe at rest and the restore is reproducible.
/// </summary>
public sealed record BackupManifest
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public required long CreatedAt { get; init; }
    public required int SchemaVersion { get; init; }
    public string CatalogFile { get; init; } = "catalog.db";
    public string ArtifactDir { get; init; } = "artifacts";
    public required string ConfigFingerprint { get; init; }

    /// <summary>
    /// The secret-bearing AND authorization-critical configuration this backup deliberately does NOT
    /// contain (criterion 6: back up configuration + credentials as a documented BOUNDARY, not by dumping
    /// secrets). An operator must re-provide these on the restored host; they are listed by NAME only, never
    /// value. <c>SEXTANT_SERVICE_READ_POLICY</c> is included because a restore that forgot it would start an
    /// ANONYMOUSLY-READABLE service — restore must RE-ENFORCE slice-1 authz, so the operator is reminded to
    /// re-supply the read policy (and control/query tokens) alongside the recovered catalog.
    /// </summary>
    public IReadOnlyList<string> CredentialsBoundary { get; init; } =
    [
        "SEXTANT_SERVICE_CONTROL_TOKEN",
        "SEXTANT_SERVICE_QUERY_TOKEN",
        "SEXTANT_SERVICE_CONTRIBUTE_TOKEN",
        "SEXTANT_SERVICE_READ_POLICY",
        "SEXTANT_LLM_API_KEY"
    ];
}
