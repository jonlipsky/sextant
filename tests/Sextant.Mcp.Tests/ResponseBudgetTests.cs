namespace Sextant.Mcp.Tests;

/// <summary><see cref="ResponseBudget"/> keeps the most rows whose response fits the budget, and always one.</summary>
[TestClass]
public class ResponseBudgetTests
{
    // A response of `count` rows of `rowChars` characters each, plus a fixed envelope.
    private static string Render(int count, int rowChars) => new string('e', 100) + new string('r', count * rowChars);

    [TestMethod]
    public void Fit_EverythingFits_RendersEveryRow()
    {
        var text = ResponseBudget.Fit(10, 2_000, n => Render(n, 100), t => t.Length);

        Assert.AreEqual(Render(10, 100), text);
    }

    [TestMethod]
    public void Fit_TooLarge_KeepsTheMostRowsThatFit()
    {
        // 100 + 37 * 50 = 1,950 fits 2,000; 38 rows (2,000 + 100) would not.
        var text = ResponseBudget.Fit(200, 2_000, n => Render(n, 50), t => t.Length);

        Assert.AreEqual(Render(38, 50), text);
    }

    [TestMethod]
    public void Fit_OneRowAlreadyTooLarge_StillReturnsOneRow()
    {
        var text = ResponseBudget.Fit(5, 1_000, n => Render(n, 5_000), t => t.Length);

        Assert.AreEqual(Render(1, 5_000), text);
    }

    [TestMethod]
    public void Fit_NoRows_RendersTheEmptyResponse()
    {
        var text = ResponseBudget.Fit(0, 1_000, n => Render(n, 10), t => t.Length);

        Assert.AreEqual(Render(0, 10), text);
    }

    [TestMethod]
    public void Fit_MeasuresWithTheGivenMeasure()
    {
        // The measure (the remote presentation) halves the text: 100 rows of 30 render 3,100, measured 1,550.
        var text = ResponseBudget.Fit(100, 2_000, n => Render(n, 30), t => t.Length / 2);

        Assert.AreEqual(Render(100, 30), text);
    }

    [TestMethod]
    [DataRow(null, ResponseBudget.DefaultMaxChars)]
    [DataRow(0, ResponseBudget.DefaultMaxChars)]
    [DataRow(-5, ResponseBudget.DefaultMaxChars)]
    [DataRow(10, ResponseBudget.MinMaxChars)]
    [DataRow(50_000, 50_000)]
    public void Clamp_DefaultsAndFloors(int? configured, int expected)
    {
        Assert.AreEqual(expected, ResponseBudget.Clamp(configured));
    }
}
