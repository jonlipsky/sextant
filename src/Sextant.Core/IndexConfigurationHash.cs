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
    public const string AnalyzerVersion = "1";

    /// <summary>
    /// Computes the configuration hash for a resolved profile. The canonical pre-image is a fixed,
    /// ordered <c>key=value;</c> string over invariant-formatted components so the result is stable
    /// across machines, cultures, and runs.
    /// </summary>
    public static string Compute(string profile, IndexFeature features, string generatedSourcePolicy)
    {
        var canonical =
            $"v=1;profile={IndexProfiles.Normalize(profile)};features={(long)features};" +
            $"generated={generatedSourcePolicy};analyzer={AnalyzerVersion}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(bytes);
    }
}
