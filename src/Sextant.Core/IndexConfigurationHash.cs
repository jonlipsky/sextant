using System.Security.Cryptography;
using System.Text;

namespace Sextant.Core;

/// <summary>
/// Computes the stable configuration hash recorded in every index run and snapshot (Phase 8,
/// acceptance criterion 1). The hash captures the <em>semantic configuration</em> of an index — the
/// profile, its resolved feature bits, the generated-source policy, and the analyzer/extraction-logic
/// version — so that:
/// <list type="bullet">
///   <item>the same input/profile always produces the identical hash (deterministic, machine- and
///   path-independent); and</item>
///   <item>a profile change (e.g. <c>core</c>→<c>standard</c>, which means optional tables were never
///   built) yields a different hash, which the daemon treats as a generation-invalidating change and
///   forces a full rebuild for.</item>
/// </list>
/// It is deliberately <b>distinct from and composable with</b> the Phase-4 per-project evaluation
/// fingerprint (migration 010), which hashes each project's MSBuild evaluation inputs: the evaluation
/// fingerprint answers "did this project's compilation inputs change?", while this hash answers "was
/// this index built with the same feature configuration?". A Phase-9 snapshot identity composes both
/// (commit + per-project evaluation fingerprints + this configuration hash).
/// <para>
/// It is intentionally schema-independent: an incompatible on-disk schema is already caught by the
/// migration rebuild gate and <c>IndexDatabase.CheckReadiness</c>, so folding the schema version in
/// here would only duplicate that signal.
/// </para>
/// </summary>
public static class IndexConfigurationHash
{
    /// <summary>
    /// Version of the extraction/analysis logic that shapes stored output independently of the
    /// profile. Bump it when a change to extraction would make an otherwise identically-configured
    /// index produce different rows, so the daemon rebuilds. Part of the hashed input.
    /// </summary>
    /// <remarks>
    /// <c>"1"</c> → <c>"2"</c> (issue #109): the service checkout worker no longer indexes a single
    /// nondeterministically-picked solution but the deterministic union of the selected solution set. For a
    /// multi-solution monorepo that changes which projects (and therefore which rows) an otherwise
    /// identically-configured index produces, so a pre-#109 snapshot must not be reused for the same
    /// identity — the bump invalidates it and forces the new loader to run.
    /// <para>
    /// <c>"2"</c> → <c>"3"</c> (issue #124): with no <c>solutions</c> config the service worker no longer
    /// selects ONE default-root solution but the deterministic union of EVERY discovered solution. Selection
    /// policy is not otherwise part of the snapshot identity, so a pre-#124 narrow snapshot at the same commit
    /// must not be reused — the bump invalidates it. (Like every bump, it also makes a local daemon's index
    /// rebuild once on upgrade.)
    /// </para>
    /// <para>
    /// <c>"3"</c> → <c>"4"</c> (issue #125): the service's cloning checkout provider now recursively initializes
    /// submodules at their pinned gitlink commits. The checkout policy is not otherwise part of the snapshot
    /// identity, so without this bump a pre-#125 snapshot of the SAME commit — indexed with every submodule
    /// unpopulated (missing the submodule-provided projects and their Phase-12 provider snapshots /
    /// cross-repository usage edges) — would be reused instead of re-indexing with the submodules present.
    /// </para>
    /// <para>
    /// <c>"4"</c> → <c>"5"</c>: the document extractor now keeps a usage site that does not bind exactly as a
    /// candidate occurrence (occurrence flag bit 2) instead of dropping it, the loader closes each unrestored
    /// project's ProjectReferences transitively, and the service worker restores NuGet packages before the
    /// load, so the same commit now yields more references and call edges. The indexer also records each
    /// member's C# declaration (<c>symbols.declaration</c>, migration 026) for the tools' <c>signature</c> field,
    /// so a snapshot indexed before it has no declarations and must not be reused for the same identity.
    /// </para>
    /// <para>
    /// <c>"5"</c> to <c>"6"</c> (issue #253): the document extractor records the bound constructor
    /// overload's references and call edges for object creation and explicit constructor initializers,
    /// in addition to type usages. Old snapshots have no such edges and must not be reused as fixed.
    /// </para>
    /// <para>
    /// <c>"6"</c> to <c>"7"</c> (issue #246): the service worker restores the selected solutions' deduplicated
    /// project union in one bounded MSBuild traversal, preserving the final selected-solution globals per
    /// project. A repository previously left partially restored by repeated per-solution work must be indexed
    /// again under the new restore policy.
    /// </para>
    /// <para>
    /// <c>"7"</c> to <c>"8"</c>: a load diagnostic that only replays a Warning-level message from the project's own
    /// <c>project.assets.json</c> (MSBuildWorkspace reports MSBuild warnings as failures) no longer marks a loaded
    /// project degraded, so the same commit can now publish complete where it was partial.
    /// </para>
    /// </remarks>
    public const string AnalyzerVersion = "8";

    /// <summary>
    /// Computes the configuration hash for a resolved profile. The canonical pre-image is a fixed,
    /// ordered <c>key=value;</c> string over invariant-formatted components so the result is stable
    /// across machines, cultures, and runs.
    /// </summary>
    public static string Compute(string profile, IndexFeature features, string generatedSourcePolicy)
        => Compute(profile, features, generatedSourcePolicy, documentExtractor: true);

    /// <summary>
    /// Computes the configuration hash for a resolved profile, including the Phase-5
    /// <paramref name="documentExtractor"/> toggle. The extractor selection shapes stored output (the
    /// document-oriented extractor and the legacy declaration-driven path are not row-identical), so it
    /// belongs in the hash: flipping <c>document_extractor</c> now changes the configuration hash, which
    /// the daemon treats as a generation-invalidating change and rebuilds for (issue #39). The canonical
    /// pre-image is a fixed, ordered <c>key=value;</c> string over invariant-formatted components so the
    /// result is stable across machines, cultures, and runs.
    /// </summary>
    public static string Compute(string profile, IndexFeature features, string generatedSourcePolicy, bool documentExtractor)
    {
        var canonical =
            $"v=1;profile={IndexProfiles.Normalize(profile)};features={(long)features};" +
            $"generated={generatedSourcePolicy};extractor={(documentExtractor ? "document" : "legacy")};" +
            $"analyzer={AnalyzerVersion}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(bytes);
    }
}
