using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Core.Platform;
using Sextant.Indexer;
using Sextant.Store;

namespace Sextant.Service.Contributions;

/// <summary>
/// Describes one client/CI contribution to capture: the solution to index, the exact committed coordinates
/// it was produced at, and the producing node's capability. The commit/tree/dirty facts are resolved by the
/// caller (the CLI reads them from git), so capture itself is git-agnostic and testable.
/// </summary>
public sealed record ContributionCaptureRequest
{
    public required string SolutionPath { get; init; }
    public required string RepositoryRemoteUrl { get; init; }
    public required string CommitSha { get; init; }
    public string? TreeSha { get; init; }
    public string? BranchName { get; init; }
    public bool IsDefaultBranch { get; init; }
    public bool WorkingTreeDirty { get; init; }
    public required ContributionProvenance Provenance { get; init; }

    /// <summary>The capability the producing node builds under (folded into the payload snapshot identity).</summary>
    public WorkerCapability Capability { get; init; } = WorkerCapability.LocalDefault;
}

/// <summary>
/// The CLIENT capture path (Phase 16): runs the in-process Roslyn indexer over an already-built checkout to
/// produce a self-contained payload catalog with ONE complete snapshot, then builds the deterministic
/// contribution manifest and packs a content-addressed <see cref="ContributionArtifact"/>. This is the
/// environment that already HAS the required SDKs/workloads doing the semantic indexing at (or after)
/// compile time — directly answering "can client machines do some of the indexing?".
///
/// The payload snapshot is stamped with the node's capability fingerprint (via the snapshot context), so
/// the server's "declared == built" check passes and a Windows/macOS-produced contribution assembles ONLY
/// with compatible inputs (acceptance criterion 4). The whole artifact is content-addressed, so re-running
/// capture on the same clean commit yields an idempotent, no-op re-upload (criterion 2).
///
/// It requires a real MSBuild/Roslyn toolchain, so it is exercised by an env-gated smoke test; the manifest
/// derivation and artifact packing it delegates to are covered hermetically.
/// </summary>
public static class ContributionCapture
{
    public static async Task<ContributionArtifact> CaptureAsync(
        ContributionCaptureRequest request,
        SextantConfiguration configuration,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(configuration);
        if (!File.Exists(request.SolutionPath))
            throw new FileNotFoundException($"solution not found: {request.SolutionPath}", request.SolutionPath);

        var scratchDir = Path.Combine(Path.GetTempPath(), $"sextant_capture_{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratchDir);
        var payloadDbPath = Path.Combine(scratchDir, "payload.db");

        try
        {
            var context = new SnapshotContext
            {
                RepositoryRemoteUrl = request.RepositoryRemoteUrl,
                CommitSha = request.CommitSha,
                TreeSha = request.TreeSha,
                BranchName = request.BranchName ?? "main",
                IsDefaultBranch = request.IsDefaultBranch,
                // Stamp the producing node's capability so the payload snapshot records what it was built
                // under (the server verifies declared == built before assembling — criterion 4).
                CapabilityFingerprint = request.Capability.Fingerprint
            };

            string identityHash;
            using (var db = new IndexDatabase(payloadDbPath, IndexWriteOptions.FromConfiguration(configuration)))
            {
                db.RunMigrations();
                var solution = await SolutionLoader.LoadSolutionAsync(request.SolutionPath).ConfigureAwait(false);
                var orchestrator = new IndexOrchestrator(
                    db, log, configuration.DocumentExtractor,
                    ExtractionParallelismOptions.FromConfiguration(configuration),
                    IndexProfileDescriptor.FromConfiguration(configuration));

                await orchestrator.IndexSolutionAsync(
                    solution, progress: null, metrics: null, cancellationToken: cancellationToken,
                    snapshotContext: context).ConfigureAwait(false);

                identityHash = ResolvePublishedIdentity(db.GetConnection(), context)
                    ?? throw new InvalidOperationException(
                        "indexing produced no complete snapshot for the requested commit; the checkout's " +
                        "committed state may differ from the declared commit.");
            }

            // The IndexDatabase is disposed (WAL checkpoint-truncated) before we open the payload afresh, so
            // the manifest is built from the self-contained committed catalog file.
            var manifest = BuildManifest(payloadDbPath, identityHash, request);
            var payloadBytes = await File.ReadAllBytesAsync(payloadDbPath, cancellationToken).ConfigureAwait(false);
            return ContributionArtifact.Create(manifest, payloadBytes);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDeleteDirectory(scratchDir);
        }
    }

    private static ContributionManifest BuildManifest(
        string payloadDbPath, string identityHash, ContributionCaptureRequest request)
    {
        using var conn = OpenReadOnly(payloadDbPath);
        var provenance = request.Provenance with { WorkingTreeDirty = request.WorkingTreeDirty };
        return ContributionManifestBuilder.Build(
            conn, identityHash, provenance, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    // Resolves the identity hash the orchestrator published under. It is the capability-ful snapshot the
    // context targeted; a completed snapshot carrying that exact capability + commit is the produced one.
    private static string? ResolvePublishedIdentity(SqliteConnection conn, SnapshotContext context)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.identity_hash
            FROM snapshots s
            JOIN commits c ON c.id = s.commit_id
            WHERE s.status = 'complete' AND c.commit_sha = @commit
            ORDER BY s.id DESC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("@commit", context.CommitSha);
        return cmd.ExecuteScalar() as string;
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        conn.Open();
        return conn;
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Best-effort scratch cleanup; a leftover temp dir is harmless.
        }
    }
}
