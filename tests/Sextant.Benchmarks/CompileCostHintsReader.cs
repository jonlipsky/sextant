using System.Text.Json;
using Sextant.Indexer;

namespace Sextant.Benchmarks;

/// <summary>
/// Reads the per-project compile timings of a previous benchmark report (issue #288) as the hints the service worker
/// would load from its job history: <c>full_index.projects[]</c> entries of the symbol phase carry the project's
/// <c>key</c> (repo-relative path and target framework) and its compile window. A redacted report carries no keys and
/// yields no hints.
/// </summary>
public static class CompileCostHintsReader
{
    public static CompileCostHints Load(string reportPath)
    {
        using var stream = File.OpenRead(reportPath);
        return Parse(stream);
    }

    public static CompileCostHints Parse(Stream json)
    {
        using var document = JsonDocument.Parse(json);
        var cost = new Dictionary<string, long>(StringComparer.Ordinal);
        if (document.RootElement.TryGetProperty("full_index", out var full)
            && full.TryGetProperty("projects", out var projects)
            && projects.ValueKind == JsonValueKind.Array)
        {
            foreach (var project in projects.EnumerateArray())
            {
                if (project.ValueKind != JsonValueKind.Object || Text(project, "phase") != "extracting_symbols" || Text(project, "key") is not { } key)
                    continue;
                var ms = Number(project, "compile_cost_ms")
                    ?? (Number(project, "compile_ended_at_ms") - Number(project, "compile_started_at_ms"));
                if (ms is null)
                    continue;
                if (ms is > 0)
                    cost[key] = ms.Value;
            }
        }
        return new CompileCostHints(cost);
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;
}
