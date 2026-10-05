using Sextant.Core;
using Sextant.Core.Platform;
using Sextant.Indexer;
using Sextant.Service.Restore;
using Sextant.Service.Sandbox;
using Sextant.Service.SdkPin;
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
    /// with reasons rather than silently fall back to the no-config default selection (issue #109).
    /// </summary>
    public required IReadOnlyList<string> SelectedSolutions { get; init; }

    /// <summary>How the solution set was chosen (configured vs the deterministic default union).</summary>
    public required SolutionSelectionSource Source { get; init; }

    /// <summary>Configured solution entries that could not be selected, with reasons (coverage gaps).</summary>
    public IReadOnlyList<SkippedSolution> SkippedSolutions { get; init; } = [];

    /// <summary>
    /// Solutions discovered under the checkout but NOT selected (empty when an explicit config drove
    /// selection). The no-config default selects the UNION of every discovered solution (issue #124), so
    /// this is empty by construction today; it is still derived and honored by coverage as a defensive
    /// invariant, so a future narrowing selector could never SILENTLY ignore part of a multi-solution repo.
    /// </summary>
    public IReadOnlyList<string> DiscoveredButNotSelected { get; init; } = [];

    /// <summary>
    /// Non-null when the checkout's own <c>sextant.json</c> expressed an explicit scoping intent that could
    /// not be honored — malformed JSON, or every configured <c>solutions</c> entry skipped-with-reason. In
    /// that case <see cref="SelectedSolutions"/> is empty and the worker fails the job with this reason
    /// (plus <see cref="SkippedSolutions"/>) INSTEAD of falling back to the default selection, so a broken
    /// operator config never lets partial/unintended coverage be published as complete (issue #109).
    /// </summary>
    public string? ConfigurationError { get; init; }

    /// <summary>
    /// How each <c>.gitmodules</c>-declared submodule was provisioned when the cloning provider produced this
    /// checkout (issue #125) — empty for a locate-only / externally-provisioned checkout. Coverage uses it to
    /// explain WHY a declared submodule is unpopulated (url refused, fetch failed, pinned commit missing, …).
    /// </summary>
    public IReadOnlyList<SubmoduleProvisioningOutcome> SubmoduleProvisioning { get; init; } = [];

    /// <summary>True when a solution was selected to index (false only in the config-error state).</summary>
    public bool HasSelectedSolutions => SelectedSolutions.Count > 0;

    /// <summary>The first selected solution — the provisioning/probe signal that a solution exists.</summary>
    public string PrimarySolution => SelectedSolutions[0];
}

