using Microsoft.Data.Sqlite;
using Sextant.Core;

namespace Sextant.Store;

/// <summary>Lifecycle status of a Phase-9 immutable snapshot generation.</summary>
public static class SnapshotStatus
{
    /// <summary>Rows are being staged; never selected by a branch pointer.</summary>
    public const string Pending = "pending";

    /// <summary>Validated and publishable; the only status a branch pointer may reference.</summary>
    public const string Complete = "complete";

    /// <summary>Some projects failed; diagnosable but never selected.</summary>
    public const string Partial = "partial";

    /// <summary>The run failed; diagnosable but never selected.</summary>
    public const string Failed = "failed";

    /// <summary>The (schema/analyzer/toolchain) inputs are unsupported; never selected.</summary>
    public const string Unsupported = "unsupported";

    /// <summary>A previously-complete snapshot replaced by a newer one on the same branch.</summary>
    public const string Superseded = "superseded";
}

/// <summary>A row of the <c>snapshots</c> table.</summary>
public sealed record SnapshotRow
{
    public required long Id { get; init; }
    public required long RepositoryId { get; init; }
    public long? CommitId { get; init; }
    public long? RunId { get; init; }
    public required string IdentityHash { get; init; }
    public string? TreeSha { get; init; }
    public required int SchemaVersion { get; init; }
    public required string AnalyzerVersion { get; init; }
    public string? ConfigHash { get; init; }
    public string? ToolchainFingerprint { get; init; }
    public required string Status { get; init; }
    public required long CreatedAt { get; init; }
    public long? PublishedAt { get; init; }

    /// <summary>
    /// The Phase-15 worker-capability fingerprint of the worker that produced this snapshot, or null for
    /// a local/single-node run that did not route. Recorded in provenance and folded into the snapshot
    /// identity only when non-null, so an incompatible-capability reuse is blocked (criterion 5).
    /// </summary>
    public string? CapabilityFingerprint { get; init; }

    /// <summary>The committed base snapshot this overlay layers on, or null for a base/full snapshot (Phase 10).</summary>
    public long? BaseSnapshotId { get; init; }

    /// <summary>True for an overlay generation (Phase 10); false for a base/full one.</summary>
    public bool IsOverlay { get; init; }

    /// <summary>The dirty working-tree delta digest folded into the identity, or null for a clean tree (Phase 10, #43).</summary>
    public string? WorkingTreeDelta { get; init; }

    /// <summary>When Phase 10 fell back to a full local index for lack of a compatible base, the reason (criterion 5).</summary>
    public string? FallbackReason { get; init; }

    /// <summary>
    /// True for a Phase-12 PROVIDER snapshot: a deduplicated, shared submodule generation owned by no
    /// parent, discovered/reused by identity and referenced through <c>snapshot_dependencies</c> edges.
    /// A provider snapshot carries no branch pointer and is excluded from the scope-less single-repo
    /// default. False for a normal parent/base/overlay snapshot.
    /// </summary>
    public bool IsProvider { get; init; }
}

/// <summary>
/// Thrown when a producer/contribution supplies a logical-project tuple (repo-relative path + target
/// framework) that DIVERGES from the metadata already stored for the same <c>canonical_id</c> (issue #69).
/// A contribution must never silently mutate another producer's shared logical-project row, so the write
/// is rejected rather than overwriting shared state.
/// </summary>
public sealed class LogicalProjectConflictException(string message) : Exception(message);

/// <summary>
/// Reads and writes the Phase-9 immutable-snapshot tables (<c>repositories</c>, <c>commits</c>,
/// <c>logical_projects</c>, <c>snapshots</c>, <c>branches</c>, <c>snapshot_projects</c>). The store is
/// a thin adapter over parameterized SQL; it runs on the caller's connection so its publish statements
/// (<see cref="MarkComplete"/>, <see cref="SetBranchPointer"/>) enrol in the caller's ambient write
/// transaction and flip the branch pointer atomically with the last batch of data.
/// </summary>
public sealed class SnapshotStore(SqliteConnection connection)
{
    // ---- get-or-create identity rows -----------------------------------------------------------

