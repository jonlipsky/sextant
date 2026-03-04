using Sextant.Indexer;
using Sextant.Mcp;
using Sextant.Store;

namespace Sextant.Integration.Tests;

public class IntegrationFixture
{
    public static IntegrationFixture Instance { get; private set; } = null!;

    public string DbPath { get; private set; } = null!;
    public DatabaseProvider DbProvider { get; private set; } = null!;

    public static async Task InitializeAsync()
    {
        var fixture = new IntegrationFixture();
        var solutionPath = FindSolutionFile();
        fixture.DbPath = Path.Combine(Path.GetTempPath(), $"sextant-integration-{Guid.NewGuid():N}.db");

        var solution = await SolutionLoader.LoadSolutionAsync(solutionPath);
        var db = new IndexDatabase(fixture.DbPath);
        db.RunMigrations();
        var orchestrator = new IndexOrchestrator(db, msg => { });
        await orchestrator.IndexSolutionAsync(solution);

        fixture.DbProvider = new DatabaseProvider(fixture.DbPath);
        Instance = fixture;
    }

    public static Task DisposeAsync()
    {
        if (Instance == null) return Task.CompletedTask;

        Instance.DbProvider?.Dispose();
        if (File.Exists(Instance.DbPath))
            File.Delete(Instance.DbPath);
        return Task.CompletedTask;
    }

    private static string FindSolutionFile()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            var slnx = Path.Combine(dir, "Sextant.slnx");
            if (File.Exists(slnx))
                return slnx;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException(
            "Could not find Sextant.slnx. Ensure tests run from within the repository.");
    }
}
