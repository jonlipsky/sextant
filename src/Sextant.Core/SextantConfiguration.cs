using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Sextant.Core;

public sealed class SextantConfiguration
{
    public string DbPath { get; set; } = ".sextant/profiles/default/sextant.db";
    public string Profile { get; set; } = "default";
    public int MaxCallHierarchyDepth { get; set; } = 5;
    public int FtsMaxResults { get; set; } = 20;
    public List<string> Solutions { get; set; } = [];
    public string? DaemonSocket { get; set; }
    public bool AutoSpawnDaemon { get; set; } = true;

    /// <summary>
    /// The indexing profile selecting semantic depth: <c>core</c>, <c>standard</c> (default), or
    /// <c>deep</c> (Phase 8). Distinct from <see cref="Profile"/>, which names the on-disk index slot.
    /// Overridable via <c>indexing_profile</c> in <c>sextant.json</c> or the
    /// <c>SEXTANT_INDEXING_PROFILE</c> env var.
    /// </summary>
    public string IndexingProfile { get; set; } = IndexProfiles.Default;

    /// <summary>
    /// Generated-source handling policy (Phase 8). Currently always <c>exclude</c>; recorded in the
    /// configuration hash and status so a future include-generated mode is an explicit, rebuild-forcing
    /// change. Overridable via <c>generated_source_policy</c> or <c>SEXTANT_GENERATED_SOURCE_POLICY</c>.
    /// </summary>
    public string GeneratedSourcePolicy { get; set; } = GeneratedSourcePolicies.Default;

    /// <summary>Retention limits for superseded generations, API history, and source blobs (Phase 8).</summary>
    public RetentionPolicy Retention { get; set; } = new();

    /// <summary>
    /// Row count that forces a mid-project commit so one abnormally large project cannot build an
    /// unbounded transaction (and WAL). Normal projects commit at their project boundary first.
    /// </summary>
    public int WriteBatchSize { get; set; } = 10_000;

    /// <summary><c>PRAGMA wal_autocheckpoint</c> in pages — bounds the write-ahead log during a run.</summary>
    public int WalAutocheckpointPages { get; set; } = 1_000;

    /// <summary><c>PRAGMA journal_size_limit</c> in bytes — caps the WAL file left on disk after a checkpoint.</summary>
    public long JournalSizeLimitBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>
    /// Feature flag for the document-oriented semantic extractor. When true (default) the indexer uses
    /// the single-pass, per-document usage-site extractor with compilation-scoped exact target
    /// resolution. When false it falls back to the legacy declaration-driven extractor (whole-solution
    /// <c>FindReferencesAsync</c> per declaration), retained as an emergency fallback. Defaulted on now
    /// that the new extractor is proven at parity with the legacy path. Overridable via
    /// <c>document_extractor</c> in <c>sextant.json</c> or the <c>SEXTANT_DOCUMENT_EXTRACTOR</c> env var.
    /// </summary>
    public bool DocumentExtractor { get; set; } = true;

    /// <summary>
    /// Maximum degree of parallelism for the document-oriented extractor's per-document analysis. 0
    /// (the default) means auto-resolve to the smaller of the processor count and an experimentally
    /// chosen cap. Analysis workers never touch SQLite — a single batched writer consumes their
    /// deterministically-ordered output through a bounded channel. Overridable via
    /// <c>max_parallelism</c> in <c>sextant.json</c> or the <c>SEXTANT_MAX_PARALLELISM</c> env var.
    /// </summary>
    public int MaxParallelism { get; set; }

    /// <summary>
    /// Capacity of the bounded channel that feeds extracted per-project contributions to the single
    /// SQLite writer. It bounds how many completed contribution sets can be buffered ahead of
    /// persistence (and thus outstanding contribution memory) before extraction blocks on
    /// backpressure. 0 (the default) means auto-resolve. Overridable via
    /// <c>extraction_queue_capacity</c> in <c>sextant.json</c> or the
    /// <c>SEXTANT_EXTRACTION_QUEUE_CAPACITY</c> env var.
    /// </summary>
    public int ExtractionQueueCapacity { get; set; }

