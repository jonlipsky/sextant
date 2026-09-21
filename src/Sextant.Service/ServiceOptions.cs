using Sextant.Core;
using Sextant.Indexer;

namespace Sextant.Service;

/// <summary>
/// Configuration for the standalone Sextant index service (Phase 13). Binds from environment variables
/// (<c>SEXTANT_SERVICE_*</c>) so a single-node development deployment needs zero config while a scaled
/// deployment can point the persistent volumes at durable storage. The service is a DATA PLANE: it owns
/// the durable snapshot catalog + semantic store and answers control + query requests. It never depends
/// on ProcessStack (Phase 14 orchestrates OVER these options), and the local stdio MCP path is entirely
/// independent of it (criterion 6).
/// </summary>
public sealed record ServiceOptions
{
    /// <summary>The durable catalog + semantic store (the Phase-9 snapshot catalog lives here).</summary>
    public required string CatalogDbPath { get; init; }

    /// <summary>Persistent checkout/artifact/cache volumes, kept SEPARATE from worker scratch.</summary>
    public required ServiceVolumes Volumes { get; init; }

    /// <summary>Bearer token required by the control endpoints (<c>/control/*</c>). Null disables control auth (dev only).</summary>
    public string? ControlToken { get; init; }

    /// <summary>Bearer token required by the query endpoints (<c>/mcp</c>, <c>/query/*</c>). Null allows anonymous read (local default).</summary>
    public string? QueryToken { get; init; }

    /// <summary>Port for the control + query surface (one port hosts both when <see cref="QueryPort"/> matches or is null).</summary>
    public int ControlPort { get; init; } = 3011;

    /// <summary>Optional dedicated query port; when null the query surface shares <see cref="ControlPort"/>.</summary>
    public int? QueryPort { get; init; }

    /// <summary>Writer-lease TTL. The service holds a single-writer lease for its lifetime (issue #38).</summary>
    public TimeSpan LeaseTtl { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Remote peer base URLs for snapshot federation (issue #51). Empty for a standalone node.</summary>
    public IReadOnlyList<string> Peers { get; init; } = [];

    /// <summary>Per-request timeout for a remote federation fetch before falling back to the cached base (issue #51).</summary>
    public TimeSpan RemoteFetchTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Retention policy applied by the service-owned retention/GC pass (issues #46/#37/#54).</summary>
    public RetentionPolicy Retention { get; init; } = new();

    /// <summary>A human-readable identifier for this service instance (lease holder + logs).</summary>
    public string Holder { get; init; } = $"sextant-service@{Environment.MachineName}#{Environment.ProcessId}";

    /// <summary>
    /// The node's own profile/feature configuration hash, folded into a request's snapshot identity when
    /// the request omits <see cref="EnsureSnapshotRequest.ConfigHash"/>. The live worker publishes under
    /// the orchestrator's <c>IndexProfileDescriptor.ConfigurationHash</c>, so defaulting the request
    /// identity to the SAME hash is what lets the service's idempotency lookup find the worker's published
    /// snapshot. Null for tests whose fake worker publishes under the request's own (config-less) identity.
    /// </summary>
    public string? DefaultConfigHash { get; init; }

    private const string EnvPrefix = "SEXTANT_SERVICE_";

    /// <summary>
    /// Builds options from environment variables, falling back to the repo <see cref="SextantConfiguration"/>
    /// for the database path and to a data directory rooted at <c>.sextant/service</c> for volumes. Missing
    /// values take documented defaults so a bare <c>sextant service</c> works out of the box.
    /// </summary>
    public static ServiceOptions FromEnvironment(SextantConfiguration? config = null)
    {
        config ??= SextantConfiguration.Load();
        var dbPath = Env("DB_PATH") ?? config.DbPath;
        var dataRoot = Env("DATA_ROOT")
            ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath)) is { Length: > 0 } dir ? dir : ".", "service");

        return new ServiceOptions
        {
            CatalogDbPath = dbPath,
            Volumes = ServiceVolumes.Rooted(dataRoot,
                checkoutRoot: Env("CHECKOUT_ROOT"),
                artifactRoot: Env("ARTIFACT_ROOT"),
                cacheRoot: Env("CACHE_ROOT"),
                scratchRoot: Env("SCRATCH_ROOT")),
            ControlToken = Env("CONTROL_TOKEN"),
            QueryToken = Env("QUERY_TOKEN"),
            ControlPort = EnvInt("CONTROL_PORT") ?? 3011,
            QueryPort = EnvInt("QUERY_PORT"),
            LeaseTtl = EnvInt("LEASE_TTL_SECONDS") is int ttl and > 0 ? TimeSpan.FromSeconds(ttl) : TimeSpan.FromSeconds(30),
            Peers = Env("PEERS") is { Length: > 0 } peers
                ? peers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [],
            RemoteFetchTimeout = EnvInt("REMOTE_TIMEOUT_SECONDS") is int t and > 0 ? TimeSpan.FromSeconds(t) : TimeSpan.FromSeconds(10),
            Retention = config.Retention,
            // Fold the node's own profile hash into request identities by default, so an ensure request that
            // omits ConfigHash resolves to the SAME identity_hash the local worker's orchestrator publishes
            // under (IndexProfileDescriptor.ConfigurationHash) — without it the idempotency lookup misses.
            DefaultConfigHash = IndexProfileDescriptor.FromConfiguration(config).ConfigurationHash
        };
    }

    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(EnvPrefix + name) is { Length: > 0 } v ? v : null;

    private static int? EnvInt(string name) =>
        int.TryParse(Env(name), out var v) ? v : null;
}

/// <summary>
/// The four on-disk roots the service manages, with the hard invariant that the WORKER SCRATCH root is
/// SEPARATE from the persistent checkout/artifact/cache volumes (acceptance criterion: worker scratch
/// cleanup can never delete a published snapshot). <see cref="ServicePaths"/> enforces the separation.
/// </summary>
public sealed record ServiceVolumes
{
    /// <summary>Persistent repository checkouts (base-branch working trees the service indexes).</summary>
    public required string CheckoutRoot { get; init; }

    /// <summary>Persistent published artifacts (immutable snapshot outputs).</summary>
    public required string ArtifactRoot { get; init; }

    /// <summary>Persistent bounded local caches (federation pages, resolved metadata).</summary>
    public required string CacheRoot { get; init; }

    /// <summary>Ephemeral per-job worker scratch — SEPARATE from the persistent volumes and freely deletable.</summary>
    public required string ScratchRoot { get; init; }

    public static ServiceVolumes Rooted(
        string dataRoot,
        string? checkoutRoot = null,
        string? artifactRoot = null,
        string? cacheRoot = null,
        string? scratchRoot = null) => new()
        {
            CheckoutRoot = checkoutRoot ?? Path.Combine(dataRoot, "checkouts"),
            ArtifactRoot = artifactRoot ?? Path.Combine(dataRoot, "artifacts"),
            CacheRoot = cacheRoot ?? Path.Combine(dataRoot, "cache"),
            ScratchRoot = scratchRoot ?? Path.Combine(dataRoot, "scratch")
        };
}
