using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Service.Tests;

/// <summary>
/// Shared hermetic fixtures for the standalone-service tests: ephemeral SQLite catalogs, a configurable
/// in-process <see cref="ISnapshotWorker"/> that publishes real snapshots through the catalog (so a
/// complete snapshot is genuinely queryable), and option builders that keep worker scratch on a path
/// SEPARATE from the persistent volumes. No real git, no MSBuild, no network.
/// </summary>
internal static class ServiceTestFixtures
{
    public static string NewDbPath() =>
        Path.Combine(Path.GetTempPath(), $"sextant_svc_{Guid.NewGuid():N}.db");

    public static string NewDataRoot() =>
        Path.Combine(Path.GetTempPath(), $"sextant_svcdata_{Guid.NewGuid():N}");

    public static ServiceOptions NewOptions(
        string dbPath,
        string? dataRoot = null,
        string? controlToken = null,
        string? queryToken = null,
        RetentionPolicy? retention = null)
    {
        dataRoot ??= NewDataRoot();
        return new ServiceOptions
        {
            CatalogDbPath = dbPath,
            Volumes = ServiceVolumes.Rooted(dataRoot),
            ControlToken = controlToken,
            QueryToken = queryToken,
            Retention = retention ?? new RetentionPolicy(),
            LeaseTtl = TimeSpan.FromSeconds(30)
        };
    }

    public static EnsureSnapshotRequest Request(
        string repo = "https://github.com/org/app",
        string commit = "commit-aaaa",
        string? branch = null) => new()
        {
            RepositoryRemoteUrl = repo,
            CommitSha = commit,
            BranchName = branch
        };