    /// <summary>
    /// How often (seconds) the daemon runs an AUTHORITATIVE Git reconciliation pass that reconstructs
    /// the working-tree state from git (independent of file-watcher events) and refreshes it as a
    /// local overlay (Phase 10). The file watcher supplies only hints; this periodic pass is the
    /// backstop that guarantees the overlay converges to git state even if a watcher event is missed,
    /// and it re-resolves the solution + live <c>sextant.json</c> config each pass (issue #28). 0
    /// disables the periodic pass (startup reconciliation still runs). Overridable via
    /// <c>reconcile_interval_seconds</c> in <c>sextant.json</c> or <c>SEXTANT_RECONCILE_INTERVAL</c>.
    /// </summary>
    public int ReconcileIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Platform-routing policy (Phase 15): how the standalone index service routes platform-specific
    /// project graphs across worker capabilities. <c>auto</c> (default) escalates a project to a native
    /// Windows/macOS worker only when Linux evaluation is demonstrably insufficient AND a compatible
    /// worker exists; <c>linux_only</c> never leaves the default (Linux) worker. This is an orchestration
    /// choice consumed only by the multi-worker service — a local/single-node index ignores it and always
    /// evaluates in-process — so it is deliberately NOT folded into the configuration hash. Overridable
    /// via <c>platform_routing</c> in <c>sextant.json</c> or the <c>SEXTANT_PLATFORM_ROUTING</c> env var.
    /// </summary>
    public string PlatformRouting { get; set; } = "auto";

    private static readonly Regex ValidProfileName = new(@"^[a-zA-Z0-9_-]+$", RegexOptions.Compiled);

    public string LogsPath => Path.Combine(
        Path.GetDirectoryName(DbPath) ?? ".sextant/profiles/default", "logs");

    public static string ResolveDbPath(string? explicitDb, string? profileOverride, SextantConfiguration config)
    {
        if (explicitDb != null)
            return explicitDb;

        var profile = profileOverride
            ?? Environment.GetEnvironmentVariable("SEXTANT_PROFILE")
            ?? config.Profile;

        ValidateProfileName(profile);
        return $".sextant/profiles/{profile}/sextant.db";
    }

    public static void ValidateProfileName(string profile)
    {
        if (!ValidProfileName.IsMatch(profile))
            throw new ArgumentException(
                $"Invalid profile name '{profile}'. Only [a-zA-Z0-9_-] characters are allowed.");
    }

