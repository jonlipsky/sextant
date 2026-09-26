using Sextant.Indexer;
using Sextant.Service.Placement;

namespace Sextant.Service.Tests;

/// <summary>
/// Phase 15 — the non-MSBuild glue in <see cref="SolutionEvaluationProbe"/>: the checkout-missing arm
/// (→ an unavailable probe so the routing worker fails the job closed) and the two pure helpers that feed
/// the unit-tested <see cref="LinuxEvaluationAnalyzer"/> — target-platform display parsing and solution-level
/// diagnostic attribution. The clean-load / diagnostic arms of <see cref="LinuxEvaluationAnalyzer"/> itself
/// are covered separately (no real MSBuild load is required on CI).
/// </summary>
[TestClass]
public class SolutionEvaluationProbeTests
{
    private sealed class FakeCheckoutProvider(bool resolves) : ICheckoutProvider
    {
        public bool TryResolve(EnsureSnapshotRequest request, out CheckoutResolution resolution)
        {
            resolution = resolves
                ? new CheckoutResolution
                {
                    CheckoutDir = "/checkouts/app",
                    SelectedSolutions = ["/checkouts/app/App.sln"],
                    Source = SolutionSelectionSource.DefaultRoot
                }
                : null!;
            return resolves;
        }
    }

    [TestMethod]
    public async Task NoCheckout_ReportsUnavailable_SoJobFailsClosed()
    {
        var probe = new SolutionEvaluationProbe(new FakeCheckoutProvider(resolves: false));

        var result = await probe.ProbeAsync(ServiceTestFixtures.Request(), "/scratch", CancellationToken.None);

        Assert.IsFalse(result.CheckoutAvailable, "a missing checkout must not read as an all-clear probe");
        Assert.IsNotNull(result.UnavailableReason);
        StringAssert.Contains(result.UnavailableReason, "https://github.com/org/app");
        Assert.AreEqual(0, result.Requirements.Count);
        Assert.AreEqual(0, result.LinuxOutcomes.Count);
    }

    [DataTestMethod]
    [DataRow("Foo (net8.0-windows)", "windows")]
    [DataRow("Foo (net8.0-windows10.0.19041)", "windows")]
    [DataRow("Foo (net9.0-ios)", "ios")]
    [DataRow("Foo (net8.0-maccatalyst)", "maccatalyst")]
    [DataRow("Foo (net8.0)", null)]           // no platform suffix
    [DataRow("Foo", null)]                     // single-TFM plain name carries no TFM
    [DataRow("Acme-Cli", null)]                // a hyphenated bare name must NOT parse to a bogus "cli"
    [DataRow("Foo (net8.0-windows) (x)", null)] // trailing paren group has no platform token
    public void ParseTargetPlatform_OnlyTrustsMultiTfmDisplaySuffix(string projectName, string? expected)
    {
        Assert.AreEqual(expected, SolutionEvaluationProbe.ParseTargetPlatform(projectName));
    }

    [TestMethod]
    public void AttributeDiagnostics_SelectsOnlyMessagesNamingTheProjectFile()
    {
        string[] diagnostics =
        [
            "Foo.csproj: missing workload 'ios'",
            "Bar.csproj: some other failure",
            "unattributed workspace failure"
        ];

        var attributed = SolutionEvaluationProbe.AttributeDiagnostics("Foo.csproj", diagnostics);

        Assert.AreEqual(1, attributed.Length);
        StringAssert.Contains(attributed[0], "missing workload 'ios'");
    }

    [TestMethod]
    public void AttributeDiagnostics_NullFileName_AttributesNothing()
    {
        string[] diagnostics = ["Foo.csproj: boom"];
        Assert.AreEqual(0, SolutionEvaluationProbe.AttributeDiagnostics(null, diagnostics).Length);
    }
}
