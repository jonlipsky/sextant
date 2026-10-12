namespace Sextant.Core;

/// <summary>
/// The git executable, resolved once to an absolute path: <c>SEXTANT_GIT_PATH</c> when it names an existing
/// file, else the first <c>git</c> found on <c>PATH</c> (absolute directories only). When neither is found it
/// falls back to the bare name, so a machine without git behaves exactly as before (the start fails and the
/// caller's existing error handling applies).
/// </summary>
public static class GitExecutable
{
    private static readonly Lazy<string> Resolved = new(() => Resolve(
        Environment.GetEnvironmentVariable("SEXTANT_GIT_PATH"), Environment.GetEnvironmentVariable("PATH")));

    public static string Path => Resolved.Value;

    internal static string Resolve(string? configured, string? path)
    {
        configured = configured?.Trim().Trim('"').Trim();
        if (!string.IsNullOrWhiteSpace(configured) && System.IO.Path.IsPathRooted(configured) && File.Exists(configured))
            return configured;

        var fileName = OperatingSystem.IsWindows() ? "git.exe" : "git";
        if (!string.IsNullOrEmpty(path))
        {
            foreach (var entry in path.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var directory = entry.Trim().Trim('"').Trim();
                    if (directory.Length == 0 || !System.IO.Path.IsPathRooted(directory))
                        continue;
                    var candidate = System.IO.Path.Combine(directory, fileName);
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry is skipped.
                }
            }
        }

        return "git";
    }
}
