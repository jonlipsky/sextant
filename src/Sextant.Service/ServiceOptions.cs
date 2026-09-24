using Sextant.Core;
using Sextant.Core.Platform;
using Sextant.Indexer;
using Sextant.Service.Contributions;
using Sextant.Service.Sandbox;

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

    /// <summary>
    /// A LEAST-PRIVILEGE contributor token for <c>/control/contribute</c>, SEPARATE from the control token
    /// (issue #71). A contributor holding only this token can upload contributions but CANNOT reach the
    /// other control-plane endpoints (ensure/status/resolve/retention). The full <see cref="ControlToken"/>
    /// remains a superset that also authorizes contribution. Null → the contribute endpoint falls back to
    /// the control token (or open, when neither is set — dev default).
    /// </summary>
    public string? ContributeToken { get; init; }

    /// <summary>
    /// The enforced read-authorization policy for the query plane (Phase 17, criterion 1). When
    /// <see cref="ReadAuthorizationPolicy.Enabled"/> the query plane authenticates KNOWN principals and the
    /// <c>PolicyReadAuthorizer</c> fails closed on any repository a principal is not granted. The default
    /// <see cref="ReadAuthorizationPolicy.Disabled"/> keeps single-node local operation zero-friction.
    /// </summary>
    public ReadAuthorizationPolicy ReadPolicy { get; init; } = ReadAuthorizationPolicy.Disabled;

    /// <summary>
    /// The network interface the HTTP surface binds to. Defaults to <c>localhost</c> (loopback only),
    /// preserving the original dev-safe behavior. Set it to a routable address (e.g. <c>0.0.0.0</c> or a
    /// specific IP) to make the service reachable from other hosts/containers — that widens exposure, so
    /// pair it with network/firewall scoping and the control/query tokens. Kestrel only binds a SPECIFIC
    /// interface for <c>localhost</c> or an IP literal; a non-IP hostname (like <c>0.0.0.0</c>) binds ALL
    /// interfaces, so prefer an IP literal when you need to pin one interface.
    /// </summary>
    public string BindAddress { get; init; } = "localhost";

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

    /// <summary>
    /// The producing NODE's default worker-capability fingerprint (Phase 15), folded into a request's
    /// snapshot identity (and stamped into the published snapshot) so request identity == published
    /// identity and an incompatible-capability reuse is blocked (criterion 5). Defaults to this host's
    /// <see cref="WorkerCapability.LocalDefault"/> fingerprint. Null for tests whose fake worker publishes
    /// under the request's own (capability-less) identity — keeping their identity byte-identical to
    /// before Phase 15.
    /// </summary>
    public string? DefaultCapabilityFingerprint { get; init; }

    /// <summary>
    /// The per-repository/profile platform-routing policy (Phase 15): whether the service may escalate a
    /// project off the default (Linux) worker to a native one, and to which OS families. Defaults to
    /// <see cref="PlatformRoutingPolicy.Default"/> (auto-escalate to any compatible native worker).
    /// </summary>
    public PlatformRoutingPolicy PlatformRouting { get; init; } = PlatformRoutingPolicy.Default;

    /// <summary>
    /// The client/CI contribution policy (Phase 16): how strictly the service authorizes and Git-content-
    /// verifies an uploaded contribution, and the max artifact size. Defaults to the dev-open posture
    /// (<see cref="ContributionPolicy.Default"/>) so a single-node service accepts contributions with no
    /// auth server / Git provider wired; a multi-tenant deployment sets the require-* flags true.
    /// </summary>
    public ContributionPolicy Contribution { get; init; } = ContributionPolicy.Default;

    /// <summary>
    /// The enforced evaluation-sandbox limits for the service worker (Phase 17, criterion 2). MSBuild
    /// evaluation of a checkout is an UNTRUSTED execution boundary even for a private repo, so the worker
    /// always evaluates under this policy. Defaults to <see cref="SandboxPolicy.Enforced"/>; the single-node
    /// local CLI/daemon path does not run the service worker, so local operation stays byte-identical.
    /// </summary>
    public SandboxPolicy Sandbox { get; init; } = SandboxPolicy.Enforced;

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
            ContributeToken = Env("CONTRIBUTE_TOKEN"),
            ReadPolicy = ReadAuthorizationPolicy.Parse(Env("READ_POLICY")),
            BindAddress = EnvHost("BIND_ADDRESS") ?? "localhost",
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
            DefaultConfigHash = IndexProfileDescriptor.FromConfiguration(config).ConfigurationHash,
            // The producing node's default capability (Phase 15). Folded into request identity and stamped
            // into published snapshots so request identity == published identity and cross-node reuse under
            // an incompatible capability is blocked (criterion 5).
            DefaultCapabilityFingerprint = WorkerCapability.LocalDefault.Fingerprint,
            PlatformRouting = PlatformRoutingPolicy.Parse(config.PlatformRouting),
            // Client/CI contribution policy (Phase 16). Dev-open by default; a multi-tenant deployment sets
            // the require-* flags true (and wires a real authorizer/provider — enforced fail-closed at Start).
            Contribution = new ContributionPolicy
            {
                RequireAuthorization = EnvBool("CONTRIB_REQUIRE_AUTH") ?? false,
                RequireGitContentVerification = EnvBool("CONTRIB_REQUIRE_GIT_VERIFY") ?? false,
                MaxArtifactBytes = EnvLong("CONTRIB_MAX_ARTIFACT_BYTES") is long max and > 0
                    ? max : ContributionPolicy.Default.MaxArtifactBytes
            },
            // Evaluation sandbox (criterion 2). Enforced by default; a deployment can widen the budgets or
            // (rarely, e.g. a fully trusted single-tenant node) allow network. Secrets are always scrubbed
            // unless explicitly disabled.
            Sandbox = new SandboxPolicy
            {
                Enabled = EnvBool("SANDBOX_ENABLED") ?? true,
                TimeBudget = EnvInt("SANDBOX_TIME_BUDGET_SECONDS") is int secs and > 0
                    ? TimeSpan.FromSeconds(secs) : SandboxPolicy.Enforced.TimeBudget,
                MemoryBudgetBytes = EnvLong("SANDBOX_MEMORY_BUDGET_BYTES") is long mem and > 0
                    ? mem : SandboxPolicy.Enforced.MemoryBudgetBytes,
                AllowNetwork = EnvBool("SANDBOX_ALLOW_NETWORK") ?? false,
                ScrubSecrets = EnvBool("SANDBOX_SCRUB_SECRETS") ?? true
            }
        };
    }

    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(EnvPrefix + name) is { Length: > 0 } v ? v : null;

    private static int? EnvInt(string name) =>
        int.TryParse(Env(name), out var v) ? v : null;

    private static long? EnvLong(string name) =>
        long.TryParse(Env(name), out var v) ? v : null;

    /// <summary>
    /// Parses and validates a network host token (a bind address). Returns null when unset (the caller's
    /// loopback default then applies) and THROWS on a blank or syntactically malformed value rather than
    /// binding to an empty/ambiguous interface — a bad host token is a config error, not a silent no-op
    /// (fail closed, matching the boolean-toggle convention). Validation uses <see cref="Uri.CheckHostName"/>
    /// so garbage (e.g. an unclosed <c>[::1</c> or a value with whitespace) is rejected instead of being
    /// handed to Kestrel, which would silently WIDEN such a token to a bind on ALL interfaces. A recognized
    /// value (<c>localhost</c>, an IPv4/IPv6 literal — bracketed or not, or a hostname) is returned trimmed.
    /// </summary>
    private static string? EnvHost(string name)
    {
        var v = Env(name);
        if (v is null)
            return null;
        var trimmed = v.Trim();
        if (trimmed.Length == 0 || Uri.CheckHostName(trimmed) == UriHostNameType.Unknown)
            throw new InvalidOperationException(
                $"Environment variable {EnvPrefix + name} has an invalid host value '{v}'. Provide a host " +
                "token such as 'localhost', '127.0.0.1', '0.0.0.0', a specific IP, or a hostname. Refusing " +
                "to start with a malformed bind address that Kestrel would widen to all interfaces (fail closed).");
        return trimmed;
    }

    /// <summary>
    /// Parses a boolean env var, recognizing common true/false spellings case-insensitively. Returns null
    /// when unset (the caller's secure default then applies) and THROWS on any other nonempty value rather
    /// than silently returning false — a typo in a security-relevant toggle (e.g. SANDBOX_ENABLED=tru) must
    /// fail startup loudly, never quietly disable the control (fail closed; hardening review finding).
    /// </summary>
    private static bool? EnvBool(string name)
    {
        var v = Env(name);
        if (v is null)
            return null;
        return v.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => throw new InvalidOperationException(
                $"Environment variable {EnvPrefix + name} has an invalid boolean value '{v}'. Use one of " +
                "1/0, true/false, yes/no, on/off. Refusing to start with an ambiguous security-relevant " +
                "setting (fail closed).")
        };
    }
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
