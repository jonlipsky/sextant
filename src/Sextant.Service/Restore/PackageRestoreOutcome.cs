namespace Sextant.Service.Restore;

/// <summary>One restore process of a job (issue #272).</summary>
public sealed record RestorePassTiming
{
    /// <summary><c>solution</c> (whole-solution restore), <c>union</c> (traversal over the project union), <c>partial</c> (a solution's uncovered projects only) or <c>retry</c> (per-project retry of a failed solution).</summary>
    public required string Kind { get; init; }

    /// <summary>The solution file name, or <c>union</c> for the project union. Never a path.</summary>
    public required string Target { get; init; }

    /// <summary>Projects the pass was asked to restore.</summary>
    public int Projects { get; init; }

    /// <summary>
    /// APPROXIMATE count of projects whose assets file the pass left untouched (NuGet's up-to-date check passed, so the
    /// pass only evaluated them). Estimated from the file's last-write time at the default <c>obj/</c> location (older
    /// than the pass start minus one second), so a project with a custom intermediate path reads as not up to date.
    /// Telemetry only; nothing decides on it.
    /// </summary>
    public int ProjectsUpToDateApprox { get; init; }

    /// <summary>Wall time of the process, in milliseconds.</summary>
    public long WallMs { get; init; }

    /// <summary>True when the pass ran with static-graph evaluation.</summary>
    public bool StaticGraph { get; init; }

    /// <summary><c>succeeded</c>, <c>failed</c>, <c>timed_out</c> or <c>not_started</c>.</summary>
    public required string Result { get; init; }
}

/// <summary>A multi-target project restored for its buildable targets only, after its platform targets failed (issue #352).</summary>
/// <param name="Project">The project file relative to the checkout (forward slashes).</param>
/// <param name="Restored">The <c>;</c>-joined targets it was restored for.</param>
/// <param name="LeftOut">The declared targets the repair left out (platform targets this worker cannot build).</param>
public sealed record PlatformRepair(string Project, string Restored, IReadOnlyList<string> LeftOut);

/// <summary>One project's restore failure, as reduced by <see cref="RestoreOutputParser"/>.</summary>
public sealed record PackageRestoreProjectIssue
{
    /// <summary>The project file relative to the checkout (forward slashes), or its file name when outside it.</summary>
    public required string Project { get; init; }

    /// <summary>The NuGet error codes reported for the project, ordered.</summary>
    public IReadOnlyList<string> Codes { get; init; } = [];

    /// <summary>The package ids no source provided, ordered.</summary>
    public IReadOnlyList<string> MissingPackages { get; init; } = [];

    /// <summary>A configured package source could not be reached while restoring the project.</summary>
    public bool SourceUnreachable { get; init; }

    /// <summary>The first message of each code, sanitized (no URL, no checkout path, at most <see cref="RestoreErrorClassifier.MaxMessageLength"/> characters), keyed by code (issue #349).</summary>
    public IReadOnlyDictionary<string, string> Messages { get; init; } = new Dictionary<string, string>();

    /// <summary>A message of this project's errors reads like a network or server failure (issue #349).</summary>
    public bool TransientMessage { get; init; }

    /// <summary>
    /// True when the same restore inputs fail the same way every time (issue #349): every code is a deterministic one
    /// (<see cref="RestoreErrorClassifier.IsDeterministicCode"/>), no source was unreachable and no message reads like a
    /// network failure. A project with no recognised code is not deterministic.
    /// </summary>
    public bool Deterministic => Codes.Count > 0 && !SourceUnreachable && !TransientMessage && Codes.All(RestoreErrorClassifier.IsDeterministicCode);

    /// <summary>A one-line description for <see cref="Sextant.Core.ProjectBindingHealth.LoadIssue"/>.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (MissingPackages.Count > 0)
            parts.Add($"package(s) not found: {PackageRestoreOutcome.JoinCapped(MissingPackages, 5)}");
        if (SourceUnreachable)
            parts.Add("a package source was unreachable");
        var otherCodes = Codes
            .Where(c => c is not ("NU1101" or "NU1102" or "NU1103") && !RestoreOutputParser.SourceUnreachableCodes.Contains(c))
            .ToList();
        if (otherCodes.Count > 0 || parts.Count == 0)
            parts.Add($"errors {PackageRestoreOutcome.JoinCapped(otherCodes.Count > 0 ? otherCodes : Codes, 5)}");
        return "restore: " + string.Join("; ", parts);
    }
}

