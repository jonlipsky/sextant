using System.Diagnostics;
using System.Text;

namespace Sextant.TestSupport;

/// <summary>The outcome of a <see cref="BoundedProcess"/> run.</summary>
/// <param name="CommandLine">The command that was run, for diagnostics.</param>
/// <param name="ProcessId">The id of the process that was started.</param>
/// <param name="ExitCode">The child's exit code, or <c>null</c> when it could not be observed (it timed out
/// and did not exit even after its process tree was killed).</param>
/// <param name="TimedOut">The child did not exit within the timeout, so its process tree was killed.</param>
/// <param name="OutputComplete">Both redirected streams reached end-of-file. <c>false</c> means a descendant
/// process inherited the pipes and still held them open when the drain grace period ended, so the captured
/// output may be missing its tail. The pending reads are then cancelled where the platform supports it;
/// otherwise the drains finish in the background when the last descendant closes the pipes.</param>
/// <param name="StandardOutput">Captured stdout (the tail, if it exceeded the capture cap).</param>
/// <param name="StandardError">Captured stderr (the tail, if it exceeded the capture cap).</param>
/// <param name="Elapsed">Wall-clock time from start until the result was produced.</param>
internal sealed record BoundedProcessResult(
    string CommandLine,
    int ProcessId,
    int? ExitCode,
    bool TimedOut,
    bool OutputComplete,
    string StandardOutput,
    string StandardError,
    TimeSpan Elapsed)
{
    /// <summary>The child exited on its own with exit code 0.</summary>
    public bool Succeeded => !TimedOut && ExitCode == 0;

    /// <summary>A multi-line description including the captured output, for assertion messages.</summary>
    public string Describe()
    {
        var outcome = TimedOut
            ? $"timed out after {Elapsed.TotalSeconds:0.#}s and its process tree was killed"
            : $"exited with code {ExitCode} after {Elapsed.TotalSeconds:0.#}s";
        var truncation = OutputComplete
            ? ""
            : " (output may be incomplete: a descendant process kept the redirected pipes open)";
        return $"`{CommandLine}` {outcome}{truncation}\n--- stdout ---\n{StandardOutput}\n--- stderr ---\n{StandardError}";
    }
}

/// <summary>
/// Runs a child process from tests and the benchmark harness without the ways a naive
/// <see cref="Process"/> call can hang (issue #144):
/// <list type="bullet">
///   <item>stdout and stderr are drained <b>concurrently</b>, so a child that fills one pipe's buffer while
///   the parent reads the other cannot deadlock;</item>
///   <item>stdin is closed immediately, so a child that unexpectedly prompts sees EOF instead of waiting;</item>
///   <item>the wait is <b>bounded</b>: on timeout only the process tree this helper started is killed
///   (<see cref="Process.Kill(bool)"/> on its own handle — never by name) and the captured output is
///   returned for the failure message;</item>
///   <item>completion never depends on pipe EOF. A descendant that inherited the redirected pipes (for
///   example a reused MSBuild worker node or a compiler server) can hold them open long after the child
///   exits, so once the child exits the helper waits only a short grace period for the streams to finish
///   and then returns with <see cref="BoundedProcessResult.OutputComplete"/> = <c>false</c>. The parameterless
///   <see cref="Process.WaitForExit()"/> and a read-to-end both block on that EOF indefinitely.</item>
/// </list>
/// </summary>
internal static class BoundedProcess
{
    /// <summary>The default bound on how long a child may run before its process tree is killed.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    /// <summary>How long to keep draining after the child exits before giving up on pipe EOF.</summary>
    public static readonly TimeSpan DefaultDrainGrace = TimeSpan.FromSeconds(10);

    /// <summary>How long to wait for a killed process tree to actually exit.</summary>
    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(30);

    /// <summary>Per-stream capture cap; the stream is still fully drained, only the tail is kept.</summary>
    private const int MaxCapturedChars = 256 * 1024;

    /// <summary>
    /// Environment for every <c>dotnet</c> CLI child: no MSBuild node reuse, no MSBuild server and no shared
    /// compiler server, so no long-lived build process outlives the command while holding its pipes.
    /// </summary>
    private static readonly KeyValuePair<string, string>[] DotnetEnvironment =
    [
        new("MSBUILDDISABLENODEREUSE", "1"),
        new("DOTNET_CLI_USE_MSBUILD_SERVER", "0"),
        new("UseSharedCompilation", "false"),
        new("DOTNET_NOLOGO", "1"),
    ];

    /// <summary>Runs <c>dotnet restore</c> on a project or solution with build servers and node reuse off.</summary>
    public static BoundedProcessResult DotnetRestore(
        string projectOrSolutionPath, TimeSpan? timeout = null, string? workingDirectory = null)
        => Run(CreateDotnetRestoreStartInfo(projectOrSolutionPath, workingDirectory), timeout);

