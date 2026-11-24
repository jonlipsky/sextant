using Microsoft.Data.Sqlite;

namespace Sextant.Store;

/// <summary>A durable request to refresh one repository branch, without carrying a commit hint.</summary>
public sealed record PendingRefresh(
    long RepositoryId,
    string RepositoryUrl,
    string Branch,
    string AdmissionId,
    long AdmittedAt,
    string? ClaimedByTurn,
    long? ClaimedAt,
    int Attempts,
    long NextAttemptAt,
    string? LastError);

public sealed record CompletedRefreshAdmission(
    string AdmissionId,
    string RepositoryUrl,
    string Branch,
    long CompletedAt,
    long? SnapshotId,
    string? CommitSha,
    string Status);

/// <summary>
/// The status of any branch intent by its public admission id (issue #273, B3). <see cref="Kind"/> is
/// <c>refresh</c>, <c>ensure</c>, <c>retire</c> or <c>delete</c>. A refresh request absorbed into an earlier pending
/// refresh is answered with that refresh's admission id, so its status is that refresh's.
/// </summary>
public sealed record RefreshAdmissionStatus(
    string AdmissionId,
    string RepositoryUrl,
    string Branch,
    string Status,
    int Attempts,
    long? NextAttemptAt,
    string? LastError,
    long? SnapshotId,
    string? CommitSha,
    string Kind = "refresh");

