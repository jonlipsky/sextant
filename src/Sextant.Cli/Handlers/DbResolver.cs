namespace Sextant.Cli.Handlers;

internal static class DbResolver
{
    public static string? Resolve(string? db, string? profile, Core.SextantConfiguration config)
    {
        if (db != null && profile != null)
        {
            Console.Error.WriteLine("Error: --db and --profile are mutually exclusive.");
            return null;
        }

        return Core.SextantConfiguration.ResolveDbPath(db, profile, config);
    }
}
