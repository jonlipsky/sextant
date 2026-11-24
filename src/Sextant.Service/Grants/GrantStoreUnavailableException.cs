namespace Sextant.Service.Grants;

/// <summary>
/// A grant-style control write could not complete within its response bound; the host answers 503 and the caller retries.
/// For an admission, <see cref="AdmissionId"/> is supplied when its durable write may still be completing.
/// </summary>
public sealed class GrantStoreUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string? AdmissionId { get; init; }
}
