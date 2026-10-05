using System.Diagnostics;
using System.Text.Json;
using static Sextant.Service.Tests.CallerAssertionHttpTests;

namespace Sextant.Service.Tests;

/// <summary>
/// Measures what repository inference adds to a call: the same <c>find_symbol</c> over <c>/mcp</c> with the repository
/// named, and with it omitted by a caller that can read <see cref="Repositories"/> repositories (one holds the symbol,
/// several hold it, none holds it). The numbers are written to the test output. Gated behind <c>SEXTANT_RUN_PERF=1</c>
/// and <c>[TestCategory("Performance")]</c> like the other wall-clock tests; it asserts only a loose ceiling.
/// </summary>
[TestClass]
[TestCategory("Performance")]
public class RepositoryInferencePerformanceTests
{
    private const int Repositories = 10;
    private const int TypesPerRepository = 200;
    private const int Iterations = 40;
    private const string Sub = "user-perf";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task InferredCall_CostsAboutOneLookupPerReadableRepository()
    {
        if (Environment.GetEnvironmentVariable("SEXTANT_RUN_PERF") != "1")
            Assert.Inconclusive("Performance test skipped (set SEXTANT_RUN_PERF=1 to run).");

        var root = Path.Combine(Path.GetTempPath(), "sextant-inference-perf-" + Guid.NewGuid().ToString("N"));
        var urls = Enumerable.Range(0, Repositories).Select(i => $"https://github.com/acme/repo{i:D2}").ToArray();
        await using var host = await Harness.StartAsync(
            configure: o => o with { RequireRepositorySelection = true },
            seed: db =>
            {
                for (var i = 0; i < urls.Length; i++)
                    RepositoryInferenceHttpTests.Index(db, root, urls[i], $"commit-{i}", Sources(i));
            });
        try
        {
            foreach (var url in urls)
            {
                using var response = await host.ControlAsync(HttpMethod.Put, "/control/grants/self", ControlToken,
                    host.UserAssertion(sub: Sub), JsonSerializer.Serialize(new { repository = url }));
                Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
            }

            var last = Repositories - 1;
            var results = new List<(string Scenario, double Median, double P95)>
            {
                await Measure(host, "explicit repository", $$"""{"name":"Repo{{last}}.Type7","repository":"acme/repo{{last:D2}}"}""", expectError: false),
                await Measure(host, $"inferred, 1 of {Repositories} holds it", $$"""{"name":"Repo{{last}}.Type7"}""", expectError: false),
                await Measure(host, $"inferred, {Repositories} of {Repositories} hold it", """{"name":"Common.Shared"}""", expectError: true),
                await Measure(host, $"inferred, 0 of {Repositories} hold it", """{"name":"Nowhere.Missing"}""", expectError: true)
            };
            foreach (var (scenario, median, p95) in results)
                TestContext.WriteLine($"{scenario,-34} median {median,8:F2} ms   p95 {p95,8:F2} ms");

            Assert.IsTrue(results[1].Median < 500, $"an inferred call took {results[1].Median:F2} ms (median)");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private async Task<(string, double, double)> Measure(Harness host, string scenario, string arguments, bool expectError)
    {
        for (var i = 0; i < 3; i++)
        {
            var warm = await host.CallAsync("find_symbol", arguments, DelegateToken, host.UserAssertion(sub: Sub));
            Assert.AreEqual(expectError, warm.IsError, warm.Body.ToString());
        }
        var samples = new List<double>();
        for (var i = 0; i < Iterations; i++)
        {
            var assertion = host.UserAssertion(sub: Sub);
            var watch = Stopwatch.StartNew();
            await host.CallAsync("find_symbol", arguments, DelegateToken, assertion);
            samples.Add(watch.Elapsed.TotalMilliseconds);
        }
        samples.Sort();
        return (scenario, samples[samples.Count / 2], samples[(int)Math.Ceiling(samples.Count * 0.95) - 1]);
    }

    private static (string Path, string Source)[] Sources(int repository)
    {
        var types = string.Join("\n", Enumerable.Range(0, TypesPerRepository)
            .Select(t => $"    public class Type{t} {{ public int Value{t}() => {t}; }}"));
        return
        [
            ("Types.cs", $"namespace Repo{repository}\n{{\n{types}\n}}\n"),
            ("Shared.cs", "namespace Common\n{\n    public class Shared { }\n}\n")
        ];
    }
}
