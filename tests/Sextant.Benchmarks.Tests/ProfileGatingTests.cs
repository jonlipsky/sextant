using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Sextant.Core;
using Sextant.Indexer;
using Sextant.Store;

namespace Sextant.Benchmarks.Tests;

/// <summary>
/// Phase 8, acceptance criterion 2: when a profile omits an optional feature the orchestrator SKIPS
/// building it, and that omission never breaks a supported core query. These load the real generated
/// corpus through MSBuildWorkspace (a NuGet restore per corpus), so they are heavier than unit tests.
/// The deep-only <em>dataflow</em> feature (argument/return flow) is the observable gate: the corpus
/// exercises calls with arguments, so a deep index populates dataflow while a core index leaves those
/// tables empty — with identical core symbols/occurrences either way.
/// </summary>
[TestClass]
public sealed class ProfileGatingTests
{
    [TestMethod]
    public async Task CoreProfile_OmitsDataflow_ButKeepsCoreQueriesIntact()
    {
        var root = NewTempDir("profile-core");
        var coreDbDir = NewTempDir("profile-core-db");
        var deepDbDir = NewTempDir("profile-deep-db");
        try
        {
            var slnx = CorpusGenerator.GenerateCorrectnessCorpus(root);
            Restore(slnx);

            // Index the SAME on-disk state twice: once at the core profile, once at deep.
            using var coreDb = new IndexDatabase(Path.Combine(coreDbDir, "index.db"));
            coreDb.RunMigrations();
            var coreSolution = await SolutionLoader.LoadSolutionAsync(slnx);
            await new IndexOrchestrator(coreDb, useDocumentExtractor: true,
                profile: IndexProfileDescriptor.For(IndexProfiles.Core)).IndexSolutionAsync(coreSolution);

            using var deepDb = new IndexDatabase(Path.Combine(deepDbDir, "index.db"));
            deepDb.RunMigrations();
            var deepSolution = await SolutionLoader.LoadSolutionAsync(slnx);
            await new IndexOrchestrator(deepDb, useDocumentExtractor: true,
                profile: IndexProfileDescriptor.For(IndexProfiles.Deep)).IndexSolutionAsync(deepSolution);

            var core = coreDb.GetConnection();
            var deep = deepDb.GetConnection();

            // Core tables are always built.
            var coreSymbols = Scalar(core, "SELECT COUNT(*) FROM symbols");
            var coreOccurrences = Scalar(core, "SELECT COUNT(*) FROM occurrences");
            Assert.IsTrue(coreSymbols > 0, "the core profile must still build symbols");
            Assert.IsTrue(coreOccurrences > 0, "the core profile must still build occurrences (references + calls)");

            // The omitted optional feature (dataflow) built nothing under core...
            Assert.AreEqual(0, Scalar(core, "SELECT COUNT(*) FROM argument_flow"),
                "dataflow is deep-only: the core profile must skip argument_flow");
            Assert.AreEqual(0, Scalar(core, "SELECT COUNT(*) FROM return_flow"),
                "dataflow is deep-only: the core profile must skip return_flow");

            // ...but the deep profile DID build it over the same corpus, proving the core zero is a real
            // skip and not an empty-corpus artifact.
            Assert.IsTrue(Scalar(deep, "SELECT COUNT(*) FROM argument_flow") > 0,
                "the deep profile builds dataflow (argument_flow) for calls-with-arguments in the corpus");

            // Core data is identical across profiles — omitting optional work does not perturb core rows.
            Assert.AreEqual(Scalar(deep, "SELECT COUNT(*) FROM symbols"), coreSymbols,
                "symbols are a core feature: identical under core and deep");
            Assert.AreEqual(Scalar(deep, "SELECT COUNT(*) FROM occurrences"), coreOccurrences,
                "occurrences are a core feature: identical under core and deep");

            // A supported core query still returns results under the core profile: the Phase-4
            // cross-project closure is driven by source-NULL occurrences whose using project differs
            // from the target declaration's owning project (App → Lib here).
            var crossProjectRefs = Scalar(core, """
                SELECT COUNT(*)
                FROM occurrences r
                JOIN symbols s ON s.id = r.target_symbol_id
                WHERE r.source_symbol_id IS NULL
                  AND r.in_project_id != s.project_id
                """);
            Assert.IsTrue(crossProjectRefs > 0,
                "a supported core query (cross-project references) must work under the core profile");
            Assert.AreEqual(crossProjectRefs, Scalar(deep, """
                SELECT COUNT(*)
                FROM occurrences r
                JOIN symbols s ON s.id = r.target_symbol_id
                WHERE r.source_symbol_id IS NULL
                  AND r.in_project_id != s.project_id
                """), "the core cross-project closure query is profile-independent");
        }
        finally
        {
            SqliteTestDatabase.DeleteDirectory(root);
            SqliteTestDatabase.DeleteDirectory(coreDbDir);
            SqliteTestDatabase.DeleteDirectory(deepDbDir);
        }
    }

    private static long Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

    private static string NewTempDir(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sextant-profile-{tag}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Restore(string solutionPath)
    {
        var psi = new ProcessStartInfo("dotnet", $"restore \"{solutionPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        stdoutTask.GetAwaiter().GetResult();
        process.WaitForExit();
        if (process.ExitCode != 0)
            Assert.Inconclusive($"restore of the generated corpus failed (exit {process.ExitCode}): {stderr}");
    }
}
