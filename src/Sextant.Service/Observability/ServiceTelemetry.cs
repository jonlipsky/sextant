using System.Diagnostics;

namespace Sextant.Service.Observability;

/// <summary>
/// The distributed-tracing source for the standalone index service (criterion 5: traces). A single
/// <see cref="System.Diagnostics.ActivitySource"/> named <c>Sextant.Service</c> that the control core
/// starts spans on (ensure-snapshot, retention, backup/restore) and the host starts query spans on. It
/// is inert unless an <see cref="ActivityListener"/> (a test, or an OpenTelemetry exporter wired by a
/// deployment) is subscribed — so tracing is OPTIONAL and adds no cost on the zero-config local path.
/// </summary>
public static class ServiceTelemetry
{
    public const string SourceName = "Sextant.Service";

    /// <summary>The shared activity source. Callers start spans with <c>StartActivity</c>.</summary>
    public static readonly ActivitySource Source = new(SourceName);
}
