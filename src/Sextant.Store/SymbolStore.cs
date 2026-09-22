using Sextant.Core;
using Microsoft.Data.Sqlite;

namespace Sextant.Store;

public sealed class SymbolStore(SqliteConnection connection)
{
    private const string InsertSql = """
        INSERT INTO symbols (project_id, symbol_key, fully_qualified_name, display_name, kind, accessibility,
            is_static, is_abstract, is_virtual, is_override, signature, signature_hash,
            doc_comment, file_path, line_start, line_end, attributes, last_indexed_at)
        VALUES (@project_id, @symbol_key, @fqn, @display_name, @kind, @accessibility,
            @is_static, @is_abstract, @is_virtual, @is_override, @signature, @signature_hash,
            @doc_comment, @file_path, @line_start, @line_end, @attributes, @last_indexed_at)
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
            file_path = excluded.file_path,
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
        Bind(cmd, symbol);
        return (long)cmd.ExecuteScalar()!;
    }

    private static void Bind(SqliteCommand cmd, SymbolInfo symbol)
    {
        SqlParam.Set(cmd, "@project_id", symbol.ProjectId);
        SqlParam.Set(cmd, "@symbol_key", symbol.SymbolKey);
        SqlParam.Set(cmd, "@fqn", symbol.FullyQualifiedName);
        SqlParam.Set(cmd, "@display_name", symbol.DisplayName);
        SqlParam.Set(cmd, "@kind", symbol.Kind.ToString().ToLowerInvariant());
        SqlParam.Set(cmd, "@accessibility", FormatAccessibility(symbol.Accessibility));
        SqlParam.Set(cmd, "@is_static", symbol.IsStatic ? 1 : 0);
        SqlParam.Set(cmd, "@is_abstract", symbol.IsAbstract ? 1 : 0);
        SqlParam.Set(cmd, "@is_virtual", symbol.IsVirtual ? 1 : 0);
        SqlParam.Set(cmd, "@is_override", symbol.IsOverride ? 1 : 0);
        SqlParam.Set(cmd, "@signature", symbol.Signature);
        SqlParam.Set(cmd, "@signature_hash", symbol.SignatureHash);
        SqlParam.Set(cmd, "@doc_comment", symbol.DocComment);
        SqlParam.Set(cmd, "@file_path", symbol.FilePath);
        SqlParam.Set(cmd, "@line_start", symbol.LineStart);
        SqlParam.Set(cmd, "@line_end", symbol.LineEnd);
        SqlParam.Set(cmd, "@attributes", symbol.Attributes);
        SqlParam.Set(cmd, "@last_indexed_at", symbol.LastIndexedAt);
    }

    public SymbolInfo? GetById(long id)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM symbols WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadSymbol(reader) : null;
    }

    public SymbolInfo? GetByFqn(string fullyQualifiedName, long? projectId = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = projectId.HasValue
            ? "SELECT * FROM symbols WHERE fully_qualified_name = @fqn AND project_id = @project_id;"
            : "SELECT * FROM symbols WHERE fully_qualified_name = @fqn;";
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
        cmd.CommandText = "SELECT * FROM symbols WHERE symbol_key = @symbol_key AND project_id = @project_id;";
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
            ? "SELECT * FROM symbols WHERE fully_qualified_name = @fqn AND project_id = @project_id ORDER BY project_id, symbol_key, id;"
            : "SELECT * FROM symbols WHERE fully_qualified_name = @fqn ORDER BY project_id, symbol_key, id;";
        cmd.Parameters.AddWithValue("@fqn", fullyQualifiedName);
        if (projectId.HasValue)
            cmd.Parameters.AddWithValue("@project_id", projectId.Value);
        return ReadAll(cmd);
    }

    public List<SymbolInfo> GetByFile(string filePath)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM symbols WHERE file_path = @file_path;";
        cmd.Parameters.AddWithValue("@file_path", filePath);
        return ReadAll(cmd);
    }

    // Project-scoped variant: a source file that is shared across the evaluated target frameworks of a
    // multi-targeted project appears once per logical (per-TFM) project, so callers that re-index or
    // resolve within one framework must restrict to that project rather than matching every variant.
    public List<SymbolInfo> GetByFile(string filePath, long projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM symbols WHERE file_path = @file_path AND project_id = @project_id;";
        cmd.Parameters.AddWithValue("@file_path", filePath);
        cmd.Parameters.AddWithValue("@project_id", projectId);
        return ReadAll(cmd);
    }

    public List<SymbolInfo> GetByProjectAndAccessibility(long projectId, string accessibility)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM symbols WHERE project_id = @project_id AND accessibility = @accessibility;";
        cmd.Parameters.AddWithValue("@project_id", projectId);
        cmd.Parameters.AddWithValue("@accessibility", accessibility);
        return ReadAll(cmd);
    }

    public List<SymbolInfo> GetByProjectAndAccessibility(long projectId, string[] accessibilities)
    {
        using var cmd = connection.CreateCommand();
        var placeholders = string.Join(", ", accessibilities.Select((_, i) => $"@acc{i}"));
        cmd.CommandText = $"SELECT * FROM symbols WHERE project_id = @project_id AND accessibility IN ({placeholders});";
        cmd.Parameters.AddWithValue("@project_id", projectId);
        for (var i = 0; i < accessibilities.Length; i++)
            cmd.Parameters.AddWithValue($"@acc{i}", accessibilities[i]);
        return ReadAll(cmd);
    }

    public List<SymbolInfo> SearchFts(string query, int maxResults, string? kindFilter = null)
    {
        using var cmd = connection.CreateCommand();
        var kindClause = kindFilter != null ? " AND s.kind = @kind" : "";
        cmd.CommandText = $"""
            SELECT s.* FROM symbols_fts fts
            JOIN symbols s ON s.id = fts.rowid
            WHERE symbols_fts MATCH @query{kindClause}
            ORDER BY rank
            LIMIT @max_results;
            """;
        cmd.Parameters.AddWithValue("@query", query);
        cmd.Parameters.AddWithValue("@max_results", maxResults);
        if (kindFilter != null)
            cmd.Parameters.AddWithValue("@kind", kindFilter);
        return ReadAll(cmd);
    }

    public List<string> GetAllTypeFqns(long? projectId = null)
    {
        using var cmd = connection.CreateCommand();
        var projectClause = projectId.HasValue ? " AND project_id = @projectId" : "";
        cmd.CommandText = $"SELECT fully_qualified_name FROM symbols WHERE kind IN ('class','interface','struct','enum','delegate','record'){projectClause};";
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
        var projectClause = projectId.HasValue ? " AND project_id = @projectId" : "";
        cmd.CommandText = $"SELECT * FROM symbols WHERE fully_qualified_name LIKE @prefix || '%'{projectClause};";
        cmd.Parameters.AddWithValue("@prefix", prefix);
        if (projectId.HasValue)
            cmd.Parameters.AddWithValue("@projectId", projectId.Value);
        return ReadAll(cmd);
    }

    public List<SymbolInfo> GetByAttribute(string attributeFqn)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM symbols WHERE attributes LIKE '%' || @attr || '%';";
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

    public List<SymbolInfo> SearchBySignature(
        string? returnTypePattern, string? paramTypePattern,
        string? kind, long? projectId, int maxResults)
    {
        using var cmd = connection.CreateCommand();
        var sql = new System.Text.StringBuilder("SELECT * FROM symbols WHERE 1=1");

        if (kind != null)
        {
            sql.Append(" AND kind = @kind");
            cmd.Parameters.AddWithValue("@kind", kind);
        }
        else
        {
            sql.Append(" AND kind IN ('method', 'constructor')");
        }

        if (returnTypePattern != null)
        {
            sql.Append(" AND signature LIKE @return_type_pattern");
            cmd.Parameters.AddWithValue("@return_type_pattern", $"%{returnTypePattern} %");
        }

        if (paramTypePattern != null)
        {
            sql.Append(" AND signature LIKE @param_type_pattern");
            cmd.Parameters.AddWithValue("@param_type_pattern", $"%(%{paramTypePattern}%");
        }

        if (projectId.HasValue)
        {
            sql.Append(" AND project_id = @projectId");
            cmd.Parameters.AddWithValue("@projectId", projectId.Value);
        }

        sql.Append(" LIMIT @max_results");
        cmd.Parameters.AddWithValue("@max_results", maxResults);

        cmd.CommandText = sql.ToString();
        return ReadAll(cmd);
    }

    public void DeleteByFile(string filePath)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM symbols WHERE file_path = @file_path;";
        cmd.Parameters.AddWithValue("@file_path", filePath);
        cmd.ExecuteNonQuery();
    }

    // Project-scoped delete: only clears this logical (per-TFM) project's symbols for the file, so
    // re-indexing one framework of a multi-targeted project does not delete the sibling framework's
    // symbols declared in the same shared source file.
    public void DeleteByFile(string filePath, long projectId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM symbols WHERE file_path = @file_path AND project_id = @project_id;";
        cmd.Parameters.AddWithValue("@file_path", filePath);
        cmd.Parameters.AddWithValue("@project_id", projectId);
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
        return new SymbolInfo
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            ProjectId = reader.GetInt64(reader.GetOrdinal("project_id")),
            SymbolKey = reader.GetString(reader.GetOrdinal("symbol_key")),
            FullyQualifiedName = reader.GetString(reader.GetOrdinal("fully_qualified_name")),
            DisplayName = reader.GetString(reader.GetOrdinal("display_name")),
            Kind = Enum.Parse<SymbolKind>(reader.GetString(reader.GetOrdinal("kind")), ignoreCase: true),
            Accessibility = ParseAccessibility(reader.GetString(reader.GetOrdinal("accessibility"))),
            IsStatic = reader.GetInt64(reader.GetOrdinal("is_static")) != 0,
            IsAbstract = reader.GetInt64(reader.GetOrdinal("is_abstract")) != 0,
            IsVirtual = reader.GetInt64(reader.GetOrdinal("is_virtual")) != 0,
            IsOverride = reader.GetInt64(reader.GetOrdinal("is_override")) != 0,
            Signature = reader.IsDBNull(reader.GetOrdinal("signature")) ? null : reader.GetString(reader.GetOrdinal("signature")),
            SignatureHash = reader.IsDBNull(reader.GetOrdinal("signature_hash")) ? null : reader.GetString(reader.GetOrdinal("signature_hash")),
            DocComment = reader.IsDBNull(reader.GetOrdinal("doc_comment")) ? null : reader.GetString(reader.GetOrdinal("doc_comment")),
            FilePath = reader.GetString(reader.GetOrdinal("file_path")),
            LineStart = reader.GetInt32(reader.GetOrdinal("line_start")),
            LineEnd = reader.GetInt32(reader.GetOrdinal("line_end")),
            Attributes = reader.IsDBNull(reader.GetOrdinal("attributes")) ? null : reader.GetString(reader.GetOrdinal("attributes")),
            LastIndexedAt = reader.GetInt64(reader.GetOrdinal("last_indexed_at"))
        };
    }

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
