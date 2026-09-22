using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Store;

namespace Sextant.Mcp;

/// <summary>
/// Reads the feature capabilities of the currently-served index generation (Phase 8, criterion 3).
/// A capability-aware query tool consults this to decide whether the data it needs was actually
/// indexed under the active profile, rather than crashing or returning a silently-empty result.
/// </summary>
public static class IndexCapabilities
{
    /// <summary>
    /// The effective feature set of the served generation. A generation with no recorded features — a
    /// pre-Phase-8 index, or a directly-seeded test database with no run ledger — is treated as having
    /// every feature so older indexes and existing tests keep serving all tools. Under an enforced policy
    /// the run is scoped to the caller's selected snapshot (see <see cref="ResolveRun"/>).
    /// </summary>
    public static IndexFeature Effective(IndexDatabase db, long? selectedSnapshotId = null, bool enforcing = false)
    {
        try
        {
            using var conn = db.OpenReadConnection();
            var run = ResolveRun(conn, selectedSnapshotId, enforcing);
            if (run?.Features is { } features)
                return (IndexFeature)features;
        }
        catch
        {
            // Fall through to the permissive default on any ledger read failure.
        }
        return IndexFeature.Deep;
    }

    /// <summary>The served generation's profile name, or null when none was recorded.</summary>
    public static string? ActiveProfile(IndexDatabase db, long? selectedSnapshotId = null, bool enforcing = false)
    {
        try
        {
            using var conn = db.OpenReadConnection();
            return ResolveRun(conn, selectedSnapshotId, enforcing)?.IndexingProfile;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves the run whose capabilities/profile describe the served data. Under an enforced multi-tenant
    /// policy this is the caller's SELECTED snapshot's own run — never the DB-wide last-complete run, which
    /// would leak another tenant's profile name and feature set through a capability tool's
    /// success/feature-unavailable outcome (Phase 17, criterion 1). The zero-policy local path keeps using
    /// the last-complete run (byte-identical to pre-Phase-17).
    /// </summary>
    private static IndexRun? ResolveRun(SqliteConnection conn, long? selectedSnapshotId, bool enforcing)
    {
        var runStore = new IndexRunStore(conn);
        if (!enforcing)
            return runStore.GetLastCompleteRun();

        var runId = selectedSnapshotId is long sid ? new SnapshotStore(conn).GetById(sid)?.RunId : null;
        return runId is long rid ? runStore.GetById(rid) : null;
    }
}

/// <summary>
/// Guards a capability-gated MCP query tool. When the active generation lacks the feature a tool
/// needs, produces the structured feature-unavailable response (never throws, never returns empty
/// results the caller could mistake for "no matches").
/// </summary>
public static class CapabilityGate
{
    /// <summary>
    /// Returns true when the active index provides <paramref name="required"/>. When it does not, sets
    /// <paramref name="unavailableResponse"/> to a structured feature-unavailable envelope and returns
    /// false. <paramref name="feature"/> is the snake_case capability name surfaced to the caller.
    /// </summary>
    /// <summary>
    /// Returns true when the active index provides <paramref name="required"/>. When it does not, sets
    /// <paramref name="unavailableResponse"/> to a structured feature-unavailable envelope and returns
    /// false. <paramref name="feature"/> is the snake_case capability name surfaced to the caller.
    /// <paramref name="selectedSnapshotId"/>/<paramref name="enforcing"/> scope the capability lookup to the
    /// caller's own snapshot under an enforced policy (criterion 1); omitted, they preserve the pre-Phase-17
    /// DB-wide behavior for the zero-policy local path and existing callers/tests.
    /// </summary>
    public static bool Ensure(
        IndexDatabase db, IndexFeature required, string feature, out string unavailableResponse,
        long? selectedSnapshotId = null, bool enforcing = false)
    {
        var effective = IndexCapabilities.Effective(db, selectedSnapshotId, enforcing);
        if ((effective & required) == required)
        {
            unavailableResponse = string.Empty;
            return true;
        }

        var requiredProfile = IndexProfiles.MinimumProfileFor(required);
        var activeProfile = IndexCapabilities.ActiveProfile(db, selectedSnapshotId, enforcing);
        var message =
            $"This query needs the '{feature}' capability, which the active index " +
            $"(profile '{activeProfile ?? "unknown"}') did not build. Re-index with the " +
            $"'{requiredProfile}' profile or higher to enable it.";
        unavailableResponse = ResponseBuilder.BuildFeatureUnavailable(
            feature, requiredProfile, activeProfile, message);
        return false;
    }
}
