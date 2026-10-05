using System.ComponentModel;
using System.Diagnostics;

namespace Sextant.Service.Restore;

/// <summary>
/// Runs <c>dotnet restore</c> over a checkout's selected solutions before the worker loads them. Without a
/// restore there is no <c>obj/project.assets.json</c>, so the design-time build has no package compile assets
/// and the SDK never adds the TRANSITIVE project references the assets file lists: a project then compiles
/// against its direct references only, every type it reaches through another project is unresolved, and the
/// calls that mention one cannot bind.
/// <para>
/// The restore is best effort and never fails the job. It runs with <c>--ignore-failed-sources</c>, so an
/// unreachable or credential-gated source does not stop the other packages from being restored (the assets
/// file is still written), and with <c>DesignTimeBuild=true</c>, like the workspace load, so a missing optional
/// workload does not fail it. Whatever it could not do is reported in the returned
/// <see cref="PackageRestoreOutcome"/>. The process tree is bounded by one deadline across all solutions, is
/// killed on expiry or cancellation, and is never waited on for pipe EOF (a reused MSBuild node can inherit
/// the pipes). Restore evaluates the repository's MSBuild files exactly as the in-process load does, so it runs
/// inside the same evaluation sandbox and inherits its scrubbed environment.
/// </para>
/// </summary>
public sealed class PackageRestoreRunner(bool enabled = true, TimeSpan? timeout = null, Action<string>? log = null)
{
    /// <summary>The default bound on one job's restore step (<c>SEXTANT_SERVICE_PACKAGE_RESTORE_TIMEOUT_SECONDS</c>).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    /// <summary>The largest accepted bound; a larger configured value is clamped to it.</summary>
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromHours(1);

    /// <summary>The identity component a node with restore disabled publishes under.</summary>
    public const string DisabledIdentityComponent = "off";

    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(10);

    // MSBuildLocator points these at the SDK it registered for the in-process load. A child dotnet must resolve
    // its own SDK from the solution's global.json instead, exactly like a developer's restore.
    private static readonly string[] InheritedMSBuildVariables =
        ["MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "MSBuildSDKsPath", "MSBuildLoadMicrosoftTargetsReadOnly"];

    /// <summary>The identity component for a restore toggle: null when on (the default), <c>off</c> when off.</summary>
    public static string? IdentityComponentFor(bool restoreEnabled) => restoreEnabled ? null : DisabledIdentityComponent;

    /// <summary>Restore runs on this node.</summary>
    public bool Enabled => enabled;

    /// <summary>The bound on one job's restore step, clamped to <see cref="MaxTimeout"/>.</summary>
    public TimeSpan Timeout { get; } = timeout is { } t && t > TimeSpan.Zero
        ? (t > MaxTimeout ? MaxTimeout : t)
        : DefaultTimeout;

    /// <summary>The <see cref="Sextant.Core.SnapshotIdentity.RestorePolicy"/> component this runner publishes under.</summary>
    public string? IdentityComponent => IdentityComponentFor(enabled);

    /// <summary>The <c>dotnet</c> host to run; tests point it at a missing file to exercise a failed start.</summary>
    internal string DotnetPath { get; init; } = ResolveDotnet();

    /// <summary>
    /// Restores every solution in <paramref name="solutions"/> in order, under one deadline. Returns what was
    /// achieved; throws only <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/>
    /// is cancelled (after killing the running restore).
    /// </summary>
    public async Task<PackageRestoreOutcome> RunAsync(
        string checkoutDir, IReadOnlyList<string> solutions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(solutions);
        if (!enabled)
            return PackageRestoreOutcome.Disabled;

        var parser = new RestoreOutputParser(checkoutDir);
        var stopwatch = Stopwatch.StartNew();
        int attempted = 0, succeeded = 0, notStarted = 0;
        var timedOut = false;

        foreach (var solution in solutions)
        {
            var remaining = Timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                timedOut = true;
                break;
            }

            attempted++;
            var result = await RunOneAsync(solution, remaining, parser, cancellationToken).ConfigureAwait(false);
            switch (result)
            {
                case RunResult.Succeeded:
                    succeeded++;
                    break;
                case RunResult.NotStarted:
                    notStarted++;
                    break;
                case RunResult.TimedOut:
                    timedOut = true;
                    break;
            }
            if (timedOut)
                break;
        }

        var outcome = new PackageRestoreOutcome
        {
            SolutionsAttempted = attempted,
            SolutionsSucceeded = succeeded,
            SolutionsNotStarted = notStarted,
            TimedOut = timedOut,
            Timeout = Timeout,
            Elapsed = stopwatch.Elapsed,
            Projects = parser.Projects(),
            ProjectsDropped = parser.ProjectsDropped,
            GeneralCodes = parser.GeneralCodes(),
            SourceUnreachableGeneral = parser.SourceUnreachableGeneral
        };
        log?.Invoke(
            $"package restore: {succeeded}/{attempted} solution(s) restored cleanly in {outcome.Elapsed.TotalSeconds:0.#}s" +
            (timedOut ? " (timed out)" : string.Empty) +
            (outcome.Projects.Count > 0 ? $"; {outcome.Projects.Count} project(s) reported restore errors" : string.Empty) + ".");
        return outcome;
    }

