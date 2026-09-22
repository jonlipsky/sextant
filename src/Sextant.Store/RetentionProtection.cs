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
