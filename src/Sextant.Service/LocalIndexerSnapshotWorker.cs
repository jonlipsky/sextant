using Sextant.Core;
using Sextant.Core.Platform;
using Sextant.Indexer;
using Sextant.Service.Sandbox;
using Sextant.Store;

namespace Sextant.Service;

/// <summary>
/// Resolves the on-disk checkout + the DETERMINISTIC solution set to index for an ensure-snapshot request.
/// Automated checkout PROVISIONING (git clone/fetch/worktree of the requested commit) is ProcessStack's
/// job in Phase 14; the data plane only needs to LOCATE an already-provisioned checkout on its persistent
/// checkout volume and decide which solution(s) it covers. This seam keeps that boundary explicit and lets
/// tests supply a checkout without a real git host.
/// </summary>
public interface ICheckoutProvider
{
    bool TryResolve(EnsureSnapshotRequest request, out CheckoutResolution resolution);
}

/// <summary>
/// A resolved checkout together with the EXPLICIT, DETERMINISTIC set of solutions to index for it and how
/// that set was chosen (issue #109). Replaces the historical single nondeterministic solution pick so a
/// monorepo is covered as a unit and the chosen set is recorded in snapshot provenance.
/// </summary>
public sealed record CheckoutResolution
{
    /// <summary>The absolute checkout directory (always inside the persistent checkout volume).</summary>
    public required string CheckoutDir { get; init; }

    /// <summary>
    /// The solutions to index, in a stable deterministic order. Non-empty for a normally-resolved checkout;
    /// EMPTY only for the config-error state (see <see cref="ConfigurationError"/>), where the operator's
    /// per-repo config is present but broken so no solution could be selected and the worker must fail
    /// with reasons rather than silently fall back to a default-root pick (issue #109).
    /// </summary>
    public required IReadOnlyList<string> SelectedSolutions { get; init; }

    /// <summary>How the solution set was chosen (configured vs deterministic default).</summary>
    public required SolutionSelectionSource Source { get; init; }

    /// <summary>Configured solution entries that could not be selected, with reasons (coverage gaps).</summary>
    public IReadOnlyList<SkippedSolution> SkippedSolutions { get; init; } = [];

    /// <summary>
    /// Solutions discovered under the checkout but NOT selected by the deterministic default (empty when an
    /// explicit config drove selection). Recorded for transparency so a default pick never SILENTLY ignores
    /// the rest of a multi-solution repo.
    /// </summary>
    public IReadOnlyList<string> DiscoveredButNotSelected { get; init; } = [];

    /// <summary>
    /// Non-null when the checkout's own <c>sextant.json</c> expressed an explicit scoping intent that could
    /// not be honored — malformed JSON, or every configured <c>solutions</c> entry skipped-with-reason. In
    /// that case <see cref="SelectedSolutions"/> is empty and the worker fails the job with this reason
    /// (plus <see cref="SkippedSolutions"/>) INSTEAD of falling back to a default-root pick, so a broken
    /// operator config never lets partial/unintended coverage be published as complete (issue #109).
    /// </summary>
    public string? ConfigurationError { get; init; }

    /// <summary>True when a solution was selected to index (false only in the config-error state).</summary>
    public bool HasSelectedSolutions => SelectedSolutions.Count > 0;

    /// <summary>The first selected solution — the provisioning/probe signal that a solution exists.</summary>
    public string PrimarySolution => SelectedSolutions[0];
}

