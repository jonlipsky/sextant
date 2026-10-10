using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Sextant.Indexer;

namespace Sextant.Service.Restore;

/// <summary>
/// Restores a checkout's selected project union before the worker loads it. Without a
/// restore there is no <c>obj/project.assets.json</c>, so the design-time build has no package compile assets
/// and the SDK never adds the TRANSITIVE project references the assets file lists: a project then compiles
/// against its direct references only, every type it reaches through another project is unresolved, and the
/// calls that mention one cannot bind.
/// <para>
/// Multi-solution restores use one generated MSBuild traversal and schedule each declared project once, with
/// the globals from its last selected solution. Solution configurations that cannot be reproduced safely
/// fall back to the existing per-solution traversal.
/// </para>
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
/// <para>
/// Issue #231: with <paramref name="sourceCredentials"/> configured, each restore writes them to an owner-only file in
/// its scratch directory, points the child at <see cref="FeedCredentialPlugin"/> (<paramref name="credentialPluginPath"/>)
/// through <c>NUGET_PLUGIN_PATHS</c> and at the file through <see cref="FeedCredentialPlugin.CredentialsFileVariable"/>,
/// and deletes the file when the restore ends. The plugin answers only for an <c>https</c> request to a configured
/// host, so a credential never reaches a source a repository's <c>nuget.config</c> points elsewhere.
/// </para>
/// </summary>
public sealed class PackageRestoreRunner(
    bool enabled = true, TimeSpan? timeout = null, Action<string>? log = null,
    IReadOnlyList<PackageSourceCredential>? sourceCredentials = null, string? credentialPluginPath = null)
{
    private readonly IReadOnlyList<PackageSourceCredential> _sourceCredentials = ValidateCredentials(sourceCredentials, credentialPluginPath);

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

    /// <summary>The package source hosts this runner holds a credential for (never the credentials themselves).</summary>
    public IReadOnlyList<string> CredentialHosts => _sourceCredentials.Select(c => c.HostAndPort).ToList();

    private static IReadOnlyList<PackageSourceCredential> ValidateCredentials(
        IReadOnlyList<PackageSourceCredential>? credentials, string? pluginPath)
    {
        if (credentials is not { Count: > 0 })
            return [];
        if (string.IsNullOrEmpty(pluginPath) || !File.Exists(pluginPath))
            throw new ArgumentException(
                "Package source credentials are configured, but the credential provider plugin assembly was not found.",
                nameof(pluginPath));
        return credentials;
    }

    /// <summary>The <c>dotnet</c> host to run; tests point it at a missing file to exercise a failed start.</summary>
    internal string DotnetPath { get; init; } = ResolveDotnet();

    /// <summary>
    /// Restores <paramref name="solutions"/> under one deadline: <see cref="Timeout"/>,
    /// or <paramref name="limit"/> when it is shorter (the worker's share of a time-budgeted evaluation). Returns
    /// what was achieved; throws only <see cref="OperationCanceledException"/> when
    /// <paramref name="cancellationToken"/> is cancelled (after killing the running restore).
    /// </summary>
    public async Task<PackageRestoreOutcome> RunAsync(
        string checkoutDir, IReadOnlyList<string> solutions, TimeSpan? limit, CancellationToken cancellationToken,
        string? scratchDir = null)
    {
        ArgumentNullException.ThrowIfNull(solutions);
        if (!enabled)
            return PackageRestoreOutcome.Disabled;

        var deadline = limit is { } l && l < Timeout ? (l > TimeSpan.Zero ? l : TimeSpan.Zero) : Timeout;
        var stopwatch = Stopwatch.StartNew();
        var credentialsDir = PrepareCredentials(scratchDir, out var environment);
        var credentialsUnavailable = _sourceCredentials.Count > 0 && credentialsDir is null;
        try
        {
            string? unionFallbackReason = null;
            if (solutions.Count > 1)
            {
                if (TryCreateProjectUnion(checkoutDir, solutions, cancellationToken, out var projects, out var fallbackReason))
                {
                    var union = await RunProjectUnionAsync(
                            checkoutDir, solutions.Count, projects, Remaining(deadline, stopwatch), cancellationToken,
                            scratchDir, environment)
                        .ConfigureAwait(false);
                    if (union is not null)
                        return union with { Timeout = deadline, Elapsed = stopwatch.Elapsed, CredentialsUnavailable = credentialsUnavailable };

                    fallbackReason = "the scratch location could not hold the generated restore traversal";
                }

                log?.Invoke($"package restore: using per-solution fallback ({fallbackReason}).");
                unionFallbackReason = fallbackReason;
            }

            var fallback = await RunPerSolutionAsync(
                    checkoutDir, solutions, Remaining(deadline, stopwatch), cancellationToken, environment)
                .ConfigureAwait(false);
            return fallback with
            {
                Timeout = deadline, Elapsed = stopwatch.Elapsed, CredentialsUnavailable = credentialsUnavailable,
                UnionFallbackReason = unionFallbackReason
            };
        }
        finally
        {
            DeleteCredentials(credentialsDir);
        }
    }

    // Writes the configured credentials to an owner-only file in a fresh owner-only directory and returns that
    // directory (null when none are configured or the file could not be written: the restore then runs without
    // them, and the outcome reports CredentialsUnavailable so the snapshot records why sources were unreachable).
    // environment gets the plugin variables for every restore child of this run.
    private string? PrepareCredentials(string? scratchDir, out IReadOnlyDictionary<string, string> environment)
    {
        environment = new Dictionary<string, string>();
        if (_sourceCredentials.Count == 0)
            return null;
        var dir = Path.Combine(Path.GetFullPath(scratchDir ?? Path.GetTempPath()), $"feed-credentials-{Guid.NewGuid():N}");
        try
        {
            if (OperatingSystem.IsWindows())
                Directory.CreateDirectory(dir);
            else
                Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var file = Path.Combine(dir, "credentials.json");
            FeedCredentialPlugin.WriteCredentialsFile(file, _sourceCredentials);
            environment = new Dictionary<string, string>
            {
                ["NUGET_PLUGIN_PATHS"] = credentialPluginPath!,
                [FeedCredentialPlugin.CredentialsFileVariable] = file,
                // NuGet caches each plugin's per-source claims for 30 days; a per-run cache means a host configured
                // later is asked about at once, never answered from an older run's "no claim".
                ["NUGET_PLUGINS_CACHE_PATH"] = Path.Combine(dir, "plugins-cache")
            };
            return dir;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"package restore could not stage its package source credentials: {ex.GetType().Name}.");
            DeleteCredentials(dir);
            return null;
        }
    }

    private void DeleteCredentials(string? dir)
    {
        if (dir is null)
            return;
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"package restore could not delete its package source credentials: {ex.GetType().Name}.");
        }
    }

    private static TimeSpan Remaining(TimeSpan deadline, Stopwatch stopwatch)
        => deadline > stopwatch.Elapsed ? deadline - stopwatch.Elapsed : TimeSpan.Zero;

    private async Task<PackageRestoreOutcome> RunPerSolutionAsync(
        string checkoutDir, IReadOnlyList<string> solutions, TimeSpan deadline, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string> environment)
    {
        var parser = new RestoreOutputParser(checkoutDir);
        var stopwatch = Stopwatch.StartNew();
        int attempted = 0, succeeded = 0, notStarted = 0;
        var timedOut = false;

        foreach (var solution in solutions)
        {
            var remaining = deadline - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                timedOut = true;
                break;
            }

            attempted++;
            var result = await RunOneAsync(solution, remaining, parser, cancellationToken, environment).ConfigureAwait(false);
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
            SolutionsSelected = solutions.Count,
            SolutionsSucceeded = succeeded,
            SolutionsNotStarted = notStarted,
            TimedOut = timedOut,
            Timeout = deadline,
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

    private async Task<PackageRestoreOutcome?> RunProjectUnionAsync(
        string checkoutDir, int solutionCount, IReadOnlyList<UnionProject> projects,
        TimeSpan deadline, CancellationToken cancellationToken, string? scratchDir,
        IReadOnlyDictionary<string, string> environment)
    {
        var stopwatch = Stopwatch.StartNew();
        var stagingRoot = Path.GetFullPath(scratchDir ?? Path.GetTempPath());
        var stagingDir = Path.Combine(stagingRoot, $"package-restore-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(stagingDir);
            var traversal = Path.Combine(stagingDir, "RestoreUnion.proj");
            await File.WriteAllTextAsync(traversal, BuildUnionProject(projects), cancellationToken).ConfigureAwait(false);

            var parser = new RestoreOutputParser(checkoutDir);
            RunResult result;
            if (deadline <= TimeSpan.Zero)
            {
                result = RunResult.TimedOut;
            }
            else
            {
                result = await RunProcessAsync(
                        CreateUnionStartInfo(traversal, checkoutDir, environment), traversal, deadline, parser, cancellationToken)
                    .ConfigureAwait(false);
            }

            var timedOut = result == RunResult.TimedOut;
            var succeeded = result == RunResult.Succeeded ? 1 : 0;
            var outcome = new PackageRestoreOutcome
            {
                SolutionsAttempted = 1,
                SolutionsSelected = solutionCount,
                SolutionsSucceeded = succeeded,
                SolutionsNotStarted = result == RunResult.NotStarted ? 1 : 0,
                TimedOut = timedOut,
                Timeout = deadline,
                Elapsed = stopwatch.Elapsed,
                ProjectsAttempted = projects.Count,
                UsedProjectUnion = true,
                Projects = parser.Projects(),
                ProjectsDropped = parser.ProjectsDropped,
                GeneralCodes = parser.GeneralCodes(),
                SourceUnreachableGeneral = parser.SourceUnreachableGeneral
            };
            log?.Invoke(
                $"package restore: {projects.Count} distinct project(s) from {solutionCount} selected solution(s) " +
                $"in {outcome.Elapsed.TotalSeconds:0.#}s" +
                (timedOut ? " (timed out)" : string.Empty) +
                (outcome.Projects.Count > 0 ? $"; {outcome.Projects.Count} project(s) reported restore errors" : string.Empty) +
                ".");
            return outcome;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            log?.Invoke($"package restore could not prepare its project union: {ex.GetType().Name}.");
            return null;
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingDir))
                    Directory.Delete(stagingDir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"package restore scratch cleanup failed: {ex.GetType().Name}.");
            }
        }
    }

    private enum RunResult { Succeeded, Failed, NotStarted, TimedOut }

    private async Task<RunResult> RunOneAsync(
        string solution, TimeSpan timeLimit, RestoreOutputParser parser, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string> environment)
    {
        return await RunProcessAsync(CreateStartInfo(solution, environment), solution, timeLimit, parser, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<RunResult> RunProcessAsync(
        ProcessStartInfo startInfo, string description, TimeSpan timeLimit, RestoreOutputParser parser,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return RunResult.NotStarted;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            log?.Invoke($"package restore could not start '{Path.GetFileName(description)}': {ex.GetType().Name}.");
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
                log?.Invoke($"package restore of '{Path.GetFileName(description)}' did not exit after its process tree was killed.");
            }
        }

        if (await Task.WhenAny(drains, Task.Delay(DrainGrace, CancellationToken.None)).ConfigureAwait(false) != drains)
            await abandonDrains.CancelAsync().ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        if (timedOut)
            return RunResult.TimedOut;
        return process.HasExited && process.ExitCode == 0 ? RunResult.Succeeded : RunResult.Failed;
    }

    private static bool TryCreateProjectUnion(
        string checkoutDir, IReadOnlyList<string> solutions, CancellationToken cancellationToken,
        out IReadOnlyList<UnionProject> projects, out string fallbackReason)
    {
        var ordered = new List<UnionProject>();
        var indexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var solution in solutions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CanPreserveSolutionGlobals(solution, out fallbackReason))
            {
                projects = [];
                return false;
            }

            var declared = SolutionProjectEnumerator.Enumerate(solution);
            if (declared.Count == 0)
            {
                projects = [];
                fallbackReason = $"'{Path.GetFileName(solution)}' could not be enumerated completely";
                return false;
            }

            var solutionPath = Path.GetFullPath(solution);
            foreach (var project in declared)
            {
                var projectPath = Path.GetFullPath(project);
                if (!SafeForMsBuildProperty(projectPath) || !SafeForMsBuildProperty(solutionPath))
                {
                    projects = [];
                    fallbackReason = "a solution or project path contains MSBuild list/property syntax";
                    return false;
                }

                if (indexes.TryGetValue(projectPath, out var index))
                {
                    // Existing per-solution restores run in selected-solution order and leave the last solution's
                    // evaluation in project.assets.json. Keep that same owner while restoring each path only once.
                    ordered[index] = ordered[index] with { SolutionPath = solutionPath };
                }
                else
                {
                    indexes.Add(projectPath, ordered.Count);
                    ordered.Add(new UnionProject(projectPath, solutionPath));
                }
            }
        }

        projects = ordered;
        var projectPaths = new HashSet<string>(ordered.Select(project => project.ProjectPath), StringComparer.OrdinalIgnoreCase);
        foreach (var project in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ProjectReferencesAreContained(project.ProjectPath, projectPaths, checkoutDir, out fallbackReason))
            {
                projects = [];
                return false;
            }
        }
        fallbackReason = string.Empty;
        return ordered.Count > 0;
    }

    private static bool ProjectReferencesAreContained(
        string projectPath, IReadOnlySet<string> projectPaths, string checkoutDir, out string reason)
    {
        reason = string.Empty;
        try
        {
            var projectRoot = Path.GetFullPath(checkoutDir);
            var projectDirectory = Path.GetDirectoryName(projectPath)!;
            var relativeDirectory = Path.GetRelativePath(projectRoot, projectDirectory);
            if (Path.IsPathRooted(relativeDirectory) || relativeDirectory == ".."
                || relativeDirectory.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                reason = $"'{Path.GetFileName(projectPath)}' is outside the checkout";
                return false;
            }

            var directory = projectDirectory;
            while (true)
            {
                foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets" })
                {
                    var buildFile = Path.Combine(directory, name);
                    if (!File.Exists(buildFile))
                        continue;
                    using var buildReader = XmlReader.Create(buildFile,
                        new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                    var buildDocument = XDocument.Load(buildReader);
                    if (buildDocument.Descendants().Any(element =>
                            element.Name.LocalName is "ProjectReference" or "Import" or "ImportGroup"))
                    {
                        reason = $"'{Path.GetFileName(projectPath)}' uses a Directory.Build file with additional project definitions";
                        return false;
                    }
                }
                var pathComparison = OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                if (Path.GetFullPath(directory).Equals(projectRoot, pathComparison))
                    break;
                var parent = Path.GetDirectoryName(directory);
                if (parent is null)
                    break;
                directory = parent;
            }

            using var reader = XmlReader.Create(projectPath,
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var document = XDocument.Load(reader);
            var root = document.Root;
            if (root is null || root.Name.LocalName != "Project")
            {
                reason = $"'{Path.GetFileName(projectPath)}' is not a readable MSBuild project";
                return false;
            }

            if (root.Descendants().Any(element => element.Name.LocalName is "Import" or "ImportGroup"))
            {
                reason = $"'{Path.GetFileName(projectPath)}' imports additional project definitions";
                return false;
            }

            foreach (var reference in root.Descendants().Where(element => element.Name.LocalName == "ProjectReference"))
            {
                if (HasCondition(reference, root)
                    || reference.Attribute("Update") is not null
                    || reference.Attribute("Remove") is not null
                    || reference.Elements().Any(metadata =>
                        metadata.Name.LocalName is "AdditionalProperties" or "GlobalPropertiesToRemove"
                            or "SetConfiguration" or "SetPlatform"))
                {
                    reason = $"'{Path.GetFileName(projectPath)}' has conditional or customized project references";
                    return false;
                }

                var include = reference.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include)
                    || include.Contains("$(", StringComparison.Ordinal)
                    || include.Contains("@(", StringComparison.Ordinal)
                    || include.Contains(';') || include.Contains('*') || include.Contains('?'))
                {
                    reason = $"'{Path.GetFileName(projectPath)}' has a project reference that cannot be enumerated safely";
                    return false;
                }

                var referencedPath = Path.GetFullPath(
                    include.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar),
                    Path.GetDirectoryName(projectPath)!);
                if (!projectPaths.Contains(referencedPath))
                {
                    reason = $"'{Path.GetFileName(projectPath)}' references a project outside the selected solution union";
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or ArgumentException)
        {
            reason = $"'{Path.GetFileName(projectPath)}' project references could not be inspected";
            return false;
        }
    }

    private static bool HasCondition(XElement reference, XElement project)
    {
        for (var element = reference; element is not null && element != project; element = element.Parent)
            if (element.Attribute("Condition") is not null)
                return true;
        return project.Attribute("Condition") is not null;
    }

    private static bool CanPreserveSolutionGlobals(string solution, out string reason)
    {
        reason = string.Empty;
        var extension = Path.GetExtension(solution);
        if (extension.Equals(".sln", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var section = string.Empty;
                foreach (var line in File.ReadLines(solution))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("GlobalSection(SolutionConfigurationPlatforms)", StringComparison.Ordinal))
                    {
                        section = "solution";
                        continue;
                    }
                    if (trimmed.StartsWith("GlobalSection(ProjectConfigurationPlatforms)", StringComparison.Ordinal))
                    {
                        section = "project";
                        continue;
                    }
                    if (trimmed.StartsWith("EndGlobalSection", StringComparison.Ordinal))
                    {
                        section = string.Empty;
                        continue;
                    }

                    if (section == "solution" && trimmed.Contains('='))
                    {
                        var separator = trimmed.IndexOf('=');
                        var configuration = trimmed[..separator].Trim();
                        var mappedConfiguration = trimmed[(separator + 1)..].Trim();
                        if (configuration is not ("Debug|Any CPU" or "Release|Any CPU" or "Debug|AnyCPU" or "Release|AnyCPU"))
                        {
                            reason = $"'{Path.GetFileName(solution)}' has a non-default solution configuration";
                            return false;
                        }
                        if (!NormalizeConfiguration(configuration).Equals(
                                NormalizeConfiguration(mappedConfiguration), StringComparison.OrdinalIgnoreCase))
                        {
                            reason = $"'{Path.GetFileName(solution)}' maps a solution configuration to a different configuration";
                            return false;
                        }
                    }
                    else if (section == "project" && !ProjectConfigurationMatches(trimmed))
                    {
                        reason = $"'{Path.GetFileName(solution)}' has a project-specific configuration mapping";
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                reason = $"'{Path.GetFileName(solution)}' configuration could not be inspected";
                return false;
            }
        }

        if (extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                using var reader = XmlReader.Create(solution, settings);
                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.Element
                        && (reader.LocalName.Contains("Configuration", StringComparison.OrdinalIgnoreCase)
                            || reader.LocalName.Equals("Platform", StringComparison.OrdinalIgnoreCase)
                            || reader.LocalName.Equals("BuildType", StringComparison.OrdinalIgnoreCase)))
                    {
                        reason = $"'{Path.GetFileName(solution)}' declares solution-specific configuration";
                        return false;
                    }
                    if (reader.NodeType == XmlNodeType.Element)
                    {
                        for (var i = 0; i < reader.AttributeCount; i++)
                        {
                            reader.MoveToAttribute(i);
                            if (reader.LocalName.Contains("Configuration", StringComparison.OrdinalIgnoreCase)
                                || reader.LocalName.Equals("Platform", StringComparison.OrdinalIgnoreCase)
                                || reader.LocalName.Equals("BuildType", StringComparison.OrdinalIgnoreCase))
                            {
                                reason = $"'{Path.GetFileName(solution)}' declares solution-specific configuration";
                                return false;
                            }
                        }
                        reader.MoveToElement();
                    }
                }
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or ArgumentException)
            {
                reason = $"'{Path.GetFileName(solution)}' configuration could not be inspected";
                return false;
            }
        }

        reason = $"'{Path.GetFileName(solution)}' is not a supported solution format";
        return false;
    }

    private static bool ProjectConfigurationMatches(string line)
    {
        var marker = line.IndexOf(".ActiveCfg = ", StringComparison.Ordinal);
        var markerLength = ".ActiveCfg = ".Length;
        if (marker < 0)
        {
            marker = line.IndexOf(".Build.0 = ", StringComparison.Ordinal);
            markerLength = ".Build.0 = ".Length;
        }
        if (marker < 0)
            return true;

        var guidEnd = line.IndexOf('}');
        if (guidEnd < 0 || guidEnd + 2 > marker)
            return false;
        var source = line[(guidEnd + 2)..marker].Trim();
        var target = line[(marker + markerLength)..].Trim();
        return NormalizeConfiguration(source).Equals(NormalizeConfiguration(target), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeConfiguration(string value) => value.Replace("Any CPU", "AnyCPU", StringComparison.OrdinalIgnoreCase);

    private static bool SafeForMsBuildProperty(string value) =>
        !value.Contains(';') && !value.Contains('$') && !value.Contains('%');

    private static string BuildUnionProject(IReadOnlyList<UnionProject> projects)
    {
        var xml = new StringBuilder();
        xml.AppendLine("<Project>");
        xml.AppendLine("  <Target Name=\"RestoreUnion\">");
        foreach (var project in projects)
        {
            var solutionDir = Path.GetDirectoryName(project.SolutionPath) ?? ".";
            solutionDir = Path.EndsInDirectorySeparator(solutionDir) ? solutionDir : solutionDir + Path.DirectorySeparatorChar;
            var properties = string.Join(';',
                "Configuration=Debug",
                "Platform=AnyCPU",
                "DesignTimeBuild=true",
                "RestoreIgnoreFailedSources=true",
                "RestoreProjectReferences=false",
                "RestoreRecursive=false",
                "BuildingSolutionFile=true",
                $"SolutionDir={solutionDir}",
                $"SolutionName={Path.GetFileNameWithoutExtension(project.SolutionPath)}",
                $"SolutionPath={project.SolutionPath}",
                $"SolutionFileName={Path.GetFileName(project.SolutionPath)}",
                $"SolutionExt={Path.GetExtension(project.SolutionPath)}");
            xml.AppendLine(
                $"    <MSBuild Projects=\"{Xml(project.ProjectPath)}\" Targets=\"Restore\" BuildInParallel=\"false\" " +
                $"ContinueOnError=\"ErrorAndContinue\" Properties=\"{Xml(properties)}\" />");
        }
        xml.AppendLine("  </Target>");
        xml.AppendLine("</Project>");
        return xml.ToString();
    }

    private static string Xml(string value) => SecurityElement.Escape(value) ?? string.Empty;

    private sealed record UnionProject(string ProjectPath, string SolutionPath);

    internal ProcessStartInfo CreateStartInfo(string solution, IReadOnlyDictionary<string, string>? environment = null)
        => CreateStartInfo(Arguments(solution), Path.GetDirectoryName(Path.GetFullPath(solution)) ?? Environment.CurrentDirectory,
            environment);

    private ProcessStartInfo CreateUnionStartInfo(string project, string checkoutDir, IReadOnlyDictionary<string, string> environment)
        => CreateStartInfo(
            ["msbuild", project, "-target:RestoreUnion", "-nologo", "-verbosity:minimal", "--disable-build-servers", "-nodeReuse:false"],
            checkoutDir, environment);

    private ProcessStartInfo CreateStartInfo(
        IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string>? environment)
    {
        var startInfo = new ProcessStartInfo(DotnetPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory
        };
        foreach (var argument in arguments)
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
        // Issue #231: with credentials configured, this runner's plugin is the only one NuGet starts, and it reads the
        // credentials from a file; the variables carry paths, never a credential.
        startInfo.Environment.Remove(FeedCredentialPlugin.CredentialsFileVariable);
        if (environment is not null)
        {
            foreach (var (name, value) in environment)
                startInfo.Environment[name] = value;
        }
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
