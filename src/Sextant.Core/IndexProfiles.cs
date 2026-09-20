namespace Sextant.Core;

/// <summary>
/// The named indexing profiles (Phase 8). A profile selects the semantic depth a repository indexes
/// for, resolving to a fixed <see cref="IndexFeature"/> set. Names are the stable public contract used
/// in <c>sextant.json</c>, the <c>SEXTANT_INDEXING_PROFILE</c> env var, and every recorded index run.
/// </summary>
public static class IndexProfiles
{
    /// <summary>Definitions, occurrences, calls, type relationships, project dependencies.</summary>
    public const string Core = "core";

    /// <summary>Core plus documentation search, comments, and test indexing. The default.</summary>
    public const string Standard = "standard";

    /// <summary>Standard plus detailed dataflow (argument/return flow) and extended evidence.</summary>
    public const string Deep = "deep";

    /// <summary>The default profile when none is configured.</summary>
    public const string Default = Standard;

    /// <summary>All recognized profile names, in increasing depth.</summary>
    public static IReadOnlyList<string> All { get; } = [Core, Standard, Deep];

    /// <summary>
    /// Canonicalizes a configured profile name (case/whitespace-insensitive). An unrecognized value
    /// resolves to <see cref="Default"/> so a typo degrades to the safe default rather than crashing
    /// an index run; recording the canonical name keeps the configuration hash stable.
    /// </summary>
    public static string Normalize(string? profile)
    {
        var trimmed = profile?.Trim().ToLowerInvariant();
        return trimmed switch
        {
            Core => Core,
            Standard => Standard,
            Deep => Deep,
            _ => Default
        };
    }

    /// <summary>Whether <paramref name="profile"/> names a recognized profile (before normalization).</summary>
    public static bool IsRecognized(string? profile)
    {
        var trimmed = profile?.Trim().ToLowerInvariant();
        return trimmed is Core or Standard or Deep;
    }

    /// <summary>Resolves a profile name to the feature set it builds.</summary>
    public static IndexFeature ResolveFeatures(string? profile) => Normalize(profile) switch
    {
        Core => IndexFeature.Core,
        Deep => IndexFeature.Deep,
        _ => IndexFeature.Standard
    };

    /// <summary>
    /// The lowest-depth profile that builds <paramref name="feature"/>. Used to tell a caller which
    /// profile to re-index at when a capability-gated query hits data the active profile omitted.
    /// </summary>
    public static string MinimumProfileFor(IndexFeature feature)
    {
        if ((IndexFeature.Core & feature) == feature) return Core;
        if ((IndexFeature.Standard & feature) == feature) return Standard;
        return Deep;
    }

    /// <summary>
    /// The stable snake_case names of every single-capability bit enabled in <paramref name="features"/>,
    /// in fixed depth order. Used by status surfaces so a human/agent can see exactly what the active
    /// generation built.
    /// </summary>
    public static IReadOnlyList<string> FeatureNames(IndexFeature features)
    {
        var names = new List<string>();
        void Add(IndexFeature bit, string name)
        {
            if ((features & bit) == bit) names.Add(name);
        }

        Add(IndexFeature.Definitions, "definitions");
        Add(IndexFeature.Occurrences, "occurrences");
        Add(IndexFeature.Calls, "calls");
        Add(IndexFeature.TypeRelationships, "type_relationships");
        Add(IndexFeature.ProjectDependencies, "project_dependencies");
        Add(IndexFeature.DocumentationSearch, "documentation_search");
        Add(IndexFeature.Comments, "comments");
        Add(IndexFeature.TestIndexing, "test_indexing");
        Add(IndexFeature.Dataflow, "dataflow");
        return names;
    }
}

/// <summary>
/// The generated-source handling policy recorded for an index run (Phase 8). Generated code is
/// currently always excluded (<c>*.g.cs</c>, <c>*.designer.cs</c>, <c>obj/</c>, and implicitly-declared
/// symbols); the value is surfaced in status and folded into the configuration hash so a future
/// include-generated mode is a recorded, rebuild-forcing change rather than a silent one.
/// </summary>
public static class GeneratedSourcePolicies
{
    /// <summary>Generated source is excluded from the index (the only supported policy today).</summary>
    public const string Exclude = "exclude";

    /// <summary>The default policy.</summary>
    public const string Default = Exclude;
}

/// <summary>
/// A fully-resolved indexing profile: its canonical name, the feature bits it builds, its
/// generated-source policy, and the stable configuration hash those imply. Threaded into the
/// indexer so every optional phase is gated on <see cref="Has"/> and every index run records its
/// profile/feature/hash triple (Phase 8, criteria 1–3).
/// </summary>
public sealed record IndexProfileDescriptor
{
    /// <summary>The canonical profile name (see <see cref="IndexProfiles"/>).</summary>
    public required string Profile { get; init; }

    /// <summary>The feature bits this profile builds.</summary>
    public required IndexFeature Features { get; init; }

    /// <summary>The generated-source policy (see <see cref="GeneratedSourcePolicies"/>).</summary>
    public required string GeneratedSourcePolicy { get; init; }

    /// <summary>
    /// The Phase-5 document-oriented extractor toggle (<c>document_extractor</c>, default ON). Folded
    /// into <see cref="ConfigurationHash"/> so flipping it forces a rebuild (issue #39). Defaults to
    /// <c>true</c> so a descriptor constructed without specifying it hashes as the default-ON extractor.
    /// </summary>
    public bool DocumentExtractor { get; init; } = true;

    /// <summary>The stable configuration hash for this profile/feature/policy/extractor combination.</summary>
    public string ConfigurationHash =>
        IndexConfigurationHash.Compute(Profile, Features, GeneratedSourcePolicy, DocumentExtractor);

    /// <summary>Whether every bit in <paramref name="feature"/> is enabled.</summary>
    public bool Has(IndexFeature feature) => (Features & feature) == feature;

    /// <summary>Builds a descriptor for a profile name and (optional) generated-source policy.</summary>
    public static IndexProfileDescriptor For(string? profile, string? generatedSourcePolicy = null, bool documentExtractor = true)
    {
        var name = IndexProfiles.Normalize(profile);
        return new IndexProfileDescriptor
        {
            Profile = name,
            Features = IndexProfiles.ResolveFeatures(name),
            GeneratedSourcePolicy = string.IsNullOrWhiteSpace(generatedSourcePolicy)
                ? GeneratedSourcePolicies.Default
                : generatedSourcePolicy.Trim().ToLowerInvariant(),
            DocumentExtractor = documentExtractor
        };
    }

    /// <summary>Resolves the descriptor from repository configuration (json + env precedence).</summary>
    public static IndexProfileDescriptor FromConfiguration(SextantConfiguration config)
        => For(config.IndexingProfile, config.GeneratedSourcePolicy, config.DocumentExtractor);

    /// <summary>
    /// The "everything on" descriptor (profile <c>deep</c>). Used as the indexer's default so a
    /// construction that does not specify a profile keeps the pre-profile behavior of building every
    /// optional feature — behavior-preserving for existing callers and tests.
    /// </summary>
    public static IndexProfileDescriptor Full { get; } = For(IndexProfiles.Deep);
}
