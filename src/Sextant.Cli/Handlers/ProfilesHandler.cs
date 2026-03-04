namespace Sextant.Cli.Handlers;

internal static class ProfilesHandler
{
    public static void Run(string? profileOverride)
    {
        var root = Core.SextantConfiguration.FindRepoRoot(Directory.GetCurrentDirectory()) ?? ".";
        var profilesDir = Path.Combine(root, ".sextant", "profiles");

        if (!Directory.Exists(profilesDir))
        {
            Console.WriteLine("No profiles found.");
            return;
        }

        var profiles = Directory.GetDirectories(profilesDir)
            .Select(d => new DirectoryInfo(d))
            .OrderBy(d => d.Name);

        var config = Core.SextantConfiguration.Load();
        var activeProfile = profileOverride
            ?? Environment.GetEnvironmentVariable("SEXTANT_PROFILE")
            ?? config.Profile;

        Console.WriteLine("Profiles:");
        foreach (var dir in profiles)
        {
            var dbFile = Path.Combine(dir.FullName, "sextant.db");
            var exists = File.Exists(dbFile);
            var size = exists ? new FileInfo(dbFile).Length : 0;
            var marker = dir.Name == activeProfile ? " (active)" : "";
            var sizeStr = exists ? $"{size / 1024.0 / 1024.0:F1} MB" : "empty";
            Console.WriteLine($"  {dir.Name}{marker} — {sizeStr}");
        }
    }
}
