using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Core.Platform;
using Sextant.Store;

namespace Sextant.Service.Contributions;

/// <summary>The result of importing one contribution's payload into an assembly snapshot.</summary>
public sealed record ContributionImportSummary
{
    public required int ProjectsImported { get; init; }
    public required int SymbolsImported { get; init; }
    public required int OccurrencesImported { get; init; }
    public IReadOnlyList<ProjectOutcome> Diagnostics { get; init; } = [];
}

/// <summary>
/// Copies a validated contribution's compact semantic PAYLOAD (a portable Sextant catalog containing ONE
/// complete snapshot) into the service catalog's PENDING assembly snapshot, remapping every foreign key
/// (Phase 16). It is invoked only AFTER <see cref="ContributionValidator"/> accepts the contribution, and
/// runs inside the service's single writer transaction, so an assembled snapshot is published atomically.
///
/// Assembly (acceptance criterion 4): multiple contributions built by DIFFERENT capabilities (Windows +
/// macOS) target the SAME capability-less assembly snapshot identity and each imports its own DISJOINT
/// project versions into that one pending snapshot; each imported project-version row is stamped with the
/// capability that produced it (migration 018 <c>projects.capability_fingerprint</c>), so the assembled
/// snapshot records which environment produced each project version while presenting ONE repository
/// snapshot. Determinism: payload rows are copied in ascending id order and id maps thread the remap, so a
/// given payload imports to byte-identical semantic rows every time.
/// </summary>
public sealed class ContributionImporter(SqliteConnection target)
{
    /// <summary>
    /// Imports the payload snapshot's project versions + compact rows into <paramref name="targetSnapshotId"/>
    /// (a pending snapshot), stamping each project's capability from the manifest. Returns import counts.
    /// </summary>
    public ContributionImportSummary Import(
        SqliteConnection payload, long payloadSnapshotId, long targetSnapshotId, long targetRepositoryId,
        ContributionManifest manifest, long now)
    {
        var targetSnapshots = new SnapshotStore(target);
        var targetProjects = new ProjectStore(target);
        var payloadSnapshots = new SnapshotStore(payload);

        var capabilityByCanonical = manifest.Projects.ToDictionary(p => p.CanonicalId, p => p.CapabilityFingerprint);

        var projectMap = new Dictionary<long, long>();
        var fileVersionMap = new Dictionary<long, long>();
        var symbolMap = new Dictionary<long, long>();
        var occurrenceMap = new Dictionary<long, long>();
        var symbolsImported = 0;

        foreach (var payloadProjectId in payloadSnapshots.GetSnapshotProjectIds(payloadSnapshotId).OrderBy(id => id))
        {
            var source = ReadProject(payload, payloadProjectId);
            var logicalId = targetSnapshots.EnsureLogicalProject(
                targetRepositoryId, source.LogicalCanonicalId, source.RepoRelativePath, source.TargetFramework, now);

            var identity = new ProjectIdentity
            {
                CanonicalId = source.LogicalCanonicalId,
                GitRemoteUrl = source.GitRemoteUrl,
                RepoRelativePath = source.RepoRelativePath,
                DiskPath = null, // never import an absolute producer path into the shared catalog
                AssemblyName = source.AssemblyName,
                TargetFramework = source.TargetFramework,
                IsTestProject = source.IsTestProject
            };
            var newProjectId = targetProjects.UpsertSnapshotProject(identity, targetSnapshotId, logicalId, now);

            var capability = capabilityByCanonical.GetValueOrDefault(source.LogicalCanonicalId) ?? manifest.CapabilityFingerprint;
            StampCapability(newProjectId, capability);
            targetSnapshots.MapProject(targetSnapshotId, newProjectId);
            projectMap[payloadProjectId] = newProjectId;

            CopyFilesAndVersions(payload, payloadProjectId, newProjectId, fileVersionMap);
            symbolsImported += CopySymbols(payload, payloadProjectId, newProjectId, fileVersionMap, symbolMap);        }

        CopyComments(payload, projectMap, fileVersionMap, symbolMap);
        var occurrencesImported = CopyOccurrences(payload, projectMap, symbolMap, fileVersionMap, occurrenceMap);
        CopyRelationships(payload, projectMap, symbolMap);
        CopyDataFlow(payload, occurrenceMap);

        return new ContributionImportSummary
        {
            ProjectsImported = projectMap.Count,
            SymbolsImported = symbolsImported,
            OccurrencesImported = occurrencesImported
        };
    }

