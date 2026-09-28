using System.Buffers.Text;
using System.Security.Cryptography;
using Sextant.Service.CallerIdentity;
using static Sextant.Service.Tests.CallerAssertionSigner;

namespace Sextant.Service.Tests;

/// <summary>
/// SVC-3: the caller-assertion verifier's vectors. Keys are generated per test run; the tenants are the fake
/// <c>tenant-a</c>/<c>tenant-b</c>. Each negative vector changes ONE thing about an otherwise valid assertion and
/// pins the step that refuses it.
/// </summary>
[TestClass]
public class CallerAssertionVerifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] KeyA = NewKey();
    private static readonly byte[] KeyA2 = NewKey();
    private static readonly byte[] KeyB = NewKey();

    private static CallerAssertionOptions Options(IReadOnlySet<string>? idps = null, IReadOnlySet<string>? apps = null,
        IReadOnlySet<string>? issuers = null) => new()
    {
        Keys = CallerKeyRing.Create([("kid-a", KeyA, "tenant-a"), ("kid-a2", KeyA2, "tenant-a"), ("kid-b", KeyB, "tenant-b")]),
        Audience = Audience,
        Issuers = issuers ?? new HashSet<string> { Issuer },
        Idps = idps ?? new HashSet<string> { CallerAssertionOptions.PlatformIdp },
        Apps = apps ?? new HashSet<string>()
    };

    private static CallerAssertionResult Verify(string? assertion, CallerAssertionOptions? options = null) =>
        new CallerAssertionVerifier(options ?? Options(), new FixedTimeProvider(Now)).Verify(assertion);

    private static void AssertInvalid(CallerAssertionResult result, string reason)
    {
        Assert.AreEqual(CallerAssertionOutcome.Invalid, result.Outcome, result.Reason);
        Assert.AreEqual(reason, result.Reason);
        Assert.IsNull(result.Principal);
    }

    // ==== positive vectors ===========================================================================

    [TestMethod]
    public void UserCaller_Verifies_WithEveryClaim()
    {
        var claims = UserClaims(Now);
        claims["run"] = "run-7";
        var result = Verify(Sign(KeyA, "kid-a", claims));

        Assert.AreEqual(CallerAssertionOutcome.Verified, result.Outcome, result.Reason);
        var p = result.Principal!;
        Assert.AreEqual("tenant-a", p.TenantId);
        Assert.AreEqual("acme", p.TenantSlug);
        Assert.AreEqual(CallerActor.User, p.Actor);
        Assert.AreEqual("processstack", p.Idp);
        Assert.AreEqual("user-1", p.UserId);
        Assert.AreEqual("sextant", p.App);
        Assert.AreEqual("dep-1", p.Deployment);
        Assert.AreEqual("conn-1", p.Connection);
        Assert.AreEqual("mcp-surface", p.Via);
        Assert.AreEqual("run-7", p.Run);
        Assert.AreEqual("kid-a", p.KeyId);
        Assert.AreEqual("jti-1", p.Jti);
        Assert.AreEqual("tenant-a/user-1", p.AuditPrincipal);
    }

    [TestMethod]
    public void ApplicationCaller_Verifies_WithoutIdpOrSubject()
    {
        var claims = ApplicationClaims(Now);
        claims["via"] = "activity";
        var result = Verify(Sign(KeyA, "kid-a", claims));

        Assert.AreEqual(CallerAssertionOutcome.Verified, result.Outcome, result.Reason);
        var p = result.Principal!;
        Assert.AreEqual(CallerActor.Application, p.Actor);
        Assert.IsNull(p.Idp);
        Assert.IsNull(p.UserId);
        Assert.IsNull(p.Run);
        Assert.AreEqual("activity", p.Via);
        Assert.AreEqual("tenant-a/app:sextant", p.AuditPrincipal);
    }

    [TestMethod]
    public void TwoKeyIdsForOneTenant_BothVerify()
    {
        Assert.AreEqual(CallerAssertionOutcome.Verified, Verify(Sign(KeyA, "kid-a", UserClaims(Now))).Outcome);
        var rotated = Verify(Sign(KeyA2, "kid-a2", UserClaims(Now)));
        Assert.AreEqual(CallerAssertionOutcome.Verified, rotated.Outcome, rotated.Reason);
        Assert.AreEqual("kid-a2", rotated.Principal!.KeyId);
        Assert.AreEqual("tenant-a", rotated.Principal.TenantId);
    }

    [TestMethod]
    public void OtherTenantsKey_VerifiesItsOwnTenant()
    {
        var result = Verify(Sign(KeyB, "kid-b", UserClaims(Now, tenantId: "tenant-b")));
        Assert.AreEqual(CallerAssertionOutcome.Verified, result.Outcome, result.Reason);
        Assert.AreEqual("tenant-b", result.Principal!.TenantId);
    }

    [TestMethod]
    public void ExternalIdp_WithNamespacedSubject_Verifies_WhenAllowed()
    {
        var claims = UserClaims(Now, sub: "slack:conn-1:U123:extra");
        claims["idp"] = "slack";
        var result = Verify(Sign(KeyA, "kid-a", claims), Options(idps: new HashSet<string> { "processstack", "slack" }));

        Assert.AreEqual(CallerAssertionOutcome.Verified, result.Outcome, result.Reason);
        Assert.AreEqual("slack:conn-1:U123:extra", result.Principal!.UserId, "the full sub is the user id");
    }

    [TestMethod]
    public void AudienceArray_ContainingTheAudience_Verifies()
    {
        var claims = UserClaims(Now);
        claims["aud"] = new[] { "other", Audience };
        Assert.AreEqual(CallerAssertionOutcome.Verified, Verify(Sign(KeyA, "kid-a", claims)).Outcome);
    }

    [TestMethod]
    public void AbsentTyp_AndUnknownClaims_AreAccepted()
    {
        var header = new Dictionary<string, object?> { ["alg"] = "HS256", ["kid"] = "kid-a" };
        var claims = UserClaims(Now);
        claims["future_claim"] = new { nested = true };
        Assert.AreEqual(CallerAssertionOutcome.Verified, Verify(Sign(KeyA, header, claims)).Outcome);
    }

    [TestMethod]
    public void NoIssuersConfigured_AcceptsAnyIssuer()
    {
        var claims = UserClaims(Now);
        claims["iss"] = "https://elsewhere.example.test";
        Assert.AreEqual(CallerAssertionOutcome.Verified,
            Verify(Sign(KeyA, "kid-a", claims), Options(issuers: new HashSet<string>())).Outcome);
    }

    [TestMethod]
    public void TimingWithinSkew_Verifies()
    {
        var claims = UserClaims(Now);
        var now = Now.ToUnixTimeSeconds();
        claims["iat"] = now - 200;
        claims["nbf"] = now - 200;
        claims["exp"] = now - 59; // expired, but within the 60 s skew
        Assert.AreEqual(CallerAssertionOutcome.Verified, Verify(Sign(KeyA, "kid-a", claims)).Outcome);

        claims = UserClaims(Now);
        claims["nbf"] = now + 60; // not yet valid, but within the skew
        claims["iat"] = now + 60;
        claims["exp"] = now + 360; // exactly the 300 s maximum lifetime
        Assert.AreEqual(CallerAssertionOutcome.Verified, Verify(Sign(KeyA, "kid-a", claims)).Outcome);
    }

    // ==== step 1: shape and size ======================================================================

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("onlyone")]
    [DataRow("two.segments")]
    [DataRow("a.b.c.d")]
    [DataRow(".b.c")]
    [DataRow("a..c")]
    [DataRow("a.b.")]
    public void MalformedShape_IsRefused(string? assertion) =>
        AssertInvalid(Verify(assertion), CallerAssertionReasons.Malformed);

    [TestMethod]
    public void SegmentOutsideBase64Url_IsRefused()
    {
        var valid = Sign(KeyA, "kid-a", UserClaims(Now));
        var parts = valid.Split('.');
        AssertInvalid(Verify($"{parts[0]}.{parts[1]}.{parts[2]}="), CallerAssertionReasons.Malformed);
        AssertInvalid(Verify($"{parts[0]}+.{parts[1]}.{parts[2]}"), CallerAssertionReasons.Malformed);
        AssertInvalid(Verify($"{parts[0]}.{parts[1]} .{parts[2]}"), CallerAssertionReasons.Malformed);
        AssertInvalid(Verify($"{parts[0]}.{parts[1]}.{parts[2]}AA"), CallerAssertionReasons.Malformed);
    }

    [TestMethod]
    public void NonCanonicalSegment_IsRefused()
    {
        // "QR" and "QQ" decode to the same byte ('A'); only the canonical form (zero trailing bits) is accepted,
        // so one byte sequence has exactly one encoding.
        Assert.IsTrue(CallerAssertionVerifier.TryDecodeSegment("QQ", out var canonical));
        CollectionAssert.AreEqual("A"u8.ToArray(), canonical);
        Assert.IsFalse(CallerAssertionVerifier.TryDecodeSegment("QR", out _));

        var valid = Sign(KeyA, "kid-a", UserClaims(Now));
        var parts = valid.Split('.');
        var last = parts[2][^1];
        var tweaked = parts[2][..^1] + (char)(last == 'A' ? 'B' : last - 1);
        var result = Verify($"{parts[0]}.{parts[1]}.{tweaked}");
        Assert.AreEqual(CallerAssertionOutcome.Invalid, result.Outcome);
        Assert.IsTrue(result.Reason is CallerAssertionReasons.Malformed or CallerAssertionReasons.BadSignature, result.Reason);
    }

    [TestMethod]
    public void OversizedAssertion_IsRefused_BeforeDecoding()
    {
        var claims = UserClaims(Now);
        claims["padding"] = new string('x', CallerAssertionVerifier.MaxAssertionLength);
        var assertion = Sign(KeyA, "kid-a", claims);
        Assert.IsTrue(assertion.Length > CallerAssertionVerifier.MaxAssertionLength);
        AssertInvalid(Verify(assertion), CallerAssertionReasons.Oversized);
    }

    // ==== step 2: the JOSE header =====================================================================

    [TestMethod]
    [DataRow("none")]
    [DataRow("HS512")]
    [DataRow("RS256")]
    [DataRow("hs256")]
    public void AlgorithmOtherThanHs256_IsRefused(string alg)
    {
        var header = Header("kid-a");
        header["alg"] = alg;
        AssertInvalid(Verify(Sign(KeyA, header, UserClaims(Now))), CallerAssertionReasons.AlgorithmNotAllowed);
    }

    [TestMethod]
    public void AlgNone_WithEmptySignature_IsRefused()
    {
        var unsigned = $"{Encode("""{"alg":"none","kid":"kid-a"}""")}.{Encode(System.Text.Json.JsonSerializer.Serialize(UserClaims(Now)))}.";
        AssertInvalid(Verify(unsigned), CallerAssertionReasons.Malformed);
    }

    [TestMethod]
    public void MissingAlg_IsRefused()
    {
        var header = Header("kid-a");
        header.Remove("alg");
        AssertInvalid(Verify(Sign(KeyA, header, UserClaims(Now))), CallerAssertionReasons.AlgorithmNotAllowed);
    }

    [TestMethod]
    [DataRow("jwt")]
    [DataRow("at+jwt")]
    public void TypOtherThanJwt_IsRefused(string typ)
    {
        var header = Header("kid-a");
        header["typ"] = typ;
        AssertInvalid(Verify(Sign(KeyA, header, UserClaims(Now))), CallerAssertionReasons.TypeNotAllowed);
    }

    [TestMethod]
    [DataRow("jku")]
    [DataRow("jwk")]
    [DataRow("x5u")]
    [DataRow("x5c")]
    [DataRow("crit")]
    public void KeyCarryingOrCriticalHeaderParameter_IsRefused(string parameter)
    {
        var header = Header("kid-a");
        header[parameter] = "https://keys.example.test";
        AssertInvalid(Verify(Sign(KeyA, header, UserClaims(Now))), CallerAssertionReasons.HeaderParameterNotAllowed);
    }

    [TestMethod]
    public void UnknownOrMissingKeyId_IsRefused_WithoutEchoingIt()
    {
        var unknown = Verify(Sign(KeyA, "kid-unknown", UserClaims(Now)));
        AssertInvalid(unknown, CallerAssertionReasons.UnknownKeyId);
        Assert.IsNull(unknown.KeyId, "an unconfigured kid is attacker-chosen text and is never echoed");
        Assert.IsNull(unknown.Jti);

        var header = Header("kid-a");
        header.Remove("kid");
        AssertInvalid(Verify(Sign(KeyA, header, UserClaims(Now))), CallerAssertionReasons.UnknownKeyId);

        header["kid"] = 42;
        AssertInvalid(Verify(Sign(KeyA, header, UserClaims(Now))), CallerAssertionReasons.UnknownKeyId);
    }

    [TestMethod]
    public void HeaderThatIsNotAJsonObject_IsRefused()
    {
        AssertInvalid(Verify(SignRaw(KeyA, "not json", "{}")), CallerAssertionReasons.BadHeader);
        AssertInvalid(Verify(SignRaw(KeyA, """["HS256"]""", "{}")), CallerAssertionReasons.BadHeader);
    }

    [TestMethod]
    public void DuplicateHeaderMember_IsRefused()
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(UserClaims(Now));
        AssertInvalid(Verify(SignRaw(KeyA, """{"alg":"HS256","kid":"kid-b","kid":"kid-a"}""", payload)),
            CallerAssertionReasons.BadHeader);
    }

    // The JSON parser defers string decoding, so an undecodable string must be refused up front rather than throw
    // (a 500) when a later step reads it.
    [TestMethod]
    [DataRow("""{"alg":"\uD800","kid":"kid-a"}""", DisplayName = "lone high surrogate in alg")]
    [DataRow("""{"alg":"HS256","kid":"\uDC00"}""", DisplayName = "lone low surrogate in kid")]
    [DataRow("""{"\uD800":1,"alg":"HS256","kid":"kid-a"}""", DisplayName = "lone surrogate member name")]
    [DataRow("""{"alg":"HS256","kid":"kid-a","x":{"y":["\uD800"]}}""", DisplayName = "nested lone surrogate")]
    [DataRow("""{"alg":"HS256","typ":"\uDBFF","kid":"kid-a"}""", DisplayName = "lone surrogate in typ")]
    public void HeaderWithAnUndecodableString_IsRefused(string header)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(UserClaims(Now));
        AssertInvalid(Verify(SignRaw(KeyA, header, payload)), CallerAssertionReasons.BadHeader);
    }

    [TestMethod]
    public void HeaderWithInvalidUtf8_IsRefused()
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(UserClaims(Now)));
        byte[][] headers =
        [
            [.. "{\"alg\":\"HS256\",\"kid\":\"kid-"u8, 0xFF, .. "\"}"u8],
            [.. "{\"alg\":\"HS"u8, 0xC3, 0x28, .. "\",\"kid\":\"kid-a\"}"u8],
            [.. "{\""u8, 0xED, 0xA0, 0x80, .. "\":1,\"alg\":\"HS256\",\"kid\":\"kid-a\"}"u8]
        ];
        foreach (var header in headers)
            AssertInvalid(Verify(SignRawBytes(KeyA, header, payload)), CallerAssertionReasons.BadHeader);
    }

    [TestMethod]
    [DataRow("sub", "\\uD800")]
    [DataRow("tid", "\\uDC00")]
    [DataRow("jti", "\\uD800")]
    public void SignedPayloadWithAnUndecodableString_IsRefused(string claim, string escaped)
    {
        var header = System.Text.Json.JsonSerializer.Serialize(Header("kid-a"));
        var claims = UserClaims(Now);
        claims.Remove(claim);
        var payload = System.Text.Json.JsonSerializer.Serialize(claims)[..^1] + $",\"{claim}\":\"{escaped}\"}}";
        var result = Verify(SignRaw(KeyA, header, payload));
        AssertInvalid(result, CallerAssertionReasons.BadPayload);
        Assert.IsNull(result.Jti);

        var nested = System.Text.Json.JsonSerializer.Serialize(UserClaims(Now))[..^1] + $",\"x\":[{{\"{escaped}\":1}}]}}";
        AssertInvalid(Verify(SignRaw(KeyA, header, nested)), CallerAssertionReasons.BadPayload);

        byte[] invalidUtf8 = [.. System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(claims)[..^1]),
            .. ",\""u8, .. System.Text.Encoding.UTF8.GetBytes(claim), .. "\":\"a"u8, 0xFE, .. "\"}"u8];
        AssertInvalid(Verify(SignRawBytes(KeyA, System.Text.Encoding.UTF8.GetBytes(header), invalidUtf8)),
            CallerAssertionReasons.BadPayload);
    }

    // ==== step 3: the signature =======================================================================

    [TestMethod]
    public void TamperedPayload_IsRefused()
    {
        var parts = Sign(KeyA, "kid-a", UserClaims(Now)).Split('.');
        var forged = UserClaims(Now, sub: "someone-else");
        var result = Verify($"{parts[0]}.{Encode(System.Text.Json.JsonSerializer.Serialize(forged))}.{parts[2]}");
        AssertInvalid(result, CallerAssertionReasons.BadSignature);
        Assert.AreEqual("kid-a", result.KeyId, "a configured kid is logged");
        Assert.IsNull(result.Jti, "no claim is read before the signature verifies");
    }

    [TestMethod]
    public void TamperedSignature_IsRefused()
    {
        var parts = Sign(KeyA, "kid-a", UserClaims(Now)).Split('.');
        var signature = Base64Url.DecodeFromChars(parts[2]);
        signature[0] ^= 0x01;
        AssertInvalid(Verify($"{parts[0]}.{parts[1]}.{Base64Url.EncodeToString(signature)}"), CallerAssertionReasons.BadSignature);
    }

    [TestMethod]
    public void TruncatedSignature_IsRefused()
    {
        var parts = Sign(KeyA, "kid-a", UserClaims(Now)).Split('.');
        var signature = Base64Url.DecodeFromChars(parts[2])[..16];
        AssertInvalid(Verify($"{parts[0]}.{parts[1]}.{Base64Url.EncodeToString(signature)}"), CallerAssertionReasons.BadSignature);
    }

    [TestMethod]
    public void SignedWithAnotherKidsKey_IsRefused()
    {
        AssertInvalid(Verify(Sign(KeyB, "kid-a", UserClaims(Now))), CallerAssertionReasons.BadSignature);
    }

    [TestMethod]
    public void ValidlySignedPayloadThatIsNotAJsonObject_IsRefused()
    {
        var header = System.Text.Json.JsonSerializer.Serialize(Header("kid-a"));
        AssertInvalid(Verify(SignRaw(KeyA, header, "[1,2]")), CallerAssertionReasons.BadPayload);
        AssertInvalid(Verify(SignRaw(KeyA, header, "{\"tid\":")), CallerAssertionReasons.BadPayload);
    }

    [TestMethod]
    public void DuplicatePayloadMember_IsRefused()
    {
        // A second "tid" must never let a signer's parser and this one disagree about the tenant.
        var header = System.Text.Json.JsonSerializer.Serialize(Header("kid-a"));
        var payload = System.Text.Json.JsonSerializer.Serialize(UserClaims(Now))[..^1] + ",\"tid\":\"tenant-b\"}";
        AssertInvalid(Verify(SignRaw(KeyA, header, payload)), CallerAssertionReasons.BadPayload);
    }

    // ==== step 4: audience and issuer =================================================================

    [TestMethod]
    public void WrongOrMissingAudience_IsRefused()
    {
        var claims = UserClaims(Now);
        claims["aud"] = "other";
        var result = Verify(Sign(KeyA, "kid-a", claims));
        AssertInvalid(result, CallerAssertionReasons.BadAudience);
        Assert.AreEqual("jti-1", result.Jti, "after the signature verifies, the signed jti is logged");

        claims["aud"] = new[] { "other", "another" };
        AssertInvalid(Verify(Sign(KeyA, "kid-a", claims)), CallerAssertionReasons.BadAudience);

        claims["aud"] = new object[] { Audience, 7 };
        AssertInvalid(Verify(Sign(KeyA, "kid-a", claims)), CallerAssertionReasons.BadAudience);

        claims.Remove("aud");
        AssertInvalid(Verify(Sign(KeyA, "kid-a", claims)), CallerAssertionReasons.BadAudience);
    }

    [TestMethod]
    public void WrongOrMissingIssuer_IsRefused_WhenIssuersAreConfigured()
    {
        var claims = UserClaims(Now);
        claims["iss"] = "https://elsewhere.example.test";
        AssertInvalid(Verify(Sign(KeyA, "kid-a", claims)), CallerAssertionReasons.BadIssuer);

        claims.Remove("iss");
        AssertInvalid(Verify(Sign(KeyA, "kid-a", claims)), CallerAssertionReasons.BadIssuer);
    }

    // ==== step 5: timing ==============================================================================

    [TestMethod]
    public void ExpiredBeyondSkew_IsRefused()
    {
        var claims = UserClaims(Now);
        var now = Now.ToUnixTimeSeconds();
        claims["iat"] = now - 200;
        claims["nbf"] = now - 200;
        claims["exp"] = now - 61;
        AssertInvalid(Verify(Sign(KeyA, "kid-a", claims)), CallerAssertionReasons.Expired);
    }

    [TestMethod]
    public void NotBeforeInTheFuture_IsRefused()
    {
        var claims = UserClaims(Now);
        var now = Now.ToUnixTimeSeconds();
        claims["nbf"] = now + 61;
        claims["exp"] = now + 200;
        AssertInvalid(Verify(Sign(KeyA, "kid-a", claims)), CallerAssertionReasons.NotYetValid);
    }

    [TestMethod]
    public void IssuedInTheFuture_IsRefused()
    {
        var claims = UserClaims(Now);
        var now = Now.ToUnixTimeSeconds();
        claims["iat"] = now + 61;
        claims["exp"] = now + 200;
        AssertInvalid(Verify(Sign(KeyA, "kid-a", claims)), CallerAssertionReasons.IssuedInFuture);
    }

    [TestMethod]
    public void LifetimeAbove300Seconds_IsRefused()
    {
        var claims = UserClaims(Now);
        claims["exp"] = Now.ToUnixTimeSeconds() + 301;
        AssertInvalid(Verify(Sign(KeyA, "kid-a", claims)), CallerAssertionReasons.LifetimeTooLong);
    }

    [TestMethod]
    [DataRow("exp", null)]
    [DataRow("nbf", null)]
    [DataRow("iat", null)]
    [DataRow("exp", "soon")]
    [DataRow("exp", 1.5)]
    [DataRow("iat", -1L)]
    [DataRow("exp", 253402300800L)]
    public void MissingOrMalformedNumericDate_IsRefused(string claim, object? value)
    {
        var claims = UserClaims(Now);
        if (value is null)
            claims.Remove(claim);
        else
            claims[claim] = value;
        AssertInvalid(Verify(Sign(KeyA, "kid-a", claims)), CallerAssertionReasons.BadTimes);
    }

    // ==== step 6: the multi-tenant guard ==============================================================

    [TestMethod]
    public void TenantOtherThanTheKeysTenant_IsRefused()
    {
        // kid-a is bound to tenant-a: a valid signature over tid=tenant-b must not act for tenant-b.
        var result = Verify(Sign(KeyA, "kid-a", UserClaims(Now, tenantId: "tenant-b")));
        AssertInvalid(result, CallerAssertionReasons.TenantMismatch);

        var claims = UserClaims(Now);
        claims.Remove("tid");
        AssertInvalid(Verify(Sign(KeyA, "kid-a", claims)), CallerAssertionReasons.TenantMismatch);

        claims["tid"] = "Tenant-A";
        AssertInvalid(Verify(Sign(KeyA, "kid-a", claims)), CallerAssertionReasons.TenantMismatch);
    }

    // ==== step 7: the actor ===========================================================================

    [TestMethod]
    [DataRow("sub")]
    [DataRow("idp")]
    public void UserWithoutIdpOrSubject_IsRefused(string missing)
    {
        var claims = UserClaims(Now);
        claims.Remove(missing);
        AssertInvalid(Verify(Sign(KeyA, "kid-a", claims)), CallerAssertionReasons.BadActor);

        claims[missing] = "";
        AssertInvalid(Verify(Sign(KeyA, "kid-a", claims)), CallerAssertionReasons.BadActor);
    }

    [TestMethod]
    [DataRow("sub", "user-1")]
    [DataRow("idp", "processstack")]
    [DataRow("sub", null)]
    public void ApplicationWithIdpOrSubject_IsRefused(string claim, string? value)
    {
        var claims = ApplicationClaims(Now);
        claims[claim] = value;
        AssertInvalid(Verify(Sign(KeyA, "kid-a", claims)), CallerAssertionReasons.BadActor);
    }

    [TestMethod]
    [DataRow("admin")]
    [DataRow("User")]
    [DataRow(null)]
    public void UnknownActor_IsRefused(string? act)
    {
        var claims = UserClaims(Now);
        if (act is null)
            claims.Remove("act");
        else
            claims["act"] = act;
        AssertInvalid(Verify(Sign(KeyA, "kid-a", claims)), CallerAssertionReasons.BadActor);
    }

    // ==== step 8: the subject namespace guard =========================================================

    [TestMethod]
    [DataRow("processstack", "user-1", true)]
    [DataRow("processstack", "slack:conn-1:U123", false)]
    [DataRow("processstack", "user:1", false)]
    [DataRow("slack", "slack:conn-1:U123", true)]
    [DataRow("slack", "slack:conn-1:U1:23", true)]
    [DataRow("slack", "U123", false)]
    [DataRow("slack", "teams:conn-1:U123", false)]
    [DataRow("slack", "slack:conn-1", false)]
    [DataRow("slack", "slack:conn-1:", false)]
    [DataRow("slack", "slack::U123", false)]
    [DataRow("slack", "slackx:conn-1:U123", false)]
    public void SubjectNamespace_MatchesIdp(string idp, string sub, bool expected) =>
        Assert.AreEqual(expected, CallerAssertionVerifier.SubjectMatchesIdp(idp, sub));

    [TestMethod]
    public void PlatformIdp_WithNamespacedLookingSubject_IsRefused()
    {
        var result = Verify(Sign(KeyA, "kid-a", UserClaims(Now, sub: "slack:conn-1:U123")));
        AssertInvalid(result, CallerAssertionReasons.BadSubjectNamespace);
    }

    [TestMethod]
    [DataRow("U123")]
    [DataRow("teams:conn-1:U123")]
    public void ExternalIdp_WithUnNamespacedOrWrongPrefixSubject_IsRefused(string sub)
    {
        var claims = UserClaims(Now, sub: sub);
        claims["idp"] = "slack";
        // Refused at step 8 even though slack is allowed: the namespace guard precedes the policy check.
        AssertInvalid(Verify(Sign(KeyA, "kid-a", claims), Options(idps: new HashSet<string> { "processstack", "slack" })),
            CallerAssertionReasons.BadSubjectNamespace);
    }

    // ==== step 9: the remaining claims ================================================================

    [TestMethod]
    [DataRow("jti", null)]
    [DataRow("jti", "")]
    [DataRow("via", null)]
    [DataRow("via", "web")]
    [DataRow("tslug", null)]
    [DataRow("app", null)]
    [DataRow("dep", null)]
    [DataRow("cid", null)]
    [DataRow("cid", 5)]
    [DataRow("run", 5)]
    public void MissingOrMalformedRecordedClaim_IsRefused(string claim, object? value)
    {
        var claims = UserClaims(Now);
        if (value is null)
            claims.Remove(claim);
        else
            claims[claim] = value;
        AssertInvalid(Verify(Sign(KeyA, "kid-a", claims)), CallerAssertionReasons.BadClaims);
    }

    // ==== steps 10-11: policy =========================================================================

    [TestMethod]
    public void ExternalIdp_UnderTheDefaultIdps_IsNotAllowed()
    {
        var claims = UserClaims(Now, sub: "slack:conn-1:U123");
        claims["idp"] = "slack";
        var result = Verify(Sign(KeyA, "kid-a", claims));

        Assert.AreEqual(CallerAssertionOutcome.NotAllowed, result.Outcome);
        Assert.AreEqual(CallerAssertionReasons.IdpNotAllowed, result.Reason);
        Assert.IsNull(result.Principal);
    }

    [TestMethod]
    public void AppOutsideTheAllowList_IsNotAllowed()
    {
        var apps = new HashSet<string> { "sextant" };
        var claims = UserClaims(Now);
        claims["app"] = "other";
        var result = Verify(Sign(KeyA, "kid-a", claims), Options(apps: apps));
        Assert.AreEqual(CallerAssertionOutcome.NotAllowed, result.Outcome);
        Assert.AreEqual(CallerAssertionReasons.AppNotAllowed, result.Reason);

        var application = ApplicationClaims(Now);
        application["app"] = "other";
        Assert.AreEqual(CallerAssertionOutcome.NotAllowed, Verify(Sign(KeyA, "kid-a", application), Options(apps: apps)).Outcome);

        Assert.AreEqual(CallerAssertionOutcome.Verified, Verify(Sign(KeyA, "kid-a", UserClaims(Now)), Options(apps: apps)).Outcome);
    }

    [TestMethod]
    public void ApplicationCaller_IsNotSubjectToTheIdpPolicy()
    {
        var result = Verify(Sign(KeyA, "kid-a", ApplicationClaims(Now)), Options(idps: new HashSet<string> { "slack" }));
        Assert.AreEqual(CallerAssertionOutcome.Verified, result.Outcome, result.Reason);
    }

    // ==== keys ========================================================================================

    [TestMethod]
    public void KeyRing_Parse_AcceptsRotationAndPadding()
    {
        // One standard trailing '=' of padding is tolerated on the key.
        var ring = CallerKeyRing.Parse(
            $"{KeySpec("kid-a", KeyA, "tenant-a")}; kid-a2={Base64Url.EncodeToString(KeyA2)}=@tenant-a;{KeySpec("kid-b", KeyB, "tenant-b")}");
        Assert.AreEqual(3, ring.Count);
        Assert.AreEqual("tenant-a", ring.TenantOf("kid-a2"));
        Assert.AreEqual("tenant-b", ring.TenantOf("kid-b"));
        Assert.IsNull(ring.TenantOf("kid-c"));
        Assert.AreEqual("CallerKeyRing(3 key(s))", ring.ToString(), "ToString never reports key material");
    }

    [TestMethod]
    public void KeyRing_Parse_RefusesInvalidEntries_WithoutKeyMaterial()
    {
        var shortKey = Base64Url.EncodeToString(NewKey()[..31]);
        var standard = Convert.ToBase64String([0xFB, 0xFF, .. NewKey()]); // contains '+' or '/'
        string[] specs =
        [
            "",
            " ; ",
            $"kid-a={shortKey}@tenant-a",
            $"{KeySpec("kid-a", KeyA, "tenant-a")};{KeySpec("kid-a", KeyB, "tenant-b")}",
            $"kid a={Base64Url.EncodeToString(KeyA)}@tenant-a",
            $"{new string('k', 65)}={Base64Url.EncodeToString(KeyA)}@tenant-a",
            $"kid-a={Base64Url.EncodeToString(KeyA)}@",
            $"kid-a={Base64Url.EncodeToString(KeyA)}@tenant a",
            $"kid-a={Base64Url.EncodeToString(KeyA)}",
            $"={Base64Url.EncodeToString(KeyA)}@tenant-a",
            $"kid-a={standard}@tenant-a",
            $"kid-a={Base64Url.EncodeToString(KeyA)}===@tenant-a"
        ];
        foreach (var spec in specs)
        {
            var ex = Assert.ThrowsExactly<FormatException>(() => CallerKeyRing.Parse(spec), spec);
            Assert.IsFalse(ex.Message.Contains(Base64Url.EncodeToString(KeyA), StringComparison.Ordinal), ex.Message);
            Assert.IsFalse(ex.Message.Contains(shortKey, StringComparison.Ordinal), ex.Message);
        }
    }

    [TestMethod]
    [DataRow(34)]
    [DataRow(37)]
    [DataRow(46)]
    public void KeyRing_Parse_EntryMissingItsKeyId_NeverEchoesThePaddedKey(int keyBytes)
    {
        // A '='-padded key pasted without "kid=" splits at its own padding, so the key lands where the key id goes
        // (it matches the key-id pattern). The message must name the entry by position only.
        var key = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(keyBytes));
        Assert.IsTrue(key.Length <= 64, "the key fits the key-id pattern");
        var spec = $"{KeySpec("kid-a", KeyA, "tenant-a")};{key}==@tenant-a";
        var ex = Assert.ThrowsExactly<FormatException>(() => CallerKeyRing.Parse(spec));
        Assert.IsFalse(ex.Message.Contains(key, StringComparison.Ordinal), ex.Message);
        StringAssert.Contains(ex.Message, "entry #2");
    }

    [TestMethod]
    public void KeyRing_Messages_NeverEchoTheKeyId()
    {
        // Every post-split message names the entry by position, because the "key id" may be a misplaced key.
        var kid = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(33));
        string[] specs =
        [
            $"{kid}={Base64Url.EncodeToString(NewKey()[..31])}@tenant-a",
            $"{kid}={Base64Url.EncodeToString(KeyA)}@tenant a",
            $"{kid}={Base64Url.EncodeToString(KeyA)}@tenant-a;{kid}={Base64Url.EncodeToString(KeyB)}@tenant-b",
            $"{kid}=not*base64@tenant-a"
        ];
        foreach (var spec in specs)
        {
            var ex = Assert.ThrowsExactly<FormatException>(() => CallerKeyRing.Parse(spec), spec);
            Assert.IsFalse(ex.Message.Contains(kid, StringComparison.Ordinal), ex.Message);
        }
    }

    [TestMethod]
    public void KeyRing_Create_RefusesAShortKey()
    {
        Assert.ThrowsExactly<FormatException>(() => CallerKeyRing.Create([("kid-a", NewKey()[..16], "tenant-a")]));
    }

    [TestMethod]
    public void Options_Validate_RefusesKeysWithoutAudience_AndBadHeaderOrLists()
    {
        var keys = CallerKeyRing.Create([("kid-a", KeyA, "tenant-a")]);
        Assert.ThrowsExactly<InvalidOperationException>(() => new CallerAssertionOptions { Keys = keys }.Validate());
        Assert.ThrowsExactly<InvalidOperationException>(() => new CallerAssertionOptions { Keys = keys, Audience = " " }.Validate());
        new CallerAssertionOptions { Keys = keys, Audience = Audience }.Validate();
        CallerAssertionOptions.Disabled.Validate();

        foreach (var header in new[] { "Authorization", "authorization", "Cookie", "Proxy-Authorization", "X-Sextant-Repository", "X Caller", "" })
            Assert.ThrowsExactly<InvalidOperationException>(
                () => new CallerAssertionOptions { Keys = keys, Audience = Audience, Header = header }.Validate(), header);
        Assert.ThrowsExactly<InvalidOperationException>(
            () => new CallerAssertionOptions { Keys = keys, Audience = Audience, Idps = new HashSet<string>() }.Validate());
        Assert.ThrowsExactly<InvalidOperationException>(
            () => new CallerAssertionOptions { Keys = keys, Audience = Audience, Idps = new HashSet<string> { "Slack" } }.Validate());
        Assert.ThrowsExactly<InvalidOperationException>(
            () => new CallerAssertionOptions { Keys = keys, Audience = Audience, Apps = new HashSet<string> { "a b" } }.Validate());
        Assert.ThrowsExactly<InvalidOperationException>(
            () => new CallerAssertionOptions { Keys = keys, Audience = Audience, Issuers = new HashSet<string> { " " } }.Validate());
    }

    [TestMethod]
    public void Verify_IsDeterministicAcrossThreads()
    {
        var verifier = new CallerAssertionVerifier(Options(), new FixedTimeProvider(Now));
        var assertions = Enumerable.Range(0, 64).Select(i => (i, Sign(KeyA, "kid-a", UserClaims(Now, sub: $"user-{i}")))).ToList();
        var results = new System.Collections.Concurrent.ConcurrentDictionary<int, string?>();
        Parallel.ForEach(assertions, a => results[a.i] = verifier.Verify(a.Item2).Principal?.UserId);
        foreach (var (i, _) in assertions)
            Assert.AreEqual($"user-{i}", results[i]);
    }

    [TestMethod]
    public void Utf8Claims_RoundTrip()
    {
        var claims = UserClaims(Now);
        claims["tslug"] = "acmé";
        var result = Verify(Sign(KeyA, "kid-a", claims));
        Assert.AreEqual(CallerAssertionOutcome.Verified, result.Outcome, result.Reason);
        Assert.AreEqual("acmé", result.Principal!.TenantSlug);
    }
}