    /// <summary>
    /// Publishes a genuine complete snapshot (repository + complete run + snapshot + one project mapped via
    /// <c>snapshot_projects</c> + <paramref name="symbolCount"/> symbols) for the request's identity, using
    /// the caller's writer connection. Mirrors what a real worker's orchestrator run leaves behind, so the
    /// snapshot is queryable through <see cref="LocalBaseSnapshotSource"/> and the HTTP query plane.
    /// </summary>
    public static long PublishComplete(IndexDatabase db, EnsureSnapshotRequest request, int symbolCount = 3)
    {
        var conn = db.GetConnection();
        var snapshots = new SnapshotStore(conn);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var identity = request.ToIdentity();

        var existing = snapshots.GetByIdentityHash(identity.Hash);
        if (existing is { Status: SnapshotStatus.Complete }) return existing.Id;

        var repoId = snapshots.EnsureRepository(request.RepositoryRemoteUrl, now);
        var runStore = new IndexRunStore(conn);
        var runId = runStore.BeginRun("full", now,
            IndexProfileDescriptor.Full.ConfigurationHash, IndexProfiles.Deep, (long)IndexFeature.Deep);
        runStore.MarkComplete(runId, now, 1);

        var (snapId, _, _) = snapshots.BeginPending(identity, repoId, null, runId, now);

        long projectId;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO projects (canonical_id, git_remote_url, repo_relative_path, last_indexed_at, snapshot_id)
                VALUES (@c, @g, @p, @now, @snap) RETURNING id;
                """;
            cmd.Parameters.AddWithValue("@c", $"proj_{snapId}");
            cmd.Parameters.AddWithValue("@g", request.RepositoryRemoteUrl);
            cmd.Parameters.AddWithValue("@p", "src/App/App.csproj");
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@snap", snapId);
            projectId = (long)cmd.ExecuteScalar()!;
        }
        snapshots.MapProject(snapId, projectId);

        long fileId;
        using (var f = conn.CreateCommand())
        {
            f.CommandText = "INSERT INTO files (project_id, repo_relative_path) VALUES (@p, @path) RETURNING id;";
            f.Parameters.AddWithValue("@p", projectId);
            f.Parameters.AddWithValue("@path", "src/App/App.cs");
            fileId = (long)f.ExecuteScalar()!;
        }

        long fvId;
        using (var fv = conn.CreateCommand())
        {
            fv.CommandText =
                "INSERT INTO file_versions (file_id, content_hash, last_indexed_at) VALUES (@f, @h, @now) RETURNING id;";
            fv.Parameters.AddWithValue("@f", fileId);
            fv.Parameters.AddWithValue("@h", Hash(snapId));
            fv.Parameters.AddWithValue("@now", now);
            fvId = (long)fv.ExecuteScalar()!;
        }

        for (var i = 0; i < symbolCount; i++)
        {
            using var s = conn.CreateCommand();
            s.CommandText = """
                INSERT INTO symbols
                    (project_id, symbol_key, fully_qualified_name, display_name, kind, accessibility,
                     file_version_id, line_start, line_end, last_indexed_at)
                VALUES (@p, @key, @fqn, @name, 0, 0, @fv, 1, 10, @now);
                """;
            s.Parameters.AddWithValue("@p", projectId);
            s.Parameters.AddWithValue("@key", $"global::App.Type{i}:{snapId}");
            s.Parameters.AddWithValue("@fqn", $"global::App.Type{i}");
            s.Parameters.AddWithValue("@name", $"Type{i}");
            s.Parameters.AddWithValue("@fv", fvId);
            s.Parameters.AddWithValue("@now", now);
            s.ExecuteNonQuery();
        }

        snapshots.MarkComplete(snapId, now);
        return snapId;
    }

    private static byte[] Hash(long seed)
    {
        var bytes = new byte[32];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)((seed + i) & 0xFF);
        return bytes;
    }
}

/// <summary>
/// A configurable in-process worker. By default it publishes a complete snapshot for the request; test
/// cases can instead have it report unsupported/failed/partial (with per-project diagnostics), publish
/// nothing while claiming success (to exercise the service's validation downgrade), throw, or block on a
/// gate to drive concurrency races. It counts invocations so a test can prove idempotent attach.
/// </summary>
internal sealed class FakeSnapshotWorker(
    IndexDatabase db,
    Func<FakeSnapshotWorker, EnsureSnapshotRequest, SnapshotWorkResult>? behavior = null) : ISnapshotWorker
{
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);
    public IndexDatabase Database => db;

    /// <summary>An optional gate the worker awaits before producing (drives concurrent-attach tests).</summary>
    public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Set true to make the worker wait on <see cref="Gate"/> before producing.</summary>
    public bool UseGate { get; init; }

    public async Task<SnapshotWorkResult> ProduceAsync(
        EnsureSnapshotRequest request, string identityHash, string scratchDir, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        if (UseGate)
            await Gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        var produce = behavior ?? DefaultPublishComplete;
        return produce(this, request);
    }

    private static SnapshotWorkResult DefaultPublishComplete(FakeSnapshotWorker self, EnsureSnapshotRequest request)
    {
        var snapId = ServiceTestFixtures.PublishComplete(self.Database, request);
        return SnapshotWorkResult.Complete(snapId);
    }

    // ---- ready-made behaviors --------------------------------------------------------------------

    public static Func<FakeSnapshotWorker, EnsureSnapshotRequest, SnapshotWorkResult> Unsupported(string message) =>
        (_, _) => SnapshotWorkResult.Unsupported(message, [
            new ProjectOutcome { ProjectPath = "src/App/App.csproj", Severity = JobDiagnosticSeverity.Error,
                Code = "unsupported_target", Message = message }
        ]);

    public static Func<FakeSnapshotWorker, EnsureSnapshotRequest, SnapshotWorkResult> FailedWithDiagnostics(string message) =>
        (_, _) => SnapshotWorkResult.Failed(message, [
            new ProjectOutcome { ProjectPath = "src/App/App.csproj", Severity = JobDiagnosticSeverity.Error,
                Code = "load_failed", Message = message }
        ]);

    /// <summary>Claims success but publishes nothing — the service must downgrade this to failed.</summary>
    public static Func<FakeSnapshotWorker, EnsureSnapshotRequest, SnapshotWorkResult> ClaimsCompleteButPublishesNothing() =>
        (_, _) => SnapshotWorkResult.Complete(999_999);

    /// <summary>
    /// Publishes a GENUINE complete snapshot, but for a DIFFERENT identity than requested (a different
    /// commit), then claims Complete pointing at it — modelling a worker/config mismatch. The service must
    /// refuse to record it as this identity's result (identity validation), because the snapshot keyed by
    /// the requested identity does not exist.
    /// </summary>
    public static Func<FakeSnapshotWorker, EnsureSnapshotRequest, SnapshotWorkResult> PublishesMismatchedIdentity() =>
        (self, request) =>
        {
            var other = request with { CommitSha = request.CommitSha + "-other" };
            var snapId = ServiceTestFixtures.PublishComplete(self.Database, other);
            return SnapshotWorkResult.Complete(snapId);
        };

    public static Func<FakeSnapshotWorker, EnsureSnapshotRequest, SnapshotWorkResult> Throws(string message) =>
        (_, _) => throw new InvalidOperationException(message);
}
