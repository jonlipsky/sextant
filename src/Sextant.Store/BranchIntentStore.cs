using Microsoft.Data.Sqlite;

namespace Sextant.Store;

public sealed record BranchIntent(
    long Id,
    long RepositoryId,
    string Branch,
    string Kind,
    long? QueuedEnsureId,
    string? AdmissionId,
    string? PayloadJson,
    string State,
    string? Outcome,
    long AdmittedAt,
    long? SettledAt,
    string? ClaimedByTurn = null,
    int Attempts = 0,
    long? NextAttemptAt = null,
    string? LastError = null,
    long? FollowingRefreshId = null);

/// <summary>
/// Persists each branch's intent queue within the caller's transaction (issue #273, rule 3). The queue is a strict FIFO
/// in admission (id) order: only its first unsettled intent runs, and no intent supersedes another. The only coalescing
/// (rule 2) is a refresh admitted directly behind a pending, unclaimed refresh that is not waiting out a retry deadline:
/// it is absorbed into that refresh.
/// </summary>
public sealed class BranchIntentStore(SqliteConnection connection)
{
    public BranchIntent Admit(
        long repositoryId,
        string branch,
        string kind,
        long? queuedEnsureId,
        string? admissionId,
        string? payloadJson,
        long admittedAt)
    {
        if (kind is not ("ensure" or "refresh" or "retire" or "delete"))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported branch intent kind.");
        admissionId ??= NewAdmissionId();

        // Rule 2: a refresh admitted directly behind a pending, unclaimed refresh that is waiting for its turn is absorbed
        // into it: both callers share that one admission (and its id). A claimed refresh, or one waiting out a retry
        // deadline, is never absorbed into; the new refresh queues behind it.
        // The absorbed push is recorded as the refresh's latest admission (admitted_at): an upstream deletion yields to
        // it (rule 5) and the refresh's maximum age counts from it (rule 7). Order is by id, so this moves nothing.
        if (kind == "refresh"
            && Tail(repositoryId, branch) is { Kind: "refresh", State: "pending", ClaimedByTurn: null } tail
            && !(tail.NextAttemptAt > admittedAt))
        {
            using var touch = connection.CreateCommand();
            touch.CommandText = "UPDATE branch_intents SET admitted_at = MAX(admitted_at, @at) WHERE id = @id;";
            touch.Parameters.AddWithValue("@id", tail.Id);
            touch.Parameters.AddWithValue("@at", admittedAt);
            touch.ExecuteNonQuery();
            return Get(tail.Id) ?? tail;
        }

        long id;
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO branch_intents
                    (repository_id, branch, kind, queued_ensure_id, admission_id, payload_json, state, admitted_at)
                VALUES (@repository, @branch, @kind, @ensure, @admission, @payload, 'pending', @at)
                RETURNING id;
                """;
            insert.Parameters.AddWithValue("@repository", repositoryId);
            insert.Parameters.AddWithValue("@branch", branch);
            insert.Parameters.AddWithValue("@kind", kind);
            insert.Parameters.AddWithValue("@ensure", (object?)queuedEnsureId ?? DBNull.Value);
            insert.Parameters.AddWithValue("@admission", (object?)admissionId ?? DBNull.Value);
            insert.Parameters.AddWithValue("@payload", (object?)payloadJson ?? DBNull.Value);
            insert.Parameters.AddWithValue("@at", admittedAt);
            id = Convert.ToInt64(insert.ExecuteScalar());
        }

        return Get(id) ?? throw new InvalidOperationException($"New branch intent {id} could not be read.");
    }

    public static string NewAdmissionId() => Guid.NewGuid().ToString("N");

    public BranchIntent? Get(long id) => ReadOne(SelectColumns + "\nWHERE id = @id;", command =>
        command.Parameters.AddWithValue("@id", id));

    public BranchIntent? GetByAdmissionId(string admissionId) => ReadOne(SelectColumns + "\nWHERE admission_id = @admission;", command =>
        command.Parameters.AddWithValue("@admission", admissionId));

    public BranchIntent? GetByQueuedEnsureId(long queuedEnsureId) => ReadOne(SelectColumns + "\nWHERE queued_ensure_id = @ensure;", command =>
        command.Parameters.AddWithValue("@ensure", queuedEnsureId));

    public IReadOnlyList<BranchIntent> ListUnsettled()
    {
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + """

            WHERE state <> 'settled'
            ORDER BY id;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<BranchIntent>();
        while (reader.Read())
            result.Add(Read(reader));
        return result;
    }

