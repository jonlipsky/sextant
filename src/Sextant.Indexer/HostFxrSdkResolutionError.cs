using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Sextant.Indexer;

/// <summary>
/// A classified hostfxr ".NET SDK not found" failure (issue #113). Roslyn's out-of-proc BuildHost locates
/// MSBuild through MSBuildLocator, which calls <c>hostfxr_resolve_sdk2</c> with the project/solution
/// directory as the working directory. When a <c>global.json</c> on that path pins an SDK band that is not
/// installed (for example <c>"rollForward": "disable"</c> on a container with a newer band), hostfxr fails
/// with error code <see cref="SdkResolveFailureCode"/> and the load surfaces as
/// <c>RemoteInvocationException: An exception of type System.InvalidOperationException was thrown: Error
/// while calling hostfxr function hostfxr_resolve_sdk2. Error code: -2147450725 Detailed error: A compatible
/// .NET SDK was not found. Requested SDK version: … global.json file: …</c>.
/// <para>
/// This type recognizes that failure in an exception chain or free text (including text whose line breaks
/// were flattened) and extracts the requested version, the <c>global.json</c> path, and any installed SDKs
/// hostfxr listed. The BuildHost's copy of the message usually lists NO installed SDKs (hostfxr writes that
/// list to the host's stdout), so callers should take the installed set from
/// <see cref="ISdkResolutionProbe.ListInstalledSdks"/> instead.
/// </para>
/// </summary>
public sealed partial record HostFxrSdkResolutionError
{
    /// <summary>The hostfxr export whose failure this classifies.</summary>
    public const string ResolveFunction = "hostfxr_resolve_sdk2";

    /// <summary>hostfxr's <c>SdkResolveFailure</c> status code (0x8000809B).</summary>
    public const int SdkResolveFailureCode = -2147450725;

    /// <summary>The SDK version the <c>global.json</c> requested, when hostfxr reported it.</summary>
    public string? RequestedVersion { get; init; }

    /// <summary>The absolute path of the <c>global.json</c> hostfxr honored, when it reported one.</summary>
    public string? GlobalJsonPath { get; init; }

    /// <summary>The installed SDK versions hostfxr listed in the message (often empty; see the type remarks).</summary>
    public IReadOnlyList<string> InstalledSdks { get; init; } = [];

    /// <summary>
    /// True when hostfxr attributed the failure to a <c>global.json</c> pin (it reported the file or a
    /// requested version). False for a plain "no compatible SDK installed" failure with no pin involved (e.g.
    /// "No .NET SDKs were found." on a runtime-only image), which callers must not describe as a pin.
    /// </summary>
    public bool IsGlobalJsonPin => GlobalJsonPath is not null || RequestedVersion is not null;

    /// <summary>
    /// True when hostfxr reported the specific "no compatible SDK is installed" outcome (status
    /// <see cref="SdkResolveFailureCode"/> or its "was not found" wording) rather than some other
    /// <c>hostfxr_resolve_sdk2</c> failure (e.g. a malformed <c>global.json</c> or an invalid argument). Only
    /// this outcome is a candidate for the service's SDK-pin override (issue #113).
    /// </summary>
    public bool IsMissingSdk { get; init; }

    /// <summary>
    /// True when <paramref name="message"/> is a hostfxr SDK-resolution failure; <paramref name="error"/>
    /// then carries whatever details the message contained.
    /// </summary>
    public static bool TryParse(string? message, [NotNullWhen(true)] out HostFxrSdkResolutionError? error)
    {
        error = null;
        if (string.IsNullOrEmpty(message) || !IsSdkResolutionFailure(message))
            return false;

        var requested = FirstGroup(RequestedVersionRegex(), message)
            ?? FirstGroup(LegacyNotFoundRegex(), message, "version")
            ?? FirstGroup(InstallTheRegex(), message);
        var path = FirstGroup(GlobalJsonFileRegex(), message)
            ?? FirstGroup(LegacyNotFoundRegex(), message, "path")
            ?? FirstGroup(UpdatePathRegex(), message);

        var installed = new List<string>();
        foreach (Match match in InstalledSdkRegex().Matches(message))
        {
            var version = match.Groups["version"].Value;
            if (!installed.Contains(version, StringComparer.Ordinal))
                installed.Add(version);
        }

        error = new HostFxrSdkResolutionError
        {
            RequestedVersion = requested,
            GlobalJsonPath = path?.Trim(),
            InstalledSdks = installed,
            IsMissingSdk = IsMissingSdkFailure(message)
        };
        return true;
    }

