namespace Sextant.Indexer;

/// <summary>
/// The opt-in platform reference pack binding of the local CLI and daemon (issue #309): the same
/// <see cref="PlatformReferencePacks.ApplyAsync"/> the service worker runs (issue #295), over a resilient solution load.
/// </summary>
public static class LocalPlatformPacks
{
    /// <summary>
    /// The options of a local run: the user's NuGet global packages folder (<c>NUGET_PACKAGES</c>, else
    /// <c>~/.nuget/packages</c>) and a per-run <paramref name="scratchDirectory"/> for the download project.
    /// </summary>
    public static PlatformPackOptions ForLocal(string scratchDirectory) =>
        new()
        {
            PackageFolders = [GlobalPackagesFolder()],
            ScratchDirectory = scratchDirectory
        };

    public static string GlobalPackagesFolder()
    {
        var configured = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        return !string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(configured)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
    }

    /// <summary>
    /// Loads <paramref name="solutionPath"/> with <see cref="SolutionLoader.LoadSolutionResilientlyAsync"/>. When
    /// <paramref name="enabled"/>, the load's degraded projects are then bound against their platform reference pack
    /// (nuget.org download into a per-run scratch directory, removed afterwards); otherwise the result is exactly the plain load's.
    /// </summary>
    public static async Task<SolutionLoadResult> LoadAsync(
        string solutionPath,
        bool enabled,
        Action<string>? onDiagnostic = null,
        CancellationToken cancellationToken = default,
        PlatformPackOptions? options = null)
    {
        if (enabled)
            await RestoreAppleProjectsAsync(solutionPath, onDiagnostic, cancellationToken).ConfigureAwait(false);
        var result = await SolutionLoader.LoadSolutionResilientlyAsync(solutionPath, onDiagnostic, cancellationToken)
            .ConfigureAwait(false);
        return await ApplyIfEnabledAsync(result, enabled, onDiagnostic, cancellationToken, options).ConfigureAwait(false);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Path, long LastWriteTicks), byte> AttemptedRestores = new();

    internal static void ResetRestoreMemo() => AttemptedRestores.Clear();

