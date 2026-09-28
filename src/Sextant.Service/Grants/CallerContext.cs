using Sextant.Service.CallerIdentity;

namespace Sextant.Service.Grants;

/// <summary>
/// The current request's verified caller, for service-side MCP tools (<see cref="ListRepositoriesTool"/>). The host
/// registers it as a singleton over an accessor that reads the ambient request at call time, so it stays correct
/// under concurrent requests. It yields null when the request carries no verified caller assertion.
/// </summary>
public sealed class CallerContext(Func<CallerPrincipal?> caller)
{
    /// <summary>The verified caller of the current request, or null.</summary>
    public CallerPrincipal? Current => caller();
}
