namespace Sextant.Integration.Tests;

/// <summary>
/// The one restore entry point for Integration tests that generate a corpus on disk. It goes through
/// <see cref="BoundedProcess"/>, which turns off MSBuild node reuse, the MSBuild server and the shared
/// compiler, bounds the run, and stops draining shortly after the child exits. A plain
/// <c>ReadToEnd()</c> on the restore's pipes hung for about 15 minutes per test whenever the caller's
/// environment left node reuse on, because a reused MSBuild node inherited and held the pipes (issue #283).
/// A timeout fails the test with the captured output, so a returning hang is visible rather than skipped. A
/// restore that merely fails marks the test inconclusive: its premise (a restored corpus) does not hold, which
/// is not a product regression (an offline machine cannot restore).
/// </summary>
internal static class GeneratedCorpusRestore
{
    public static void Restore(string projectOrSolutionPath)
    {
        var result = BoundedProcess.DotnetRestore(projectOrSolutionPath);
        if (result.TimedOut)
            Assert.Fail($"restore of '{Path.GetFileName(projectOrSolutionPath)}' did not finish: {result.Describe()}");
        if (result.ExitCode != 0)
            Assert.Inconclusive($"restore of '{Path.GetFileName(projectOrSolutionPath)}' failed: {result.Describe()}");
    }
}
