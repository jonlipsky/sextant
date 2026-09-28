using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sextant.Service.CallerIdentity;

/// <summary>What a caller-assertion verification decided.</summary>
public enum CallerAssertionOutcome
{
    /// <summary>The assertion verified and passed the policy checks.</summary>
    Verified,

    /// <summary>The assertion failed verification (steps 1-9): the request is unauthenticated (401).</summary>
    Invalid,

    /// <summary>The assertion verified, but the caller's idp or app is not allowed (steps 10-11): forbidden (403).</summary>
    NotAllowed
}

/// <summary>
/// The result of <see cref="CallerAssertionVerifier.Verify"/>. <see cref="Reason"/> is a log-only reason code
/// (see <see cref="CallerAssertionReasons"/>) and must never be sent to the caller. <see cref="KeyId"/> is set
/// only when the assertion named a CONFIGURED key id (an unknown one is never echoed), and <see cref="Jti"/> only
/// once the signature verified, so a log line built from this result never repeats attacker-chosen text.
/// </summary>
public sealed record CallerAssertionResult(
    CallerAssertionOutcome Outcome, CallerPrincipal? Principal, string? Reason, string? KeyId, string? Jti);

/// <summary>The log-only reason codes of a refused caller assertion.</summary>
public static class CallerAssertionReasons
{
    public const string Malformed = "malformed";
    public const string Oversized = "oversized";
    public const string DuplicateHeader = "duplicate_header";
    public const string BadHeader = "bad_jose_header";
    public const string AlgorithmNotAllowed = "alg_not_allowed";
    public const string TypeNotAllowed = "typ_not_allowed";
    public const string HeaderParameterNotAllowed = "jose_parameter_not_allowed";
    public const string UnknownKeyId = "unknown_kid";
    public const string BadSignature = "bad_signature";
    public const string BadPayload = "bad_payload";
    public const string BadAudience = "bad_audience";
    public const string BadIssuer = "bad_issuer";
    public const string BadTimes = "bad_times";
    public const string Expired = "expired";
    public const string NotYetValid = "not_yet_valid";
    public const string IssuedInFuture = "issued_in_future";
    public const string LifetimeTooLong = "lifetime_too_long";
    public const string TenantMismatch = "tenant_mismatch";
    public const string BadActor = "bad_actor";
    public const string BadSubjectNamespace = "bad_subject_namespace";
    public const string BadClaims = "bad_claims";
    public const string IdpNotAllowed = "idp_not_allowed";
    public const string AppNotAllowed = "app_not_allowed";
}

/// <summary>
/// Verifies caller assertions (SVC-3): a compact JWS, HMAC-SHA256 signed with a key from
/// <see cref="CallerAssertionOptions.Keys"/>, whose claims say which tenant and user (or application) a request is
/// for. Pure and thread-safe; the HTTP host applies it to the configured header.
/// <para>
/// The checks run in a fixed order and stop at the first failure. Steps 1-9 make the assertion
/// <see cref="CallerAssertionOutcome.Invalid"/>: (1) three canonical base64url segments, at most
/// <see cref="MaxAssertionLength"/> characters; (2) a JOSE header of exactly <c>alg: HS256</c>, <c>typ</c> absent or
/// <c>JWT</c>, no key-carrying or critical parameter, and a configured <c>kid</c>; (3) the HMAC over
/// <c>header.payload</c>, compared in constant time BEFORE any claim is read; (4) <c>aud</c> and, when configured,
/// <c>iss</c>; (5) <c>nbf</c>/<c>exp</c>/<c>iat</c> within <see cref="ClockSkewSeconds"/> of now and a lifetime of at
/// most <see cref="MaxLifetimeSeconds"/>; (6) <c>tid</c> equals the tenant the kid is bound to; (7) <c>act</c> is
/// <c>user</c> (with <c>idp</c> and <c>sub</c>) or <c>application</c> (with neither); (8) <c>sub</c> is namespaced by
/// its <c>idp</c>; (9) <c>jti</c>, <c>via</c>, <c>tslug</c>, <c>app</c> and <c>cid</c> are present, and <c>dep</c>
/// is either absent or a non-empty string (the signer sends it only when the calling run is bound to a
/// deployment). Steps 10-11 are policy and make it <see cref="CallerAssertionOutcome.NotAllowed"/>: (10) a user's
/// <c>idp</c> is in <see cref="CallerAssertionOptions.Idps"/>; (11) <c>app</c> is in
/// <see cref="CallerAssertionOptions.Apps"/> when that is set. Unknown claims are ignored.
/// </para>
/// </summary>
public sealed class CallerAssertionVerifier
{
    /// <summary>The largest accepted assertion, in characters (8 KiB).</summary>
    public const int MaxAssertionLength = 8 * 1024;

