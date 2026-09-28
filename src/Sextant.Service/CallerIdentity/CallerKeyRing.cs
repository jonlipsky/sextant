using System.Buffers.Text;
using System.Text.RegularExpressions;

namespace Sextant.Service.CallerIdentity;

/// <summary>
/// The HMAC keys that sign caller assertions (SVC-3), each bound to exactly ONE tenant. A key id (<c>kid</c>) maps
/// to one key and one tenant; several key ids may map to the same tenant (rotation). The kid → tenant binding is
/// the multi-tenant guard: an assertion's <c>tid</c> must equal the tenant of the key that signed it.
/// <para>
/// Key material never leaves this type: there is no public accessor for it, <see cref="ToString"/> reports only
/// the key count, and every validation message names an entry by its position only — never its key id (an entry
/// missing its key id would otherwise put the key where the key id belongs), the key, or anything derived from it.
/// </para>
/// </summary>
public sealed partial class CallerKeyRing
{
    /// <summary>The minimum decoded key length, in bytes.</summary>
    public const int MinimumKeyBytes = 32;

    private const int MaximumTenantIdLength = 128;

    private readonly Dictionary<string, (string TenantId, byte[] Key)> _keys;

    private CallerKeyRing(Dictionary<string, (string TenantId, byte[] Key)> keys) => _keys = keys;

    /// <summary>The empty key ring: no assertion can verify against it.</summary>
    public static CallerKeyRing Empty { get; } = new(new Dictionary<string, (string, byte[])>(StringComparer.Ordinal));

    /// <summary>How many keys the ring holds.</summary>
    public int Count => _keys.Count;

    /// <summary>The key ids the ring holds.</summary>
    public IReadOnlyCollection<string> KeyIds => _keys.Keys;

    /// <summary>The tenant a key id is bound to, or null when the key id is unknown.</summary>
    public string? TenantOf(string keyId) => _keys.TryGetValue(keyId, out var entry) ? entry.TenantId : null;

    internal bool TryGetKey(string keyId, out string tenantId, out byte[] key)
    {
        if (_keys.TryGetValue(keyId, out var entry))
        {
            (tenantId, key) = entry;
            return true;
        }
        tenantId = "";
        key = [];
        return false;
    }

    /// <summary>
    /// Builds a key ring from in-memory keys. Each key id must be well-formed and unique, each key at least
    /// <see cref="MinimumKeyBytes"/> long, and each tenant id well-formed. The key bytes are copied.
    /// </summary>
    /// <exception cref="FormatException">An entry is invalid. The message never contains key material.</exception>
    public static CallerKeyRing Create(IEnumerable<(string KeyId, ReadOnlyMemory<byte> Key, string TenantId)> keys)
    {
        var ring = new Dictionary<string, (string, byte[])>(StringComparer.Ordinal);
        var index = 0;
        foreach (var (keyId, key, tenantId) in keys)
            Add(ring, ++index, keyId, key.ToArray(), tenantId);
        return new CallerKeyRing(ring);
    }

    /// <summary>
    /// Parses the <c>SEXTANT_SERVICE_CALLER_KEYS</c> wire form <c>kid=base64url@tenantId;kid2=base64url@tenantId</c>.
    /// The key is base64url (RFC 4648 §5; trailing <c>=</c> padding is tolerated) and must decode to at least
    /// <see cref="MinimumKeyBytes"/> bytes. A malformed entry fails the whole parse (fail closed).
    /// </summary>
    /// <exception cref="FormatException">The spec is blank or an entry is invalid. The message never contains key material.</exception>
    public static CallerKeyRing Parse(string spec)
    {
        var entries = spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Length == 0)
            throw new FormatException("No caller keys are listed; expected 'kid=base64url@tenantId' entries separated by ';'.");

        var ring = new Dictionary<string, (string, byte[])>(StringComparer.Ordinal);
        for (var i = 0; i < entries.Length; i++)
        {
            var index = i + 1;
            var entry = entries[i];
            var equals = entry.IndexOf('=');
            if (equals <= 0)
                throw Malformed(index);
            var keyId = entry[..equals];
            if (!IsValidKeyId(keyId))
                throw new FormatException($"Caller key entry #{index} has an invalid key id (expected [A-Za-z0-9._-]{{1,64}}).");
            var rest = entry[(equals + 1)..];
            var at = rest.IndexOf('@');
            if (at <= 0)
                throw Malformed(index);
            if (!TryDecodeKey(rest[..at], out var key))
                throw new FormatException(
                    $"Caller key entry #{index} is not base64url. Use the URL-safe alphabet " +
                    "('-' and '_' in place of '+' and '/'), and check the entry has a key id before the '='.");
            Add(ring, index, keyId, key, rest[(at + 1)..]);
        }
        return new CallerKeyRing(ring);
    }

    /// <summary>True when <paramref name="keyId"/> matches <c>[A-Za-z0-9._-]{1,64}</c>.</summary>
    public static bool IsValidKeyId(string? keyId) => keyId is not null && KeyIdPattern().IsMatch(keyId);

    /// <summary>Reports only the key count, never key material.</summary>
    public override string ToString() => $"CallerKeyRing({Count} key(s))";

    private static void Add(Dictionary<string, (string, byte[])> ring, int index, string keyId, byte[] key, string tenantId)
    {
        if (!IsValidKeyId(keyId))
            throw new FormatException($"Caller key #{index} has an invalid key id (expected [A-Za-z0-9._-]{{1,64}}).");
        if (key.Length < MinimumKeyBytes)
            throw new FormatException($"Caller key entry #{index} is shorter than {MinimumKeyBytes} bytes.");
        if (tenantId.Length > MaximumTenantIdLength || !TenantIdPattern().IsMatch(tenantId))
            throw new FormatException(
                $"Caller key entry #{index} has an invalid tenant id (expected " +
                $"[A-Za-z0-9._-]{{1,{MaximumTenantIdLength}}}).");
        if (!ring.TryAdd(keyId, (tenantId, key)))
            throw new FormatException($"Caller key entry #{index} repeats an earlier entry's key id; each key id must be unique.");
    }

    private static FormatException Malformed(int index) =>
        new($"Caller key entry #{index} is malformed; expected 'kid=base64url@tenantId'.");

    private static bool TryDecodeKey(string text, out byte[] key)
    {
        key = [];
        var unpadded = text.TrimEnd('=');
        if (unpadded.Length == 0 || text.Length - unpadded.Length > 2 || !Base64Url.IsValid(unpadded))
            return false;
        foreach (var c in unpadded)
        {
            if (!IsBase64UrlChar(c))
                return false;
        }
        key = Base64Url.DecodeFromChars(unpadded);
        return true;
    }

    internal static bool IsBase64UrlChar(char c) =>
        c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_';

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyIdPattern();

    [GeneratedRegex("^[A-Za-z0-9._-]+\\z", RegexOptions.CultureInvariant)]
    private static partial Regex TenantIdPattern();
}
