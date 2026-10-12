using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Sextant.Core;

namespace Sextant.Indexer;

/// <summary>An Apple platform a project can target (<c>net10.0-ios</c>, ...), with its workload manifest and pack names.</summary>
public sealed record ApplePlatform(string Tfm, string ManifestId, string PackPrefix)
{
    public static readonly ApplePlatform IOS = new("ios", "microsoft.net.sdk.ios", "Microsoft.iOS.Ref");
    public static readonly ApplePlatform MacCatalyst = new("maccatalyst", "microsoft.net.sdk.maccatalyst", "Microsoft.MacCatalyst.Ref");
    public static readonly ApplePlatform TvOS = new("tvos", "microsoft.net.sdk.tvos", "Microsoft.tvOS.Ref");

    public static IReadOnlyList<ApplePlatform> All { get; } = [IOS, MacCatalyst, TvOS];
}

/// <summary>A target framework split into its .NET version and Apple platform (<c>net10.0-ios26.0</c>).</summary>
public sealed record PlatformTarget(int DotnetMajor, ApplePlatform Platform, Version? PlatformVersion);

/// <summary>A reference pack named by a workload manifest.</summary>
public sealed record PlatformPackId(string Id, string Version);

/// <summary>How a project was bound against a platform reference pack (issue #295).</summary>
public sealed record PlatformPackBinding(
    string ProjectPath, string? TargetFramework, string PackId, string PackVersion, string Source, int ReferenceCount);

/// <summary>Where the loader may look for and obtain platform reference packs.</summary>
public sealed record PlatformPackOptions
{
    /// <summary>The .NET installation (<c>packs</c>, <c>sdk-manifests</c>); null derives it from the running runtime.</summary>
    public string? DotnetRoot { get; init; }

    /// <summary>NuGet package folders searched for the pack, the first one also receiving a download.</summary>
    public IReadOnlyList<string> PackageFolders { get; init; } = [];

    /// <summary>Job scratch directory, for the throwaway download project. Without it no download is attempted.</summary>
    public string? ScratchDirectory { get; init; }

    /// <summary>The bound of one pack download.</summary>
    public TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// The deadline shared by every download of one load: a pack download is bounded by the time left to it, and once it
    /// has passed the remaining packs are skipped. Null leaves only <see cref="DownloadTimeout"/>.
    /// </summary>
    public IndexDeadline? Deadline { get; init; }

    /// <summary>
    /// The options the service worker passes for a job: the job's package folder (if any), scratch directory, a download
    /// bounded by <paramref name="restoreLimit"/> (at most three minutes) and the load deadline. The benchmark's service path uses it too.
    /// </summary>
    public static PlatformPackOptions ForJob(
        string? packageFolder, string? scratchDirectory, TimeSpan? restoreLimit, IndexDeadline? loadDeadline) =>
        new()
        {
            PackageFolders = packageFolder is null ? [] : [packageFolder],
            ScratchDirectory = scratchDirectory,
            DownloadTimeout = restoreLimit is { } limit && limit < TimeSpan.FromMinutes(3) ? limit : TimeSpan.FromMinutes(3),
            Deadline = loadDeadline
        };

    /// <summary>Test seam: obtains a pack into <c>packageFolder</c>; null uses <see cref="NuGetOrgPackDownloader"/>.</summary>
    public IPlatformPackDownloader? Downloader { get; init; }
}

public interface IPlatformPackDownloader
{
    /// <summary>Downloads the pack into <paramref name="packageFolder"/> (NuGet layout); returns false on failure.</summary>
    Task<bool> DownloadAsync(PlatformPackId pack, string packageFolder, string scratchDirectory, TimeSpan timeout,
        CancellationToken cancellationToken);
}