/// <summary>
/// Locates a checkout under the persistent checkout volume by a sanitized repository-url directory name,
/// then selects the DETERMINISTIC solution set to index via <see cref="SolutionSelector"/> — honoring the
/// checkout's own <c>sextant.json</c> <c>solutions</c> list, else a single Linux-loadable root solution
/// (issue #109). Returns false (→ the job is <see cref="SnapshotJobStatus.Unsupported"/>) when no checkout
/// or solution is present, so a query-only node degrades cleanly instead of failing hard.
/// </summary>
public sealed class PersistentVolumeCheckoutProvider(ServicePaths paths) : ICheckoutProvider
{
    public bool TryResolve(EnsureSnapshotRequest request, out CheckoutResolution resolution)
    {
        resolution = null!;

        var dirName = ServicePaths.RepoDirectoryName(request.RepositoryRemoteUrl);
        var root = Path.GetFullPath(paths.CheckoutRoot);
        var candidate = Path.GetFullPath(Path.Combine(root, dirName));
        // Containment guard (defense in depth with SanitizeRepo): a crafted repository URL must never
        // resolve a checkout OUTSIDE the persistent checkout volume, so a rogue ensure request can neither
        // read nor (via later cleanup) delete arbitrary host paths.
        if (!IsContainedIn(root, candidate) || !Directory.Exists(candidate))
            return false;

        // Read the CHECKOUT's own sextant.json (not the host's config) so a per-repo `solutions` list is
        // authoritative for what to index. Use the STRICT reader — unlike SextantConfiguration.Load it does
        // NOT swallow a malformed file into defaults: a broken config is an explicit scoping intent we must
        // surface, never silently discard by falling back to a default-root pick (issue #109 / criterion 3).
        if (!SextantConfiguration.TryReadCheckoutSolutions(candidate, out var configured, out var configError))
        {
            // Malformed/unreadable sextant.json: resolve the checkout but carry the error so the worker
            // fails the job WITH a reason instead of indexing a default-root subset as if it were complete.
            resolution = new CheckoutResolution
            {
                CheckoutDir = candidate,
                SelectedSolutions = [],
                Source = SolutionSelectionSource.None,
                ConfigurationError = configError
            };
            return true;
        }

        // Selection is pure over the committed checkout → stable across runs (criterion 2). When the config
        // is absent SolutionSelector falls back to a deterministic default root.
        var selection = SolutionSelector.Select(candidate, configured);

        if (selection.HasSolutions)
        {
            resolution = new CheckoutResolution
            {
                CheckoutDir = candidate,
                SelectedSolutions = selection.SolutionPaths,
                Source = selection.Source,
                SkippedSolutions = selection.SkippedSolutions,
                DiscoveredButNotSelected = selection.DiscoveredSolutions
                    .Where(d => !selection.SolutionPaths.Contains(d, CheckoutInventory.PathComparer))
                    .ToList()
            };
            return true;
        }

        // No solution selected. Distinguish an EXPLICIT-but-unusable config (operator listed solutions but
        // every entry was skipped-with-reason) from "no config and nothing on disk". The former is a config
        // error we must report WITH the per-entry reasons — never silently fall back to a default root; the
        // latter is a genuinely unsupported checkout (query-only node / non-.NET repo) → degrade cleanly.
        if (configured.Count > 0)
        {
            resolution = new CheckoutResolution
            {
                CheckoutDir = candidate,
                SelectedSolutions = [],
                Source = SolutionSelectionSource.Configured,
                SkippedSolutions = selection.SkippedSolutions,
                ConfigurationError =
                    $"every one of the {configured.Count} configured `solutions` entries was " +
                    "skipped-with-reason; no solution could be selected (see diagnostics)."
            };
            return true;
        }

        return false;
    }

    // True when <paramref name="candidate"/> is the root itself or a descendant of it, computed on the
    // normalized full paths so "..", symlink-free traversal, and separator differences cannot escape.
    private static bool IsContainedIn(string root, string candidate)
    {
        var rel = Path.GetRelativePath(root, candidate);
        return rel != ".."
            && !rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(rel);
    }
}

