using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Sextant.Store;

/// <summary>The operation an <see cref="AuditEntry"/> records (migration 020 <c>audit_log.action</c>).</summary>
public static class AuditAction
{
    public const string Ensure = "ensure";
    public const string Contribute = "contribute";
    public const string Retention = "retention";

    /// <summary>RESERVED vocabulary (not emitted today — see <see cref="AuditOutcome.Denied"/>).</summary>
    public const string Resolve = "resolve";

    /// <summary>RESERVED vocabulary (not emitted today — see <see cref="AuditOutcome.Denied"/>).</summary>
    public const string Query = "query";

    public const string Backup = "backup";
    public const string Restore = "restore";
    public const string Reconcile = "reconcile";
}

/// <summary>The outcome an <see cref="AuditEntry"/> records (migration 020 <c>audit_log.outcome</c>).</summary>
public static class AuditOutcome
{
    /// <summary>The request passed authorization and was accepted for processing.</summary>
    public const string Accepted = "accepted";

    /// <summary>
    /// RESERVED vocabulary for a future out-of-band (non-writer-path) audit sink. An authorization refusal
    /// would be recorded here WITHOUT confirming the target exists (criterion 1). It is deliberately NOT
    /// emitted from the auth-middleware hot path today: a durable write per unauthenticated request would
    /// drive the single writer into contention (a DoS amplifier), and the uniform-not-found denial already
    /// prevents an unauthorized caller from learning anything. See docs/runbooks.md.
    /// </summary>
    public const string Denied = "denied";

    public const string Complete = "complete";
    public const string Partial = "partial";
    public const string Failed = "failed";
    public const string Unsupported = "unsupported";

    /// <summary>An unexpected server-side error (not an authorization decision).</summary>
    public const string Error = "error";
}

/// <summary>One durable audit-log row (migration 020).</summary>
public sealed record AuditEntry
{
    public long Id { get; init; }
    public required long Ts { get; init; }
    public required string Action { get; init; }
    public string? Actor { get; init; }
    public string? RepositoryScope { get; init; }
    public required string Outcome { get; init; }
    public string? Detail { get; init; }
    public long? CostIndexMs { get; init; }
    public long? CostBytes { get; init; }
}

/// <summary>Per-repository cost attribution rolled up from the audit log (criterion 5: cost attribution).</summary>
public sealed record AuditCostAttribution
{
    public required string RepositoryScope { get; init; }
    public required long Events { get; init; }
    public required long IndexMs { get; init; }
    public required long Bytes { get; init; }
}