    /// <summary>
    /// Builds the start info <see cref="DotnetRestore"/> uses. Exposed so tests can assert that build
    /// servers and MSBuild node reuse are disabled.
    /// </summary>
    public static ProcessStartInfo CreateDotnetRestoreStartInfo(string projectOrSolutionPath, string? workingDirectory = null)
    {
        var psi = CreateStartInfo(
            "dotnet",
            ["restore", projectOrSolutionPath, "--disable-build-servers", "-nodeReuse:false"],
            workingDirectory);
        foreach (var (key, value) in DotnetEnvironment)
            psi.Environment[key] = value;
        return psi;
    }

    /// <summary>Runs <paramref name="fileName"/> with <paramref name="arguments"/> (each passed verbatim).</summary>
    public static BoundedProcessResult Run(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan? timeout = null,
        string? workingDirectory = null,
        TimeSpan? drainGrace = null)
        => Run(CreateStartInfo(fileName, arguments, workingDirectory), timeout, drainGrace);

    /// <summary>
    /// Runs a prepared start info. Redirection and shell settings are forced so the guarantees above hold;
    /// throws if the executable cannot be started.
    /// </summary>
    public static BoundedProcessResult Run(ProcessStartInfo startInfo, TimeSpan? timeout = null, TimeSpan? drainGrace = null)
    {
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        var commandLine = FormatCommandLine(startInfo);
        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = startInfo };
        process.Start();

        try { process.StandardInput.Close(); }
        catch (IOException) { /* the child already exited and closed its end */ }

        var stdout = new TailBuffer(MaxCapturedChars);
        var stderr = new TailBuffer(MaxCapturedChars);
        using var abandonDrains = new CancellationTokenSource();
        var drains = new[]
        {
            stdout.DrainAsync(process.StandardOutput, abandonDrains.Token),
            stderr.DrainAsync(process.StandardError, abandonDrains.Token),
        };

        // The timed overload waits only for the process itself, not for pipe EOF.
        var exited = process.WaitForExit(timeout ?? DefaultTimeout);
        var timedOut = !exited;
        if (timedOut)
        {
            KillProcessTree(process);
            exited = process.WaitForExit(KillGrace);
        }

        var outputComplete = Task.WaitAll(drains, drainGrace ?? DefaultDrainGrace);
        if (!outputComplete)
        {
            // Release the pending reads where the platform supports cancelling them, so the drains dispose
            // their readers instead of lingering until the last descendant closes the pipes.
            abandonDrains.Cancel();
        }
        int? exitCode = exited ? process.ExitCode : null;

        return new BoundedProcessResult(
            commandLine, process.Id, exitCode, timedOut, outputComplete,
            stdout.ToString(), stderr.ToString(), stopwatch.Elapsed);
    }

    private static ProcessStartInfo CreateStartInfo(string fileName, IReadOnlyList<string> arguments, string? workingDirectory)
    {
        var psi = new ProcessStartInfo(fileName);
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);
        if (workingDirectory != null)
            psi.WorkingDirectory = workingDirectory;
        return psi;
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // It exited between the timed-out wait and the kill.
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or AggregateException)
        {
            // A descendant is already exiting or cannot be signalled; the bounded wait below still applies.
        }
    }

    private static string FormatCommandLine(ProcessStartInfo startInfo)
    {
        var parts = new List<string> { startInfo.FileName };
        parts.AddRange(startInfo.ArgumentList.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        if (startInfo.ArgumentList.Count == 0 && !string.IsNullOrEmpty(startInfo.Arguments))
            parts.Add(startInfo.Arguments);
        return string.Join(' ', parts);
    }

    /// <summary>Keeps the last <c>capacity</c> characters written; thread-safe.</summary>
    private sealed class TailBuffer(int capacity)
    {
        private readonly Queue<string> _chunks = new();
        private readonly object _gate = new();
        private int _length;
        private bool _truncated;

        public Task DrainAsync(StreamReader reader, CancellationToken abandon) => Task.Run(async () =>
        {
            var buffer = new char[4096];
            try
            {
                int read;
                while ((read = await reader.ReadAsync(buffer.AsMemory(), abandon).ConfigureAwait(false)) > 0)
                    Append(new string(buffer, 0, read));
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The pipe broke (e.g. the child was killed mid-write), or the caller abandoned the drain after
                // the grace period because a descendant still holds the pipe open.
            }
            finally
            {
                reader.Dispose();
            }
        });

        private void Append(string chunk)
        {
            lock (_gate)
            {
                _chunks.Enqueue(chunk);
                _length += chunk.Length;
                while (_length > capacity && _chunks.Count > 1)
                {
                    _length -= _chunks.Dequeue().Length;
                    _truncated = true;
                }
            }
        }

        public override string ToString()
        {
            lock (_gate)
            {
                var sb = new StringBuilder(_length + 32);
                if (_truncated)
                    sb.Append("[...earlier output truncated...]\n");
                foreach (var chunk in _chunks)
                    sb.Append(chunk);
                return sb.ToString();
            }
        }
    }
}