/// <summary>
/// The production single-node worker: it runs the in-process Roslyn indexer over an already-provisioned
/// checkout and lets the orchestrator publish the immutable snapshot through the Phase-9 catalog. The
/// service validates + records the terminal status; this worker just drives extraction. It shares the
/// service's writer <see cref="IndexDatabase"/> (the service serializes worker execution behind its write
/// gate + single-writer lease), so publication is atomic and never races another writer. It stays entirely
/// decoupled from ProcessStack — Phase 14 will instead register a worker that schedules remote execution.
/// </summary>
public sealed class LocalIndexerSnapshotWorker(
    IndexDatabase database,
    SextantConfiguration configuration,
    ICheckoutProvider checkoutProvider,
    Action<string>? log = null,
    WorkerCapability? capability = null,
    IEvaluationSandbox? sandbox = null) : ISnapshotWorker
{
    public async Task<SnapshotWorkResult> ProduceAsync(
        EnsureSnapshotRequest request, string identityHash, string scratchDir, CancellationToken cancellationToken)
    {
        if (!checkoutProvider.TryResolve(request, out var resolution))
        {
            return SnapshotWorkResult.Unsupported(
                $"No provisioned checkout with a solution found for '{request.RepositoryRemoteUrl}'. " +
                "Checkout provisioning is orchestrated separately (Phase 14); this node indexes existing checkouts only.");
        }

        var checkoutDir = resolution.CheckoutDir;

        // Config-error state: the checkout's own sextant.json expressed an explicit scoping intent that
        // could not be honored (malformed JSON, or every configured `solutions` entry skipped-with-reason).
        // Fail the job WITH the reason — never silently fall back to a default-root pick and report it as
        // complete coverage (issue #109 / criterion 3). Nothing is indexed or published.
        if (!resolution.HasSelectedSolutions)
        {
            return SnapshotWorkResult.Failed(
                resolution.ConfigurationError
                    ?? "no indexable solution could be selected for the checkout.",
                BuildConfigErrorDiagnostics(checkoutDir, resolution));
        }

        var context = CreateSnapshotContext(request, capability);

        // The untrusted region: loading the solution EVALUATES its MSBuild projects (arbitrary imported
        // targets / SDK resolvers / inline tasks), so — private repo or not — it runs under the evaluation
        // sandbox's enforced time/memory/secret/filesystem isolation (criterion 2) whenever one is wired.
        // With no sandbox (the byte-identical single-node local default) it runs directly.
        async Task<SnapshotWorkResult> EvaluateAsync(CancellationToken token)
        {
            // Load the DETERMINISTIC selected solution set into ONE workspace (union of projects,
            // de-duplicated by project path/identity). A single selected solution keeps the byte-identical
            // whole-solution fast path; multiple solutions aggregate into one repository snapshot (#109).
            var load = await MultiSolutionLoader.LoadAsync(
                resolution.SelectedSolutions, log, token).ConfigureAwait(false);

            // Guard (coordinator constraint / issue #90 parity): if EVERY project across the selected set was
            // skipped — e.g. an operator scoped this Linux worker to only platform (iOS/Android/Mac/WPF)
            // heads — the union carries no indexable content. Fail BEFORE indexing so an EMPTY snapshot is
            // never published and the branch pointer is never advanced to it. The single-solution loader has
            // the equivalent all-skipped guard (SolutionLoader.LoadSolutionResilientlyAsync); this gives the
            // multi-project path the same protection. The skip reasons ride along as diagnostics.
            if (!load.Solution.Projects.Any(p => p.Documents.Any()))
            {
                var reason =
                    $"no project across the {resolution.SelectedSolutions.Count} selected solution(s) could be " +
                    $"loaded on this worker ({load.SkippedProjects.Count} project(s) skipped-with-reason); " +
                    "nothing was indexed and no snapshot was published.";
                return SnapshotWorkResult.Failed(reason, BuildDiagnostics(checkoutDir, resolution, load));
            }

            // Coverage (issue #119) depends only on the selection, the load, and the checkout's file tree —
            // all known BEFORE indexing — so it rides on the snapshot context and is recorded in the SAME
            // transaction that publishes the snapshot.
            var coverage = SnapshotCoverageBuilder.Build(
                checkoutDir, resolution, load, SnapshotCoverageBuilder.Inventory.Scan(checkoutDir));
            var indexContext = context with { Coverage = coverage.Coverage };

            var orchestrator = new IndexOrchestrator(
                database, log, configuration.DocumentExtractor,
                ExtractionParallelismOptions.FromConfiguration(configuration),
                IndexProfileDescriptor.FromConfiguration(configuration));

            await orchestrator.IndexSolutionAsync(
                load.Solution, progress: null, metrics: null, cancellationToken: token,
                snapshotContext: indexContext).ConfigureAwait(false);

            var published = new SnapshotStore(database.GetConnection()).GetByIdentityHash(identityHash);
            if (published is not { Status: SnapshotStatus.Complete })
            {
                return SnapshotWorkResult.Failed(
                    "indexing finished but no complete snapshot was published for the requested identity " +
                    "(the checkout's committed state may differ from the requested commit).");
            }

            // The verdict follows the coverage DURABLY recorded for the snapshot. It normally equals the one
            // just computed; it differs only when this identity was already published with a recorded
            // verdict (immutable), which then stays authoritative.
            var recorded = new SnapshotCoverageStore(database.GetConnection()).Get(published.Id) ?? coverage.Coverage;
            return BuildResult(published.Id, checkoutDir, resolution, load, coverage with { Coverage = recorded });
        }

        try
        {
            return sandbox is not null
                ? await sandbox.RunAsync(checkoutDir, scratchDir, EvaluateAsync, cancellationToken).ConfigureAwait(false)
                : await EvaluateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SandboxLimitExceededException ex)
        {
            // A budget breach aborts the job cleanly — Failed (retryable), never a partial/complete publish.
            return SnapshotWorkResult.Failed(ex.Message);
        }
        catch (SandboxViolationException ex)
        {
            return SnapshotWorkResult.Failed(ex.Message);
        }
        catch (TransientProvisioningException)
        {
            // A RETRYABLE provisioning failure must NOT be swallowed into a terminal Failed: the service
            // requeues the identity so a later ensure re-attempts it. (Today TryResolve runs before this
            // try, so this is belt-and-suspenders against a future refactor that moves it inside.)
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SnapshotWorkResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Turns a published snapshot + its checkout coverage into the terminal work result. The result is
    /// <see cref="SnapshotWorkResult.Partial"/> — never Complete — whenever the coverage verdict is partial
    /// (issue #119): a discovered solution left unselected, a configured solution skipped, a declared project
    /// that failed to load, a selected solution with no readable projects, zero loaded projects, an
    /// unpopulated submodule, or (no-config default) an on-disk project file in no selected solution. The
    /// Partial reason carries every coverage reason, so a partial snapshot is never presented as complete.
    /// </summary>
    internal static SnapshotWorkResult BuildResult(
        long snapshotId, string checkoutDir, CheckoutResolution resolution, MultiSolutionLoadResult load,
        SnapshotCoverageBuilder.Result coverage)
    {
        var diagnostics = BuildDiagnostics(checkoutDir, resolution, load);
        diagnostics.AddRange(coverage.Diagnostics);

        if (coverage.Coverage.IsPartial)
        {
            var reason =
                "snapshot coverage is partial: " + string.Join(" ", coverage.Coverage.Reasons) +
                " (see diagnostics).";
            return SnapshotWorkResult.Partial(snapshotId, reason, diagnostics, coverage.Coverage);
        }

        return SnapshotWorkResult.Complete(snapshotId, diagnostics, coverage.Coverage);
    }

    /// <summary>
    /// Builds the diagnostics for the config-error state (malformed <c>sextant.json</c> or every configured
    /// <c>solutions</c> entry unusable): an error carrying the configuration reason plus one warning per
    /// skipped configured solution, so the failed job records WHY the operator's scoping intent could not be
    /// honored rather than silently falling back to a default-root pick (issue #109).
    /// </summary>
    internal static List<ProjectOutcome> BuildConfigErrorDiagnostics(
        string checkoutDir, CheckoutResolution resolution)
    {
        var diagnostics = new List<ProjectOutcome>
        {
            new()
            {
                Severity = JobDiagnosticSeverity.Error,
                Code = "solution_config_invalid",
                Message =
                    resolution.ConfigurationError
                    ?? "the checkout's sextant.json solution configuration could not be honored."
            }
        };

        foreach (var skippedSolution in resolution.SkippedSolutions)
        {
            diagnostics.Add(new ProjectOutcome
            {
                Severity = JobDiagnosticSeverity.Warning,
                Code = "solution_skipped",
                ProjectPath = skippedSolution.RequestedPath,
                Message = $"Configured solution '{skippedSolution.RequestedPath}' was skipped: {skippedSolution.Reason}."
            });
        }

        return diagnostics;
    }

    /// <summary>
    /// Builds the provenance diagnostics for a multi-solution load: which solutions were indexed and at what
    /// coverage, and every skipped configured solution / unreadable-or-empty selected solution / skipped
    /// project (warning). Shared by the Complete/Partial result path AND the empty-load Failed path so a run
    /// that indexed nothing still records WHY every project was skipped (criteria 2 &amp; 3 — coverage gaps
    /// are never silent). Checkout-level gaps (unselected solutions, unpopulated submodules, unreferenced
    /// project files) come from <see cref="SnapshotCoverageBuilder"/>.
    /// </summary>
    private static List<ProjectOutcome> BuildDiagnostics(
        string checkoutDir, CheckoutResolution resolution, MultiSolutionLoadResult load)
    {
        var diagnostics = new List<ProjectOutcome>();

        foreach (var coverage in load.Solutions)
        {
            // A selected solution that declares ZERO recognized projects could not be STATICALLY enumerated
            // on this worker (unreadable, empty, or an unrecognized solution shape). Because the multi-
            // solution set is enumerated statically — the selected solutions frequently cannot be MSBuild-
            // evaluated on this worker's platform (iOS/Android/Mac/WPF heads on Linux), which is the whole
            // point of #109 — a zero-project read is a coverage gap that must NOT pass as fully covered.
            // Surface it explicitly (warning ⇒ Partial) rather than letting a silent 0/0 read as success.
            if (coverage.DeclaredProjectCount == 0)
            {
                diagnostics.Add(new ProjectOutcome
                {
                    Severity = JobDiagnosticSeverity.Warning,
                    Code = "solution_no_projects",
                    ProjectPath = RepoRelative(checkoutDir, coverage.SolutionPath),
                    Message =
                        $"Selected solution '{RepoRelative(checkoutDir, coverage.SolutionPath)}' contributed no " +
                        "recognized projects: it could not be statically enumerated on this worker (unreadable, " +
                        "empty, or an unrecognized solution format), so its coverage is reported partial, not complete."
                });
                continue;
            }

            diagnostics.Add(new ProjectOutcome
            {
                Severity = JobDiagnosticSeverity.Info,
                Code = "solution_indexed",
                ProjectPath = RepoRelative(checkoutDir, coverage.SolutionPath),
                Message =
                    $"Indexed solution '{RepoRelative(checkoutDir, coverage.SolutionPath)}' " +
                    $"({coverage.LoadedProjectCount}/{coverage.DeclaredProjectCount} projects loaded; " +
                    $"selection source: {SourceLabel(resolution.Source)})."
            });
        }

        // Discovered-but-unselected solutions are coverage gaps; SnapshotCoverageBuilder records one warning
        // per solution (issue #119), so they are not repeated here.

        foreach (var skippedSolution in resolution.SkippedSolutions)
        {
            diagnostics.Add(new ProjectOutcome
            {
                Severity = JobDiagnosticSeverity.Warning,
                Code = "solution_skipped",
                ProjectPath = skippedSolution.RequestedPath,
                Message = $"Configured solution '{skippedSolution.RequestedPath}' was skipped: {skippedSolution.Reason}."
            });
        }

        foreach (var skippedProject in load.SkippedProjects)
        {
            diagnostics.Add(new ProjectOutcome
            {
                Severity = JobDiagnosticSeverity.Warning,
                Code = "project_skipped",
                ProjectPath = RepoRelative(checkoutDir, skippedProject.ProjectPath),
                Message =
                    $"Project '{skippedProject.ProjectName}' was declared in a selected solution but could " +
                    $"not be loaded on this worker: {skippedProject.Reason}."
            });
        }

        // An empty index must NEVER be reported Complete: if the whole selected set produced zero loaded
        // projects, record it (the caller forces Partial or, before publish, Failed).
        if (load.Solutions.Sum(c => c.LoadedProjectCount) == 0)
        {
            diagnostics.Add(new ProjectOutcome
            {
                Severity = JobDiagnosticSeverity.Warning,
                Code = "no_projects_loaded",
                Message =
                    "No project across the selected solution(s) could be loaded on this worker; the snapshot " +
                    "covers zero projects. See the per-project skip diagnostics for reasons."
            });
        }

        return diagnostics;
    }

    private static string SourceLabel(SolutionSelectionSource source) => source switch
    {
        SolutionSelectionSource.Configured => "configured (sextant.json)",
        SolutionSelectionSource.DefaultRoot => "default root (no config)",
        _ => "none"
    };

    private static string RepoRelative(string checkoutDir, string fullPath)
    {
        try
        {
            var relative = Path.GetRelativePath(checkoutDir, fullPath);
            return relative.StartsWith("..", StringComparison.Ordinal) ? fullPath : relative;
        }
        catch
        {
            return fullPath;
        }
    }

    /// <summary>
    /// Builds the <see cref="SnapshotContext"/> the orchestrator publishes the ensured snapshot against.
    /// This is the ONE seam that carries the service ensure request's optional monotonic
    /// <see cref="EnsureSnapshotRequest.BranchHeadSequence"/> into the orchestrator so the branch advance
    /// becomes forward-only on the service path (issue #84); a null sequence flows through unchanged, so a
    /// non-sequence ensure (and every local CLI/daemon run, which never reaches this worker) keeps the
    /// unconditional-advance behavior byte-for-byte. Internal + static so it is unit-testable without a
    /// real checkout/MSBuild.
    /// </summary>
    internal static SnapshotContext CreateSnapshotContext(EnsureSnapshotRequest request, WorkerCapability? capability) => new()
    {
        RepositoryRemoteUrl = request.RepositoryRemoteUrl,
        CommitSha = request.CommitSha,
        TreeSha = request.TreeSha,
        BranchName = request.BranchName ?? "main",
        IsDefaultBranch = request.ResolveIsDefaultBranch(),
        // Stamp the producing node's capability fingerprint (Phase 15) so the published snapshot's
        // identity + provenance record what evaluated it. Null when unset — an ordinary local index
        // that never routes — keeping the identity byte-identical to the pre-Phase-15 path (CRITICAL 2).
        CapabilityFingerprint = capability?.Fingerprint,
        // The control-plane forward-only head sequence (issue #84); null preserves unconditional advance.
        BranchHeadSequence = request.BranchHeadSequence
    };
}