    /// <summary>The allowed clock skew, in seconds.</summary>
    public const long ClockSkewSeconds = 60;

    /// <summary>The longest accepted lifetime (<c>exp - iat</c>), in seconds.</summary>
    public const long MaxLifetimeSeconds = 300;

    private const int SignatureLength = 32;

    // Numeric dates outside [0, year 9999] are refused, so the timing arithmetic below cannot overflow.
    private const long MaxNumericDate = 253_402_300_799;

    private static readonly string[] ForbiddenHeaderParameters = ["jku", "jwk", "x5u", "x5c", "crit"];

    private static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = 8 };

    private readonly CallerAssertionOptions _options;
    private readonly TimeProvider _time;

    public CallerAssertionVerifier(CallerAssertionOptions options, TimeProvider? timeProvider = null)
    {
        _options = options;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Verifies one assertion (the raw header value).</summary>
    public CallerAssertionResult Verify(string? assertion)
    {
        // Step 1: shape and size, before any decoding.
        if (string.IsNullOrEmpty(assertion))
            return Invalid(CallerAssertionReasons.Malformed);
        if (assertion.Length > MaxAssertionLength)
            return Invalid(CallerAssertionReasons.Oversized);
        var firstDot = assertion.IndexOf('.');
        var secondDot = firstDot < 0 ? -1 : assertion.IndexOf('.', firstDot + 1);
        if (firstDot <= 0 || secondDot <= firstDot + 1 || secondDot == assertion.Length - 1
            || assertion.IndexOf('.', secondDot + 1) >= 0)
            return Invalid(CallerAssertionReasons.Malformed);
        if (!TryDecodeSegment(assertion.AsSpan(0, firstDot), out var headerBytes)
            || !TryDecodeSegment(assertion.AsSpan(firstDot + 1, secondDot - firstDot - 1), out var payloadBytes)
            || !TryDecodeSegment(assertion.AsSpan(secondDot + 1), out var signature))
            return Invalid(CallerAssertionReasons.Malformed);

        // Step 2: the JOSE header.
        if (!TryParseObject(headerBytes, out var header))
            return Invalid(CallerAssertionReasons.BadHeader);
        using (header)
        {
            var headerCheck = CheckHeader(header.RootElement, out var keyId, out var tenantId, out var key);
            if (headerCheck is not null)
                return headerCheck;

            // Step 3: the signature, in constant time, before any claim is trusted.
            if (signature.Length != SignatureLength || !SignatureMatches(key, assertion.AsSpan(0, secondDot), signature))
                return Invalid(CallerAssertionReasons.BadSignature, keyId);

            if (!TryParseObject(payloadBytes, out var payload))
                return Invalid(CallerAssertionReasons.BadPayload, keyId);
            using (payload)
                return CheckClaims(payload.RootElement, keyId, tenantId);
        }
    }

    private CallerAssertionResult? CheckHeader(JsonElement header, out string keyId, out string tenantId, out byte[] key)
    {
        keyId = "";
        tenantId = "";
        key = [];
        if (StringOf(header, "alg") != "HS256")
            return Invalid(CallerAssertionReasons.AlgorithmNotAllowed);
        if (header.TryGetProperty("typ", out var typ) && (typ.ValueKind != JsonValueKind.String || typ.GetString() != "JWT"))
            return Invalid(CallerAssertionReasons.TypeNotAllowed);
        foreach (var parameter in ForbiddenHeaderParameters)
        {
            if (header.TryGetProperty(parameter, out _))
                return Invalid(CallerAssertionReasons.HeaderParameterNotAllowed);
        }
        var kid = StringOf(header, "kid");
        if (kid is null || !CallerKeyRing.IsValidKeyId(kid) || !_options.Keys.TryGetKey(kid, out tenantId, out key))
            return Invalid(CallerAssertionReasons.UnknownKeyId);
        keyId = kid;
        return null;
    }

    private CallerAssertionResult CheckClaims(JsonElement claims, string keyId, string keyTenant)
    {
        // Read only for logging from here on: the signature has verified, so it is the signer's value.
        var jti = StringOf(claims, "jti");
        var loggedJti = jti is { Length: > 0 } ? jti : null;

        // Step 4: audience and issuer.
        if (!AudienceMatches(claims))
            return Invalid(CallerAssertionReasons.BadAudience, keyId, loggedJti);
        if (_options.Issuers.Count > 0 && (StringOf(claims, "iss") is not { } issuer || !_options.Issuers.Contains(issuer)))
            return Invalid(CallerAssertionReasons.BadIssuer, keyId, loggedJti);

        // Step 5: timing, with skew.
        if (!TryNumericDate(claims, "nbf", out var nbf) || !TryNumericDate(claims, "exp", out var exp)
            || !TryNumericDate(claims, "iat", out var iat))
            return Invalid(CallerAssertionReasons.BadTimes, keyId, loggedJti);
        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        if (now > exp + ClockSkewSeconds)
            return Invalid(CallerAssertionReasons.Expired, keyId, loggedJti);
        if (nbf - ClockSkewSeconds > now)
            return Invalid(CallerAssertionReasons.NotYetValid, keyId, loggedJti);
        if (iat > now + ClockSkewSeconds)
            return Invalid(CallerAssertionReasons.IssuedInFuture, keyId, loggedJti);
        if (exp - iat > MaxLifetimeSeconds)
            return Invalid(CallerAssertionReasons.LifetimeTooLong, keyId, loggedJti);

        // Step 6: the multi-tenant guard. The key is bound to one tenant; any other tid means the key signed for
        // a tenant it does not belong to.
        if (StringOf(claims, "tid") is not { } tid || !string.Equals(tid, keyTenant, StringComparison.Ordinal))
            return Invalid(CallerAssertionReasons.TenantMismatch, keyId, loggedJti);

        // Step 7: the actor and the claims tied to it. For an application, idp and sub must be ABSENT (a JSON null
        // counts as present).
        var act = StringOf(claims, "act");
        CallerActor actor;
        string? idp = null;
        string? sub = null;
        switch (act)
        {
            case "user":
                idp = StringOf(claims, "idp");
                sub = StringOf(claims, "sub");
                if (string.IsNullOrEmpty(idp) || string.IsNullOrEmpty(sub))
                    return Invalid(CallerAssertionReasons.BadActor, keyId, loggedJti);
                actor = CallerActor.User;
                break;
            case "application":
                if (claims.TryGetProperty("idp", out _) || claims.TryGetProperty("sub", out _))
                    return Invalid(CallerAssertionReasons.BadActor, keyId, loggedJti);
                actor = CallerActor.Application;
                break;
            default:
                return Invalid(CallerAssertionReasons.BadActor, keyId, loggedJti);
        }

        // Step 8: the subject's namespace matches its idp, so a platform user id never collides with an external
        // peer id.
        if (actor == CallerActor.User && !SubjectMatchesIdp(idp!, sub!))
            return Invalid(CallerAssertionReasons.BadSubjectNamespace, keyId, loggedJti);

        // Step 9: the remaining claims.
        var via = StringOf(claims, "via");
        var tslug = StringOf(claims, "tslug");
        var app = StringOf(claims, "app");
        var cid = StringOf(claims, "cid");
        if (loggedJti is null || via is not ("mcp-surface" or "activity")
            || tslug is null || app is null || cid is null
            || !TryOptionalString(claims, "run", out var run)
            || !TryOptionalString(claims, "dep", out var dep) || dep is { Length: 0 })
            return Invalid(CallerAssertionReasons.BadClaims, keyId, loggedJti);

        // Steps 10-11: policy.
        if (actor == CallerActor.User && !_options.Idps.Contains(idp!))
            return new CallerAssertionResult(CallerAssertionOutcome.NotAllowed, null, CallerAssertionReasons.IdpNotAllowed, keyId, loggedJti);
        if (_options.Apps.Count > 0 && !_options.Apps.Contains(app))
            return new CallerAssertionResult(CallerAssertionOutcome.NotAllowed, null, CallerAssertionReasons.AppNotAllowed, keyId, loggedJti);

        var principal = new CallerPrincipal
        {
            TenantId = tid,
            TenantSlug = tslug,
            Actor = actor,
            Idp = idp,
            UserId = sub,
            App = app,
            Deployment = dep,
            Connection = cid,
            Via = via,
            Run = run,
            KeyId = keyId,
            Jti = loggedJti
        };
        return new CallerAssertionResult(CallerAssertionOutcome.Verified, principal, null, keyId, loggedJti);
    }

    /// <summary>
    /// The namespace guard (step 8). The platform's own idp names a platform user id, which never contains
    /// <c>:</c>. Any other idp names an external peer as <c>{idp}:{connectionInstanceId}:{peerId}</c> with both later
    /// segments non-empty (the peer id may itself contain <c>:</c>).
    /// </summary>
    internal static bool SubjectMatchesIdp(string idp, string sub)
    {
        if (string.Equals(idp, CallerAssertionOptions.PlatformIdp, StringComparison.Ordinal))
            return !sub.Contains(':', StringComparison.Ordinal);

        var prefix = idp + ":";
        if (!sub.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var rest = sub.AsSpan(prefix.Length);
        var separator = rest.IndexOf(':');
        return separator > 0 && separator < rest.Length - 1;
    }

    private bool AudienceMatches(JsonElement claims)
    {
        var audience = _options.Audience;
        if (string.IsNullOrEmpty(audience) || !claims.TryGetProperty("aud", out var aud))
            return false;
        switch (aud.ValueKind)
        {
            case JsonValueKind.String:
                return aud.GetString() == audience;
            case JsonValueKind.Array:
                var found = false;
                foreach (var entry in aud.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.String)
                        return false;
                    found |= entry.GetString() == audience;
                }
                return found;
            default:
                return false;
        }
    }

    private static bool SignatureMatches(byte[] key, ReadOnlySpan<char> signingInput, byte[] signature)
    {
        // The signing input was already checked to be base64url and dots, so it is pure ASCII.
        var input = new byte[signingInput.Length];
        Encoding.ASCII.GetBytes(signingInput, input);
        Span<byte> expected = stackalloc byte[SignatureLength];
        HMACSHA256.HashData(key, input, expected);
        return CryptographicOperations.FixedTimeEquals(expected, signature);
    }

    /// <summary>
    /// Decodes one compact-JWS segment: unpadded base64url only (no whitespace, padding or standard-alphabet
    /// characters) and in canonical form, so exactly one string encodes each byte sequence.
    /// </summary>
    internal static bool TryDecodeSegment(ReadOnlySpan<char> segment, out byte[] bytes)
    {
        bytes = [];
        if (segment.IsEmpty || segment.Length % 4 == 1)
            return false;
        foreach (var c in segment)
        {
            if (!CallerKeyRing.IsBase64UrlChar(c))
                return false;
        }
        try
        {
            bytes = Base64Url.DecodeFromChars(segment);
        }
        catch (FormatException)
        {
            return false;
        }
        return segment.SequenceEqual(Base64Url.EncodeToString(bytes));
    }

    /// <summary>
    /// Parses a JSON object whose top-level member names are all distinct and whose every string (member names
    /// included, at any depth) decodes to UTF-16. <c>JsonDocument.Parse</c>
    /// defers decoding, so a lone-surrogate escape or an invalid UTF-8 byte only throws when the string is read;
    /// checking every string here means no later read can throw.
    /// </summary>
    private static bool TryParseObject(byte[] json, out JsonDocument document)
    {
        document = null!;
        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(json, ParseOptions);
        }
        catch (JsonException)
        {
            return false;
        }
        try
        {
            if (parsed.RootElement.ValueKind == JsonValueKind.Object && AllStringsDecode(parsed.RootElement)
                && TopLevelNamesDistinct(parsed.RootElement))
            {
                document = parsed;
                return true;
            }
        }
        catch (InvalidOperationException)
        {
            // An undecodable string: fall through to the refusal.
        }
        parsed.Dispose();
        return false;
    }

    // Reads every member name and string value, so an undecodable one throws here. The depth is bounded by
    // ParseOptions.MaxDepth.
    private static bool AllStringsDecode(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    _ = property.Name;
                    if (!AllStringsDecode(property.Value))
                        return false;
                }
                return true;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (!AllStringsDecode(item))
                        return false;
                }
                return true;
            case JsonValueKind.String:
                _ = element.GetString();
                return true;
            default:
                return true;
        }
    }

    private static bool TopLevelNamesDistinct(JsonElement root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!names.Add(property.Name))
                return false;
        }
        return true;
    }

    private static string? StringOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // An absent claim is fine; a present one must be a string.
    private static bool TryOptionalString(JsonElement element, string name, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property))
            return true;
        if (property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString();
        return true;
    }

    private static bool TryNumericDate(JsonElement element, string name, out long value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property)
               && property.ValueKind == JsonValueKind.Number
               && property.TryGetInt64(out value)
               && value is >= 0 and <= MaxNumericDate;
    }

    private static CallerAssertionResult Invalid(string reason, string? keyId = null, string? jti = null) =>
        new(CallerAssertionOutcome.Invalid, null, reason, keyId, jti);
}
