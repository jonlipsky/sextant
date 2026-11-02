using System.Text.RegularExpressions;

namespace Sextant.Core;

/// <summary>
/// Operator-facing text for a failed git call: the exit code and the first line of stderr, bounded and with any URL
/// userinfo (<c>user[:password]@</c>) removed, so a token embedded in a URL git echoes never reaches a log or diagnostic.
/// </summary>
public static partial class GitFailureText
{
    public const int MaxLength = 300;

    [GeneratedRegex(@"(?<=://)[^\s/?#]*@", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UrlUserInfo();

    /// <summary>The first line of <paramref name="stderr"/>, credential-free, without a leading <c>fatal: </c>, at most <see cref="MaxLength"/> characters.</summary>
    public static string FirstLine(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
            return string.Empty;
        var lines = stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
            return string.Empty;
        // git prefixes advice with hint:/warning:; the line that says what failed is fatal:/error:.
        var line = lines.FirstOrDefault(l => l.StartsWith("fatal:", StringComparison.Ordinal)
                                             || l.StartsWith("error:", StringComparison.Ordinal)) ?? lines[0];
        try
        {
            line = UrlUserInfo().Replace(line, string.Empty);
        }
        catch (RegexMatchTimeoutException)
        {
            return "stderr withheld";
        }

        if (line.StartsWith("fatal: ", StringComparison.Ordinal))
            line = line["fatal: ".Length..];
        var chars = line.Select(c => char.IsControl(c) ? '?' : c).ToArray();
        line = new string(chars);
        return line.Length <= MaxLength ? line : line[..MaxLength] + "…";
    }

    /// <summary><c>exit code 128: not a git repository</c>, or <c>exit code 128, no stderr</c>; no code when git never ran.</summary>
    public static string Describe(int? exitCode, string? stderr)
    {
        var line = FirstLine(stderr);
        var code = exitCode is { } c ? $"exit code {c}" : "git did not run";
        return line.Length > 0 ? $"{code}: {line}" : $"{code}, no stderr";
    }
}
