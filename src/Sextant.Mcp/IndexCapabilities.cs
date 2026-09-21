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
    /// The effective feature set of the last complete generation. A generation with no recorded
    /// features — a pre-Phase-8 index, or a directly-seeded test database with no run ledger — is
    /// treated as having every feature so older indexes and existing tests keep serving all tools.
    /// </summary>
    public static IndexFeature Effective(IndexDatabase db)
    {
        try
        {
            using var conn = db.OpenReadConnection();
            var run = new IndexRunStore(conn).GetLastCompleteRun();
            if (run?.Features is { } features)
                return (IndexFeature)features;
        }
        catch
        {
            // Fall through to the permissive default on any ledger read failure.
        }
        return IndexFeature.Deep;
    }

    /// <summary>The active generation's profile name, or null when none was recorded.</summary>
    public static string? ActiveProfile(IndexDatabase db)
    {
        try
        {
            using var conn = db.OpenReadConnection();
            return new IndexRunStore(conn).GetLastCompleteRun()?.IndexingProfile;
        }
        catch
        {
            return null;
        }
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
    public static bool Ensure(
        IndexDatabase db, IndexFeature required, string feature, out string unavailableResponse)
    {
        var effective = IndexCapabilities.Effective(db);
        if ((effective & required) == required)
        {
            unavailableResponse = string.Empty;
            return true;
        }

        var requiredProfile = IndexProfiles.MinimumProfileFor(required);
        var activeProfile = IndexCapabilities.ActiveProfile(db);
        var message =
            $"This query needs the '{feature}' capability, which the active index " +
            $"(profile '{activeProfile ?? "unknown"}') did not build. Re-index with the " +
            $"'{requiredProfile}' profile or higher to enable it.";
        unavailableResponse = ResponseBuilder.BuildFeatureUnavailable(
            feature, requiredProfile, activeProfile, message);
        return false;
    }
}
