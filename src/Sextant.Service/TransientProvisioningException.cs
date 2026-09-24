namespace Sextant.Service;

/// <summary>
/// Signals that CHECKOUT PROVISIONING failed for a reason that is expected to be RECOVERABLE on a later
/// attempt — a network blip, DNS hiccup, fetch timeout, connection reset, or a remote 5xx/429 during a
/// clone in <see cref="ServiceCheckoutMode.Clone"/> mode. It is the provisioning analogue of
/// <see cref="OperationCanceledException"/>: the service catches it and REQUEUES the identity's job (bounded
/// by <see cref="ServiceOptions.MaxProvisioningAttempts"/>) instead of recording a terminal
/// <see cref="SnapshotJobStatus.Unsupported"/>/<see cref="SnapshotJobStatus.Failed"/>, so a single transient
/// failure can never permanently poison a commit's identity (the idempotent-ensure cache would otherwise
/// attach that terminal result forever). A DETERMINISTIC provisioning failure — the clone succeeded but the
/// repo has no solution, the commit/repository genuinely does not exist, authentication was refused, or the
/// request was malformed — must NOT throw this; it stays a legitimately-permanent terminal result.
///
/// <para>
/// The message MUST already be token-redacted and URL-sanitized by the thrower: raw git stderr can echo the
/// authenticated fetch URL, and this message is persisted as a job diagnostic.
/// </para>
/// </summary>
public sealed class TransientProvisioningException(string message, Exception? innerException = null)
    : Exception(message, innerException);
