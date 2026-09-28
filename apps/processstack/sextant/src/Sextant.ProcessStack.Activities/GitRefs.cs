namespace Sextant.ProcessStack.Activities;

/// <summary>Commit SHA and branch-name rules.</summary>
internal static class GitRefs
{
    /// <summary>The <c>refs/heads/</c> prefix of a qualified branch ref.</summary>
    public const string HeadsPrefix = "refs/heads/";

    /// <summary>The <c>refs/tags/</c> prefix of a qualified tag ref.</summary>
    public const string TagsPrefix = "refs/tags/";

    private const int MaxBranchLength = 255;

    /// <summary>True for a full SHA-1 (40) or SHA-256 (64) hex commit id.</summary>
    public static bool IsCommitSha(string value) =>
        value.Length is 40 or 64 && value.All(char.IsAsciiHexDigit);

    /// <summary>True for an all-zero SHA (the <c>before</c> of a branch-create push, the <c>after</c> of a delete).</summary>
    public static bool IsZeroSha(string value) => value.Length > 0 && value.All(c => c == '0');

    /// <summary>The branch name of <paramref name="value"/>, without a leading <c>refs/heads/</c>.</summary>
    public static string StripHeadsPrefix(string value) =>
        value.StartsWith(HeadsPrefix, StringComparison.Ordinal) ? value[HeadsPrefix.Length..] : value;

    /// <summary>
    /// True for a name <c>git check-ref-format --branch</c> accepts: no control characters, space, <c>~^:?*[\</c>,
    /// <c>..</c>, <c>@{</c> or <c>//</c>; no leading <c>-</c> or <c>/</c>; no trailing <c>/</c> or <c>.</c>; no
    /// component starting with <c>.</c> or ending with <c>.lock</c>; not <c>@</c> or <c>HEAD</c>. At most 255
    /// characters.
    /// </summary>
    public static bool IsValidBranchName(string name)
    {
        if (name.Length is 0 or > MaxBranchLength || name is "@" or "HEAD")
            return false;
        if (name[0] is '-' or '/' || name[^1] is '/' or '.')
            return false;
        if (name.Contains("..", StringComparison.Ordinal) || name.Contains("@{", StringComparison.Ordinal)
            || name.Contains("//", StringComparison.Ordinal))
            return false;
        foreach (var c in name)
        {
            if (c < ' ' || c == '\u007f' || c is ' ' or '~' or '^' or ':' or '?' or '*' or '[' or '\\')
                return false;
        }
        foreach (var component in name.Split('/'))
        {
            if (component.StartsWith('.') || component.EndsWith(".lock", StringComparison.Ordinal))
                return false;
        }
        return true;
    }
}
