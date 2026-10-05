using System.Text.Json.Serialization;

namespace Sextant.Core;

/// <summary>The coverage verdicts a <see cref="SnapshotCoverage"/> can carry (migration 022 <c>snapshot_coverage.verdict</c>).</summary>
public static class SnapshotCoverageVerdict
{
    /// <summary>Every discovered solution, declared project, and declared submodule of the checkout was indexed.</summary>
    public const string Complete = "complete";

    /// <summary>At least one coverage gap exists; <see cref="SnapshotCoverage.Reasons"/> says which.</summary>
    public const string Partial = "partial";
}

/// <summary>
/// A machine-readable summary of how much of a checkout a published snapshot actually covers (issue #119).
/// A snapshot row's <c>complete</c> status only means "published and servable"; this record says whether
/// the published data covers the whole repository or only part of it, and why. It is computed by the
/// service worker from the solution selection and the multi-solution load BEFORE indexing, persisted in the
/// SAME transaction that publishes the snapshot (migration 022), and surfaced on ensure/status/resolve, on
/// the snapshot symbol page, and in MCP <c>meta.snapshot</c>. Serialized snake_case.
/// </summary>
public sealed record SnapshotCoverage
{
    /// <summary><see cref="SnapshotCoverageVerdict.Complete"/> or <see cref="SnapshotCoverageVerdict.Partial"/>.</summary>
    public required string Verdict { get; init; }

    /// <summary>One human-readable reason per coverage gap; empty when <see cref="Verdict"/> is complete.</summary>
    public IReadOnlyList<string> Reasons { get; init; } = [];

    /// <summary>
    /// How the solution set was chosen: <c>configured</c>, <c>default_union</c> (no config: every discovered
    /// solution, issue #124), or <c>none</c>. Rows recorded before #124 may carry the retired single-solution
    /// value <c>default_root</c>. A Phase-12 PROVIDER snapshot (issue #162) carries the indexing parent's
    /// source when at least one of the provider's OWN solutions was selected, or <c>parent_selection</c> when
    /// none was, i.e. the provider was built only from projects the parent's selection reached.
    /// </summary>
    public string? SelectionSource { get; init; }

    /// <summary>Solutions found on disk (default selection) or listed in <c>sextant.json</c> (configured selection).</summary>
    public int SolutionsDiscovered { get; init; }

    /// <summary>Solutions actually indexed.</summary>
    public int SolutionsSelected { get; init; }

    /// <summary>
    /// Solutions discovered on disk that the selection left out. Always 0 under <c>default_union</c> (every
    /// discovered solution is selected) and <c>configured</c> (no discovery); non-zero only on pre-#124
    /// <c>default_root</c> rows.
    /// </summary>
    public int SolutionsNotSelected { get; init; }

    /// <summary>Configured solutions that could not be used (missing, invalid, outside the checkout).</summary>
    public int SolutionsSkipped { get; init; }

    /// <summary>Distinct project files declared across the selected solutions.</summary>
    public int ProjectsDeclared { get; init; }

    /// <summary>
    /// Distinct project files in the loaded workspace — declared projects that loaded plus any the load
    /// pulled in through <c>ProjectReference</c> (so it can exceed <see cref="ProjectsDeclared"/>).
    /// </summary>
    public int ProjectsLoaded { get; init; }

    /// <summary>Declared project files that failed to load on this worker.</summary>
    public int ProjectsSkipped { get; init; }

    /// <summary>Project files (<c>.csproj</c>/<c>.fsproj</c>/<c>.vbproj</c>) found on disk, excluding build output.</summary>
    public int ProjectFilesOnDisk { get; init; }

    /// <summary>On-disk project files that no selected solution declared and the load did not pull in.</summary>
    public int ProjectFilesUnreferenced { get; init; }

    /// <summary>Submodules declared by <c>.gitmodules</c> (recursively, through populated submodules).</summary>
    public int SubmodulesDeclared { get; init; }

