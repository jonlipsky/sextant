using Sextant.Core;

namespace Sextant.Store;

public enum ChangeClassification
{
    NonBreaking,
    Additive,
    Breaking
}

public sealed class ChangeDetail
{
    public required string SymbolFqn { get; init; }
    public required ChangeClassification Classification { get; init; }
    public required string Reason { get; init; }
}

public static class BreakingChangeDetector
{
    /// <summary>
    /// Compares two API surface snapshots to detect breaking changes.
    /// </summary>
    public static List<ChangeDetail> DetectChanges(
        IReadOnlyList<(string fqn, string signatureHash, string accessibility)> oldSurface,
        IReadOnlyList<(string fqn, string signatureHash, string accessibility)> newSurface)
    {
        var changes = new List<ChangeDetail>();

        var oldByFqn = oldSurface.ToDictionary(s => s.fqn, s => (s.signatureHash, s.accessibility));
        var newByFqn = newSurface.ToDictionary(s => s.fqn, s => (s.signatureHash, s.accessibility));

        // Check for removals and modifications
        foreach (var (fqn, (oldHash, oldAccess)) in oldByFqn)
        {
            if (!newByFqn.TryGetValue(fqn, out var newEntry))
            {
                changes.Add(new ChangeDetail
                {
                    SymbolFqn = fqn,
                    Classification = ChangeClassification.Breaking,
                    Reason = "Symbol removed"
                });
                continue;
            }

            if (oldHash != newEntry.signatureHash)
            {
                changes.Add(new ChangeDetail
                {
                    SymbolFqn = fqn,
                    Classification = ChangeClassification.Breaking,
                    Reason = "Signature changed"
                });
                continue;
            }

            if (IsAccessibilityReduced(oldAccess, newEntry.accessibility))
            {
                changes.Add(new ChangeDetail
                {
                    SymbolFqn = fqn,
                    Classification = ChangeClassification.Breaking,
                    Reason = "Accessibility reduced"
                });
                continue;
            }

            // Same symbol, same signature, same or broader accessibility — non-breaking
        }

        // Check for additions
        foreach (var (fqn, _) in newByFqn)
        {
            if (!oldByFqn.ContainsKey(fqn))
            {
                changes.Add(new ChangeDetail
                {
                    SymbolFqn = fqn,
                    Classification = ChangeClassification.Additive,
                    Reason = "Symbol added"
                });
            }
        }

        return changes;
    }

    /// <summary>
    /// Returns the overall classification: Breaking > Additive > NonBreaking.
    /// </summary>
    public static ChangeClassification GetOverallClassification(IReadOnlyList<ChangeDetail> changes)
    {
        if (changes.Any(c => c.Classification == ChangeClassification.Breaking))
            return ChangeClassification.Breaking;
        if (changes.Any(c => c.Classification == ChangeClassification.Additive))
            return ChangeClassification.Additive;
        return ChangeClassification.NonBreaking;
    }

    private static readonly Dictionary<string, int> s_accessibilityOrder = new()
    {
        ["public"] = 5,
        ["protected_internal"] = 4,
        ["protected"] = 3,
        ["internal"] = 2,
        ["private_protected"] = 1,
        ["private"] = 0
    };

    private static bool IsAccessibilityReduced(string oldAccess, string newAccess)
    {
        var oldLevel = s_accessibilityOrder.GetValueOrDefault(oldAccess, -1);
        var newLevel = s_accessibilityOrder.GetValueOrDefault(newAccess, -1);
        return newLevel < oldLevel;
    }
}
