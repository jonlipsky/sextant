using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sextant.Service.CallerIdentity;

namespace Sextant.Service.Search;

/// <summary>Where one snapshot's <c>search_symbols</c> paging resumes: after the symbol row <paramref name="AfterId"/>.</summary>
internal sealed record SymbolSearchPosition(string IdentityHash, long AfterId);

/// <summary>
/// The resume state a <c>search_symbols</c> cursor carries (SVC-F): the snapshots still being paged, each with its
/// position, ordered by identity hash, and the watermark, the greatest identity hash ever admitted. A visible
/// snapshot whose hash sorts after the watermark has not been searched yet.
/// </summary>
internal sealed record SymbolSearchCursorState(IReadOnlyList<SymbolSearchPosition> Active, string? Watermark);

/// <summary>
/// Encodes and validates the opaque <c>search_symbols</c> cursor (SVC-F): base64url of the JSON
/// <c>{"v":1,"a":[[hash,afterId],...],"w":hash|null,"b":digest}</c>, at most <see cref="MaxLength"/> characters.
/// <para>
/// <c>b</c> is an unkeyed SHA-256 digest over the cursor's contents and its binding (see <see cref="Binding"/>): the
/// tenant, the caller and the query. It detects a tampered cursor and a cursor replayed by another caller, another
/// tenant or another query, which all get <c>invalid_cursor</c>. It is NOT an authorization control: anyone can
/// compute it. What the cursor may read is decided again on every call, against the caller's visible snapshots, so a
/// hash the caller cannot see is ignored like one that does not exist. It is deliberately unkeyed, so a cursor stays
/// valid across service replicas and restarts.
/// </para>
/// </summary>
internal static class SymbolSearchCursor
{
    /// <summary>The longest cursor accepted, in characters.</summary>
    public const int MaxLength = 16 * 1024;

    private const int Version = 1;
    private const int HashLength = 64;

    /// <summary>
    /// The binding a cursor is valid for: the tenant, the caller (a user's subject or an application's name) and every
    /// query argument that shapes the result. A cursor issued under one binding is <c>invalid_cursor</c> under another.
    /// </summary>
    public static string Binding(
        CallerPrincipal caller, string namePrefix, string? kind, string? repositoryKey, string? branch)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var subject = caller.Actor == CallerActor.User ? "user:" + caller.UserId : "app:" + caller.App;
        return JsonSerializer.Serialize(new[] { caller.TenantId, subject, namePrefix, kind, repositoryKey, branch });
    }

    /// <summary>Whether <paramref name="value"/> has the shape of a snapshot identity hash (64 lowercase hex digits).</summary>
    public static bool IsIdentityHash(string? value) =>
        value is { Length: HashLength } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>Encodes <paramref name="state"/> for <paramref name="binding"/>.</summary>
    public static string Encode(SymbolSearchCursorState state, string binding)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(binding);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", Version);
            writer.WriteStartArray("a");
            foreach (var position in state.Active)
            {
                writer.WriteStartArray();
                writer.WriteStringValue(position.IdentityHash);
                writer.WriteNumberValue(position.AfterId);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            if (state.Watermark is null)
                writer.WriteNull("w");
            else
                writer.WriteString("w", state.Watermark);
            writer.WriteString("b", Digest(state, binding));
            writer.WriteEndObject();
        }
        return Base64Url.EncodeToString(buffer.ToArray());
    }

    /// <summary>
    /// Decodes a cursor issued for <paramref name="binding"/>. False (<c>invalid_cursor</c>) when it is empty or too
    /// long, is not base64url JSON of the expected shape, has another version, holds more than
    /// <paramref name="maxEntries"/> positions, malformed or unordered hashes or negative positions, or was issued for
    /// another binding or altered.
    /// </summary>
    public static bool TryDecode(string cursor, string binding, int maxEntries, out SymbolSearchCursorState? state)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        ArgumentNullException.ThrowIfNull(binding);
        state = null;
        if (cursor.Length is 0 or > MaxLength)
            return false;

        byte[] json;
        try
        {
            json = Base64Url.DecodeFromChars(cursor);
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            if (!TryRead(document.RootElement, maxEntries, out var decoded, out var digest))
                return false;
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(digest), Encoding.UTF8.GetBytes(Digest(decoded, binding))))
                return false;
            state = decoded;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryRead(JsonElement root, int maxEntries, out SymbolSearchCursorState state, out string digest)
    {
        state = null!;
        digest = "";
        if (root.ValueKind != JsonValueKind.Object)
            return false;

        JsonElement? version = null;
        JsonElement? active = null;
        JsonElement? watermark = null;
        JsonElement? binding = null;
        foreach (var property in root.EnumerateObject())
        {
            switch (property.Name)
            {
                case "v" when version is null: version = property.Value; break;
                case "a" when active is null: active = property.Value; break;
                case "w" when watermark is null: watermark = property.Value; break;
                case "b" when binding is null: binding = property.Value; break;
                default: return false;
            }
        }
        if (version is not { ValueKind: JsonValueKind.Number } v || !v.TryGetInt32(out var number) || number != Version)
            return false;
        if (binding is not { ValueKind: JsonValueKind.String } b || b.GetString() is not { Length: > 0 } digestValue)
            return false;
        if (!TryReadWatermark(watermark, out var mark))
            return false;
        if (active is not { ValueKind: JsonValueKind.Array } entries || entries.GetArrayLength() > maxEntries)
            return false;

        var positions = new List<SymbolSearchPosition>(entries.GetArrayLength());
        foreach (var entry in entries.EnumerateArray())
        {
            if (!TryReadPosition(entry, out var position))
                return false;
            // Every position was admitted at or before the watermark, in strictly ascending hash order.
            if (mark is null || string.CompareOrdinal(position.IdentityHash, mark) > 0)
                return false;
            if (positions.Count > 0 && string.CompareOrdinal(positions[^1].IdentityHash, position.IdentityHash) >= 0)
                return false;
            positions.Add(position);
        }

        state = new SymbolSearchCursorState(positions, mark);
        digest = digestValue;
        return true;
    }

    private static bool TryReadWatermark(JsonElement? watermark, out string? mark)
    {
        mark = null;
        if (watermark is { ValueKind: JsonValueKind.Null })
            return true;
        if (watermark is not { ValueKind: JsonValueKind.String } w || !IsIdentityHash(w.GetString()))
            return false;
        mark = w.GetString();
        return true;
    }

    private static bool TryReadPosition(JsonElement entry, out SymbolSearchPosition position)
    {
        position = null!;
        if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() != 2)
            return false;
        var hash = entry[0];
        var after = entry[1];
        if (hash.ValueKind != JsonValueKind.String || hash.GetString() is not { } hashValue || !IsIdentityHash(hashValue))
            return false;
        if (after.ValueKind != JsonValueKind.Number || !after.TryGetInt64(out var afterId) || afterId < 0)
            return false;
        position = new SymbolSearchPosition(hashValue, afterId);
        return true;
    }

    // SHA-256 over the binding and the canonical form of the state. Hashes are validated hex and positions integers,
    // so the canonical form is unambiguous.
    private static string Digest(SymbolSearchCursorState state, string binding)
    {
        var canonical = new StringBuilder();
        canonical.Append(binding).Append("|v=").Append(Version).Append(";w=").Append(state.Watermark).Append(";a=");
        foreach (var position in state.Active)
            canonical.Append(position.IdentityHash).Append(':').Append(position.AfterId).Append(',');
        return Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }
}
