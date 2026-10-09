using Sextant.Core;
using Sextant.Indexer;

namespace Sextant.Indexer.Tests;

/// <summary>Issue #270: how many projects the document extractor analyzes at once resolves from configuration.</summary>
[TestClass]
[DoNotParallelize] // sets a process environment variable
public sealed class ExtractionParallelismOptionsTests
{
    private string _dir = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sextant_pif_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void TestCleanup()
    {
        Environment.SetEnvironmentVariable("SEXTANT_EXTRACTION_PROJECTS_IN_FLIGHT", null);
        Directory.Delete(_dir, recursive: true);
    }

    [TestMethod]
    public void ProjectsInFlight_AutoResolvesToTheParallelism_CappedAtTheDefault()
    {
        var parallelism = Math.Min(2, Environment.ProcessorCount);
        Assert.AreEqual(Math.Min(parallelism, ExtractionParallelismOptions.DefaultProjectsInFlight),
            ExtractionParallelismOptions.Resolve(parallelism, 0).ProjectsInFlight);
        Assert.AreEqual(1, ExtractionParallelismOptions.Resolve(1, 0).ProjectsInFlight);
        Assert.AreEqual(1, ExtractionParallelismOptions.Sequential.ProjectsInFlight);
    }

    [TestMethod]
    public void ProjectsInFlight_APositiveValueIsHonoredAsIs()
    {
        Assert.AreEqual(7, ExtractionParallelismOptions.Resolve(1, 0, configuredProjectsInFlight: 7).ProjectsInFlight);
    }

    [TestMethod]
    public void ProjectsInFlight_ComesFromSextantJson_AndTheEnvironmentOverridesIt()
    {
        File.WriteAllText(Path.Combine(_dir, "sextant.json"), """{ "extraction_projects_in_flight": 3 }""");
        Assert.AreEqual(3, ExtractionParallelismOptions.FromConfiguration(SextantConfiguration.Load(_dir)).ProjectsInFlight);

        Environment.SetEnvironmentVariable("SEXTANT_EXTRACTION_PROJECTS_IN_FLIGHT", "5");
        Assert.AreEqual(5, ExtractionParallelismOptions.FromConfiguration(SextantConfiguration.Load(_dir)).ProjectsInFlight);
    }
}
