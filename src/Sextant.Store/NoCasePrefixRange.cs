using System.Text;

namespace Sextant.Store;

/// <summary>
/// The key range of a literal, ASCII-case-insensitive prefix under SQLite's <c>NOCASE</c> collation (issue #196).
/// NOCASE, like <c>LIKE</c>, folds only the ASCII letters and otherwise compares UTF-8 bytes, which order like Unicode
/// scalar values. So a text starts with the prefix (ignoring ASCII case) exactly when, under NOCASE, it is at least
/// <see cref="Lo"/> and, unless <see cref="Hi"/> is null, below <see cref="Hi"/>. That turns a prefix match into an
/// index range. Shared by the service's <c>search_symbols</c> page seek and the MCP symbol resolver's suggestions.
/// </summary>
/// <param name="Lo">The prefix with its ASCII letters folded to lowercase.</param>
/// <param name="Hi">
/// The least text above every text that starts with the prefix, or null when there is none (a prefix made only of
/// U+10FFFF).
/// </param>
public readonly record struct NoCasePrefixRange(string Lo, string? Hi)
{
    public static NoCasePrefixRange Of(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        // A lone surrogate reads as U+FFFD, which is also how it reaches SQLite (bound text is encoded as UTF-8).
        var runes = new List<Rune>(prefix.Length);
        foreach (var rune in prefix.EnumerateRunes())
            runes.Add(rune.Value is >= 'A' and <= 'Z' ? new Rune(rune.Value + ('a' - 'A')) : rune);

        var lo = Concat(runes, runes.Count);
        for (var i = runes.Count - 1; i >= 0; i--)
        {
            // U+10FFFF has no successor: drop it and carry into the rune before it.
            if (Successor(runes[i]) is not { } next)
                continue;
            runes[i] = next;
            return new NoCasePrefixRange(lo, Concat(runes, i + 1));
        }
        return new NoCasePrefixRange(lo, null);
    }

    // The next scalar value as NOCASE orders folded text: the surrogates are not scalar values, and '@' is followed by
    // '[' because NOCASE folds 'A'..'Z' onto 'a'..'z' (a folded rune is never 'A'..'Z' itself).
    private static Rune? Successor(Rune rune) => rune.Value switch
    {
        0x10FFFF => null,
        0xD7FF => new Rune(0xE000),
        '@' => new Rune('['),
        var value => new Rune(value + 1)
    };

    private static string Concat(List<Rune> runes, int count)
    {
        var text = new StringBuilder(count * 2);
        for (var i = 0; i < count; i++)
            text.Append(runes[i].ToString());
        return text.ToString();
    }
}
