using Sextant.Core;

namespace Sextant.Mcp;

/// <summary>
/// Reads a <c>kind</c> tool argument: a symbol kind name in any case (<c>class</c>, <c>Method</c>,
/// <c>type_parameter</c>), <c>ctor</c> for a constructor, or <c>type</c> for every type kind. An unknown name is an
/// error that lists the accepted ones, never a silently empty result.
/// </summary>
public static class SymbolKindNames
{
    private static readonly string Accepted =
        string.Join(", ", Enum.GetValues<SymbolKind>().Select(SymbolResolver.KindName)) + ", or 'type' for any type";

    /// <summary>
    /// Parses <paramref name="kind"/>: null or blank = no filter (<paramref name="kinds"/> null); otherwise the kinds
    /// it names and how they read in a message, or false with <paramref name="error"/>.
    /// </summary>
    public static bool TryParse(
        string? kind, out IReadOnlySet<SymbolKind>? kinds, out string? description, out string? error)
    {
        kinds = null;
        description = null;
        error = null;
        if (string.IsNullOrWhiteSpace(kind))
            return true;

        var text = kind.Trim().Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty);
        switch (text.ToLowerInvariant())
        {
            case "type":
            case "types":
                kinds = SymbolQuery.TypeKinds;
                description = "type";
                return true;
            case "ctor":
                kinds = new HashSet<SymbolKind> { SymbolKind.Constructor };
                description = "constructor";
                return true;
        }

        if (!text.All(char.IsLetter) || !Enum.TryParse<SymbolKind>(text, ignoreCase: true, out var parsed))
        {
            error = $"Unknown kind '{kind.Trim()}'. Use one of: {Accepted}.";
            return false;
        }
        kinds = new HashSet<SymbolKind> { parsed };
        description = SymbolResolver.KindName(parsed) == "typeparameter" ? "type parameter" : SymbolResolver.KindName(parsed);
        return true;
    }
}
