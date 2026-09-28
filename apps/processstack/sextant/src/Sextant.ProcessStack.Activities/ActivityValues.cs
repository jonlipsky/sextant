using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProcessStack.Abstractions;

namespace Sextant.ProcessStack.Activities;

/// <summary>
/// Input resolution and value conversion shared by the activities. Inputs reach an activity in several
/// shapes: bound CLR properties, <c>Definition.Parameters</c> entries, <see cref="JsonElement"/> or
/// <see cref="JsonNode"/> wrappers, CLR graphs (<c>Dictionary</c>/<c>List</c>) and JSON text. Every
/// accessor here accepts all of them and never throws on a malformed value; it returns <c>null</c> instead.
/// </summary>
internal static class ActivityValues
{
    /// <summary>
    /// The value of input <paramref name="name"/>: the node's <c>Definition.Parameters</c> entry when it has a
    /// non-null one, otherwise <paramref name="propertyValue"/> (the bound or directly set property). The host
    /// binds parameters onto the properties before <c>ExecuteAsync</c>, but a caller that fills only
    /// <c>Definition.Parameters</c> must see its inputs too (the dual-resolution rule).
    /// </summary>
    public static object? Input(AbstractActivity activity, string name, object? propertyValue)
    {
        var parameters = activity.Definition?.Parameters;
        if (parameters is not null && parameters.TryGetValue(name, out var value) && value is not null)
            return Unwrap(value);
        return Unwrap(propertyValue);
    }

    /// <summary>Unwraps a JSON wrapper into plain CLR values (string, long, double, bool, list, map, null).</summary>
    public static object? Unwrap(object? value) => value switch
    {
        JsonElement element => FromElement(element),
        JsonNode node => FromElement(JsonSerializer.SerializeToElement(node)),
        _ => value,
    };

    /// <summary>The value as trimmed text, or <c>""</c> when it is absent.</summary>
    public static string Text(object? value) => AsString(value)?.Trim() ?? string.Empty;

    /// <summary>The value as a string: text as is, booleans as <c>true</c>/<c>false</c>, numbers invariantly.</summary>
    public static string? AsString(object? value) => Unwrap(value) switch
    {
        null => null,
        string text => text,
        bool flag => flag ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        var other => other.ToString(),
    };

    /// <summary>
    /// The value as a boolean: a bool, <c>"true"</c>/<c>"false"</c> in any case, <c>"1"</c>/<c>"0"</c>, or a
    /// number (non-zero is true). <c>null</c> for anything else, including <c>""</c>.
    /// </summary>
    public static bool? AsBool(object? value)
    {
        switch (Unwrap(value))
        {
            case bool flag:
                return flag;
            case string text:
                var trimmed = text.Trim();
                if (trimmed.Equals("true", StringComparison.OrdinalIgnoreCase) || trimmed == "1")
                    return true;
                if (trimmed.Equals("false", StringComparison.OrdinalIgnoreCase) || trimmed == "0")
                    return false;
                return null;
            default:
                return AsLong(value) is long number ? number != 0 : null;
        }
    }

    /// <summary>The value as a whole number, or <c>null</c> when it is not one.</summary>
    public static long? AsLong(object? value)
    {
        var unwrapped = Unwrap(value);
        switch (unwrapped)
        {
            case long number:
                return number;
            case int or short or byte or sbyte or ushort or uint:
                return Convert.ToInt64(unwrapped, CultureInfo.InvariantCulture);
            case ulong unsigned:
                return unsigned <= long.MaxValue ? (long)unsigned : null;
            case double real:
                return IsWhole(real) ? (long)real : null;
            case float single:
                return IsWhole(single) ? (long)single : null;
            case decimal exact:
                return exact == decimal.Truncate(exact) && exact is >= long.MinValue and <= long.MaxValue
                    ? (long)exact
                    : null;
            case string text:
                return long.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : null;
            default:
                return null;
        }
    }

    /// <summary>The value as a list (a CLR collection, a JSON array, or JSON array text), or <c>null</c>.</summary>
    public static IReadOnlyList<object?>? AsList(object? value)
    {
        switch (Unwrap(value))
        {
            case null:
                return null;
            case string text:
                return ParseJsonText(text, '[') as IReadOnlyList<object?>;
            case IDictionary:
                return null;
            case IEnumerable<KeyValuePair<string, object?>>:
                return null;
            case IEnumerable items:
                var list = new List<object?>();
                foreach (var item in items)
                    list.Add(Unwrap(item));
                return list;
            default:
                return null;
        }
    }

    /// <summary>
    /// The value as a map with case-insensitive keys (a CLR dictionary, a JSON object, or JSON object text),
    /// or <c>null</c>. When two keys differ only by case, the first one wins.
    /// </summary>
    public static IReadOnlyDictionary<string, object?>? AsMap(object? value)
    {
        switch (Unwrap(value))
        {
            case null:
                return null;
            case string text:
                return ParseJsonText(text, '{') as IReadOnlyDictionary<string, object?>;
            case IDictionary dictionary:
                var fromDictionary = NewMap();
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Key?.ToString() is { } key)
                        fromDictionary.TryAdd(key, Unwrap(entry.Value));
                }
                return fromDictionary;
            case IEnumerable<KeyValuePair<string, object?>> pairs:
                var fromPairs = NewMap();
                foreach (var (key, item) in pairs)
                    fromPairs.TryAdd(key, Unwrap(item));
                return fromPairs;
            default:
                return null;
        }
    }

    /// <summary>The first of <paramref name="keys"/> that <paramref name="map"/> has a non-null value for.</summary>
    public static object? Get(IReadOnlyDictionary<string, object?>? map, params string[] keys)
    {
        if (map is null)
            return null;
        foreach (var key in keys)
        {
            if (map.TryGetValue(key, out var value) && value is not null)
                return value;
        }
        return null;
    }

    /// <summary>
    /// Clamps an optional limit input: absent or negative means <paramref name="defaultValue"/>, and the
    /// result never exceeds <paramref name="maximum"/>.
    /// </summary>
    public static int Limit(object? value, int defaultValue, int maximum)
    {
        var requested = AsLong(value);
        if (requested is not long limit || limit < 0)
            return defaultValue;
        return (int)Math.Min(limit, maximum);
    }

    /// <summary>A new map with the case-insensitive key comparer every map here uses.</summary>
    public static Dictionary<string, object?> NewMap() => new(StringComparer.OrdinalIgnoreCase);

    private static bool IsWhole(double real) =>
        !double.IsNaN(real) && !double.IsInfinity(real) && Math.Floor(real) == real
        && real is >= long.MinValue and < long.MaxValue;

    // Parses JSON text that starts with `opening` into CLR values; anything else (or invalid JSON) is null.
    private static object? ParseJsonText(string text, char opening)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed[0] != opening)
            return null;
        try
        {
            using var document = JsonDocument.Parse(trimmed);
            return FromElement(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static object? FromElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var number))
                    return number;
                return element.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Array:
                return element.EnumerateArray().Select(FromElement).ToList();
            case JsonValueKind.Object:
                var map = NewMap();
                foreach (var property in element.EnumerateObject())
                    map.TryAdd(property.Name, FromElement(property.Value));
                return map;
            default:
                return null;
        }
    }
}