/// <summary>
/// What the worker's <c>dotnet restore</c> step achieved for one job. A failed or partial restore never fails
/// the job; it is recorded as coverage notes and per-project load issues so an agent can tell why code that
/// uses a package did not bind.
/// </summary>
public sealed record PackageRestoreOutcome
{
    /// <summary>The outcome when restore is disabled on this node.</summary>
    public static PackageRestoreOutcome Disabled { get; } = new() { Enabled = false };

    /// <summary>Restore is enabled on this node.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Solutions the restore was started for.</summary>
    public int SolutionsAttempted { get; init; }

    /// <summary>Solutions selected for this worker's restore.</summary>
    public int SolutionsSelected { get; init; }

    /// <summary>Solutions whose restore exited 0.</summary>
    public int SolutionsSucceeded { get; init; }

    /// <summary>Solutions the restore could not start for (no <c>dotnet</c> on the path, ...).</summary>
    public int SolutionsNotStarted { get; init; }

    /// <summary>The restore step ran out of time; the running restore was killed and later solutions were skipped.</summary>
    public bool TimedOut { get; init; }

    /// <summary>The bound the restore step ran under.</summary>
    public TimeSpan Timeout { get; init; }

    /// <summary>Wall-clock time the restore step took.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>
    /// Solutions whose restore failed as a whole and were retried project by project (issue #296), so one project's
    /// restore-graph error does not leave the others unrestored.
    /// </summary>
    public int SolutionsRetriedPerProject { get; init; }

    /// <summary>Projects the per-project retries restored one by one.</summary>
    public int ProjectsRetried { get; init; }

    /// <summary>Projects whose stale-lock-file (NU1004) failure a second restore without the locked mode resolved (issue #352).</summary>
    public IReadOnlyList<string> LockFileRetriedProjects { get; init; } = [];

    /// <summary>Projects whose only failure was a package downgrade (NU1605) raised to an error, restored again with it a warning (issue #352).</summary>
    public IReadOnlyList<string> DowngradeRetriedProjects { get; init; } = [];

    /// <summary>Multi-target projects restored for their buildable targets after the platform targets failed, with those targets (issue #352).</summary>
    public IReadOnlyList<string> PlatformRetriedProjects { get; init; } = [];

    /// <summary>
    /// The same repairs as <see cref="PlatformRetriedProjects"/>, structured: the checkout-relative project, the targets
    /// it was restored for and the platform targets left out, so coverage can keep those versions excluded (issue #352).
    /// </summary>
    public IReadOnlyList<PlatformRepair> PlatformRepairs { get; init; } = [];

    /// <summary>Checkout-relative paths of every project a repair restored with an unlocked lock file or reduced targets (issue #352): restored, but not cleanly.</summary>
    public IReadOnlyList<string> RepairedProjects { get; init; } = [];

    /// <summary>Repair candidates the restore time limit left unrepaired (issue #352).</summary>
    public int RepairNotAttempted { get; init; }

    /// <summary>Per-solution restore passes run with static-graph evaluation (issue #272).</summary>
    public int StaticGraphPasses { get; init; }

    /// <summary>Static-graph passes that failed as a whole and were re-run once without it (issue #272).</summary>
    public int StaticGraphFallbacks { get; init; }

    /// <summary>
    /// Selected solutions the per-solution fallback did not restore because every project they declare was already
    /// restored cleanly by an earlier (broader) solution of the same job (issue #290).
    /// </summary>
    public int SolutionsSkippedRedundant { get; init; }

    /// <summary>File names of the solutions counted in <see cref="SolutionsSkippedRedundant"/>.</summary>
    public IReadOnlyList<string> SkippedSolutionNames { get; init; } = [];

    /// <summary>
    /// Solutions the per-solution fallback restored only for the projects an earlier same-directory solution had not
    /// covered, through the per-project traversal instead of a whole-solution restore (issue #290).
    /// </summary>
    public int SolutionsPartiallySkipped { get; init; }

    /// <summary>Projects restored that way (the uncovered ones), summed over those solutions.</summary>
    public int ProjectsRestoredPartially { get; init; }

    /// <summary>File names of the solutions counted in <see cref="SolutionsPartiallySkipped"/>.</summary>
    public IReadOnlyList<string> PartiallySkippedSolutionNames { get; init; } = [];

    /// <summary>Per-solution passes that did not try static-graph evaluation because the restore state remembers it failing for this solution and fingerprint (issue #349).</summary>
    public int StaticGraphSkippedByMemo { get; init; }

    /// <summary>Selected solutions not restored (or restored only for their remaining projects) because the replayed state already covers their projects (issue #349).</summary>
    public int SolutionsCoveredByReplay { get; init; }

