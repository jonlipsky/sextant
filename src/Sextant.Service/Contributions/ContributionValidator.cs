using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Core.Platform;
using Sextant.Store;

namespace Sextant.Service.Contributions;

/// <summary>
/// The SUPPLY-CHAIN gate for a client/CI contribution (Phase 16, CRITICAL 1). A contribution is an
/// UNTRUSTED external input, so it is authenticated, authorized, and hash/capability-verified BEFORE a
/// single row is imported or published. Every check is ORDERED and produces a structured rejection
/// (<see cref="ContributionRejectionCode"/>) recorded in <c>snapshot_job_diagnostics</c> (acceptance
/// criterion 3); a contribution that fails ANY check is rejected and NEVER published as complete. No check
/// weakens an existing authz/immutability/completeness contract — validation only ADDS gates.
///
/// The checks, in order: (1) the working tree must be clean (a dirty contribution is rejected — criterion
/// 6); (2) repository + commit identity must be present; (3) the tenant must be authorized for the repo /
/// commit / projects; (4) the payload's schema + analyzer versions must match the service's, so an
/// incompatible payload is never assembled; (5) the payload must actually contain a COMPLETE snapshot at
/// the declared identity; (6) the declared producing capability must match what the payload recorded it
/// built under (declared == built); (7) declared source/import/reference blobs must verify against provider
/// Git content (fail-closed when the policy requires it); (8) the project graph must be self-consistent.
/// </summary>
public sealed class ContributionValidator(
    int expectedSchemaVersion,
    string expectedAnalyzerVersion,
    ContributionPolicy policy,
    IGitContentProvider gitContent,
    IContributionAuthorizer authorizer)
{
    /// <summary>Validator wired with the running service's own schema/analyzer versions.</summary>
    public static ContributionValidator ForService(
        ContributionPolicy policy, IGitContentProvider gitContent, IContributionAuthorizer authorizer) =>
        new(IndexDatabase.LatestSchemaVersion, IndexConfigurationHash.AnalyzerVersion, policy, gitContent, authorizer);

    /// <summary>
    /// Validates a contribution against the payload catalog (opened read-only) and the caller's auth token.
    /// Returns an accepting result carrying the resolved payload snapshot id, or a rejecting result with a
    /// structured code + per-project diagnostics. Never mutates state.
    /// </summary>
    public ContributionValidationResult Validate(ContributionArtifact artifact, SqliteConnection payloadConnection, string? token)
    {
        var m = artifact.Manifest;

        // (1) Dirty working tree: a committed, publishable contribution MUST be clean (criterion 6). The
        // client keeps a dirty tree local-only; a dirty artifact reaching the server is always rejected.
        if (m.WorkingTreeDirty)
            return Reject(ContributionRejectionCode.DirtyTree,
                "the contribution was produced from a DIRTY working tree; committed contributions must be clean.");

        // (2) Repository + commit identity must be present (they key the assembled snapshot).
        if (string.IsNullOrWhiteSpace(m.RepositoryRemoteUrl))
            return Reject(ContributionRejectionCode.RepositoryMismatch, "the contribution declares no repository.");
        if (string.IsNullOrWhiteSpace(m.CommitSha))
            return Reject(ContributionRejectionCode.CommitMismatch, "the contribution declares no commit.");

        // (3) Authorization: the tenant must be authorized for this repo/commit/projects. A wired authorizer
        // that DENIES always rejects; the RequireAuthorization policy additionally demands a token be present
        // so an open (dev) authorizer cannot silently accept in a deployment that opted into required auth.
        var authz = authorizer.Authorize(new ContributionAuthContext
        {
            Token = token,
            Tenant = m.Tenant,
            RepositoryRemoteUrl = m.RepositoryRemoteUrl,
            CommitSha = m.CommitSha,
            ProjectCanonicalIds = m.Projects.Select(p => p.CanonicalId).ToList()
        });
        if (!authz.Allowed)
            return Reject(ContributionRejectionCode.Unauthorized, authz.Reason ?? "the contributor is not authorized for this repository.");
        if (policy.RequireAuthorization && string.IsNullOrWhiteSpace(token))
            return Reject(ContributionRejectionCode.Unauthorized, "this service requires an authenticated contributor token.");

        // (4) Schema / analyzer compatibility with THIS service: a payload produced under a different schema
        // or extraction-logic version must never be assembled into this service's snapshot.
        if (m.SchemaVersion != expectedSchemaVersion)
            return Reject(ContributionRejectionCode.SchemaMismatch,
                $"contribution schema version {m.SchemaVersion} != service schema version {expectedSchemaVersion}.");
        if (!string.Equals(m.AnalyzerVersion, expectedAnalyzerVersion, StringComparison.Ordinal))
            return Reject(ContributionRejectionCode.AnalyzerMismatch,
                $"contribution analyzer version '{m.AnalyzerVersion}' != service analyzer version '{expectedAnalyzerVersion}'.");

        // (5) The payload must actually contain a COMPLETE snapshot at the declared identity — otherwise
        // there is nothing verifiable to import, and a partial/absent payload is never publishable.
        var payloadSnapshots = new SnapshotStore(payloadConnection);
        var payloadSnapshot = payloadSnapshots.GetByIdentityHash(m.PayloadSnapshotIdentityHash);
        if (payloadSnapshot is null)
            return Reject(ContributionRejectionCode.PayloadMissing,
                "the payload contains no snapshot matching the declared payload identity hash.");
        if (payloadSnapshot.Status != SnapshotStatus.Complete)
            return Reject(ContributionRejectionCode.PayloadMissing,
                $"the payload snapshot is '{payloadSnapshot.Status}', not complete; only a complete snapshot is publishable.");
        if (payloadSnapshot.SchemaVersion != m.SchemaVersion)
            return Reject(ContributionRejectionCode.SchemaMismatch,
                $"payload snapshot schema {payloadSnapshot.SchemaVersion} != declared schema {m.SchemaVersion}.");

        // (5b) BIND the payload to the manifest's declared (and authorized) identity. The tenant is authorized
        // for a specific repository + commit, but the importer imports whatever the PAYLOAD contains — so the
        // payload snapshot must actually belong to the SAME repository + commit the manifest declares. Without
        // this bind, a contributor authorized for repo A could ship repo B's complete snapshot (matching
        // schema/capability/count) and have B's content imported under A's assembly identity. The payload
        // catalog records its own repository + commit rows; compare them to the authorized manifest identity.
        var payloadRepositoryId = payloadSnapshots.GetRepositoryId(m.RepositoryRemoteUrl);
        if (payloadRepositoryId is null || payloadRepositoryId != payloadSnapshot.RepositoryId)
            return Reject(ContributionRejectionCode.RepositoryMismatch,
                "the payload snapshot belongs to a different repository than the manifest declares.");
        var payloadCommitSha = payloadSnapshots.GetCommitSha(payloadSnapshot.CommitId);
        if (!string.Equals(payloadCommitSha, m.CommitSha, StringComparison.Ordinal))
            return Reject(ContributionRejectionCode.CommitMismatch,
                "the payload snapshot's commit does not match the manifest's declared commit.");
        // Bind the REMAINING assembly-identity fields (tree, analyzer, config) the manifest declares to the
        // payload's own recorded identity. The assembly identity is keyed on these declared values, but the
        // importer imports whatever the payload contains — so a payload built for a DIFFERENT tree /
        // extraction-logic version / profile could otherwise be relabeled with acceptable manifest values and
        // published under a mismatched identity. (Toolchain is intentionally NOT bound here: it is excluded
        // from the assembly identity so cross-OS contributions can converge — it stays provenance only.)
        if (!string.Equals(payloadSnapshot.TreeSha, m.TreeSha, StringComparison.Ordinal))
            return Reject(ContributionRejectionCode.CommitMismatch,
                "the payload snapshot's tree does not match the manifest's declared commit tree.");
        if (!string.Equals(payloadSnapshot.AnalyzerVersion, m.AnalyzerVersion, StringComparison.Ordinal))
            return Reject(ContributionRejectionCode.AnalyzerMismatch,
                "the payload snapshot's analyzer version does not match the manifest's declared analyzer version.");
        if (!string.Equals(payloadSnapshot.ConfigHash, m.ConfigHash, StringComparison.Ordinal))
            return Reject(ContributionRejectionCode.ConfigMismatch,
                "the payload snapshot's config hash does not match the manifest's declared config hash.");

        // (6) Capability declared == built: the payload must record the capability it was produced under and
        // it must equal the manifest's declared capability (a contribution cannot claim a capability it did
        // not build with). This is what lets a native (Windows/macOS) contribution assemble ONLY with
        // compatible inputs (acceptance criterion 4) — the recorded per-project capability is trusted.
        if (string.IsNullOrEmpty(m.CapabilityFingerprint))
            return Reject(ContributionRejectionCode.CapabilityMismatch, "the contribution declares no capability fingerprint.");
        if (payloadSnapshot.CapabilityFingerprint is null)
            return Reject(ContributionRejectionCode.CapabilityMismatch,
                "the payload snapshot did not record the capability it was produced under (declared != built).");
        if (!string.Equals(payloadSnapshot.CapabilityFingerprint, m.CapabilityFingerprint, StringComparison.Ordinal))
            return Reject(ContributionRejectionCode.CapabilityMismatch,
                "the declared capability fingerprint does not match the capability the payload was produced under.");
        foreach (var project in m.Projects)
        {
            if (string.IsNullOrEmpty(project.CapabilityFingerprint))
                return Reject(ContributionRejectionCode.CapabilityMismatch,
                    $"project '{project.CanonicalId}' declares no capability fingerprint.",
                    project);
            // Every project version in ONE contribution was built by ONE environment, so each must declare
            // the SAME capability the contribution was produced under. A per-project capability that differs
            // from the contribution capability means the manifest was assembled from mismatched inputs.
            if (!string.Equals(project.CapabilityFingerprint, m.CapabilityFingerprint, StringComparison.Ordinal))
                return Reject(ContributionRejectionCode.CapabilityMismatch,
                    $"project '{project.CanonicalId}' declares a capability that differs from the contribution's built capability.",
                    project);
        }

        // (7) Git content verification: every declared source/import/reference blob must match provider Git
        // content for the exact commit. A MISMATCH is always fatal (tampered input). An UNAVAILABLE answer is
        // fatal ONLY when the policy requires verification (fail-closed); otherwise (single-node dev with no
        // provider) the content check is skipped.
        foreach (var project in m.Projects)
        {
            // Under required verification an EMPTY fingerprint set would pass zero checks and silently bypass
            // the gate, so a project that declares no source fingerprints is itself a fatal rejection.
            if (policy.RequireGitContentVerification && project.SourceFingerprints.Count == 0)
                return Reject(ContributionRejectionCode.ContentMismatch,
                    $"project '{project.CanonicalId}' declares no source fingerprints to verify (verification required).",
                    project);

            foreach (var fingerprint in project.SourceFingerprints
                         .Concat(project.ImportFingerprints)
                         .Concat(project.ReferenceFingerprints))
            {
                if (!TrySplitFingerprint(fingerprint, out var path, out var blobHash))
                {
                    if (policy.RequireGitContentVerification)
                        return Reject(ContributionRejectionCode.ContentMismatch,
                            $"malformed content fingerprint '{fingerprint}' in project '{project.CanonicalId}'.", project);
                    continue;
                }

                var check = gitContent.VerifyBlob(m.RepositoryRemoteUrl, m.CommitSha, path, blobHash);
                if (check == GitContentCheck.Mismatch)
                    return Reject(ContributionRejectionCode.ContentMismatch,
                        $"declared content for '{path}' does not match the repository blob at commit {m.CommitSha}.", project);
                if (check == GitContentCheck.Unavailable && policy.RequireGitContentVerification)
                    return Reject(ContributionRejectionCode.ContentMismatch,
                        $"could not verify content for '{path}' against provider Git content (verification required).", project);
            }
        }

        // (8) Project-graph consistency: the payload's mapped project versions must EXACTLY match the set the
        // manifest declares (not merely the count), so a payload cannot smuggle in undeclared project versions
        // nor can a tampered manifest declare authorized projects the payload does not contain — the
        // authorizer authorizes the DECLARED project ids, so the declared set must equal the imported set.
        // Canonical ids must also be UNIQUE — a manifest that declares the same project version twice is
        // malformed and would otherwise break the importer's per-project capability map.
        if (m.Projects.Select(p => p.CanonicalId).Distinct(StringComparer.Ordinal).Count() != m.Projects.Count)
            return Reject(ContributionRejectionCode.ProjectGraphMismatch,
                "the manifest declares duplicate project canonical ids.");
        var declaredCanonicalIds = m.Projects.Select(p => p.CanonicalId).ToHashSet(StringComparer.Ordinal);
        var payloadCanonicalIds = ReadPayloadCanonicalIds(payloadConnection, payloadSnapshots, payloadSnapshot.Id);
        if (!payloadCanonicalIds.SetEquals(declaredCanonicalIds))
            return Reject(ContributionRejectionCode.ProjectGraphMismatch,
                $"the manifest declares {declaredCanonicalIds.Count} project versions but the payload contains " +
                $"{payloadCanonicalIds.Count} that do not match the declared set.");

        return ContributionValidationResult.Accept(payloadSnapshot.Id);
    }

    // The set of logical canonical ids the payload snapshot's project versions map to (COALESCE of the
    // logical-project canonical id and the physical project canonical id — the same identity the importer
    // maps under), so the declared project set can be compared to what would actually be imported.
    private static HashSet<string> ReadPayloadCanonicalIds(
        SqliteConnection payload, SnapshotStore snapshots, long snapshotId)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var projectId in snapshots.GetSnapshotProjectIds(snapshotId))
        {
            using var cmd = payload.CreateCommand();
            cmd.CommandText = """
                SELECT COALESCE(lp.canonical_id, p.canonical_id)
                FROM projects p
                LEFT JOIN logical_projects lp ON lp.id = p.logical_project_id
                WHERE p.id = @id;
                """;
            cmd.Parameters.AddWithValue("@id", projectId);
            if (cmd.ExecuteScalar() is string canonical)
                ids.Add(canonical);
        }
        return ids;
    }

    private static ContributionValidationResult Reject(string code, string message, ContributionProjectEntry? project = null)
    {
        var diagnostic = new ProjectOutcome
        {
            ProjectCanonicalId = project?.CanonicalId,
            ProjectPath = project?.RepoRelativePath,
            Severity = JobDiagnosticSeverity.Error,
            Code = code,
            Message = message
        };
        return ContributionValidationResult.Reject(code, message, [diagnostic]);
    }

    // A content fingerprint is "repo-relative-path@git-blob-hash". Split on the LAST '@' so a path that
    // itself contains '@' is handled correctly.
    private static bool TrySplitFingerprint(string fingerprint, out string path, out string blobHash)
    {
        var at = fingerprint.LastIndexOf('@');
        if (at <= 0 || at == fingerprint.Length - 1)
        {
            path = fingerprint;
            blobHash = string.Empty;
            return false;
        }
        path = fingerprint[..at];
        blobHash = fingerprint[(at + 1)..];
        return true;
    }
}
