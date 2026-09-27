using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Sextant.Service.SdkPin;

/// <summary>
/// Reads and neutralizes the <c>sdk</c> pin of a <c>global.json</c> (issue #113). Neutralizing removes the
/// whole <c>sdk</c> object — <c>version</c>, <c>rollForward</c>, <c>allowPrerelease</c>, <c>paths</c>,
/// <c>errorMessage</c> — which makes hostfxr and the MSBuild SDK resolver behave exactly as if the file had
/// no pin (they pick the newest installed SDK). Every other section (<c>msbuild-sdks</c>, <c>test</c>, …)
/// is preserved so project-SDK versions still resolve as the repository intends. Parsing tolerates the
/// comments and trailing commas hostfxr accepts, and a UTF-8 BOM.
/// </summary>
internal static partial class GlobalJsonSdkPin
{
    private const string SdkProperty = "sdk";

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>The pin's <c>sdk.version</c> and <c>sdk.rollForward</c> (either may be null).</summary>
    public readonly record struct Pin(string? Version, string? RollForward);

    /// <summary>Reads the <c>sdk</c> pin; false when the content is not a JSON object.</summary>
    public static bool TryRead(byte[] content, out Pin pin, out string? error)
    {
        pin = default;
        if (!TryParseObject(content, out var root, out error))
            return false;

        if (FindProperty(root, SdkProperty) is JsonObject sdk)
            pin = new Pin(StringValue(sdk, "version"), StringValue(sdk, "rollForward"));
        return true;
    }

    /// <summary>
    /// True when <paramref name="version"/> is a well-formed .NET SDK version — <c>major.minor.patch</c> with
    /// a feature band of at least 100 (e.g. <c>10.0.300</c>), optionally with a prerelease/build suffix. hostfxr
    /// reports a malformed pin such as <c>1.2.0</c> with the same "A compatible .NET SDK was not found"
    /// wording as an absent band, so only a pin that names a real SDK band is a candidate for the override.
    /// </summary>
    public static bool IsSdkVersion(string? version) => version is not null && SdkVersionRegex().IsMatch(version);

    /// <summary>
    /// Produces the content with the <c>sdk</c> object removed. False (with a reason) when the content is
    /// not a JSON object or carries no <c>sdk</c> section to remove.
    /// </summary>
    public static bool TryNeutralize(byte[] content, out byte[] neutralized, out string? error)
    {
        neutralized = [];
        if (!TryParseObject(content, out var root, out error))
            return false;

        var sdkKeys = root.Where(p => string.Equals(p.Key, SdkProperty, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Key)
            .ToList();
        if (sdkKeys.Count == 0)
        {
            error = "global.json has no \"sdk\" section to neutralize";
            return false;
        }

        foreach (var key in sdkKeys)
            root.Remove(key);

        neutralized = Encoding.UTF8.GetBytes(root.ToJsonString(WriteOptions) + "\n");
        return true;
    }

    private static bool TryParseObject(byte[] content, out JsonObject root, out string? error)
    {
        root = null!;
        error = null;
        var bytes = content.AsMemory();
        if (bytes.Span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            bytes = bytes[3..];

        try
        {
            if (JsonNode.Parse(bytes.Span, documentOptions: DocumentOptions) is JsonObject obj)
            {
                // JsonObject materializes its properties lazily; force it here so a duplicate key surfaces
                // as a parse failure instead of escaping from a later enumeration.
                _ = obj.Count;
                root = obj;
                return true;
            }
            error = "global.json is not a JSON object";
            return false;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            error = $"global.json could not be parsed ({ex.Message})";
            return false;
        }
    }

    private static JsonNode? FindProperty(JsonObject obj, string name) =>
        obj.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static string? StringValue(JsonObject obj, string name) =>
        FindProperty(obj, name) switch
        {
            JsonValue value when value.TryGetValue<string>(out var s) => s,
            null => null,
            var other => other.ToJsonString()
        };

    [GeneratedRegex(@"^(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.[1-9]\d{2,}(?:-[0-9A-Za-z\-]+(?:\.[0-9A-Za-z\-]+)*)?(?:\+[0-9A-Za-z\-]+(?:\.[0-9A-Za-z\-]+)*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SdkVersionRegex();
}
