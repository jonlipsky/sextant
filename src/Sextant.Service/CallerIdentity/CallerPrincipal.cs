namespace Sextant.Service.CallerIdentity;

/// <summary>Who a verified caller assertion says the request is for (the signed <c>act</c> claim).</summary>
public enum CallerActor
{
    /// <summary>An end user: the assertion carries an identity provider (<c>idp</c>) and a subject (<c>sub</c>).</summary>
    User,

    /// <summary>The calling application itself, on no user's behalf (a schedule, webhook or platform event).</summary>
    Application
}

/// <summary>
/// The caller of one query-plane or control-plane request, built ONLY from the claims of a verified caller
/// assertion (SVC-3), never from a request body, tool argument or header other than the assertion itself. The
/// host keeps it in the request's items and exposes it as a <c>Func&lt;CallerPrincipal?&gt;</c>, so an authorizer
/// built from delegates reads the ambient request's caller at call time.
/// </summary>
public sealed record CallerPrincipal
{
    /// <summary>The tenant (<c>tid</c>). Always the tenant the signing key is bound to.</summary>
    public required string TenantId { get; init; }

    /// <summary>The tenant's slug (<c>tslug</c>), recorded for diagnostics only.</summary>
    public required string TenantSlug { get; init; }

    /// <summary>Whether the request is for a user or for the application itself (<c>act</c>).</summary>
    public required CallerActor Actor { get; init; }

    /// <summary>The identity provider of a user caller (<c>idp</c>); null for an application caller.</summary>
    public string? Idp { get; init; }

    /// <summary>
    /// The full subject of a user caller (<c>sub</c>), already namespaced by <see cref="Idp"/>: a platform user id
    /// for the platform's own idp, otherwise <c>{idp}:{connectionInstanceId}:{peerId}</c>. Null for an
    /// application caller.
    /// </summary>
    public string? UserId { get; init; }

    /// <summary>The calling application (<c>app</c>).</summary>
    public required string App { get; init; }

    /// <summary>The calling application's deployment (<c>dep</c>).</summary>
    public required string Deployment { get; init; }

    /// <summary>The connection the request was sent through (<c>cid</c>).</summary>
    public required string Connection { get; init; }

    /// <summary>How the request was sent (<c>via</c>): <c>mcp-surface</c> or <c>activity</c>.</summary>
    public required string Via { get; init; }

    /// <summary>The execution the request belongs to (<c>run</c>), when the signer recorded one.</summary>
    public string? Run { get; init; }

    /// <summary>The key id that signed the assertion (<c>kid</c>).</summary>
    public required string KeyId { get; init; }

    /// <summary>The assertion's unique id (<c>jti</c>).</summary>
    public required string Jti { get; init; }

    /// <summary>
    /// The principal string an audit row hashes for this caller: <c>{tid}/{sub}</c> for a user (the subject is
    /// already idp-namespaced) and <c>{tid}/app:{app}</c> for an application.
    /// </summary>
    public string AuditPrincipal => Actor == CallerActor.User ? $"{TenantId}/{UserId}" : $"{TenantId}/app:{App}";
}
