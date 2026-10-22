using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sextant.Indexer;

namespace Sextant.Service.Restore;

/// <summary>Where a repository's restore state is kept, and the package folder its assets files point into (issue #272).</summary>
/// <param name="StateDirectory">The repository's directory under <see cref="ServicePaths.RestoreStateRoot"/>.</param>
/// <param name="PackagesDirectory">The repository's persistent NuGet package folder the restore writes into.</param>
public sealed record RestoreReuse(string StateDirectory, string PackagesDirectory);

/// <summary>What the restore-state cache did for one job (issue #272).</summary>
public sealed record RestoreReuseOutcome
{
    /// <summary><c>reused</c> (restore skipped, assets replayed), <c>miss</c> (restore ran) or <c>unavailable</c> (could not decide).</summary>
    public required string Decision { get; init; }

    /// <summary>
    /// Why a miss was a miss (<c>no_state</c>, <c>inputs_changed</c>, <c>packages_missing</c>, <c>floating_versions</c>,
    /// <c>legacy_restore</c>, <c>symlink_outside</c>, <c>checkout_not_clean</c>, <c>replay_failed</c>, ...).
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>Projects whose assets files the state holds (replayed on a reuse, saved after a miss).</summary>
    public int Projects { get; init; }

    /// <summary>Files written into the checkout on a reuse.</summary>
    public int FilesWritten { get; init; }

    /// <summary>True when this job saved its restore result as the repository's new state.</summary>
    public bool Saved { get; init; }

    /// <summary>Why nothing was saved after a miss, when it was not (<c>incomplete</c>, <c>legacy_restore</c>, <c>stale_outputs</c>, ...).</summary>
    public string? SaveSkipped { get; init; }

    /// <summary>Wall time of hashing the restore inputs (twice after a miss), in milliseconds.</summary>
    public long FingerprintMs { get; init; }

    /// <summary>Wall time of the replay (reuse) or the save (miss), in milliseconds.</summary>
    public long StateMs { get; init; }

    /// <summary>The first 12 hex characters of the inputs fingerprint, for correlating jobs in logs.</summary>
    public string? FingerprintPrefix { get; init; }
}