/// <summary>
/// Binds iOS, Mac Catalyst and tvOS projects against their platform reference pack when the workload is missing
/// (issue #295). The .NET SDK refuses those workloads on Linux, so such a project fails evaluation and is indexed from its
/// surviving documents with every platform type unbound. The reference pack (<c>Microsoft.iOS.Ref.net10.0_27.0</c>) is an
/// ordinary NuGet package that holds the C# API surface, so its assemblies are added to the project's compilation as
/// metadata references. Only a degraded project whose target platform is one of the three and whose evaluation failed
/// because of the missing workload is touched.
/// </summary>
public static partial class PlatformReferencePacks
{
    public const string SourcePacksDirectory = "packs";
    public const string SourcePackageFolder = "package_folder";
    public const string SourceDownload = "nuget.org";

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    [GeneratedRegex(@"^net(?<major>\d+)(?:\.\d+)?-(?<platform>ios|maccatalyst|tvos)(?<version>\d+(?:\.\d+){0,2})?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TfmPattern();

    [GeneratedRegex(@"^(?<prefix>Microsoft\.(?:iOS|MacCatalyst|tvOS)\.Ref)\.net(?<major>\d+)\.0_(?<version>\d+(?:\.\d+)*)$",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PackNamePattern();

    // What the SDK and MSBuild report for a project whose platform workload is not installed (or not installable).
    [GeneratedRegex(@"NETSDK1147|NETSDK1139|NETSDK1178|isn't supported on this platform|Platform version is not present",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex WorkloadFailurePattern();

    /// <summary>Splits <paramref name="tfm"/> into .NET version and Apple platform; null for any other framework.</summary>
    public static PlatformTarget? ParseTarget(string? tfm)
    {
        if (string.IsNullOrWhiteSpace(tfm))
            return null;
        var match = TfmPattern().Match(tfm.Trim());
        if (!match.Success)
            return null;
        var platform = ApplePlatform.All.First(p => p.Tfm.Equals(match.Groups["platform"].Value, StringComparison.OrdinalIgnoreCase));
        Version? version = null;
        if (match.Groups["version"].Success)
        {
            var text = match.Groups["version"].Value;
            if (!text.Contains('.'))
                text += ".0";
            Version.TryParse(text, out version);
        }
        return new PlatformTarget(int.Parse(match.Groups["major"].Value, System.Globalization.CultureInfo.InvariantCulture), platform, version);
    }

    /// <summary>True when the pack id is one of the Apple reference packs this feature may download.</summary>
    public static bool IsAllowedPackId(string? id) => id is not null && PackNamePattern().IsMatch(id);

    /// <summary>True when an evaluation failure message is the missing-workload failure.</summary>
    public static bool IsWorkloadFailure(string? reason) => reason is not null && WorkloadFailurePattern().IsMatch(reason);

    /// <summary>The .NET installation directory of the running runtime (<c>.../shared/Microsoft.NETCore.App/&lt;v&gt;/</c> three levels up).</summary>
    public static string? DefaultDotnetRoot()
    {
        var env = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env))
            return env;
        var runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(runtime)));
    }

    /// <summary>
    /// The reference pack the SDK's workload manifest (<c>sdk-manifests/&lt;band&gt;/microsoft.net.sdk.&lt;platform&gt;/&lt;version&gt;</c>)
    /// names for <paramref name="target"/>: the pack of the TFM's platform version when it lists one, else the newest
    /// pack for the .NET version. Reads the highest SDK band that has the manifest; null when there is none.
    /// </summary>
    public static PlatformPackId? ResolvePack(string dotnetRoot, PlatformTarget target)
    {
        var manifestsRoot = Path.Combine(dotnetRoot, "sdk-manifests");
        if (!Directory.Exists(manifestsRoot))
            return null;
        foreach (var manifest in EnumerateManifests(manifestsRoot, target.Platform))
        {
            if (PickPack(manifest, target) is { } pack)
                return pack;
        }
        return null;
    }

    private static IEnumerable<string> EnumerateManifests(string manifestsRoot, ApplePlatform platform)
    {
        foreach (var band in Directory.EnumerateDirectories(manifestsRoot).OrderByDescending(d => ParseVersion(Path.GetFileName(d)))
                     .ThenByDescending(d => d, StringComparer.Ordinal))
        {
            var manifestDir = Path.Combine(band, platform.ManifestId);
            if (!Directory.Exists(manifestDir))
                continue;
            var direct = Path.Combine(manifestDir, "WorkloadManifest.json");
            if (File.Exists(direct))
                yield return direct;
            foreach (var versioned in Directory.EnumerateDirectories(manifestDir)
                         .OrderByDescending(d => ParseVersion(Path.GetFileName(d))))
            {
                var file = Path.Combine(versioned, "WorkloadManifest.json");
                if (File.Exists(file))
                    yield return file;
            }
        }
    }

    private static Version ParseVersion(string text)
    {
        var numeric = new string(text.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');
        return Version.TryParse(numeric, out var v) ? v : new Version(0, 0);
    }

    internal static PlatformPackId? PickPack(string manifestPath, PlatformTarget target)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            if (!document.RootElement.TryGetProperty("packs", out var packs) || packs.ValueKind != JsonValueKind.Object)
                return null;

            var candidates = new List<(PlatformPackId Pack, Version PlatformVersion)>();
            foreach (var pack in packs.EnumerateObject())
            {
                var match = PackNamePattern().Match(pack.Name);
                if (!match.Success
                    || !match.Groups["prefix"].Value.Equals(target.Platform.PackPrefix, StringComparison.Ordinal)
                    || int.Parse(match.Groups["major"].Value, System.Globalization.CultureInfo.InvariantCulture) != target.DotnetMajor
                    || !pack.Value.TryGetProperty("version", out var version)
                    || version.ValueKind != JsonValueKind.String
                    || version.GetString() is not { Length: > 0 } packVersion)
                    continue;
                candidates.Add((new PlatformPackId(pack.Name, packVersion), ParseVersion(match.Groups["version"].Value)));
            }

            if (candidates.Count == 0)
                return null;
            if (target.PlatformVersion is { } wanted)
            {
                var exact = candidates.Where(c => c.PlatformVersion.Major == wanted.Major && c.PlatformVersion.Minor == Math.Max(wanted.Minor, 0))
                    .Select(c => c.Pack).FirstOrDefault();
                if (exact is not null)
                    return exact;
            }
            return candidates.OrderByDescending(c => c.PlatformVersion).First().Pack;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Finds the pack's reference assemblies: the SDK's <c>packs</c> directory, then each package folder, then a download
    /// into the first package folder (or a folder in scratch). Null when the pack cannot be obtained.
    /// </summary>
    public static async Task<(IReadOnlyList<string> Assemblies, string Source)?> LocateAsync(
        PlatformPackId pack, int dotnetMajor, PlatformPackOptions options, string dotnetRoot,
        Action<string>? onDiagnostic, CancellationToken cancellationToken)
    {
        if (!IsAllowedPackId(pack.Id) || !SafeVersion().IsMatch(pack.Version))
            return null;

        if (ReferenceAssemblies(Path.Combine(dotnetRoot, "packs", pack.Id, pack.Version), dotnetMajor) is { Count: > 0 } installed)
            return (installed, SourcePacksDirectory);

        var folders = options.PackageFolders.Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
        if (Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } env)
            folders.Add(env);
        foreach (var folder in folders)
        {
            if (IsCompletePackage(PackageDirectory(folder, pack)) && ReferenceAssemblies(PackageDirectory(folder, pack), dotnetMajor) is { Count: > 0 } cached)
                return (cached, SourcePackageFolder);
        }

        if (options.ScratchDirectory is not { Length: > 0 } scratch)
            return null;
        var timeout = options.DownloadTimeout;
        if (options.Deadline is { } deadline)
        {
            var remaining = deadline.At - deadline.Clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                onDiagnostic?.Invoke(
                    $"Platform reference pack {pack.Id} {pack.Version} was not downloaded: the load time budget is spent.");
                return null;
            }
            if (remaining < timeout)
                timeout = remaining;
        }
        var target = options.PackageFolders.FirstOrDefault(f => !string.IsNullOrWhiteSpace(f))
            ?? Path.Combine(scratch, "platform-packs");
        try
        {
            Directory.CreateDirectory(target);
            var downloader = options.Downloader ?? new NuGetOrgPackDownloader();
            if (!await downloader.DownloadAsync(pack, target, scratch, timeout, cancellationToken).ConfigureAwait(false))
            {
                onDiagnostic?.Invoke($"Platform reference pack {pack.Id} {pack.Version} could not be downloaded from nuget.org.");
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            onDiagnostic?.Invoke($"Platform reference pack {pack.Id} {pack.Version} could not be downloaded: {ex.Message}");
            return null;
        }

        return IsCompletePackage(PackageDirectory(target, pack))
            && ReferenceAssemblies(PackageDirectory(target, pack), dotnetMajor) is { Count: > 0 } downloaded
            ? (downloaded, SourceDownload)
            : null;
    }

    // NuGet writes .nupkg.metadata after a package is fully extracted, so a download that was cancelled or timed out
    // mid-extraction is never taken for a present pack.
    private static bool IsCompletePackage(string packageDirectory) =>
        File.Exists(Path.Combine(packageDirectory, ".nupkg.metadata"));

    private static string PackageDirectory(string folder, PlatformPackId pack) =>
        Path.Combine(folder, pack.Id.ToLowerInvariant(), pack.Version.ToLowerInvariant());

    private static IReadOnlyList<string>? ReferenceAssemblies(string packDirectory, int dotnetMajor)
    {
        var refDir = Path.Combine(packDirectory, "ref");
        if (!Directory.Exists(refDir))
            return null;
        var tfmDir = Path.Combine(refDir, $"net{dotnetMajor}.0");
        var directory = Directory.Exists(tfmDir)
            ? tfmDir
            : Directory.EnumerateDirectories(refDir).OrderByDescending(d => d, StringComparer.Ordinal).FirstOrDefault();
        return directory is null
            ? null
            : Directory.EnumerateFiles(directory, "*.dll").OrderBy(f => f, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Adds the platform reference pack to every degraded project of <paramref name="degraded"/> that targets an Apple
    /// platform and failed because its workload is missing. A pack that cannot be obtained leaves the project as it was.
    /// </summary>
    public static async Task<(Solution Solution, IReadOnlyList<PlatformPackBinding> Bindings)> ApplyAsync(
        Solution solution, IEnumerable<DegradedProject> degraded, PlatformPackOptions options,
        Action<string>? onDiagnostic, CancellationToken cancellationToken)
    {
        var bindings = new List<PlatformPackBinding>();
        var dotnetRoot = options.DotnetRoot ?? DefaultDotnetRoot();
        if (dotnetRoot is null)
            return (solution, bindings);

        var located = new Dictionary<string, (IReadOnlyList<string> Assemblies, string Source)?>(StringComparer.Ordinal);
        foreach (var project in degraded)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = ParseTarget(project.TargetFramework);
            var roslynProject = project.ProjectId is null ? null : solution.GetProject(project.ProjectId);
            var workloadFailure = IsWorkloadFailure(project.Reason);
            if (target is null || !workloadFailure || roslynProject is null)
            {
                onDiagnostic?.Invoke(
                    $"Platform reference pack: '{Path.GetFileName(project.ProjectPath)}' ({project.TargetFramework ?? "no TFM"}) not bound: " +
                    $"apple_target={(target is null ? "no" : "yes")}, workload_failure={(workloadFailure ? "yes" : "no")}, " +
                    $"workspace_project={(roslynProject is null ? "no" : "yes")}; reason: {Excerpt(project.Reason)}");
                continue;
            }
            if (ResolvePack(dotnetRoot, target) is not { } pack)
            {
                onDiagnostic?.Invoke(
                    $"No {target.Platform.PackPrefix} pack for net{target.DotnetMajor}.0 is named by the SDK's workload manifests; '{Path.GetFileName(project.ProjectPath)}' stays unbound.");
                continue;
            }

            var key = $"{pack.Id}/{pack.Version}";
            if (!located.TryGetValue(key, out var found))
            {
                found = await LocateAsync(pack, target.DotnetMajor, options, dotnetRoot, onDiagnostic, cancellationToken).ConfigureAwait(false);
                located[key] = found;
            }
            if (found is not { } resolved)
            {
                onDiagnostic?.Invoke(
                    $"Platform reference pack: '{Path.GetFileName(project.ProjectPath)}' ({project.TargetFramework}) not bound: {pack.Id} {pack.Version} could not be located or downloaded.");
                continue;
            }

            var existing = ExistingReferenceNames(roslynProject);
            var additions = resolved.Assemblies.Where(a => existing.Add(Path.GetFileName(a))).ToList();
            if (!existing.Contains("System.Runtime.dll") && BaseFrameworkAssemblies(dotnetRoot, target.DotnetMajor) is { } baseline)
                additions.AddRange(baseline.Where(a => existing.Add(Path.GetFileName(a))));
            if (additions.Count == 0)
            {
                onDiagnostic?.Invoke(
                    $"Platform reference pack: '{Path.GetFileName(project.ProjectPath)}' ({project.TargetFramework}) not bound: {pack.Id} {pack.Version} adds no reference the project lacks.");
                continue;
            }

            solution = solution.AddMetadataReferences(
                project.ProjectId, additions.Select(a => (MetadataReference)MetadataReference.CreateFromFile(a)));
            bindings.Add(new PlatformPackBinding(
                project.ProjectPath, project.TargetFramework, pack.Id, pack.Version, resolved.Source, additions.Count));
            onDiagnostic?.Invoke(
                $"Compiled '{Path.GetFileName(project.ProjectPath)}' ({project.TargetFramework}) against the {pack.Id} {pack.Version} " +
                $"reference pack ({resolved.Source}) without the {target.Platform.Tfm} workload; package references may still be missing.");
        }
        return (solution, bindings);
    }

    private static string Excerpt(string? reason)
    {
        var text = string.Join(' ', (reason ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return text.Length <= 240 ? text : text[..240] + "...";
    }

    private static HashSet<string> ExistingReferenceNames(Project project)
    {
        var names = new HashSet<string>(PathComparer);
        foreach (var reference in project.MetadataReferences.OfType<PortableExecutableReference>())
        {
            if (reference.FilePath is { Length: > 0 } path)
                names.Add(Path.GetFileName(path));
        }
        return names;
    }

    private static IReadOnlyList<string>? BaseFrameworkAssemblies(string dotnetRoot, int dotnetMajor)
    {
        var root = Path.Combine(dotnetRoot, "packs", "Microsoft.NETCore.App.Ref");
        if (!Directory.Exists(root))
            return null;
        var version = Directory.EnumerateDirectories(root)
            .Where(d => ParseVersion(Path.GetFileName(d)).Major == dotnetMajor)
            .OrderByDescending(d => ParseVersion(Path.GetFileName(d))).FirstOrDefault();
        return version is null ? null : ReferenceAssemblies(version, dotnetMajor);
    }

    internal const string EmptyProject = "<Project />\n";

    internal const string CentralPackageManagementOff =
        "<Project>\n  <PropertyGroup>\n    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>\n  </PropertyGroup>\n</Project>\n";

    internal static string GenerateNuGetConfig() => """
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <packageSources>
            <clear />
            <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
          </packageSources>
        </configuration>
        """;

    internal static string GenerateDownloadProject(PlatformPackId pack, string packageFolder)
    {
        if (!IsAllowedPackId(pack.Id) || !SafeVersion().IsMatch(pack.Version))
            throw new InvalidOperationException($"'{pack.Id}' {pack.Version} is not an allowed platform reference pack.");
        var escapedFolder = System.Security.SecurityElement.Escape(packageFolder);
        return $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net{Environment.Version.Major}.0</TargetFramework>
                <RestorePackagesPath>{escapedFolder}</RestorePackagesPath>
              </PropertyGroup>
              <ItemGroup>
                <PackageDownload Include="{pack.Id}" Version="[{pack.Version}]" />
              </ItemGroup>
            </Project>
            """;
    }

    [GeneratedRegex(@"^[0-9A-Za-z.\-+]{1,64}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SafeVersion();

    /// <summary>The download environment: the parent's, minus every <c>SEXTANT_*</c> and NuGet credential-provider variable.</summary>
    internal static bool IsScrubbedVariable(string name) =>
        name.StartsWith("SEXTANT_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("NUGET_", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("VSS_NUGET", StringComparison.OrdinalIgnoreCase)
        || name.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)
        || name.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Restores a pack with a generated throwaway project whose generated <c>nuget.config</c> clears every source and names
    /// nuget.org only, so the repository's feeds and credentials are never used.
    /// </summary>
    public sealed class NuGetOrgPackDownloader : IPlatformPackDownloader
    {
        public async Task<bool> DownloadAsync(PlatformPackId pack, string packageFolder, string scratchDirectory,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            var directory = Path.Combine(scratchDirectory, $"platform-pack-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                var configPath = Path.Combine(directory, "nuget.config");
                var projectPath = Path.Combine(directory, "download.csproj");
                await File.WriteAllTextAsync(configPath, GenerateNuGetConfig(), cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(projectPath, GenerateDownloadProject(pack, packageFolder), cancellationToken).ConfigureAwait(false);
                // Empty ancestors of the throwaway project, so a Directory.* file above scratch cannot change the restore.
                await File.WriteAllTextAsync(Path.Combine(directory, "Directory.Build.props"), EmptyProject, cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(directory, "Directory.Build.targets"), EmptyProject, cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(directory, "Directory.Packages.props"), CentralPackageManagementOff, cancellationToken).ConfigureAwait(false);

                var start = new ProcessStartInfo(DotnetExecutable(), "")
                {
                    WorkingDirectory = directory,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                foreach (var argument in new[] { "restore", projectPath, "--configfile", configPath, "--disable-build-servers", "-nodeReuse:false", "--nologo" })
                    start.ArgumentList.Add(argument);
                foreach (var name in start.Environment.Keys.Where(IsScrubbedVariable).ToList())
                    start.Environment.Remove(name);
                start.Environment["NUGET_PACKAGES"] = packageFolder;
                start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
                start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
                start.Environment["DOTNET_NOLOGO"] = "1";
                start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

                using var process = Process.Start(start) ?? throw new InvalidOperationException("dotnet did not start.");
                process.StandardInput.Close();
                // Drain both pipes concurrently and never wait on pipe EOF: a leaked MSBuild node could hold them open.
                var drain = Task.WhenAll(process.StandardOutput.ReadToEndAsync(CancellationToken.None),
                    process.StandardError.ReadToEndAsync(CancellationToken.None));
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                limit.CancelAfter(timeout);
                try
                {
                    await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    cancellationToken.ThrowIfCancellationRequested();
                    return false;
                }
                _ = drain;
                return process.ExitCode == 0;
            }
            finally
            {
                try { Directory.Delete(directory, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        private static string DotnetExecutable()
        {
            var root = DefaultDotnetRoot();
            var name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
            return root is not null && File.Exists(Path.Combine(root, name)) ? Path.Combine(root, name) : "dotnet";
        }
    }
}
