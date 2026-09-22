using Microsoft.Data.Sqlite;
using Sextant.Store;

namespace Sextant.Service.Contributions;

/// <summary>Structured rejection codes for a contribution (recorded in <c>snapshot_job_diagnostics</c>).</summary>
public static class ContributionRejectionCode
{
    public const string MalformedArtifact = "malformed_artifact";
    public const string SizeLimit = "size_limit";
    public const string DirtyTree = "dirty_tree";
    public const string RepositoryMismatch = "repository_mismatch";
    public const string CommitMismatch = "commit_mismatch";
    public const string Unauthorized = "unauthorized";
    public const string SchemaMismatch = "schema_mismatch";
    public const string AnalyzerMismatch = "analyzer_mismatch";
    public const string PayloadMissing = "payload_missing";
    public const string ContentMismatch = "content_mismatch";
    public const string ConfigMismatch = "config_mismatch";
    public const string CapabilityMismatch = "capability_mismatch";
    public const string ProjectGraphMismatch = "project_graph_mismatch";
}

/// <summary>
/// The outcome of validating a contribution BEFORE any import/publish (Phase 16, CRITICAL 1). On rejection
/// it carries a structured <see cref="Code"/> + <see cref="Message"/> and per-project
/// <see cref="Diagnostics"/> (surfaced through <c>snapshot_job_diagnostics</c>, acceptance criterion 3) and
/// the contribution is NEVER published. On acceptance it carries the resolved <see cref="PayloadSnapshotId"/>
/// the importer copies from.
/// </summary>
public sealed record ContributionValidationResult
{
    public required bool Ok { get; init; }
    public string? Code { get; init; }
    public string? Message { get; init; }
    public long? PayloadSnapshotId { get; init; }
    public IReadOnlyList<ProjectOutcome> Diagnostics { get; init; } = [];

    public static ContributionValidationResult Reject(string code, string message, IReadOnlyList<ProjectOutcome>? diagnostics = null) => new()
    {
        Ok = false,
        Code = code,
        Message = message,
        Diagnostics = diagnostics ?? [new ProjectOutcome { Severity = JobDiagnosticSeverity.Error, Code = code, Message = message }]
    };

    public static ContributionValidationResult Accept(long payloadSnapshotId, IReadOnlyList<ProjectOutcome>? diagnostics = null) => new()
    {
        Ok = true,
        PayloadSnapshotId = payloadSnapshotId,
        Diagnostics = diagnostics ?? []
    };
}
