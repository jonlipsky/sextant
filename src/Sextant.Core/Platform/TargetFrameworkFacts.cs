namespace Sextant.Core.Platform;

/// <summary>
/// The parsed facts of a .NET target framework moniker (TFM): its framework token, and its OS platform +
/// optional platform version when the TFM is platform-specific (e.g. <c>net8.0-windows10.0.19041.0</c> →
/// framework <c>net8.0</c>, platform <c>windows</c>, platform version <c>10.0.19041.0</c>). A portable TFM
/// (e.g. <c>net8.0</c>, <c>netstandard2.0</c>) has a null <see cref="Platform"/>.
///
/// This is the ONE canonical TFM parser shared by capability detection (issue #65): the placement probe and
/// the Phase-16 contribution manifest builder both derive a project version's target platform from it, so
/// per-project workload/TFM/reference-pack capability detection is accurate and CONSISTENT across the route
/// decision and the "declare what you built" contribution manifest — rather than each re-implementing a
/// fuzzy suffix parse. It lives in core (ProcessStack-agnostic), asserted by ArchitectureBoundaryTests.
/// </summary>
public readonly record struct TargetFrameworkFacts
{
    /// <summary>The framework token (e.g. <c>net8.0</c>, <c>netstandard2.0</c>), lowercased; empty when unknown.</summary>
    public required string Framework { get; init; }

    /// <summary>The OS platform token (e.g. <c>windows</c>, <c>ios</c>), lowercased; null for a portable TFM.</summary>
    public string? Platform { get; init; }

    /// <summary>The platform version suffix (e.g. <c>10.0.19041.0</c>) when the TFM carries one; else null.</summary>
    public string? PlatformVersion { get; init; }

    /// <summary>Whether the TFM targets a specific OS platform (and so may need a non-Linux capability).</summary>
    public bool IsPlatformSpecific => !string.IsNullOrEmpty(Platform);

    /// <summary>The empty/unknown result for a null or blank TFM.</summary>
    public static TargetFrameworkFacts Unknown { get; } = new() { Framework = string.Empty };

    /// <summary>
    /// Parses a raw TFM string. Accepts either a bare TFM (<c>net8.0-windows</c>) or a Roslyn multi-TFM
    /// display suffix (<c>Foo (net8.0-windows)</c>) — the last parenthesized group is unwrapped first, so
    /// the same parser serves the probe (which sees project display names) and the manifest builder (which
    /// sees the MSBuild-evaluated TFM column). A framework token with no OS suffix is portable.
    /// </summary>
    public static TargetFrameworkFacts Parse(string? tfm)
    {
        if (string.IsNullOrWhiteSpace(tfm))
            return Unknown;

        var value = UnwrapParenthesizedTfm(tfm.Trim());

        // Split framework token from the OS suffix on the FIRST '-': "net8.0-windows10.0" → "net8.0" +
        // "windows10.0". A leading dash or an empty token yields no platform.
        var dash = value.IndexOf('-');
        if (dash <= 0)
            return new TargetFrameworkFacts { Framework = value.ToLowerInvariant() };

        var framework = value[..dash].ToLowerInvariant();

        // Only a recognized .NET 5+ framework token ("net" + a digit, e.g. net8.0) carries an OS platform
        // suffix. This disambiguates a real TFM ("net8.0-windows" → platform "windows") from an ordinary
        // hyphenated project display NAME ("Acme-Cli" must NOT parse to a bogus platform "cli"), so the ONE
        // parser can serve both the manifest builder (evaluated TFM column) and the probe (display name).
        if (!IsDotNetPlatformFrameworkToken(framework))
            return new TargetFrameworkFacts { Framework = value.ToLowerInvariant() };

        var suffix = value[(dash + 1)..].Trim();
        if (suffix.Length == 0)
            return new TargetFrameworkFacts { Framework = framework };

        // The OS suffix is an alpha platform name optionally followed by a numeric platform version
        // ("windows10.0.19041.0" → platform "windows", version "10.0.19041.0").
        var split = 0;
        while (split < suffix.Length && !char.IsDigit(suffix[split])) split++;
        var platform = suffix[..split].Trim().ToLowerInvariant();
        var version = suffix[split..].Trim();

        return new TargetFrameworkFacts
        {
            Framework = framework,
            Platform = platform.Length > 0 ? platform : null,
            PlatformVersion = version.Length > 0 ? version : null
        };
    }

    // Unwraps the LAST "(...)" group of a Roslyn multi-TFM display name so "My (Legacy) App (net8.0-ios)"
    // yields "net8.0-ios". A string with no parentheses is returned unchanged; an unwrapped value that is
    // not TFM-shaped simply parses to a portable/unknown result downstream (a name without a TFM suffix
    // must never be mis-read as a bogus platform).
    private static string UnwrapParenthesizedTfm(string value)
    {
        var close = value.LastIndexOf(')');
        if (close <= 0)
            return value;
        var open = value.LastIndexOf('(', close - 1);
        return open >= 0 ? value[(open + 1)..close].Trim() : value;
    }

    // A .NET 5+ framework token that can carry an OS platform suffix is "net" immediately followed by a
    // version digit (net5.0 … net8.0). Portable tokens ("netstandard2.0", "netcoreapp3.1") never carry an
    // OS suffix, and an arbitrary hyphenated word ("acme") is not a framework at all.
    private static bool IsDotNetPlatformFrameworkToken(string framework) =>
        framework.StartsWith("net", StringComparison.Ordinal) &&
        framework.Length > 3 &&
        char.IsDigit(framework[3]);
}
