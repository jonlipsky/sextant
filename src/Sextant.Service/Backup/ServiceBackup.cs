using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sextant.Store;

namespace Sextant.Service.Backup;

/// <summary>
/// Creates and restores consistent backups of the standalone index service (criterion 6:
/// backup/restore + disaster recovery). A backup captures the two durable, non-reconstructable stores:
/// <list type="bullet">
///   <item>the CATALOG database — copied with SQLite's ONLINE backup API so it is a transactionally
///   consistent point-in-time image even while the writer is active (no torn WAL);</item>
///   <item>the immutable ARTIFACT volume — the published snapshot outputs.</item>
/// </list>
/// It does NOT copy worker scratch (ephemeral) or the checkout/cache volumes (reconstructable from Git /
/// federation), and it NEVER copies secrets — the manifest documents the credentials boundary an operator
/// re-provides on restore. Restore lays the catalog + artifacts back down; the caller then starts a
/// <see cref="SnapshotService"/> on the restored catalog, which runs migrations, recovers the WAL,
/// reconciles orphaned jobs, and RE-ENFORCES the read-authorization policy — so a restored service is a
/// queryable, authorized service, never a policy-stripped one.
/// </summary>
public static class ServiceBackup
{
    private const string ManifestFileName = "manifest.json";

    /// <summary>
    /// Writes a consistent backup of <paramref name="source"/> (a live catalog connection) and the
    /// artifact volume into <paramref name="destinationDir"/>. The caller must serialize this with catalog
    /// writes (the service runs it under its writer gate) so the online backup races nothing. Returns the
    /// manifest that was written.
    /// </summary>
    public static BackupManifest Create(
        SqliteConnection source,
        int schemaVersion,
        ServicePaths paths,
        string destinationDir,
        string configFingerprint)
    {
        Directory.CreateDirectory(destinationDir);

        var manifest = new BackupManifest
        {
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SchemaVersion = schemaVersion,
            ConfigFingerprint = configFingerprint
        };

        // Consistent online copy of the catalog (folds the WAL into a standalone image).
        var catalogTarget = Path.Combine(destinationDir, manifest.CatalogFile);
        DeleteDatabaseFiles(catalogTarget);
        using (var dest = new SqliteConnection($"Data Source={catalogTarget}"))
        {
            dest.Open();
            source.BackupDatabase(dest);
        }

        // Immutable artifact volume.
        var artifactTarget = Path.Combine(destinationDir, manifest.ArtifactDir);
        CopyDirectory(paths.ArtifactRoot, artifactTarget);

        File.WriteAllText(
            Path.Combine(destinationDir, ManifestFileName),
            JsonSerializer.Serialize(manifest, ServiceJson.Options));

        return manifest;
    }

    /// <summary>Reads (and validates) the manifest in a backup directory.</summary>
    public static BackupManifest ReadManifest(string backupDir)
    {
        var manifestPath = Path.Combine(backupDir, ManifestFileName);
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException(
                $"'{backupDir}' is not a Sextant service backup: no {ManifestFileName} found.");
        var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(manifestPath), ServiceJson.Options)
            ?? throw new InvalidOperationException($"Backup manifest at '{manifestPath}' is empty or invalid.");

        // A backup produced by a NEWER schema than this build must NOT be silently loaded — the on-disk
        // format may be incompatible. This is the same forward-only guard IndexDatabase.CheckReadiness
        // applies, enforced BEFORE we overwrite the target catalog so a bad restore never half-lands.
        if (manifest.SchemaVersion > IndexDatabase.LatestSchemaVersion)
            throw new InvalidOperationException(
                $"Backup was taken at schema v{manifest.SchemaVersion}, newer than this build supports " +
                $"(v{IndexDatabase.LatestSchemaVersion}). Upgrade Sextant before restoring this backup.");