    /// <summary>Records <paramref name="refreshId"/> as ensure <paramref name="ensureId"/>'s own following refresh.</summary>
    public void SetFollowingRefresh(long ensureId, long refreshId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE branch_intents SET following_refresh_id = @refresh WHERE id = @id AND kind = 'ensure';
            """;
        command.Parameters.AddWithValue("@id", ensureId);
        command.Parameters.AddWithValue("@refresh", refreshId);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Issue #273 (rule 1): admits ensure <paramref name="ensure"/>'s own following refresh in the ensure's admission
    /// transaction, so it sits directly behind the ensure (the tail; never absorbed into another refresh), and records it
    /// as the ensure's following refresh. Returns the existing refresh when one is already recorded.
    /// </summary>
    public BranchIntent AdmitFollowingRefresh(BranchIntent ensure, string admissionId)
    {
        if (ensure.Kind != "ensure")
            throw new ArgumentException("Only an ensure has a following refresh.", nameof(ensure));
        if (ensure.FollowingRefreshId is long recorded)
            return Get(recorded) ?? throw new InvalidOperationException($"Following refresh {recorded} could not be read.");
        long id;
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO branch_intents
                    (repository_id, branch, kind, admission_id, state, admitted_at)
                VALUES (@repository, @branch, 'refresh', @admission, 'pending', @at)
                RETURNING id;
                """;
            insert.Parameters.AddWithValue("@repository", ensure.RepositoryId);
            insert.Parameters.AddWithValue("@branch", ensure.Branch);
            insert.Parameters.AddWithValue("@admission", admissionId);
            insert.Parameters.AddWithValue("@at", ensure.AdmittedAt);
            id = Convert.ToInt64(insert.ExecuteScalar());
        }
        SetFollowingRefresh(ensure.Id, id);
        return Get(id) ?? throw new InvalidOperationException($"Following refresh {id} could not be read.");
    }

    /// <summary>
    /// Issue #273 (D2): whether ensure intent <paramref name="intentId"/> is still unsettled and its branch's newest
    /// intent. The only intent allowed after it is its own following refresh, identified by the id recorded on the
    /// ensure (<c>following_refresh_id</c>). Any other later intent means an identical ensure must be admitted on its own after it.
    /// <para>
    /// Only the transaction that would admit a live identical ensure evaluates this, and its decision is final: a share
    /// admits no intent, and every admitted ensure intent takes its own turn. The queue is FIFO and nothing supersedes,
    /// so while the ensure is unsettled every intent admitted after it is unsettled too (a refresh absorbed into an
    /// earlier one leaves no row): no pruned row can hide one.
    /// </para>
    /// </summary>
    public bool IsNewestEnsureBefore(long intentId)
    {
        if (Get(intentId) is not { Kind: "ensure" } intent || intent.State == "settled")
            return false;
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1 FROM branch_intents later
                WHERE later.repository_id = @repository AND later.branch = @branch
                  AND later.id > @id
                  AND later.id IS NOT @following);
            """;
        command.Parameters.AddWithValue("@repository", intent.RepositoryId);
        command.Parameters.AddWithValue("@branch", intent.Branch);
        command.Parameters.AddWithValue("@id", intent.Id);
        command.Parameters.AddWithValue("@following", (object?)intent.FollowingRefreshId ?? DBNull.Value);
        return command.ExecuteScalar() is long found && found == 0;
    }

    /// <summary>The last unsettled intent of a branch in queue order (where a new admission would join).</summary>
    public BranchIntent? Tail(long repositoryId, string branch) => ReadOne(SelectColumns + "\n" + """
        WHERE repository_id = @repository AND branch = @branch AND state <> 'settled'
        ORDER BY id DESC LIMIT 1;
        """, command =>
    {
        command.Parameters.AddWithValue("@repository", repositoryId);
        command.Parameters.AddWithValue("@branch", branch);
    });

    /// <summary>
    /// Rule 5: whether any intent of the branch other than <paramref name="excludeId"/> was admitted at or after
    /// <paramref name="since"/>: a push admitted after the listing an upstream deletion relies on.
    /// </summary>
    public bool HasAdmissionSince(long repositoryId, string branch, long since, long? excludeId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (SELECT 1 FROM branch_intents
                           WHERE repository_id = @repository AND branch = @branch AND admitted_at >= @since
                             AND (@exclude IS NULL OR id <> @exclude));
            """;
        command.Parameters.AddWithValue("@repository", repositoryId);
        command.Parameters.AddWithValue("@branch", branch);
        command.Parameters.AddWithValue("@since", since);
        command.Parameters.AddWithValue("@exclude", (object?)excludeId ?? DBNull.Value);
        return command.ExecuteScalar() is long found && found == 1;
    }

    public bool SettleByQueuedEnsureId(long queuedEnsureId, string outcome, long settledAt)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE branch_intents
            SET state = 'settled', outcome = @outcome, settled_at = @at
            WHERE queued_ensure_id = @ensure AND state IN ('pending', 'claimed');
            """;
        command.Parameters.AddWithValue("@outcome", outcome);
        command.Parameters.AddWithValue("@at", settledAt);
        command.Parameters.AddWithValue("@ensure", queuedEnsureId);
        return command.ExecuteNonQuery() == 1;
    }

    public BranchIntent? Head(long repositoryId, string branch) => ReadOne(SelectColumns + "\n" + """
        WHERE repository_id = @repository AND branch = @branch AND state <> 'settled'
        ORDER BY id LIMIT 1;
        """, command =>
    {
        command.Parameters.AddWithValue("@repository", repositoryId);
        command.Parameters.AddWithValue("@branch", branch);
    });

    public bool Claim(long id) => Claim(id, null, _ => true);

    /// <summary>
    /// Claims the branch head for <paramref name="turnId"/>. A pending head is claimable (its scheduler has already
    /// waited out its retry time); a claimed one only when its owning turn is no longer live in this process (the turn
    /// ended without settling or returning it, for example because that write failed too).
    /// </summary>
    public bool Claim(long id, string? turnId, Func<string, bool> isLiveTurn)
    {
        var head = HeadForId(id);
        if (head != id)
            return false;
        var current = Get(id);
        if (current is null)
            return false;
        var claimable = current.State switch
        {
            "pending" => true,
            "claimed" => current.ClaimedByTurn is not { } owner || !isLiveTurn(owner),
            _ => false
        };
        if (!claimable)
            return false;
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE branch_intents SET state = 'claimed', claimed_by_turn = @turn
            WHERE id = @id AND state = @state AND claimed_by_turn IS @owner;
            """;
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@turn", (object?)turnId ?? DBNull.Value);
        command.Parameters.AddWithValue("@state", current.State);
        command.Parameters.AddWithValue("@owner", (object?)current.ClaimedByTurn ?? DBNull.Value);
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>
    /// Returns a claimed intent to <c>pending</c> with its next attempt time: the recovery of a turn that ended
    /// without settling it. <paramref name="countAttempt"/> is false for shutdown, which is not a failure.
    /// </summary>
    public bool Release(long id, string? turnId, long nextAttemptAt, string error, bool countAttempt = true)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE branch_intents
            SET state = 'pending', claimed_by_turn = NULL, next_attempt_at = @next, last_error = @error,
                attempts = attempts + @increment
            WHERE id = @id AND state = 'claimed' AND (@turn IS NULL OR claimed_by_turn IS @turn);
            """;
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@turn", (object?)turnId ?? DBNull.Value);
        command.Parameters.AddWithValue("@next", nextAttemptAt);
        command.Parameters.AddWithValue("@error", error);
        command.Parameters.AddWithValue("@increment", countAttempt ? 1 : 0);
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>Records the retry schedule of an unclaimed intent whose turn failed before it could settle.</summary>
    public bool Defer(long id, long nextAttemptAt, string error)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE branch_intents
            SET next_attempt_at = @next, last_error = @error, attempts = attempts + 1
            WHERE id = @id AND state = 'pending';
            """;
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@next", nextAttemptAt);
        command.Parameters.AddWithValue("@error", error);
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>The first unsettled intent in queue order of every branch: the only intents allowed to run.</summary>
    public IReadOnlyList<BranchIntent> ListHeads()
    {
        using var command = connection.CreateCommand();
        command.CommandText = ListHeadsSql;
        using var reader = command.ExecuteReader();
        var result = new List<BranchIntent>();
        while (reader.Read())
            result.Add(Read(reader));
        // One row per branch: ordering the heads themselves is cheap here, and keeps the query free of a sort.
        result.Sort((a, b) => a.Id.CompareTo(b.Id));
        return result;
    }

    /// <summary>
    /// One pass over the unsettled intents in queue order per branch, read from the partial index
    /// <c>ix_branch_intents_queue</c> (no sort, no per-row lookup): the first row of each branch is its head.
    /// </summary>
    internal const string ListHeadsSql = """
        SELECT id, repository_id, branch, kind, queued_ensure_id, admission_id, payload_json,
               state, outcome, admitted_at, settled_at,
               claimed_by_turn, attempts, next_attempt_at, last_error, following_refresh_id
        FROM (SELECT *, ROW_NUMBER() OVER (
                  PARTITION BY repository_id, branch ORDER BY id) AS queue_rank
              FROM branch_intents
              WHERE state <> 'settled')
        WHERE queue_rank = 1;
        """;

    public bool Settle(long id, string outcome, long settledAt)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE branch_intents
            SET state = 'settled', outcome = @outcome, settled_at = @at
            WHERE id = @id AND state IN ('pending', 'claimed');
            """;
        command.Parameters.AddWithValue("@outcome", outcome);
        command.Parameters.AddWithValue("@at", settledAt);
        command.Parameters.AddWithValue("@id", id);
        return command.ExecuteNonQuery() == 1;
    }

    public bool SettleByAdmissionId(string admissionId, string outcome, long settledAt)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE branch_intents
            SET state = 'settled', outcome = @outcome, settled_at = @at
            WHERE admission_id = @admission AND state IN ('pending', 'claimed');
            """;
        command.Parameters.AddWithValue("@outcome", outcome);
        command.Parameters.AddWithValue("@at", settledAt);
        command.Parameters.AddWithValue("@admission", admissionId);
        return command.ExecuteNonQuery() == 1;
    }

    public bool Unclaim(long id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE branch_intents SET state = 'pending', claimed_by_turn = NULL
            WHERE id = @id AND state = 'claimed';
            """;
        command.Parameters.AddWithValue("@id", id);
        return command.ExecuteNonQuery() == 1;
    }

    public int ReleaseClaims()
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE branch_intents SET state = 'pending', claimed_by_turn = NULL WHERE state = 'claimed';";
        return command.ExecuteNonQuery();
    }

    private long? HeadForId(long id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id FROM branch_intents
            WHERE repository_id = (SELECT repository_id FROM branch_intents WHERE id = @id)
              AND branch = (SELECT branch FROM branch_intents WHERE id = @id)
              AND state <> 'settled'
            ORDER BY id LIMIT 1;
            """;
        command.Parameters.AddWithValue("@id", id);
        return command.ExecuteScalar() is long head ? head : null;
    }

    // sql is always a compile-time constant (SelectColumns plus a constant WHERE clause); values are bound parameters.
    private BranchIntent? ReadOne(string sql, Action<SqliteCommand> bind)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind(command);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private const string SelectColumns = """
        SELECT id, repository_id, branch, kind, queued_ensure_id, admission_id, payload_json,
               state, outcome, admitted_at, settled_at,
               claimed_by_turn, attempts, next_attempt_at, last_error, following_refresh_id
        FROM branch_intents
        """;

    private static BranchIntent Read(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetInt64(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.GetInt64(9),
        reader.IsDBNull(10) ? null : reader.GetInt64(10),
        reader.IsDBNull(11) ? null : reader.GetString(11), reader.GetInt32(12),
        reader.IsDBNull(13) ? null : reader.GetInt64(13),
        reader.IsDBNull(14) ? null : reader.GetString(14),
        reader.IsDBNull(15) ? null : reader.GetInt64(15));
}
