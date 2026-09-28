using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Sextant.Service.CallerIdentity;

namespace Sextant.Service.Host;

/// <summary>
/// Which control routes a user caller (a verified <c>act=user</c> assertion) may reach (issue #193). The rule is
/// default-deny. A route serves a user caller only when its endpoint is marked <see cref="DecidesUserCallers"/>,
/// which means the route applies its own <c>act=user</c> rule: the SVC-4 visibility gates on ensure, status and
/// resolve, and the grant routes' actor rule. Every other control route refuses a user caller with 403
/// <c>caller_not_allowed</c>. That covers branch retire, the operational routes (retention, backup, metrics, audit,
/// pilot) and any route added later. The refusal runs in the middleware after the assertion is verified (a bad
/// assertion is still 401) and before the route binds its request or reads any state, so it is identical whatever
/// the request names. An application caller and an assertion-less control call are unchanged.
/// </summary>
internal static class ControlCallerRules
{
    /// <summary>Marks a control route that applies its own <c>act=user</c> rule, so a user caller reaches it.</summary>
    public static TBuilder DecidesUserCallers<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(UserCallersDecidedByRoute.Instance);

    /// <summary>
    /// Records a refused user call on this route as an <paramref name="action"/>/<c>denied</c> audit row, for the routes
    /// whose accepted calls are audited under that action.
    /// </summary>
    public static TBuilder AuditsRefusedUserCallsAs<TBuilder>(this TBuilder builder, string action) where TBuilder : IEndpointConventionBuilder
    {
        ArgumentException.ThrowIfNullOrEmpty(action);
        return builder.WithMetadata(new RefusedUserCallAudit(action));
    }

    /// <summary>Whether <paramref name="endpoint"/> serves a user caller (false for no endpoint).</summary>
    public static bool AdmitsUserCallers(Endpoint? endpoint) =>
        endpoint?.Metadata.GetMetadata<UserCallersDecidedByRoute>() is not null;

    /// <summary>
    /// Admits an authenticated control request whose caller assertion (if any) was already verified, or writes the 403
    /// and returns false. A refused call on an audited route writes its denied row first. The row has the reason as its
    /// detail and no repository scope, because the refusal precedes reading the request.
    /// </summary>
    public static async Task<bool> AdmitAsync(HttpContext context)
    {
        if (CallerRequest.Get(context)?.Principal is not { Actor: CallerActor.User })
            return true;
        var endpoint = context.GetEndpoint();
        if (AdmitsUserCallers(endpoint))
            return true;

        if (endpoint?.Metadata.GetMetadata<RefusedUserCallAudit>() is { } audit)
            await context.RequestServices.GetRequiredService<SnapshotService>().RecordControlDeniedAsync(
                audit.Action, CallerAssertionGate.NotAllowedCode, ServiceApp.AuditActor(context.Request), context.RequestAborted);
        await CallerAssertionGate.WriteForbiddenAsync(context);
        return false;
    }

    internal sealed class UserCallersDecidedByRoute
    {
        public static readonly UserCallersDecidedByRoute Instance = new();

        private UserCallersDecidedByRoute()
        {
        }
    }

    internal sealed record RefusedUserCallAudit(string Action);
}
