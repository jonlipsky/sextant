namespace Sextant.Benchmarks.Tests;

/// <summary>
/// The one restore entry point for the heavy MSBuildWorkspace tests. It goes through
/// <see cref="BoundedProcess"/> so a restore can never hang the test run (issue #144): a timeout fails the
/// test with the captured output, and a restore that merely fails (e.g. no network) marks the test
/// inconclusive, because its premise (a restored corpus) does not hold. Most callers already behaved that
/// way; <c>ResilientSolutionLoadTests</c> previously ignored the restore exit code.
/// </summary>
internal static class CorpusRestore
{
    public static void Restore(string projectOrSolutionPath)
    {
        var result = BoundedProcess.DotnetRestore(projectOrSolutionPath);
        if (result.TimedOut)
            Assert.Fail("restore of the generated corpus did not finish: " + result.Describe());
        if (result.ExitCode != 0)
            Assert.Inconclusive("restore of the generated corpus failed: " + result.Describe());
    }
}