    /// <summary>
    /// Walks <paramref name="exception"/>, its inner exceptions, and any <see cref="AggregateException"/>
    /// members, returning the first hostfxr SDK-resolution failure found.
    /// </summary>
    public static bool TryClassify(Exception? exception, [NotNullWhen(true)] out HostFxrSdkResolutionError? error)
    {
        error = null;
        var pending = new Stack<Exception>();
        if (exception is not null)
            pending.Push(exception);

        var visited = 0;
        while (pending.Count > 0 && visited++ < 64)
        {
            var current = pending.Pop();
            if (TryParse(current.Message, out error))
                return true;

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                    pending.Push(inner);
            }
            else if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
            }
        }

        return false;
    }

    private static bool IsSdkResolutionFailure(string message) =>
        message.Contains(ResolveFunction, StringComparison.Ordinal) || IsMissingSdkFailure(message);

    private static bool IsMissingSdkFailure(string message) =>
        message.Contains(SdkResolveFailureCodeText, StringComparison.Ordinal)
        || message.Contains(SdkResolveFailureHex, StringComparison.OrdinalIgnoreCase)
        || message.Contains("A compatible .NET SDK was not found", StringComparison.OrdinalIgnoreCase)
        || message.Contains("A compatible installed .NET SDK for global.json version", StringComparison.OrdinalIgnoreCase)
        || message.Contains("No .NET SDKs were found", StringComparison.OrdinalIgnoreCase)
        || (InstallTheRegex().IsMatch(message) && message.Contains("to match an installed SDK", StringComparison.OrdinalIgnoreCase));

    private static readonly string SdkResolveFailureCodeText =
        "Error code: " + SdkResolveFailureCode.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private const string SdkResolveFailureHex = "0x8000809B";

    private static string? FirstGroup(Regex regex, string input, string group = "value")
    {
        var match = regex.Match(input);
        return match.Success ? match.Groups[group].Value : null;
    }

    [GeneratedRegex(@"Requested SDK version:\s*(?<value>[^\s\[\]]+)", RegexOptions.IgnoreCase)]
    private static partial Regex RequestedVersionRegex();

    // The path runs to the first "global.json" on the same line, which also copes with messages whose line
    // breaks were flattened ("…/global.json Installed SDKs: …") and with paths that contain spaces.
    [GeneratedRegex(@"global\.json file:\s*(?<value>[^\r\n]*?global\.json)", RegexOptions.IgnoreCase)]
    private static partial Regex GlobalJsonFileRegex();

    // Older hostfxr wording: "A compatible installed .NET SDK for global.json version [x] from [path] was not found".
    [GeneratedRegex(@"global\.json version \[(?<version>[^\]]+)\] from \[(?<path>[^\]]+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex LegacyNotFoundRegex();

    [GeneratedRegex(@"Install the \[(?<value>[^\]]+)\] \.NET SDK", RegexOptions.IgnoreCase)]
    private static partial Regex InstallTheRegex();

    [GeneratedRegex(@"update \[(?<value>[^\]]+)\] to match", RegexOptions.IgnoreCase)]
    private static partial Regex UpdatePathRegex();

    // "10.0.401 [/usr/share/dotnet/sdk]" — a version NOT itself inside brackets, followed by a bracketed path.
    [GeneratedRegex(@"(?<![\w.\[\-])(?<version>\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.\-]+)?)\s+\[(?<path>[^\]\r\n]+)\]")]
    private static partial Regex InstalledSdkRegex();
}
