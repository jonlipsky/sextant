using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sextant.Service.CallerIdentity;

namespace Sextant.Service.Tests;

/// <summary>
/// Mints caller assertions for tests: compact HS256 JWS over keys generated in-test (never a committed secret).
/// The claim set is a plain dictionary so a test can drop or change any claim before signing.
/// </summary>
internal static class CallerAssertionSigner
{
    public const string Audience = "sextant";
    public const string Issuer = "https://platform.example.test";

    /// <summary>A fresh random 32-byte HMAC key.</summary>
    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(CallerKeyRing.MinimumKeyBytes);

    /// <summary>One <c>SEXTANT_SERVICE_CALLER_KEYS</c> entry for <paramref name="key"/>.</summary>
    public static string KeySpec(string keyId, byte[] key, string tenantId) =>
        $"{keyId}={Base64Url.EncodeToString(key)}@{tenantId}";

    /// <summary>A valid user claim set (<c>idp=processstack</c>), issued at <paramref name="now"/>.</summary>
    public static Dictionary<string, object?> UserClaims(
        DateTimeOffset now, string tenantId = "tenant-a", string sub = "user-1", string jti = "jti-1")
    {
        var claims = ApplicationClaims(now, tenantId, jti);
        claims["act"] = "user";
        claims["idp"] = CallerAssertionOptions.PlatformIdp;
        claims["sub"] = sub;
        return claims;
    }

    /// <summary>A valid application claim set (no <c>idp</c>/<c>sub</c>), issued at <paramref name="now"/>.</summary>
    public static Dictionary<string, object?> ApplicationClaims(DateTimeOffset now, string tenantId = "tenant-a", string jti = "jti-1")
    {
        var seconds = now.ToUnixTimeSeconds();
        return new Dictionary<string, object?>
        {
            ["iss"] = Issuer,
            ["aud"] = Audience,
            ["tid"] = tenantId,
            ["tslug"] = "acme",
            ["act"] = "application",
            ["app"] = "sextant",
            ["dep"] = "dep-1",
            ["cid"] = "conn-1",
            ["via"] = "mcp-surface",
            ["jti"] = jti,
            ["iat"] = seconds,
            ["nbf"] = seconds,
            ["exp"] = seconds + 120
        };
    }

    /// <summary>The standard JOSE header for <paramref name="keyId"/>.</summary>
    public static Dictionary<string, object?> Header(string keyId) =>
        new() { ["alg"] = "HS256", ["typ"] = "JWT", ["kid"] = keyId };

    /// <summary>Signs <paramref name="claims"/> under <paramref name="keyId"/> with <paramref name="key"/>.</summary>
    public static string Sign(byte[] key, string keyId, IReadOnlyDictionary<string, object?> claims) =>
        Sign(key, Header(keyId), claims);

    public static string Sign(byte[] key, IReadOnlyDictionary<string, object?> header, IReadOnlyDictionary<string, object?> claims) =>
        SignRaw(key, JsonSerializer.Serialize(header), JsonSerializer.Serialize(claims));

    /// <summary>Signs raw JSON texts, for malformed-JSON and duplicate-member vectors.</summary>
    public static string SignRaw(byte[] key, string headerJson, string payloadJson) =>
        SignRawBytes(key, Encoding.UTF8.GetBytes(headerJson), Encoding.UTF8.GetBytes(payloadJson));

    /// <summary>Signs raw header/payload bytes, for vectors that are not valid UTF-8.</summary>
    public static string SignRawBytes(byte[] key, byte[] header, byte[] payload)
    {
        var signingInput = $"{Base64Url.EncodeToString(header)}.{Base64Url.EncodeToString(payload)}";
        var signature = HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(signingInput));
        return $"{signingInput}.{Base64Url.EncodeToString(signature)}";
    }

    public static string Encode(string json) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));
}

/// <summary>A clock frozen at one instant.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