    /// <summary>
    /// The restore error codes of this job, most affected projects first, at most <paramref name="max"/> (issue #349).
    /// <see cref="RestoreErrorSummary.Deterministic"/> is true only when every project that reported the code is deterministic.
    /// </summary>
    public IReadOnlyList<RestoreErrorSummary> ErrorCodes(int max = 8)
    {
        var byCode = new Dictionary<string, (int Projects, bool Deterministic, string? Message)>(StringComparer.Ordinal);
        foreach (var project in Projects)
        {
            foreach (var code in project.Codes)
            {
                project.Messages.TryGetValue(code, out var message);
                byCode[code] = byCode.TryGetValue(code, out var seen)
                    ? (seen.Projects + 1, seen.Deterministic && project.Deterministic, seen.Message ?? message)
                    : (1, project.Deterministic, message);
            }
        }
        foreach (var code in GeneralCodes)
            if (!byCode.ContainsKey(code))
                byCode[code] = (0, false, null);
        return [.. byCode
            .OrderByDescending(e => e.Value.Projects).ThenBy(e => e.Key, StringComparer.Ordinal)
            .Take(max)
            .Select(e => new RestoreErrorSummary(e.Key, e.Value.Projects, e.Value.Deterministic, e.Value.Message))];
    }

    /// <summary>True when any project's failure is not provably deterministic (transient, an unreachable source, or an unrecognised code), so it must never be cached as failing.</summary>
    public bool HasNonDeterministicFailure => Projects.Any(p => !p.Deterministic) || GeneralCodes.Count > 0 || SourceUnreachableGeneral;

    /// <summary>One Information line: the top restore error codes with counts (issue #349); null when none were reported.</summary>
    public string? ErrorSummaryLine(int max = 8)
    {
        var codes = ErrorCodes(max);
        if (codes.Count == 0)
            return null;
        var text = new System.Text.StringBuilder("package restore errors: ")
            .Append(Projects.Count).Append(" project(s) failed");
        if (HasNonDeterministicFailure)
            text.Append(" (not provably deterministic: never cached as failing)");
        text.Append("; ");
        text.Append(string.Join("; ", codes.Select(c =>
            $"{c.Code} x{c.Projects}{(c.Deterministic ? string.Empty : " transient_or_unknown")}{(string.IsNullOrEmpty(c.Message) ? string.Empty : ": " + c.Message)}")));
        return text.Append('.').ToString();
    }

    /// <summary>Every restore process this job ran, in order (issue #272): where the restore time went.</summary>
    public IReadOnlyList<RestorePassTiming> Passes { get; init; } = [];

    /// <summary>What the restore-state cache did (issue #272); null when it was not in play (no persistent package folder).</summary>
    public RestoreReuseOutcome? Reuse { get; init; }

    /// <summary>The one log line describing this job's restore: mode, wall time, cache decision and every pass.</summary>
    public string Summary()
    {
        if (!Enabled)
            return "package restore summary: off.";
        var mode = Reuse is { Decision: "reused" } ? "reused"
            : UsedProjectUnion ? "union" : SolutionsSelected > 1 ? "per_solution" : "solution";
        var text = new System.Text.StringBuilder("package restore summary: ")
            .Append("mode=").Append(mode)
            .Append(" wall_ms=").Append((long)Elapsed.TotalMilliseconds)
            .Append(" timed_out=").Append(TimedOut ? "true" : "false")
            .Append(" passes=").Append(Passes.Count);
        if (StaticGraphSkippedByMemo > 0)
            text.Append(" static_graph_skipped_by_memo=").Append(StaticGraphSkippedByMemo);
        if (Projects.Count > 0)
            text.Append(" projects_failed=").Append(Projects.Count)
                .Append(" error_codes=").Append(string.Join(',', ErrorCodes(5).Select(c => c.Code + "x" + c.Projects)));
        if (Reuse is { } reuse)
        {
            text.Append(" cache=").Append(reuse.Decision);
            if (reuse.Reason is not null)
                text.Append('(').Append(reuse.Reason).Append(')');
            text.Append(" cache_projects=").Append(reuse.Projects)
                .Append(" fingerprint_ms=").Append(reuse.FingerprintMs)
                .Append(" state_ms=").Append(reuse.StateMs);
            if (reuse.ProjectsRemaining > 0)
                text.Append(" cache_projects_remaining=").Append(reuse.ProjectsRemaining);
            if (reuse.Saved)
                text.Append(reuse.ProjectsRemaining > 0 ? " saved=partial" : " saved=true");
            else if (reuse.SaveSkipped is not null)
                text.Append(" save_skipped=").Append(reuse.SaveSkipped);
        }
        foreach (var pass in Passes)
        {
            text.Append(" [").Append(pass.Kind).Append(' ').Append(pass.Target).Append(" projects=").Append(pass.Projects)
                .Append(" up_to_date_approx=").Append(pass.ProjectsUpToDateApprox).Append(" ms=").Append(pass.WallMs)
                .Append(pass.StaticGraph ? " static_graph" : string.Empty).Append(' ').Append(pass.Result).Append(']');
        }
        return text.Append('.').ToString();
    }

