using Sextant.Core;
using Sextant.Indexer;
using Sextant.Store;

namespace Sextant.Service;

/// <summary>
/// Computes a checkout's <see cref="SnapshotCoverage"/> and the per-gap diagnostics that explain it (issue
/// #119). Pure over its inputs — the solution selection, the multi-solution load, and a file-system
/// inventory of the checkout — so the verdict is deterministic for a given commit and unit-testable
/// without MSBuild.
/// <para>
/// The verdict is PARTIAL whenever the published snapshot does not cover the whole checkout: a discovered
/// solution was left unselected (defensive — the no-config default selects the union of every discovered
/// solution, issue #124, so this is empty unless a future selector narrows again), a configured solution
/// was unusable, a declared project failed to load, a selected solution declared nothing readable, nothing
/// loaded at all, a declared submodule is not populated, or (under the no-config default) a project file on
/// disk is in no selected solution. Each gap is also recorded as a warning diagnostic, so a partial
/// snapshot is never silent.
/// </para>
/// </summary>
public static class SnapshotCoverageBuilder
{
    /// <summary>Caps per-item diagnostics of one kind so a huge monorepo cannot flood the job ledger.</summary>
    internal const int MaxItemDiagnosticsPerKind = 200;

    /// <summary>Caps how many items a single coverage REASON names, keeping the persisted record compact.</summary>
    internal const int MaxNamedInReason = 10;

    /// <summary>The checkout facts coverage is computed over, gathered without running git or MSBuild.</summary>
    public sealed record Inventory(
        IReadOnlyList<string> ProjectFilesOnDisk,
        IReadOnlyList<DeclaredSubmodule> Submodules,
        IReadOnlyList<string>? ScanErrors = null)
    {
        public static Inventory Scan(string checkoutDir)
        {
            var errors = new List<string>();
            var projects = CheckoutInventory.FindProjectFiles(checkoutDir, errors);
            var submodules = CheckoutInventory.FindDeclaredSubmodules(checkoutDir, errors);
            return new Inventory(projects, submodules, errors);
        }
    }

    /// <summary>The computed coverage plus the diagnostics that explain every gap it records.</summary>
    public sealed record Result(SnapshotCoverage Coverage, IReadOnlyList<ProjectOutcome> Diagnostics);

