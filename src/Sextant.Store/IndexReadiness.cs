namespace Sextant.Store;

/// <summary>
/// The result of <see cref="IndexDatabase.CheckReadiness"/>: whether an index database can be served
/// as-is, or must be rebuilt first (with an actionable message explaining why).
/// </summary>
public sealed record IndexReadiness(bool Ready, string? Message)
{
    /// <summary>A ready, complete index.</summary>
    public static readonly IndexReadiness ReadyIndex = new(true, null);

    /// <summary>An index that must be rebuilt before it can be served, with an actionable reason.</summary>
    public static IndexReadiness NotReady(string message) => new(false, message);
}
