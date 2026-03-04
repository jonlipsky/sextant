using Sextant.Core;
using Microsoft.Data.Sqlite;

namespace Sextant.Store;

public sealed class SymbolStore(SqliteConnection connection)
{
    public long Insert(SymbolInfo symbol)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO symbols (project_id, fully_qualified_name, display_name, kind, accessibility,
                is_static, is_abstract, is_virtual, is_override, signature, signature_hash,
                doc_comment, file_path, line_start, line_end, attributes, last_indexed_at)
            VALUES (@project_id, @fqn, @display_name, @kind, @accessibility,
                @is_static, @is_abstract, @is_virtual, @is_override, @signature, @signature_hash,
                @doc_comment, @file_path, @line_start, @line_end, @attributes, @last_indexed_at)
            ON CONFLICT(fully_qualified_name, project_id) DO UPDATE SET
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
        cmd.Parameters.AddWithValue("@project_id", symbol.ProjectId);
        cmd.Parameters.AddWithValue("@fqn", symbol.FullyQualifiedName);
        cmd.Parameters.AddWithValue("@display_name", symbol.DisplayName);
        cmd.Parameters.AddWithValue("@kind", symbol.Kind.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("@accessibility", FormatAccessibility(symbol.Accessibility));
        cmd.Parameters.AddWithValue("@is_static", symbol.IsStatic ? 1 : 0);
        cmd.Parameters.AddWithValue("@is_abstract", symbol.IsAbstract ? 1 : 0);
        cmd.Parameters.AddWithValue("@is_virtual", symbol.IsVirtual ? 1 : 0);
        cmd.Parameters.AddWithValue("@is_override", symbol.IsOverride ? 1 : 0);
        cmd.Parameters.AddWithValue("@signature", (object?)symbol.Signature ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@signature_hash", (object?)symbol.SignatureHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@doc_comment", (object?)symbol.DocComment ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@file_path", symbol.FilePath);
        cmd.Parameters.AddWithValue("@line_start", symbol.LineStart);
        cmd.Parameters.AddWithValue("@line_end", symbol.LineEnd);
        cmd.Parameters.AddWithValue("@attributes", (object?)symbol.Attributes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@last_indexed_at", symbol.LastIndexedAt);

        return (long)cmd.ExecuteScalar()!;
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

    public List<SymbolInfo> GetByFile(string filePath)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM symbols WHERE file_path = @file_path;";
        cmd.Parameters.AddWithValue("@file_path", filePath);
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