    public static Result Build(
        string checkoutDir, CheckoutResolution resolution, MultiSolutionLoadResult load, Inventory inventory,
        IReadOnlyList<SdkPinOverride>? sdkPinOverrides = null)
    {
        var comparer = CheckoutInventory.PathComparer;
        var reasons = new List<string>();
        var diagnostics = new List<ProjectOutcome>();

        var totalLoaded = load.Solutions.Sum(c => c.LoadedProjectCount);
        var emptySolutions = load.Solutions.Count(c => c.DeclaredProjectCount == 0);

        // Every project file the index actually accounts for: declared by a selected solution, or pulled
        // into the workspace by the load (e.g. a ProjectReference outside every selected solution). Physical
        // paths, so the per-TFM Roslyn projects of one multi-targeted file count once.
        var loadedFiles = new HashSet<string>(comparer);
        foreach (var project in load.Solution.Projects)
            if (!string.IsNullOrEmpty(project.FilePath))
                loadedFiles.Add(Path.GetFullPath(project.FilePath));
        var accounted = new HashSet<string>(load.DeclaredProjects.Select(Path.GetFullPath), comparer);
        accounted.UnionWith(loadedFiles);
        var unreferenced = inventory.ProjectFilesOnDisk
            .Where(p => !accounted.Contains(Path.GetFullPath(p)))
            .ToList();
        var unpopulated = inventory.Submodules.Where(s => !s.Populated).ToList();
        var scanErrors = inventory.ScanErrors ?? [];

        // Defensive invariant: the no-config default selects the UNION of every discovered solution (#124)
        // and an explicit config records no discovery, so this is empty today. It is still honored so that a
        // future narrowing selector can never silently hide an unselected solution.
        var notSelected = resolution.DiscoveredButNotSelected;
        if (notSelected.Count > 0)
        {
            var discovered = notSelected.Count + resolution.SelectedSolutions.Count;
            reasons.Add(
                $"{notSelected.Count} of {discovered} discovered solution(s) were not selected; their projects " +
                "are not indexed unless another selected solution declares them (list them in sextant.json " +
                "`solutions` to index them).");
            AddCapped(diagnostics, notSelected, s => new ProjectOutcome
            {
                Severity = JobDiagnosticSeverity.Warning,
                Code = "solution_not_selected",
                ProjectPath = RepoRelative(checkoutDir, s),
                Message = $"Solution '{RepoRelative(checkoutDir, s)}' was discovered but not selected for indexing."
            }, "solution_not_selected", "discovered solution(s) were not selected");
        }

        if (resolution.SkippedSolutions.Count > 0)
            reasons.Add($"{resolution.SkippedSolutions.Count} configured solution(s) could not be used.");

        if (load.SkippedProjects.Count > 0)
        {
            // A project skipped because hostfxr could not resolve the SDK its global.json pins (issue #113) is
            // named as such, with the pin, so the operator sees the actionable cause rather than a generic skip
            // (its per-project detail is an `sdk_resolution_failed` diagnostic).
            var sdkSkips = load.SkippedProjects
                .Select(s => (Skip: s, Ok: HostFxrSdkResolutionError.TryParse(s.Reason, out var e), Error: e))
                .Where(x => x.Ok)
                .ToList();
            var otherSkips = load.SkippedProjects
                .Where(s => !HostFxrSdkResolutionError.TryParse(s.Reason, out _))
                .ToList();
            if (otherSkips.Count > 0)
            {
                // Name a bounded sample so the record (surfaced in MCP meta) stays compact; the full list with
                // each project's load failure is in the `project_skipped` job diagnostics.
                var sample = otherSkips
                    .Take(MaxNamedInReason)
                    .Select(s => ReasonPath(checkoutDir, s.ProjectPath));
                var more = otherSkips.Count > MaxNamedInReason
                    ? $", +{otherSkips.Count - MaxNamedInReason} more"
                    : string.Empty;
                reasons.Add(
                    $"{otherSkips.Count} declared project(s) could not be loaded on this worker " +
                    $"({string.Join(", ", sample)}{more}); see the `project_skipped` diagnostics for each reason.");
            }
            if (sdkSkips.Count > 0)
            {
                var pins = sdkSkips
                    .Where(x => x.Error!.IsGlobalJsonPin)
                    .Select(x => DescribePin(checkoutDir, x.Error!))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                reasons.Add(pins.Count > 0
                    ? $"{sdkSkips.Count} declared project(s) could not be loaded because the .NET SDK their " +
                      $"global.json pins is not installed on this worker ({string.Join("; ", pins.Take(10))}" +
                      $"{(pins.Count > 10 ? "; …" : string.Empty)})."
                    : $"{sdkSkips.Count} declared project(s) could not be loaded because no compatible .NET SDK " +
                      "could be resolved on this worker.");
            }
        }

        if (emptySolutions > 0)
            reasons.Add($"{emptySolutions} selected solution(s) declared no readable projects.");

        if (totalLoaded == 0)
            reasons.Add("no project across the selected solution(s) loaded.");

        if (unpopulated.Count > 0)
        {
            reasons.Add(
                $"{unpopulated.Count} of {inventory.Submodules.Count} declared submodule(s) are not populated " +
                $"in the checkout ({string.Join(", ", unpopulated.Take(10).Select(s => s.Path))}" +
                $"{(unpopulated.Count > 10 ? ", …" : string.Empty)}); their projects are not indexed.");
            AddCapped(diagnostics, unpopulated, s => new ProjectOutcome
            {
                Severity = JobDiagnosticSeverity.Warning,
                Code = "submodule_unpopulated",
                ProjectPath = s.Path,
                Message =
                    $"Submodule '{s.Path}' is declared in .gitmodules but is not populated in the checkout, so " +
                    "none of its projects were indexed."
            }, "submodule_unpopulated", "declared submodule(s) are not populated");
        }

        // An operator's explicit `solutions` list is a deliberate scope: project files outside it are
        // reported (info) but do not make the snapshot partial. Under the no-config default nobody chose to
        // exclude them, so they are a coverage gap.
        var orphansArePartial = resolution.Source != SolutionSelectionSource.Configured;
        if (unreferenced.Count > 0)
        {
            if (orphansArePartial)
                reasons.Add(
                    $"{unreferenced.Count} of {inventory.ProjectFilesOnDisk.Count} project file(s) on disk are " +
                    "not declared by any selected solution and were not indexed.");
            var severity = orphansArePartial ? JobDiagnosticSeverity.Warning : JobDiagnosticSeverity.Info;
            AddCapped(diagnostics, unreferenced, p => new ProjectOutcome
            {
                Severity = severity,
                Code = "project_file_unreferenced",
                ProjectPath = RepoRelative(checkoutDir, p),
                Message =
                    $"Project file '{RepoRelative(checkoutDir, p)}' is not declared by any selected solution and " +
                    (orphansArePartial
                        ? "was not indexed."
                        : "was not indexed (outside the configured `solutions` scope).")
            }, "project_file_unreferenced", "project file(s) are not declared by any selected solution", severity);
        }

        if (scanErrors.Count > 0)
        {
            reasons.Add(
                $"the coverage scan could not inspect {scanErrors.Count} part(s) of the checkout, so it cannot " +
                "prove every project and submodule was accounted for.");
            AddCapped(diagnostics, scanErrors, e => new ProjectOutcome
            {
                Severity = JobDiagnosticSeverity.Warning,
                Code = "coverage_scan_incomplete",
                Message = RedactCheckout(checkoutDir, e)
            }, "coverage_scan_incomplete", "part(s) of the checkout could not be inspected");
        }

        var coverage = new SnapshotCoverage
        {
            Verdict = reasons.Count > 0 ? SnapshotCoverageVerdict.Partial : SnapshotCoverageVerdict.Complete,
            Reasons = reasons,
            SelectionSource = SourceWireName(resolution.Source),
            SolutionsDiscovered = resolution.Source == SolutionSelectionSource.Configured
                ? resolution.SelectedSolutions.Count + resolution.SkippedSolutions.Count
                : resolution.SelectedSolutions.Count + notSelected.Count,
            SolutionsSelected = resolution.SelectedSolutions.Count,
            SolutionsNotSelected = notSelected.Count,
            SolutionsSkipped = resolution.SkippedSolutions.Count,
            ProjectsDeclared = load.DeclaredProjects.Count,
            ProjectsLoaded = loadedFiles.Count,
            ProjectsSkipped = load.SkippedProjects.Count,
            ProjectFilesOnDisk = inventory.ProjectFilesOnDisk.Count,
            ProjectFilesUnreferenced = unreferenced.Count,
            SubmodulesDeclared = inventory.Submodules.Count,
            SubmodulesUnpopulated = unpopulated.Count,
            ScanErrors = scanErrors.Count,
            SdkPinOverrides = sdkPinOverrides is { Count: > 0 } ? sdkPinOverrides : null
        };

        return new Result(coverage, diagnostics);
    }

