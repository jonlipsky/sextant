using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

if (args.Length != 1)
    throw new ArgumentException("Expected the .NET installation directory.");

var dotnetRoot = Path.GetFullPath(args[0]);
var manifestsRoot = Path.Combine(dotnetRoot, "sdk-manifests");
var http = new HttpClient();

foreach (var (manifestId, prefix) in new[]
         {
             ("microsoft.net.sdk.ios", "Microsoft.iOS.Ref"),
             ("microsoft.net.sdk.maccatalyst", "Microsoft.MacCatalyst.Ref"),
             ("microsoft.net.sdk.tvos", "Microsoft.tvOS.Ref")
         })
{
    var (packId, packVersion) = ResolvePack(manifestsRoot, manifestId, prefix);
    var destination = Path.Combine(dotnetRoot, "packs", packId, packVersion);
    if (Directory.Exists(Path.Combine(destination, "ref", "net10.0")))
        continue;

    var packageName = packId.ToLowerInvariant();
    var packageVersion = packVersion.ToLowerInvariant();
    var uri = $"https://api.nuget.org/v3-flatcontainer/{packageName}/{packageVersion}/{packageName}.{packageVersion}.nupkg";
    var staging = destination + $".prefetch-{Guid.NewGuid():N}";
    Directory.CreateDirectory(staging);
    try
    {
        await using var package = await http.GetStreamAsync(uri);
        ZipFile.ExtractToDirectory(package, staging);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        Directory.Move(staging, destination);
    }
    catch
    {
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        throw;
    }

    Console.WriteLine($"Prefetched {packId} {packVersion} from nuget.org.");
}

static (string Id, string Version) ResolvePack(string manifestsRoot, string manifestId, string prefix)
{
    foreach (var band in Directory.EnumerateDirectories(manifestsRoot)
                 .OrderByDescending(path => ParseVersion(Path.GetFileName(path)))
                 .ThenByDescending(path => path, StringComparer.Ordinal))
    {
        var manifestDirectory = Path.Combine(band, manifestId);
        if (!Directory.Exists(manifestDirectory))
            continue;

        var directManifest = Path.Combine(manifestDirectory, "WorkloadManifest.json");
        if (File.Exists(directManifest) && FindPack(directManifest, prefix) is { } direct)
            return direct;

        foreach (var versionDirectory in Directory.EnumerateDirectories(manifestDirectory)
                     .OrderByDescending(path => ParseVersion(Path.GetFileName(path))))
        {
            var manifest = Path.Combine(versionDirectory, "WorkloadManifest.json");
            if (File.Exists(manifest) && FindPack(manifest, prefix) is { } versioned)
                return versioned;
        }
    }

    throw new InvalidOperationException($"No .NET 10 reference pack was found for {prefix}.");
}

static (string Id, string Version)? FindPack(string manifest, string prefix)
{
    using var document = JsonDocument.Parse(File.ReadAllText(manifest), new JsonDocumentOptions
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    });
    if (!document.RootElement.TryGetProperty("packs", out var packs) || packs.ValueKind != JsonValueKind.Object)
        return null;

    var pattern = new Regex(
        $@"^{Regex.Escape(prefix)}\.net(?<major>\d+)\.0_(?<platform>\d+(?:\.\d+)*)$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    var candidates = new List<(string Id, string Version, Version Platform)>();
    foreach (var pack in packs.EnumerateObject())
    {
        var match = pattern.Match(pack.Name);
        if (!match.Success || match.Groups["major"].Value != "10"
            || !pack.Value.TryGetProperty("version", out var version)
            || version.ValueKind != JsonValueKind.String
            || version.GetString() is not { Length: > 0 } packageVersion
            || !Version.TryParse(match.Groups["platform"].Value, out var platformVersion))
            continue;
        candidates.Add((pack.Name, packageVersion, platformVersion));
    }

    var selected = candidates.OrderByDescending(candidate => candidate.Platform).FirstOrDefault();
    return selected.Id is null ? null : (selected.Id, selected.Version);
}

static Version ParseVersion(string text)
{
    var numeric = new string(text.TakeWhile(character => char.IsDigit(character) || character == '.').ToArray()).Trim('.');
    return Version.TryParse(numeric, out var version) ? version : new Version(0, 0);
}