    private enum RunResult { Succeeded, Failed, NotStarted, TimedOut }

    private async Task<RunResult> RunOneAsync(
        string solution, TimeSpan timeLimit, RestoreOutputParser parser, CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(solution);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return RunResult.NotStarted;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            log?.Invoke($"package restore could not start '{Path.GetFileName(solution)}': {ex.GetType().Name}.");
            return RunResult.NotStarted;
        }

        try { process.StandardInput.Close(); }
        catch (IOException) { /* the child already exited and closed its end */ }

        using var abandonDrains = new CancellationTokenSource();
        var drains = Task.WhenAll(
            DrainAsync(process.StandardOutput, parser, abandonDrains.Token),
            DrainAsync(process.StandardError, parser, abandonDrains.Token));

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeLimit);
        var timedOut = false;
        try
        {
            // Waits for the process only: the streams are drained by our own readers, never by the Process
            // async-read machinery, so this does not wait for pipe EOF.
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            KillProcessTree(process);
            using var killWait = new CancellationTokenSource(KillGrace);
            try
            {
                await process.WaitForExitAsync(killWait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                log?.Invoke($"package restore of '{Path.GetFileName(solution)}' did not exit after its process tree was killed.");
            }
        }

        if (await Task.WhenAny(drains, Task.Delay(DrainGrace, CancellationToken.None)).ConfigureAwait(false) != drains)
            await abandonDrains.CancelAsync().ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        if (timedOut)
            return RunResult.TimedOut;
        return process.HasExited && process.ExitCode == 0 ? RunResult.Succeeded : RunResult.Failed;
    }

    internal ProcessStartInfo CreateStartInfo(string solution)
    {
        var startInfo = new ProcessStartInfo(DotnetPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(solution)) ?? Environment.CurrentDirectory
        };
        foreach (var argument in Arguments(solution))
            startInfo.ArgumentList.Add(argument);

        foreach (var name in InheritedMSBuildVariables)
            startInfo.Environment.Remove(name);
        // The service's own settings (control/query tokens, caller keys, clone credentials) never reach a process
        // that evaluates the repository's MSBuild files and talks to the package sources its nuget.config names.
        foreach (var name in startInfo.Environment.Keys
                     .Where(k => k.StartsWith("SEXTANT_", StringComparison.OrdinalIgnoreCase)).ToList())
            startInfo.Environment.Remove(name);
        // No build server, compiler server or reused MSBuild node may outlive the restore and hold its pipes.
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        startInfo.Environment["UseSharedCompilation"] = "false";
        startInfo.Environment["MSBUILDTERMINALLOGGER"] = "off";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        // The parser reads English messages for package ids; codes are language neutral.
        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en-US";
        return startInfo;
    }

    /// <summary>The <c>dotnet restore</c> arguments for one solution.</summary>
    internal static IReadOnlyList<string> Arguments(string solution) =>
    [
        "restore", solution,
        "-p:DesignTimeBuild=true",
        "--ignore-failed-sources",
        "--disable-build-servers",
        "-nodeReuse:false",
        "-nologo"
    ];

    private static async Task DrainAsync(StreamReader reader, RestoreOutputParser parser, CancellationToken abandon)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync(abandon).ConfigureAwait(false)) is not null)
                parser.Accept(line);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The pipe broke (the child was killed mid-write), or the drain was abandoned because a descendant
            // still holds the pipe open after the child exited.
        }
        finally
        {
            reader.Dispose();
        }
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // It exited between the wait and the kill.
        }
        catch (Exception ex) when (ex is Win32Exception or AggregateException)
        {
            // A descendant is already exiting or cannot be signalled; the bounded wait still applies.
        }
    }

    // The service normally runs under the dotnet host (`dotnet Sextant.Cli.dll`); use that same host so the
    // restore sees the same installed SDKs. An apphost launch falls back to DOTNET_HOST_PATH, then the PATH.
    private static string ResolveDotnet()
    {
        var processPath = Environment.ProcessPath;
        if (processPath is not null
            && Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }
        var hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return string.IsNullOrEmpty(hostPath) ? "dotnet" : hostPath;
    }
}