    /// <summary>
    /// "'&lt;repo-relative global.json&gt;' requests SDK &lt;version&gt;" for a classified hostfxr failure —
    /// checkout-relative so the job ledger never records the worker's volume layout.
    /// </summary>
    internal static string DescribePin(string checkoutDir, HostFxrSdkResolutionError error)
    {
        var path = error.GlobalJsonPath is { } p ? RepoRelative(checkoutDir, p) : "global.json";
        return error.RequestedVersion is { } v ? $"'{path}' requests SDK {v}" : $"'{path}'";
    }

    /// <summary>
    /// The snake_case wire name of a selection source. Coverage rows are immutable, so rows recorded before
    /// issue #124 may still carry the retired single-solution name <c>default_root</c>.
    /// </summary>
    public static string SourceWireName(SolutionSelectionSource source) => source switch
    {
        SolutionSelectionSource.Configured => "configured",
        SolutionSelectionSource.DefaultUnion => "default_union",
        _ => "none"
    };

    private static void AddCapped<T>(
        List<ProjectOutcome> diagnostics, IReadOnlyList<T> items, Func<T, ProjectOutcome> toDiagnostic,
        string code, string summary, string severity = JobDiagnosticSeverity.Warning)
    {
        foreach (var item in items.Take(MaxItemDiagnosticsPerKind))
            diagnostics.Add(toDiagnostic(item));
        if (items.Count > MaxItemDiagnosticsPerKind)
            diagnostics.Add(new ProjectOutcome
            {
                Severity = severity,
                Code = code,
                Message = $"…and {items.Count - MaxItemDiagnosticsPerKind} more {summary} (not listed individually)."
            });
    }

    // Scan errors carry absolute paths; express them checkout-relative so the job ledger never records the
    // worker's volume layout.
    private static string RedactCheckout(string checkoutDir, string message)
    {
        var root = Path.GetFullPath(checkoutDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return message.Replace(root, ".", StringComparison.OrdinalIgnoreCase).Replace('\\', '/');
    }

    // A coverage reason is persisted in the immutable record and surfaced to query clients (MCP meta,
    // /control/resolve), so it must never carry a worker volume path. A declared project OUTSIDE the
    // checkout (a solution entry like `../../other/X.csproj`) is named by its file name only.
    private static string ReasonPath(string checkoutDir, string fullPath)
    {
        var relative = RepoRelative(checkoutDir, fullPath);
        return Path.IsPathRooted(relative) ? $"<outside checkout>/{Path.GetFileName(fullPath)}" : relative;
    }

    private static string RepoRelative(string checkoutDir, string fullPath)
    {
        try
        {
            var relative = Path.GetRelativePath(checkoutDir, fullPath);
            return relative.StartsWith("..", StringComparison.Ordinal) ? fullPath : relative.Replace('\\', '/');
        }
        catch
        {
            return fullPath;
        }
    }
}
