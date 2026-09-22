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
    /// Compares two API surface snapshots to detect breaking changes. Symbols are matched by their stable
    /// Phase-2 <c>symbol_key</c>, NOT by fully-qualified name: public overloads share one FQN (e.g.
    /// <c>Foo(int)</c> and <c>Foo(string)</c>), so keying by FQN threw a duplicate-key
    /// <see cref="ArgumentException"/> the moment a project exposed an overloaded public member (issue
    /// #29). The <c>symbol_key</c> distinguishes overloads, so each is compared independently; the FQN is
    /// retained only for display in <see cref="ChangeDetail.SymbolFqn"/>.
    /// </summary>
    public static List<ChangeDetail> DetectChanges(
        IReadOnlyList<(string symbolKey, string fqn, string signatureHash, string accessibility)> oldSurface,
        IReadOnlyList<(string symbolKey, string fqn, string signatureHash, string accessibility)> newSurface)
    {
        var changes = new List<ChangeDetail>();

        // Key by the stable symbol_key so overloads sharing an FQN are distinct entries. A defensive
        // last-writer-wins guards against a (malformed) duplicate key rather than throwing.
        var oldByKey = new Dictionary<string, (string fqn, string signatureHash, string accessibility)>();
        foreach (var s in oldSurface)
            oldByKey[s.symbolKey] = (s.fqn, s.signatureHash, s.accessibility);
        var newByKey = new Dictionary<string, (string fqn, string signatureHash, string accessibility)>();
        foreach (var s in newSurface)
            newByKey[s.symbolKey] = (s.fqn, s.signatureHash, s.accessibility);

        // Check for removals and modifications
        foreach (var (key, (fqn, oldHash, oldAccess)) in oldByKey)
        {
            if (!newByKey.TryGetValue(key, out var newEntry))
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
        foreach (var (key, entry) in newByKey)
        {
            if (!oldByKey.ContainsKey(key))
            {
                changes.Add(new ChangeDetail
                {
                    SymbolFqn = entry.fqn,
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