/// <summary>Persists service-owned watched-branch refresh markers and their completed outcomes.</summary>
public sealed class PendingRefreshStore(SqliteConnection connection)
{
    /// <summary>Admits a refresh marker. BranchIntentStore owns the branch queue (order and absorption).</summary>
    public (PendingRefresh Marker, bool Created) Admit(
        long repositoryId, string branch, string admissionId, long now)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO pending_refreshes
                    (repository_id, branch, admission_id, admitted_at, next_attempt_at)
                VALUES (@repository, @branch, @admission, @at, @at);
                """;
            command.Parameters.AddWithValue("@repository", repositoryId);
            command.Parameters.AddWithValue("@branch", branch);
            command.Parameters.AddWithValue("@admission", admissionId);
            command.Parameters.AddWithValue("@at", now);
            command.ExecuteNonQuery();
        }
        return (GetByAdmissionId(admissionId)!, true);
    }

    public PendingRefresh? GetByAdmissionId(string admissionId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + "\nWHERE r.admission_id = @admission;";
        command.Parameters.AddWithValue("@admission", admissionId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public CompletedRefreshAdmission? GetCompleted(string admissionId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            -- A refresh that expired (rule 7) without effect.
            SELECT i.admission_id, repositories.remote_url, i.branch, COALESCE(i.settled_at, i.admitted_at),
                   NULL, NULL, i.outcome
            FROM branch_intents i
            JOIN repositories ON repositories.id = i.repository_id
            WHERE i.admission_id = @admission AND i.state = 'settled' AND i.kind = 'refresh' AND i.outcome = 'expired'
            UNION ALL
            SELECT c.admission_id, repositories.remote_url, c.branch, c.completed_at, c.snapshot_id, c.commit_sha, 'completed'
            FROM completed_refresh_admissions c
            JOIN repositories ON repositories.id = c.repository_id
            WHERE c.admission_id = @admission
            ;
            """;
        command.Parameters.AddWithValue("@admission", admissionId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new CompletedRefreshAdmission(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6))
            : null;
    }

    public RefreshAdmissionStatus? GetStatus(string admissionId)
    {
        var intent = new BranchIntentStore(connection).GetByAdmissionId(admissionId);
        if (intent is not null && intent.Kind != "refresh")
            return new RefreshAdmissionStatus(
                admissionId, GetRepositoryUrl(intent.RepositoryId), intent.Branch,
                intent.State switch
                {
                    "claimed" => "claimed",
                    "pending" => intent.Attempts > 0 ? "retrying" : "queued",
                    _ => intent.Outcome ?? "settled"
                },
                intent.Attempts, intent.State == "pending" ? intent.NextAttemptAt : null, intent.LastError,
                null, null, intent.Kind);
        var pending = GetByAdmissionId(admissionId);
        if (pending is not null)
            return new RefreshAdmissionStatus(
                pending.AdmissionId, pending.RepositoryUrl, pending.Branch,
                pending.ClaimedByTurn is not null && pending.ClaimedAt is not null
                    ? "claimed" : pending.Attempts > 0 ? "retrying" : "queued",
                pending.Attempts, pending.NextAttemptAt, pending.LastError, null, null);
        var completed = GetCompleted(admissionId);
        return completed is null
            ? null
            : new RefreshAdmissionStatus(completed.AdmissionId, completed.RepositoryUrl, completed.Branch,
                completed.Status, 0, null, null, completed.SnapshotId, completed.CommitSha);
    }

    private string GetRepositoryUrl(long repositoryId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT remote_url FROM repositories WHERE id = @id;";
        command.Parameters.AddWithValue("@id", repositoryId);
        return command.ExecuteScalar() as string
            ?? throw new InvalidOperationException($"Repository {repositoryId} for branch intent is missing.");
    }

    public IReadOnlyList<PendingRefresh> ListPending()
    {
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + "\n" + """
            ORDER BY r.admitted_at, r.admission_id;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<PendingRefresh>();
        while (reader.Read())
            result.Add(Read(reader));
        return result;
    }

    /// <summary>Claims a due marker at the beginning of its writer turn.</summary>
    public bool Claim(string admissionId, string turnId, long now)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE pending_refreshes
            SET claimed_by_turn = @turn, claimed_at = @now
            WHERE admission_id = @admission AND next_attempt_at <= @now
              AND (claimed_by_turn IS NULL OR claimed_by_turn = @turn OR claimed_at IS NULL);
            """;
        command.Parameters.AddWithValue("@turn", turnId);
        command.Parameters.AddWithValue("@now", now);
        command.Parameters.AddWithValue("@admission", admissionId);
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>Returns a marker to the queue with exponential backoff. Caller owns the transaction.</summary>
    public bool Retry(string admissionId, string turnId, long nextAttemptAt, string error, bool countAttempt = true)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE pending_refreshes
            SET attempts = attempts + @increment,
                next_attempt_at = @next,
                last_error = @error,
                claimed_by_turn = NULL,
                claimed_at = NULL
            WHERE admission_id = @admission AND claimed_by_turn = @turn;
            """;
        command.Parameters.AddWithValue("@next", nextAttemptAt);
        command.Parameters.AddWithValue("@error", error);
        command.Parameters.AddWithValue("@admission", admissionId);
        command.Parameters.AddWithValue("@turn", turnId);
        command.Parameters.AddWithValue("@increment", countAttempt ? 1 : 0);
        return command.ExecuteNonQuery() == 1;
    }

    public bool DeleteClaimed(string admissionId, string turnId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM pending_refreshes
            WHERE admission_id = @admission AND claimed_by_turn = @turn;
            """;
        command.Parameters.AddWithValue("@admission", admissionId);
        command.Parameters.AddWithValue("@turn", turnId);
        return command.ExecuteNonQuery() == 1;
    }


    /// <summary>Atomically stores the completed outcome and removes the claimed marker. Caller owns the transaction.</summary>
    public bool CompleteClaimed(
        string admissionId, string turnId, long completedAt, long snapshotId, string commitSha)
    {
        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO completed_refresh_admissions
                (admission_id, repository_id, branch, completed_at, snapshot_id, commit_sha)
            SELECT admission_id, repository_id, branch, @completed, @snapshot, @commit
            FROM pending_refreshes
            WHERE admission_id = @admission AND claimed_by_turn = @turn;
            """;
        insert.Parameters.AddWithValue("@completed", completedAt);
        insert.Parameters.AddWithValue("@snapshot", snapshotId);
        insert.Parameters.AddWithValue("@commit", commitSha);
        insert.Parameters.AddWithValue("@admission", admissionId);
        insert.Parameters.AddWithValue("@turn", turnId);
        if (insert.ExecuteNonQuery() != 1)
            return false;
        return DeleteClaimed(admissionId, turnId);
    }

    // Issue #273: settled branch intents are pruned after the seven-day status window, oldest first, through the partial
    // ix_branch_intents_settled_at index, so one pass reads at most @limit candidates. No correctness decision re-reads
    // a pruned intent: an identical-ensure share is decided once at admission and stored on the intent row.
    internal const string PruneSettledIntentsSql = """
        DELETE FROM branch_intents
        WHERE id IN (
            SELECT id FROM branch_intents
            WHERE state = 'settled' AND settled_at < @olderThan
            ORDER BY settled_at, id
            LIMIT @limit
        );
        """;

    public int PruneCompleted(long olderThan, int limit = 1000)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM completed_refresh_admissions
            WHERE admission_id IN (
                SELECT admission_id FROM completed_refresh_admissions
                WHERE completed_at < @olderThan
                ORDER BY completed_at, admission_id
                LIMIT @limit
            );
            """;
        command.Parameters.AddWithValue("@olderThan", olderThan);
        command.Parameters.AddWithValue("@limit", limit);
        var pruned = command.ExecuteNonQuery();
        using var intents = connection.CreateCommand();
        intents.CommandText = PruneSettledIntentsSql;
        intents.Parameters.AddWithValue("@olderThan", olderThan);
        intents.Parameters.AddWithValue("@limit", limit);
        return pruned + intents.ExecuteNonQuery();
    }

    /// <summary>Releases claims made by prior service processes, preserving markers and retry schedules.</summary>
    public int ReleaseDeadClaims(string currentTurn)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE pending_refreshes
            SET claimed_by_turn = NULL, claimed_at = NULL
            WHERE claimed_by_turn IS NOT NULL AND claimed_by_turn <> @turn
              AND NOT EXISTS (
                  SELECT 1 FROM pending_refreshes pending
                  WHERE pending.repository_id = pending_refreshes.repository_id
                    AND pending.branch = pending_refreshes.branch
                    AND (pending.claimed_by_turn IS NULL
                         OR (pending.claimed_by_turn <> @turn
                             AND (pending.admitted_at < pending_refreshes.admitted_at
                                  OR (pending.admitted_at = pending_refreshes.admitted_at
                                      AND pending.admission_id < pending_refreshes.admission_id)))));
            """;
        command.Parameters.AddWithValue("@turn", currentTurn);
        var released = command.ExecuteNonQuery();
        using var remaining = connection.CreateCommand();
        remaining.CommandText = """
            UPDATE pending_refreshes SET claimed_by_turn = @turn, claimed_at = NULL
            WHERE claimed_by_turn IS NOT NULL AND claimed_by_turn <> @turn;
            """;
        remaining.Parameters.AddWithValue("@turn", currentTurn);
        return released + remaining.ExecuteNonQuery();
    }

    private const string SelectColumns = """
        SELECT r.repository_id, repositories.remote_url, r.branch, r.admission_id, r.admitted_at,
               r.claimed_by_turn, r.claimed_at, r.attempts, r.next_attempt_at, r.last_error
        FROM pending_refreshes r
        JOIN repositories ON repositories.id = r.repository_id
        """;

    private static PendingRefresh Read(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetInt64(4), reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetInt64(6), reader.GetInt32(7),
        reader.GetInt64(8), reader.IsDBNull(9) ? null : reader.GetString(9));
}
