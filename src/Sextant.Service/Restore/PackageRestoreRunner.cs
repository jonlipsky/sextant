using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Sextant.Core;
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
/// workload does not fail it. It also applies the workspace load's <see cref="EvaluationGlobalProperties"/>, decided once over
/// the selected solutions' declared projects, so a Windows-targeting project restores its reference packs on a
/// non-Windows host, and a selection without one restores as before (issue #293). Whatever it could not do is reported in the returned
/// <see cref="PackageRestoreOutcome"/>. The process tree is bounded by one deadline across all solutions, is
/// killed on expiry or cancellation, and is never waited on for pipe EOF (a reused MSBuild node can inherit
/// the pipes). Restore evaluates the repository's MSBuild files exactly as the in-process load does, so it runs
/// inside the same evaluation sandbox and inherits its scrubbed environment.
/// </para>
/// <para>
/// Issue #296: a restore is an index input, not a build gate. It passes <see cref="RestoreOnlyProperties"/>, so a
/// repository's <c>TreatWarningsAsErrors</c> (for example inherited from a superproject's <c>Directory.Build.props</c>)
/// cannot turn a restore warning such as <c>NU1505</c> into an error, and a per-solution restore that still exits
/// non-zero (an error raised while NuGet builds the restore graph leaves NO project of that solution restored) is retried
/// project by project through the traversal the union uses, so every restorable project gets its assets file and only the
/// failing ones are reported. A timeout or cancellation is never retried.
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
    IReadOnlyList<PackageSourceCredential>? sourceCredentials = null, string? credentialPluginPath = null,
    bool staticGraphRestore = PackageRestoreRunner.DefaultStaticGraphRestore, int maxNodes = PackageRestoreRunner.DefaultMaxNodes)
{
    private readonly IReadOnlyList<PackageSourceCredential> _sourceCredentials = ValidateCredentials(sourceCredentials, credentialPluginPath);

    /// <summary>The default cap on MSBuild worker processes of a restore (<c>SEXTANT_SERVICE_RESTORE_MAX_NODES</c>, issue #325).</summary>
    public const int DefaultMaxNodes = 2;

    /// <summary>The largest accepted cap; a larger configured value is clamped to it.</summary>
    public const int MaxMaxNodes = 64;

    /// <summary>The MSBuild node cap every restore invocation passes as <c>-maxcpucount:N</c>; a value below 1 means the default.</summary>
    public int MaxNodes { get; } = maxNodes < 1 ? DefaultMaxNodes : Math.Min(maxNodes, MaxMaxNodes);

    /// <summary>The default bound on one job's restore step (<c>SEXTANT_SERVICE_PACKAGE_RESTORE_TIMEOUT_SECONDS</c>).</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    /// <summary>The largest accepted bound; a larger configured value is clamped to it.</summary>
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromHours(1);

    /// <summary>The one default of static-graph restore (issue #272): the constructor, <see cref="ServiceOptions.RestoreStaticGraph"/> and the benchmark share it.</summary>
    public const bool DefaultStaticGraphRestore = true;

    /// <summary>The identity component a node with restore disabled publishes under.</summary>
    public const string DisabledIdentityComponent = "off";

    // KEEP IN SYNC WITH THE RESTORE STATE CACHE (issue #272): RestoreStateStore replays one job's restore output into a
    // later job. The restore arguments and environment are hashed into its fingerprint, but a change to HOW the restore
    // runs that the arguments do not show (this list, the union traversal in RunProjectUnionAsync/RunTraversalAsync, the
    // retry/partial-skip logic, the static-graph fallback, which projects a partial replay leaves to the traversals)
    // must bump RunnerPolicy so no older output is replayed. "2": a not-clean restore saves its clean projects (#349).
    internal const string RunnerPolicy = "2";

    /// <summary>
    /// Global properties only the restore passes (never the workspace load, whose <see cref="EvaluationGlobalProperties"/>
    /// stay as they are): they keep a restore warning from failing the restore (issue #296). <c>MSBuildTreatWarningsAsErrors</c>
    /// is deliberately absent: NuGet's restore task does not read it, and measured with it set the warning is still an error.
    /// Changing this list changes what a restore writes: bump <see cref="RunnerPolicy"/> (the restore state cache, issue #272).
    /// </summary>
    internal static readonly IReadOnlyList<KeyValuePair<string, string>> RestoreOnlyProperties =
    [
        new("TreatWarningsAsErrors", "false"),
        new("WarningsAsErrors", string.Empty)
    ];

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

    /// <summary>
    /// Per-solution <c>dotnet restore</c> passes add <c>-p:RestoreUseStaticGraphEvaluation=true</c> (issue #272), and a
    /// pass that fails as a whole is run once more without it. The generated project traversals (the union and the
    /// per-project retries) are unchanged: their MSBuild task would start one NuGet static-graph process per project
    /// instead of one for the whole graph.
    /// </summary>
    public bool StaticGraphRestore => staticGraphRestore;

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
        string? scratchDir = null, RestoreReuse? reuse = null)
    {
        ArgumentNullException.ThrowIfNull(solutions);
        if (!enabled)
            return PackageRestoreOutcome.Disabled;

        var deadline = limit is { } l && l < Timeout ? (l > TimeSpan.Zero ? l : TimeSpan.Zero) : Timeout;
        var stopwatch = Stopwatch.StartNew();
        // One decision for the whole selection, from the same declared projects the union load evaluates (#293).
        IReadOnlyList<KeyValuePair<string, string>> globals =
            [.. EvaluationGlobalProperties.For(solutions.SelectMany(SolutionProjectEnumerator.Enumerate)), .. RestoreOnlyProperties];

        // Issue #272: skip the restore when nothing it reads changed since the repository's last clean restore.
        RestoreStateStore.Inputs? inputs = null;
        RestoreReuseOutcome? reuseOutcome = null;
        HashSet<string>? replayed = null;
        RestoreMemo? memo = null;
        if (reuse is not null)
        {
            var fingerprintWatch = Stopwatch.StartNew();
            inputs = RestoreStateStore.Compute(checkoutDir, StateContext(checkoutDir, reuse, solutions, globals), cancellationToken);
            var fingerprintMs = fingerprintWatch.ElapsedMilliseconds;
            var prefix = inputs?.Fingerprint[..12];
            if (inputs is null)
                reuseOutcome = new RestoreReuseOutcome { Decision = "unavailable", Reason = "inputs_unreadable", FingerprintMs = fingerprintMs };
            else if (inputs.Refusal is not null)
                reuseOutcome = new RestoreReuseOutcome
                    { Decision = "miss", Reason = inputs.Refusal, SaveSkipped = inputs.Refusal, FingerprintMs = fingerprintMs, FingerprintPrefix = prefix };
            else if (inputs.Floating)
                reuseOutcome = new RestoreReuseOutcome
                    { Decision = "miss", Reason = "floating_versions", SaveSkipped = "floating_versions", FingerprintMs = fingerprintMs, FingerprintPrefix = prefix };
            else
            {
                var replayWatch = Stopwatch.StartNew();
                var replay = RestoreStateStore.TryReplay(reuse, checkoutDir, inputs, log, solutions);
                var (missReason, projectCount, written) = (replay.MissReason, replay.Projects, replay.FilesWritten);
                if (missReason is null && replay.Remaining.Count > 0)
                {
                    // Issue #349: the state holds only the projects that restored cleanly; the rest are restored below.
                    replayed = new HashSet<string>(replay.Replayed, CheckoutInventory.PathComparer);
                    reuseOutcome = new RestoreReuseOutcome
                    {
                        Decision = "partial", Projects = projectCount, ProjectsRemaining = replay.Remaining.Count, FilesWritten = written,
                        FingerprintMs = fingerprintMs, StateMs = replayWatch.ElapsedMilliseconds, FingerprintPrefix = prefix
                    };
                }
                else if (missReason is null)
                {
                    return new PackageRestoreOutcome
                    {
                        SolutionsSelected = solutions.Count,
                        ProjectsAttempted = projectCount,
                        Timeout = deadline,
                        Elapsed = stopwatch.Elapsed,
                        Reuse = new RestoreReuseOutcome
                        {
                            Decision = "reused", Projects = projectCount, FilesWritten = written, FingerprintMs = fingerprintMs,
                            StateMs = replayWatch.ElapsedMilliseconds, FingerprintPrefix = prefix
                        }
                    };
                }
                else
                    reuseOutcome = new RestoreReuseOutcome
                        { Decision = "miss", Reason = missReason, FingerprintMs = fingerprintMs, FingerprintPrefix = prefix };

                // Static-graph failures remembered for exactly these inputs (the memo is keyed by the fingerprint).
                memo = new RestoreMemo(RestoreStateStore.LoadStaticGraphFailures(reuse, inputs.Fingerprint));
            }
        }

        var outcome = await RunCoreAsync(checkoutDir, solutions, globals, deadline, stopwatch, cancellationToken, scratchDir, replayed, memo)
            .ConfigureAwait(false);
        if (reuseOutcome is null)
        {
            LogRestoreErrors(outcome);
            return outcome;
        }
        if (reuse is not null && inputs is not null && reuseOutcome.Decision is "miss" or "partial" && reuseOutcome.SaveSkipped is null)
            reuseOutcome = SaveState(reuse, checkoutDir, solutions, globals, inputs, outcome, reuseOutcome, memo, replayed, cancellationToken);
        outcome = outcome with { Reuse = reuseOutcome };
        LogRestoreErrors(outcome);
        return outcome;
    }

    // The restore context the state fingerprint binds (see RestoreStateStore.Context): the exact argument lists and the
    // environment this runner starts its restore processes with, so a change to either never replays older output.
    private string StateContext(
        string checkoutDir, RestoreReuse reuse, IReadOnlyList<string> solutions, IReadOnlyList<KeyValuePair<string, string>> globals)
    {
        var arguments = new List<string> { Arguments("<solution>", globals, staticGraphRestore, MaxNodes).Aggregate(string.Empty, (a, b) => a + "\u001f" + b) };
        var union = CreateUnionStartInfo("<project>", checkoutDir, new Dictionary<string, string>());
        arguments.Add(union.ArgumentList.Aggregate(string.Empty, (a, b) => a + "\u001f" + b));
        arguments.Add("static_graph=" + staticGraphRestore);
        return RestoreStateStore.Context(
            checkoutDir, reuse.PackagesDirectory, solutions, arguments,
            union.Environment.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)), CredentialHosts, DotnetPath,
            IndexConfigurationHash.AnalyzerVersion, RunnerPolicy);
    }

    // One Information line per job with the top restore error codes (issue #349).
    private void LogRestoreErrors(PackageRestoreOutcome outcome)
    {
        if (outcome.ErrorSummaryLine() is { } line)
            log?.Invoke(line);
    }

    // Saves the state of a restore whose inputs did not change while it ran (a restore can rewrite a lock file). A clean
    // restore saves every project; a not-clean one (issue #349) saves the projects that restored cleanly and lists the
    // rest as remaining, so the next job replays those and restores only the others. A failing project is never saved,
    // known-failing or otherwise: a failed restore's assets cannot be shown identical to a later failed restore's, so its
    // failure is reproduced by restoring it again, whatever its error codes (a transient one included).
    private RestoreReuseOutcome SaveState(
        RestoreReuse reuse, string checkoutDir, IReadOnlyList<string> solutions, IReadOnlyList<KeyValuePair<string, string>> globals,
        RestoreStateStore.Inputs before, PackageRestoreOutcome outcome, RestoreReuseOutcome miss, RestoreMemo? memo,
        HashSet<string>? replayed, CancellationToken cancellationToken)
    {
        // Only a restore that ended with every failure attributed to a named project can be told apart per project.
        if (outcome.TimedOut || outcome.ProjectsDropped > 0 || outcome.GeneralCodes.Count > 0 || outcome.SourceUnreachableGeneral
            || outcome.CredentialsUnavailable || outcome.SolutionsNotStarted > 0
            || (!outcome.Clean && outcome.Projects.Count == 0))
            return miss with { SaveSkipped = outcome.TimedOut ? "timed_out" : "restore_not_clean" };
        var watch = Stopwatch.StartNew();
        var after = RestoreStateStore.Compute(checkoutDir, StateContext(checkoutDir, reuse, solutions, globals), cancellationToken);
        if (after is null || !string.Equals(after.Fingerprint, before.Fingerprint, StringComparison.Ordinal))
            return miss with { SaveSkipped = "inputs_changed_during_restore", FingerprintMs = miss.FingerprintMs + watch.ElapsedMilliseconds };
        if (memo is { Added.Count: > 0 })
            RestoreStateStore.SaveStaticGraphFailures(reuse, before.Fingerprint, memo.Added, log);
        // A partial replay's own output is not "stale" (the checkout held none before it) and is what the state holds, so
        // saving after one stores the replayed projects plus whatever this job restored cleanly.
        // A repaired project (reduced targets, unlocked lock file) is never saved as clean: replaying it later would
        // skip the repair and hide that it was repaired.
        var failed = new HashSet<string>(
            outcome.Projects.Select(p => p.Project).Concat(outcome.RepairedProjects)
                .Select(p => Path.GetFullPath(Path.Combine(checkoutDir, p))), CheckoutInventory.PathComparer);
        var saved = RestoreStateStore.Save(
            reuse, checkoutDir, before, solutions, log, out var skipReason,
            partial: !outcome.Clean || outcome.RepairedProjects.Count > 0, failed: failed);
        return miss with
        {
            Saved = saved is not null, Projects = saved?.Saved ?? 0, ProjectsRemaining = saved?.Remaining ?? 0, SaveSkipped = skipReason,
            FingerprintMs = miss.FingerprintMs + watch.ElapsedMilliseconds, StateMs = watch.ElapsedMilliseconds
        };
    }

    private async Task<PackageRestoreOutcome> RunCoreAsync(
        string checkoutDir, IReadOnlyList<string> solutions, IReadOnlyList<KeyValuePair<string, string>> globals,
        TimeSpan deadline, Stopwatch stopwatch, CancellationToken cancellationToken, string? scratchDir,
        IReadOnlySet<string>? replayed = null, RestoreMemo? memo = null)
    {
        var credentialsDir = PrepareCredentials(scratchDir, out var environment);
        var credentialsUnavailable = _sourceCredentials.Count > 0 && credentialsDir is null;
        try
        {
            string? unionFallbackReason = null;
            if (solutions.Count > 1)
            {
                if (TryCreateProjectUnion(checkoutDir, solutions, cancellationToken, out var projects, out var fallbackReason))
                {
                    // The union traversal restores each project on its own (not recursive), so a project whose saved
                    // assets were replayed is simply left out; an empty remainder restores them all.
                    if (replayed is { Count: > 0 } && projects.Where(p => !replayed.Contains(p.ProjectPath)).ToList() is { Count: > 0 } left)
                        projects = left;
                    var union = await RunProjectUnionAsync(
                            checkoutDir, solutions.Count, projects, globals, Remaining(deadline, stopwatch), cancellationToken,
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
                    checkoutDir, solutions, globals, Remaining(deadline, stopwatch), cancellationToken, environment, scratchDir,
                    replayed, memo)
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
        string checkoutDir, IReadOnlyList<string> solutions, IReadOnlyList<KeyValuePair<string, string>> globals,
        TimeSpan deadline, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string> environment, string? scratchDir,
        IReadOnlySet<string>? replayed = null, RestoreMemo? memo = null)
    {
        var parser = new RestoreOutputParser(checkoutDir);
        var stopwatch = Stopwatch.StartNew();
        int attempted = 0, succeeded = 0, notStarted = 0, retried = 0, retriedProjects = 0, failedAttributed = 0;
        int staticPasses = 0, staticFallbacks = 0, staticMemoSkips = 0, coveredByReplay = 0;
        var timedOut = false;
        var startedUtc = DateTime.UtcNow;
        var passes = new List<RestorePassTiming>();
        var repairs = new List<RepairOutcome>();

        var declaredBySolution = solutions.ToDictionary(
            solution => solution,
            solution => SolutionProjectEnumerator.Enumerate(solution).Select(Path.GetFullPath)
                .Distinct(CheckoutInventory.PathComparer).ToList());
        // Only solutions of one directory can skip each other, and they share the global.json chain, so a differing
        // SDK pin is irrelevant: assess each directory's solutions on their own.
        var sensitiveByDir = new Dictionary<string, IReadOnlyDictionary<string, string>>(CheckoutInventory.PathComparer);
        foreach (var group in solutions.GroupBy(
                     solution => Path.GetDirectoryName(Path.GetFullPath(solution)) ?? string.Empty, CheckoutInventory.PathComparer))
        {
            var members = group.ToList();
            if (members.Count > 1)
                sensitiveByDir[group.Key] = SolutionSensitivity.Classify(
                    members, [.. members.SelectMany(m => declaredBySolution[m]).Distinct(CheckoutInventory.PathComparer)], sameDirectory: true);
        }
        // Project -> directories of the solutions that restored it cleanly in this job.
        var restoredBy = new Dictionary<string, HashSet<string>>(CheckoutInventory.PathComparer);
        // Project -> directories of the solutions whose attempt in this job failed for it DETERMINISTICALLY: the
        // same restore would fail the same way, and its diagnostics are already in the outcome (no assets file).
        var failedBy = new Dictionary<string, HashSet<string>>(CheckoutInventory.PathComparer);
        var skippedNames = new List<string>();
        int partialSkipped = 0, partialProjects = 0;
        var partialDeclinedNames = new List<string>();
        var partialSkippedNames = new List<string>();
        var attemptedDirs = new HashSet<string>(CheckoutInventory.PathComparer);
        // Project -> directory of the solution that made the MOST RECENT attempt at it in this job (any outcome).
        var lastAttemptDir = new Dictionary<string, string>(CheckoutInventory.PathComparer);

        foreach (var solution in solutions)
        {
            // Skipping is output-neutral for the SKIPPED solution: every project it declares was restored cleanly in
            // this job by a solution in the SAME directory (same global.json, NuGet.config and Directory.Solution.*
            // chain), has its assets file, and is not solution-sensitive, so restoring it again would rewrite the
            // same files. It is exactly output-neutral: the most recent attempt at every declared project was by a
            // solution of this same directory, so skipping never changes which solution restored a project last.
            var declared = declaredBySolution[solution];
            var solutionDir = Path.GetDirectoryName(Path.GetFullPath(solution)) ?? string.Empty;
            var sensitive = sensitiveByDir.TryGetValue(solutionDir, out var sensitiveHere)
                ? sensitiveHere
                : new Dictionary<string, string>();
            // Issue #349: a project whose clean assets were replayed from the restore state is covered too, unless it is
            // solution-sensitive (then the solution restores it itself, over the replayed file). The replay wrote and
            // verified its files, wherever the project's intermediate output is configured to be.
            bool Replayed(string project) => replayed is { Count: > 0 } && !sensitive.ContainsKey(project)
                && replayed.Contains(project);
            bool Covered(string project) => Replayed(project) || (!sensitive.ContainsKey(project)
                && lastAttemptDir.TryGetValue(project, out var lastDir) && CheckoutInventory.PathComparer.Equals(lastDir, solutionDir)
                && ((restoredBy.TryGetValue(project, out var dirs) && dirs.Contains(solutionDir) && File.Exists(AssetsFile(project)))
                    || (failedBy.TryGetValue(project, out var failedDirs) && failedDirs.Contains(solutionDir))));
            var uncovered = declared.Where(project => !Covered(project)).ToList();
            var replayDriven = declared.Any(Replayed);
            if (declared.Count > 0 && uncovered.Count == 0)
            {
                skippedNames.Add(Path.GetFileName(solution));
                if (replayDriven)
                    coveredByReplay++;
                continue;
            }

            // Partial skip (#290): when most projects are covered, restore only the rest. Output-neutral for the
            // same reason as the full skip, plus: no project of the solution is solution-sensitive and the solution
            // maps every project to its own default configuration, so a traversal restoring a project on its own
            // (Debug|AnyCPU, recursive like the solution restore) writes what the solution would have written, also for
            // the covered projects it recurses into. An unreadable mapping or any doubt restores the whole solution.
            var solutionImports = declared.Count > 0 && (uncovered.Count * 2 < declared.Count || replayDriven)
                && HasSolutionLevelImports(solutionDir, checkoutDir);
            if (solutionImports)
                partialDeclinedNames.Add(Path.GetFileName(solution));
            if (declared.Count > 0 && ((uncovered.Count * 2 < declared.Count && attemptedDirs.Contains(solutionDir)) || replayDriven)
                && !solutionImports
                && !declared.Any(sensitive.ContainsKey) && SolutionSensitivity.FindNotDefaultMapped(solution).Count == 0
                && SafeForMsBuildProperty(Path.GetFullPath(solution)) && uncovered.All(project => SafeForMsBuildProperty(project)))
            {
                var partialRemaining = deadline - stopwatch.Elapsed;
                if (partialRemaining <= TimeSpan.Zero)
                {
                    timedOut = true;
                    break;
                }

                var partialParser = new RestoreOutputParser(checkoutDir);
                var solutionPath = Path.GetFullPath(solution);
                var partialStarted = DateTime.UtcNow;
                var partialWatch = Stopwatch.StartNew();
                var partialResult = await RunTraversalAsync(
                        solutionDir, [.. uncovered.Select(project => new UnionProject(project, solutionPath))], globals, recursive: true,
                        partialRemaining, partialParser, cancellationToken, scratchDir, environment)
                    .ConfigureAwait(false);
                if (partialResult is { } ran)
                {
                    passes.Add(Pass("partial", Path.GetFileName(solution), uncovered, false, partialStarted, partialWatch.ElapsedMilliseconds, ran));
                    attempted++;
                    partialSkipped++;
                    partialProjects += uncovered.Count;
                    if (replayDriven)
                        coveredByReplay++;
                    partialSkippedNames.Add(Path.GetFileName(solution));
                    foreach (var project in uncovered)
                        lastAttemptDir[project] = solutionDir;
                    parser.MergeFrom(partialParser);
                    switch (ran)
                    {
                        case RunResult.Succeeded:
                            succeeded++;
                            foreach (var project in uncovered)
                            {
                                if (!restoredBy.TryGetValue(project, out var dirs))
                                    restoredBy[project] = dirs = new HashSet<string>(CheckoutInventory.PathComparer);
                                dirs.Add(solutionDir);
                            }
                            break;
                        case RunResult.NotStarted:
                            notStarted++;
                            break;
                        case RunResult.TimedOut:
                            timedOut = true;
                            break;
                        case RunResult.Failed:
                            foreach (var project in DeterministicallyAttempted(uncovered, partialParser, startedUtc, solutionDir, restoredBy))
                            {
                                if (!failedBy.TryGetValue(project, out var dirs))
                                    failedBy[project] = dirs = new HashSet<string>(CheckoutInventory.PathComparer);
                                dirs.Add(solutionDir);
                            }
                            break;
                    }
                    if (timedOut)
                        break;
                    continue;
                }
            }

            if (declared.Count > 0 && attemptedDirs.Contains(solutionDir) && sensitiveByDir.ContainsKey(solutionDir))
                log?.Invoke($"package restore: '{Path.GetFileName(solution)}' is restored although a solution of its directory ran earlier: " +
                    DescribeNotSkipped(declared, solutionDir, sensitive, restoredBy, failedBy));
            if (declared.Count > 0 && attemptedDirs.Contains(solutionDir) && uncovered.Count * 2 < declared.Count)
                log?.Invoke($"package restore: '{Path.GetFileName(solution)}' is a partial-skip candidate ({uncovered.Count} of {declared.Count} uncovered) but is restored whole: " +
                    DescribePartialBlocker(solution, declared, uncovered, sensitive));

            var remaining = deadline - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                timedOut = true;
                break;
            }

            attempted++;
            attemptedDirs.Add(solutionDir);
            foreach (var project in declared)
                lastAttemptDir[project] = solutionDir;
            var attempt = new RestoreOutputParser(checkoutDir);
            var memoKey = (Path.GetFullPath(solution), File.GetLastWriteTimeUtc(solution).Ticks);
            var memoName = Path.GetRelativePath(checkoutDir, Path.GetFullPath(solution)).Replace('\\', '/');
            var rememberedFailure = staticGraphRestore && memo is not null && memo.Knows(memoName);
            if (rememberedFailure)
                staticMemoSkips++;
            var useStaticGraph = staticGraphRestore && !rememberedFailure && !FellBackSolutions.ContainsKey(memoKey);
            var passStarted = DateTime.UtcNow;
            var passWatch = Stopwatch.StartNew();
            var result = await RunOneAsync(solution, globals, remaining, attempt, cancellationToken, environment, useStaticGraph)
                .ConfigureAwait(false);
            passes.Add(Pass("solution", Path.GetFileName(solution), declared, useStaticGraph, passStarted, passWatch.ElapsedMilliseconds, result));
            if (useStaticGraph)
            {
                staticPasses++;
                // A static pass that failed as a whole (failed, no project named) is re-run once without it, since
                // static-graph evaluation runs no targets and can fail where the default restore succeeds. A pass
                // that names failing projects is not re-run: any project it left without assets already gets the
                // #296 per-project retry below, which never uses static graph. The fallback's result is the one reported.
                // A timeout or cancellation is not re-run either (the default restore would hit it too).
                if (result == RunResult.Failed && attempt.Projects().Count == 0)
                {
                    var staticAttempt = attempt;
                    staticFallbacks++;
                    FellBackSolutions[memoKey] = 0;
                    log?.Invoke(
                        $"package restore of '{Path.GetFileName(solution)}' failed with static-graph evaluation; re-running it once without, and restoring it without for the rest of this process.");
                    var fallbackRemaining = deadline - stopwatch.Elapsed;
                    if (fallbackRemaining <= TimeSpan.Zero)
                        result = RunResult.TimedOut;
                    else
                    {
                        attempt = new RestoreOutputParser(checkoutDir);
                        passStarted = DateTime.UtcNow;
                        passWatch.Restart();
                        result = await RunOneAsync(solution, globals, fallbackRemaining, attempt, cancellationToken, environment)
                            .ConfigureAwait(false);
                        passes.Add(Pass("solution", Path.GetFileName(solution), declared, false, passStarted, passWatch.ElapsedMilliseconds, result));
                    }

                    // Remember it only when the failure looks like the graph's own, never a network or time-out one.
                    if (memo is not null && result is RunResult.Succeeded or RunResult.Failed && StaticFailureIsDeterministic(staticAttempt))
                        memo.Add(memoName);
                }
            }
            var retrySucceeded = false;
            var ranRetry = false;
            var outcomeParser = attempt;
            if (result == RunResult.Failed && AnyProjectWithoutAssets(solution))
            {
                // Issue #296: a graph-level error leaves projects of the solution without assets. Restore its
                // projects one by one so only the ones that really cannot restore stay unrestored. A failure that
                // left every project its assets file (an ordinary per-project error) is reported as it is.
                var retryParser = new RestoreOutputParser(checkoutDir);
                var retryStarted = DateTime.UtcNow;
                var retryWatch = Stopwatch.StartNew();
                var retry = await RetryPerProjectAsync(
                        solution, globals, deadline - stopwatch.Elapsed, retryParser, cancellationToken, scratchDir, environment)
                    .ConfigureAwait(false);
                RepairOutcome? repair = null;
                if (retry is { Result: RunResult.Failed })
                {
                    repair = await RepairAsync(
                            checkoutDir, retryParser, globals, deadline - stopwatch.Elapsed, cancellationToken, scratchDir, environment)
                        .ConfigureAwait(false);
                    repairs.Add(repair);
                }
                parser.MergeFrom(retryParser);
                if (retry is { Result: not RunResult.NotStarted } done)
                {
                    passes.Add(Pass("retry", Path.GetFileName(solution), declared, false, retryStarted, retryWatch.ElapsedMilliseconds, done.Result));
                    retried++;
                    ranRetry = true;
                    retriedProjects += done.Projects;
                    outcomeParser = retryParser;
                    result = done.Result;
                    if (result == RunResult.Failed && repair?.Any == true && retryParser.Projects().Count == 0
                        && retryParser.GeneralCodes().Count == 0 && retryParser.ProjectsDropped == 0)
                        result = RunResult.Succeeded;
                    retrySucceeded = result == RunResult.Succeeded;
                }
            }
            if (!ranRetry && result == RunResult.Failed)
            {
                // The failure left every project its assets file, so there was no per-project retry: the projects the
                // attempt named may still be repaired one by one.
                var repair = await RepairAsync(
                        checkoutDir, attempt, globals, deadline - stopwatch.Elapsed, cancellationToken, scratchDir, environment)
                    .ConfigureAwait(false);
                repairs.Add(repair);
                if (repair.Any && attempt.Projects().Count == 0 && attempt.GeneralCodes().Count == 0 && attempt.ProjectsDropped == 0)
                    result = RunResult.Succeeded;
            }
            if (!retrySucceeded)
                parser.MergeFrom(attempt);
            switch (result)
            {
                case RunResult.Succeeded:
                    succeeded++;
                    // A repaired project was restored with reduced targets or an unlocked lock file, not as this solution
                    // would restore it, so another solution of the directory must restore it itself.
                    var repairedHere = RepairedFullPaths(checkoutDir, repairs);
                    foreach (var project in declared.Where(p => !repairedHere.Contains(p)))
                    {
                        if (!restoredBy.TryGetValue(project, out var dirs))
                            restoredBy[project] = dirs = new HashSet<string>(CheckoutInventory.PathComparer);
                        dirs.Add(solutionDir);
                    }
                    break;
                case RunResult.NotStarted:
                    notStarted++;
                    break;
                case RunResult.TimedOut:
                    timedOut = true;
                    break;
                case RunResult.Failed:
                    if (outcomeParser.Projects().Count > 0)
                        failedAttributed++;
                    foreach (var project in DeterministicallyAttempted(declared, outcomeParser, startedUtc, solutionDir, restoredBy))
                    {
                        if (!failedBy.TryGetValue(project, out var dirs))
                            failedBy[project] = dirs = new HashSet<string>(CheckoutInventory.PathComparer);
                        dirs.Add(solutionDir);
                    }
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
            SolutionsFailedAttributed = failedAttributed,
            SolutionsRetriedPerProject = retried,
            ProjectsRetried = retriedProjects,
            LockFileRetriedProjects = [.. repairs.SelectMany(r => r.LockFile)],
            PlatformRetriedProjects = [.. repairs.SelectMany(r => r.Platform)],
            RepairedProjects = [.. repairs.SelectMany(r => r.Projects)],
            RepairNotAttempted = repairs.Sum(r => r.NotAttempted),
            StaticGraphPasses = staticPasses,
            StaticGraphFallbacks = staticFallbacks,
            StaticGraphSkippedByMemo = staticMemoSkips,
            SolutionsCoveredByReplay = coveredByReplay,
            Passes = passes,
            SolutionsSkippedRedundant = skippedNames.Count,
            SkippedSolutionNames = skippedNames,
            SolutionsPartiallySkipped = partialSkipped,
            ProjectsRestoredPartially = partialProjects,
            PartiallySkippedSolutionNames = partialSkippedNames,
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
            (retried > 0 ? $"; {retried} solution(s) retried per project ({retriedProjects} project(s))" : string.Empty) +
            (staticPasses > 0 ? $"; static-graph evaluation on {staticPasses} pass(es), {staticFallbacks} fell back" : string.Empty) +
            (staticMemoSkips > 0 ? $"; static-graph evaluation skipped for {staticMemoSkips} solution(s) it failed for under the same inputs" : string.Empty) +
            (coveredByReplay > 0 ? $"; {coveredByReplay} solution(s) covered by replayed projects" : string.Empty) +
            (skippedNames.Count > 0
                ? $"; {skippedNames.Count} solution(s) skipped as already restored ({string.Join(", ", skippedNames)})"
                : string.Empty) +
            (partialSkipped > 0
                ? $"; {partialSkipped} solution(s) restored only for their uncovered projects ({partialProjects} project(s): {string.Join(", ", partialSkippedNames)})"
                : string.Empty) +
            (partialDeclinedNames.Count > 0
                ? $"; partial skip declined for {partialDeclinedNames.Count} solution(s) because a Directory.Solution.props/targets applies ({string.Join(", ", partialDeclinedNames)})"
                : string.Empty) +
            (timedOut ? " (timed out)" : string.Empty) +
            (outcome.Projects.Count > 0 ? $"; {outcome.Projects.Count} project(s) reported restore errors" : string.Empty) + ".");
        return outcome;
    }

    // The traversal never evaluates the solution metaproject, which imports these files.
    private static bool HasSolutionLevelImports(string solutionDir, string checkoutDir)
    {
        var root = Path.GetFullPath(checkoutDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var dir = solutionDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        while (!string.IsNullOrEmpty(dir))
        {
            foreach (var name in new[] { "Directory.Solution.props", "Directory.Solution.targets" })
            {
                if (File.Exists(Path.Combine(dir, name)))
                    return true;
            }

            if (CheckoutInventory.PathComparer.Equals(dir, root))
                break;
            dir = Path.GetDirectoryName(dir) ?? string.Empty;
        }

        return false;
    }

    private static string DescribeNotSkipped(
        IReadOnlyList<string> declared, string solutionDir, IReadOnlyDictionary<string, string> sensitive,
        Dictionary<string, HashSet<string>> restoredBy, Dictionary<string, HashSet<string>> failedBy)
    {
        var otherDirectory = declared.Count(project =>
            !(restoredBy.TryGetValue(project, out var r) && r.Contains(solutionDir))
            && !(failedBy.TryGetValue(project, out var f) && f.Contains(solutionDir)));
        var notCovered = declared.Where(project => otherDirectory > 0 && !sensitive.ContainsKey(project)
            && !(restoredBy.TryGetValue(project, out var r2) && r2.Contains(solutionDir))
            && !(failedBy.TryGetValue(project, out var f2) && f2.Contains(solutionDir))).ToList();
        var missingAssets = declared.Where(project =>
            restoredBy.TryGetValue(project, out var r) && r.Contains(solutionDir) && !File.Exists(AssetsFile(project))).ToList();
        var reasons = declared.Where(sensitive.ContainsKey).GroupBy(project => sensitive[project])
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Count()} solution-sensitive ({group.Key}){NameSome(group)}");
        return string.Join("; ", reasons
            .Append($"{notCovered.Count} with no clean or deterministic outcome from a solution of this directory (earlier restore failed transiently, reported no result, or a solution of another directory attempted it since){NameSome(notCovered)}")
            .Append($"{missingAssets.Count} without an assets file{NameSome(missingAssets)}")) + $" (of {declared.Count} project(s)).";
    }

    private static string DescribePartialBlocker(
        string solution, IReadOnlyList<string> declared, IReadOnlyList<string> uncovered, IReadOnlyDictionary<string, string> sensitive)
    {
        var parts = new List<string>();
        var sensitiveHere = declared.Where(sensitive.ContainsKey).ToList();
        if (sensitiveHere.Count > 0)
            parts.Add($"{sensitiveHere.Count} solution-sensitive project(s) ({string.Join(", ", sensitiveHere.Select(p => sensitive[p]).Distinct().Order(StringComparer.Ordinal))}){NameSome(sensitiveHere)}");
        var notDefault = SolutionSensitivity.FindNotDefaultMapped(solution);
        if (notDefault.Count > 0)
        {
            var read = "mapping read: " + SolutionSensitivity.DescribeMapping(solution, notDefault);
            parts.Add($"{notDefault.Count} project(s) not mapped to Debug|Any CPU ({read}){NameSome(notDefault)}");
        }
        var unsafePaths = new List<string>();
        if (!SafeForMsBuildProperty(Path.GetFullPath(solution)))
            unsafePaths.Add(solution);
        unsafePaths.AddRange(uncovered.Where(project => !SafeForMsBuildProperty(project)));
        if (unsafePaths.Count > 0)
            parts.Add($"{unsafePaths.Count} path(s) unsafe for an MSBuild property{NameSome(unsafePaths)}");
        if (parts.Count == 0)
            parts.Add("no eligibility rule failed, so the traversal could not be staged or ran no result");
        return string.Join("; ", parts) + ".";
    }

    private static string NameSome(IEnumerable<string> projects)
    {
        var names = projects.Take(6).Select(project => Path.GetFileName(project)).ToList();
        return names.Count == 0 ? string.Empty : " [" + string.Join(", ", names.Take(5)) + (names.Count > 5 ? ", ..." : string.Empty) + "]";
    }

    // Issue #290: the declared projects whose outcome in a FAILED restore is known and deterministic, so a later
    // solution of the same directory need not restore them again. Coverage rests on POSITIVE evidence, never on the
    // absence of an error. A project qualifies when
    //  - the per-project traversal printed its success sentinel (the MSBuild task for it succeeded), or
    //  - the parser attributed errors to it and none is a source-unreachable code (NU1301/NU1801 are transient), or
    //  - an earlier clean restore of the SAME directory credited it and its assets file was rewritten during this job.
    // A source-unreachable report that named no project makes the whole attempt transient. An unreached project of an
    // aborted traversal has no sentinel and no issue, so it is never covered. Known limitation: a whole-solution
    // restore that succeeds only counts a project whose assets file is at the default obj/ location (a custom
    // ArtifactsPath is never skipped there), and error lines for a project outside the checkout are keyed by file name.
    private static List<string> DeterministicallyAttempted(
        IReadOnlyList<string> declared, RestoreOutputParser attempt, DateTime startedUtc, string solutionDir,
        Dictionary<string, HashSet<string>> restoredBy)
    {
        var covered = new List<string>();
        if (attempt.SourceUnreachableGeneral)
            return covered;
        var issues = attempt.Projects().ToDictionary(issue => issue.Project, StringComparer.Ordinal);
        foreach (var project in declared)
        {
            if (issues.TryGetValue(attempt.ProjectKey(project), out var issue))
            {
                if (!issue.SourceUnreachable)
                    covered.Add(project);
            }
            else if (attempt.WasRestored(project))
                covered.Add(project);
            else if (restoredBy.TryGetValue(project, out var dirs) && dirs.Contains(solutionDir)
                && File.Exists(AssetsFile(project)) && File.GetLastWriteTimeUtc(AssetsFile(project)) >= startedUtc)
                covered.Add(project);
        }
        return covered;
    }

    // True when a declared project of the solution has no assets file. Only the default obj/ location is checked (a
    // custom BaseIntermediateOutputPath/MSBuildProjectExtensionsPath is not resolved here, so such a project reads as
    // missing and a failed restore of it is retried, which is harmless). An unreadable solution counts as missing.
    private static bool AnyProjectWithoutAssets(string solution)
    {
        var declared = SolutionProjectEnumerator.Enumerate(solution);
        return declared.Any(project => !File.Exists(AssetsFile(project)));
    }

    private static string AssetsFile(string project) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(project)) ?? ".", "obj", "project.assets.json");

    private async Task<PackageRestoreOutcome?> RunProjectUnionAsync(
        string checkoutDir, int solutionCount, IReadOnlyList<UnionProject> projects,
        IReadOnlyList<KeyValuePair<string, string>> globals, TimeSpan deadline, CancellationToken cancellationToken, string? scratchDir,
        IReadOnlyDictionary<string, string> environment)
    {
        var stopwatch = Stopwatch.StartNew();
        var parser = new RestoreOutputParser(checkoutDir);
        var startedUtc = DateTime.UtcNow;
        var traversal = await RunTraversalAsync(
                checkoutDir, projects, globals, recursive: false, deadline, parser, cancellationToken, scratchDir, environment)
            .ConfigureAwait(false);
        if (traversal is not { } result)
            return null;

        RepairOutcome? repair = null;
        if (result == RunResult.Failed)
        {
            repair = await RepairAsync(checkoutDir, parser, globals, deadline - stopwatch.Elapsed, cancellationToken, scratchDir, environment)
                .ConfigureAwait(false);
            if (repair.Any && parser.Projects().Count == 0 && parser.GeneralCodes().Count == 0 && parser.ProjectsDropped == 0)
                result = RunResult.Succeeded;
        }

        var timedOut = result == RunResult.TimedOut;
        var outcome = new PackageRestoreOutcome
        {
            LockFileRetriedProjects = repair?.LockFile ?? [],
            PlatformRetriedProjects = repair?.Platform ?? [],
            RepairedProjects = repair?.Projects ?? [],
            RepairNotAttempted = repair?.NotAttempted ?? 0,
            SolutionsAttempted = 1,
            SolutionsSelected = solutionCount,
            SolutionsSucceeded = result == RunResult.Succeeded ? 1 : 0,
            SolutionsNotStarted = result == RunResult.NotStarted ? 1 : 0,
            SolutionsFailedAttributed = result == RunResult.Failed && parser.Projects().Count > 0 ? 1 : 0,
            TimedOut = timedOut,
            Timeout = deadline,
            Elapsed = stopwatch.Elapsed,
            ProjectsAttempted = projects.Count,
            UsedProjectUnion = true,
            Passes = [Pass("union", "union", [.. projects.Select(project => project.ProjectPath)], false, startedUtc, (long)stopwatch.Elapsed.TotalMilliseconds, result)],
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

    // Issue #296: the per-project retry of one solution whose restore failed. Null means no retry was possible (the
    // time is spent, a project path is unsafe in an MSBuild property, the solution lists no project, or the traversal
    // could not be staged), so the caller keeps the failed attempt's findings.
    private async Task<(RunResult Result, int Projects)?> RetryPerProjectAsync(
        string solution, IReadOnlyList<KeyValuePair<string, string>> globals, TimeSpan deadline,
        RestoreOutputParser parser, CancellationToken cancellationToken, string? scratchDir,
        IReadOnlyDictionary<string, string> environment)
    {
        if (deadline <= TimeSpan.Zero)
            return null;
        var solutionPath = Path.GetFullPath(solution);
        var declared = SolutionProjectEnumerator.Enumerate(solution);
        if (declared.Count == 0 || !SafeForMsBuildProperty(solutionPath))
            return null;
        var projects = new List<UnionProject>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in declared)
        {
            var projectPath = Path.GetFullPath(project);
            if (!SafeForMsBuildProperty(projectPath))
                return null;
            if (seen.Add(projectPath))
                projects.Add(new UnionProject(projectPath, solutionPath));
        }

        log?.Invoke(
            $"package restore of '{Path.GetFileName(solution)}' failed as a whole; retrying its {projects.Count} project(s) one by one.");
        var workingDirectory = Path.GetDirectoryName(solutionPath) ?? Environment.CurrentDirectory;
        var result = await RunTraversalAsync(
                workingDirectory, projects, globals, recursive: true, deadline, parser, cancellationToken, scratchDir, environment)
            .ConfigureAwait(false);
        return result is { } ran ? (ran, projects.Count) : null;
    }

    // Stages the generated MSBuild traversal in scratch, runs it and removes it. Null when it could not be staged.
    private async Task<RunResult?> RunTraversalAsync(
        string workingDirectory, IReadOnlyList<UnionProject> projects, IReadOnlyList<KeyValuePair<string, string>> globals,
        bool recursive, TimeSpan deadline, RestoreOutputParser parser, CancellationToken cancellationToken, string? scratchDir,
        IReadOnlyDictionary<string, string> environment)
    {
        var stagingRoot = Path.GetFullPath(scratchDir ?? Path.GetTempPath());
        var stagingDir = Path.Combine(stagingRoot, $"package-restore-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(stagingDir);
            var traversal = Path.Combine(stagingDir, "RestoreUnion.proj");
            await File.WriteAllTextAsync(traversal, BuildUnionProject(projects, globals, recursive), cancellationToken)
                .ConfigureAwait(false);

            if (deadline <= TimeSpan.Zero)
                return RunResult.TimedOut;
            return await RunProcessAsync(
                    CreateUnionStartInfo(traversal, workingDirectory, environment), traversal, deadline, parser, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            log?.Invoke($"package restore could not prepare its project traversal: {ex.GetType().Name}.");
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

    // Solutions whose static-graph pass failed, remembered for the life of the process (keyed by path + last write time).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Path, long LastWriteTicks), byte> FellBackSolutions = new();

    internal static void ResetStaticGraphMemo() => FellBackSolutions.Clear();

    // The static pass failed as a whole with at least one recognised code, every code deterministic and no source
    // unreachable. A code-less failure (OOM kill, crash, full disk, uncoded error line) proves nothing.
    private static bool StaticFailureIsDeterministic(RestoreOutputParser staticAttempt)
    {
        var codes = staticAttempt.GeneralCodes();
        return codes.Count > 0 && codes.All(RestoreErrorClassifier.IsDeterministicCode) && !staticAttempt.SourceUnreachableGeneral;
    }

    private enum RunResult { Succeeded, Failed, NotStarted, TimedOut }

    // Telemetry (issue #272): a project whose assets file the pass did not rewrite passed NuGet's up-to-date check.
    private static RestorePassTiming Pass(
        string kind, string target, IReadOnlyList<string> projects, bool staticGraph, DateTime startedUtc, long wallMs, RunResult result)
    {
        var upToDate = 0;
        if (result is RunResult.Succeeded or RunResult.Failed)
        {
            var threshold = startedUtc - TimeSpan.FromSeconds(1);
            foreach (var project in projects)
            {
                var assets = AssetsFile(project);
                if (File.Exists(assets) && File.GetLastWriteTimeUtc(assets) < threshold)
                    upToDate++;
            }
        }
        return new RestorePassTiming
        {
            Kind = kind, Target = target, Projects = projects.Count, ProjectsUpToDateApprox = upToDate, WallMs = wallMs, StaticGraph = staticGraph,
            Result = result switch
            {
                RunResult.Succeeded => "succeeded", RunResult.Failed => "failed", RunResult.TimedOut => "timed_out", _ => "not_started"
            }
        };
    }

    private async Task<RunResult> RunOneAsync(
        string solution, IReadOnlyList<KeyValuePair<string, string>> globals, TimeSpan timeLimit, RestoreOutputParser parser, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string> environment, bool staticGraph = false)
    {
        return await RunProcessAsync(CreateStartInfo(solution, globals, environment, staticGraph), solution, timeLimit, parser, cancellationToken)
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

    private static string BuildUnionProject(
        IReadOnlyList<UnionProject> projects, IReadOnlyList<KeyValuePair<string, string>> globals, bool recursive)
    {
        var xml = new StringBuilder();
        xml.AppendLine("<Project>");
        xml.AppendLine("  <Target Name=\"RestoreUnion\">");
        foreach (var project in projects)
        {
            var solutionDir = Path.GetDirectoryName(project.SolutionPath) ?? ".";
            solutionDir = Path.EndsInDirectorySeparator(solutionDir) ? solutionDir : solutionDir + Path.DirectorySeparatorChar;
            string[] properties =
            [
                "Configuration=Debug",
                "Platform=AnyCPU",
                "DesignTimeBuild=true",
                "RestoreIgnoreFailedSources=true",
                // The union schedules every project itself. A per-project retry (#296) restores each project's
                // references too, so a referenced project outside the solution still gets its assets.
                .. recursive ? Array.Empty<string>() : ["RestoreProjectReferences=false", "RestoreRecursive=false"],
                "BuildingSolutionFile=true",
                $"SolutionDir={solutionDir}",
                $"SolutionName={Path.GetFileNameWithoutExtension(project.SolutionPath)}",
                $"SolutionPath={project.SolutionPath}",
                $"SolutionFileName={Path.GetFileName(project.SolutionPath)}",
                $"SolutionExt={Path.GetExtension(project.SolutionPath)}",
                .. globals.Select(p => $"{p.Key}={p.Value}"),
                .. project.Extra ?? []
            ];
            xml.AppendLine(
                $"    <MSBuild Projects=\"{Xml(project.ProjectPath)}\" Targets=\"Restore\" BuildInParallel=\"false\" " +
                $"ContinueOnError=\"ErrorAndContinue\" Properties=\"{Xml(string.Join(';', properties))}\" />");
            // Positive per-project evidence (issue #290): printed only when the task above succeeded.
            xml.AppendLine(
                $"    <Message Importance=\"high\" Condition=\"'$(MSBuildLastTaskResult)' == 'true'\" " +
                $"Text=\"{RestoreOutputParser.RestoredSentinel}{Xml(project.ProjectPath)}\" />");
        }
        xml.AppendLine("  </Target>");
        xml.AppendLine("</Project>");
        return xml.ToString();
    }

    private static HashSet<string> RepairedFullPaths(string checkoutDir, IEnumerable<RepairOutcome> repairs)
        => repairs.SelectMany(r => r.Projects)
            .Select(p => Path.GetFullPath(Path.Combine(checkoutDir, p.Replace('/', Path.DirectorySeparatorChar))))
            .ToHashSet(CheckoutInventory.PathComparer);

    private static string Xml(string value) => SecurityElement.Escape(value) ?? string.Empty;

    // LockFile/Platform are the report lines; Projects the checkout-relative paths (parser keys) of every repaired
    // project, which were restored with reduced targets or an unlocked lock file and so are not clean; NotAttempted the
    // candidates the restore time limit left unrepaired.
    internal sealed record RepairOutcome(
        IReadOnlyList<string> LockFile, IReadOnlyList<string> Platform, IReadOnlyList<string> Projects, int NotAttempted = 0)
    {
        public bool Any => LockFile.Count > 0 || Platform.Count > 0;
    }

    // Codes a platform-only failure of a multi-target project reports: the SDK's missing-workload errors.
    private static readonly HashSet<string> PlatformFailureCodes = new(StringComparer.Ordinal)
        { "NETSDK1178", "NETSDK1147", "NETSDK1139", "NETSDK1084", "NU1012" };

    // Two narrow second attempts for projects whose restore failed for a reason the checkout's own settings cause, not
    // the package graph (issue #352). Each restores the project alone, without touching the checkout:
    //  - NU1004 (a committed packages.lock.json is stale under RestoreLockedMode): restore unlocked, writing the lock
    //    file to scratch, so the tracked file is never rewritten;
    //  - every error is a missing platform workload of a multi-target project: restore only the targets this worker can
    //    build (TargetFrameworks), so the buildable target gets its assets while the platform ones stay unrestored.
    // A project whose retry restores leaves the parser's findings; anything else keeps what it had. Each probe and the
    // retry are bounded by what is left of <paramref name="deadline"/>; candidates the limit leaves unrepaired are counted.
    internal async Task<RepairOutcome> RepairAsync(
        string checkoutDir, RestoreOutputParser parser, IReadOnlyList<KeyValuePair<string, string>> globals, TimeSpan deadline,
        CancellationToken cancellationToken, string? scratchDir, IReadOnlyDictionary<string, string> environment)
    {
        var none = new RepairOutcome([], [], []);
        var issues = parser.Projects();
        if (issues.Count == 0 || parser.SourceUnreachableGeneral)
            return none;

        var watch = Stopwatch.StartNew();
        var notAttempted = 0;
        var lockDirectory = Path.Combine(Path.GetFullPath(scratchDir ?? Path.GetTempPath()), $"restore-lock-{Guid.NewGuid():N}");
        var projects = new List<UnionProject>();
        var kinds = new Dictionary<string, (string Name, bool LockFile, string? Frameworks)>(CheckoutInventory.PathComparer);
        try
        {
            foreach (var issue in issues)
            {
                if (issue.SourceUnreachable || issue.TransientMessage)
                    continue;
                var isLock = issue.Codes.Contains("NU1004", StringComparer.Ordinal);
                var isPlatform = !isLock && issue.Codes.Count > 0 && issue.Codes.All(PlatformFailureCodes.Contains);
                if (!isLock && !isPlatform)
                    continue;
                var path = Path.GetFullPath(Path.Combine(checkoutDir, issue.Project.Replace('/', Path.DirectorySeparatorChar)));
                if (!File.Exists(path) || !SafeForMsBuildProperty(path))
                    continue;
                if (deadline - watch.Elapsed <= TimeSpan.Zero)
                {
                    notAttempted++;
                    continue;
                }
                if (isLock)
                {
                    if (!SafeForMsBuildProperty(lockDirectory))
                        continue;
                    Directory.CreateDirectory(lockDirectory);
                    var lockFile = Path.Combine(lockDirectory, $"{projects.Count}.packages.lock.json");
                    projects.Add(new UnionProject(path, path, ["RestoreLockedMode=false", $"NuGetLockFilePath={lockFile}"]));
                    kinds[path] = (issue.Project, true, null);
                    continue;
                }

                var (frameworks, outOfTime) = await BuildableFrameworksAsync(
                        path, globals, environment, deadline - watch.Elapsed, cancellationToken).ConfigureAwait(false);
                if (outOfTime)
                    notAttempted++;
                else if (frameworks is not null)
                {
                    projects.Add(new UnionProject(path, path, [$"TargetFrameworks={frameworks.Replace(";", "%3B", StringComparison.Ordinal)}"]));
                    kinds[path] = (issue.Project, false, frameworks);
                }
            }
            if (projects.Count == 0)
                return none with { NotAttempted = notAttempted };

            var left = deadline - watch.Elapsed;
            if (left <= TimeSpan.Zero)
                return none with { NotAttempted = notAttempted + projects.Count };

            log?.Invoke($"package restore: retrying {projects.Count} project(s) whose restore failed on a stale lock file or an unavailable platform target.");
            var repairParser = new RestoreOutputParser(checkoutDir);
            // The solution path of a UnionProject only sets the SolutionDir properties; a project alone has none. The
            // traversal is NOT recursive: the reduced TargetFrameworks is a global property that reaches every project
            // reference, so a recursive restore would rewrite a multi-target sibling's assets with the reduced targets.
            var ran = await RunTraversalAsync(
                    checkoutDir, [.. projects.Select(p => p with { SolutionPath = Path.Combine(Path.GetDirectoryName(p.ProjectPath)!, "_.sln") })],
                    globals, recursive: false, left, repairParser, cancellationToken, scratchDir, environment)
                .ConfigureAwait(false);
            if (ran is null)
                return none with { NotAttempted = notAttempted };

            var lockFiles = new List<string>();
            var platform = new List<string>();
            var repaired = new List<string>();
            foreach (var (path, kind) in kinds)
            {
                if (!repairParser.WasRestored(path))
                {
                    if (ran == RunResult.TimedOut)
                        notAttempted++;
                    continue;
                }
                parser.Forget(path);
                repaired.Add(kind.Name);
                if (kind.LockFile)
                    lockFiles.Add(kind.Name);
                else
                    platform.Add($"{kind.Name} ({kind.Frameworks})");
            }
            lockFiles.Sort(StringComparer.Ordinal);
            platform.Sort(StringComparer.Ordinal);
            repaired.Sort(StringComparer.Ordinal);
            return new RepairOutcome(lockFiles, platform, repaired, notAttempted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"package restore could not prepare its repair retry: {ex.GetType().Name}.");
            return none with { NotAttempted = notAttempted };
        }
        finally
        {
            try
            {
                if (Directory.Exists(lockDirectory))
                    Directory.Delete(lockDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"package restore scratch cleanup failed: {ex.GetType().Name}.");
            }
        }
    }

    // The project's evaluated TargetFrameworks reduced to the ones this worker can build, joined by ';'. Null unless the
    // project is multi-target and at least one target is buildable and at least one is not (nothing to gain otherwise).
    // The probe runs for at most min(60 s, remaining); OutOfTime is true when the restore time limit, not the probe's own
    // bound, stopped it (or none was left).
    internal async Task<(string? Frameworks, bool OutOfTime)> BuildableFrameworksAsync(
        string project, IReadOnlyList<KeyValuePair<string, string>> globals, IReadOnlyDictionary<string, string> environment,
        TimeSpan remaining, CancellationToken cancellationToken)
    {
        var probeLimit = TimeSpan.FromSeconds(60);
        if (remaining <= TimeSpan.Zero)
            return (null, true);
        var bound = remaining < probeLimit ? remaining : probeLimit;
        var startInfo = CreateStartInfo(
            ["msbuild", project, "-getProperty:TargetFrameworks", "-p:DesignTimeBuild=true", .. globals.Select(p => $"-p:{p.Key}={p.Value}"),
                "--disable-build-servers", "-nodeReuse:false", "-nologo"],
            Path.GetDirectoryName(project) ?? Environment.CurrentDirectory, environment);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return (null, false);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return (null, false);
        }
        try { process.StandardInput.Close(); }
        catch (IOException) { /* already exited */ }

        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(bound);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            cancellationToken.ThrowIfCancellationRequested();
            return (null, remaining <= probeLimit);
        }
        var both = Task.WhenAll(output, error);
        if (await Task.WhenAny(both, Task.Delay(DrainGrace, CancellationToken.None)).ConfigureAwait(false) != both || process.ExitCode != 0)
            return (null, false);

        // A single -getProperty prints the bare value.
        var declared = (await output.ConfigureAwait(false)).Trim()
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var buildable = declared.Where(f => ProjectExclusionClassifier.UnavailablePlatform(f) is null).ToList();
        return (buildable.Count > 0 && buildable.Count < declared.Length ? string.Join(';', buildable) : null, false);
    }

    private sealed record UnionProject(string ProjectPath, string SolutionPath, IReadOnlyList<string>? Extra = null);

    // Static-graph failures per solution for one fingerprint (issue #349): Known are loaded from the restore state,
    // Added are the ones this job learned. Keys are checkout-relative solution paths with '/' separators.
    private sealed class RestoreMemo(HashSet<string> known)
    {
        private readonly object _gate = new();
        public HashSet<string> Known { get; } = known;
        public HashSet<string> Added { get; } = new(StringComparer.Ordinal);
        public bool Knows(string solution) { lock (_gate) return Known.Contains(solution); }
        public void Add(string solution) { lock (_gate) Added.Add(solution); }
    }

    internal ProcessStartInfo CreateStartInfo(
        string solution, IReadOnlyList<KeyValuePair<string, string>> globals, IReadOnlyDictionary<string, string>? environment = null,
        bool staticGraph = false)
        => CreateStartInfo(Arguments(solution, globals, staticGraph, MaxNodes), Path.GetDirectoryName(Path.GetFullPath(solution)) ?? Environment.CurrentDirectory,
            environment);

    // The union traversal and the per-project retry both run this invocation.
    internal ProcessStartInfo CreateUnionStartInfo(string project, string checkoutDir, IReadOnlyDictionary<string, string> environment)
        => CreateStartInfo(
            ["msbuild", project, "-target:RestoreUnion", "-nologo", "-verbosity:minimal", "--disable-build-servers", "-nodeReuse:false",
                MaxCpuCountArgument(MaxNodes)],
            checkoutDir, environment);

    private static string MaxCpuCountArgument(int maxNodes) => $"-maxcpucount:{maxNodes}";

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

    /// <summary>
    /// The <c>dotnet restore</c> arguments for one solution, with the selection's <see cref="EvaluationGlobalProperties"/>
    /// and, when <paramref name="staticGraph"/> is set, static-graph evaluation (issue #272). A <paramref name="maxNodes"/>
    /// caps MSBuild's worker processes with <c>-maxcpucount:N</c> (issue #325); null leaves MSBuild's one per CPU.
    /// </summary>
    internal static IReadOnlyList<string> Arguments(
        string solution, IReadOnlyList<KeyValuePair<string, string>> globals, bool staticGraph = false, int? maxNodes = null) =>
    [
        "restore", solution,
        "-p:DesignTimeBuild=true",
        .. globals.Select(p => $"-p:{p.Key}={p.Value}"),
        .. staticGraph ? new[] { "-p:RestoreUseStaticGraphEvaluation=true" } : [],
        "--ignore-failed-sources",
        "--disable-build-servers",
        "-nodeReuse:false",
        .. maxNodes is { } n ? new[] { MaxCpuCountArgument(n) } : [],
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
