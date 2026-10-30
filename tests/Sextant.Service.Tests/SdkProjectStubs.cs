namespace Sextant.Service.Tests;

/// <summary>
/// Writes SDK-style project files, so a test about a project that failed to load is not also about a project that
/// does not exist (a missing file is an exclusion, issue #351).
/// </summary>
internal static class SdkProjectStubs
{
    public static void Create(params string[] projectFiles)
    {
        foreach (var file in projectFiles)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        }
    }
}
