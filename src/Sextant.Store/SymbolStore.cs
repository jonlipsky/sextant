using Sextant.Core;
using Microsoft.Data.Sqlite;

namespace Sextant.Store;

public sealed class SymbolStore(SqliteConnection connection)
{
    /// <summary>
    /// Optional shared file-version resolver. The indexer injects one so the symbol phase, occurrence
    /// phase and comment phase share a single (project, path) → file_version_id cache and repo-root
    /// cache. When unset (ad-hoc/test callers of the convenience <see cref="Insert(SymbolInfo)"/>) a
    /// per-store instance is created lazily.
    /// </summary>
    public FileStore? Files { get; set; }

    private FileStore FilesOrDefault => Files ??= new FileStore(connection);

    // Reads reconstruct the absolute FilePath from the file version's repo-relative path and the
    // owning project's disk path, so no absolute path is ever stored (acceptance criterion 1).
    private const string SelectPrefix = """
        SELECT s.id, s.project_id, s.symbol_key, s.fully_qualified_name, s.display_name, s.kind,
               s.accessibility, s.is_static, s.is_abstract, s.is_virtual, s.is_override, s.signature,
               s.signature_hash, s.doc_comment, s.line_start, s.line_end, s.attributes, s.last_indexed_at,
               f.repo_relative_path AS repo_relative_path, p.disk_path AS disk_path,
               p.repo_relative_path AS project_repo_relative
        FROM symbols s
        LEFT JOIN file_versions fv ON fv.id = s.file_version_id
        LEFT JOIN files f ON f.id = fv.file_id
        LEFT JOIN projects p ON p.id = s.project_id
        """;

    private const string InsertSql = """
        INSERT INTO symbols (project_id, symbol_key, fully_qualified_name, display_name, kind, accessibility,
            is_static, is_abstract, is_virtual, is_override, signature, signature_hash,
            doc_comment, file_version_id, line_start, line_end, attributes, last_indexed_at)
        VALUES (@project_id, @symbol_key, @fqn, @display_name, @kind, @accessibility,
            @is_static, @is_abstract, @is_virtual, @is_override, @signature, @signature_hash,
            @doc_comment, @file_version_id, @line_start, @line_end, @attributes, @last_indexed_at)
        ON CONFLICT(project_id, symbol_key) DO UPDATE SET
            fully_qualified_name = excluded.fully_qualified_name,
            display_name = excluded.display_name,
            kind = excluded.kind,
            accessibility = excluded.accessibility,
            is_static = excluded.is_static,
            is_abstract = excluded.is_abstract,
            is_virtual = excluded.is_virtual,
            is_override = excluded.is_override,
            signature = excluded.signature,
            signature_hash = excluded.signature_hash,
            doc_comment = excluded.doc_comment,
            file_version_id = excluded.file_version_id,
            line_start = excluded.line_start,
            line_end = excluded.line_end,
            attributes = excluded.attributes,
            last_indexed_at = excluded.last_indexed_at
        RETURNING id;
        """;

