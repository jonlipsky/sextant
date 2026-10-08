using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Sextant.Core;

namespace Sextant.Indexer;

public static class SolutionLoader
{
    /// <summary>
    /// Backward-compatible entry point: loads the solution and returns its (possibly partial)
    /// <see cref="Solution"/>. Any project that could not be loaded is isolated and reported through
    /// <paramref name="onDiagnostic"/> rather than aborting the whole load (issue #90). Callers that
    /// need the structured list of skipped projects should use <see cref="LoadSolutionResilientlyAsync"/>.
    /// </summary>
    public static async Task<Solution> LoadSolutionAsync(
        string solutionPath,
        Action<string>? onDiagnostic = null,
        CancellationToken cancellationToken = default)
    {
        var result = await LoadSolutionResilientlyAsync(solutionPath, onDiagnostic, cancellationToken);
        return result.Solution;
    }

    /// <summary>
    /// Loads the solution with per-project fault isolation. The fast path is the standard parallel
    /// <see cref="MSBuildWorkspace.OpenSolutionAsync"/>; if that aborts the whole load (issue #90 — a
    /// legacy/non-SDK project crashing the out-of-process MSBuild BuildHost), it retries project-by-project
    /// so a single unloadable project is SKIPPED WITH A DIAGNOSTIC instead of yielding an empty index.
    /// On either path, projects that were declared in the solution but did not load are returned in
    /// <see cref="SolutionLoadResult.SkippedProjects"/>.
    /// </summary>
    public static async Task<SolutionLoadResult> LoadSolutionResilientlyAsync(
        string solutionPath,
        Action<string>? onDiagnostic = null,
        CancellationToken cancellationToken = default)
    {
        var failures = new ConcurrentQueue<LoadFailure>();
        var workspace = MSBuildWorkspace.Create();
        RegisterFailureSink(workspace, failures, onDiagnostic);

        Solution solution;
        try
        {
            solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            // The whole-solution load aborted: a single project's evaluation threw hard enough to fault
            // OpenSolutionAsync (issue #90). Tear down this workspace (its BuildHost may be in a bad
            // state) and retry project-by-project so the loadable projects still index. A process-fatal
            // exception (out-of-memory, ...) is NOT one bad project — the filter lets it propagate.
            onDiagnostic?.Invoke(
                $"Solution load aborted ({ex.GetType().Name}: {ex.Message}); retrying project-by-project " +
                "to isolate the failing project(s).");
            workspace.Dispose();
            return await LoadPerProjectAsync(solutionPath, ex, onDiagnostic, cancellationToken);
        }

        var declared = SolutionProjectEnumerator.Enumerate(solutionPath);
        var reconciled = ReconcileLoads(declared, solution, failures, [], onDiagnostic);
        var skipped = reconciled.SkippedProjects;

        if (declared.Count > 0 && AllDeclaredSkipped(declared, solution, reconciled))
        {
            // OpenSolutionAsync did not throw, but EVERY declared project failed to produce content (all
            // empty stubs named by failures). The resulting index would be empty — fail LOUD rather than
            // return exit 0 behind an all-projects-PARTIAL warning, mirroring the fallback guard. This is
            // the whole point of issue #90: never silently publish an empty index.
            workspace.Dispose();
            throw new InvalidOperationException(
                $"Solution '{Path.GetFileName(solutionPath)}' loaded but all {declared.Count} declared " +
                "project(s) failed to load; the index would be empty. See the load diagnostics above for " +
                "the per-project failures.");
        }

        return reconciled.ToResult(TransitiveProjectReferences.Close(solution, onDiagnostic));
    }