    private static readonly System.Text.RegularExpressions.Regex AppleTfmText = new(
        @"-(ios|maccatalyst|tvos)|<UseMaui>\s*true\s*</UseMaui>",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    // The static read is empty for a conditional or $(...) TargetFramework(s) (MAUI-style), so fall back to a looser text test.
    private static bool IsAppleCandidate(string project)
    {
        var expected = SolutionLoader.ReadExpectedFrameworks(project);
        if (expected.Count > 0)
            return expected.Any(t => PlatformReferencePacks.ParseTarget(t) is not null);
        try
        {
            return AppleTfmText.IsMatch(File.ReadAllText(project));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// The service worker restores before it loads (issue #295), and that restore is what makes the load report the
    /// missing workload: a project with no <c>obj/project.assets.json</c> loads without any failure, so it is never
    /// degraded and nothing is bound. Locally, with the opt-in on, each declared project that is an Apple-platform
    /// candidate (statically targets one, or has a conditional/<c>$(...)</c> target framework that mentions one, or sets
    /// <c>UseMaui</c>) and has no assets file is restored the same way (<c>dotnet restore</c> with the service's flags; it
    /// restores the project's references too). It never fails the load. A restore that was attempted is not retried
    /// (whatever its outcome) until the project file's last-write time changes, for the life of the process, and the whole
    /// pass is bounded by <paramref name="totalTimeout"/> (default 5 minutes); a timeout kills the process tree.
    /// Returns the number of projects restored.
    /// </summary>
    public static async Task<int> RestoreAppleProjectsAsync(
        string solutionPath, Action<string>? onDiagnostic, CancellationToken cancellationToken,
        TimeSpan? timeout = null, TimeSpan? totalTimeout = null, string? dotnetRoot = null)
    {
        var restored = 0;
        var overall = System.Diagnostics.Stopwatch.StartNew();
        var budget = totalTimeout ?? TimeSpan.FromMinutes(5);
        var projects = SolutionProjectEnumerator.Enumerate(solutionPath).ToList();
        for (var i = 0; i < projects.Count; i++)
        {
            var project = projects[i];
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(Path.Combine(Path.GetDirectoryName(project)!, "obj", "project.assets.json"))
                || !IsAppleCandidate(project))
                continue;
            var key = (Path.GetFullPath(project), File.GetLastWriteTimeUtc(project).Ticks);
            if (AttemptedRestores.ContainsKey(key))
                continue;
            var remaining = budget - overall.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                onDiagnostic?.Invoke(
                    "Platform reference pack: the restore time budget is spent; the remaining Apple-platform projects were not restored.");
                break;
            }
            onDiagnostic?.Invoke($"Platform reference pack: restoring '{Path.GetFileName(project)}' so its missing workload is reported.");
            var limit = timeout is { } each && each < remaining ? each : remaining;
            var ok = await RunRestoreAsync(project, limit, cancellationToken, dotnetRoot).ConfigureAwait(false);
            AttemptedRestores[key] = 0;
            if (ok)
                restored++;
            else
                onDiagnostic?.Invoke($"Platform reference pack: restore of '{Path.GetFileName(project)}' did not complete.");
        }
        return restored;
    }

    private static async Task<bool> RunRestoreAsync(
        string project, TimeSpan timeout, CancellationToken cancellationToken, string? dotnetRoot = null)
    {
        var root = dotnetRoot ?? PlatformReferencePacks.DefaultDotnetRoot();
        var name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var start = new System.Diagnostics.ProcessStartInfo(
            root is not null && File.Exists(Path.Combine(root, name)) ? Path.Combine(root, name) : "dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(project)!,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[]
                 {
                     "restore", project, "-p:DesignTimeBuild=true", "-p:TreatWarningsAsErrors=false", "-p:WarningsAsErrors=",
                     "--ignore-failed-sources", "--disable-build-servers", "-nodeReuse:false", "--nologo"
                 }.Concat(EvaluationGlobalProperties.ForSolutionOrProject(project).Select(p => $"-p:{p.Key}={p.Value}")))
            start.ArgumentList.Add(argument);
        start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        try
        {
            using var process = System.Diagnostics.Process.Start(start);
            if (process is null)
                return false;
            process.StandardInput.Close();
            // Drain both pipes concurrently and never wait on pipe EOF: a leaked MSBuild node could hold them open.
            var drain = Task.WhenAll(process.StandardOutput.ReadToEndAsync(CancellationToken.None),
                process.StandardError.ReadToEndAsync(CancellationToken.None));
            _ = drain.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                               or System.ComponentModel.Win32Exception) { }
                cancellationToken.ThrowIfCancellationRequested();
                return false;
            }
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    /// <summary>The coverage line for bound projects, worded like the service's coverage reason (<c>SnapshotCoverageBuilder</c>).</summary>
    public static string BindingSummary(IReadOnlyList<PlatformPackBinding> bindings)
    {
        var names = bindings.Select(b => $"{Path.GetFileName(b.ProjectPath)} ({b.TargetFramework ?? "unknown TFM"})").Take(10).ToList();
        var sample = string.Join(", ", names) + (bindings.Count > names.Count ? $", +{bindings.Count - names.Count} more" : string.Empty);
        return $"{bindings.Count} Apple-platform project version(s) were compiled against the platform " +
            $"reference pack without the workload ({sample}); their package references may still be missing.";
    }

    /// <summary>Returns <paramref name="result"/> untouched when off or when nothing degraded; else <see cref="ApplyAsync"/>.</summary>
    public static async Task<SolutionLoadResult> ApplyIfEnabledAsync(
        SolutionLoadResult result,
        bool enabled,
        Action<string>? onDiagnostic = null,
        CancellationToken cancellationToken = default,
        PlatformPackOptions? options = null) =>
        !enabled || result.DegradedProjects.Count == 0
            ? result
            : await ApplyAsync(result, onDiagnostic, cancellationToken, options).ConfigureAwait(false);

    /// <summary>Binds the degraded projects of <paramref name="result"/>; a scratch directory is created and removed unless <paramref name="options"/> supplies one.</summary>
    public static async Task<SolutionLoadResult> ApplyAsync(
        SolutionLoadResult result,
        Action<string>? onDiagnostic,
        CancellationToken cancellationToken,
        PlatformPackOptions? options = null)
    {
        var scratch = options?.ScratchDirectory is null
            ? Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"sextant-platform-packs-{Guid.NewGuid():N}"))
            : null;
        try
        {
            var effective = options is null
                ? ForLocal(scratch!)
                : scratch is null ? options : options with { ScratchDirectory = scratch };
            if (scratch is not null)
                Directory.CreateDirectory(scratch);
            onDiagnostic?.Invoke($"Platform reference pack: {result.DegradedProjects.Count} degraded project version(s) considered.");
            var (solution, bindings) = await PlatformReferencePacks.ApplyAsync(
                result.Solution, result.DegradedProjects, effective, onDiagnostic, cancellationToken).ConfigureAwait(false);
            return result with { Solution = solution, PlatformPackBindings = bindings };
        }
        finally
        {
            if (scratch is not null)
            {
                try
                {
                    if (Directory.Exists(scratch))
                        Directory.Delete(scratch, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
