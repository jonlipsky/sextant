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
    }
}