    /// <summary>
    /// Loads a multi-solution project union (issue #268): <see cref="UnionPartition.OnePass"/> with ONE
    /// <see cref="MSBuildWorkspace.OpenSolutionAsync"/> over a solution generated in
    /// <see cref="UnionPartition.Directory"/> (deleted once the open has read it), so one BuildHost evaluates them,
    /// instead of a BuildHost started and stopped per <see cref="MSBuildWorkspace.OpenProjectAsync"/>; then each of
    /// <see cref="UnionPartition.Individually"/> opened on its own into the same workspace, as before. Failures are
    /// reconciled per project exactly as on the per-project path (keyed by project path). The whole union is loaded
    /// project by project instead (<see cref="LoadProjectsResilientlyAsync"/>) when the one-pass open aborts (issue
    /// #90), when <paramref name="deadline"/> passes before it finishes, or when it pulled in, through a project
    /// reference, a project that must be evaluated on its own.
    /// </summary>
    internal static async Task<SolutionLoadResult> LoadUnionInOnePassAsync(
        IReadOnlyList<string> projectPaths,
        UnionPartition partition,
        Action<string>? onDiagnostic = null,
        IndexDeadline? deadline = null,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var generated = UnionSolutionWriter.Write(partition.Directory!, partition.OnePass);
        onProgress?.Invoke($"Loading {partition.OnePass.Count} project(s) in one pass" +
            (partition.Individually.Count > 0 ? $", then {partition.Individually.Count} individually" : string.Empty));
        var failures = new ConcurrentQueue<LoadFailure>();
        var workspace = MSBuildWorkspace.Create();
        RegisterFailureSink(workspace, failures, onDiagnostic);

        using var atDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (deadline is not null)
            atDeadline.CancelAfter(Max(deadline.At - deadline.Clock.GetUtcNow(), TimeSpan.Zero));
        string? fallback;
        try
        {
            try
            {
                await workspace.OpenSolutionAsync(generated, cancellationToken: atDeadline.Token);
            }
            finally
            {
                // Nothing reads the generated solution after the open, and it must not stay in the checkout.
                File.Delete(generated);
            }
            var opened = new MSBuildWorkspaceProjectLoader(workspace);
            fallback = partition.Individually.FirstOrDefault(opened.IsLoaded) is { } pulledIn
                ? $"the one-pass load pulled in '{Path.GetFileName(pulledIn)}', which must be evaluated on its own"
                : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            fallback = "the load deadline passed during the one-pass union load";
        }
        catch (OperationCanceledException)
        {
            workspace.Dispose();
            throw;
        }
        catch (Exception ex) when (!IsFatal(ex))
        {
            // Issue #90: one project's evaluation can fault the whole open. Isolate it project by project.
            fallback = $"the one-pass union load aborted ({ex.GetType().Name}: {ex.Message})";
        }
        if (fallback is not null)
        {
            workspace.Dispose();
            onDiagnostic?.Invoke($"Loading the solution union project by project: {fallback}.");
            return await LoadProjectsResilientlyAsync(projectPaths, onDiagnostic, deadline, onProgress, cancellationToken);
        }

        Solution solution;
        List<SkippedProject> thrownSkipped = [];
        List<string> deferred = [];
        var openTimings = new List<ProjectTiming>();
        try
        {
            var loader = new MSBuildWorkspaceProjectLoader(workspace);
            solution = loader.CurrentSolution;
            if (partition.Individually.Count > 0)
                (solution, thrownSkipped, deferred) = await LoadProjectsIndividuallyAsync(
                    partition.Individually, loader, onDiagnostic, deadline, onProgress, cancellationToken, openTimings);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }

        var deferredSet = new HashSet<string>(deferred.Select(NormalizePath), StringComparer.OrdinalIgnoreCase);
        var attempted = deferred.Count == 0
            ? projectPaths
            : projectPaths.Where(p => !deferredSet.Contains(NormalizePath(p))).ToList();
        var reconciled = ReconcileLoads(attempted, solution, failures, thrownSkipped, onDiagnostic);
        return reconciled.ToResult(TransitiveProjectReferences.Close(
            WithoutSkippedStubs(solution, reconciled.SkippedProjects), onDiagnostic)) with
        {
            DeferredProjects = deferred,
            LoadTimings = openTimings
        };
    }

