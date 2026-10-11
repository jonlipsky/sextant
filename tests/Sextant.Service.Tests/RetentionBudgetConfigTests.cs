using Sextant.Service;

namespace Sextant.Service.Tests;

/// <summary>Issue #303: the configurable retention pass budget and the interactive <c>?seconds=</c> clamp.</summary>
[TestClass]
public class RetentionBudgetConfigTests
{
    [TestMethod]
    public void PassSeconds_DefaultsTo600_ClampsTo3600_AndFallsBackOnInvalid()
    {
        Assert.AreEqual(600, ServiceOptions.ParseMaintenance(null, null, null, null, null).RetentionPassSeconds);
        Assert.AreEqual(1800, ServiceOptions.ParseMaintenance(null, null, null, null, null, "1800").RetentionPassSeconds);
        Assert.AreEqual(3600, ServiceOptions.ParseMaintenance(null, null, null, null, null, "999999").RetentionPassSeconds);
        Assert.AreEqual(600, ServiceOptions.ParseMaintenance(null, null, null, null, null, "0").RetentionPassSeconds);
        Assert.AreEqual(600, ServiceOptions.ParseMaintenance(null, null, null, null, null, "-5").RetentionPassSeconds);
        Assert.AreEqual(600, ServiceOptions.ParseMaintenance(null, null, null, null, null, "abc").RetentionPassSeconds);
    }

    [TestMethod]
    public void InteractiveRetention_DefaultsTo10Seconds_AndNeverExceedsTheConfiguredMaximum()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(10), SnapshotService.ClampInteractiveRetention(null, 600));
        Assert.AreEqual(TimeSpan.FromSeconds(120), SnapshotService.ClampInteractiveRetention(120, 600));
        Assert.AreEqual(TimeSpan.FromSeconds(600), SnapshotService.ClampInteractiveRetention(5000, 600));
        Assert.AreEqual(TimeSpan.FromSeconds(3600), SnapshotService.ClampInteractiveRetention(99999, 99999));
        Assert.AreEqual(TimeSpan.FromSeconds(1), SnapshotService.ClampInteractiveRetention(0, 600));
        Assert.AreEqual(TimeSpan.FromSeconds(5), SnapshotService.ClampInteractiveRetention(null, 5));
    }

    [TestMethod]
    [DataRow(0, 60)]
    [DataRow(-5, 60)]
    [DataRow(1, 1)]
    [DataRow(600, 600)]
    [DataRow(601, 600)]
    public void UnitMaxSeconds_Normalized_MapsNonPositiveToTheDefaultAndCapsAt600(int configured, int expected) =>
        Assert.AreEqual(expected, new Sextant.Core.RetentionPolicy { UnitMaxSeconds = configured }.Normalized().UnitMaxSeconds);

    [TestMethod]
    [DataRow(null, 60)]
    [DataRow("0", 60)]
    [DataRow("-3", 60)]
    [DataRow("1", 1)]
    [DataRow("600", 600)]
    [DataRow("601", 600)]
    [DataRow("garbage", 60)]
    public void UnitMaxSeconds_EnvVar_IsClamped(string? env, int expected)
    {
        const string name = "SEXTANT_SERVICE_RETENTION_UNIT_MAX_SECONDS";
        var previous = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, env);
            Assert.AreEqual(expected, ServiceOptions.WithUnitMaxForTesting(new Sextant.Core.RetentionPolicy()).UnitMaxSeconds);
        }
        finally { Environment.SetEnvironmentVariable(name, previous); }
    }
}