    public long EnsureRepository(string remoteUrl, long now)
    {
        using var cmd = connection.CreateCommand();
        // A repository indexed in its own right is a PRIMARY/consumer repo: clear any is_provider flag
        // left over from an earlier submodule-provider-only discovery so the scope-less single-repo local
        // default counts it. For an ordinary consumer this stays 0 (no-op).
        cmd.CommandText = """
            INSERT INTO repositories (remote_url, created_at) VALUES (@url, @now)
            ON CONFLICT(remote_url) DO UPDATE SET is_provider = 0
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("@url", remoteUrl);
        cmd.Parameters.AddWithValue("@now", now);
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// Get-or-create the repository row for a submodule PROVIDER (Phase 12), flagged <c>is_provider = 1</c>
    /// so the scope-less single-repo local default counts only primary/consumer repositories. If the
    /// remote was already indexed as a PRIMARY repository (<c>is_provider = 0</c>) the flag is left as-is:
    /// a repository genuinely indexed in its own right stays a consumer for default selection, and its
    /// symbols are simply shared. Only a brand-new provider-only remote is marked a provider.
    /// </summary>
    public long EnsureProviderRepository(string remoteUrl, long now)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO repositories (remote_url, created_at, is_provider) VALUES (@url, @now, 1)
            ON CONFLICT(remote_url) DO UPDATE SET remote_url = excluded.remote_url
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("@url", remoteUrl);
        cmd.Parameters.AddWithValue("@now", now);
        return (long)cmd.ExecuteScalar()!;
    }

    public long EnsureCommit(long repositoryId, string commitSha, string? treeSha, long now)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO commits (repository_id, commit_sha, tree_sha, created_at)
            VALUES (@repo, @sha, @tree, @now)
            ON CONFLICT(repository_id, commit_sha) DO UPDATE SET tree_sha = COALESCE(excluded.tree_sha, commits.tree_sha)
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("@repo", repositoryId);
        cmd.Parameters.AddWithValue("@sha", commitSha);
        cmd.Parameters.AddWithValue("@tree", (object?)treeSha ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", now);
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>
    /// Get-or-verify the commit-invariant logical project identity WITHOUT perturbing shared state (issue
    /// #69). <paramref name="canonicalId"/> is the existing logical hash (git-remote|repo-relative-path|tfm);
    /// it is the identity surfaced to clients and correlated across snapshots — never the
    /// per-snapshot-suffixed storage value on the row. The row is inserted when absent; on conflict the
    /// EXISTING metadata is kept, never overwritten — a contribution or a second producer must not silently
    /// mutate another producer's logical-project tuple. If the supplied <paramref name="repoRelativePath"/>/
    /// <paramref name="targetFramework"/> DIVERGE from the stored tuple, this throws
    /// <see cref="LogicalProjectConflictException"/> rather than corrupting shared metadata. (canonical_id
    /// deterministically derives from the path/tfm, so an honest producer never diverges.)
    /// </summary>
    public long EnsureLogicalProject(long repositoryId, string canonicalId, string repoRelativePath, string? targetFramework, long now)
    {
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO logical_projects (repository_id, canonical_id, repo_relative_path, target_framework, created_at)
                VALUES (@repo, @canon, @rel, @tfm, @now)
                ON CONFLICT(repository_id, canonical_id) DO NOTHING;
                """;
            insert.Parameters.AddWithValue("@repo", repositoryId);
            insert.Parameters.AddWithValue("@canon", canonicalId);
            insert.Parameters.AddWithValue("@rel", repoRelativePath);
            insert.Parameters.AddWithValue("@tfm", (object?)targetFramework ?? DBNull.Value);
            insert.Parameters.AddWithValue("@now", now);
            insert.ExecuteNonQuery();
        }

        using var read = connection.CreateCommand();
        read.CommandText = """
            SELECT id, repo_relative_path, target_framework
            FROM logical_projects WHERE repository_id = @repo AND canonical_id = @canon;
            """;
        read.Parameters.AddWithValue("@repo", repositoryId);
        read.Parameters.AddWithValue("@canon", canonicalId);
        using var reader = read.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException($"logical_projects row for canonical_id '{canonicalId}' vanished after upsert.");

