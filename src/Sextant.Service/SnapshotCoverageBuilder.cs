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
/// <para>
/// <see cref="BuildProviders"/> applies the same idea to each Phase-12 provider snapshot (issue #162), scoped
/// to that provider's submodule subtree: it is partial when a provider project was skipped or left
/// unreached, when the provider's own solutions were not (all) the selection basis, when a nested submodule
/// is unpopulated, or when the scan was incomplete.
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
        /// <summary>
        /// The solution files (full paths) found under the checkout's POPULATED submodules — the Phase-12
        /// providers' OWN solutions (issue #162), whether or not the parent's selection chose them. Null when
        /// not scanned (then a provider is judged as if it declared no solution).
        /// </summary>
        public IReadOnlyList<string>? SubmoduleSolutionFiles { get; init; }

        public static Inventory Scan(string checkoutDir)
        {
            var errors = new List<string>();
            var projects = CheckoutInventory.FindProjectFiles(checkoutDir, errors);
            var submodules = CheckoutInventory.FindDeclaredSubmodules(checkoutDir, errors);
            return new Inventory(projects, submodules, errors)
            {
                SubmoduleSolutionFiles = ScanSubmoduleSolutions(checkoutDir, submodules, errors)
            };
        }

        // Walks only the OUTERMOST populated submodules (a nested one lies inside its parent's walk), so a
        // checkout without submodules pays nothing. The project walk already covered these directories, so an
        // I/O error it reported is not counted twice.
        private static IReadOnlyList<string> ScanSubmoduleSolutions(
            string checkoutDir, IReadOnlyList<DeclaredSubmodule> submodules, List<string> errors)
        {
            var root = Path.GetFullPath(checkoutDir);
            var populated = submodules.Where(s => s.Populated).Select(s => s.Path).ToList();
            var solutions = new List<string>();
            var walkErrors = new List<string>();
            foreach (var path in populated)
            {
                if (populated.Any(outer => path.StartsWith(outer + "/", StringComparison.Ordinal)))
                    continue;
                solutions.AddRange(CheckoutInventory.FindSolutionFiles(Path.Combine(root, path), walkErrors));
            }
            foreach (var error in walkErrors)
                if (!errors.Contains(error))
                    errors.Add(error);
            return solutions.Distinct(CheckoutInventory.PathComparer).OrderBy(s => s, StringComparer.Ordinal).ToList();
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
            // Explain WHY each gap exists when the cloning provider recorded it (issue #125): url refused,
            // fetch failed, pinned commit missing, … — keyed by the SAME checkout-relative path the scan uses.
            var provisioning = new Dictionary<string, SubmoduleProvisioningOutcome>(comparer);
            foreach (var outcome in resolution.SubmoduleProvisioning)
                provisioning.TryAdd(outcome.Path, outcome);
            string Describe(DeclaredSubmodule s) =>
                provisioning.TryGetValue(s.Path, out var o) && !o.IsPopulated
                    ? $"{s.Path} ({SubmoduleProvisioningStatus.Describe(o.Status)}{(o.Reason is { Length: > 0 } r ? ": " + r : string.Empty)})"
                    : s.Path;

            reasons.Add(
                $"{unpopulated.Count} of {inventory.Submodules.Count} declared submodule(s) are not populated " +
                $"in the checkout ({string.Join(", ", unpopulated.Take(10).Select(Describe))}" +
                $"{(unpopulated.Count > 10 ? ", …" : string.Empty)}); their projects are not indexed.");
            AddCapped(diagnostics, unpopulated, s => new ProjectOutcome
            {
                Severity = JobDiagnosticSeverity.Warning,
                Code = "submodule_unpopulated",
                ProjectPath = s.Path,
                Message = provisioning.TryGetValue(s.Path, out var o) && !o.IsPopulated
                    ? $"Submodule '{s.Path}' is declared in .gitmodules but was not populated " +
                      $"({SubmoduleProvisioningStatus.Describe(o.Status)}" +
                      $"{(o.Reason is { Length: > 0 } r ? ": " + r : string.Empty)}), so none of its projects were indexed."
                    : $"Submodule '{s.Path}' is declared in .gitmodules but is not populated in the checkout, so " +
                      "none of its projects were indexed."
            }, "submodule_unpopulated", "declared submodule(s) are not populated");
        }

        // Project files on disk that no selected solution declares and no loaded project references are
        // reported as a NOTE, never a partial verdict. Under the no-config default every discovered solution is
        // indexed (#124) and every project a solution reaches is loaded, so such a file is outside every build
        // of the repository (a sample, a template, a scratch project): what is missing is only the code INSIDE
        // it, which the note names. An explicit `solutions` list is a deliberate scope, reported the same way.
        var notes = new List<string>();
        if (unreferenced.Count > 0)
        {
            var paths = unreferenced.Select(p => RepoRelative(checkoutDir, p)).Order(StringComparer.Ordinal).ToList();
            notes.Add(
                $"{unreferenced.Count} project file(s) outside every selected solution were not indexed: " +
                $"{NameSample(paths)}.");
            AddCapped(diagnostics, unreferenced, p => new ProjectOutcome
            {
                Severity = JobDiagnosticSeverity.Info,
                Code = "project_file_unreferenced",
                ProjectPath = RepoRelative(checkoutDir, p),
                Message =
                    $"Project file '{RepoRelative(checkoutDir, p)}' is not declared by any selected solution and " +
                    (resolution.Source == SolutionSelectionSource.Configured
                        ? "was not indexed (outside the configured `solutions` scope)."
                        : "was not indexed.")
            }, "project_file_unreferenced", "project file(s) are not declared by any selected solution",
            JobDiagnosticSeverity.Info);
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
            SdkPinOverrides = sdkPinOverrides is { Count: > 0 } ? sdkPinOverrides : null,
            Notes = notes.Count > 0 ? notes : null
        };

        return new Result(coverage, diagnostics);
    }

    /// <summary>
    /// Appends <paramref name="notes"/> to <paramref name="coverage"/>'s notes. Notes never change the verdict.
    /// </summary>
    public static SnapshotCoverage WithNotes(SnapshotCoverage coverage, IReadOnlyList<string> notes)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        if (notes is not { Count: > 0 })
            return coverage;
        return coverage with { Notes = [.. coverage.Notes ?? [], .. notes] };
    }

    /// <summary>
    /// Computes the coverage of every Phase-12 PROVIDER snapshot this checkout can produce (issue #162), keyed
    /// by the populated submodule's checkout-relative <c>/</c>-separated path (the same key the orchestrator
    /// routes a provider from). A provider snapshot is built from exactly the projects of its submodule
    /// subtree that the PARENT's solution selection declared or loaded, so its verdict is computed over that
    /// subtree alone, and is PARTIAL when:
    /// <list type="bullet">
    /// <item>a declared project in the subtree failed to load;</item>
    /// <item>a declared project in the subtree was not loaded because the parent's time budget ran out (issue #245);</item>
    /// <item>a project file in the subtree is on disk but was neither declared nor loaded (always a gap for
    /// the provider, even under a <c>configured</c> parent scope: nobody scoped the PROVIDER repository);</item>
    /// <item>one of the provider's OWN solutions was not selected — in particular when none was, i.e. the
    /// provider was built only from projects the parent's selection reaches, so it lacks the provider's
    /// solution-scoped view even if every project happened to load;</item>
    /// <item>a selected provider solution declared nothing readable;</item>
    /// <item>a submodule nested in the provider is not populated;</item>
    /// <item>the checkout scan reported any error (it cannot prove nothing in the subtree was missed).</item>
    /// </list>
    /// Reasons name paths relative to the PROVIDER root only, never the parent's layout or URL, because the
    /// record is served to whoever later ensures the provider repository directly.
    /// </summary>
    public static IReadOnlyDictionary<string, SnapshotCoverage> BuildProviders(
        string checkoutDir, CheckoutResolution resolution, MultiSolutionLoadResult load, Inventory inventory,
        IReadOnlyList<SdkPinOverride>? sdkPinOverrides = null)
    {
        var comparer = CheckoutInventory.PathComparer;
        var root = Path.GetFullPath(checkoutDir);
        var result = new Dictionary<string, SnapshotCoverage>(comparer);

        var loadedFiles = new HashSet<string>(comparer);
        foreach (var project in load.Solution.Projects)
            if (!string.IsNullOrEmpty(project.FilePath))
                loadedFiles.Add(Path.GetFullPath(project.FilePath));
        var declaredFiles = new HashSet<string>(load.DeclaredProjects.Select(Path.GetFullPath), comparer);
        var selectedSolutions = resolution.SelectedSolutions.Select(Path.GetFullPath).ToList();
        var providerSolutionsOnDisk = (inventory.SubmoduleSolutionFiles ?? []).Select(Path.GetFullPath).ToList();
        var scanErrors = inventory.ScanErrors ?? [];

        foreach (var submodule in inventory.Submodules.Where(s => s.Populated))
        {
            var subtree = Path.GetFullPath(Path.Combine(root, submodule.Path));
            bool InSubtree(string fullPath) => IsUnder(subtree, fullPath);
            string Rel(string fullPath) => Path.GetRelativePath(subtree, fullPath).Replace('\\', '/');

            var declared = declaredFiles.Where(InSubtree).ToList();
            var loaded = loadedFiles.Where(InSubtree).ToList();
            var skipped = load.SkippedProjects.Where(s => InSubtree(Path.GetFullPath(s.ProjectPath))).ToList();
            var deferred = load.DeferredProjects.Select(Path.GetFullPath).Where(InSubtree).ToList();
            var onDisk = inventory.ProjectFilesOnDisk.Where(p => InSubtree(Path.GetFullPath(p))).ToList();
            var accounted = new HashSet<string>(declared, comparer);
            accounted.UnionWith(loaded);
            var unreferenced = onDisk.Where(p => !accounted.Contains(Path.GetFullPath(p))).ToList();
            var nested = inventory.Submodules
                .Where(s => s.Path.StartsWith(submodule.Path + "/", StringComparison.Ordinal))
                .ToList();
            var nestedUnpopulated = nested.Where(s => !s.Populated).ToList();
            var ownSolutions = providerSolutionsOnDisk.Where(InSubtree).ToHashSet(comparer);
            var selectedOwn = selectedSolutions.Where(InSubtree).ToList();
            ownSolutions.UnionWith(selectedOwn);
            var notSelectedOwn = ownSolutions.Where(s => !selectedOwn.Contains(s, comparer))
                .OrderBy(s => Rel(s), StringComparer.Ordinal)
                .ToList();
            var emptyOwn = load.Solutions
                .Count(c => c.DeclaredProjectCount == 0 && InSubtree(Path.GetFullPath(c.SolutionPath)));

            var reasons = new List<string>();
            if (skipped.Count > 0)
                reasons.Add(
                    $"{skipped.Count} project(s) of this repository could not be loaded while indexing the checkout " +
                    $"that pins it ({NameSample(skipped.Select(s => Rel(Path.GetFullPath(s.ProjectPath))).ToList())}).");
            if (deferred.Count > 0)
                reasons.Add(
                    $"{deferred.Count} project(s) of this repository were not loaded because the indexing checkout's " +
                    $"time budget ran out ({NameSample(deferred.Select(Rel).ToList())}).");
            if (selectedOwn.Count == 0 && ownSolutions.Count > 0)
                reasons.Add(
                    $"none of this repository's {ownSolutions.Count} solution(s) was selected; it was built only from " +
                    "the projects the indexing checkout's solution selection reaches, so its solution-scoped view " +
                    "is missing.");
            else if (notSelectedOwn.Count > 0)
                reasons.Add(
                    $"{notSelectedOwn.Count} of this repository's {ownSolutions.Count} solution(s) were not selected " +
                    $"({NameSample(notSelectedOwn.Select(Rel).ToList())}).");
            if (unreferenced.Count > 0)
                reasons.Add(
                    $"{unreferenced.Count} of {onDisk.Count} project file(s) on disk were not reached by the indexing " +
                    $"checkout's solution selection and were not indexed ({NameSample(unreferenced.Select(p => Rel(Path.GetFullPath(p))).ToList())}).");
            if (emptyOwn > 0)
                reasons.Add($"{emptyOwn} selected solution(s) of this repository declared no readable projects.");
            if (nestedUnpopulated.Count > 0)
                reasons.Add(
                    $"{nestedUnpopulated.Count} of {nested.Count} nested submodule(s) are not populated " +
                    $"({NameSample(nestedUnpopulated.Select(s => s.Path[(submodule.Path.Length + 1)..]).ToList())}); " +
                    "their projects are not indexed.");
            if (scanErrors.Count > 0)
                reasons.Add(
                    $"the coverage scan could not inspect {scanErrors.Count} part(s) of the indexing checkout, so it " +
                    "cannot prove every project of this repository was accounted for.");

            var pins = (sdkPinOverrides ?? [])
                .Where(p => PinAppliesTo(p.GlobalJsonPath, submodule.Path))
                .Select(p => p with { GlobalJsonPath = ProviderPinPath(p.GlobalJsonPath, submodule.Path) })
                .ToList();
            result[submodule.Path] = new SnapshotCoverage
            {
                Verdict = reasons.Count > 0 ? SnapshotCoverageVerdict.Partial : SnapshotCoverageVerdict.Complete,
                Reasons = reasons,
                SelectionSource = selectedOwn.Count > 0 ? SourceWireName(resolution.Source) : ParentSelectionSource,
                SolutionsDiscovered = ownSolutions.Count,
                SolutionsSelected = selectedOwn.Count,
                SolutionsNotSelected = notSelectedOwn.Count,
                SolutionsSkipped = 0,
                ProjectsDeclared = declared.Count,
                ProjectsLoaded = loaded.Count,
                ProjectsSkipped = skipped.Count,
                ProjectFilesOnDisk = onDisk.Count,
                ProjectFilesUnreferenced = unreferenced.Count,
                SubmodulesDeclared = nested.Count,
                SubmodulesUnpopulated = nestedUnpopulated.Count,
                ScanErrors = scanErrors.Count,
                SdkPinOverrides = pins.Count > 0 ? pins : null
            };
        }

        return result;
    }

    /// <summary>
    /// The <see cref="SnapshotCoverage.SelectionSource"/> of a provider snapshot none of whose own solutions
    /// was selected: it was built only from the projects the indexing parent's selection reached (issue #162).
    /// </summary>
    public const string ParentSelectionSource = "parent_selection";

    private static bool IsUnder(string directory, string fullPath)
    {
        var relative = Path.GetRelativePath(directory, fullPath);
        return relative != "." && !Path.IsPathRooted(relative)
            && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith("../", StringComparison.Ordinal);
    }

    // A global.json governs every project below its directory, so a pin override applies to a provider when
    // the pin sits at or above the provider's root, or inside it.
    private static bool PinAppliesTo(string globalJsonPath, string submodulePath)
    {
        var slash = globalJsonPath.LastIndexOf('/');
        var dir = slash < 0 ? string.Empty : globalJsonPath[..slash];
        return dir.Length == 0
            || string.Equals(dir, submodulePath, StringComparison.Ordinal)
            || submodulePath.StartsWith(dir + "/", StringComparison.Ordinal)
            || dir.StartsWith(submodulePath + "/", StringComparison.Ordinal);
    }

    // A pin inside the provider is named relative to the provider root; one at or above it belongs to the
    // indexing checkout, whose layout the provider's readers must not learn, so only its file name is kept.
    private static string ProviderPinPath(string globalJsonPath, string submodulePath) =>
        globalJsonPath.StartsWith(submodulePath + "/", StringComparison.Ordinal)
            ? globalJsonPath[(submodulePath.Length + 1)..]
            : $"<indexing checkout>/{Path.GetFileName(globalJsonPath)}";

    private static string NameSample(IReadOnlyList<string> names) =>
        string.Join(", ", names.Take(MaxNamedInReason))
        + (names.Count > MaxNamedInReason ? $", +{names.Count - MaxNamedInReason} more" : string.Empty);

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
