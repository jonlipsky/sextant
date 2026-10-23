using System.Text.RegularExpressions;

namespace Sextant.Service.Restore;

/// <summary>
/// Decides whether a restore error is LIKELY DETERMINISTIC (the same restore inputs usually fail the same way: an
/// unsatisfiable framework, workload or package) or TRANSIENT (a feed timeout, a 5xx, an unreachable source, anything
/// not recognised). This is a heuristic, not a proof: NU1101/NU1102 depend on the state of the feeds (a package can be
/// published later), so a deterministic verdict only means "do not retry within this job's cached state"; the cache is
/// keyed by the inputs and re-verified on every use. A code or message it does not know is transient, so the rule is:
/// when in doubt, restore.
/// </summary>
internal static partial class RestoreErrorClassifier
{
    /// <summary>The longest sanitized message kept.</summary>
    internal const int MaxMessageLength = 160;

    // NuGet restore errors that follow from the project graph and the package contents, not from the network:
    // NU1100-NU1108 (no package / version / dependency resolved), NU1201-NU1203 (incompatible framework or project),
    // NU1605 (package downgrade). NU1101/NU1102 only count when no source was unreachable for the same project: a source
    // that failed under --ignore-failed-sources is reported as NU1801, which makes the issue transient.
    private static readonly HashSet<string> DeterministicCodes = new(StringComparer.Ordinal)
    {
        "NU1100", "NU1101", "NU1102", "NU1103", "NU1104", "NU1105", "NU1106", "NU1107", "NU1108",
        "NU1201", "NU1202", "NU1203", "NU1605",
        "MSB4019", "MSB4057", "MSB4025", "MSB3202"
    };

    private static readonly string[] TransientPhrases =
    [
        "timed out", "timeout", "status code", "service index", "error occurred while sending", "connection",
        "name or service not known", "temporary failure", "socket", "too many requests", "unable to load", "unauthorized",
        "forbidden", "certificate"
    ];

    /// <summary>
    /// True for a code whose failure is a function of the restore inputs. <c>NETSDK*</c> (SDK and workload evaluation,
    /// e.g. NETSDK1147 "workload missing") is deterministic for one installed SDK, which the state fingerprint binds.
    /// </summary>
    internal static bool IsDeterministicCode(string code)
        => DeterministicCodes.Contains(code) || code.StartsWith("NETSDK", StringComparison.Ordinal);

    /// <summary>True when a message reads like a network or server failure rather than a verdict on the inputs.</summary>
    internal static bool IsTransientMessage(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return false;
        foreach (var phrase in TransientPhrases)
        {
            if (message.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        try
        {
            return HttpServerError().IsMatch(message);
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }

    /// <summary>
    /// Reduces an error message to what is safe and useful in telemetry: the trailing <c>[project]</c> is dropped, any
    /// URL (it could carry credentials) becomes <c>&lt;url&gt;</c>, the checkout path is removed, whitespace is
    /// collapsed and the text is cut to <see cref="MaxMessageLength"/>.
    /// </summary>
    internal static string Sanitize(string message, string checkoutDir)
    {
        try
        {
            var text = TrailingBracket().Replace(message, string.Empty);
            text = UrlLike().Replace(text, "<url>");
            text = MaskedSource().Replace(text, "<url>");
            text = UserInfoNoScheme().Replace(text, "<url>");
            if (checkoutDir.Length > 1)
                text = text.Replace(checkoutDir.TrimEnd('/', '\\') + Path.DirectorySeparatorChar, string.Empty, StringComparison.Ordinal);
            text = WindowsPath().Replace(text, "<path>");
            text = UncPath().Replace(text, "<path>");
            text = HomePath().Replace(text, "<path>");
            text = UnixPath().Replace(text, "<path>");
            text = Whitespace().Replace(text, " ").Trim();
            return text.Length <= MaxMessageLength ? text : text[..(MaxMessageLength - 3)] + "...";
        }
        catch (RegexMatchTimeoutException)
        {
            return string.Empty;
        }
    }

    [GeneratedRegex(@"\s*\[[^\[\]]*\]\s*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TrailingBracket();

    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9+.\-]*://\S+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UrlLike();

    // NuGet prints a source it masked as "******host/path".
    [GeneratedRegex(@"\*{2,}\S+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MaskedSource();

    // user:pass@host (or user@host) without a scheme.
    [GeneratedRegex(@"[^\s/@:]+(?::[^\s/@]*)?@[A-Za-z0-9][A-Za-z0-9.\-]*(?::\d+)?(?:/\S*)?", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UserInfoNoScheme();

    [GeneratedRegex(@"\b[A-Za-z]:[\\/][^\s'""]*", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex WindowsPath();

    [GeneratedRegex(@"\\\\[^\s\\'""]+(?:\\[^\s'""]*)*", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UncPath();

    // "~/.nuget/..." and "$HOME"-style paths.
    [GeneratedRegex(@"(?:~|\$HOME|%USERPROFILE%)[/\\][^\s'""]*", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HomePath();

    // A rooted Unix path: a slash that starts a token and is followed by a path segment and another slash.
    [GeneratedRegex(@"(?<![\w<>:/.])/[^\s/'""]+(?:/[^\s'""]*)+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UnixPath();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\bHTTP\s*5\d\d\b|\(5\d\d\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HttpServerError();
}