    /// <summary>
    /// Creates a reusable insert command for batched writes. The caller executes it many times via
    /// <see cref="Insert(SqliteCommand, SymbolInfo)"/> so SQLite keeps one compiled statement, and
    /// disposes it when the batch is done.
    /// </summary>
    public SqliteCommand CreateInsertCommand()
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = InsertSql;
        return cmd;
    }

    public long Insert(SymbolInfo symbol)
    {
        using var cmd = CreateInsertCommand();
        return Insert(cmd, symbol);
    }

    public long Insert(SqliteCommand cmd, SymbolInfo symbol)
    {
        var fileVersionId = FilesOrDefault.ResolveFileVersionId(
            symbol.ProjectId, symbol.FilePath, contentHash: null, lastIndexedAt: symbol.LastIndexedAt);
        Bind(cmd, symbol, fileVersionId);
        return (long)cmd.ExecuteScalar()!;
    }

    private static void Bind(SqliteCommand cmd, SymbolInfo symbol, long fileVersionId)
    {
        SqlParam.Set(cmd, "@project_id", symbol.ProjectId);
        SqlParam.Set(cmd, "@symbol_key", symbol.SymbolKey);
        SqlParam.Set(cmd, "@fqn", symbol.FullyQualifiedName);
        SqlParam.Set(cmd, "@display_name", symbol.DisplayName);
        SqlParam.Set(cmd, "@kind", (int)symbol.Kind);
        SqlParam.Set(cmd, "@accessibility", (int)symbol.Accessibility);
        SqlParam.Set(cmd, "@is_static", symbol.IsStatic ? 1 : 0);
        SqlParam.Set(cmd, "@is_abstract", symbol.IsAbstract ? 1 : 0);
        SqlParam.Set(cmd, "@is_virtual", symbol.IsVirtual ? 1 : 0);
        SqlParam.Set(cmd, "@is_override", symbol.IsOverride ? 1 : 0);
        SqlParam.Set(cmd, "@signature", symbol.Signature);
        SqlParam.Set(cmd, "@signature_hash", symbol.SignatureHash);
        SqlParam.Set(cmd, "@doc_comment", symbol.DocComment);
        SqlParam.Set(cmd, "@file_version_id", fileVersionId);
        SqlParam.Set(cmd, "@line_start", symbol.LineStart);
        SqlParam.Set(cmd, "@line_end", symbol.LineEnd);
        SqlParam.Set(cmd, "@attributes", symbol.Attributes);
        SqlParam.Set(cmd, "@last_indexed_at", symbol.LastIndexedAt);
    }

    public SymbolInfo? GetById(long id)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SelectPrefix + " WHERE s.id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadSymbol(reader) : null;
    }

    public SymbolInfo? GetByFqn(string fullyQualifiedName, long? projectId = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = projectId.HasValue
            ? SelectPrefix + " WHERE s.fully_qualified_name = @fqn AND s.project_id = @project_id;"
            : SelectPrefix + " WHERE s.fully_qualified_name = @fqn;";
        cmd.Parameters.AddWithValue("@fqn", fullyQualifiedName);
        if (projectId.HasValue)
            cmd.Parameters.AddWithValue("@project_id", projectId.Value);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadSymbol(reader) : null;
    }

    /// <summary>Looks up a definition by its stable semantic key within a project.</summary>
    public SymbolInfo? GetBySymbolKey(string symbolKey, long projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SelectPrefix + " WHERE s.symbol_key = @symbol_key AND s.project_id = @project_id;";
        cmd.Parameters.AddWithValue("@symbol_key", symbolKey);
        cmd.Parameters.AddWithValue("@project_id", projectId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadSymbol(reader) : null;
    }

    /// <summary>
    /// Resolves a display fully-qualified name to every matching definition. Because the FQN is no
    /// longer a unique identity (overloads share one, and the same FQN can exist in several
    /// projects), callers get all candidates and decide how to report ambiguity rather than
    /// silently selecting one row. Results are ordered deterministically (project_id, then the stable
    /// symbol_key, then id) so a best-match pick is content-stable across rebuilds even though the
    /// underlying row ids are reassigned on every full re-index.
    /// </summary>
    public List<SymbolInfo> ResolveByFqn(string fullyQualifiedName, long? projectId = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = projectId.HasValue
            ? SelectPrefix + " WHERE s.fully_qualified_name = @fqn AND s.project_id = @project_id ORDER BY s.project_id, s.symbol_key, s.id;"
            : SelectPrefix + " WHERE s.fully_qualified_name = @fqn ORDER BY s.project_id, s.symbol_key, s.id;";
        cmd.Parameters.AddWithValue("@fqn", fullyQualifiedName);
        if (projectId.HasValue)
            cmd.Parameters.AddWithValue("@project_id", projectId.Value);
        return ReadAll(cmd);
    }

    public List<SymbolInfo> GetByFile(string filePath)
    {
        var candidates = CandidateRelatives(filePath);
        using var cmd = connection.CreateCommand();
        var placeholders = string.Join(", ", candidates.Select((_, i) => $"@rel{i}"));
        cmd.CommandText = SelectPrefix + $" WHERE f.repo_relative_path IN ({placeholders});";
        for (var i = 0; i < candidates.Count; i++)
            cmd.Parameters.AddWithValue($"@rel{i}", candidates[i]);
        // Exact-match on the reconstructed absolute path so a coincidental same-relative-path file in
        // another repo root (submodule) is not returned.
        return ReadAll(cmd).Where(s => PathEquals(s.FilePath, filePath)).ToList();
    }

    // Project-scoped variant: a source file that is shared across the evaluated target frameworks of a
    // multi-targeted project appears once per logical (per-TFM) project, so callers that re-index or
    // resolve within one framework must restrict to that project rather than matching every variant.
    public List<SymbolInfo> GetByFile(string filePath, long projectId)
    {
        var rel = SourcePaths.ToRepoRelative(FilesOrDefault.GetRepoRoot(projectId), filePath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SelectPrefix + " WHERE s.project_id = @project_id AND f.repo_relative_path = @rel;";
        cmd.Parameters.AddWithValue("@project_id", projectId);
        cmd.Parameters.AddWithValue("@rel", rel);
        return ReadAll(cmd);
    }

    public List<SymbolInfo> GetByProjectAndAccessibility(long projectId, string accessibility)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SelectPrefix + " WHERE s.project_id = @project_id AND s.accessibility = @accessibility;";
        cmd.Parameters.AddWithValue("@project_id", projectId);
        cmd.Parameters.AddWithValue("@accessibility", AccessibilityNameToInt(accessibility));
        return ReadAll(cmd);
    }

    public List<SymbolInfo> GetByProjectAndAccessibility(long projectId, string[] accessibilities)
    {
        using var cmd = connection.CreateCommand();
        var placeholders = string.Join(", ", accessibilities.Select((_, i) => $"@acc{i}"));
        cmd.CommandText = SelectPrefix + $" WHERE s.project_id = @project_id AND s.accessibility IN ({placeholders});";
        cmd.Parameters.AddWithValue("@project_id", projectId);
        for (var i = 0; i < accessibilities.Length; i++)
            cmd.Parameters.AddWithValue($"@acc{i}", AccessibilityNameToInt(accessibilities[i]));
        return ReadAll(cmd);
    }

    public List<SymbolInfo> SearchFts(string query, int maxResults, string? kindFilter = null)
    {
        using var cmd = connection.CreateCommand();
        var kindClause = kindFilter != null ? " AND s.kind = @kind" : "";
        cmd.CommandText = $"""
            {SelectPrefix}
            JOIN symbols_fts fts ON fts.rowid = s.id
            WHERE symbols_fts MATCH @query{kindClause}
            ORDER BY rank
            LIMIT @max_results;
            """;
        cmd.Parameters.AddWithValue("@query", query);
        cmd.Parameters.AddWithValue("@max_results", maxResults);
        if (kindFilter != null)
            cmd.Parameters.AddWithValue("@kind", KindNameToInt(kindFilter));
        return ReadAll(cmd);
    }

    private static readonly int[] TypeKindOrdinals =
    {
        (int)SymbolKind.Class, (int)SymbolKind.Interface, (int)SymbolKind.Struct,
        (int)SymbolKind.Enum, (int)SymbolKind.Delegate, (int)SymbolKind.Record
    };

    public List<string> GetAllTypeFqns(long? projectId = null)
    {
        using var cmd = connection.CreateCommand();
        var projectClause = projectId.HasValue ? " AND project_id = @projectId" : "";
        var kindList = string.Join(",", TypeKindOrdinals);
        cmd.CommandText = $"SELECT fully_qualified_name FROM symbols WHERE kind IN ({kindList}){projectClause};";
        if (projectId.HasValue)
            cmd.Parameters.AddWithValue("@projectId", projectId.Value);

        var results = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(reader.GetString(0));
        return results;
    }

    public List<SymbolInfo> GetByFqnPrefix(string prefix, long? projectId = null)
    {
        using var cmd = connection.CreateCommand();
        var projectClause = projectId.HasValue ? " AND s.project_id = @projectId" : "";
        cmd.CommandText = SelectPrefix + $" WHERE s.fully_qualified_name LIKE @prefix || '%'{projectClause};";
        cmd.Parameters.AddWithValue("@prefix", prefix);
        if (projectId.HasValue)
            cmd.Parameters.AddWithValue("@projectId", projectId.Value);
        return ReadAll(cmd);
    }

    public List<SymbolInfo> GetByAttribute(string attributeFqn)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SelectPrefix + " WHERE s.attributes LIKE '%' || @attr || '%' ORDER BY s.fully_qualified_name, s.id;";
        cmd.Parameters.AddWithValue("@attr", attributeFqn);
        var results = ReadAll(cmd);
        // Verify exact match in JSON array
        return results.Where(s =>
        {
            if (s.Attributes == null) return false;
            try
            {
                var attrs = System.Text.Json.JsonSerializer.Deserialize<List<string>>(s.Attributes);
                return attrs != null && attrs.Contains(attributeFqn);
            }
            catch { return false; }
        }).ToList();
    }

    /// <summary>
    /// Symbols with zero inbound pure-reference occurrences (dead-code detection). Mirrors the
    /// pre-Phase-7 <c>LEFT JOIN "references"</c> semantics against the unified occurrences table:
    /// only source-NULL (pure-reference) occurrences count as a "reference", which — because the
    /// document extractor emits a reference row for every usage including invocations — is the same
    /// referenced-set the old dedicated references table produced.
    /// </summary>
    public List<SymbolInfo> GetUnreferenced(long? projectDbId, string? kind, bool excludeTestProjects, string? accessibility)
    {
        using var cmd = connection.CreateCommand();
        var clauses = new List<string> { "occ.id IS NULL" };
        if (kind != null)
        {
            clauses.Add("s.kind = @kind");
            cmd.Parameters.AddWithValue("@kind", KindNameToInt(kind));
        }
        if (projectDbId != null)
        {
            clauses.Add("s.project_id = @project_db_id");
            cmd.Parameters.AddWithValue("@project_db_id", projectDbId.Value);
        }
        if (excludeTestProjects)
            clauses.Add("p.is_test_project = 0");
        if (accessibility != null)
        {
            clauses.Add("s.accessibility = @accessibility");
            cmd.Parameters.AddWithValue("@accessibility", AccessibilityNameToInt(accessibility));
        }
        var where = string.Join(" AND ", clauses);
        cmd.CommandText = SelectPrefix + $"""

            LEFT JOIN occurrences occ ON occ.target_symbol_id = s.id AND occ.source_symbol_id IS NULL
            WHERE {where}
            ORDER BY f.repo_relative_path, s.line_start;
            """;
        return ReadAll(cmd);
    }

    public List<SymbolInfo> SearchBySignature(
        string? returnTypePattern, string? paramTypePattern,
        string? kind, long? projectId, int maxResults)
    {
        using var cmd = connection.CreateCommand();
        var sql = new System.Text.StringBuilder(SelectPrefix + " WHERE 1=1");

        if (kind != null)
        {
            sql.Append(" AND s.kind = @kind");
            cmd.Parameters.AddWithValue("@kind", KindNameToInt(kind));
        }
        else
        {
            sql.Append($" AND s.kind IN ({(int)SymbolKind.Method}, {(int)SymbolKind.Constructor})");
        }

        if (returnTypePattern != null)
        {
            sql.Append(" AND s.signature LIKE @return_type_pattern");
            cmd.Parameters.AddWithValue("@return_type_pattern", $"%{returnTypePattern} %");
        }

        if (paramTypePattern != null)
        {
            sql.Append(" AND s.signature LIKE @param_type_pattern");
            cmd.Parameters.AddWithValue("@param_type_pattern", $"%(%{paramTypePattern}%");
        }

        if (projectId.HasValue)
        {
            sql.Append(" AND s.project_id = @projectId");
            cmd.Parameters.AddWithValue("@projectId", projectId.Value);
        }

        sql.Append(" LIMIT @max_results");
        cmd.Parameters.AddWithValue("@max_results", maxResults);

        cmd.CommandText = sql.ToString();
        return ReadAll(cmd);
    }

    public void DeleteByFile(string filePath)
    {
        var candidates = CandidateRelatives(filePath);
        using var cmd = connection.CreateCommand();
        var placeholders = string.Join(", ", candidates.Select((_, i) => $"@rel{i}"));
        cmd.CommandText = $"""
            DELETE FROM symbols WHERE file_version_id IN (
                SELECT fv.id FROM files f JOIN file_versions fv ON fv.file_id = f.id
                WHERE f.repo_relative_path IN ({placeholders}));
            """;
        for (var i = 0; i < candidates.Count; i++)
            cmd.Parameters.AddWithValue($"@rel{i}", candidates[i]);
        cmd.ExecuteNonQuery();
    }

    // Project-scoped delete: only clears this logical (per-TFM) project's symbols for the file, so
    // re-indexing one framework of a multi-targeted project does not delete the sibling framework's
    // symbols declared in the same shared source file.
    public void DeleteByFile(string filePath, long projectId)
    {
        var rel = SourcePaths.ToRepoRelative(FilesOrDefault.GetRepoRoot(projectId), filePath);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM symbols WHERE project_id = @project_id AND file_version_id IN (
                SELECT fv.id FROM files f JOIN file_versions fv ON fv.file_id = f.id
                WHERE f.project_id = @project_id AND f.repo_relative_path = @rel);
            """;
        cmd.Parameters.AddWithValue("@project_id", projectId);
        cmd.Parameters.AddWithValue("@rel", rel);
        cmd.ExecuteNonQuery();
    }

    public void DeleteByProject(long projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM symbols WHERE project_id = @project_id;";
        cmd.Parameters.AddWithValue("@project_id", projectId);
        cmd.ExecuteNonQuery();
    }

    private static List<SymbolInfo> ReadAll(SqliteCommand cmd)
    {
        var results = new List<SymbolInfo>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(ReadSymbol(reader));
        return results;
    }

    private static SymbolInfo ReadSymbol(SqliteDataReader reader)
    {
        var repoRelOrdinal = reader.GetOrdinal("repo_relative_path");
        string filePath = "";
        if (!reader.IsDBNull(repoRelOrdinal))
        {
            var repoRel = reader.GetString(repoRelOrdinal);
            var diskOrdinal = reader.GetOrdinal("disk_path");
            var projRelOrdinal = reader.GetOrdinal("project_repo_relative");
            var disk = reader.IsDBNull(diskOrdinal) ? null : reader.GetString(diskOrdinal);
            var projRel = reader.IsDBNull(projRelOrdinal) ? null : reader.GetString(projRelOrdinal);
            filePath = SourcePaths.ToAbsolute(SourcePaths.DeriveRepoRoot(disk, projRel), repoRel);
        }

        return new SymbolInfo
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            ProjectId = reader.GetInt64(reader.GetOrdinal("project_id")),
            SymbolKey = reader.GetString(reader.GetOrdinal("symbol_key")),
            FullyQualifiedName = reader.GetString(reader.GetOrdinal("fully_qualified_name")),
            DisplayName = reader.GetString(reader.GetOrdinal("display_name")),
            Kind = (SymbolKind)reader.GetInt64(reader.GetOrdinal("kind")),
            Accessibility = (Accessibility)reader.GetInt64(reader.GetOrdinal("accessibility")),
            IsStatic = reader.GetInt64(reader.GetOrdinal("is_static")) != 0,
            IsAbstract = reader.GetInt64(reader.GetOrdinal("is_abstract")) != 0,
            IsVirtual = reader.GetInt64(reader.GetOrdinal("is_virtual")) != 0,
            IsOverride = reader.GetInt64(reader.GetOrdinal("is_override")) != 0,
            Signature = reader.IsDBNull(reader.GetOrdinal("signature")) ? null : reader.GetString(reader.GetOrdinal("signature")),
            SignatureHash = reader.IsDBNull(reader.GetOrdinal("signature_hash")) ? null : reader.GetString(reader.GetOrdinal("signature_hash")),
            DocComment = reader.IsDBNull(reader.GetOrdinal("doc_comment")) ? null : reader.GetString(reader.GetOrdinal("doc_comment")),
            FilePath = filePath,
            LineStart = reader.GetInt32(reader.GetOrdinal("line_start")),
            LineEnd = reader.GetInt32(reader.GetOrdinal("line_end")),
            Attributes = reader.IsDBNull(reader.GetOrdinal("attributes")) ? null : reader.GetString(reader.GetOrdinal("attributes")),
            LastIndexedAt = reader.GetInt64(reader.GetOrdinal("last_indexed_at"))
        };
    }

    // Distinct repo-relative candidate strings for an absolute (or already-relative) path across every
    // known project root, plus the raw input, so file lookups by absolute path still resolve.
    private List<string> CandidateRelatives(string path)
    {
        var candidates = new HashSet<string>(StringComparer.Ordinal) { path };
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT disk_path, repo_relative_path FROM projects;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var disk = reader.IsDBNull(0) ? null : reader.GetString(0);
            var projRel = reader.IsDBNull(1) ? null : reader.GetString(1);
            candidates.Add(SourcePaths.ToRepoRelative(SourcePaths.DeriveRepoRoot(disk, projRel), path));
        }
        return candidates.ToList();
    }

    private static bool PathEquals(string a, string b)
        => string.Equals(
            a.Replace('\\', '/'), b.Replace('\\', '/'),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // Maps a lowercase kind name (as used by MCP filters) to its stored ordinal, or -1 when unknown so
    // an unrecognized filter matches nothing (matching the pre-Phase-7 text-equality behavior).
    private static int KindNameToInt(string name)
        => Enum.TryParse<SymbolKind>(name, ignoreCase: true, out var kind) ? (int)kind : -1;

    private static int AccessibilityNameToInt(string value) => value switch
    {
        "public" => (int)Accessibility.Public,
        "internal" => (int)Accessibility.Internal,
        "protected" => (int)Accessibility.Protected,
        "private" => (int)Accessibility.Private,
        "protected_internal" => (int)Accessibility.ProtectedInternal,
        "private_protected" => (int)Accessibility.PrivateProtected,
        _ => -1
    };

    public static string FormatAccessibility(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public => "public",
        Accessibility.Internal => "internal",
        Accessibility.Protected => "protected",
        Accessibility.Private => "private",
        Accessibility.ProtectedInternal => "protected_internal",
        Accessibility.PrivateProtected => "private_protected",
        _ => "public"
    };

    public static Accessibility ParseAccessibility(string value) => value switch
    {
        "public" => Accessibility.Public,
        "internal" => Accessibility.Internal,
        "protected" => Accessibility.Protected,
        "private" => Accessibility.Private,
        "protected_internal" => Accessibility.ProtectedInternal,
        "private_protected" => Accessibility.PrivateProtected,
        _ => Accessibility.Public
    };
}
