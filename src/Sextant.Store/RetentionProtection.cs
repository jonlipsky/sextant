using Microsoft.Data.Sqlite;

namespace Sextant.Store;

/// <summary>
/// The accumulated set of index data a retention pass must never delete (Phase 8, criteria 4 &amp; 5).
/// A protection has three independent axes so a future referencing source can pin exactly what it
/// needs: whole generations (<c>index_runs</c> rows), API-history commits (<c>api_surface_snapshots</c>
/// rows for a git commit), and individual source blobs (<c>file_versions</c> rows). Each protected id
/// carries a human-readable reason so a dry-run/execution report can explain why an item was spared.
/// </summary>
public sealed class RetentionProtectionBuilder
{
    private readonly Dictionary<long, string> _generations = new();
    private readonly Dictionary<string, string> _commits = new(StringComparer.Ordinal);
    private readonly Dictionary<long, string> _fileVersions = new();

    /// <summary>Protects a complete/staging generation (an <c>index_runs</c> id) from deletion.</summary>
    public void ProtectGeneration(long runId, string reason) => Add(_generations, runId, reason);

    /// <summary>Protects every <c>api_surface_snapshots</c> row for a git commit from deletion.</summary>
    public void ProtectCommit(string gitCommit, string reason)
    {
        if (string.IsNullOrWhiteSpace(gitCommit)) return;
        if (!_commits.ContainsKey(gitCommit)) _commits[gitCommit] = reason;
    }

    /// <summary>Protects a specific source blob (a <c>file_versions</c> id) from pruning.</summary>
    public void ProtectFileVersion(long fileVersionId, string reason) => Add(_fileVersions, fileVersionId, reason);

    private static void Add(Dictionary<long, string> map, long id, string reason)
    {
        if (!map.ContainsKey(id)) map[id] = reason;
    }

    /// <summary>Freezes the accumulated protections into an immutable set.</summary>
    public RetentionProtectionSet Build() => new(
        new Dictionary<long, string>(_generations),
        new Dictionary<string, string>(_commits, StringComparer.Ordinal),
        new Dictionary<long, string>(_fileVersions));
}

/// <summary>An immutable snapshot of everything a retention pass must protect, with per-item reasons.</summary>
public sealed class RetentionProtectionSet(
    IReadOnlyDictionary<long, string> generations,
    IReadOnlyDictionary<string, string> commits,
    IReadOnlyDictionary<long, string> fileVersions)
{
    public IReadOnlyDictionary<long, string> Generations { get; } = generations;
    public IReadOnlyDictionary<string, string> Commits { get; } = commits;
    public IReadOnlyDictionary<long, string> FileVersions { get; } = fileVersions;

    public bool IsGenerationProtected(long runId) => Generations.ContainsKey(runId);
    public bool IsCommitProtected(string gitCommit) => Commits.ContainsKey(gitCommit);
    public bool IsFileVersionProtected(long fileVersionId) => FileVersions.ContainsKey(fileVersionId);
}

/// <summary>
/// Contributes protected index data to a retention pass. Providers are the extension seam that keeps
/// retention forward-compatible: Phase 9 adds branch-snapshot and pull-request-snapshot providers,
/// Phase 10/12 add overlay/submodule-pin providers, each contributing the generations/commits/blobs
/// its referencing rows depend on — all without changing <see cref="RetentionService"/>. A provider
/// contributing nothing (because its backing table does not exist yet, or its referencing set is
/// empty) is the correct no-op today.
/// </summary>
public interface IRetentionProtectionProvider
{
    /// <summary>A short name for the protection source, surfaced in the retention report.</summary>
    string Name { get; }

    /// <summary>Adds this source's protected generations/commits/blobs to <paramref name="builder"/>.</summary>
    void Contribute(SqliteConnection connection, RetentionProtectionBuilder builder);
}

/// <summary>
/// Protects the currently-servable last-complete generation (<see cref="IndexRunStore.GetLastCompleteRun"/>).
/// This is the one protection that must always be present: deleting the servable generation would
/// leave <c>GetLastCompleteRun</c> pointing at nothing and trip the Phase-7 rebuild gate. The service
/// also enforces this as a hard, provider-independent guard, so this provider is defense in depth plus
/// the source of the human-readable "servable generation" reason in the report.
/// </summary>
public sealed class LastCompleteRunProtection : IRetentionProtectionProvider
{
    public string Name => "last-complete-generation";

    public void Contribute(SqliteConnection connection, RetentionProtectionBuilder builder)
    {
        var servable = new IndexRunStore(connection).GetLastCompleteRun();
        if (servable != null)
            builder.ProtectGeneration(servable.Id, "currently-servable last-complete generation");
    }
}