    // A failed project stays in a whole-solution load as an empty stub (issue #90), where a failed OpenProjectAsync
    // adds nothing. Drop each stub reconciled as skipped that no loaded project references, so the one-pass union
    // indexes the same projects the per-project union did (a referenced stub stays, keeping that reference).
    private static Solution WithoutSkippedStubs(Solution solution, IReadOnlyList<SkippedProject> skipped)
    {
        var skippedPaths = skipped.Select(s => NormalizePath(s.ProjectPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stubs = solution.Projects
            .Where(p => p.FilePath != null && !p.Documents.Any() && skippedPaths.Contains(NormalizePath(p.FilePath)))
            .Select(p => p.Id)
            .ToList();
        foreach (var stub in stubs)
            if (!solution.Projects.Any(p => p.ProjectReferences.Any(r => r.ProjectId == stub)))
                solution = solution.RemoveProject(stub);
        return solution;
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    /// <summary>
    /// Loads a UNION of individually-named project paths (across one or more solutions) into ONE workspace
    /// with the same per-project fault isolation + skipped-project reconciliation as the solution loader
    /// (issue #109 multi-solution aggregation). Projects already pulled in transitively by an earlier
    /// project's reference graph are opened once (de-duplicated by the caller and again via
    /// <see cref="IWorkspaceProjectLoader.IsLoaded"/>). The workspace is intentionally NOT disposed on the
    /// success path so the returned <see cref="Solution"/> stays usable (mirroring
    /// <see cref="LoadSolutionResilientlyAsync"/>). When <paramref name="deadline"/> passes, the projects not
    /// yet opened are returned in <see cref="SolutionLoadResult.DeferredProjects"/> instead of being loaded.
    /// </summary>
    internal static async Task<SolutionLoadResult> LoadProjectsResilientlyAsync(
        IReadOnlyList<string> projectPaths,
        Action<string>? onDiagnostic = null,
        IndexDeadline? deadline = null,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var failures = new ConcurrentQueue<LoadFailure>();
        var workspace = MSBuildWorkspace.Create();
        RegisterFailureSink(workspace, failures, onDiagnostic);

        Solution solution;
        List<SkippedProject> thrownSkipped;
        List<string> deferred;
        var openTimings = new List<ProjectTiming>();
        try
        {
            var loader = new MSBuildWorkspaceProjectLoader(workspace);
            (solution, thrownSkipped, deferred) = await LoadProjectsIndividuallyAsync(
                projectPaths, loader, onDiagnostic, deadline, onProgress, cancellationToken, openTimings);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }

        // A deferred project was never opened, so it is neither loaded nor skipped: reconcile only the rest.
        var deferredSet = new HashSet<string>(deferred.Select(NormalizePath), StringComparer.OrdinalIgnoreCase);
        var attempted = deferred.Count == 0
            ? projectPaths
            : projectPaths.Where(p => !deferredSet.Contains(NormalizePath(p))).ToList();
        var reconciled = ReconcileLoads(attempted, solution, failures, thrownSkipped, onDiagnostic);
        return reconciled.ToResult(TransitiveProjectReferences.Close(solution, onDiagnostic)) with
        {
            DeferredProjects = deferred,
            LoadTimings = openTimings,
            LoadedProjectByProject = true
        };
    }

    /// <summary>
    /// Opens each declared project individually, isolating a per-project load failure. Factored out
    /// (over the <see cref="IWorkspaceProjectLoader"/> seam) so the isolation logic is unit-testable
    /// without a real crashing MSBuild toolchain. Once <paramref name="deadline"/> has passed, every project
    /// not yet in the workspace is returned as deferred instead of being opened; the first project is always
    /// attempted, so a load that starts late still makes progress.
    /// </summary>
    internal static async Task<(Solution Solution, List<SkippedProject> Skipped, List<string> Deferred)> LoadProjectsIndividuallyAsync(
        IReadOnlyList<string> projectPaths,
        IWorkspaceProjectLoader loader,
        Action<string>? onDiagnostic,
        IndexDeadline? deadline,
        Action<string>? onProgress,
        CancellationToken cancellationToken,
        ICollection<ProjectTiming>? openTimings = null)
    {
        var skipped = new List<SkippedProject>();
        var deferred = new List<string>();
        var attempted = 0;
        for (var i = 0; i < projectPaths.Count; i++)
        {
            var projectPath = projectPaths[i];
            cancellationToken.ThrowIfCancellationRequested();

            // Skip a project already pulled into the workspace transitively by an earlier project's
            // reference graph — re-opening it is redundant work and can fault.
            if (loader.IsLoaded(projectPath))
                continue;

            if (attempted > 0 && deadline is { HasPassed: true })
            {
                for (var j = i; j < projectPaths.Count; j++)
                    if (!loader.IsLoaded(projectPaths[j]))
                        deferred.Add(projectPaths[j]);
                onDiagnostic?.Invoke(
                    $"The load deadline passed after opening {attempted} project(s); {deferred.Count} of the " +
                    $"{projectPaths.Count} declared project(s) were not loaded.");
                break;
            }

            attempted++;
            onProgress?.Invoke($"Loading project {i + 1}/{projectPaths.Count}: {Path.GetFileName(projectPath)}");
            // Issue #267: one open evaluates the project and every reference it pulls into the workspace.
            var projectsBefore = openTimings == null ? 0 : loader.CurrentSolution.ProjectIds.Count;
            var openWatch = Stopwatch.StartNew();
            try
            {
                await loader.OpenProjectAsync(projectPath, cancellationToken);
                openTimings?.Add(new ProjectTiming
                {
                    Phase = IndexPhaseNames.Load,
                    Project = Path.GetFileName(projectPath),
                    WallMs = openWatch.ElapsedMilliseconds,
                    ProjectsAdded = loader.CurrentSolution.ProjectIds.Count - projectsBefore
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (!IsFatal(ex))
            {
                var reason = Describe(ex);
                skipped.Add(new SkippedProject(projectPath, reason));
                onDiagnostic?.Invoke($"Skipped project '{Path.GetFileName(projectPath)}': {reason}");
            }
        }

        return (loader.CurrentSolution, skipped, deferred);
    }

    private static async Task<SolutionLoadResult> LoadPerProjectAsync(
        string solutionPath, Exception originalException, Action<string>? onDiagnostic,
        CancellationToken cancellationToken)
    {
        var projectPaths = SolutionProjectEnumerator.Enumerate(solutionPath);
        if (projectPaths.Count == 0)
        {
            // We could not enumerate any projects to isolate (unknown/unreadable solution format), so we
            // cannot do better than surfacing the ORIGINAL whole-solution failure to the caller — never
            // swallow it into an empty "successful" index.
            onDiagnostic?.Invoke(
                "Could not enumerate any project to isolate; surfacing the original solution load failure.");
            ExceptionDispatchInfo.Throw(originalException);
        }

        var failures = new ConcurrentQueue<LoadFailure>();
        var workspace = MSBuildWorkspace.Create();
        RegisterFailureSink(workspace, failures, onDiagnostic);

        Solution loadedSolution;
        List<SkippedProject> thrownSkipped;
        try
        {
            var loader = new MSBuildWorkspaceProjectLoader(workspace);
            (loadedSolution, thrownSkipped, _) =
                await LoadProjectsIndividuallyAsync(
                    projectPaths, loader, onDiagnostic, deadline: null, onProgress: null, cancellationToken);
        }
        catch
        {
            // The per-project loop faulted (e.g. cancellation) before producing a solution — dispose the
            // workspace (it owns an out-of-process BuildHost) before propagating so we do not leak it.
            workspace.Dispose();
            throw;
        }

        // Reconcile first, then decide whether ANY declared project actually loaded. Presence in the
        // solution is not success — MSBuild keeps empty stubs for failed projects (issue #90).
        var reconciled = ReconcileLoads(projectPaths, loadedSolution, failures, thrownSkipped, onDiagnostic);
        if (AllDeclaredSkipped(projectPaths, loadedSolution, reconciled))
        {
            // Every declared project was skipped: this was not one bad project but a global/environment
            // failure (broken toolchain, unreadable solution, ...). Fail LOUD with the original cause
            // rather than publishing an empty index behind a partial-load warning — the very outcome
            // issue #90 set out to prevent.
            workspace.Dispose();
            onDiagnostic?.Invoke(
                "Per-project fallback loaded zero usable projects; treating as a fatal solution load failure.");
            ExceptionDispatchInfo.Throw(originalException);
        }

        return reconciled.ToResult(TransitiveProjectReferences.Close(loadedSolution, onDiagnostic));
    }

    internal sealed record LoadReconciliation(
        IReadOnlyList<SkippedProject> SkippedProjects,
        IReadOnlyList<DegradedProject> DegradedProjects,
        int UnattributedFailureCount)
    {
        internal SolutionLoadResult ToResult(Solution solution) => new(solution, SkippedProjects)
        {
            DegradedProjects = DegradedProjects,
            UnattributedFailureCount = UnattributedFailureCount
        };
    }

    private static bool AllDeclaredSkipped(IReadOnlyList<string> declared, Solution solution, LoadReconciliation result) =>
        !solution.Projects.Any(p => p.Documents.Any()) && result.DegradedProjects.Count == 0 && declared.All(p =>
            result.SkippedProjects.Any(s => string.Equals(
                NormalizePath(s.ProjectPath), NormalizePath(p), StringComparison.OrdinalIgnoreCase)));

    // A typed ProjectId pins a failure to one TFM. A path-only failure cannot prove any of that
    // file's variants healthy, even if MSBuild returned documents; retain those documents as degraded.
    internal static LoadReconciliation ReconcileLoads(
        IReadOnlyList<string> declared, Solution solution, IReadOnlyCollection<LoadFailure> failures,
        List<SkippedProject> knownSkipped, Action<string>? onDiagnostic)
    {
        var loaded = LoadedProjectsByPath(solution);
        var ambiguousFileNames = AmbiguousFileNames(declared);
        // A diagnostic may fire before its project is in CurrentSolution, so resolve any typed ProjectId
        // to a path against the FINAL solution here rather than only at event time.
        var resolvedFailures = ResolveFailurePaths(failures, solution);
        var attributed = new HashSet<LoadFailure>();
        var result = new List<SkippedProject>(knownSkipped);
        var degraded = new List<DegradedProject>();
        var seen = new HashSet<string>(
            result.Select(s => NormalizePath(s.ProjectPath)), StringComparer.OrdinalIgnoreCase);

        foreach (var projectPath in declared)
        {
            var wasThrown = !seen.Add(NormalizePath(projectPath));
            var projectFailures = resolvedFailures.Where(f =>
                AttributeFailure(projectPath, [f], ambiguousFileNames, attributed) != null).ToList();
            var present = loaded.TryGetValue(NormalizePath(projectPath), out var variants);

            if (!present)
            {
                if (!wasThrown)
                    result.Add(new SkippedProject(projectPath,
                        projectFailures.FirstOrDefault()?.Message ?? "project was declared in the solution but did not load"));
                continue;
            }

            var thrown = knownSkipped.FirstOrDefault(s => string.Equals(
                NormalizePath(s.ProjectPath), NormalizePath(projectPath), StringComparison.OrdinalIgnoreCase));
            if (wasThrown)
                result.RemoveAll(s => string.Equals(
                    NormalizePath(s.ProjectPath), NormalizePath(projectPath), StringComparison.OrdinalIgnoreCase));
            var expected = ReadExpectedFrameworks(projectPath);
            // A failure that only replays a restore warning is not a load failure, but only for a variant that
            // actually loaded documents: an empty stub (presence is not success) keeps every failure as its skip.
            var restoreWarnings = projectFailures.Count > 0 && variants!.Any(v => v.Documents.Any())
                ? ReadRestoreWarnings(projectPath)
                : [];
            var replayed = projectFailures.Where(f => IsReplayedRestoreWarning(f.Message, restoreWarnings)).ToHashSet();
            foreach (var warning in replayed)
                onDiagnostic?.Invoke(
                    $"Restore warning replayed by the load of '{Path.GetFileName(projectPath)}' (not a load failure): {warning.Message}");
            foreach (var variant in variants!)
            {
                var candidates = variant.Documents.Any()
                    ? projectFailures.Where(f => !replayed.Contains(f)).ToList()
                    : projectFailures;
                var failure = candidates.FirstOrDefault(f => f.ProjectId == variant.Id)
                    ?? candidates.FirstOrDefault(f => f.ProjectId == null);
                var reason = failure?.Message ?? thrown?.Reason;
                if (reason == null)
                    continue;
                var tfm = VariantFramework(variant, expected);
                if (variant.Documents.Any())
                    degraded.Add(new DegradedProject(projectPath, tfm, reason));
                else
                    result.Add(new SkippedProject(projectPath, reason) { TargetFramework = tfm });
            }
            foreach (var failure in projectFailures.Where(f =>
                         f.ProjectId != null && !variants.Any(v => v.Id == f.ProjectId)))
                result.Add(new SkippedProject(projectPath, failure.Message));

            // Only unconditional literal declarations establish an expected set. Do not interpret
            // conditional properties or expand MSBuild expressions with a second, divergent evaluator.
            foreach (var tfm in expected)
                if (!variants.Any(v => string.Equals(VariantFramework(v, expected), tfm, StringComparison.OrdinalIgnoreCase)))
                    result.Add(new SkippedProject(projectPath, "declared target framework did not load")
                    {
                        TargetFramework = tfm
                    });
        }

        ReportUnattributedFailures(resolvedFailures, attributed, onDiagnostic);
        var skips = result.DistinctBy(s => (NormalizePath(s.ProjectPath), s.TargetFramework)).ToList();
        var degradedVersions = degraded.DistinctBy(s => (NormalizePath(s.ProjectPath), s.TargetFramework)).ToList();
        foreach (var skip in skips)
            onDiagnostic?.Invoke($"Skipped project '{skip.ProjectName}' ({skip.TargetFramework ?? "unknown TFM"}): {skip.Reason}");
        foreach (var project in degradedVersions)
            onDiagnostic?.Invoke($"Degraded project '{Path.GetFileName(project.ProjectPath)}' ({project.TargetFramework ?? "unknown TFM"}): {project.Reason}");
        return new LoadReconciliation(skips, degradedVersions, resolvedFailures.Count(f => !attributed.Contains(f)));
    }

    // MSBuildWorkspace reports an MSBuild WARNING raised during the design-time load as a Failure diagnostic
    // ("Msbuild failed when processing the file '<project>' with message: <text>"). The load replays every
    // warning NuGet recorded in the project's assets file during restore (for example an authenticated feed's
    // first 401 before its credential provider answered, or a vulnerability notice), so a project whose
    // packages restored and whose code loaded was reported degraded. Such a diagnostic is reclassified ONLY when
    // its text ends with the exact message of a Warning-only entry in that project's own assets file, and only
    // for a variant that loaded documents; anything else, an empty stub and a project that did not load keep it.
    internal static bool IsReplayedRestoreWarning(string message, IReadOnlyCollection<string> restoreWarnings)
    {
        var text = message.TrimEnd();
        return restoreWarnings.Any(warning => text.EndsWith(": " + warning, StringComparison.Ordinal));
    }

    // The messages recorded ONLY at Warning level in '<project dir>/obj/project.assets.json' (the default restore
    // output path; a repository that moves it keeps today's behavior: no reclassification).
    internal static IReadOnlyCollection<string> ReadRestoreWarnings(string projectPath)
    {
        var assets = Path.Combine(Path.GetDirectoryName(projectPath) ?? ".", "obj", "project.assets.json");
        try
        {
            if (!File.Exists(assets))
                return [];
            using var stream = File.OpenRead(assets);
            using var document = System.Text.Json.JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("logs", out var logs) || logs.ValueKind != System.Text.Json.JsonValueKind.Array)
                return [];
            var warnings = new HashSet<string>(StringComparer.Ordinal);
            var others = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in logs.EnumerateArray())
            {
                if (entry.ValueKind == System.Text.Json.JsonValueKind.Object
                    && entry.TryGetProperty("message", out var text) && text.ValueKind == System.Text.Json.JsonValueKind.String
                    && text.GetString()?.Trim() is { Length: > 0 } message)
                {
                    var isWarning = entry.TryGetProperty("level", out var level)
                        && level.ValueKind == System.Text.Json.JsonValueKind.String
                        && string.Equals(level.GetString(), "Warning", StringComparison.OrdinalIgnoreCase);
                    (isWarning ? warnings : others).Add(message);
                }
            }
            // A message also recorded at another level (an error under another target graph) is never reclassified.
            warnings.ExceptWith(others);
            return warnings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private static string? VariantFramework(Project project, IReadOnlyList<string> expected)
    {
        var suffix = ProjectIdentityFactory.ExtractParentheticalSuffix(project.Name);
        if (suffix != null && ProjectIdentityFactory.LooksLikeTfm(suffix))
            return suffix;
        return expected.Count == 1 ? expected[0] : null;
    }

    private static IReadOnlyList<string> ReadExpectedFrameworks(string? path)
    {
        if (path == null) return [];
        try
        {
            var xml = System.Xml.Linq.XDocument.Load(path);
            var properties = xml.Descendants().Where(e =>
                e.Name.LocalName is "TargetFramework" or "TargetFrameworks").ToList();
            if (properties.Any(e => e.AncestorsAndSelf().Any(a => a.Attribute("Condition") != null)
                                    || e.Value.Contains("$(", StringComparison.Ordinal)))
                return [];
            return properties.SelectMany(e => e.Value.Split(';',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Where(ProjectIdentityFactory.LooksLikeTfm).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return []; // Failure diagnostics, not a static XML guess, remain authoritative.
        }
    }

    // File names that occur more than once across the declared projects — a failure diagnostic that only
    // mentions such a name cannot be attributed to a single project without ambiguity, so we refuse the
    // file-name fallback for them (a monorepo routinely has several 'Build.csproj'/'Utils.csproj').
    private static HashSet<string> AmbiguousFileNames(IReadOnlyList<string> declared)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in declared)
        {
            var name = Path.GetFileName(path);
            counts[name] = counts.TryGetValue(name, out var c) ? c + 1 : 1;
        }
        return counts.Where(kv => kv.Value > 1)
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    // Fills in each failure's owning-project path from its typed ProjectId when the path was not already
    // resolvable at event time (the project may only appear in the solution once loading finishes).
    private static List<LoadFailure> ResolveFailurePaths(
        IReadOnlyCollection<LoadFailure> failures, Solution solution)
    {
        var resolved = new List<LoadFailure>(failures.Count);
        foreach (var failure in failures)
        {
            if (failure.ProjectPath == null && failure.ProjectId != null)
            {
                var filePath = solution.GetProject(failure.ProjectId)?.FilePath;
                resolved.Add(filePath != null ? failure with { ProjectPath = NormalizePath(filePath) } : failure);
            }
            else
            {
                resolved.Add(failure);
            }
        }
        return resolved;
    }

    // Surfaces the honest completeness caveat: FAILURE diagnostics that could not be tied to any declared
    // project (e.g. an ambiguous shared file name we refused to guess at) are still reported raw, but the
    // structured skipped list may be incomplete — say so rather than silently dropping them.
    private static void ReportUnattributedFailures(
        IReadOnlyList<LoadFailure> failures, HashSet<LoadFailure> attributed, Action<string>? onDiagnostic)
    {
        var unattributed = failures.Count(f => !attributed.Contains(f));
        if (unattributed > 0)
            onDiagnostic?.Invoke(
                $"Note: {unattributed} load-failure diagnostic(s) could not be attributed to a specific " +
                "declared project; the skipped-project list may be incomplete (see the raw diagnostics above).");
    }

    private static Dictionary<string, List<Project>> LoadedProjectsByPath(Solution solution)
    {
        var map = new Dictionary<string, List<Project>>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in solution.Projects)
        {
            if (project.FilePath == null)
                continue;
            var key = NormalizePath(project.FilePath);
            if (!map.TryGetValue(key, out var variants))
                map[key] = variants = new List<Project>();
            variants.Add(project);
        }
        return map;
    }

    // Attributes a FAILURE diagnostic to a declared project, most reliable signal first:
    //   1. the diagnostic's typed owning-project path (from a ProjectDiagnostic's ProjectId) matches;
    //   2. an UNTYPED diagnostic message quotes the project's absolute path;
    //   3. an UNTYPED message mentions the project's file name AND that name is unique among declared
    //      projects.
    // Passes 2 and 3 deliberately ignore failures that already carry a resolved owning project: such a
    // diagnostic is attributable ONLY to its own project (pass 1), never to a sibling it merely mentions
    // (e.g. project A's failure naming a referenced B.csproj must not mark a valid empty B as skipped).
    // Records the chosen failure in <paramref name="attributed"/> and returns it, or null if none matched.
    private static LoadFailure? AttributeFailure(
        string projectPath, IReadOnlyList<LoadFailure> failures, HashSet<string> ambiguousFileNames,
        HashSet<LoadFailure> attributed)
    {
        var fullPath = NormalizePath(projectPath);
        var fileName = Path.GetFileName(projectPath);

        foreach (var failure in failures)
            if (failure.ProjectPath != null &&
                string.Equals(failure.ProjectPath, fullPath, StringComparison.OrdinalIgnoreCase))
                return Attribute(failure, attributed);

        foreach (var failure in failures)
            if (failure.ProjectPath == null && failure.ProjectId == null &&
                failure.Message.Contains(fullPath, StringComparison.OrdinalIgnoreCase))
                return Attribute(failure, attributed);

        if (!ambiguousFileNames.Contains(fileName))
            foreach (var failure in failures)
                if (failure.ProjectPath == null && failure.ProjectId == null &&
                    failure.Message.Contains(fileName, StringComparison.OrdinalIgnoreCase))
                    return Attribute(failure, attributed);

        return null;
    }

    private static LoadFailure Attribute(LoadFailure failure, HashSet<LoadFailure> attributed)
    {
        attributed.Add(failure);
        return failure;
    }

    private static void RegisterFailureSink(
        MSBuildWorkspace workspace, ConcurrentQueue<LoadFailure> failures, Action<string>? onDiagnostic)
    {
        workspace.RegisterWorkspaceFailedHandler(e =>
        {
            if (e.Diagnostic.Kind != WorkspaceDiagnosticKind.Failure)
                return;

            // Prefer the diagnostic's typed owning project (when Roslyn reports a ProjectDiagnostic) so
            // attribution keys on project identity, not brittle message-substring matching. The path may
            // not be resolvable yet (the project can be absent from CurrentSolution at event time), so we
            // also carry the ProjectId for a late resolve in ReconcileLoads.
            ProjectId? projectId = null;
            string? projectPath = null;
            if (e.Diagnostic is ProjectDiagnostic projectDiagnostic)
            {
                projectId = projectDiagnostic.ProjectId;
                var filePath = workspace.CurrentSolution.GetProject(projectId)?.FilePath;
                if (filePath != null)
                    projectPath = NormalizePath(filePath);
            }

            failures.Enqueue(new LoadFailure(projectId, projectPath, e.Diagnostic.Message));
            // Forward the raw message so downstream consumers (e.g. SolutionEvaluationProbe) still see it.
            onDiagnostic?.Invoke(e.Diagnostic.Message);
        });
    }

    // A captured WorkspaceFailed FAILURE diagnostic: the owning project's typed id and/or absolute path
    // when Roslyn reported it (else null), plus the raw human-readable message.
    internal sealed record LoadFailure(ProjectId? ProjectId, string? ProjectPath, string Message);

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    // A process-fatal exception is never "one bad project" — it must abort the whole load rather than be
    // masked as a per-project skip. Used as an exception filter so these propagate untouched.
    private static bool IsFatal(Exception ex) =>
        ex is OutOfMemoryException or InsufficientMemoryException or AccessViolationException;

    private static string NormalizePath(string path) => Path.GetFullPath(path);

    /// <summary>Seam over the accumulating workspace so the per-project isolation loop can be tested
    /// with a fake loader (one project deliberately throwing) — no real MSBuild required.</summary>
    internal interface IWorkspaceProjectLoader
    {
        Solution CurrentSolution { get; }
        bool IsLoaded(string projectPath);
        Task OpenProjectAsync(string projectPath, CancellationToken cancellationToken);
    }

    private sealed class MSBuildWorkspaceProjectLoader(MSBuildWorkspace workspace) : IWorkspaceProjectLoader
    {
        public Solution CurrentSolution => workspace.CurrentSolution;

        public bool IsLoaded(string projectPath) =>
            workspace.CurrentSolution.Projects.Any(
                p => p.FilePath != null &&
                     string.Equals(NormalizePath(p.FilePath), NormalizePath(projectPath), StringComparison.OrdinalIgnoreCase));

        public Task OpenProjectAsync(string projectPath, CancellationToken cancellationToken) =>
            workspace.OpenProjectAsync(projectPath, cancellationToken: cancellationToken);
    }
}