        var id = reader.GetInt64(0);
        var storedPath = reader.IsDBNull(1) ? null : reader.GetString(1);
        var storedTfm = reader.IsDBNull(2) ? null : reader.GetString(2);
        if (storedPath != repoRelativePath || storedTfm != targetFramework)
            throw new LogicalProjectConflictException(
                $"contribution project tuple diverges from the shared logical project '{canonicalId}': " +
                $"stored ({storedPath}, {storedTfm ?? "<null>"}) vs supplied ({repoRelativePath}, {targetFramework ?? "<null>"}).");
        return id;
    }

    public long EnsureBranch(long repositoryId, string name, bool isDefault, long now)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO branches (repository_id, name, snapshot_id, is_default, updated_at)
            VALUES (@repo, @name, NULL, @is_default, @now)
            ON CONFLICT(repository_id, name) DO UPDATE SET is_default = excluded.is_default
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("@repo", repositoryId);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@is_default", isDefault ? 1 : 0);
        cmd.Parameters.AddWithValue("@now", now);
        return (long)cmd.ExecuteScalar()!;
    }

    // ---- snapshots -----------------------------------------------------------------------------

    /// <summary>
    /// Idempotently begins (or attaches to) a snapshot for an identity. If a snapshot with the same
    /// identity hash already exists it is returned with <c>existed = true</c> and its current status so a
    /// duplicate publish attaches to existing work and never creates a second snapshot (criterion 3).
    /// Otherwise a fresh <see cref="SnapshotStatus.Pending"/> snapshot is created. Single-writer, so the
    /// select-then-insert has no race.
    /// </summary>
    public (long id, bool existed, string status) BeginPending(
        SnapshotIdentity identity, long repositoryId, long? commitId, long? runId, long now,
        long? baseSnapshotId = null, string? fallbackReason = null, bool isProvider = false)
    {
        var existing = GetByIdentityHash(identity.Hash);
        if (existing is not null)
            return (existing.Id, true, existing.Status);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO snapshots
                (repository_id, commit_id, run_id, identity_hash, tree_sha, schema_version,
                 analyzer_version, config_hash, toolchain_fingerprint, status, created_at,
                 base_snapshot_id, is_overlay, working_tree_delta, fallback_reason, is_provider,
                 capability_fingerprint)
            VALUES (@repo, @commit, @run, @hash, @tree, @schema, @analyzer, @config, @toolchain, @status, @now,
                    @base, @is_overlay, @delta, @fallback, @is_provider, @capability)
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("@repo", repositoryId);
        cmd.Parameters.AddWithValue("@commit", (object?)commitId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@run", (object?)runId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@hash", identity.Hash);
        cmd.Parameters.AddWithValue("@tree", (object?)identity.TreeSha ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@schema", identity.SchemaVersion);
        cmd.Parameters.AddWithValue("@analyzer", identity.AnalyzerVersion);
        cmd.Parameters.AddWithValue("@config", (object?)identity.ConfigHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@toolchain", (object?)identity.ToolchainFingerprint ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@status", SnapshotStatus.Pending);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.Parameters.AddWithValue("@base", (object?)baseSnapshotId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@is_overlay", baseSnapshotId.HasValue ? 1 : 0);
        cmd.Parameters.AddWithValue("@delta", (object?)identity.WorkingTreeDelta ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@fallback", (object?)fallbackReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@is_provider", isProvider ? 1 : 0);
        cmd.Parameters.AddWithValue("@capability", (object?)identity.CapabilityFingerprint ?? DBNull.Value);
        return ((long)cmd.ExecuteScalar()!, false, SnapshotStatus.Pending);
    }

    /// <summary>Maps a project version row into a snapshot (idempotent).</summary>
    public void MapProject(long snapshotId, long projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO snapshot_projects (snapshot_id, project_id) VALUES (@snap, @proj)
            ON CONFLICT(snapshot_id, project_id) DO NOTHING;
            """;
        cmd.Parameters.AddWithValue("@snap", snapshotId);
        cmd.Parameters.AddWithValue("@proj", projectId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Atomically publishes a pending snapshot as complete. Guarded on <c>status = pending</c> so a
    /// snapshot abandoned/failed is never resurrected and a duplicate publish is a no-op. Runs on the
    /// caller's connection: invoke it inside the final write transaction so the status flip commits
    /// atomically with the data and the subsequent branch-pointer advance. Returns rows updated (1 on
    /// success, 0 if not pending).
    /// </summary>
    public int MarkComplete(long snapshotId, long publishedAt)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE snapshots SET status = @complete, published_at = @at
             WHERE id = @id AND status = @pending;
            """;
        cmd.Parameters.AddWithValue("@complete", SnapshotStatus.Complete);
        cmd.Parameters.AddWithValue("@at", publishedAt);
        cmd.Parameters.AddWithValue("@id", snapshotId);
        cmd.Parameters.AddWithValue("@pending", SnapshotStatus.Pending);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Guarded pending→partial transition for the finalize completeness gate (issue #70). Like
    /// <see cref="MarkComplete"/> it advances ONLY a still-pending snapshot, so it can never downgrade an
    /// already-published (immutable) complete snapshot. A partial snapshot is diagnosable but is NEVER
    /// selected by a branch pointer, so a materially-incomplete assembly is retained for inspection yet
    /// never served as if it were complete. Returns the number of rows updated (1 = transitioned).
    /// </summary>
    public int MarkPartial(long snapshotId, long publishedAt)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE snapshots SET status = @partial, published_at = @at
             WHERE id = @id AND status = @pending;
            """;
        cmd.Parameters.AddWithValue("@partial", SnapshotStatus.Partial);
        cmd.Parameters.AddWithValue("@at", publishedAt);
        cmd.Parameters.AddWithValue("@id", snapshotId);
        cmd.Parameters.AddWithValue("@pending", SnapshotStatus.Pending);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Sets a snapshot's status (e.g. to partial/failed/unsupported/superseded for diagnosis).</summary>
    public void MarkStatus(long snapshotId, string status)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE snapshots SET status = @status WHERE id = @id;";
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@id", snapshotId);
        cmd.ExecuteNonQuery();
    }

    public SnapshotRow? GetByIdentityHash(string identityHash)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SelectSnapshot + " WHERE identity_hash = @hash;";
        cmd.Parameters.AddWithValue("@hash", identityHash);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadSnapshot(reader) : null;
    }

    public SnapshotRow? GetById(long id)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SelectSnapshot + " WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadSnapshot(reader) : null;
    }

    /// <summary>
    /// Resolves a repository + git head commit SHA to the id of a COMPLETE snapshot indexed at that commit,
    /// or null when no such snapshot exists yet. Used by the Phase-17 open-PR retention root (criterion 4)
    /// to bind a PR head to the snapshot it must protect when the caller supplies only the head commit. When
    /// several complete snapshots share the commit (multiple generations) the most recent is returned, so
    /// the protected root is the freshest complete index for that PR head.
    /// </summary>
    public long? ResolveCompleteSnapshotByCommit(long repositoryId, string commitSha)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT s.id
            FROM snapshots s
            JOIN commits c ON c.id = s.commit_id
            WHERE s.repository_id = @repo AND c.commit_sha = @sha AND s.status = 'complete'
            ORDER BY s.created_at DESC, s.id DESC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@repo", repositoryId);
        cmd.Parameters.AddWithValue("@sha", commitSha);
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    /// <summary>
    /// The git commit SHA for a <c>commits.id</c> (a snapshot's <see cref="SnapshotRow.CommitId"/>), or
    /// null when the id is null or unknown. Used by the Phase-11 federated read planner to stamp the base
    /// commit into a response's provenance metadata (criterion 4) without exposing internal row ids.
    /// </summary>
    public string? GetCommitSha(long? commitId)
    {
        if (!commitId.HasValue) return null;
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT commit_sha FROM commits WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", commitId.Value);
        return cmd.ExecuteScalar() as string;
    }

    // ---- branch pointers -----------------------------------------------------------------------

    /// <summary>
    /// Ensures the requesting branch has its OWN pointer to a snapshot it resolves to on ATTACH, without
    /// perturbing shared state (issue #62). Because snapshot identity excludes the branch name, two
    /// branches at the same commit share ONE snapshot; the first indexes and points at it, but a second
    /// branch that merely ATTACHES (no re-index) would otherwise have no pointer — so it neither resolves
    /// via <c>ResolveBranch</c> nor protects the snapshot from retention. This inserts the branch as
    /// NON-default when absent (<c>ON CONFLICT DO NOTHING</c> preserves any existing <c>is_default</c> and
    /// never demotes the real default) and points it at the snapshot only when it has no pointer yet — it
    /// never supersedes an EXISTING pointer, so a late/duplicate attach for an older snapshot can never roll
    /// a branch (e.g. the default) back off a newer target it already advanced to (branch advancement is the
    /// exclusive job of the publish path, <c>AdvanceBranch</c>). Returns the branch id.
    /// </summary>
    public long AttachBranchPointer(long repositoryId, string branchName, long snapshotId, long now)
    {
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO branches (repository_id, name, snapshot_id, is_default, updated_at)
                VALUES (@repo, @name, NULL, 0, @now)
                ON CONFLICT(repository_id, name) DO NOTHING;
                """;
            insert.Parameters.AddWithValue("@repo", repositoryId);
            insert.Parameters.AddWithValue("@name", branchName);
            insert.Parameters.AddWithValue("@now", now);
            insert.ExecuteNonQuery();
        }

        var branchId = GetBranchId(repositoryId, branchName)!.Value;
        if (GetBranchSnapshotId(branchId) is null)
            SetBranchPointer(branchId, snapshotId, now);
        return branchId;
    }

    /// <summary>
    /// Points a branch at a snapshot (advance or rollback). Mutating only this pointer row leaves every
    /// snapshot's semantic rows byte-identical (criterion 2). Runs on the caller's connection so a full
    /// index can advance the default branch inside the same transaction that publishes the snapshot.
    /// </summary>
    public void SetBranchPointer(long branchId, long? snapshotId, long now)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE branches SET snapshot_id = @snap, updated_at = @now WHERE id = @id;";
        cmd.Parameters.AddWithValue("@snap", (object?)snapshotId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.Parameters.AddWithValue("@id", branchId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>The control-plane head sequence a branch pointer has advanced to (issue #84), or null when
    /// no sequence-bearing ensure has advanced it yet (every branch advanced only by the local path).</summary>
    public long? GetBranchHeadSequence(long branchId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT head_sequence FROM branches WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", branchId);
        return cmd.ExecuteScalar() is long seq ? seq : null;
    }

    /// <summary>Records the control-plane head sequence a branch pointer has advanced to (issue #84).</summary>
    public void SetBranchHeadSequence(long branchId, long sequence)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE branches SET head_sequence = @seq WHERE id = @id;";
        cmd.Parameters.AddWithValue("@seq", sequence);
        cmd.Parameters.AddWithValue("@id", branchId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Forward-only branch-pointer advance for the SERVICE ensure path (issue #84). Encapsulates the whole
    /// gate so it is unit-testable and shared by the orchestrator's <c>AdvanceBranchToSnapshot</c>:
    /// <list type="bullet">
    /// <item><paramref name="headSequence"/> is <c>null</c> (the local CLI/daemon path, and every non-service
    /// caller): the pointer advances UNCONDITIONALLY and the previous target is superseded — byte-identical
    /// to the pre-#84 behavior; <c>head_sequence</c> is never written.</item>
    /// <item><paramref name="headSequence"/> is present and strictly greater than the branch's stored
    /// sequence (or the branch has none yet): the pointer advances to <paramref name="snapshotId"/>, the
    /// previous target is superseded, and the new sequence is stored.</item>
    /// <item><paramref name="headSequence"/> is present and less-than-or-equal to the stored sequence: an
    /// out-of-order/older ensure — the pointer and the previous target are left UNTOUCHED (no regression),
    /// and the immutable snapshot is ensured/attached elsewhere. Returns <c>false</c>.</item>
    /// </list>
    /// Runs on the caller's connection so a full index advances the branch inside the same transaction that
    /// publishes the snapshot. Returns <c>true</c> when the pointer advanced.
    /// </summary>
    public bool AdvanceBranchPointerForwardOnly(long branchId, long snapshotId, long? headSequence, long now)
    {
        if (headSequence is long seq)
        {
            if (GetBranchHeadSequence(branchId) is long stored && seq <= stored)
                return false;
            SetBranchHeadSequence(branchId, seq);
        }

        var previousSnapshot = GetBranchSnapshotId(branchId);
        SetBranchPointer(branchId, snapshotId, now);
        if (previousSnapshot is long prev && prev != snapshotId)
            MarkStatus(prev, SnapshotStatus.Superseded);
        return true;
    }

    /// <summary>
    /// Makes <paramref name="keepBranchId"/> the single default branch for its repository by clearing
    /// <c>is_default</c> on every sibling. A full index marks the checked-out branch it just published as
    /// default; without demoting siblings, indexing a second branch of one repo would leave two
    /// <c>is_default = 1</c> rows and <see cref="GetSelectedSnapshotId"/> would resolve the OLDER branch
    /// (its <c>ORDER BY</c> tiebreak), silently serving the wrong generation for a scope-less query. Run
    /// inside the publish transaction so the single-default invariant flips atomically with the pointer.
    /// </summary>
    public void PromoteSoleDefaultBranch(long repositoryId, long keepBranchId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE branches SET is_default = 0 WHERE repository_id = @repo AND id <> @keep AND is_default = 1;";
        cmd.Parameters.AddWithValue("@repo", repositoryId);
        cmd.Parameters.AddWithValue("@keep", keepBranchId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// The set of logical project canonical ids present in an assembled/native snapshot (issue #70 finalize
    /// gate). COALESCEs the logical-project canonical id with the physical project canonical id — the same
    /// identity the importer/validator map under — so it can be compared to a manifest's declared graph.
    /// </summary>
    public HashSet<string> GetSnapshotLogicalCanonicalIds(long snapshotId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT COALESCE(lp.canonical_id, p.canonical_id)
            FROM snapshot_projects sp
            JOIN projects p ON p.id = sp.project_id
            LEFT JOIN logical_projects lp ON lp.id = p.logical_project_id
            WHERE sp.snapshot_id = @s;
            """;
        cmd.Parameters.AddWithValue("@s", snapshotId);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            if (!reader.IsDBNull(0)) ids.Add(reader.GetString(0));
        return ids;
    }

    /// <summary>
    /// Every logical project canonical id the catalog has ever recorded for a repository (issue #70). Used
    /// by the finalize gate to classify a referenced project-version key as intra-repo (must be assembled)
    /// vs a cross-repo provider reference (resolved via snapshot_dependencies; #72 deferred).
    /// </summary>
    public HashSet<string> GetKnownLogicalCanonicalIds(long repositoryId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT canonical_id FROM logical_projects WHERE repository_id = @repo;";
        cmd.Parameters.AddWithValue("@repo", repositoryId);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            if (!reader.IsDBNull(0)) ids.Add(reader.GetString(0));
        return ids;
    }

    public long? GetDefaultBranchId(long repositoryId)
    {        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id FROM branches WHERE repository_id = @repo AND is_default = 1 ORDER BY id LIMIT 1;";
        cmd.Parameters.AddWithValue("@repo", repositoryId);
        return cmd.ExecuteScalar() is long id ? id : null;
    }

    /// <summary>The repository id for a remote url, or null when it has never been indexed (read-only lookup).</summary>
    public long? GetRepositoryId(string remoteUrl)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id FROM repositories WHERE remote_url = @url LIMIT 1;";
        cmd.Parameters.AddWithValue("@url", remoteUrl);
        return cmd.ExecuteScalar() is long id ? id : null;
    }

    /// <summary>The branch id for a (repository, name) pair, or null when absent (read-only lookup).</summary>
    public long? GetBranchId(long repositoryId, string name)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id FROM branches WHERE repository_id = @repo AND name = @name LIMIT 1;";
        cmd.Parameters.AddWithValue("@repo", repositoryId);
        cmd.Parameters.AddWithValue("@name", name);
        return cmd.ExecuteScalar() is long id ? id : null;
    }

    /// <summary>The snapshot a branch currently points at (null if the branch has no pointer).</summary>
    public long? GetBranchSnapshotId(long branchId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT snapshot_id FROM branches WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", branchId);
        return cmd.ExecuteScalar() is long id ? id : null;
    }

    /// <summary>
    /// The single selected current snapshot for a scope-less local query (criterion 6). Resolves the
    /// default branch's pointer only when exactly one NON-PROVIDER (consumer) repository is present (the
    /// single-repo local default; submodule providers added by Phase 12 are excluded from the count so a
    /// parent repo that pulls in providers still resolves its own default branch); returns null for a
    /// legacy database (no repositories), a multi-consumer database (Phase 11 supplies explicit scope),
    /// or when the default branch has no complete snapshot yet. A null result means "no snapshot filter"
    /// — the store reads behave exactly as before Phase 9.
    /// </summary>
    public long? GetSelectedSnapshotId()
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT b.snapshot_id
            FROM branches b
            JOIN snapshots s ON s.id = b.snapshot_id
            WHERE b.is_default = 1 AND s.status = @complete
              AND (SELECT COUNT(*) FROM repositories WHERE is_provider = 0) = 1
            ORDER BY b.updated_at DESC, b.id DESC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@complete", SnapshotStatus.Complete);
        return cmd.ExecuteScalar() is long id ? id : null;
    }

    /// <summary>
    /// The full <see cref="SnapshotRow"/> for the current selected snapshot (<see
    /// cref="GetSelectedSnapshotId"/>), or null when none is selected (legacy/multi-repo/pre-first-publish
    /// DB). The Phase-11 federated read planner resolves this ONCE per MCP request so the base snapshot,
    /// overlay generation, completeness status, and schema/analyzer/toolchain fingerprint used for the
    /// read-time compatibility gate all come from a single pinned generation (issue #42), and every
    /// sub-query in the request reuses it.
    /// </summary>
    public SnapshotRow? GetSelectedSnapshotRow()
    {
        var id = GetSelectedSnapshotId();
        return id.HasValue ? GetById(id.Value) : null;
    }

    /// <summary>
    /// The selected current snapshot for a NAMED consumer repository (Phase 17, criterion 1 — the
    /// multi-tenant request-level selector). Resolves that repository's default-branch complete snapshot
    /// regardless of how many repositories the catalog holds, so an enforced multi-tenant service is
    /// queryable-and-scoped (the caller names its authorized repository via the request) instead of
    /// deny-all — unlike <see cref="GetSelectedSnapshotId"/>, which resolves ONLY when exactly one
    /// consumer repository exists. Providers are excluded (<c>is_provider = 0</c>). Returns null when the
    /// URL is unknown, has no complete default-branch snapshot yet, or is a provider — the enforced read
    /// gate then denies (a null selection is unauthorized), so an unknown/unauthorized repository and a
    /// genuinely-absent one collapse to the same uniform not-found (no existence oracle).
    /// </summary>
    public long? GetSelectedSnapshotIdForRepository(string remoteUrl)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT b.snapshot_id
            FROM branches b
            JOIN snapshots s ON s.id = b.snapshot_id
            JOIN repositories r ON r.id = b.repository_id
            WHERE b.is_default = 1 AND s.status = @complete
              AND r.is_provider = 0 AND r.remote_url = @url
            ORDER BY b.updated_at DESC, b.id DESC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@complete", SnapshotStatus.Complete);
        cmd.Parameters.AddWithValue("@url", remoteUrl);
        return cmd.ExecuteScalar() is long id ? id : null;
    }

    /// <summary>
    /// The full <see cref="SnapshotRow"/> for a named repository's selected snapshot
    /// (<see cref="GetSelectedSnapshotIdForRepository"/>), or null when none resolves.
    /// </summary>
    public SnapshotRow? GetSelectedSnapshotRowForRepository(string remoteUrl)
    {
        var id = GetSelectedSnapshotIdForRepository(remoteUrl);
        return id.HasValue ? GetById(id.Value) : null;
    }

    /// <summary>
    /// True for a single-repository database that carries snapshot-tagged project rows
    /// (<c>projects.snapshot_id IS NOT NULL</c>) — i.e. a Phase-9 build has run or is in flight. When this
    /// holds but <see cref="GetSelectedSnapshotId"/> returns null (no complete snapshot selected yet),
    /// <see cref="SnapshotReadScope.ForSelected"/> pins reads to the legacy rows so a concurrent reader
    /// stays on the previous complete generation and never observes the half-built pending snapshot rows
    /// (criterion 7, across the first/in-progress upgrade rebuild). A pure legacy database (no snapshot
    /// rows) returns false and reads stay unscoped, byte-for-byte as before Phase 9. Restricted to a
    /// single repository so multi-repo databases (Phase 11) fall through to explicit scope, never a pin.
    /// Restricted to a single NON-PROVIDER repository so Phase-12 submodule providers do not defeat the pin.
    /// </summary>
    public bool HasUnselectedSnapshotProjectRows()
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT (SELECT COUNT(*) FROM repositories WHERE is_provider = 0) = 1
               AND EXISTS (SELECT 1 FROM projects WHERE snapshot_id IS NOT NULL);
            """;
        return cmd.ExecuteScalar() is long flag && flag == 1;
    }

    public List<long> GetSnapshotProjectIds(long snapshotId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT project_id FROM snapshot_projects WHERE snapshot_id = @snap;";
        cmd.Parameters.AddWithValue("@snap", snapshotId);
        var ids = new List<long>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        return ids;
    }

    // ---- retention support ---------------------------------------------------------------------

    /// <summary>
    /// Every snapshot currently referenced by a branch pointer, with its run and commit. Feeds the
    /// Phase-9 branch-pointer retention protection so a branch-pointed generation is never GC'd.
    /// </summary>
    public IReadOnlyList<(long snapshotId, long? runId, long? commitId)> GetBranchPointedSnapshots()
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT s.id, s.run_id, s.commit_id
            FROM branches b JOIN snapshots s ON s.id = b.snapshot_id
            WHERE b.snapshot_id IS NOT NULL;
            """;
        var rows = new List<(long, long?, long?)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add((
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2)));
        return rows;
    }

    /// <summary>
    /// The (run id, commit SHA) targets a branch-pointer retention protection must spare: for every
    /// branch-pointed snapshot, its generation (<c>run_id</c>) and the git commit whose API history
    /// (<c>api_surface_snapshots.git_commit</c>) belongs to it. A branch head is thus never GC'd
    /// (criterion: branch-pointer protection) — the default/current-selected branch included.
    /// </summary>
    public IReadOnlyList<(long? runId, string? commitSha)> GetBranchProtectionTargets()
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT s.run_id, c.commit_sha
            FROM branches b
            JOIN snapshots s ON s.id = b.snapshot_id
            LEFT JOIN commits c ON c.id = s.commit_id
            WHERE b.snapshot_id IS NOT NULL;
            """;
        var rows = new List<(long?, string?)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add((
                reader.IsDBNull(0) ? null : reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetString(1)));
        return rows;
    }

    // ---- Phase-10 overlay support --------------------------------------------------------------

    /// <summary>
    /// The (base run id, base commit SHA) targets a Phase-10 overlay-base retention protection must
    /// spare: for every branch-pointed OVERLAY, its committed base snapshot's generation
    /// (<c>run_id</c>) and commit. A live overlay SHARES its base snapshot's unchanged project-version
    /// rows (they are mapped into the overlay via <c>snapshot_projects</c> but physically belong to the
    /// base's generation), so GC'ing the base generation would delete rows the selected overlay still
    /// reads. This keeps the base pinned for as long as any branch points at an overlay layered on it.
    /// </summary>
    public IReadOnlyList<(long? runId, string? commitSha)> GetOverlayBaseProtectionTargets()
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT base.run_id, c.commit_sha
            FROM branches b
            JOIN snapshots o ON o.id = b.snapshot_id AND o.is_overlay = 1 AND o.base_snapshot_id IS NOT NULL
            JOIN snapshots base ON base.id = o.base_snapshot_id
            LEFT JOIN commits c ON c.id = base.commit_id
            WHERE b.snapshot_id IS NOT NULL;
            """;
        var rows = new List<(long?, string?)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add((
                reader.IsDBNull(0) ? null : reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetString(1)));
        return rows;
    }

    /// <summary>
    /// The (run id, commit SHA) targets an open-pull-request retention protection must spare (Phase 17,
    /// criterion 4): for every snapshot an OPEN pull request root points at, its generation
    /// (<c>run_id</c>) and the git commit whose API history belongs to it. This keeps a PR-head snapshot
    /// alive for as long as the PR is open even when its generation has fallen out of the keep window and
    /// its head commit is not itself a branch pointer. Contributes nothing when the migration-019
    /// <c>pull_request_snapshots</c> table is absent (pre-migration DB).
    /// </summary>
    public IReadOnlyList<(long? runId, string? commitSha)> GetOpenPullRequestProtectionTargets()
    {
        if (!TableExists("pull_request_snapshots")) return [];

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT s.run_id, c.commit_sha
            FROM pull_request_snapshots pr
            JOIN snapshots s ON s.id = pr.snapshot_id
            LEFT JOIN commits c ON c.id = s.commit_id
            WHERE pr.state = 'open' AND pr.snapshot_id IS NOT NULL;
            """;
        var rows = new List<(long?, string?)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add((
                reader.IsDBNull(0) ? null : reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetString(1)));
        return rows;
    }

    /// <summary>
    /// The snapshot ids currently pinned by an OPEN pull request (never data-GC eligible). Feeds the
    /// retained-snapshot closure in retention/quota GC alongside branch-pointed heads. Empty when the
    /// migration-019 <c>pull_request_snapshots</c> table is absent.
    /// </summary>
    public IReadOnlyList<long> GetOpenPullRequestSnapshotIds()
    {
        if (!TableExists("pull_request_snapshots")) return [];

        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT DISTINCT snapshot_id FROM pull_request_snapshots WHERE state = 'open' AND snapshot_id IS NOT NULL;";
        var ids = new List<long>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        return ids;
    }

    private bool TableExists(string table)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name LIMIT 1;";
        cmd.Parameters.AddWithValue("@name", table);
        return cmd.ExecuteScalar() is not null;
    }

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>
    /// Retention targets for Phase-12 submodule PROVIDER snapshots (criterion 6). A consumer snapshot
    /// that is currently branch-pointed SHARES a provider's deduplicated project-version rows through a
    /// <c>snapshot_dependencies</c> edge; those rows physically belong to the provider snapshot's
    /// generation, so GC'ing the provider generation would delete rows a live parent still reads (and
    /// would let updating one parent's pin mutate another parent's usable data). Returns the provider
    /// generation (<c>run_id</c>) and provider commit for every edge whose consumer snapshot is
    /// branch-pointed, so a provider survives as long as any live parent pins it.
    /// </summary>
    public IReadOnlyList<(long? runId, string? commitSha)> GetSubmoduleProviderProtectionTargets()
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT provider.run_id, c.commit_sha
            FROM snapshot_dependencies d
            JOIN branches b ON b.snapshot_id = d.consumer_snapshot_id
            JOIN snapshots provider ON provider.id = d.provider_snapshot_id
            LEFT JOIN commits c ON c.id = provider.commit_id
            WHERE b.snapshot_id IS NOT NULL;
            """;
        var rows = new List<(long?, string?)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add((
                reader.IsDBNull(0) ? null : reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetString(1)));
        return rows;
    }

    /// <summary>
    /// Every snapshot row a retention pass needs to classify data ownership: its id, owning repository,
    /// owning generation (<c>run_id</c>), status, base snapshot (overlay sharing), provider flag, and
    /// creation timestamp (recency for the per-repository quota). Used to compute the retained-snapshot
    /// closure so orphaned snapshot DATA can be GC'd (issue #46) while providers shared by any retained
    /// consumer are spared (issue #54) and the per-repository quota evicts oldest-first (Phase 17).
    /// </summary>
    public IReadOnlyList<(long id, long repositoryId, long? runId, string status, long? baseSnapshotId, bool isProvider, long createdAt)> GetSnapshotsForRetention()
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT id, repository_id, run_id, status, base_snapshot_id, is_provider, created_at FROM snapshots;";
        var rows = new List<(long, long, long?, string, long?, bool, long)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add((
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                !reader.IsDBNull(5) && reader.GetInt64(5) != 0,
                reader.GetInt64(6)));
        return rows;
    }

    /// <summary>
    /// Every submodule-dedup edge (consumer snapshot -&gt; provider snapshot). Feeds the retained-snapshot
    /// closure: a provider whose consumer is retained must itself be retained (issue #54). Empty when the
    /// Phase-12 <c>snapshot_dependencies</c> table is absent (pre-migration DB).
    /// </summary>
    public IReadOnlyList<(long consumerSnapshotId, long providerSnapshotId)> GetSnapshotDependencyEdges()
    {
        using (var check = connection.CreateCommand())
        {
            check.CommandText =
                "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'snapshot_dependencies' LIMIT 1;";
            if (check.ExecuteScalar() is null) return [];
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT consumer_snapshot_id, provider_snapshot_id FROM snapshot_dependencies;";
        var rows = new List<(long, long)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add((reader.GetInt64(0), reader.GetInt64(1)));
        return rows;
    }

    /// <summary>The snapshot ids currently referenced by a branch pointer (never data-GC eligible).</summary>
    public IReadOnlyList<long> GetBranchPointedSnapshotIds()
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT DISTINCT snapshot_id FROM branches WHERE snapshot_id IS NOT NULL;";
        var ids = new List<long>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        return ids;
    }

    private const string SelectSnapshot = """
        SELECT id, repository_id, commit_id, run_id, identity_hash, tree_sha, schema_version,
               analyzer_version, config_hash, toolchain_fingerprint, status, created_at, published_at,
               base_snapshot_id, is_overlay, working_tree_delta, fallback_reason, is_provider,
               capability_fingerprint
        FROM snapshots
        """;

    private static SnapshotRow ReadSnapshot(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        RepositoryId = reader.GetInt64(1),
        CommitId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
        RunId = reader.IsDBNull(3) ? null : reader.GetInt64(3),
        IdentityHash = reader.GetString(4),
        TreeSha = reader.IsDBNull(5) ? null : reader.GetString(5),
        SchemaVersion = reader.GetInt32(6),
        AnalyzerVersion = reader.GetString(7),
        ConfigHash = reader.IsDBNull(8) ? null : reader.GetString(8),
        ToolchainFingerprint = reader.IsDBNull(9) ? null : reader.GetString(9),
        Status = reader.GetString(10),
        CreatedAt = reader.GetInt64(11),
        PublishedAt = reader.IsDBNull(12) ? null : reader.GetInt64(12),
        BaseSnapshotId = reader.IsDBNull(13) ? null : reader.GetInt64(13),
        IsOverlay = !reader.IsDBNull(14) && reader.GetInt64(14) != 0,
        WorkingTreeDelta = reader.IsDBNull(15) ? null : reader.GetString(15),
        FallbackReason = reader.IsDBNull(16) ? null : reader.GetString(16),
        IsProvider = !reader.IsDBNull(17) && reader.GetInt64(17) != 0,
        CapabilityFingerprint = reader.IsDBNull(18) ? null : reader.GetString(18)
    };
}