    /// <summary>Declared submodules that are not checked out (no git worktree at the declared path).</summary>
    public int SubmodulesUnpopulated { get; init; }

    /// <summary>
    /// Parts of the checkout the coverage scan could not inspect (unreadable directories or
    /// <c>.gitmodules</c>, entries escaping the checkout). Any error makes the verdict partial, because the
    /// scan cannot prove nothing was missed.
    /// </summary>
    public int ScanErrors { get; init; }

    /// <summary>
    /// The unsatisfiable <c>global.json</c> SDK pins the worker temporarily neutralized so the checkout could
    /// be evaluated with an installed SDK (issue #113). Null when no pin was overridden (omitted from the
    /// JSON, so records written before #113 read back unchanged). An override does NOT make the verdict
    /// partial — every project still loaded — but it is provenance an operator must be able to see: the
    /// snapshot was built with a substituted SDK.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<SdkPinOverride>? SdkPinOverrides { get; init; }

    /// <summary>
    /// How well the indexed code BOUND: how many names and invocations the compiler could not resolve, per
    /// project, and which projects are degraded enough that references or calls inside them may be missing. A
    /// degraded project makes the verdict partial and the reason names it. Null when the producer recorded no
    /// binding health (rows written by an older service, and the local CLI/daemon); omitted from the JSON.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BindingHealth? Binding { get; init; }

    /// <summary>
    /// Facts about the checkout that do NOT make the verdict partial but an operator or agent may want to
    /// know, for example project files that no selected solution declares, or packages the restore could not
    /// resolve. Null when there are none; omitted from the JSON.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Notes { get; init; }

    /// <summary>
    /// What the service worker left out because its evaluation time budget ran out: projects not loaded, not
    /// indexed, or indexed without relationships, references, calls and comments, and the selected solutions they
    /// leave unfinished. Any entry makes the verdict partial. Null when the budget did not run out (and on every
    /// row written before it existed); omitted from the JSON.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TimeBudgetCoverage? TimeBudget { get; init; }

    /// <summary>True when <see cref="Verdict"/> is <see cref="SnapshotCoverageVerdict.Partial"/>.</summary>
    [JsonIgnore]
    public bool IsPartial => string.Equals(Verdict, SnapshotCoverageVerdict.Partial, StringComparison.Ordinal);
}

/// <summary>
/// The part of a checkout a time-budgeted evaluation left out (<see cref="SnapshotCoverage.TimeBudget"/>).
/// Serialized snake_case.
/// </summary>
public sealed record TimeBudgetCoverage
{
    /// <summary>The cap on <see cref="UnfinishedSolutions"/>.</summary>
    public const int MaxSolutions = 100;

    /// <summary>The evaluation's whole time budget, in seconds.</summary>
    public long BudgetSeconds { get; init; }

    /// <summary>Declared project files the loader did not open before its share of the budget ran out.</summary>
    public int ProjectsNotLoaded { get; init; }

    /// <summary>Loaded project versions left without symbols (and so without anything else).</summary>
    public int ProjectsNotIndexed { get; init; }

    /// <summary>
    /// Project versions that have symbols but whose relationships, references, calls or comments were cut short
    /// when the extraction deadline passed (some may have references but no comments).
    /// </summary>
    public int ProjectsNotFullyExtracted { get; init; }

    /// <summary>Selected solutions that declare, or reach through a project reference, any project left out.</summary>
    public int SolutionsUnfinished { get; init; }

    /// <summary>
    /// The unfinished solutions, relative to the checkout root (forward slashes), in selection order, capped at
    /// <see cref="MaxSolutions"/>.
    /// </summary>
    public IReadOnlyList<string> UnfinishedSolutions { get; init; } = [];

    /// <summary>True when anything was left out.</summary>
    [JsonIgnore]
    public bool Exhausted => ProjectsNotLoaded > 0 || ProjectsNotIndexed > 0 || ProjectsNotFullyExtracted > 0;
}

