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

    /// <summary>
    /// Optional Phase-9 snapshot read scope. Default <see cref="SnapshotReadScope.Unscoped"/> makes every
    /// query byte-identical to the pre-Phase-9 behavior (no snapshot filter), so the write path and all
    /// direct-seed/legacy readers are unchanged; the MCP layer sets it to the selected snapshot so a
    /// scope-less query transparently defaults to the current snapshot (criterion 6).
    /// </summary>
    public SnapshotReadScope Scope { get; set; } = SnapshotReadScope.Unscoped;

    /// <summary>
    /// Optional further restriction of the name and key lookups (<see cref="GetScopedProjectIds"/>) to these project
    /// versions, e.g. a shared submodule's provider projects; null = every project in <see cref="Scope"/>.
    /// </summary>
    public IReadOnlySet<long>? ProjectRestriction { get; init; }

    // Reads reconstruct the absolute FilePath from the file version's repo-relative path and the
    // owning project's disk path, so no absolute path is ever stored (acceptance criterion 1).
    private const string SelectBase = """
        SELECT s.id, s.project_id, s.symbol_key, s.fully_qualified_name, s.display_name, s.kind,
               s.accessibility, s.is_static, s.is_abstract, s.is_virtual, s.is_override, s.signature,
               s.signature_hash, s.declaration, s.doc_comment, s.line_start, s.line_end, s.attributes, s.last_indexed_at,
               f.repo_relative_path AS repo_relative_path, p.disk_path AS disk_path,
               p.repo_relative_path AS project_repo_relative
        FROM symbols s
        LEFT JOIN file_versions fv ON fv.id = s.file_version_id
        LEFT JOIN files f ON f.id = fv.file_id
        LEFT JOIN projects p ON p.id = s.project_id
        """;

    // When scoped, an inner JOIN to snapshot_projects restricts every SelectBase-based read to the
    // selected snapshot's project versions (criterion 6). Unscoped, it is exactly SelectBase.
    private string SelectPrefix => SelectBase + Scope.Join("s.project_id");

    private const string InsertSql = """
        INSERT INTO symbols (project_id, symbol_key, fully_qualified_name, display_name, kind, accessibility,
            is_static, is_abstract, is_virtual, is_override, signature, signature_hash, declaration,
            doc_comment, file_version_id, line_start, line_end, attributes, last_indexed_at)
        VALUES (?1, ?2, ?3, ?4, ?5, ?6,
            ?7, ?8, ?9, ?10, ?11, ?12, ?13,
            ?14, ?15, ?16, ?17, ?18, ?19)
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
            declaration = excluded.declaration,
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
    /// <see cref="Insert(PreparedInsert, SymbolInfo)"/> so SQLite keeps one compiled statement, and
    /// disposes it when the batch is done.
    /// </summary>
    public PreparedInsert CreateInsertCommand() => new(connection, InsertSql);

    public long Insert(SymbolInfo symbol)
    {
        using var cmd = CreateInsertCommand();
        return Insert(cmd, symbol);
    }

    public long Insert(PreparedInsert cmd, SymbolInfo symbol)
    {
        var fileVersionId = FilesOrDefault.ResolveFileVersionId(
            symbol.ProjectId, symbol.FilePath, contentHash: null, lastIndexedAt: symbol.LastIndexedAt);
        Bind(cmd, symbol, fileVersionId);
        return cmd.ExecuteReturningId();
    }

    private static void Bind(PreparedInsert cmd, SymbolInfo symbol, long fileVersionId)
    {
        cmd.Bind(1, symbol.ProjectId);
        cmd.Bind(2, symbol.SymbolKey);
        cmd.Bind(3, symbol.FullyQualifiedName);
        cmd.Bind(4, symbol.DisplayName);
        cmd.Bind(5, (int)symbol.Kind);
        cmd.Bind(6, (int)symbol.Accessibility);
        cmd.Bind(7, symbol.IsStatic ? 1 : 0);
        cmd.Bind(8, symbol.IsAbstract ? 1 : 0);
        cmd.Bind(9, symbol.IsVirtual ? 1 : 0);
        cmd.Bind(10, symbol.IsOverride ? 1 : 0);
        cmd.Bind(11, symbol.Signature);
        cmd.Bind(12, symbol.SignatureHash);
        cmd.Bind(13, symbol.Declaration);
        cmd.Bind(14, symbol.DocComment);
        cmd.Bind(15, fileVersionId);
        cmd.Bind(16, symbol.LineStart);
        cmd.Bind(17, symbol.LineEnd);
        cmd.Bind(18, symbol.Attributes);
        cmd.Bind(19, symbol.LastIndexedAt);
    }

    public SymbolInfo? GetById(long id)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SelectPrefix + " WHERE s.id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        Scope.Bind(cmd);
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
        Scope.Bind(cmd);

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
        Scope.Bind(cmd);
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

    /// <summary>
    /// The symbols of every file whose stored <c>files.repo_relative_path</c> is one of
    /// <paramref name="storedPaths"/> (issue #145), in the scoped project versions (<see cref="GetScopedProjectIds"/>).
    /// A caller's repository-relative path is passed with its whole-segment suffixes, because a submodule's files are
    /// stored relative to the submodule's own root; the caller confirms each hit against the full path it asked for.
    /// </summary>
    public List<SymbolInfo> GetByStoredRelativePaths(IReadOnlyList<string> storedPaths)
    {
        if (storedPaths.Count == 0)
            return [];
        var pids = ProjectIdsJson(null);
        if (pids is null)
            return [];
        using var cmd = connection.CreateCommand();
        cmd.CommandText = ByStoredRelativePathsSql;
        cmd.Parameters.AddWithValue("@pids", pids);
        cmd.Parameters.AddWithValue("@paths", System.Text.Json.JsonSerializer.Serialize(storedPaths));
        return ReadAll(cmd);
    }

    // One constant statement whatever the number of candidate paths: both lists are bound as JSON arrays.
    private const string ByStoredRelativePathsSql = $"""
        {SelectBase}
        WHERE f.repo_relative_path IN (SELECT value FROM json_each(@paths))
          AND s.project_id IN (SELECT value FROM json_each(@pids));
        """;

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

    /// <summary>
    /// The (symbol_key -> symbol id) pairs for every symbol owned by a project version. Phase 12 uses
    /// this to re-populate the in-run <see cref="Sextant.Core.SymbolCatalog"/> when a later parent REUSES
    /// an already-indexed submodule provider project version (its symbols are not re-extracted this run),
    /// so the parent's occurrence resolution can still bind cross-repo targets to the provider's stable
    /// keys. The declaration key is unique per (project, symbol_key), so each key maps to one id.
    /// </summary>
    public List<(string symbolKey, long id)> GetKeyIdPairsByProject(long projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT symbol_key, id FROM symbols WHERE project_id = @project_id;";
        cmd.Parameters.AddWithValue("@project_id", projectId);
        var pairs = new List<(string, long)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            pairs.Add((reader.GetString(0), reader.GetInt64(1)));
        return pairs;
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

    // ==== symbol lookup by name / key (the MCP symbol resolver) ========================================

    // The SelectBase columns and joins after `FROM symbols s`, for queries that must drive the symbols table through a
    // chosen index (`INDEXED BY` must follow the table name, before the joins).
    private const string SelectColumns = """
        SELECT s.id, s.project_id, s.symbol_key, s.fully_qualified_name, s.display_name, s.kind,
               s.accessibility, s.is_static, s.is_abstract, s.is_virtual, s.is_override, s.signature,
               s.signature_hash, s.declaration, s.doc_comment, s.line_start, s.line_end, s.attributes, s.last_indexed_at,
               f.repo_relative_path AS repo_relative_path, p.disk_path AS disk_path,
               p.repo_relative_path AS project_repo_relative
        """;

    private const string SymbolJoins = """

        LEFT JOIN file_versions fv ON fv.id = s.file_version_id
        LEFT JOIN files f ON f.id = fv.file_id
        LEFT JOIN projects p ON p.id = s.project_id
        """;

    /// <summary>
    /// The ids of the project versions this store's <see cref="Scope"/> reads (narrowed to
    /// <see cref="ProjectRestriction"/> when set), ascending (every project when unscoped). The name lookups below seek
    /// each one through <c>ix_symbols_project_name_nocase</c>.
    /// </summary>
    public List<long> GetScopedProjectIds()
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = Scope.ProjectIdsQuery;
        Scope.Bind(cmd);
        var ids = new List<long>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            if (ProjectRestriction is null || ProjectRestriction.Contains(id))
                ids.Add(id);
        }
        return ids;
    }

    /// <summary>
    /// The symbols whose <c>display_name</c> equals <paramref name="name"/> ignoring ASCII case, in the scoped project
    /// versions (narrowed to <paramref name="projectIds"/> when given) and, when <paramref name="kinds"/> is given, of
    /// those kinds; at most <paramref name="limit"/> rows ordered by (project, id). Each project is one seek of
    /// <c>ix_symbols_project_name_nocase</c>, so the cost follows the matches, not the table.
    /// </summary>
    public List<SymbolInfo> GetByDisplayName(
        string name, IReadOnlyCollection<SymbolKind>? kinds = null, IReadOnlyCollection<long>? projectIds = null,
        int limit = 1000)
    {
        ArgumentNullException.ThrowIfNull(name);
        var pids = ProjectIdsJson(projectIds);
        if (pids is null)
            return [];
        using var cmd = connection.CreateCommand();
        cmd.CommandText = ByDisplayNameSql;
        cmd.Parameters.AddWithValue("@pids", pids);
        cmd.Parameters.AddWithValue("@name", name);
        BindKinds(cmd, kinds);
        cmd.Parameters.AddWithValue("@limit", limit);
        return ReadAll(cmd);
    }

    private const string ByDisplayNameSql = $"""
        {SelectColumns}
        FROM json_each(@pids) __pid
        CROSS JOIN symbols s INDEXED BY ix_symbols_project_name_nocase{SymbolJoins}
        WHERE s.project_id = __pid.value
          AND s.display_name COLLATE NOCASE = @name{KindFilter}
        ORDER BY s.project_id, s.id
        LIMIT @limit;
        """;

    /// <summary>
    /// The symbols whose <c>display_name</c> starts with <paramref name="prefix"/> ignoring ASCII case (the
    /// <see cref="NoCasePrefixRange"/> of the prefix, one index range per project), narrowed like
    /// <see cref="GetByDisplayName"/>; at most <paramref name="limit"/> rows.
    /// </summary>
    public List<SymbolInfo> GetByDisplayNamePrefix(
        string prefix, IReadOnlyCollection<SymbolKind>? kinds = null, IReadOnlyCollection<long>? projectIds = null,
        int limit = 50)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        var pids = ProjectIdsJson(projectIds);
        if (pids is null || prefix.Length == 0)
            return [];
        var range = NoCasePrefixRange.Of(prefix);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = range.Hi is null ? ByDisplayNameFromSql : ByDisplayNameRangeSql;
        cmd.Parameters.AddWithValue("@pids", pids);
        cmd.Parameters.AddWithValue("@lo", range.Lo);
        if (range.Hi is not null)
            cmd.Parameters.AddWithValue("@hi", range.Hi);
        BindKinds(cmd, kinds);
        cmd.Parameters.AddWithValue("@limit", limit);
        return ReadAll(cmd);
    }

    // The prefix's NOCASE range [@lo, @hi); a prefix with no upper bound (NoCasePrefixRange.Hi null) reads from @lo on.
    private const string ByDisplayNameRangeSql = $"""
        {SelectColumns}
        FROM json_each(@pids) __pid
        CROSS JOIN symbols s INDEXED BY ix_symbols_project_name_nocase{SymbolJoins}
        WHERE s.project_id = __pid.value
          AND s.display_name COLLATE NOCASE >= @lo
          AND s.display_name COLLATE NOCASE < @hi{KindFilter}
        ORDER BY s.project_id, s.id
        LIMIT @limit;
        """;

    private const string ByDisplayNameFromSql = $"""
        {SelectColumns}
        FROM json_each(@pids) __pid
        CROSS JOIN symbols s INDEXED BY ix_symbols_project_name_nocase{SymbolJoins}
        WHERE s.project_id = __pid.value
          AND s.display_name COLLATE NOCASE >= @lo{KindFilter}
        ORDER BY s.project_id, s.id
        LIMIT @limit;
        """;

    /// <summary>
    /// The symbols of <paramref name="projectId"/> whose <c>symbol_key</c> starts with <paramref name="keyPrefix"/>
    /// (binary, case-sensitive), ordered by key; at most <paramref name="limit"/> rows, and none when the project is
    /// outside the scoped project versions (<see cref="GetScopedProjectIds"/>). One range seek of the unique
    /// <c>(project_id, symbol_key)</c> index. With documentation-ID keys, <c>M:Ns.Type.</c> covers every method of the
    /// type across all its partial declarations; the caller filters nested members out.
    /// </summary>
    public List<SymbolInfo> GetByKeyPrefix(long projectId, string keyPrefix, int limit = 5000)
    {
        ArgumentException.ThrowIfNullOrEmpty(keyPrefix);
        var last = keyPrefix[^1];
        if (char.IsSurrogate(last) || last == char.MaxValue)
            return [];
        var pids = ProjectIdsJson([projectId]);
        if (pids is null)
            return [];
        var hi = keyPrefix[..^1] + (char)(last + 1);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = ByKeyPrefixSql;
        cmd.Parameters.AddWithValue("@pids", pids);
        cmd.Parameters.AddWithValue("@lo", keyPrefix);
        cmd.Parameters.AddWithValue("@hi", hi);
        cmd.Parameters.AddWithValue("@limit", limit);
        return ReadAll(cmd);
    }

    private const string ByKeyPrefixSql = $"""
        {SelectColumns}
        FROM json_each(@pids) __pid
        CROSS JOIN symbols s INDEXED BY ix_symbols_key{SymbolJoins}
        WHERE s.project_id = __pid.value
          AND s.symbol_key >= @lo AND s.symbol_key < @hi
        ORDER BY s.symbol_key
        LIMIT @limit;
        """;

    /// <summary>
    /// The symbols whose <c>symbol_key</c> is exactly <paramref name="symbolKey"/> in the scoped project versions
    /// (narrowed to <paramref name="projectIds"/> when given), ordered by project: one row per project version that
    /// declares it.
    /// </summary>
    public List<SymbolInfo> GetBySymbolKeyInScope(string symbolKey, IReadOnlyCollection<long>? projectIds = null)
    {
        ArgumentNullException.ThrowIfNull(symbolKey);
        var pids = ProjectIdsJson(projectIds);
        if (pids is null)
            return [];
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"""
            {SelectColumns}
            FROM json_each(@pids) __pid
            CROSS JOIN symbols s INDEXED BY ix_symbols_key{SymbolJoins}
            WHERE s.project_id = __pid.value AND s.symbol_key = @symbol_key
            ORDER BY s.project_id, s.id;
            """;
        cmd.Parameters.AddWithValue("@pids", pids);
        cmd.Parameters.AddWithValue("@symbol_key", symbolKey);
        return ReadAll(cmd);
    }

    // The JSON array of project ids a name/key lookup seeks: the scoped project versions, intersected with
    // `restriction` when given; null when that leaves none (the lookup then reads nothing). Scoping by the explicit id
    // list keeps the lookups on their index whatever the scope's own join would make the planner do.
    private string? ProjectIdsJson(IReadOnlyCollection<long>? restriction)
    {
        IEnumerable<long> ids = GetScopedProjectIds();
        if (restriction is not null)
        {
            var allowed = restriction as IReadOnlySet<long> ?? restriction.ToHashSet();
            ids = ids.Where(allowed.Contains);
        }
        var list = ids.ToList();
        return list.Count == 0 ? null : "[" + string.Join(",", list) + "]";
    }

    // A kind filter bound as a JSON array of SymbolKind ordinals (@kinds NULL = every kind), so the lookups stay one
    // constant statement whatever the filter.
    private const string KindFilter = "\n      AND (@kinds IS NULL OR s.kind IN (SELECT value FROM json_each(@kinds)))";

    private static void BindKinds(SqliteCommand cmd, IReadOnlyCollection<SymbolKind>? kinds) =>
        cmd.Parameters.AddWithValue("@kinds", kinds is null || kinds.Count == 0
            ? (object)DBNull.Value
            : "[" + string.Join(",", kinds.Select(k => (int)k).Distinct().Order()) + "]");

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
        cmd.CommandText = $"SELECT fully_qualified_name FROM symbols WHERE kind IN ({kindList}){projectClause}{Scope.And("project_id")};";
        if (projectId.HasValue)
            cmd.Parameters.AddWithValue("@projectId", projectId.Value);
        Scope.Bind(cmd);

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
        var results = GetByAttributeFragment(attributeFqn);
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
    /// The symbols whose attribute list contains <paramref name="fragment"/> anywhere (case-insensitive for ASCII), a
    /// pre-filter the caller narrows by parsing <see cref="SymbolInfo.Attributes"/>.
    /// </summary>
    public List<SymbolInfo> GetByAttributeFragment(string fragment)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SelectPrefix + " WHERE s.attributes LIKE '%' || @attr || '%' ORDER BY s.fully_qualified_name, s.id;";
        cmd.Parameters.AddWithValue("@attr", fragment);
        return ReadAll(cmd);
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

        // A coarse prefilter over the printed signature (the declaration when recorded, migration 026, else the
        // legacy display): any row whose text contains the pattern. The caller decides on the parsed signature.
        if (returnTypePattern != null)
        {
            sql.Append(" AND COALESCE(s.declaration, s.signature) LIKE @return_type_pattern");
            cmd.Parameters.AddWithValue("@return_type_pattern", $"%{returnTypePattern}%");
        }

        if (paramTypePattern != null)
        {
            sql.Append(" AND COALESCE(s.declaration, s.signature) LIKE @param_type_pattern");
            cmd.Parameters.AddWithValue("@param_type_pattern", $"%{paramTypePattern}%");
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

    private List<SymbolInfo> ReadAll(SqliteCommand cmd)
    {
        Scope.Bind(cmd);
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
            Declaration = reader.IsDBNull(reader.GetOrdinal("declaration")) ? null : reader.GetString(reader.GetOrdinal("declaration")),
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
    // an unrecognized filter matches nothing (matching the pre-Phase-7 text-equality behavior). Public
    // (like FormatAccessibility) so a federated remote-base row can be kind-filtered with the exact same
    // parse the FTS query uses.
    public static int KindNameToInt(string name)
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