/// <summary>
/// A repository's restore state (issue #272): the NuGet assets files (<c>project.assets.json</c>,
/// <c>project.nuget.cache</c>, <c>*.nuget.g.props</c>/<c>*.nuget.g.targets</c>) of its last CLEAN restore, keyed by a
/// fingerprint of every input of that restore. A new commit is checked out fresh, with no restore output, so without
/// this every job paid the whole evaluation-bound restore even when nothing that restore reads had changed.
/// <para>
/// The rule is: when in doubt, restore. A replay must never produce different index results from a full restore. A job
/// skips restore only when the fingerprint of every project, props, targets, solution, lock, <c>global.json</c> and
/// <c>nuget.config</c> file under the checkout (and the NuGet and MSBuild files above it), the restore arguments and
/// environment, the installed SDKs and workloads, the indexer's analyzer version and the runner's policy is identical;
/// no package version floats; no legacy (<c>packages.config</c>, <c>*.nuspec</c>, <c>paket.*</c>) input exists; the
/// checkout holds no restore output of its own; and every package each saved assets file names is still in the package
/// folder. It then writes back the very files that restore produced, staged and renamed into place, so the indexer
/// reads byte-identical assets; any failure removes what was written and restores in full.
/// </para>
/// <para>
/// A save needs the same discipline: every project the solutions declare must yield an assets file (found wherever
/// its restore wrote it, via <c>project.restore.outputPath</c>) with a <c>project.nuget.cache</c>, and the checkout
/// must have held no restore output before the restore ran. <b>Known limit:</b> a package republished under the same
/// id and version (a mutable feed) is not detected, exactly as a warm package folder does not detect it.
/// </para>
/// <para>
/// Isolation: one state directory per repository (<see cref="ServicePaths.RestoreStateRoot"/>); the fingerprint binds
/// the repository's own checkout and package-folder paths, so a state is never replayed into another repository.
/// Only a clean restore is saved.
/// </para>
/// </summary>
internal static class RestoreStateStore
{
    internal const string FormatVersion = "2";
    internal const string ManifestName = "state.json";
    private const string CurrentName = "current";
    private const string FilesName = "files";
    private const string GitName = ".git";
    private const string AssetsName = "project.assets.json";
    private const string CacheName = "project.nuget.cache";
    private const long MaxStateBytes = 512L * 1024 * 1024;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static readonly HashSet<string> InputExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csproj", ".vbproj", ".fsproj", ".proj", ".props", ".targets", ".sln", ".slnx", ".slnf", ".rsp", ".user",
        ".projitems", ".shproj", ".wapproj", ".sqlproj", ".dcproj", ".nuproj"
    };

    private static readonly HashSet<string> InputNames = new(StringComparer.OrdinalIgnoreCase)
        { "global.json", "nuget.config", "packages.lock.json" };

    // Restore flows this cache does not model: never saved, never replayed. The names are hashed like any input.
    private static readonly HashSet<string> LegacyNames = new(StringComparer.OrdinalIgnoreCase)
        { "packages.config", "paket.dependencies", "paket.lock", "paket.references", "paket.template" };

    // What MSBuild and NuGet read from the directories ABOVE the checkout.
    private static readonly HashSet<string> AncestorNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "global.json", "nuget.config", "directory.build.props", "directory.build.targets", "directory.packages.props",
        "directory.solution.props", "directory.solution.targets", "directory.build.rsp", "msbuild.rsp"
    };

    private static readonly HashSet<string> NonRestoredProjectExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".shproj", ".projitems" };

    private static readonly string[] EnvironmentPrefixes = ["NUGET_", "DOTNET_", "MSBUILD"];

    // A per-job scratch path: it differs on every job and decides nothing a restore writes.
    private static readonly HashSet<string> VolatileEnvironment = new(StringComparer.OrdinalIgnoreCase) { "DOTNET_CLI_HOME" };

    // A version that can resolve differently tomorrow: a wildcard, or a range with no lower bound.
    private static readonly Regex FloatingAttribute = new(
        @"\bVersion(?:Override)?\s*=\s*""(?:[^""]*\*|\s*[\[(]\s*,)", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex FloatingElement = new(
        @"<([A-Za-z0-9_.]*Version(?:Override)?)>\s*(?:[^<]*\*|[\[(]\s*,)", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly HashSet<string> NonPackageVersionElements = new(StringComparer.OrdinalIgnoreCase)
        { "AssemblyVersion", "FileVersion", "ApplicationVersion", "InformationalVersion", "AssemblyInformationalVersion", "ProductVersion" };

    /// <param name="Fingerprint">SHA-256 over every restore input and the restore context.</param>
    /// <param name="Floating">A package version can resolve differently tomorrow.</param>
    /// <param name="FileCount">Input files hashed.</param>
    internal sealed record Inputs(string Fingerprint, bool Floating, int FileCount)
    {
        /// <summary><c>legacy_restore</c> or <c>symlink_outside</c> when the checkout must be restored in full and never saved; else null.</summary>
        public string? Refusal { get; init; }

        /// <summary>Checkout-relative ('/') paths of restore output files (assets, cache, generated props/targets, dgspec) already present.</summary>
        public IReadOnlyList<string> ExistingOutputs { get; init; } = [];
    }

    private sealed class Manifest
    {
        public string Version { get; set; } = string.Empty;
        public string Fingerprint { get; set; } = string.Empty;
        public int Projects { get; set; }
        public List<string> Files { get; set; } = [];
    }

    /// <summary>
    /// Hashes every restore input under <paramref name="checkoutDir"/> together with <paramref name="context"/> (the
    /// caller's restore parameters), or returns null when the checkout cannot be read completely. Restore inputs are
    /// found anywhere under the checkout (<c>bin</c>, <c>obj</c> and <c>node_modules</c> included; only <c>.git</c> is
    /// skipped), by name, so a custom intermediate or artifacts directory is covered. A symbolic link to a directory
    /// inside the checkout is recorded and not entered (its contents are hashed where they live); a link to a
    /// directory outside it, or a restore input that is a link leaving the checkout, refuses reuse.
    /// </summary>
    internal static Inputs? Compute(string checkoutDir, string context, CancellationToken cancellationToken)
    {
        try
        {
            var root = TrimRoot(Path.GetFullPath(checkoutDir));
            var files = new List<string>();
            var links = new List<string>();
            var existing = new List<string>();
            var legacy = false;
            var outside = false;
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dir = pending.Pop();
                foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                {
                    if (entry.Name == GitName)
                        continue;
                    var isLink = entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
                    if (entry is DirectoryInfo)
                    {
                        if (!isLink)
                        {
                            pending.Push(entry.FullName);
                            continue;
                        }
                        var target = ResolveInside(root, entry);
                        if (target is null)
                            outside = true;
                        else
                            links.Add(Relative(root, entry.FullName) + "->" + Relative(root, target));
                        continue;
                    }

                    if (IsRestoreOutput(entry.Name))
                    {
                        existing.Add(Relative(root, entry.FullName));
                        continue;
                    }
                    var isLegacy = LegacyNames.Contains(entry.Name) || Path.GetExtension(entry.Name).Equals(".nuspec", StringComparison.OrdinalIgnoreCase);
                    if (!isLegacy && !IsInput(entry.Name))
                        continue;
                    legacy |= isLegacy;
                    if (isLink)
                    {
                        var target = ResolveInside(root, entry);
                        if (target is null || !File.Exists(target))
                        {
                            outside = true;
                            continue;
                        }
                        links.Add(Relative(root, entry.FullName) + "->" + Relative(root, target));
                    }
                    files.Add(entry.FullName);
                }
            }

            files.Sort(StringComparer.Ordinal);
            links.Sort(StringComparer.Ordinal);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Append(hash, FormatVersion);
            Append(hash, context);
            foreach (var link in links)
                Append(hash, "link:" + link);
            var floating = false;
            foreach (var file in files)
            {
                var bytes = File.ReadAllBytes(file);
                Append(hash, Relative(root, file));
                hash.AppendData(SHA256.HashData(bytes));
                var extension = Path.GetExtension(file);
                if (!floating && IsMsBuildText(extension) && HasFloatingVersion(Encoding.UTF8.GetString(bytes)))
                    floating = true;
            }

            existing.Sort(StringComparer.Ordinal);
            return new Inputs(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), floating, files.Count)
            {
                Refusal = legacy ? "legacy_restore" : outside ? "symlink_outside" : null,
                ExistingOutputs = existing
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The restore parameters that decide what a restore writes for unchanged files: the checkout and package folder
    /// locations, the indexer analyzer version and the runner policy (<paramref name="analyzerVersion"/>,
    /// <paramref name="runnerPolicy"/>: a change to either must never replay older output), the full restore argument
    /// lists, the NuGet/DOTNET/MSBuild environment of the restore process (values hashed, never kept), the installed
    /// SDKs and workloads, every NuGet config the restore reads from outside the checkout, MSBuild files above the
    /// checkout, and the hosts a credential exists for. Never a credential.
    /// </summary>
    internal static string Context(
        string checkoutDir, string packagesDirectory, IEnumerable<string> solutions, IEnumerable<string> restoreArguments,
        IEnumerable<KeyValuePair<string, string?>> environment, IEnumerable<string> credentialHosts, string dotnetPath,
        string analyzerVersion, string runnerPolicy)
    {
        var root = TrimRoot(Path.GetFullPath(checkoutDir));
        var builder = new StringBuilder();
        builder.Append("checkout=").Append(root).Append('\n');
        builder.Append("packages=").Append(Path.GetFullPath(packagesDirectory)).Append('\n');
        builder.Append("analyzer=").Append(analyzerVersion).Append('\n');
        builder.Append("runner_policy=").Append(runnerPolicy).Append('\n');
        foreach (var solution in solutions.Select(s => Relative(root, Path.GetFullPath(s))).Order(StringComparer.Ordinal))
            builder.Append("solution=").Append(solution).Append('\n');
        foreach (var argument in restoreArguments)
            builder.Append("arg=").Append(argument).Append('\n');
        foreach (var (name, value) in environment.Where(IsRestoreEnvironment).OrderBy(e => e.Key, StringComparer.Ordinal))
            builder.Append("env=").Append(name).Append('=').Append(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? "\0")))).Append('\n');
        foreach (var host in credentialHosts.Order(StringComparer.OrdinalIgnoreCase))
            builder.Append("credential_host=").Append(host).Append('\n');
        builder.Append("runtime=").Append(Environment.Version).Append('\n');
        var dotnetRoot = DotnetRoot(dotnetPath);
        builder.Append("sdks=").Append(string.Join(',', Listing(dotnetRoot, "sdk", 1))).Append('\n');
        builder.Append("workloads=").Append(string.Join(',',
            Listing(dotnetRoot, "sdk-manifests", 3).Concat(Listing(dotnetRoot, Path.Combine("metadata", "workloads"), 3)).Concat(Listing(dotnetRoot, "packs", 2)))).Append('\n');
        AppendNuGetConfigs(builder);
        AppendAncestors(builder, root);
        return builder.ToString();
    }

    private static bool IsRestoreEnvironment(KeyValuePair<string, string?> variable)
        => !VolatileEnvironment.Contains(variable.Key)
            && EnvironmentPrefixes.Any(prefix => variable.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static string DotnetRoot(string dotnetPath)
    {
        try
        {
            if (Path.IsPathRooted(dotnetPath) && File.Exists(dotnetPath))
                return Path.GetDirectoryName(new FileInfo(dotnetPath).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? dotnetPath) ?? string.Empty;
            return Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return string.Empty;
        }
    }

    // Relative directory names below <dotnetRoot>/<sub> down to <depth> levels (SDK, workload manifest and pack versions).
    private static IEnumerable<string> Listing(string dotnetRoot, string sub, int depth)
    {
        var names = new List<string>();
        try
        {
            var start = Path.Combine(dotnetRoot, sub);
            if (dotnetRoot.Length > 0 && Directory.Exists(start))
                Collect(start, start, depth, names);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            names.Add("unreadable:" + Guid.NewGuid().ToString("N"));
        }
        names.Sort(StringComparer.Ordinal);
        return names.Select(n => sub + ":" + n);

        static void Collect(string start, string dir, int remaining, List<string> into)
        {
            foreach (var child in Directory.EnumerateDirectories(dir))
            {
                into.Add(Path.GetRelativePath(start, child).Replace('\\', '/'));
                if (remaining > 1)
                    Collect(start, child, remaining - 1, into);
            }
        }
    }

    // The NuGet.Config files NuGet merges besides the checkout's own: user level, machine wide.
    private static void AppendNuGetConfigs(StringBuilder builder)
    {
        var directories = new List<(string Path, bool Recursive)>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            directories.Add((Path.Combine(home, ".nuget", "NuGet"), false));
            directories.Add((Path.Combine(home, ".config", "NuGet"), false));
        }
        if (Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg)
            directories.Add((Path.Combine(xdg, "NuGet"), false));
        directories.Add(("/etc/opt/NuGet/Config", true));
        directories.Add(("/Library/Application Support/NuGet/Config", true));
        if (Environment.GetEnvironmentVariable("ProgramFiles(x86)") is { Length: > 0 } programFiles)
            directories.Add((Path.Combine(programFiles, "NuGet", "Config"), true));
        if (Environment.GetEnvironmentVariable("APPDATA") is { Length: > 0 } appData)
            directories.Add((Path.Combine(appData, "NuGet"), false));

        foreach (var (directory, recursive) in directories.DistinctBy(d => d.Path, StringComparer.Ordinal))
        {
            try
            {
                if (!Directory.Exists(directory))
                    continue;
                var options = new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, RecurseSubdirectories = recursive };
                foreach (var file in Directory.EnumerateFiles(directory, "*.config", options).Order(StringComparer.Ordinal))
                    builder.Append("nuget_config=").Append(file).Append(':').Append(Sha(file)).Append('\n');
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                builder.Append("nuget_config_unreadable=").Append(directory).Append(':').Append(Guid.NewGuid().ToString("N")).Append('\n');
            }
        }
    }

    // NuGet.Config, Directory.Build.* and global.json in every directory above the checkout apply to it.
    private static void AppendAncestors(StringBuilder builder, string root)
    {
        for (var dir = Path.GetDirectoryName(root); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir).Where(f => AncestorNames.Contains(Path.GetFileName(f))).Order(StringComparer.Ordinal))
                    builder.Append("ancestor=").Append(file).Append(':').Append(Sha(file)).Append('\n');
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                builder.Append("ancestor_unreadable=").Append(dir).Append(':').Append(Guid.NewGuid().ToString("N")).Append('\n');
            }
        }
    }

    private static string Sha(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

    internal static bool HasFloatingVersion(string text) => HasFloatingVersion(text, FloatingAttribute, FloatingElement);

    // A match timeout cannot prove the text stable, so it counts as floating (a full restore).
    internal static bool HasFloatingVersion(string text, Regex attribute, Regex element)
    {
        try
        {
            if (attribute.IsMatch(text))
                return true;
            foreach (Match match in element.Matches(text))
            {
                if (!NonPackageVersionElements.Contains(match.Groups[1].Value))
                    return true;
            }
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }

    private static bool IsInput(string name) => InputNames.Contains(name) || InputExtensions.Contains(Path.GetExtension(name));

    private static bool IsMsBuildText(string extension) => extension.EndsWith("proj", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".props", StringComparison.OrdinalIgnoreCase) || extension.Equals(".targets", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".projitems", StringComparison.OrdinalIgnoreCase);

    private static void Append(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData([0]);
    }

    private static bool IsRestoreOutput(string fileName)
        => fileName.Equals(AssetsName, StringComparison.OrdinalIgnoreCase)
            || fileName.Equals(CacheName, StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".nuget.g.props", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".nuget.g.targets", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".nuget.dgspec.json", StringComparison.OrdinalIgnoreCase);

    // The files a saved project carries: the ones the indexer's load reads.
    private static bool IsSavedOutput(string fileName)
        => fileName.Equals(AssetsName, StringComparison.OrdinalIgnoreCase)
            || fileName.Equals(CacheName, StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".nuget.g.props", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".nuget.g.targets", StringComparison.OrdinalIgnoreCase);

    private static string TrimRoot(string root) => Path.TrimEndingDirectorySeparator(root);

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static bool IsInside(string root, string path)
        => path.Equals(root, PathComparison) || path.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);

    private static string? ResolveInside(string root, FileSystemInfo link)
    {
        var target = link.ResolveLinkTarget(returnFinalTarget: true);
        if (target is null)
            return null;
        var full = Path.GetFullPath(target.FullName);
        return IsInside(root, full) ? full : null;
    }

    /// <summary>
    /// Replays the saved assets into <paramref name="checkoutDir"/> when the saved state matches
    /// <paramref name="inputs"/>, the checkout holds no restore output the state does not own, and every package each
    /// saved assets file names is present. Writes are staged (a temp file per target, then renamed); any failure removes
    /// what was written. Null reason = replayed.
    /// </summary>
    internal static (string? MissReason, int Projects, int FilesWritten) TryReplay(
        RestoreReuse reuse, string checkoutDir, Inputs inputs, Action<string>? log)
    {
        var temps = new List<string>();
        var renamed = new List<string>();
        var createdDirectories = new List<string>();
        try
        {
            if (inputs.Refusal is not null)
                return (inputs.Refusal, 0, 0);
            var current = Path.Combine(reuse.StateDirectory, CurrentName);
            var manifestPath = Path.Combine(current, ManifestName);
            if (!File.Exists(manifestPath))
                return ("no_state", 0, 0);
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath), Json);
            if (manifest is null || manifest.Version != FormatVersion || manifest.Files.Count == 0)
                return ("no_state", 0, 0);
            if (!string.Equals(manifest.Fingerprint, inputs.Fingerprint, StringComparison.Ordinal))
                return ("inputs_changed", 0, 0);

            var root = TrimRoot(Path.GetFullPath(checkoutDir));
            var filesRoot = Path.Combine(current, FilesName);
            var plan = new List<(string Relative, string Blob, string Target)>();
            foreach (var relative in manifest.Files)
            {
                var target = ConfinedTarget(root, relative);
                var blob = Path.Combine(filesRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                if (target is null || !File.Exists(blob))
                    return ("state_unreadable", 0, 0);
                plan.Add((relative, blob, target));
            }

            // Every saved project needs its assets and its cache; no project, no state.
            var assets = plan.Where(p => Path.GetFileName(p.Target).Equals(AssetsName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (assets.Count == 0 || assets.Count != manifest.Projects)
                return ("state_unreadable", 0, 0);
            if (assets.Any(a => !plan.Any(p => p.Target.Equals(Path.Combine(Path.GetDirectoryName(a.Target)!, CacheName), PathComparison))))
                return ("state_unreadable", 0, 0);

            // A checkout that already holds restore output (a reused one) is restored in full: a replay only ever goes
            // into a clean obj state, so it never mixes with files an earlier restore left.
            if (inputs.ExistingOutputs.Count > 0)
                return ("checkout_not_clean", 0, 0);

            foreach (var item in plan)
            {
                if (HasLinkBetween(root, item.Target))
                    return ("link_in_checkout", 0, 0);
            }

            foreach (var (_, blob, _) in plan.Where(p => Path.GetFileName(p.Target).Equals(AssetsName, StringComparison.OrdinalIgnoreCase)))
            {
                if (!PackagesPresent(blob))
                    return ("packages_missing", 0, 0);
            }
            foreach (var (_, blob, _) in plan.Where(p => Path.GetFileName(p.Target).Equals(CacheName, StringComparison.OrdinalIgnoreCase)))
            {
                if (!ExpectedPackageFilesPresent(blob))
                    return ("packages_missing", 0, 0);
            }

            foreach (var (_, blob, target) in plan)
            {
                var directory = Path.GetDirectoryName(target)!;
                CreateTracked(directory, createdDirectories);
                var temp = Path.Combine(directory, $".sextant-replay-{Guid.NewGuid():N}.tmp");
                temps.Add(temp);
                File.Copy(blob, temp);
            }
            for (var i = 0; i < plan.Count; i++)
            {
                File.Move(temps[i], plan[i].Target, overwrite: true);
                renamed.Add(plan[i].Target);
            }
            return (null, manifest.Projects, plan.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            var written = renamed.Count > 0 || temps.Count > 0 || createdDirectories.Count > 0;
            Rollback(temps, renamed, createdDirectories);
            log?.Invoke($"package restore state could not be replayed: {ex.GetType().Name}.");
            return (written ? "replay_failed" : "state_unreadable", 0, 0);
        }
    }

    private static void CreateTracked(string directory, List<string> created)
    {
        var missing = new Stack<string>();
        for (var dir = directory; dir is not null && !Directory.Exists(dir); dir = Path.GetDirectoryName(dir))
            missing.Push(dir);
        Directory.CreateDirectory(directory);
        foreach (var dir in missing)
            created.Add(dir);
    }

    private static void Rollback(List<string> temps, List<string> renamed, List<string> createdDirectories)
    {
        foreach (var path in temps.Concat(renamed))
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the full restore rewrites it */ }
        }
        for (var i = createdDirectories.Count - 1; i >= 0; i--)
        {
            try
            {
                if (Directory.Exists(createdDirectories[i]) && !Directory.EnumerateFileSystemEntries(createdDirectories[i]).Any())
                    Directory.Delete(createdDirectories[i]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* left empty */ }
        }
    }

    // project.assets.json names every package the project compiles against (libraries) and every download-only
    // dependency; each must be in one of the folders the assets file lists, with the marker NuGet writes last.
    private static bool PackagesPresent(string assetsPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(assetsPath));
        var rootElement = document.RootElement;
        var folders = new List<string>();
        if (rootElement.TryGetProperty("packageFolders", out var packageFolders) && packageFolders.ValueKind == JsonValueKind.Object)
            folders.AddRange(packageFolders.EnumerateObject().Select(f => f.Name));
        if (folders.Count == 0)
            return false;

        if (rootElement.TryGetProperty("libraries", out var libraries) && libraries.ValueKind == JsonValueKind.Object)
        {
            foreach (var library in libraries.EnumerateObject())
            {
                if (!library.Value.TryGetProperty("type", out var type) || type.GetString() != "package")
                    continue;
                if (!library.Value.TryGetProperty("path", out var path) || path.GetString() is not { Length: > 0 } relative
                    || !InAnyFolder(folders, relative))
                    return false;
            }
        }

        if (rootElement.TryGetProperty("project", out var project) && project.TryGetProperty("frameworks", out var frameworks)
            && frameworks.ValueKind == JsonValueKind.Object)
        {
            foreach (var framework in frameworks.EnumerateObject())
            {
                if (!framework.Value.TryGetProperty("downloadDependencies", out var downloads) || downloads.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var download in downloads.EnumerateArray())
                {
                    var name = download.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var version = download.TryGetProperty("version", out var v) ? LowerBound(v.GetString()) : null;
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(version)
                        || !InAnyFolder(folders, name.ToLowerInvariant() + "/" + version.ToLowerInvariant()))
                        return false;
                }
            }
        }
        return true;
    }

    private static bool InAnyFolder(List<string> folders, string relative)
        => folders.Any(folder => File.Exists(Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar), ".nupkg.metadata")));

    // "[8.0.0, 8.0.0]" -> "8.0.0"; a floating or unbounded range has no single package to check.
    private static string? LowerBound(string? range)
    {
        if (string.IsNullOrWhiteSpace(range))
            return null;
        var text = range.Trim().TrimStart('[', '(');
        var comma = text.IndexOf(',');
        text = (comma >= 0 ? text[..comma] : text).Trim().TrimEnd(']', ')');
        return text.Length == 0 || text.Contains('*') ? null : text;
    }

    // project.nuget.cache lists the package files restore expected to find.
    private static bool ExpectedPackageFilesPresent(string nugetCache)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(nugetCache));
        if (!document.RootElement.TryGetProperty("expectedPackageFiles", out var expected) || expected.ValueKind != JsonValueKind.Array)
            return true;
        foreach (var item in expected.EnumerateArray())
        {
            if (item.GetString() is { Length: > 0 } path && !File.Exists(path))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Saves the restore result of <paramref name="checkoutDir"/> as the repository's state. Every project the
    /// <paramref name="solutions"/> declare must have an assets file (located through its own
    /// <c>project.restore.outputPath</c>, wherever the restore wrote it) next to a <c>project.nuget.cache</c>; the
    /// checkout must have held no restore output before the restore. Returns the number of projects saved, or null
    /// (with <paramref name="skipReason"/>) when nothing was saved. Never throws.
    /// </summary>
    internal static int? Save(
        RestoreReuse reuse, string checkoutDir, Inputs inputs, IReadOnlyList<string> solutions, Action<string>? log, out string? skipReason)
    {
        skipReason = null;
        string? temp = null;
        try
        {
            if (inputs.Refusal is not null)
            {
                skipReason = inputs.Refusal;
                return null;
            }
            if (inputs.ExistingOutputs.Count > 0)
            {
                skipReason = "stale_outputs";
                return null;
            }

            var root = TrimRoot(Path.GetFullPath(checkoutDir));
            var declared = solutions.SelectMany(SolutionProjectEnumerator.Enumerate).Select(Path.GetFullPath)
                .Where(p => !NonRestoredProjectExtensions.Contains(Path.GetExtension(p))).Distinct(StringComparer.Ordinal).ToList();
            if (declared.Count == 0)
            {
                skipReason = "no_assets";
                return null;
            }

            var located = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var assets in FindAssets(root))
            {
                var identity = ReadIdentity(assets);
                if (identity is null || !IsInside(root, identity.Value.ProjectPath)
                    || !string.Equals(identity.Value.OutputPath, TrimRoot(Path.GetDirectoryName(assets)!), PathComparison)
                    || !located.TryAdd(identity.Value.ProjectPath, assets))
                {
                    skipReason = "incomplete";
                    return null;
                }
            }
            foreach (var project in declared)
            {
                if (!located.TryGetValue(project, out var assets)
                    || !File.Exists(Path.Combine(Path.GetDirectoryName(assets)!, CacheName)))
                {
                    skipReason = "incomplete";
                    return null;
                }
            }

            var files = new List<string>();
            long bytes = 0;
            foreach (var assets in located.Values)
            {
                foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(assets)!, "*", SearchOption.TopDirectoryOnly))
                {
                    if (!IsSavedOutput(Path.GetFileName(file)))
                        continue;
                    var info = new FileInfo(file);
                    if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null)
                    {
                        skipReason = "incomplete";
                        return null;
                    }
                    files.Add(file);
                    bytes += info.Length;
                }
            }
            if (bytes > MaxStateBytes)
            {
                skipReason = "too_large";
                return null;
            }

            Directory.CreateDirectory(reuse.StateDirectory);
            temp = Path.Combine(reuse.StateDirectory, $".saving-{Guid.NewGuid():N}");
            var filesRoot = Path.Combine(temp, FilesName);
            var manifest = new Manifest { Version = FormatVersion, Fingerprint = inputs.Fingerprint, Projects = located.Count };
            foreach (var file in files.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var relative = Relative(root, file);
                var blob = Path.Combine(filesRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(blob)!);
                File.Copy(file, blob);
                manifest.Files.Add(relative);
            }
            File.WriteAllText(Path.Combine(temp, ManifestName), JsonSerializer.Serialize(manifest, Json));

            var current = Path.Combine(reuse.StateDirectory, CurrentName);
            if (Directory.Exists(current))
            {
                var retired = Path.Combine(reuse.StateDirectory, $".retired-{Guid.NewGuid():N}");
                Directory.Move(current, retired);
                Directory.Move(temp, current);
                temp = null;
                TryDelete(retired);
            }
            else
            {
                Directory.Move(temp, current);
                temp = null;
            }
            return located.Count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            log?.Invoke($"package restore state could not be saved: {ex.GetType().Name}.");
            skipReason = "save_failed";
            return null;
        }
        finally
        {
            if (temp is not null)
                TryDelete(temp);
        }
    }

    // Every assets file under the checkout, wherever BaseIntermediateOutputPath/MSBuildProjectExtensionsPath put it.
    private static IEnumerable<string> FindAssets(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                if (entry.Name == GitName || entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;
                if (entry is DirectoryInfo)
                    pending.Push(entry.FullName);
                else if (entry.Name.Equals(AssetsName, StringComparison.OrdinalIgnoreCase))
                    yield return entry.FullName;
            }
        }
    }

    // The project an assets file belongs to and the directory NuGet wrote it to (project.restore.*).
    private static (string ProjectPath, string OutputPath)? ReadIdentity(string assets)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(assets));
            if (!document.RootElement.TryGetProperty("project", out var project) || !project.TryGetProperty("restore", out var restore))
                return null;
            var projectPath = restore.TryGetProperty("projectPath", out var p) ? p.GetString() : null;
            var outputPath = restore.TryGetProperty("outputPath", out var o) ? o.GetString() : null;
            return string.IsNullOrEmpty(projectPath) || string.IsNullOrEmpty(outputPath)
                ? null
                : (Path.GetFullPath(projectPath), TrimRoot(Path.GetFullPath(outputPath)));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // A manifest path is data read from disk: it must stay inside the checkout.
    private static string? ConfinedTarget(string root, string relative)
    {
        if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.Contains('\0'))
            return null;
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(root + Path.DirectorySeparatorChar, PathComparison) ? full : null;
    }

    // True when any existing directory between the checkout root and the file is a link, which could redirect a write.
    private static bool HasLinkBetween(string root, string target)
    {
        var dir = Path.GetDirectoryName(target);
        while (dir is not null && dir.Length > root.Length)
        {
            if (Directory.Exists(dir) && new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint))
                return true;
            dir = Path.GetDirectoryName(dir);
        }
        return File.Exists(target) && new FileInfo(target).Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leftovers are swept with the repository's state when it is reclaimed.
        }
    }
}
