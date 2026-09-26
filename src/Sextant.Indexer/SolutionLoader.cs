using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

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
        var skipped = ReconcileSkipped(declared, solution, failures, [], onDiagnostic);

        if (declared.Count > 0 && skipped.Count == declared.Count)
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

        return new SolutionLoadResult(solution, skipped);
    }

    /// <summary>
    /// Loads a UNION of individually-named project paths (across one or more solutions) into ONE workspace
    /// with the same per-project fault isolation + skipped-project reconciliation as the solution loader
    /// (issue #109 multi-solution aggregation). Projects already pulled in transitively by an earlier
    /// project's reference graph are opened once (de-duplicated by the caller and again via
    /// <see cref="IWorkspaceProjectLoader.IsLoaded"/>). The workspace is intentionally NOT disposed on the
    /// success path so the returned <see cref="Solution"/> stays usable (mirroring
    /// <see cref="LoadSolutionResilientlyAsync"/>).
    /// </summary>
    internal static async Task<SolutionLoadResult> LoadProjectsResilientlyAsync(
        IReadOnlyList<string> projectPaths,
        Action<string>? onDiagnostic = null,
        CancellationToken cancellationToken = default)
    {
        var failures = new ConcurrentQueue<LoadFailure>();
        var workspace = MSBuildWorkspace.Create();
        RegisterFailureSink(workspace, failures, onDiagnostic);

        Solution solution;
        List<SkippedProject> thrownSkipped;
        try
        {
            var loader = new MSBuildWorkspaceProjectLoader(workspace);
            (solution, thrownSkipped) =
                await LoadProjectsIndividuallyAsync(projectPaths, loader, onDiagnostic, cancellationToken);
        }
        catch
        {
            workspace.Dispose();
            throw;
        }

        var skipped = ReconcileSkipped(projectPaths, solution, failures, thrownSkipped, onDiagnostic);
        return new SolutionLoadResult(solution, skipped);
    }

    /// <summary>
    /// Opens each declared project individually, isolating a per-project load failure. Factored out
    /// (over the <see cref="IWorkspaceProjectLoader"/> seam) so the isolation logic is unit-testable
    /// without a real crashing MSBuild toolchain.
    /// </summary>
    internal static async Task<(Solution Solution, List<SkippedProject> Skipped)> LoadProjectsIndividuallyAsync(
        IReadOnlyList<string> projectPaths,
        IWorkspaceProjectLoader loader,
        Action<string>? onDiagnostic,
        CancellationToken cancellationToken)
    {
        var skipped = new List<SkippedProject>();
        foreach (var projectPath in projectPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Skip a project already pulled into the workspace transitively by an earlier project's
            // reference graph — re-opening it is redundant work and can fault.
            if (loader.IsLoaded(projectPath))
                continue;

            try
            {
                await loader.OpenProjectAsync(projectPath, cancellationToken);
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

        return (loader.CurrentSolution, skipped);
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
            (loadedSolution, thrownSkipped) =
                await LoadProjectsIndividuallyAsync(projectPaths, loader, onDiagnostic, cancellationToken);
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
        var skipped = ReconcileSkipped(projectPaths, loadedSolution, failures, thrownSkipped, onDiagnostic);

        var skippedPaths = new HashSet<string>(
            skipped.Select(s => NormalizePath(s.ProjectPath)), StringComparer.OrdinalIgnoreCase);
        if (projectPaths.All(p => skippedPaths.Contains(NormalizePath(p))))
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

        return new SolutionLoadResult(loadedSolution, skipped);
    }

    // Reconciles the DECLARED projects against what actually loaded to produce the skipped set. A project
    // is skipped when it threw during open (<paramref name="knownSkipped"/>), when it is missing from the
    // solution entirely, or when MSBuild retained it as an empty stub after a load failure (present with
    // zero documents AND named by a WorkspaceFailed FAILURE diagnostic). Requiring the empty-stub shape
    // avoids a false positive where a project that loaded real content is merely mentioned by an unrelated
    // failure diagnostic. Internal so the reconciliation logic can be unit-tested with a synthetic
    // solution + failure set (no real MSBuild required).
    internal static List<SkippedProject> ReconcileSkipped(
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
        var seen = new HashSet<string>(
            result.Select(s => NormalizePath(s.ProjectPath)), StringComparer.OrdinalIgnoreCase);

        foreach (var projectPath in declared)
        {
            if (!seen.Add(NormalizePath(projectPath)))
                continue; // already recorded as a throw during per-project open

            var failure = AttributeFailure(projectPath, resolvedFailures, ambiguousFileNames, attributed);
            var present = loaded.TryGetValue(NormalizePath(projectPath), out var variants);

            if (present)
            {
                // A multi-targeted project appears once per TFM under the same file path. It counts as
                // skipped only when NO target variant loaded real content AND a failure named it (the
                // empty-stub shape MSBuild retains after a failed load). If any variant carries documents
                // the project indexed — never report the whole project skipped. Order-independent, so the
                // classification is deterministic regardless of the order Roslyn lists the variants.
                var hasContent = variants!.Any(v => v.Documents.Any());
                if (hasContent || failure == null)
                    continue;
            }

            var reason = failure?.Message ?? "project was declared in the solution but did not load";
            result.Add(new SkippedProject(projectPath, reason));
            onDiagnostic?.Invoke($"Skipped project '{Path.GetFileName(projectPath)}': {reason}");
        }

        ReportUnattributedFailures(resolvedFailures, attributed, onDiagnostic);
        return result;
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
            if (failure.ProjectPath == null &&
                failure.Message.Contains(fullPath, StringComparison.OrdinalIgnoreCase))
                return Attribute(failure, attributed);

        if (!ambiguousFileNames.Contains(fileName))
            foreach (var failure in failures)
                if (failure.ProjectPath == null &&
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
            // also carry the ProjectId for a late resolve in ReconcileSkipped.
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
