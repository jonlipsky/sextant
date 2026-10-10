namespace Sextant.Service.Restore;

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
