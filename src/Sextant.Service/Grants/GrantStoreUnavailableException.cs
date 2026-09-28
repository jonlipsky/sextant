namespace Sextant.Service.Grants;

/// <summary>
/// A grant write could not run right now: the service no longer holds the single-writer lease, or the catalog stayed
/// locked by another writer past the write's wait. Nothing was written; the host answers 503 and the caller retries.
/// </summary>
public sealed class GrantStoreUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