/// <summary>
/// Locates a checkout under the persistent checkout volume by a sanitized repository-url directory name,
/// then selects the DETERMINISTIC solution set to index via <see cref="SolutionSelector"/> — honoring the
/// checkout's own <c>sextant.json</c> <c>solutions</c> list, else the deterministic UNION of every
/// discovered solution (issues #109, #124). Returns false (→ the job is <see cref="SnapshotJobStatus.Unsupported"/>) when no checkout
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
        // surface, never silently discard by falling back to the default selection (issue #109 / criterion 3).
        if (!SextantConfiguration.TryReadCheckoutSolutions(candidate, out var configured, out var configError))
        {
            // Malformed/unreadable sextant.json: resolve the checkout but carry the error so the worker
            // fails the job WITH a reason instead of indexing a default selection as if it were intended.
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
        // is absent SolutionSelector selects the deterministic union of every discovered solution (#124).
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
        // error we must report WITH the per-entry reasons — never silently fall back to the default union; the
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
    IEvaluationSandbox? sandbox = null,
    SdkPinGuard? sdkPinGuard = null,
    PackageRestoreRunner? packageRestore = null,
    TimeProvider? clock = null) : ISnapshotWorker
{
    // Issue #113: neutralizes an unsatisfiable global.json SDK pin for the duration of the MSBuild load only.
    private readonly SdkPinGuard _sdkPinGuard = sdkPinGuard ?? new SdkPinGuard(log: log);

    // Restores the selected solutions before the load so package compile assets and the SDK's transitive
    // project references exist; without them code that reaches a type through another project cannot bind.
    private readonly PackageRestoreRunner _packageRestore = packageRestore ?? new PackageRestoreRunner(log: log);

    // The clock the time-budget plan reads (issue #245); tests drive it by hand.
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<SnapshotWorkResult> ProduceAsync(
        EnsureSnapshotRequest request, string identityHash, string scratchDir, CancellationToken cancellationToken)
    {
        // Repair any checkout an interrupted job left with a neutralized SDK pin BEFORE the provider decides to
        // reuse a cached checkout (reuse only checks HEAD, never the working tree).
        _sdkPinGuard.RecoverAll();

        if (!checkoutProvider.TryResolve(request, out var resolution))
        {
            return SnapshotWorkResult.Unsupported(
                $"No provisioned checkout with a solution found for '{request.RepositoryRemoteUrl}'. " +
                "Checkout provisioning is orchestrated separately (Phase 14); this node indexes existing checkouts only.");
        }

        var checkoutDir = resolution.CheckoutDir;

        // Never index over a checkout whose committed global.json could not be put back after a crash.
        if (!_sdkPinGuard.Recover(checkoutDir))
        {
            throw SdkPinRestoreFailure(
                "a previous job left this checkout's global.json SDK pin neutralized and its restore journal could not " +
                "be replayed; see the service log for the file and the journal");
        }

        // Config-error state: the checkout's own sextant.json expressed an explicit scoping intent that
        // could not be honored (malformed JSON, or every configured `solutions` entry skipped-with-reason).
        // Fail the job WITH the reason — never silently fall back to the default selection and report it as
        // complete coverage (issue #109 / criterion 3). Nothing is indexed or published.
        if (!resolution.HasSelectedSolutions)
        {
            return SnapshotWorkResult.Failed(
                resolution.ConfigurationError
                    ?? "no indexable solution could be selected for the checkout.",
                BuildConfigErrorDiagnostics(checkoutDir, resolution));
        }

        var context = CreateSnapshotContext(
            request, capability, _sdkPinGuard.IdentityComponent, _packageRestore.IdentityComponent);
        var pinState = new SdkPinLoadState();

        // The untrusted region: loading the solution EVALUATES its MSBuild projects (arbitrary imported
        // targets / SDK resolvers / inline tasks), so — private repo or not — it runs under the evaluation
        // sandbox's enforced time/memory/secret/filesystem isolation (criterion 2) whenever one is wired.
        // With no sandbox (the byte-identical single-node local default) it runs directly.
        async Task<SnapshotWorkResult> EvaluateAsync(CancellationToken token)
        {
            // Issue #245: plan the phases inside the sandbox's time budget, so a checkout too large to index in
            // time is published PARTIAL (what was not done recorded as coverage gaps) before the hard abort.
            var plan = sandbox?.TimeBudget is { } budget && budget > TimeSpan.Zero
                ? EvaluationTimePlan.Begin(_clock, budget)
                : null;
            if (plan is not null)
                log?.Invoke(plan.Describe());

            // Issue #113: a global.json pin hostfxr cannot satisfy (e.g. rollForward "disable" on an SDK band
            // this worker lacks) would fail the BuildHost before any project evaluates. Neutralize ONLY such
            // pins for the load, and put the committed bytes back before anything else reads the checkout —
            // coverage scan, EvaluationFingerprint, indexing — so the published checkout never diverges.
            var pinOverlay = _sdkPinGuard.Apply(checkoutDir, resolution.SelectedSolutions);
            pinState.Overlay = pinOverlay;
            MultiSolutionLoadResult load;
            PackageRestoreOutcome restore;
            try
            {
                // Restore BEFORE the load and while an unsatisfiable SDK pin is still neutralized, so the restore
                // resolves the same SDK the load will. A failed or partial restore never fails the job.
                restore = await _packageRestore.RunAsync(
                        checkoutDir, resolution.SelectedSolutions, plan?.RestoreLimit, token)
                    .ConfigureAwait(false);

                // Load the DETERMINISTIC selected solution set into ONE workspace (union of projects,
                // de-duplicated by project path/identity). A single selected solution keeps the byte-identical
                // whole-solution fast path; multiple solutions — an explicit list, or the no-config default
                // union of every discovered solution (#124) — aggregate into one repository snapshot (#109).
                // Under a time budget the union load stops opening projects at its deadline.
                load = await MultiSolutionLoader.LoadAsync(
                    resolution.SelectedSolutions, log,
                    deadline: plan?.LoadDeadline(_clock.GetUtcNow()), onProgress: log, cancellationToken: token)
                    .ConfigureAwait(false);
            }
            finally
            {
                pinOverlay.Restore();
            }
            pinState.LoadCompleted = true;

            if (pinOverlay.RestoreError is { } restoreError)
                throw SdkPinRestoreFailure(pinOverlay, restoreError);

            var pins = pinOverlay.Findings;

            // Guard (coordinator constraint / issue #90 parity): if EVERY project across the selected set was
            // skipped — e.g. an operator scoped this Linux worker to only platform (iOS/Android/Mac/WPF)
            // heads — the union carries no indexable content. Fail BEFORE indexing so an EMPTY snapshot is
            // never published and the branch pointer is never advanced to it. The single-solution loader has
            // the equivalent all-skipped guard (SolutionLoader.LoadSolutionResilientlyAsync); this gives the
            // multi-project path the same protection. The skip reasons ride along as diagnostics.
            if (!load.Solution.Projects.Any(p => p.Documents.Any()))
            {
                var sdkSkips = load.SkippedProjects.Count(s => HostFxrSdkResolutionError.TryParse(s.Reason, out _));
                var reason =
                    $"no project across the {resolution.SelectedSolutions.Count} selected solution(s) could be " +
                    $"loaded on this worker ({load.SkippedProjects.Count} project(s) skipped-with-reason" +
                    (sdkSkips > 0 ? $", {sdkSkips} of them because no .NET SDK could be resolved for them (see the sdk_resolution_failed diagnostics)" : string.Empty) +
                    "); nothing was indexed and no snapshot was published.";
                return SnapshotWorkResult.Failed(
                    reason,
                    BuildDiagnostics(checkoutDir, resolution, load, pins, InstalledSdks(pins, load: load), published: false));
            }

            // Coverage (issue #119) depends only on the selection, the load, and the checkout's file tree —
            // all known BEFORE indexing — so it rides on the snapshot context and is recorded in the SAME
            // transaction that publishes the snapshot. The same inventory yields each Phase-12 provider
            // subtree's own verdict (issue #162), recorded when the orchestrator publishes that provider.
            var inventory = SnapshotCoverageBuilder.Inventory.Scan(checkoutDir);
            var pinOverrides = pins.Where(p => p.OverrideApplied).Select(p => p.ToCoverageOverride()).ToList();
            var coverage = SnapshotCoverageBuilder.Build(checkoutDir, resolution, load, inventory, pinOverrides);
            coverage = coverage with { Coverage = SnapshotCoverageBuilder.WithNotes(coverage.Coverage, restore.Notes()) };
            var indexContext = context with
            {
                Coverage = coverage.Coverage,
                ProviderCoverage = SnapshotCoverageBuilder.BuildProviders(checkoutDir, resolution, load, inventory, pinOverrides),
                ProjectLoadIssues = restore.Projects.Count > 0 ? restore.ProjectLoadIssues() : null,
                TimeBudget = plan?.ForIndexing(checkoutDir, load.DeferredProjects)
            };

            var orchestrator = new IndexOrchestrator(
                database, log, configuration.DocumentExtractor,
                ExtractionParallelismOptions.FromConfiguration(configuration),
                IndexProfileDescriptor.FromConfiguration(configuration));

            await orchestrator.IndexSolutionAsync(
                load.Solution, progress: null, metrics: null, cancellationToken: token,
                snapshotContext: indexContext, solutionMembership: load.Membership).ConfigureAwait(false);

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
            return BuildResult(
                published.Id, checkoutDir, resolution, load, coverage with { Coverage = recorded },
                pins, InstalledSdks(pins, load: load), restore);
        }

        // A failed restore outranks every other outcome: the checkout no longer matches its commit, so the job
        // must report THAT (the journal is kept so the next job can repair the checkout before reusing it).
        SnapshotWorkResult Fail(string message, IReadOnlyList<ProjectOutcome>? diagnostics = null) =>
            pinState.Overlay is { RestoreError: { } restoreError } failedOverlay
                ? throw SdkPinRestoreFailure(failedOverlay, restoreError)
                : SnapshotWorkResult.Failed(message, diagnostics);

        try
        {
            return sandbox is not null
                ? await sandbox.RunAsync(checkoutDir, scratchDir, EvaluateAsync, cancellationToken).ConfigureAwait(false)
                : await EvaluateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SandboxLimitExceededException ex)
        {
            // A budget breach aborts the job cleanly — Failed, never a partial/complete publish. The diagnostics
            // record the aborting policy, so the service retries the identity once the policy changes (issue #245).
            return Fail(ex.Message, BudgetExceededDiagnostics(ex, sandbox));
        }
        catch (SandboxViolationException ex)
        {
            return Fail(ex.Message);
        }
        catch (TransientProvisioningException)
        {
            // A RETRYABLE provisioning failure must NOT be swallowed into a terminal Failed: the service
            // requeues the identity so a later ensure re-attempts it. This includes an SDK-pin restore failure
            // thrown from inside the evaluation (issue #113).
            throw;
        }
        catch (OperationCanceledException) when (pinState.Overlay is { RestoreError: { } restoreError } failedOverlay)
        {
            // Even a cancelled job must report a checkout it could not put back, typed (it is still requeued).
            throw SdkPinRestoreFailure(failedOverlay, restoreError);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (pinState.Overlay?.RestoreError is null
                                   && HostFxrSdkResolutionError.TryClassify(ex, out var sdkError))
        {
            // Issue #113 fallback: the whole load died on hostfxr SDK resolution (a pin the service could not
            // or was not allowed to override). Fail with a TYPED, actionable diagnostic — never a bare message.
            var pins = pinState.Overlay?.Findings ?? [];
            return SdkResolutionFailed(checkoutDir, sdkError, pins, InstalledSdks(pins, sdkError));
        }
        catch (Exception ex) when (!pinState.LoadCompleted
                                   && pinState.Overlay is { RestoreError: null } overlay
                                   && overlay.Findings.FirstOrDefault(f => !f.OverrideApplied) is { } unresolved)
        {
            // The load died with an error that does not itself name hostfxr (e.g. every project came back as an
            // empty stub), while a pin this load depends on is known NOT to resolve. Report that pin, typed,
            // alongside the load error, rather than a bare exception message.
            var pinError = new HostFxrSdkResolutionError
            {
                RequestedVersion = unresolved.RequestedVersion,
                GlobalJsonPath = unresolved.FullPath,
                InstalledSdks = unresolved.InstalledSdks
            };
            return SdkResolutionFailed(
                checkoutDir, pinError, overlay.Findings, InstalledSdks(overlay.Findings, pinError), loadError: ex.Message);
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    // What the evaluation (possibly running inside the sandbox) shares with the catch handlers around it: the
    // SDK-pin overlay once applied, and whether the load finished. A holder rather than captured locals, so the
    // handlers read the evaluation's writes.
    private sealed class SdkPinLoadState
    {
        public SdkPinOverlay? Overlay { get; set; }
        public bool LoadCompleted { get; set; }
    }

    /// <summary>The installed SDKs to report: from the pin findings when known, else a fresh probe.</summary>
    private IReadOnlyList<string> InstalledSdks(
        IReadOnlyList<SdkPinFinding> pins, HostFxrSdkResolutionError? error = null, MultiSolutionLoadResult? load = null)
    {
        var known = pins.Select(p => p.InstalledSdks).FirstOrDefault(l => l.Count > 0);
        if (known is not null)
            return known;
        var needed = pins.Count > 0 || error is not null
            || load?.SkippedProjects.Any(s => HostFxrSdkResolutionError.TryParse(s.Reason, out _)) == true;
        if (!needed)
            return [];
        var probed = _sdkPinGuard.ListInstalledSdks();
        return probed.Count > 0 ? probed : error?.InstalledSdks ?? [];
    }

    /// <summary>
    /// Turns a published snapshot + its checkout coverage into the terminal work result. The result is
    /// <see cref="SnapshotWorkResult.Partial"/> — never Complete — whenever the coverage verdict is partial
    /// (issue #119): a configured solution skipped, a declared project that failed to load (e.g. a platform
    /// head in the no-config default union, #124), a selected solution with no readable projects, zero loaded
    /// projects, an unpopulated submodule, (no-config default) an on-disk project file in no selected
    /// solution, or — defensively, since the default union selects every discovered solution — a discovered
    /// solution left unselected. The Partial reason carries every coverage reason, so a partial snapshot is
    /// never presented as complete.
    /// </summary>
    internal static SnapshotWorkResult BuildResult(
        long snapshotId, string checkoutDir, CheckoutResolution resolution, MultiSolutionLoadResult load,
        SnapshotCoverageBuilder.Result coverage,
        IReadOnlyList<SdkPinFinding>? sdkPins = null, IReadOnlyList<string>? installedSdks = null,
        PackageRestoreOutcome? restore = null)
    {
        var diagnostics = BuildDiagnostics(
            checkoutDir, resolution, load, sdkPins ?? [], installedSdks ?? [], published: true);
        diagnostics.AddRange(coverage.Diagnostics);
        if (restore is { Clean: false })
        {
            // Package ids and codes only: a raw restore message can name a package source URL.
            foreach (var issue in restore.Projects)
            {
                diagnostics.Add(new ProjectOutcome
                {
                    Severity = JobDiagnosticSeverity.Warning,
                    Code = PackageRestoreIncompleteCode,
                    ProjectPath = issue.Project,
                    Message = $"Project '{issue.Project}' did not restore fully ({issue.Describe()}); code that uses the missing packages may not bind."
                });
            }
            foreach (var note in restore.Notes())
            {
                diagnostics.Add(new ProjectOutcome
                {
                    Severity = JobDiagnosticSeverity.Warning,
                    Code = PackageRestoreIncompleteCode,
                    Message = note
                });
            }
        }

        if (coverage.Coverage.TimeBudget is { Exhausted: true } budget)
        {
            diagnostics.Add(new ProjectOutcome
            {
                Severity = JobDiagnosticSeverity.Warning,
                Code = TimeBudgetExhaustedCode,
                Message = TimeBudgetCoverageBuilder.Describe(budget, coverage.Coverage.SolutionsSelected)
            });
            foreach (var solution in budget.UnfinishedSolutions)
            {
                diagnostics.Add(new ProjectOutcome
                {
                    Severity = JobDiagnosticSeverity.Warning,
                    Code = SolutionDeferredCode,
                    ProjectPath = solution,
                    Message = $"Solution '{solution}' was not fully indexed before the time budget ran out."
                });
            }
        }

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
    /// honored rather than silently falling back to the default selection (issue #109).
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
        string checkoutDir, CheckoutResolution resolution, MultiSolutionLoadResult load,
        IReadOnlyList<SdkPinFinding> sdkPins, IReadOnlyList<string> installedSdks, bool published)
    {
        var diagnostics = SdkPinDiagnostics(sdkPins, published);

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

        // Discovered-but-unselected solutions (empty under the default union, #124) are coverage gaps;
        // SnapshotCoverageBuilder records one warning per solution (issue #119), so they are not repeated here.

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
            if (HostFxrSdkResolutionError.TryParse(skippedProject.Reason, out var sdkError))
            {
                // Issue #113: the project's directory is governed by a global.json pin hostfxr cannot satisfy.
                // A typed code + the pin details replace the raw BuildHost exception text.
                diagnostics.Add(new ProjectOutcome
                {
                    Severity = published ? JobDiagnosticSeverity.Warning : JobDiagnosticSeverity.Error,
                    Code = SdkResolutionFailedCode,
                    ProjectPath = RepoRelative(checkoutDir, skippedProject.ProjectPath),
                    Message =
                        $"Project '{skippedProject.ProjectName}' could not be loaded on this worker because " +
                        (sdkError.IsGlobalJsonPin
                            ? $"the .NET SDK its global.json pins is not installed: {SnapshotCoverageBuilder.DescribePin(checkoutDir, sdkError)} "
                            : "no compatible .NET SDK could be resolved (hostfxr reported no global.json pin) ") +
                        $"(installed SDK(s): {InstalledList(installedSdks.Count > 0 ? installedSdks : sdkError.InstalledSdks)})."
                });
                continue;
            }

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

    /// <summary>Diagnostic code: an unsatisfiable global.json SDK pin was neutralized for the load (issue #113).</summary>
    public const string SdkPinOverriddenCode = "sdk_pin_overridden";

    /// <summary>Diagnostic code: hostfxr could not resolve the SDK a global.json pins, and it was not overridden.</summary>
    public const string SdkResolutionFailedCode = "sdk_resolution_failed";

    /// <summary>Diagnostic code: the pre-load package restore could not restore everything (the job still publishes).</summary>
    public const string PackageRestoreIncompleteCode = "package_restore_incomplete";

    /// <summary>
    /// Diagnostic code: the evaluation's time budget ran out before the whole checkout was indexed, so the
    /// snapshot was published partial (issue #245). Its message is the coverage reason.
    /// </summary>
    public const string TimeBudgetExhaustedCode = "time_budget_exhausted";

    /// <summary>Diagnostic code: one selected solution the time budget left unfinished (issue #245).</summary>
    public const string SolutionDeferredCode = "solution_deferred";

    /// <summary>
    /// The diagnostics of a job the sandbox aborted: the error, and the aborting policy's token so the service
    /// can tell a stale abort from one under the current policy (<see cref="EvaluationBudgetPolicy"/>).
    /// </summary>
    internal static IReadOnlyList<ProjectOutcome> BudgetExceededDiagnostics(
        SandboxLimitExceededException exception, IEvaluationSandbox? sandbox)
    {
        var diagnostics = new List<ProjectOutcome>
        {
            new()
            {
                Severity = JobDiagnosticSeverity.Error,
                Code = EvaluationBudgetPolicy.ExceededCode,
                Message = exception.Message
            }
        };
        if ((exception.PolicyToken ?? sandbox?.BudgetPolicyToken) is { } token)
        {
            diagnostics.Add(new ProjectOutcome
            {
                Severity = JobDiagnosticSeverity.Info,
                Code = EvaluationBudgetPolicy.PolicyCode,
                Message = token
            });
        }
        return diagnostics;
    }

    /// <summary>
    /// Diagnostic code: a neutralized global.json could not be restored to its committed bytes (or a leftover
    /// restore journal could not be replayed). Carried by a <see cref="TransientProvisioningException"/>, so the
    /// service requeues the identity (bounded) instead of caching a terminal failure.
    /// </summary>
    public const string SdkPinRestoreFailedCode = "sdk_pin_restore_failed";

    /// <summary>
    /// One diagnostic per unsatisfiable <c>global.json</c> pin (issue #113): <c>sdk_pin_overridden</c>
    /// (warning — the snapshot was built with a substituted SDK) when it was neutralized, else
    /// <c>sdk_resolution_failed</c> with why it was not (warning when the job still published, error when not).
    /// </summary>
    internal static List<ProjectOutcome> SdkPinDiagnostics(IReadOnlyList<SdkPinFinding> pins, bool published)
    {
        var diagnostics = new List<ProjectOutcome>();
        foreach (var pin in pins)
        {
            var requested =
                $"global.json '{pin.GlobalJsonPath}' pins .NET SDK {pin.RequestedVersion ?? "(unspecified)"} " +
                $"(rollForward: {pin.RollForward ?? "default"}), which is not installed on this worker " +
                $"(installed SDK(s): {InstalledList(pin.InstalledSdks)})";
            diagnostics.Add(pin.OverrideApplied
                ? new ProjectOutcome
                {
                    Severity = JobDiagnosticSeverity.Warning,
                    Code = SdkPinOverriddenCode,
                    ProjectPath = pin.GlobalJsonPath,
                    Message =
                        $"{requested}. Override applied: the service temporarily removed the pin and evaluated the " +
                        $"checkout with installed SDK {pin.ResolvedSdkVersion ?? "(newest)"}; the committed " +
                        "global.json was restored before indexing. This snapshot was built with a substituted SDK."
                }
                : new ProjectOutcome
                {
                    Severity = published ? JobDiagnosticSeverity.Warning : JobDiagnosticSeverity.Error,
                    Code = SdkResolutionFailedCode,
                    ProjectPath = pin.GlobalJsonPath,
                    Message =
                        $"{requested}. Override not applied: {pin.NotOverriddenReason ?? "unknown reason"}. Install " +
                        "the pinned SDK on the worker or relax the pin's rollForward policy."
                });
        }
        return diagnostics;
    }

    /// <summary>
    /// The typed failure for a load that died on hostfxr SDK resolution (issue #113 fallback): the reason
    /// names the pin, the requested and installed SDKs and why the pin was not overridden, and the
    /// diagnostics carry a <c>sdk_resolution_failed</c> error — never a bare exception message.
    /// </summary>
    internal static SnapshotWorkResult SdkResolutionFailed(
        string checkoutDir, HostFxrSdkResolutionError error, IReadOnlyList<SdkPinFinding> pins,
        IReadOnlyList<string> installedSdks, string? loadError = null)
    {
        var diagnostics = SdkPinDiagnostics(pins, published: false);
        var installed = installedSdks.Count > 0 ? installedSdks : error.InstalledSdks;

        // hostfxr also fails SDK resolution with no pin at all (no SDK installed); never blame a global.json then.
        if (!error.IsGlobalJsonPin && pins.Count == 0)
        {
            diagnostics.Add(new ProjectOutcome
            {
                Severity = JobDiagnosticSeverity.Error,
                Code = SdkResolutionFailedCode,
                Message =
                    "No compatible .NET SDK could be resolved on this worker and hostfxr reported no global.json pin " +
                    $"(installed SDK(s): {InstalledList(installed)}). Install a .NET SDK on the worker."
            });
            return SnapshotWorkResult.Failed(
                "no compatible .NET SDK could be resolved on this worker (hostfxr reported no global.json pin); " +
                $"installed SDK(s): {InstalledList(installed)}. Nothing was indexed and no snapshot was published.",
                diagnostics);
        }

        var matching = error.GlobalJsonPath is { } failedPath
            ? pins.FirstOrDefault(p => GlobalJsonLocator.PathComparer.Equals(
                Path.GetFullPath(p.FullPath), Path.GetFullPath(failedPath)))
            : null;

        if (matching is null)
        {
            diagnostics.Add(new ProjectOutcome
            {
                Severity = JobDiagnosticSeverity.Error,
                Code = SdkResolutionFailedCode,
                ProjectPath = error.GlobalJsonPath is { } p ? RepoRelative(checkoutDir, p).Replace('\\', '/') : null,
                Message =
                    $"The .NET SDK could not be resolved on this worker: {SnapshotCoverageBuilder.DescribePin(checkoutDir, error)} " +
                    $"(installed SDK(s): {InstalledList(installed)}). Install the pinned SDK on the worker or relax " +
                    "the pin's rollForward policy."
            });
        }

        var why = matching switch
        {
            { OverrideApplied: false, NotOverriddenReason: { } notOverridden } => $" The pin was not overridden: {notOverridden}.",
            { OverrideApplied: true } => " The pin was overridden, but the load still failed SDK resolution.",
            _ => string.Empty
        };
        var reason =
            (loadError is null
                ? "the .NET SDK required by the checkout's global.json could not be resolved on this worker: "
                : $"the checkout could not be loaded ({ScrubCheckoutRoot(checkoutDir, loadError)}) and the .NET SDK " +
                  "required by its global.json could not be resolved on this worker: ") +
            $"{SnapshotCoverageBuilder.DescribePin(checkoutDir, error)}" +
            (matching?.RollForward is { } rollForward ? $" (rollForward: {rollForward})" : string.Empty) +
            $"; installed SDK(s): {InstalledList(installed)}.{why} Nothing was indexed and no snapshot was published.";
        return SnapshotWorkResult.Failed(reason, diagnostics);
    }

    // A checkout that could not be returned to its committed state is a CHECKOUT-STATE problem that an operator
    // repair resolves, not a property of the commit. It is therefore a typed, bounded-retry provisioning failure
    // (the service requeues the identity under the sdk_pin_restore_failed code) — never a terminal Failed that
    // would poison the identity for every later ensure, even after the checkout is repaired.
    private static TransientProvisioningException SdkPinRestoreFailure(string detail) => new(
        "the service neutralized an unsatisfiable global.json SDK pin for the MSBuild load but could not restore the " +
        $"committed file afterwards ({detail}), so the checkout was NOT indexed (it no longer matches its commit). The " +
        "restore journal was kept: restore the committed global.json (or delete the checkout so it is re-cloned) and " +
        "the next ensure retries.",
        diagnosticCode: SdkPinRestoreFailedCode);

    private static TransientProvisioningException SdkPinRestoreFailure(SdkPinOverlay overlay, string restoreError) =>
        SdkPinRestoreFailure(ScrubCheckoutRoot(overlay.CheckoutDir, restoreError));

    private static string InstalledList(IReadOnlyList<string> installed) =>
        installed.Count > 0 ? string.Join(", ", installed) : "unknown";

    // Operator-facing text never names the worker's volume layout: the checkout root becomes ".".
    private static string ScrubCheckoutRoot(string checkoutDir, string text) =>
        text.Replace(Path.TrimEndingDirectorySeparator(checkoutDir), ".", StringComparison.OrdinalIgnoreCase)
            .Replace('\\', '/');

    private static string SourceLabel(SolutionSelectionSource source) => source switch
    {
        SolutionSelectionSource.Configured => "configured (sextant.json)",
        SolutionSelectionSource.DefaultUnion => "default union of all discovered solutions, no config",
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
    /// real checkout/MSBuild. <paramref name="sdkPinPolicy"/> is the guard's non-default SDK-pin identity
    /// component (issue #113); the orchestrator folds it into the identity of every snapshot it publishes.
    /// <paramref name="restorePolicy"/> is the restore runner's identity component, folded the same way.
    /// </summary>
    internal static SnapshotContext CreateSnapshotContext(
        EnsureSnapshotRequest request, WorkerCapability? capability, string? sdkPinPolicy = null,
        string? restorePolicy = null) => new()
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
        BranchHeadSequence = request.BranchHeadSequence,
        // SVC-6/7 branch guards: the head CAS and `branch_update: none`. Null when absent, so an unguarded
        // ensure advances exactly as before.
        ExpectedHeadCommit = request.ExpectedHeadCommit,
        SuppressBranchUpdate = request.SuppressesBranchUpdate ? true : null,
        // Issue #199: a caller that may not pick the default gets the first-branch default only for the remote's own
        // default branch, as the service verified it. Null for every other ensure, so its advance is unchanged.
        AllowImplicitDefault = request.RestrictsImplicitDefault
            ? request.AllowsImplicitDefault(request.BranchName ?? "main")
            : null,
        // Issue #113: must equal the service's ServiceOptions.SdkPinIdentityComponent, or the service's
        // ValidateWorkerResult fails the job closed (published identity != requested identity).
        SdkPinPolicy = sdkPinPolicy,
        // Same contract for the package-restore toggle (ServiceOptions.RestoreIdentityComponent).
        RestorePolicy = restorePolicy
    };
}