        return manifest;
    }

    /// <summary>
    /// Restores a backup's catalog + artifact volume into the target locations, replacing whatever is
    /// there. Validates the manifest first (refusing a newer-schema backup) and clears the target catalog's
    /// WAL/SHM sidecars so the restored image is loaded cleanly. Returns the validated manifest. The caller
    /// starts a <see cref="SnapshotService"/> on <paramref name="targetCatalogPath"/> afterward to run
    /// migrations, recover, reconcile, and re-enforce authorization.
    /// </summary>
    public static BackupManifest Restore(string backupDir, string targetCatalogPath, ServicePaths targetPaths)
    {
        var manifest = ReadManifest(backupDir);

        var catalogSource = Path.Combine(backupDir, manifest.CatalogFile);
        if (!File.Exists(catalogSource))
            throw new InvalidOperationException($"Backup is missing its catalog file '{catalogSource}'.");

        // A backup always writes an artifacts/ directory (even when empty), so a missing one means the
        // backup is incomplete/corrupt — refuse rather than silently restoring a catalog whose immutable
        // artifact volume is gone (that would leave a queryable catalog pointing at absent snapshot data).
        var artifactSource = Path.Combine(backupDir, manifest.ArtifactDir);
        if (!Directory.Exists(artifactSource))
            throw new InvalidOperationException(
                $"Backup is missing its artifact volume '{artifactSource}' — the backup is incomplete.");

        // Restore is OFFLINE by contract (it precedes service startup and replaces files wholesale). If the
        // TARGET catalog already carries a LIVE (non-expired) writer lease, another service is running on
        // it; replacing its files out from under it would corrupt a live writer and split-brain the index.
        // Refuse rather than clobber. (A stale/expired lease is fine — that is exactly what recovery clears.)
        RefuseIfTargetHasLiveLease(targetCatalogPath);

        var targetDir = Path.GetDirectoryName(Path.GetFullPath(targetCatalogPath));
        if (!string.IsNullOrEmpty(targetDir))
            Directory.CreateDirectory(targetDir);

        DeleteDatabaseFiles(targetCatalogPath);
        File.Copy(catalogSource, targetCatalogPath, overwrite: true);

        // A consistent online backup captures the catalog WHILE the source service held the single-writer
        // lease, so the copied catalog carries that service's live `writer_lease` row. A restore stands up a
        // NEW writer on a NEW host; the previous owner is gone. Clear the stale lease so the restored
        // service's fail-closed lease acquisition (recover → lease) is not rejected by a ghost owner from
        // the backed-up process. (Restore is offline — no concurrent writer to protect against here.)
        ClearWriterLease(targetCatalogPath);

        // Restore the immutable artifact volume (replace target contents).
        CopyDirectory(artifactSource, targetPaths.ArtifactRoot, clearTarget: true);

        return manifest;
    }

    // Refuses an offline restore when the target catalog is owned by a live writer (see the call site). A
    // missing target DB or an expired/absent lease is fine; only a non-expired lease blocks the restore.
    private static void RefuseIfTargetHasLiveLease(string targetCatalogPath)
    {
        if (!File.Exists(targetCatalogPath))
            return;
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = targetCatalogPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();
            using var conn = new SqliteConnection(connectionString);
            conn.Open();
            var lease = WriterLease.GetCurrent(conn);
            if (lease is not null && !lease.IsExpiredAt(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
                throw new InvalidOperationException(
                    "The target catalog is held by a LIVE writer lease — a service is running on it. Stop the " +
                    "service before restoring offline (restore replaces the catalog + artifact volume wholesale).");
        }
        catch (SqliteException)
        {
            // No writer_lease table (pre-016 target) or an unreadable/partial catalog — nothing live to guard.
        }
    }

    // Removes a SQLite database file plus its -wal/-shm sidecars so a stale WAL never shadows a restored
    // or freshly-copied image.
    private static void DeleteDatabaseFiles(string dbPath)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = dbPath + suffix;
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    // Clears the single-writer lease carried in a restored catalog (see the call site). Best-effort: a
    // very old backup predating the lease table simply has nothing to clear.
    private static void ClearWriterLease(string dbPath)
    {
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Pooling = false
            }.ToString();
            using var conn = new SqliteConnection(connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM writer_lease;";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // No writer_lease table (pre-016 backup) — nothing to clear.
        }
    }

    private static void CopyDirectory(string source, string target, bool clearTarget = false)
    {
        if (clearTarget && Directory.Exists(target))
            Directory.Delete(target, recursive: true);
        Directory.CreateDirectory(target);
        if (!Directory.Exists(source))
            return;

        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destFile = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            File.Copy(file, destFile, overwrite: true);
        }
    }
}