    /// <summary>Distinct projects explicitly restored by a project-union traversal.</summary>
    public int ProjectsAttempted { get; init; }

    /// <summary>True when one traversal restored a deduplicated project union.</summary>
    public bool UsedProjectUnion { get; init; }

    /// <summary>
    /// Why several selected solutions were restored one by one instead of as one project union (issue #272), e.g.
    /// "'All.slnx' declares solution-specific configuration"; null when the union was used or only one solution was
    /// selected. Names only solution and project file names, never a path outside them or a source URL.
    /// </summary>
    public string? UnionFallbackReason { get; init; }

    /// <summary>Per-project failures, ordered by project path.</summary>
    public IReadOnlyList<PackageRestoreProjectIssue> Projects { get; init; } = [];

    /// <summary>Projects with errors beyond <see cref="RestoreOutputParser.MaxProjects"/>.</summary>
    public int ProjectsDropped { get; init; }

    /// <summary>Error codes that named no project.</summary>
    public IReadOnlyList<string> GeneralCodes { get; init; } = [];

    /// <summary>A package source could not be reached, reported on a line that named no project.</summary>
    public bool SourceUnreachableGeneral { get; init; }

    /// <summary>
    /// Package source credentials are configured but could not be handed to this restore (issue #231), so sources
    /// that need them were restored without them.
    /// </summary>
    public bool CredentialsUnavailable { get; init; }

    /// <summary>
    /// Solutions whose restore ran to completion within the bound but exited non-zero (the one a timeout stopped
    /// is not counted here; <see cref="TimedOut"/> reports it).
    /// </summary>
    public int SolutionsFailed => Math.Max(0, SolutionsAttempted - SolutionsSucceeded - SolutionsNotStarted - (TimedOut ? 1 : 0));

    /// <summary>
    /// Of <see cref="SolutionsFailed"/>, the solutions whose own failed restore reported at least one project error
    /// (issue #351). A failed solution that reported none cannot be explained by project exclusions; zero (the
    /// default) therefore claims none is explained.
    /// </summary>
    public int SolutionsFailedAttributed { get; init; }

    /// <summary>True when every attempted restore exited 0 within the bound, or restore is disabled.</summary>
    public bool Clean => !Enabled
        || (!TimedOut && SolutionsNotStarted == 0 && SolutionsSucceeded == SolutionsAttempted
            && Projects.Count == 0 && ProjectsDropped == 0 && GeneralCodes.Count == 0 && !SourceUnreachableGeneral
            && !CredentialsUnavailable);

