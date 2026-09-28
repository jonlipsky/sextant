using System.Text.Json;
using ProcessStack.Abstractions;

namespace Sextant.ProcessStack.Activities.Tests;

/// <summary>Runs an activity in-process the way a test (or the host, after binding) does.</summary>
internal static class ActivityHarness
{
    /// <summary>Executes <paramref name="activity"/> and returns it with the log lines it wrote.</summary>
    public static async Task<List<string>> RunAsync(AbstractActivity activity)
    {
        var logs = new List<string>();
        var context = new ActivityContext { LogCallback = (message, level) => logs.Add($"{level}: {message}") };
        await activity.ExecuteAsync(context, CancellationToken.None);
        return logs;
    }

    /// <summary>Fills <c>Definition.Parameters</c> (the path a node's YAML parameters take) instead of the properties.</summary>
    public static T WithParameters<T>(this T activity, params (string Name, object Value)[] parameters)
        where T : AbstractActivity
    {
        foreach (var (name, value) in parameters)
            activity.Definition.Parameters[name] = value;
        return activity;
    }

    /// <summary>A detached <see cref="JsonElement"/> for <paramref name="json"/> (how a resolved template arrives).</summary>
    public static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>A case-sensitive CLR map, as a platform activity output or a flow variable would carry.</summary>
    public static Dictionary<string, object?> Map(params (string Key, object? Value)[] entries)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
            map[key] = value;
        return map;
    }
}
