namespace Sextant.Core;

/// <summary>Reads a secret environment variable or its <c>_FILE</c> counterpart without exposing its value.</summary>
public static class SecretEnvironmentVariable
{
    private static readonly object WarningLock = new();
    private static readonly HashSet<string> WarnedVariables = new(StringComparer.Ordinal);

    /// <summary>
    /// Reads <paramref name="name"/> or <paramref name="name"/>_FILE. File values have trailing newlines removed;
    /// environment values remain supported with a one-time deprecation warning.
    /// </summary>
    /// <exception cref="InvalidOperationException">Both forms are set or the file cannot be read.</exception>
    public static string? Read(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var fileName = name + "_FILE";
        var value = Environment.GetEnvironmentVariable(name);
        var filePath = Environment.GetEnvironmentVariable(fileName);
        if (value is not null && filePath is not null)
            throw new InvalidOperationException($"Set either {name} or {fileName}, not both.");

        if (filePath is not null)
        {
            if (filePath.Length == 0)
                throw new InvalidOperationException($"{fileName} must name a readable file.");
            try
            {
                return File.ReadAllText(filePath).TrimEnd('\r', '\n');
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or System.Security.SecurityException)
            {
                throw new InvalidOperationException($"{fileName} refers to a file that cannot be read.");
            }
        }

        if (string.IsNullOrEmpty(value))
            return null;

        lock (WarningLock)
        {
            if (WarnedVariables.Add(name))
                Console.Error.WriteLine($"WARNING: {name} is deprecated for secrets; use {fileName} instead.");
        }
        return value;
    }
}
