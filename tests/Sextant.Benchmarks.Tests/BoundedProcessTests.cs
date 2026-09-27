using System.Diagnostics;

namespace Sextant.Benchmarks.Tests;

/// <summary>
/// Pins the guarantees of the shared <see cref="BoundedProcess"/> helper that every Benchmarks subprocess
/// goes through (issue #144): concurrent draining of both streams, a bounded wait that kills only the
/// helper's own process tree, and completion that never waits on pipe EOF held open by a descendant.
/// Each scenario uses the platform shell so no build or restore is needed.
/// </summary>
[TestClass]
public sealed class BoundedProcessTests
{
    private const int Lines = 4000;
    private const string Padding = "padding-padding-padding-padding";

    [TestMethod]
    public void LargeOutputOnBothStreams_IsDrainedConcurrently_WithoutDeadlock()
    {
        // Each stream gets ~180 KB, well past a 64 KB pipe buffer: reading one stream to its end before the
        // other would deadlock as soon as the child blocked writing the unread one.
        var result = OperatingSystem.IsWindows()
            ? BoundedProcess.Run("cmd.exe",
                ["/d", "/s", "/c",
                 $"for /L %i in (0,1,{Lines - 1}) do @(echo out line %i {Padding}& echo err line %i {Padding} 1>&2)"],
                TimeSpan.FromMinutes(2))
            : BoundedProcess.Run("sh",
                ["-c",
                 $"i=0; while [ $i -lt {Lines} ]; do echo \"out line $i {Padding}\"; " +
                 $"echo \"err line $i {Padding}\" >&2; i=$((i+1)); done"],
                TimeSpan.FromMinutes(2));

        Assert.IsTrue(result.Succeeded, result.Describe());
        Assert.IsTrue(result.OutputComplete, "both streams must reach EOF once the child exits");
        StringAssert.Contains(result.StandardOutput, $"out line {Lines - 1} {Padding}");
        StringAssert.Contains(result.StandardError, $"err line {Lines - 1} {Padding}");
        Assert.IsFalse(result.StandardOutput.Contains("err line"), "stderr must not be merged into stdout");
    }

    [TestMethod]
    public void Timeout_KillsTheWholeProcessTree_AndReturnsCapturedOutput()
    {
        // The shell starts a long-lived grandchild that inherits the redirected pipes. Only killing the whole
        // tree closes every write end, so OutputComplete proves the grandchild died with the shell.
        var result = OperatingSystem.IsWindows()
            ? BoundedProcess.Run("cmd.exe",
                ["/d", "/s", "/c", "echo started& ping -n 300 127.0.0.1 >nul& echo never"],
                TimeSpan.FromSeconds(3))
            : BoundedProcess.Run("sh",
                ["-c", "echo started; sleep 300; echo never"],
                TimeSpan.FromSeconds(3));

        Assert.IsTrue(result.TimedOut, result.Describe());
        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.OutputComplete,
            "killing the entire tree must close the grandchild's inherited pipes: " + result.Describe());
        StringAssert.Contains(result.StandardOutput, "started");
        Assert.IsFalse(result.StandardOutput.Contains("never"), result.Describe());
        Assert.IsTrue(result.Elapsed < TimeSpan.FromSeconds(60), $"returned after {result.Elapsed}");
        StringAssert.Contains(result.Describe(), "timed out");
    }

    [TestMethod]
    public void DescendantHoldingThePipes_DoesNotBlockReturn_AfterTheChildExits()
    {
        // The shell exits at once but leaves a background grandchild holding the inherited stdout/stderr —
        // the shape of a reused MSBuild node or a compiler server. A read-to-end or the parameterless
        // WaitForExit() would block until the grandchild exits; the helper returns after the drain grace.
        // Both shells print the grandchild's pid so the test can kill exactly that process afterwards.
        var drainGrace = TimeSpan.FromSeconds(1);
        var result = OperatingSystem.IsWindows()
            ? BoundedProcess.Run("powershell.exe",
                ["-NoProfile", "-NonInteractive", "-Command",
                 "$psi = New-Object System.Diagnostics.ProcessStartInfo 'ping.exe', '-n 30 127.0.0.1'; " +
                 "$psi.UseShellExecute = $false; " +
                 "$p = [System.Diagnostics.Process]::Start($psi); " +
                 "Write-Output ('started ' + $p.Id)"],
                TimeSpan.FromMinutes(1), drainGrace: drainGrace)
            : BoundedProcess.Run("sh",
                ["-c", "sleep 30 & echo started $!"],
                TimeSpan.FromMinutes(1), drainGrace: drainGrace);

        try
        {
            Assert.IsTrue(result.Succeeded, result.Describe());
            Assert.IsFalse(result.OutputComplete, "the grandchild still holds the pipes, so EOF cannot have arrived");
            StringAssert.Contains(result.StandardOutput, "started");
            Assert.IsTrue(result.Elapsed < TimeSpan.FromSeconds(15),
                $"the helper must not wait for the grandchild to exit (returned after {result.Elapsed})");
        }
        finally
        {
            KillPrintedGrandchild(result.StandardOutput);
        }
    }

    [TestMethod]
    public void DotnetRestore_DisablesBuildServersAndNodeReuse()
    {
        var psi = BoundedProcess.CreateDotnetRestoreStartInfo("/repo/App.slnx");

        Assert.AreEqual("dotnet", psi.FileName);
        CollectionAssert.AreEqual(
            new[] { "restore", "/repo/App.slnx", "--disable-build-servers", "-nodeReuse:false" },
            psi.ArgumentList.ToArray());
        Assert.AreEqual("1", psi.Environment["MSBUILDDISABLENODEREUSE"]);
        Assert.AreEqual("0", psi.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"]);
        Assert.AreEqual("false", psi.Environment["UseSharedCompilation"]);
    }

    /// <summary>
    /// Kills exactly the grandchild whose pid the scenario printed (never by name), so the test leaves nothing
    /// behind holding the test host's inherited handles.
    /// </summary>
    private static void KillPrintedGrandchild(string stdout)
    {
        var match = System.Text.RegularExpressions.Regex.Match(stdout, @"started (\d+)");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var pid))
            return;
        try
        {
            using var grandchild = Process.GetProcessById(pid);
            grandchild.Kill();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }
}
