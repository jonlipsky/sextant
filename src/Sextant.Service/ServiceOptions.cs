using Sextant.Core;
using Sextant.Core.Platform;
using Sextant.Indexer;
using Sextant.Service.CallerIdentity;
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
    /// When true, every query-plane read must name the repository it reads (the <c>X-Sextant-Repository</c>
    /// header); a read that names none fails with <c>repository_required</c> instead of reading the
    /// unselected default (which spans every repository of a multi-repository catalog). The default
    /// (false) keeps the legacy behavior for callers that send no selector. Independent of
    /// <see cref="ReadPolicy"/>: the selector is honored either way. <c>SEXTANT_SERVICE_REQUIRE_REPOSITORY_SELECTION</c>.
    /// </summary>
    public bool RequireRepositorySelection { get; init; }

    /// <summary>
    /// Delegate bearer tokens for the query plane (SVC-3): <c>SEXTANT_SERVICE_DELEGATE_TOKENS</c>, as <c>tok1;tok2</c>.
    /// A delegate token grants nothing by itself. A <c>tools/call</c> or <c>/query/*</c> request made with one needs
    /// a verified caller assertion, and what that caller may read is decided per caller (deny-all until grants
    /// exist). Requires <see cref="CallerAssertion"/> keys, and must differ from every other configured token.
    /// </summary>
    public IReadOnlyList<string> DelegateTokens { get; init; } = [];

    /// <summary>
    /// How caller assertions are verified (SVC-3): the keys, audience, issuers, header, identity providers and
    /// applications bound from <c>SEXTANT_SERVICE_CALLER_*</c>. Off (no keys) by default, in which case any request
    /// that carries an assertion is refused.
    /// </summary>
    public CallerAssertionOptions CallerAssertion { get; init; } = CallerAssertionOptions.Disabled;

    /// <summary>
    /// Throws when the caller-identity settings are inconsistent (fail closed): invalid <see cref="CallerAssertion"/>
    /// options, delegate tokens without caller keys, or a delegate token that is blank or equal to the control,
    /// query or contribute token or to a read-policy principal's token. Messages never contain a token or key.
    /// </summary>
    /// <exception cref="InvalidOperationException">The settings are invalid.</exception>
    public void ValidateCallerIdentity()
    {
        CallerAssertion.Validate();
        if (DelegateTokens.Count == 0)
            return;
        if (!CallerAssertion.Enabled)
            throw new InvalidOperationException(
                $"{EnvPrefix}DELEGATE_TOKENS requires {EnvPrefix}CALLER_KEYS: a delegate token is only usable with a " +
                "verified caller assertion. Refusing to start with an unverifiable delegate token (fail closed).");

        var others = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in new[] { ControlToken, QueryToken, ContributeToken })
        {
            if (!string.IsNullOrEmpty(token))
                others.Add(token);
        }
        foreach (var principal in ReadPolicy.Principals)
            others.Add(principal.Token);
        for (var i = 0; i < DelegateTokens.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(DelegateTokens[i]))
                throw new InvalidOperationException(
                    $"{EnvPrefix}DELEGATE_TOKENS entry #{i + 1} is blank. Refusing to start (fail closed).");
            if (others.Contains(DelegateTokens[i]))
                throw new InvalidOperationException(
                    $"{EnvPrefix}DELEGATE_TOKENS entry #{i + 1} equals another configured token (the control, query or " +
                    "contribute token, or a read-policy principal). A delegate token must be distinct so it can never " +
                    "act as a legacy credential. Refusing to start (fail closed).");
        }
    }

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

    /// <summary>
    /// How the service obtains the on-disk checkout an ensure request indexes.
    /// <see cref="ServiceCheckoutMode.Locate"/> (the DEFAULT) only LOCATES an already-provisioned checkout
    /// on the persistent checkout volume — byte-identical to the pre-provisioning behavior, no outbound git.
    /// <see cref="ServiceCheckoutMode.Clone"/> makes the node self-sufficient: on a locate miss it clones the
    /// repository at the requested commit into the checkout volume (a durable cache) and then indexes it.
    /// </summary>
    public ServiceCheckoutMode CheckoutMode { get; init; } = ServiceCheckoutMode.Locate;

    /// <summary>
    /// An OPTIONAL access token used only in <see cref="ServiceCheckoutMode.Clone"/> mode to authenticate an
    /// outbound clone of a PRIVATE <c>https</c> repository (and of its submodules on the SAME host). Public
    /// repositories need none. It is never logged, never written into any git config, remote URL or process
    /// argument: it is handed to git only TRANSIENTLY, through environment-scoped config
    /// (<c>GIT_CONFIG_COUNT</c> → an <c>http.https://&lt;repository-host&gt;/.extraheader</c> Basic credential)
    /// scoped to the top-level repository's https host (issue #125). Null → unauthenticated clone.
    /// </summary>
    public string? CheckoutToken { get; init; }

    /// <summary>
    /// Extra https hosts (<c>host</c> or <c>host:port</c>, lower-cased, default port 443 dropped) whose
    /// submodules <see cref="ServiceCheckoutMode.Clone"/> mode may fetch — ANONYMOUSLY: the checkout token is
    /// only ever sent to the top-level repository's own host. A submodule on any other host is left
    /// unpopulated with a <c>url_refused</c> reason (coverage partial). Bound from
    /// <c>SEXTANT_SERVICE_SUBMODULE_HOSTS</c> (comma-separated); a malformed entry fails startup (fail closed).
    /// </summary>
    public IReadOnlyList<string> SubmoduleHosts { get; init; } = [];

    /// <summary>
    /// SSRF policy applied to every <c>POST /control/ensure</c> repository URL BEFORE any job row exists
    /// (SVC-5): https only, no userinfo/query/fragment/non-443 port, a multi-label DNS host (no IP literal or
    /// <c>localhost</c>) on the host allow-list, a path of exactly <c>/{owner}/{repo}</c>, and an optional owner
    /// allow-list. Bound from <c>SEXTANT_SERVICE_REPOSITORY_HOSTS</c> (default <c>github.com</c>; <c>*</c> = any
    /// host that passes the shape rules) and <c>SEXTANT_SERVICE_REPOSITORY_OWNERS</c> (<c>host/owner</c> or
    /// <c>host/*</c>; unset = any owner). A malformed entry fails startup (fail closed). Applies in both
    /// checkout modes; direct <see cref="SnapshotService"/> callers bypass it.
    /// </summary>
    public RepositoryUrlPolicy RepositoryUrlPolicy { get; init; } = RepositoryUrlPolicy.Default;

    /// <summary>
    /// How long an intake refusal waits for its <c>ensure</c>/<c>denied</c> audit row before answering 400
    /// (SVC-5). The refusal itself is a pure decision; the bound only stops it waiting out a running production
    /// that holds the single writer. The write stays service-owned and lands once the writer frees.
    /// </summary>
    internal TimeSpan DeniedAuditWait { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Upper bound on how many times an identity's job may run before a persistently-RETRYABLE provisioning
    /// failure (a transient clone/fetch error in <see cref="ServiceCheckoutMode.Clone"/> mode — network,
    /// DNS, timeout, remote 5xx/429) is recorded as a terminal <see cref="SnapshotJobStatus.Failed"/> instead
    /// of being requeued for the next ensure. This is the safety bound that stops a MIS-classified permanent
    /// failure (or a genuinely-down remote) from retrying forever. It conservatively shares the job-wide
    /// attempt counter, so any prior cancellation re-attempts or reclaimed-snapshot regenerations for the
    /// same identity also count toward it. DETERMINISTIC failures (no solution, commit/repo not found, auth
    /// refused, bad URL) are never retried — they stay terminal on the FIRST ensure regardless of this bound.
    /// </summary>
    public int MaxProvisioningAttempts { get; init; } = 5;

    /// <summary>Writer-lease TTL. The service holds a single-writer lease for its lifetime (issue #38).</summary>
    public TimeSpan LeaseTtl { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long <see cref="SnapshotService.Dispose"/> waits for in-flight ensures to finish after shutdown
    /// cancelled their workers (issue #148), so each cancelled job is requeued while the writer lease is
    /// still held. A worker that ignores cancellation past this bound is abandoned: the writer lease is
    /// abandoned too (left to expire by its TTL, never released under the straggler), its job stays running,
    /// and the next service instance's startup reconcile requeues it.
    /// </summary>
    public TimeSpan ShutdownDrainTimeout { get; init; } = TimeSpan.FromSeconds(30);

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

    /// <summary>
    /// Issue #113: when true (the default) the worker temporarily neutralizes a checkout's <c>global.json</c>
    /// SDK pin that hostfxr cannot satisfy on this node (e.g. <c>"rollForward": "disable"</c> on an SDK band
    /// the container lacks) for the MSBuild load only, restoring the committed file before indexing, and
    /// records a <c>sdk_pin_overridden</c> diagnostic. When false such a pin fails the load (or skips the
    /// projects it governs) with a typed <c>sdk_resolution_failed</c> diagnostic. A pin that resolves is
    /// never touched either way. <c>SEXTANT_SERVICE_SDK_PIN_OVERRIDE</c>.
    /// </summary>
    public bool SdkPinOverride { get; init; } = true;

    /// <summary>
    /// The <see cref="SnapshotIdentity.SdkPinPolicy"/> component folded into every ensure request's identity
    /// (issue #113). It is null for the default override-on policy, so identities stay byte-identical to before
    /// #113, and <c>strict</c> when <see cref="SdkPinOverride"/> is off. Flipping the toggle therefore changes the
    /// identity, and the next ensure of a commit REBUILDS it rather than reusing a snapshot (or a failed job)
    /// produced under the other policy. The worker publishes under the same value
    /// (<see cref="SdkPin.SdkPinGuard.IdentityComponent"/>), because the host builds its guard from this toggle.
    /// </summary>
    public string? SdkPinIdentityComponent => SdkPin.SdkPinOptions.IdentityComponentFor(SdkPinOverride);

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

        var options = new ServiceOptions
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
            RequireRepositorySelection = EnvBool("REQUIRE_REPOSITORY_SELECTION") ?? false,
            DelegateTokens = ParseDelegateTokens(Env("DELEGATE_TOKENS")),
            CallerAssertion = ParseCallerAssertion(
                Env("CALLER_KEYS"), Env("CALLER_AUDIENCE"), Env("CALLER_ISSUERS"), Env("CALLER_HEADER"),
                Env("CALLER_IDPS"), Env("CALLER_APPS")),
            BindAddress = EnvHost("BIND_ADDRESS") ?? "localhost",
            ControlPort = EnvInt("CONTROL_PORT") ?? 3011,
            QueryPort = EnvInt("QUERY_PORT"),
            CheckoutMode = ParseCheckoutMode(Env("CHECKOUT_MODE")),
            CheckoutToken = Env("CHECKOUT_TOKEN"),
            SubmoduleHosts = ParseSubmoduleHosts(Env("SUBMODULE_HOSTS")),
            RepositoryUrlPolicy = ParseRepositoryUrlPolicy(Env("REPOSITORY_HOSTS"), Env("REPOSITORY_OWNERS")),
            // Bound retryable-provisioning re-attempts. Out-of-range/invalid values fall back to the default
            // (5) rather than failing start, and are clamped to a sane ceiling so a huge configured value
            // cannot defeat the safety bound.
            MaxProvisioningAttempts = EnvInt("MAX_PROVISIONING_ATTEMPTS") is int a and > 0 and <= 100 ? a : 5,
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
            },
            SdkPinOverride = EnvBool("SDK_PIN_OVERRIDE") ?? true
        };
        options.ValidateCallerIdentity();
        return options;
    }

    /// <summary>
    /// Parses <c>SEXTANT_SERVICE_DELEGATE_TOKENS</c> (<c>tok1;tok2</c>; unset → none). A set variable that lists no
    /// token THROWS (fail closed). The tokens are never echoed.
    /// </summary>
    internal static IReadOnlyList<string> ParseDelegateTokens(string? value)
    {
        if (value is null)
            return [];
        var tokens = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            throw new InvalidOperationException(
                $"Environment variable {EnvPrefix}DELEGATE_TOKENS is set but lists no tokens. Refusing to start (fail closed).");
        return tokens.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Parses the <c>SEXTANT_SERVICE_CALLER_*</c> variables into <see cref="CallerAssertionOptions"/>. A malformed key
    /// entry, identity provider or application THROWS, naming the entry by position only (fail closed); consistency
    /// (for example the audience a key ring requires) is checked by <see cref="ValidateCallerIdentity"/>.
    /// </summary>
    internal static CallerAssertionOptions ParseCallerAssertion(
        string? keys, string? audience, string? issuers, string? header, string? idps, string? apps)
    {
        CallerKeyRing ring;
        try
        {
            ring = keys is null ? CallerKeyRing.Empty : CallerKeyRing.Parse(keys);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"Environment variable {EnvPrefix}CALLER_KEYS is invalid: {ex.Message} Refusing to start (fail closed).", ex);
        }

        var defaults = CallerAssertionOptions.Disabled;
        return new CallerAssertionOptions
        {
            Keys = ring,
            Audience = audience?.Trim(),
            Issuers = issuers is null ? defaults.Issuers : ParseCallerList(issuers, "CALLER_ISSUERS", _ => true, "a non-blank issuer"),
            Header = header?.Trim() ?? CallerAssertionOptions.DefaultHeader,
            Idps = idps is null ? defaults.Idps : ParseCallerList(idps, "CALLER_IDPS", CallerAssertionOptions.IsValidIdp, "[a-z0-9-]{1,32}"),
            Apps = apps is null ? defaults.Apps : ParseCallerList(apps, "CALLER_APPS", CallerAssertionOptions.IsValidApp, "[A-Za-z0-9._-]{1,64}")
        };
    }

    private static HashSet<string> ParseCallerList(string value, string name, Func<string, bool> isValid, string shape)
    {
        var entries = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Length == 0)
            throw new InvalidOperationException(
                $"Environment variable {EnvPrefix}{name} is set but has no entries. Refusing to start (fail closed).");
        for (var i = 0; i < entries.Length; i++)
        {
            if (!isValid(entries[i]))
                throw new InvalidOperationException(
                    $"Environment variable {EnvPrefix}{name} entry #{i + 1} is malformed (expected {shape}). " +
                    "Refusing to start (fail closed).");
        }
        return entries.ToHashSet(StringComparer.Ordinal);
    }

    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(EnvPrefix + name) is { Length: > 0 } v ? v : null;

    private static int? EnvInt(string name) =>
        int.TryParse(Env(name), out var v) ? v : null;

    /// <summary>
    /// Parses the checkout-provisioning mode. Unset → the safe <see cref="ServiceCheckoutMode.Locate"/>
    /// default (no outbound git). Any nonempty value other than <c>locate</c>/<c>clone</c> THROWS rather
    /// than silently falling back — an operator who typo'd the mode that governs outbound network access
    /// must fail startup loudly, never quietly run in the wrong mode (fail closed, matching the boolean and
    /// host-token conventions).
    /// </summary>
    private static ServiceCheckoutMode ParseCheckoutMode(string? value)
    {
        if (value is null)
            return ServiceCheckoutMode.Locate;
        return value.Trim().ToLowerInvariant() switch
        {
            "locate" => ServiceCheckoutMode.Locate,
            "clone" => ServiceCheckoutMode.Clone,
            _ => throw new InvalidOperationException(
                $"Environment variable {EnvPrefix}CHECKOUT_MODE has an invalid value '{value}'. Use 'locate' " +
                "(default; index only an already-provisioned checkout) or 'clone' (provision the checkout by " +
                "cloning the requested commit). Refusing to start with an ambiguous checkout mode (fail closed).")
        };
    }

    private static long? EnvLong(string name) =>
        long.TryParse(Env(name), out var v) ? v : null;

    /// <summary>
    /// Parses <c>SEXTANT_SERVICE_SUBMODULE_HOSTS</c>: a comma-separated list of extra https hosts
    /// (<c>host</c> or <c>host:port</c>) whose submodules clone mode may fetch anonymously. Unset → none.
    /// Any malformed entry (a scheme, a path, userinfo, whitespace inside, an invalid DNS name or port)
    /// THROWS rather than being skipped — the list widens outbound fetches, so a typo must fail startup
    /// loudly (fail closed, matching the checkout-mode convention). Entries are normalized (lower-cased,
    /// default port 443 dropped) and de-duplicated.
    /// </summary>
    internal static IReadOnlyList<string> ParseSubmoduleHosts(string? value)
    {
        if (value is null)
            return [];
        var hosts = new List<string>();
        foreach (var raw in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = SubmoduleUrlPolicy.NormalizeAuthority(raw);
            if (normalized is null)
                throw new InvalidOperationException(
                    $"Environment variable {EnvPrefix}SUBMODULE_HOSTS has an invalid entry '{raw}'. Provide " +
                    "comma-separated https host names with an optional port (e.g. 'gitlab.example.com' or " +
                    "'git.example.com:8443') — no scheme, path or credentials. Refusing to start with an " +
                    "ambiguous submodule host allowlist (fail closed).");
            if (!hosts.Contains(normalized, StringComparer.Ordinal))
                hosts.Add(normalized);
        }
        return hosts;
    }

    /// <summary>
    /// Parses <c>SEXTANT_SERVICE_REPOSITORY_HOSTS</c> (comma-separated DNS host names or <c>*</c>; unset →
    /// <c>github.com</c>) and <c>SEXTANT_SERVICE_REPOSITORY_OWNERS</c> (comma-separated <c>host/owner</c> or
    /// <c>host/*</c>; unset → any owner) into the ensure-intake <see cref="Service.RepositoryUrlPolicy"/>.
    /// A malformed entry, an owner entry on a host that is not allow-listed, or a set variable with no
    /// entries THROWS: these lists gate outbound fetches, so a typo must fail startup loudly (fail closed).
    /// </summary>
    internal static RepositoryUrlPolicy ParseRepositoryUrlPolicy(string? hosts, string? owners)
    {
        if (hosts is null && owners is null)
            return RepositoryUrlPolicy.Default;
        string[] hostEntries = hosts is null ? [RepositoryUrlPolicy.DefaultHost] : SplitEntries(hosts, "REPOSITORY_HOSTS");
        var ownerEntries = owners is null ? null : SplitEntries(owners, "REPOSITORY_OWNERS");
        try
        {
            return new RepositoryUrlPolicy(hostEntries, ownerEntries);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"Environment variables {EnvPrefix}REPOSITORY_HOSTS/{EnvPrefix}REPOSITORY_OWNERS are invalid: " +
                $"{ex.Message} Refusing to start with an ambiguous repository URL policy (fail closed).", ex);
        }
    }

    private static string[] SplitEntries(string value, string name)
    {
        var entries = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Length == 0)
            throw new InvalidOperationException(
                $"Environment variable {EnvPrefix}{name} is set but has no entries. Refusing to start with an " +
                "ambiguous repository URL policy (fail closed).");
        return entries;
    }

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
/// How the service obtains the on-disk checkout an ensure request indexes.
/// </summary>
public enum ServiceCheckoutMode
{
    /// <summary>Only LOCATE an already-provisioned checkout on the persistent volume (default; no outbound git).</summary>
    Locate,

    /// <summary>On a locate miss, clone the repository at the requested commit into the checkout volume, then index it.</summary>
    Clone
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