    /// <summary>
    /// The coverage notes describing what the restore could not do. Each names counts, package ids and codes;
    /// never a raw message or a package source URL.
    /// </summary>
    public IReadOnlyList<string> Notes()
    {
        var notes = new List<string>();
        if (!Enabled)
            return notes;

        if (CredentialsUnavailable)
        {
            notes.Add("Package source credentials are configured but could not be provided to the package restore; " +
                      "packages from sources that need them may not have been restored.");
        }
        if (SolutionsRetriedPerProject > 0)
        {
            notes.Add(
                $"Package restore failed as a whole for {SolutionsRetriedPerProject} solution(s) and was retried for their " +
                $"{ProjectsRetried} project(s) one by one, so a project that cannot restore does not leave the others unrestored.");
        }
        if (LockFileRetriedProjects.Count > 0)
        {
            notes.Add(
                $"Package restore of {LockFileRetriedProjects.Count} project(s) failed with NU1004 because the committed lock file " +
                $"is out of date; they were restored again without the locked mode (the lock file in the checkout is untouched): " +
                string.Join(", ", LockFileRetriedProjects.Take(5)) + (LockFileRetriedProjects.Count > 5 ? ", ..." : string.Empty) + ".");
        }
        if (DowngradeRetriedProjects.Count > 0)
        {
            notes.Add(
                $"Package restore of {DowngradeRetriedProjects.Count} project(s) failed only on a package downgrade (NU1605) the repository " +
                $"treats as an error; they were restored again with it as a warning (the checkout is untouched): " +
                string.Join(", ", DowngradeRetriedProjects.Take(5)) + (DowngradeRetriedProjects.Count > 5 ? ", ..." : string.Empty) + ".");
        }
        if (PlatformRetriedProjects.Count > 0)
        {
            notes.Add(
                $"{PlatformRetriedProjects.Count} multi-target project(s) could not restore their platform targets on this worker; " +
                $"they were restored for their buildable targets only: " +
                string.Join(", ", PlatformRetriedProjects.Take(5)) + (PlatformRetriedProjects.Count > 5 ? ", ..." : string.Empty) + ".");
        }
        if (RepairNotAttempted > 0)
        {
            notes.Add(
                $"The restore time limit cut the repair short: {RepairNotAttempted} project(s) that failed on a stale lock file, " +
                "a package downgrade or an unavailable platform target were not restored again, so they stay unrestored.");
        }
        if (SolutionsNotStarted > 0)
        {
            notes.Add(UsedProjectUnion
                ? $"The union package restore could not be started, so {ProjectsAttempted} distinct project(s) from " +
                  $"{SolutionsSelected} selected solution(s) were loaded without restored packages and code that uses packages may not bind."
                : $"Package restore could not be started for {SolutionsNotStarted} solution(s), so their projects were " +
                  "loaded without restored packages and code that uses packages may not bind.");
        }
        if (TimedOut)
        {
            notes.Add(UsedProjectUnion
                ? $"The union package restore did not finish within {Timeout.TotalSeconds:0}s and was stopped; " +
                  $"some of its {ProjectsAttempted} distinct project(s) from {SolutionsSelected} selected solution(s) " +
                  "may have been loaded without restored packages, so code in them may not bind."
                : $"Package restore did not finish within {Timeout.TotalSeconds:0}s and was stopped; projects it had " +
                  "not restored were loaded without their packages, so code in them may not bind.");
        }

        var missing = Projects.SelectMany(p => p.MissingPackages).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (missing.Count > 0)
        {
            var projects = Projects.Count(p => p.MissingPackages.Count > 0);
            notes.Add(
                $"Package restore could not find {missing.Count} package(s) ({JoinCapped(missing, 5)}) for " +
                $"{projects} project(s); code that uses them may not bind.");
        }

        var unreachable = Projects.Count(p => p.SourceUnreachable);
        if (unreachable > 0)
        {
            notes.Add(
                $"A configured package source could not be reached while restoring {unreachable} project(s); " +
                "packages only that source provides were not restored.");
        }
        else if (SourceUnreachableGeneral)
        {
            notes.Add(
                "A configured package source could not be reached during package restore; packages only that " +
                "source provides were not restored.");
        }

        var other = Projects.Where(p => p.MissingPackages.Count == 0 && !p.SourceUnreachable).ToList();
        if (other.Count > 0 || ProjectsDropped > 0 || GeneralCodes.Count > 0)
        {
            var codes = other.SelectMany(p => p.Codes).Concat(GeneralCodes).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToList();
            var count = other.Count + ProjectsDropped;
            notes.Add(
                count > 0
                    ? $"Package restore reported errors ({JoinCapped(codes, 5)}) for {count} project(s)."
                    : $"Package restore reported errors ({JoinCapped(codes, 5)}).");
        }

        // A restore that exited non-zero without one recognizable error line (a host crash, an unhandled
        // exception) must still say so, or the binding failures it causes would show up with no stated cause.
        if (SolutionsFailed > 0 && Projects.Count == 0 && ProjectsDropped == 0 && GeneralCodes.Count == 0
            && !SourceUnreachableGeneral)
        {
            notes.Add(UsedProjectUnion
                ? $"The union package restore failed without a recognized error code; its {ProjectsAttempted} distinct " +
                  $"project(s) from {SolutionsSelected} selected solution(s) may have been loaded without restored packages."
                : $"Package restore failed for {SolutionsFailed} solution(s) without a recognized error code; their " +
                  "projects may have been loaded without restored packages, so code in them may not bind.");
        }
        return notes;
    }

    /// <summary>The per-project load issues, keyed by checkout-relative project path.</summary>
    public IReadOnlyDictionary<string, string> ProjectLoadIssues()
        => Projects.ToDictionary(p => p.Project, p => p.Describe(), StringComparer.Ordinal);

    internal static string JoinCapped(IReadOnlyList<string> values, int cap)
        => values.Count <= cap
            ? string.Join(", ", values)
            : string.Join(", ", values.Take(cap)) + $", +{values.Count - cap} more";
}

/// <summary>One restore error code of a job (issue #349): how many projects reported it, whether it reproduces for the same inputs, and a sanitized sample message.</summary>
public sealed record RestoreErrorSummary(string Code, int Projects, bool Deterministic, string? Message);