    public static void MigrateLegacyIfNeeded(string? repoRoot = null)
    {
        var root = repoRoot ?? FindRepoRoot(Directory.GetCurrentDirectory()) ?? ".";
        var legacyDb = Path.Combine(root, ".sextant", "sextant.db");
        var newDb = Path.Combine(root, ".sextant", "profiles", "default", "sextant.db");

        if (File.Exists(legacyDb) && !File.Exists(newDb))
        {
            try
            {
                var newDir = Path.GetDirectoryName(newDb)!;
                Directory.CreateDirectory(newDir);
                File.Move(legacyDb, newDb);

                var legacyLogs = Path.Combine(root, ".sextant", "logs");
                var newLogs = Path.Combine(newDir, "logs");
                if (Directory.Exists(legacyLogs) && !Directory.Exists(newLogs))
                    Directory.Move(legacyLogs, newLogs);

                Console.Error.WriteLine("Migrated index to profile 'default'. Use --profile <name> to create additional profiles.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: Failed to migrate legacy DB to profiles/default: {ex.Message}");
                Console.Error.WriteLine("Falling back to legacy path.");
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Load configuration with priority: defaults → sextant.json → environment variables.
    /// </summary>
    public static SextantConfiguration Load(string? repoRoot = null)
    {
        var config = new SextantConfiguration();

        // Try to load from sextant.json at repo root
        var root = repoRoot ?? FindRepoRoot(Directory.GetCurrentDirectory());
        if (root != null)
        {
            var configPath = Path.Combine(root, "sextant.json");
            if (File.Exists(configPath))
            {
                try
                {
                    var json = File.ReadAllText(configPath);
                    var fileConfig = JsonSerializer.Deserialize<SextantConfigFile>(json, JsonOptions);
                    if (fileConfig != null)
                    {
                        if (fileConfig.DbPath != null)
                            config.DbPath = fileConfig.DbPath;
                        if (fileConfig.MaxCallHierarchyDepth.HasValue)
                            config.MaxCallHierarchyDepth = fileConfig.MaxCallHierarchyDepth.Value;
                        if (fileConfig.FtsMaxResults.HasValue)
                            config.FtsMaxResults = fileConfig.FtsMaxResults.Value;
                        if (fileConfig.Solutions != null)
                            config.Solutions = fileConfig.Solutions;
                        if (fileConfig.Profile != null)
                            config.Profile = fileConfig.Profile;
                        if (fileConfig.DaemonSocket != null)
                            config.DaemonSocket = fileConfig.DaemonSocket;
                        if (fileConfig.AutoSpawnDaemon.HasValue)
                            config.AutoSpawnDaemon = fileConfig.AutoSpawnDaemon.Value;
                        if (fileConfig.WriteBatchSize.HasValue)
                            config.WriteBatchSize = fileConfig.WriteBatchSize.Value;
                        if (fileConfig.WalAutocheckpointPages.HasValue)
                            config.WalAutocheckpointPages = fileConfig.WalAutocheckpointPages.Value;
                        if (fileConfig.JournalSizeLimitBytes.HasValue)
                            config.JournalSizeLimitBytes = fileConfig.JournalSizeLimitBytes.Value;
                        if (fileConfig.DocumentExtractor.HasValue)
                            config.DocumentExtractor = fileConfig.DocumentExtractor.Value;
                        if (fileConfig.MaxParallelism.HasValue)
                            config.MaxParallelism = fileConfig.MaxParallelism.Value;
                        if (fileConfig.ExtractionQueueCapacity.HasValue)
                            config.ExtractionQueueCapacity = fileConfig.ExtractionQueueCapacity.Value;
                        if (fileConfig.ReconcileIntervalSeconds.HasValue)
                            config.ReconcileIntervalSeconds = fileConfig.ReconcileIntervalSeconds.Value;
                        if (fileConfig.PlatformRouting != null)
                            config.PlatformRouting = fileConfig.PlatformRouting;
                        if (fileConfig.IndexingProfile != null)
                            config.IndexingProfile = fileConfig.IndexingProfile;
                        if (fileConfig.GeneratedSourcePolicy != null)
                            config.GeneratedSourcePolicy = fileConfig.GeneratedSourcePolicy;
                        if (fileConfig.Retention != null)
                        {
                            if (fileConfig.Retention.KeepCompleteGenerations.HasValue)
                                config.Retention.KeepCompleteGenerations = fileConfig.Retention.KeepCompleteGenerations.Value;
                            if (fileConfig.Retention.ApiSnapshotKeepCommits.HasValue)
                                config.Retention.ApiSnapshotKeepCommits = fileConfig.Retention.ApiSnapshotKeepCommits.Value;
                            if (fileConfig.Retention.PruneSupersededSourceBlobs.HasValue)
                                config.Retention.PruneSupersededSourceBlobs = fileConfig.Retention.PruneSupersededSourceBlobs.Value;
                        }
                    }
                }
                catch (JsonException)
                {
                    // Invalid JSON — fall through to defaults + env vars
                }
            }
        }

        // Environment variables override file settings
        ApplyEnvironmentVariables(config);

        return config;
    }

    /// <summary>
    /// Load configuration from environment variables only (legacy behavior).
    /// </summary>
    public static SextantConfiguration FromEnvironment()
    {
        var config = new SextantConfiguration();
        ApplyEnvironmentVariables(config);
        return config;
    }

    private static void ApplyEnvironmentVariables(SextantConfiguration config)
    {
        var profile = Environment.GetEnvironmentVariable("SEXTANT_PROFILE");
        if (!string.IsNullOrEmpty(profile))
            config.Profile = profile;

        var dbPath = Environment.GetEnvironmentVariable("SEXTANT_DB_PATH");
        if (!string.IsNullOrEmpty(dbPath))
            config.DbPath = dbPath;

        var maxDepth = Environment.GetEnvironmentVariable("SEXTANT_MAX_DEPTH");
        if (int.TryParse(maxDepth, out var depth))
            config.MaxCallHierarchyDepth = depth;

        var ftsMax = Environment.GetEnvironmentVariable("SEXTANT_FTS_MAX");
        if (int.TryParse(ftsMax, out var max))
            config.FtsMaxResults = max;

        var daemonSocket = Environment.GetEnvironmentVariable("SEXTANT_DAEMON_SOCKET");
        if (!string.IsNullOrEmpty(daemonSocket))
            config.DaemonSocket = daemonSocket;

        var autoSpawn = Environment.GetEnvironmentVariable("SEXTANT_AUTO_SPAWN_DAEMON");
        if (!string.IsNullOrEmpty(autoSpawn))
            config.AutoSpawnDaemon = !(autoSpawn == "false" || autoSpawn == "0");

        var writeBatch = Environment.GetEnvironmentVariable("SEXTANT_WRITE_BATCH_SIZE");
        if (int.TryParse(writeBatch, out var batch))
            config.WriteBatchSize = batch;

        var walAutocheckpoint = Environment.GetEnvironmentVariable("SEXTANT_WAL_AUTOCHECKPOINT");
        if (int.TryParse(walAutocheckpoint, out var walPages))
            config.WalAutocheckpointPages = walPages;

        var journalLimit = Environment.GetEnvironmentVariable("SEXTANT_JOURNAL_SIZE_LIMIT");
        if (long.TryParse(journalLimit, out var journalBytes))
            config.JournalSizeLimitBytes = journalBytes;

        var documentExtractor = Environment.GetEnvironmentVariable("SEXTANT_DOCUMENT_EXTRACTOR");
        if (!string.IsNullOrEmpty(documentExtractor))
            config.DocumentExtractor = !(documentExtractor == "false" || documentExtractor == "0");

        var maxParallelism = Environment.GetEnvironmentVariable("SEXTANT_MAX_PARALLELISM");
        if (int.TryParse(maxParallelism, out var parallelism))
            config.MaxParallelism = parallelism;

        var queueCapacity = Environment.GetEnvironmentVariable("SEXTANT_EXTRACTION_QUEUE_CAPACITY");
        if (int.TryParse(queueCapacity, out var capacity))
            config.ExtractionQueueCapacity = capacity;

        var reconcileInterval = Environment.GetEnvironmentVariable("SEXTANT_RECONCILE_INTERVAL");
        if (int.TryParse(reconcileInterval, out var interval))
            config.ReconcileIntervalSeconds = interval;

        var platformRouting = Environment.GetEnvironmentVariable("SEXTANT_PLATFORM_ROUTING");
        if (!string.IsNullOrWhiteSpace(platformRouting))
            config.PlatformRouting = platformRouting;

        var indexingProfile = Environment.GetEnvironmentVariable("SEXTANT_INDEXING_PROFILE");
        if (!string.IsNullOrEmpty(indexingProfile))
            config.IndexingProfile = indexingProfile;

        var generatedPolicy = Environment.GetEnvironmentVariable("SEXTANT_GENERATED_SOURCE_POLICY");
        if (!string.IsNullOrEmpty(generatedPolicy))
            config.GeneratedSourcePolicy = generatedPolicy;

        var keepGenerations = Environment.GetEnvironmentVariable("SEXTANT_RETENTION_KEEP_GENERATIONS");
        if (int.TryParse(keepGenerations, out var generations))
            config.Retention.KeepCompleteGenerations = generations;

        var apiKeepCommits = Environment.GetEnvironmentVariable("SEXTANT_RETENTION_API_KEEP_COMMITS");
        if (int.TryParse(apiKeepCommits, out var commits))
            config.Retention.ApiSnapshotKeepCommits = commits;

        var pruneBlobs = Environment.GetEnvironmentVariable("SEXTANT_RETENTION_PRUNE_SOURCE_BLOBS");
        if (!string.IsNullOrWhiteSpace(pruneBlobs))
        {
            // Destructive default-on flag: parse leniently but treat any negative spelling as "off"
            // (trimmed, case-insensitive), so "False"/"OFF"/" no " never silently keep pruning enabled.
            var normalized = pruneBlobs.Trim().ToLowerInvariant();
            config.Retention.PruneSupersededSourceBlobs =
                normalized is not ("false" or "0" or "no" or "off");
        }
    }

    public static string? FindRepoRoot(string startDir)
    {
        var dir = startDir;
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    /// <summary>
    /// Deserialization model for sextant.json — all fields nullable to distinguish
    /// "not set" from "set to default".
    /// </summary>
    private sealed class SextantConfigFile
    {
        [JsonPropertyName("db_path")]
        public string? DbPath { get; set; }

        [JsonPropertyName("profile")]
        public string? Profile { get; set; }

        [JsonPropertyName("max_call_hierarchy_depth")]
        public int? MaxCallHierarchyDepth { get; set; }

        [JsonPropertyName("fts_max_results")]
        public int? FtsMaxResults { get; set; }

        [JsonPropertyName("solutions")]
        public List<string>? Solutions { get; set; }

        [JsonPropertyName("daemon_socket")]
        public string? DaemonSocket { get; set; }

        [JsonPropertyName("auto_spawn_daemon")]
        public bool? AutoSpawnDaemon { get; set; }

        [JsonPropertyName("write_batch_size")]
        public int? WriteBatchSize { get; set; }

        [JsonPropertyName("wal_autocheckpoint_pages")]
        public int? WalAutocheckpointPages { get; set; }

        [JsonPropertyName("journal_size_limit_bytes")]
        public long? JournalSizeLimitBytes { get; set; }

        [JsonPropertyName("document_extractor")]
        public bool? DocumentExtractor { get; set; }

        [JsonPropertyName("max_parallelism")]
        public int? MaxParallelism { get; set; }

        [JsonPropertyName("extraction_queue_capacity")]
        public int? ExtractionQueueCapacity { get; set; }

        [JsonPropertyName("reconcile_interval_seconds")]
        public int? ReconcileIntervalSeconds { get; set; }

        [JsonPropertyName("platform_routing")]
        public string? PlatformRouting { get; set; }

        [JsonPropertyName("indexing_profile")]
        public string? IndexingProfile { get; set; }

        [JsonPropertyName("generated_source_policy")]
        public string? GeneratedSourcePolicy { get; set; }

        [JsonPropertyName("retention")]
        public RetentionConfigFile? Retention { get; set; }
    }

    /// <summary>Deserialization model for the <c>retention</c> object — all fields nullable.</summary>
    private sealed class RetentionConfigFile
    {
        [JsonPropertyName("keep_complete_generations")]
        public int? KeepCompleteGenerations { get; set; }

        [JsonPropertyName("api_snapshot_keep_commits")]
        public int? ApiSnapshotKeepCommits { get; set; }

        [JsonPropertyName("prune_superseded_source_blobs")]
        public bool? PruneSupersededSourceBlobs { get; set; }
    }
}
