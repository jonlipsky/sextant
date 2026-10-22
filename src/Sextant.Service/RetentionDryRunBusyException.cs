namespace Sextant.Service;

/// <summary>
/// A retention dry run was asked for while another one is running (issue #335). A dry run holds one read transaction
/// for up to its whole budget, so the service runs one at a time.
/// </summary>
public sealed class RetentionDryRunBusyException()
    : InvalidOperationException("A retention dry run is already running.");