/// <summary>
/// Protects every snapshot referenced by a branch pointer (Phase 9). A branch name is a mutable pointer
/// to an immutable snapshot; the default/current-selected branch head — and any other branch pointer —
/// must survive a retention pass even when its generation falls outside the keep window. For each
/// branch-pointed snapshot this pins its generation (<c>run_id</c>) so the ledger row is never GC'd and
/// its git commit so the commit's API history (<see cref="RetentionService"/> trims by commit) is
/// preserved (criterion 5 under the snapshot model). The default protected set is thus "default branch +
/// any live branch pointer"; PR-head, submodule-pin, and overlay providers are left to Phases 10/12/14.
/// Forward/backward-safe: if the Phase-9 <c>branches</c> table is not present (pre-migration DB) the
/// provider contributes nothing.
/// </summary>
public sealed class BranchPointerProtection : IRetentionProtectionProvider
{
    public string Name => "branch-pointer";

    public void Contribute(SqliteConnection connection, RetentionProtectionBuilder builder)
    {
        if (!TableExists(connection, "branches")) return;

        foreach (var (runId, commitSha) in new SnapshotStore(connection).GetBranchProtectionTargets())
        {
            if (runId.HasValue)
                builder.ProtectGeneration(runId.Value, "generation referenced by a branch pointer");
            if (!string.IsNullOrEmpty(commitSha))
                builder.ProtectCommit(commitSha!, "API history for a branch-pointed commit");
        }
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name LIMIT 1;";
        cmd.Parameters.AddWithValue("@name", table);
        return cmd.ExecuteScalar() is not null;
    }
}

/// <summary>
/// Protects the committed BASE snapshot of every branch-pointed overlay (Phase 10, issue #44). A live
/// overlay SHARES its base snapshot's unchanged project-version rows — they are mapped into the overlay
/// via <c>snapshot_projects</c> but physically belong to the base's generation — so GC'ing the base
/// generation would delete rows the selected overlay still reads. For each branch-pointed overlay this
/// pins the base's generation (<c>run_id</c>) and the base commit's API history, keeping the base alive
/// for as long as any branch points at an overlay layered on it. Forward/backward-safe: contributes
/// nothing when the <c>snapshots</c> table lacks the Phase-10 overlay columns (pre-migration DB).
/// </summary>
public sealed class OverlayBaseProtection : IRetentionProtectionProvider
{
    public string Name => "overlay-base";

    public void Contribute(SqliteConnection connection, RetentionProtectionBuilder builder)
    {
        if (!TableExists(connection, "snapshots") || !ColumnExists(connection, "snapshots", "is_overlay"))
            return;

        foreach (var (runId, commitSha) in new SnapshotStore(connection).GetOverlayBaseProtectionTargets())
        {
            if (runId.HasValue)
                builder.ProtectGeneration(runId.Value, "base generation shared by a branch-pointed overlay");
            if (!string.IsNullOrEmpty(commitSha))
                builder.ProtectCommit(commitSha!, "API history for an overlay's base commit");
        }
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name LIMIT 1;";
        cmd.Parameters.AddWithValue("@name", table);
        return cmd.ExecuteScalar() is not null;
    }

    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name = @col LIMIT 1;";
        cmd.Parameters.AddWithValue("@col", column);
        return cmd.ExecuteScalar() is not null;
    }
}

/// <summary>
/// Protects the submodule PROVIDER snapshot shared by every branch-pointed consumer (Phase 12, criterion
/// 6). A parent snapshot references a deduplicated provider project version through a
/// <c>snapshot_dependencies</c> edge rather than copying the provider's semantic rows, so those rows
/// belong to the provider's generation. GC'ing the provider generation would delete rows a live parent
/// still reads — and would let updating one parent's pin destroy another parent's usable data. For every
/// edge whose consumer snapshot is branch-pointed this pins the provider's generation (<c>run_id</c>) and
/// the provider commit's API history. Forward/backward-safe: contributes nothing when the Phase-12
/// <c>snapshot_dependencies</c> table is absent (pre-migration DB).
/// </summary>
public sealed class SubmoduleProviderProtection : IRetentionProtectionProvider
{
    public string Name => "submodule-provider";

    public void Contribute(SqliteConnection connection, RetentionProtectionBuilder builder)
    {
        if (!TableExists(connection, "snapshot_dependencies")) return;

        foreach (var (runId, commitSha) in new SnapshotStore(connection).GetSubmoduleProviderProtectionTargets())
        {
            if (runId.HasValue)
                builder.ProtectGeneration(runId.Value, "provider generation shared by a branch-pointed consumer");
            if (!string.IsNullOrEmpty(commitSha))
                builder.ProtectCommit(commitSha!, "API history for a submodule provider's commit");
        }
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name LIMIT 1;";
        cmd.Parameters.AddWithValue("@name", table);
        return cmd.ExecuteScalar() is not null;
    }
}