/// <summary>
/// The durable operational + security audit log (migration 020), owned by the standalone index service.
/// It records who did what to which repository scope, with what outcome and at what cost, backing the
/// security audit trail, cost attribution, and durable success/completeness history that criterion 5
/// requires.
///
/// CRITERION-1 LEAKAGE CONTRACT: audit rows are keyed by a repository scope and so reveal
/// repository/snapshot existence and cross-tenant counts. They are OPERATOR-ONLY — every read path is
/// gated behind the CONTROL token in <c>ServiceApp</c>, never the query token, so a query-plane tenant
/// can never read another tenant's rows. The <see cref="HashActor"/> helper stores a non-reversible hash
/// of the presented token/principal, never the raw secret. This is a thin SQL store that enrols in the
/// caller's ambient write transaction — the service owns the writer lease and transaction boundaries.
/// </summary>
public sealed class AuditLogStore(SqliteConnection connection)
{
    /// <summary>
    /// Appends one audit row. <paramref name="actor"/> should already be a hash (see <see cref="HashActor"/>);
    /// callers pass the hashed principal so a raw token never reaches the store. Returns the new row id.
    /// </summary>
    public long Append(
        string action,
        string outcome,
        string? actor = null,
        string? repositoryScope = null,
        string? detail = null,
        long? costIndexMs = null,
        long? costBytes = null,
        long? timestampMs = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO audit_log
                (ts, action, actor, repository_scope, outcome, detail, cost_index_ms, cost_bytes)
            VALUES (@ts, @action, @actor, @scope, @outcome, @detail, @cost_ms, @cost_bytes)
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("@ts", timestampMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("@action", action);
        cmd.Parameters.AddWithValue("@actor", (object?)actor ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@scope", (object?)repositoryScope ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@outcome", outcome);
        cmd.Parameters.AddWithValue("@detail", (object?)detail ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cost_ms", (object?)costIndexMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cost_bytes", (object?)costBytes ?? DBNull.Value);
        return Convert.ToInt64(cmd.ExecuteScalar()!);
    }

    /// <summary>
    /// Returns the most recent audit rows (newest first), optionally filtered by action and/or repository
    /// scope. OPERATOR-ONLY read (control-token gated in the host). <paramref name="limit"/> is clamped to
    /// a sane bound so an operator query can never scan the whole table unbounded.
    /// </summary>
    public IReadOnlyList<AuditEntry> Recent(
        int limit = 100, string? action = null, string? repositoryScope = null, long? sinceTs = null)
    {
        var bounded = Math.Clamp(limit, 1, 1000);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, ts, action, actor, repository_scope, outcome, detail, cost_index_ms, cost_bytes
            FROM audit_log
            WHERE (@action IS NULL OR action = @action)
              AND (@scope IS NULL OR repository_scope = @scope)
              AND (@since IS NULL OR ts >= @since)
            ORDER BY id DESC
            LIMIT @limit;
            """;
        cmd.Parameters.AddWithValue("@action", (object?)action ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@scope", (object?)repositoryScope ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@since", (object?)sinceTs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@limit", bounded);
        var rows = new List<AuditEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add(Read(reader));
        return rows;
    }

    /// <summary>
    /// Rolls up cost attribution per repository scope over the audit log (criterion 5). OPERATOR-ONLY —
    /// aggregates cross-tenant counts, so it is control-token gated. Rows with no scope (service-wide
    /// actions) are excluded.
    /// </summary>
    public IReadOnlyList<AuditCostAttribution> CostByRepository(long? sinceTs = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT repository_scope,
                   COUNT(*),
                   COALESCE(SUM(cost_index_ms), 0),
                   COALESCE(SUM(cost_bytes), 0)
            FROM audit_log
            WHERE repository_scope IS NOT NULL
              AND (@since IS NULL OR ts >= @since)
            GROUP BY repository_scope
            ORDER BY SUM(cost_index_ms) DESC;
            """;
        cmd.Parameters.AddWithValue("@since", (object?)sinceTs ?? DBNull.Value);
        var rows = new List<AuditCostAttribution>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add(new AuditCostAttribution
            {
                RepositoryScope = reader.GetString(0),
                Events = reader.GetInt64(1),
                IndexMs = reader.GetInt64(2),
                Bytes = reader.GetInt64(3)
            });
        return rows;
    }

    /// <summary>Total audit rows (operator health signal).</summary>
    public long Count()
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM audit_log;";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>
    /// True when at least one audit row exists for <paramref name="action"/> (optionally requiring a given
    /// <paramref name="outcome"/> and a minimum timestamp). Used to derive durable operational facts from
    /// the audit trail — e.g. whether a successful backup has been recorded — instead of trusting a caller-
    /// supplied flag.
    /// </summary>
    public bool HasAction(string action, string? outcome = null, long? sinceTs = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM audit_log
                WHERE action = @action
                  AND (@outcome IS NULL OR outcome = @outcome)
                  AND (@since IS NULL OR ts >= @since));
            """;
        cmd.Parameters.AddWithValue("@action", action);
        cmd.Parameters.AddWithValue("@outcome", (object?)outcome ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@since", (object?)sinceTs ?? DBNull.Value);
        return Convert.ToInt64(cmd.ExecuteScalar()) != 0;
    }

    /// <summary>
    /// Produces the NON-REVERSIBLE actor hash stored in <c>audit_log.actor</c> from a presented
    /// token/principal. A SHA-256 hex digest keyed by a fixed domain-separation prefix — enough to
    /// correlate a principal's actions across rows without ever persisting the raw secret. Returns null
    /// for a null/blank principal (the zero-policy local/dev path).
    /// </summary>
    public static string? HashActor(string? principal)
    {
        if (string.IsNullOrWhiteSpace(principal))
            return null;
        var bytes = Encoding.UTF8.GetBytes("sextant-audit-actor\u0000" + principal);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static AuditEntry Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Ts = reader.GetInt64(1),
        Action = reader.GetString(2),
        Actor = reader.IsDBNull(3) ? null : reader.GetString(3),
        RepositoryScope = reader.IsDBNull(4) ? null : reader.GetString(4),
        Outcome = reader.GetString(5),
        Detail = reader.IsDBNull(6) ? null : reader.GetString(6),
        CostIndexMs = reader.IsDBNull(7) ? null : reader.GetInt64(7),
        CostBytes = reader.IsDBNull(8) ? null : reader.GetInt64(8)
    };
}
