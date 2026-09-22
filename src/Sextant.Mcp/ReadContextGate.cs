using Sextant.Store;

namespace Sextant.Mcp;

/// <summary>
/// Guards a Phase-11 federated read the way <see cref="CapabilityGate"/> guards a capability-gated query:
/// it resolves the once-per-request <see cref="FederatedReadContext"/> and, when the read is authorized,
/// hands back a context whose <see cref="FederatedReadContext.Scope"/> every store reuses and whose
/// <see cref="FederatedReadContext.Provenance"/> every response stamps. When authorization is DENIED it
/// emits a structured <c>meta.error</c> envelope (never an empty successful result) so criterion 6 holds:
/// an authorization failure is never presented as "no matches".
/// </summary>
public static class ReadContextGate
{
    public static bool TryResolve(
        IndexDatabase db,
        out FederatedReadContext context,
        out string errorResponse,
        FederationMode mode = FederationMode.Federated,
        IReadAuthorizer? authorizer = null,
        CompatibilityInputs? compatibility = null)
    {
        context = FederatedReadContext.Resolve(db, mode, authorizer, compatibility);
        if (context.Authorization.Allowed)
        {
            errorResponse = string.Empty;
            return true;
        }

        errorResponse = ResponseBuilder.BuildError(
            "authorization_denied",
            context.Authorization.Reason ?? "The current principal is not authorized to read this index.");
        return false;
    }
}