    /// <summary>
    /// The logical project versions this contribution would import that are ALREADY mapped into the target
    /// (pending) assembly snapshot from an earlier contribution. Assembly (criterion 4) requires each
    /// environment to contribute DISJOINT capability-specific project versions into the one pending snapshot;
    /// a non-empty result means the contributions overlap, so the caller rejects it rather than letting the
    /// importer silently reuse/overwrite the earlier contribution's project row (non-atomic-persistence
    /// hazard). Pure read — never mutates either catalog.
    /// </summary>
    public IReadOnlyList<string> FindConflictingLogicalProjects(
        SqliteConnection payload, long payloadSnapshotId, long targetSnapshotId)
    {
        var payloadSnapshots = new SnapshotStore(payload);
        var targetSnapshots = new SnapshotStore(target);

        var alreadyMapped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var targetProjectId in targetSnapshots.GetSnapshotProjectIds(targetSnapshotId))
            alreadyMapped.Add(ReadLogicalCanonicalId(target, targetProjectId));

        var conflicts = new List<string>();
        foreach (var payloadProjectId in payloadSnapshots.GetSnapshotProjectIds(payloadSnapshotId).OrderBy(id => id))
        {
            var canonical = ReadLogicalCanonicalId(payload, payloadProjectId);
            if (alreadyMapped.Contains(canonical))
                conflicts.Add(canonical);
        }
        return conflicts;
    }

    private static string ReadLogicalCanonicalId(SqliteConnection conn, long projectId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(lp.canonical_id, p.canonical_id)
            FROM projects p
            LEFT JOIN logical_projects lp ON lp.id = p.logical_project_id
            WHERE p.id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", projectId);
        return cmd.ExecuteScalar() as string
            ?? throw new InvalidOperationException($"project id {projectId} has no canonical id.");
    }

    private void StampCapability(long projectId, string? capability)
    {
        using var cmd = target.CreateCommand();
        cmd.CommandText = "UPDATE projects SET capability_fingerprint = @cap WHERE id = @id;";
        cmd.Parameters.AddWithValue("@cap", (object?)capability ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", projectId);
        cmd.ExecuteNonQuery();
    }

    private sealed record SourceProject(
        string LogicalCanonicalId, string GitRemoteUrl, string RepoRelativePath,
        string? AssemblyName, string? TargetFramework, bool IsTestProject);

    private static SourceProject ReadProject(SqliteConnection payload, long projectId)
    {
        using var cmd = payload.CreateCommand();
        cmd.CommandText = """
            SELECT p.git_remote_url, p.repo_relative_path, p.assembly_name, p.target_framework, p.is_test_project,
                   COALESCE(lp.canonical_id, p.canonical_id) AS logical_canonical
            FROM projects p
            LEFT JOIN logical_projects lp ON lp.id = p.logical_project_id
            WHERE p.id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", projectId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException($"payload project id {projectId} vanished during import.");
        return new SourceProject(
            LogicalCanonicalId: reader.GetString(5),
            GitRemoteUrl: reader.GetString(0),
            RepoRelativePath: reader.GetString(1),
            AssemblyName: reader.IsDBNull(2) ? null : reader.GetString(2),
            TargetFramework: reader.IsDBNull(3) ? null : reader.GetString(3),
            IsTestProject: reader.GetInt64(4) != 0);
    }

    private void CopyFilesAndVersions(
        SqliteConnection payload, long payloadProjectId, long newProjectId, Dictionary<long, long> fileVersionMap)
    {
        var fileMap = new Dictionary<long, long>();
        using (var read = payload.CreateCommand())
        {
            read.CommandText = "SELECT id, repo_relative_path FROM files WHERE project_id = @p ORDER BY id;";
            read.Parameters.AddWithValue("@p", payloadProjectId);
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                var oldId = reader.GetInt64(0);
                using var ins = target.CreateCommand();
                ins.CommandText = "INSERT INTO files (project_id, repo_relative_path) VALUES (@p, @path) RETURNING id;";
                ins.Parameters.AddWithValue("@p", newProjectId);
                ins.Parameters.AddWithValue("@path", reader.GetString(1));
                fileMap[oldId] = (long)ins.ExecuteScalar()!;
            }
        }

        foreach (var (oldFileId, newFileId) in fileMap.OrderBy(kv => kv.Key))
        {
            using var read = payload.CreateCommand();
            read.CommandText =
                "SELECT id, content_hash, git_blob_hash, source_ref, last_indexed_at FROM file_versions WHERE file_id = @f ORDER BY id;";
            read.Parameters.AddWithValue("@f", oldFileId);
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                var oldFvId = reader.GetInt64(0);
                using var ins = target.CreateCommand();
                ins.CommandText = """
                    INSERT INTO file_versions (file_id, content_hash, git_blob_hash, source_ref, last_indexed_at)
                    VALUES (@f, @hash, @git, @ref, @now) RETURNING id;
                    """;
                ins.Parameters.AddWithValue("@f", newFileId);
                ins.Parameters.AddWithValue("@hash", reader.GetValue(1));
                ins.Parameters.AddWithValue("@git", reader.IsDBNull(2) ? DBNull.Value : reader.GetValue(2));
                ins.Parameters.AddWithValue("@ref", reader.IsDBNull(3) ? DBNull.Value : reader.GetString(3));
                ins.Parameters.AddWithValue("@now", reader.GetInt64(4));
                fileVersionMap[oldFvId] = (long)ins.ExecuteScalar()!;
            }
        }
    }

    private int CopySymbols(
        SqliteConnection payload, long payloadProjectId, long newProjectId,
        Dictionary<long, long> fileVersionMap, Dictionary<long, long> symbolMap)
    {
        var count = 0;
        using var read = payload.CreateCommand();
        read.CommandText = """
            SELECT id, symbol_key, fully_qualified_name, display_name, kind, accessibility, is_static, is_abstract,
                   is_virtual, is_override, signature, signature_hash, doc_comment, file_version_id, line_start,
                   line_end, attributes, last_indexed_at
            FROM symbols WHERE project_id = @p ORDER BY id;
            """;
        read.Parameters.AddWithValue("@p", payloadProjectId);
        using var reader = read.ExecuteReader();
        while (reader.Read())
        {
            var oldId = reader.GetInt64(0);
            long? newFv = reader.IsDBNull(13) ? null : fileVersionMap.GetValueOrDefault(reader.GetInt64(13));
            using var ins = target.CreateCommand();
            ins.CommandText = """
                INSERT INTO symbols
                    (project_id, symbol_key, fully_qualified_name, display_name, kind, accessibility, is_static,
                     is_abstract, is_virtual, is_override, signature, signature_hash, doc_comment, file_version_id,
                     line_start, line_end, attributes, last_indexed_at)
                VALUES (@p, @key, @fqn, @name, @kind, @acc, @static, @abstract, @virtual, @override, @sig, @sighash,
                        @doc, @fv, @ls, @le, @attr, @now)
                RETURNING id;
                """;
            ins.Parameters.AddWithValue("@p", newProjectId);
            ins.Parameters.AddWithValue("@key", reader.GetString(1));
            ins.Parameters.AddWithValue("@fqn", reader.GetString(2));
            ins.Parameters.AddWithValue("@name", reader.GetString(3));
            ins.Parameters.AddWithValue("@kind", reader.GetInt64(4));
            ins.Parameters.AddWithValue("@acc", reader.GetInt64(5));
            ins.Parameters.AddWithValue("@static", reader.GetInt64(6));
            ins.Parameters.AddWithValue("@abstract", reader.GetInt64(7));
            ins.Parameters.AddWithValue("@virtual", reader.GetInt64(8));
            ins.Parameters.AddWithValue("@override", reader.GetInt64(9));
            ins.Parameters.AddWithValue("@sig", reader.IsDBNull(10) ? DBNull.Value : reader.GetString(10));
            ins.Parameters.AddWithValue("@sighash", reader.IsDBNull(11) ? DBNull.Value : reader.GetString(11));
            ins.Parameters.AddWithValue("@doc", reader.IsDBNull(12) ? DBNull.Value : reader.GetString(12));
            ins.Parameters.AddWithValue("@fv", (object?)newFv ?? DBNull.Value);
            ins.Parameters.AddWithValue("@ls", reader.GetInt64(14));
            ins.Parameters.AddWithValue("@le", reader.GetInt64(15));
            ins.Parameters.AddWithValue("@attr", reader.IsDBNull(16) ? DBNull.Value : reader.GetString(16));
            ins.Parameters.AddWithValue("@now", reader.GetInt64(17));
            symbolMap[oldId] = (long)ins.ExecuteScalar()!;
            count++;
        }
        return count;
    }

    private int CopyOccurrences(
        SqliteConnection payload, Dictionary<long, long> projectMap, Dictionary<long, long> symbolMap,
        Dictionary<long, long> fileVersionMap, Dictionary<long, long> occurrenceMap)
    {
        var count = 0;
        using var read = payload.CreateCommand();
        read.CommandText = """
            SELECT id, in_project_id, target_symbol_id, source_symbol_id, file_version_id, line, col, kind, flags, last_indexed_at
            FROM occurrences ORDER BY id;
            """;
        using var reader = read.ExecuteReader();
        while (reader.Read())
        {
            var oldId = reader.GetInt64(0);
            if (!projectMap.TryGetValue(reader.GetInt64(1), out var inProject)) continue;
            if (!symbolMap.TryGetValue(reader.GetInt64(2), out var targetSymbol)) continue;
            if (!fileVersionMap.TryGetValue(reader.GetInt64(4), out var fileVersion)) continue;
            long? sourceSymbol = reader.IsDBNull(3) ? null : symbolMap.GetValueOrDefault(reader.GetInt64(3));

            using var ins = target.CreateCommand();
            ins.CommandText = """
                INSERT INTO occurrences
                    (in_project_id, target_symbol_id, source_symbol_id, file_version_id, line, col, kind, flags, last_indexed_at)
                VALUES (@inp, @target, @source, @fv, @line, @col, @kind, @flags, @now)
                RETURNING id;
                """;
            ins.Parameters.AddWithValue("@inp", inProject);
            ins.Parameters.AddWithValue("@target", targetSymbol);
            ins.Parameters.AddWithValue("@source", (object?)sourceSymbol ?? DBNull.Value);
            ins.Parameters.AddWithValue("@fv", fileVersion);
            ins.Parameters.AddWithValue("@line", reader.GetInt64(5));
            ins.Parameters.AddWithValue("@col", reader.GetInt64(6));
            ins.Parameters.AddWithValue("@kind", reader.GetInt64(7));
            ins.Parameters.AddWithValue("@flags", reader.GetInt64(8));
            ins.Parameters.AddWithValue("@now", reader.GetInt64(9));
            occurrenceMap[oldId] = (long)ins.ExecuteScalar()!;
            count++;
        }
        return count;
    }

    private void CopyRelationships(SqliteConnection payload, Dictionary<long, long> projectMap, Dictionary<long, long> symbolMap)
    {
        _ = projectMap;
        using var read = payload.CreateCommand();
        read.CommandText = "SELECT from_symbol_id, to_symbol_id, kind, last_indexed_at FROM relationships ORDER BY id;";
        using var reader = read.ExecuteReader();
        while (reader.Read())
        {
            if (!symbolMap.TryGetValue(reader.GetInt64(0), out var from)) continue;
            if (!symbolMap.TryGetValue(reader.GetInt64(1), out var to)) continue;
            using var ins = target.CreateCommand();
            ins.CommandText =
                "INSERT INTO relationships (from_symbol_id, to_symbol_id, kind, last_indexed_at) VALUES (@f, @t, @k, @now);";
            ins.Parameters.AddWithValue("@f", from);
            ins.Parameters.AddWithValue("@t", to);
            ins.Parameters.AddWithValue("@k", reader.GetInt64(2));
            ins.Parameters.AddWithValue("@now", reader.GetInt64(3));
            ins.ExecuteNonQuery();
        }
    }

    private void CopyComments(
        SqliteConnection payload, Dictionary<long, long> projectMap,
        Dictionary<long, long> fileVersionMap, Dictionary<long, long> symbolMap)
    {
        using var read = payload.CreateCommand();
        read.CommandText =
            "SELECT project_id, file_version_id, line, tag, text, enclosing_symbol_id, last_indexed_at FROM comments ORDER BY id;";
        using var reader = read.ExecuteReader();
        while (reader.Read())
        {
            if (!projectMap.TryGetValue(reader.GetInt64(0), out var project)) continue;
            long? fv = reader.IsDBNull(1) ? null : fileVersionMap.GetValueOrDefault(reader.GetInt64(1));
            long? enclosing = reader.IsDBNull(5) ? null : symbolMap.GetValueOrDefault(reader.GetInt64(5));
            using var ins = target.CreateCommand();
            ins.CommandText = """
                INSERT INTO comments (project_id, file_version_id, line, tag, text, enclosing_symbol_id, last_indexed_at)
                VALUES (@p, @fv, @line, @tag, @text, @sym, @now);
                """;
            ins.Parameters.AddWithValue("@p", project);
            ins.Parameters.AddWithValue("@fv", (object?)fv ?? DBNull.Value);
            ins.Parameters.AddWithValue("@line", reader.GetInt64(2));
            ins.Parameters.AddWithValue("@tag", reader.GetString(3));
            ins.Parameters.AddWithValue("@text", reader.GetString(4));
            ins.Parameters.AddWithValue("@sym", (object?)enclosing ?? DBNull.Value);
            ins.Parameters.AddWithValue("@now", reader.GetInt64(6));
            ins.ExecuteNonQuery();
        }
    }

    private void CopyDataFlow(SqliteConnection payload, Dictionary<long, long> occurrenceMap)
    {
        using (var read = payload.CreateCommand())
        {
            read.CommandText = """
                SELECT occurrence_id, parameter_ordinal, parameter_name, argument_expression, argument_kind,
                       source_symbol_fqn, last_indexed_at
                FROM argument_flow ORDER BY id;
                """;
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                if (!occurrenceMap.TryGetValue(reader.GetInt64(0), out var occ)) continue;
                using var ins = target.CreateCommand();
                ins.CommandText = """
                    INSERT INTO argument_flow
                        (occurrence_id, parameter_ordinal, parameter_name, argument_expression, argument_kind,
                         source_symbol_fqn, last_indexed_at)
                    VALUES (@o, @ord, @name, @expr, @kind, @fqn, @now);
                    """;
                ins.Parameters.AddWithValue("@o", occ);
                ins.Parameters.AddWithValue("@ord", reader.GetInt64(1));
                ins.Parameters.AddWithValue("@name", reader.GetString(2));
                ins.Parameters.AddWithValue("@expr", reader.GetString(3));
                ins.Parameters.AddWithValue("@kind", reader.GetString(4));
                ins.Parameters.AddWithValue("@fqn", reader.IsDBNull(5) ? DBNull.Value : reader.GetString(5));
                ins.Parameters.AddWithValue("@now", reader.GetInt64(6));
                ins.ExecuteNonQuery();
            }
        }

        using (var read = payload.CreateCommand())
        {
            read.CommandText = """
                SELECT occurrence_id, destination_kind, destination_variable, destination_symbol_fqn, last_indexed_at
                FROM return_flow ORDER BY id;
                """;
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                if (!occurrenceMap.TryGetValue(reader.GetInt64(0), out var occ)) continue;
                using var ins = target.CreateCommand();
                ins.CommandText = """
                    INSERT INTO return_flow (occurrence_id, destination_kind, destination_variable, destination_symbol_fqn, last_indexed_at)
                    VALUES (@o, @kind, @var, @fqn, @now);
                    """;
                ins.Parameters.AddWithValue("@o", occ);
                ins.Parameters.AddWithValue("@kind", reader.GetString(1));
                ins.Parameters.AddWithValue("@var", reader.IsDBNull(2) ? DBNull.Value : reader.GetString(2));
                ins.Parameters.AddWithValue("@fqn", reader.IsDBNull(3) ? DBNull.Value : reader.GetString(3));
                ins.Parameters.AddWithValue("@now", reader.GetInt64(4));
                ins.ExecuteNonQuery();
            }
        }
    }
}