/// <summary>
/// How well a snapshot's source BOUND. A name the compiler could not resolve, or an invocation whose overload
/// resolution failed, has no exact target, so an exact reference or call edge for it cannot be stored; the
/// extractor records the compiler's candidate symbols instead (marked <c>candidate</c> on the query surface).
/// A project is degraded when enough of its code failed to bind that references or calls inside it may be
/// missing; any degraded project makes the coverage verdict partial. Serialized snake_case inside
/// <see cref="SnapshotCoverage.Binding"/>.
/// </summary>
public sealed record BindingHealth
{
    /// <summary>Identifier names the extractor examined across the indexed projects.</summary>
    public long NamesExamined { get; init; }

    /// <summary>Names that did not bind to any symbol (an unresolved type or member, or a failed overload resolution).</summary>
    public long UnboundNames { get; init; }

    /// <summary>Invocations the compiler could not bind to one method (an invalid invocation operation).</summary>
    public long UnboundInvocations { get; init; }

    /// <summary>References and call edges stored against the compiler's candidate symbols rather than an exact target.</summary>
    public long CandidateOccurrences { get; init; }

    /// <summary>Projects whose binding failures exceed the degraded threshold.</summary>
    public int ProjectsDegraded { get; init; }

    /// <summary>
    /// The projects with binding failures, worst first, capped at <see cref="MaxProjects"/>; a project that
    /// bound cleanly is not listed.
    /// </summary>
    public IReadOnlyList<ProjectBindingHealth> Projects { get; init; } = [];

    /// <summary>The cap on <see cref="Projects"/>.</summary>
    public const int MaxProjects = 25;
}

/// <summary>One project's binding health inside <see cref="BindingHealth.Projects"/>.</summary>
public sealed record ProjectBindingHealth
{
    /// <summary>The project file, relative to its repository root (forward slashes).</summary>
    public required string Project { get; init; }

    /// <summary>The evaluated target framework, when known.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetFramework { get; init; }

    /// <summary>Identifier names examined in the project.</summary>
    public long NamesExamined { get; init; }

    /// <summary>Names in the project that did not bind.</summary>
    public long UnboundNames { get; init; }

    /// <summary>Invocations in the project that did not bind to one method.</summary>
    public long UnboundInvocations { get; init; }

    /// <summary>Candidate references and call edges stored for the project.</summary>
    public long CandidateOccurrences { get; init; }

    /// <summary>True when the project's binding failures exceed the degraded threshold.</summary>
    public bool Degraded { get; init; }

    /// <summary>The design-time build or restore failure the loader reported for the project, when any.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LoadIssue { get; init; }
}

/// <summary>
/// One <c>global.json</c> SDK pin the service worker overrode because hostfxr could not satisfy it on this
/// worker (issue #113) — e.g. <c>"version": "10.0.300", "rollForward": "disable"</c> on a node that only
/// has 10.0.401. Serialized snake_case inside <see cref="SnapshotCoverage.SdkPinOverrides"/>.
/// </summary>
public sealed record SdkPinOverride
{
    /// <summary>The overridden <c>global.json</c>, relative to the checkout root (forward slashes).</summary>
    public required string GlobalJsonPath { get; init; }

    /// <summary>The SDK version the pin requested (<c>sdk.version</c>), when present.</summary>
    public string? RequestedVersion { get; init; }

    /// <summary>The pin's <c>sdk.rollForward</c> policy, when present.</summary>
    public string? RollForward { get; init; }

    /// <summary>The installed SDK hostfxr resolved once the pin was neutralized (the SDK actually used).</summary>
    public string? ResolvedSdkVersion { get; init; }

    /// <summary>The SDK versions installed on the worker, newest first.</summary>
    public IReadOnlyList<string> InstalledSdks { get; init; } = [];
}
