using Sextant.Store;

namespace Sextant.Mcp;

/// <summary>
/// Guards a Phase-11 federated read the way <see cref="CapabilityGate"/> guards a capability-gated query:
/// it resolves the once-per-request <see cref="FederatedReadContext"/> and, when the read is authorized,
/// hands back a context whose <see cref="FederatedReadContext.Scope"/> every store reuses and whose
/// <see cref="FederatedReadContext.Provenance"/> every response stamps. When authorization is DENIED it
/// emits the single UNIFORM not-found response (<see cref="ResponseBuilder.BuildNotFound"/>) — never a
/// distinct <c>authorization_denied</c> code — so a fail-closed denial is byte-indistinguishable from a
/// nonexistent/unprovisioned index (Phase 17, criterion 1): no data, counts, names, existence, or timing
/// signal leaks. A denial only ever occurs under an ENABLED policy; on the zero-policy local path the
/// permissive authorizer always allows, so this path is unchanged.
/// </summary>
public static class ReadContextGate
{
    public static bool TryResolve(
        IndexDatabase db,
        out FederatedReadContext context,
        out string errorResponse,
        FederationMode mode = FederationMode.Federated,
        IReadAuthorizer? authorizer = null,
        CompatibilityInputs? compatibility = null,
        Func<string?>? requestedRepository = null)
    {
        context = FederatedReadContext.Resolve(db, mode, authorizer, compatibility, requestedRepository);
        if (context.Authorization.Allowed)
        {
            errorResponse = string.Empty;
            return true;
        }

        // Fail closed with the UNIFORM not-found (criterion 1): the denial reveals nothing an unauthorized
        // caller could use as an existence/authorization oracle.
        errorResponse = ResponseBuilder.BuildNotFound();
        return false;
    }
}
