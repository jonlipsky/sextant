using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Core.Platform;
using Sextant.Store;

namespace Sextant.Service.Contributions;

/// <summary>Inputs describing WHO/WHERE produced a contribution (provenance the payload catalog cannot know).</summary>
public sealed record ContributionProvenance
{
    /// <summary>The authenticated tenant/owner identity the contribution is published under.</summary>
    public required string Tenant { get; init; }

    /// <summary>The producer identity (machine, CI runner, or user) for provenance.</summary>
    public required string Producer { get; init; }

    /// <summary>Whether the producing working tree was dirty (a dirty contribution is server-rejected).</summary>
    public bool WorkingTreeDirty { get; init; }

    /// <summary>The Sextant CLI version that produced the contribution.</summary>
    public string CliVersion { get; init; } = "unknown";

    /// <summary>Free-form execution provenance (CI run URL, pipeline id, etc.).</summary>
    public string? ExecutionProvenance { get; init; }

    /// <summary>Installed optional workload ids used (provenance).</summary>
    public IReadOnlyList<string> Workloads { get; init; } = [];
}

/// <summary>
/// Builds a deterministic <see cref="ContributionManifest"/> from a produced payload catalog (Phase 16).
/// The payload is a portable Sextant catalog containing ONE complete snapshot the client produced by
/// indexing the exact commit WITH its node capability; this reads that snapshot's identity + per-project
/// facts and folds them, with the caller's provenance, into the manifest the server validates.
///
/// The manifest's capability fields are read FROM the payload snapshot (what it actually recorded building
/// under), so "declared == built" holds by construction on the honest path — the server's capability check
/// then catches any manifest that claims a capability the payload did not record. Per-project target
/// frameworks/platforms come from the shared <see cref="TargetFrameworkFacts"/> parser (issue #65), so the
/// declared per-project capability is accurate and consistent with route-time detection. Pure and
/// MSBuild-free (it reads an existing catalog), so it is fully unit-testable without a Roslyn build.
/// </summary>
public static class ContributionManifestBuilder
{
    /// <summary>
    /// Builds the manifest for the complete snapshot identified by <paramref name="payloadSnapshotIdentityHash"/>
    /// in the payload catalog. Throws when that snapshot is absent or not complete (the client must publish a
    /// complete snapshot into its payload before packaging).
    /// </summary>
    public static ContributionManifest Build(
        SqliteConnection payload, string payloadSnapshotIdentityHash, ContributionProvenance provenance, long nowUnixMs)
    {
        var snapshots = new SnapshotStore(payload);
        var snapshot = snapshots.GetByIdentityHash(payloadSnapshotIdentityHash)
            ?? throw new InvalidOperationException(
                $"the payload contains no snapshot for identity '{payloadSnapshotIdentityHash}'.");
        if (snapshot.Status != SnapshotStatus.Complete)
            throw new InvalidOperationException(
                $"the payload snapshot is '{snapshot.Status}', not complete; only a complete snapshot is publishable.");

        var repositoryUrl = ReadRepositoryUrl(payload, snapshot.RepositoryId)
            ?? throw new InvalidOperationException("the payload snapshot has no repository row.");
        var commitSha = snapshots.GetCommitSha(snapshot.CommitId)
            ?? throw new InvalidOperationException("the payload snapshot has no commit row.");

        var projects = new List<ContributionProjectEntry>();
        foreach (var projectId in snapshots.GetSnapshotProjectIds(snapshot.Id).OrderBy(id => id))
            projects.Add(BuildProjectEntry(payload, projectId, snapshot.CapabilityFingerprint));

        return new ContributionManifest
        {
            Tenant = provenance.Tenant,
            RepositoryRemoteUrl = repositoryUrl,
            CommitSha = commitSha,
            TreeSha = snapshot.TreeSha,
            SchemaVersion = snapshot.SchemaVersion,
            AnalyzerVersion = snapshot.AnalyzerVersion,
            CliVersion = provenance.CliVersion,
            ConfigHash = snapshot.ConfigHash,
            ToolchainFingerprint = snapshot.ToolchainFingerprint ?? ToolchainFingerprint.Current,
            // Read the capability FROM the payload (declared == built by construction). Empty when the
            // client produced without a capability — the server then rejects it, which is correct.
            CapabilityFingerprint = snapshot.CapabilityFingerprint ?? string.Empty,
            PayloadSnapshotIdentityHash = snapshot.IdentityHash,
            WorkingTreeDirty = provenance.WorkingTreeDirty,
            Projects = projects,
            Producer = provenance.Producer,
            ExecutionProvenance = provenance.ExecutionProvenance,
            Workloads = provenance.Workloads,
            CreatedAtUnixMs = nowUnixMs
        };
    }

    private static ContributionProjectEntry BuildProjectEntry(
        SqliteConnection payload, long projectId, string? capabilityFingerprint)
    {
        string canonicalId, repoRelativePath;
        string? targetFramework;
        using (var cmd = payload.CreateCommand())
        {
            cmd.CommandText = """
                SELECT COALESCE(lp.canonical_id, p.canonical_id), p.repo_relative_path, p.target_framework
                FROM projects p
                LEFT JOIN logical_projects lp ON lp.id = p.logical_project_id
                WHERE p.id = @id;
                """;
            cmd.Parameters.AddWithValue("@id", projectId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                throw new InvalidOperationException($"payload project id {projectId} not found.");
            canonicalId = reader.GetString(0);
            repoRelativePath = reader.GetString(1);
            targetFramework = reader.IsDBNull(2) ? null : reader.GetString(2);
        }

        return new ContributionProjectEntry
        {
            CanonicalId = canonicalId,
            RepoRelativePath = repoRelativePath,
            // The MSBuild-evaluated TFM recorded during indexing is the high-fidelity per-project signal
            // (issue #65); the shared TargetFrameworkFacts parser derives the platform from it consistently
            // wherever a platform decision is needed (the placement probe, assembly compatibility).
            TargetFramework = targetFramework,
            CapabilityFingerprint = capabilityFingerprint ?? string.Empty,
            SourceFingerprints = ReadSourceFingerprints(payload, projectId)
        };
    }

    // Builds "repo-relative-path@git-blob-hash" fingerprints for a project's source files, so the server can
    // hash-verify declared content against provider Git content (falls back to the content hash when no git
    // blob hash was recorded). Deterministic order for a stable manifest.
    private static IReadOnlyList<string> ReadSourceFingerprints(SqliteConnection payload, long projectId)
    {
        var fingerprints = new List<string>();
        using var cmd = payload.CreateCommand();
        cmd.CommandText = """
            SELECT f.repo_relative_path, fv.git_blob_hash, fv.content_hash
            FROM files f
            JOIN file_versions fv ON fv.file_id = f.id
            WHERE f.project_id = @p
            ORDER BY f.repo_relative_path, fv.id;
            """;
        cmd.Parameters.AddWithValue("@p", projectId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var path = reader.GetString(0);
            var hashBytes = reader.IsDBNull(1)
                ? (reader.IsDBNull(2) ? null : (byte[])reader.GetValue(2))
                : (byte[])reader.GetValue(1);
            if (hashBytes is null)
                continue;
            fingerprints.Add($"{path}@{Convert.ToHexStringLower(hashBytes)}");
        }
        return fingerprints;
    }

    private static string? ReadRepositoryUrl(SqliteConnection payload, long repositoryId)
    {
        using var cmd = payload.CreateCommand();
        cmd.CommandText = "SELECT remote_url FROM repositories WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", repositoryId);
        var value = cmd.ExecuteScalar();
        return value is string s ? s : null;
    }
}
