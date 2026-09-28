namespace Sextant.ProcessStack.Activities;

/// <summary>
/// The Sextant service's ensure-job statuses (<c>Sextant.Store.SnapshotJobStatus</c>, which this bundle does
/// not reference so it does not carry the SQLite store). A parity test pins these to the service's values.
/// </summary>
internal static class JobStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Complete = "complete";
    public const string Partial = "partial";
    public const string Failed = "failed";
    public const string Unsupported = "unsupported";
    public const string Cancelled = "cancelled";

    /// <summary>True for a status the service never re-runs for the same identity.</summary>
    public static bool IsTerminal(string status) =>
        status is Complete or Partial or Failed or Unsupported or Cancelled;
}
