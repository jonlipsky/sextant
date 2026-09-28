using Sextant.Service.CallerIdentity;

namespace Sextant.Service;

/// <summary>
/// Who an audited control-plane operation is attributed to: the principal string the audit row hashes
/// (<see cref="Sextant.Store.AuditLogStore.HashActor"/>) and, for a verified caller, the suffix appended to the
/// row's detail. A bare string (a bearer, or a test principal) converts implicitly and carries no suffix.
/// </summary>
/// <param name="Principal">The principal the audit row's actor hashes, or null for none.</param>
/// <param name="DetailSuffix">Appended to the audit detail (empty when there is no verified caller).</param>
public readonly record struct AuditCaller(string? Principal, string? DetailSuffix)
{
    private const int MaxValueLength = 64;

    /// <summary>A bare principal with no detail suffix.</summary>
    public static implicit operator AuditCaller(string? principal) => new(principal, null);

    /// <summary>
    /// The attribution for a verified caller (SVC-3/SVC-4): the actor is <see cref="CallerPrincipal.AuditPrincipal"/>
    /// and the detail gains <c>;idp=…;kid=…;via=…;cid=…;dep=…;jti=…</c>. Each value is written only when it is at
    /// most 64 characters of <c>[A-Za-z0-9._:-]</c>, else as <c>-</c>, so a claim can never inject a separator,
    /// a control character or an unbounded value into the audit row.
    /// </summary>
    public static AuditCaller ForCaller(CallerPrincipal caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var suffix =
            $";idp={Safe(caller.Idp)};kid={Safe(caller.KeyId)};via={Safe(caller.Via)}" +
            $";cid={Safe(caller.Connection)};dep={Safe(caller.Deployment)};jti={Safe(caller.Jti)}";
        return new AuditCaller(caller.AuditPrincipal, suffix);
    }

    /// <summary>The hashed actor for the audit row.</summary>
    internal string? Actor => Sextant.Store.AuditLogStore.HashActor(Principal);

    /// <summary><paramref name="detail"/> with this caller's suffix appended.</summary>
    internal string Detail(string detail) => detail + DetailSuffix;

    internal static string Safe(string? value) =>
        value is { Length: > 0 and <= MaxValueLength }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '-')
            ? value
            : "-";
}
