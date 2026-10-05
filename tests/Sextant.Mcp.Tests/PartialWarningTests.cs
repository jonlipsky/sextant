using Sextant.Core;

namespace Sextant.Mcp.Tests;

/// <summary>
/// The lean <c>meta.snapshot.warning</c> of a partial snapshot says WHAT is missing, from the coverage reasons the
/// worker recorded, so an agent can judge whether its question is affected instead of distrusting every answer.
/// </summary>
[TestClass]
public class PartialWarningTests
{
    [TestMethod]
    public void QuotesTheRecordedReasons()
    {
        var coverage = new SnapshotCoverage
        {
            Verdict = SnapshotCoverageVerdict.Partial,
            Reasons =
            [
                "Submodule 'libs/shared' is declared in .gitmodules but is not populated in the checkout, so none of its projects were indexed.",
                "Code in 1 project(s) did not fully compile on the indexer, so references and calls inside them may be missing (src/Api/Api.csproj: 63 unbound name(s)). Calls that failed to bind are kept as candidate matches."
            ]
        };

        Assert.AreEqual(
            "Partial index: Submodule 'libs/shared' is declared in .gitmodules but is not populated in the checkout, " +
            "so none of its projects were indexed. Code in 1 project(s) did not fully compile on the indexer, so " +
            "references and calls inside them may be missing (src/Api/Api.csproj: 63 unbound name(s)). Calls that " +
            "failed to bind are kept as candidate matches. Call get_index_status for details.",
            RemoteResponsePresenter.PartialWarningFor(coverage));
    }

    [TestMethod]
    public void EndsEachReasonWithAPeriod()
    {
        var coverage = new SnapshotCoverage
        {
            Verdict = SnapshotCoverageVerdict.Partial, Reasons = ["submodule_unpopulated: external/tools", "  "]
        };

        Assert.AreEqual(
            "Partial index: submodule_unpopulated: external/tools. Call get_index_status for details.",
            RemoteResponsePresenter.PartialWarningFor(coverage));
    }

    [TestMethod]
    public void FallsBackToTheGenericWarning_WhenNoReasonWasRecorded()
    {
        Assert.AreEqual(RemoteResponsePresenter.PartialWarning, RemoteResponsePresenter.PartialWarningFor(null));
        Assert.AreEqual(RemoteResponsePresenter.PartialWarning,
            RemoteResponsePresenter.PartialWarningFor(new SnapshotCoverage { Verdict = SnapshotCoverageVerdict.Partial }));
    }

    [TestMethod]
    public void CapsLongReasons_AtAWordBoundary()
    {
        var reason = string.Join(" ", Enumerable.Range(0, 200).Select(i => $"word{i}"));
        var coverage = new SnapshotCoverage { Verdict = SnapshotCoverageVerdict.Partial, Reasons = [reason] };

        var warning = RemoteResponsePresenter.PartialWarningFor(coverage);

        StringAssert.StartsWith(warning, "Partial index: word0 word1 ");
        StringAssert.EndsWith(warning, " ... Call get_index_status for details.");
        var quoted = warning[RemoteResponsePresenter.PartialWarningPrefix.Length..^RemoteResponsePresenter.PartialWarningSuffix.Length];
        Assert.IsTrue(quoted.Length <= RemoteResponsePresenter.MaxWarningReasonChars + 4, $"quoted {quoted.Length} chars");
        Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(quoted, @" word\d+ \.\.\.$"),
            $"the cut falls between words: ...{quoted[^20..]}");
    }
}
