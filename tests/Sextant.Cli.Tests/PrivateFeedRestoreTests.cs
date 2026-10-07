using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Sextant.Service.Restore;

namespace Sextant.Cli.Tests;

/// <summary>
/// Issue #231 acceptance: a real <c>dotnet restore</c>, through <see cref="PackageRestoreRunner"/> and the real
/// <c>Sextant.Cli.dll -Plugin</c> credential provider, against a local https NuGet feed that requires Basic auth. The
/// repository's <c>nuget.config</c> declares its own credential from an unset variable, like a private feed
/// authenticated by an environment variable the worker does not have.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PrivateFeedRestoreTests
{
    private const string PackageId = "Sextant.Fixture.Private";
    private const string Username = "fixture";
    private const string Password = "fixture-secret-231";

    private static readonly string[] IsolatedVariables =
        ["SSL_CERT_FILE", "SSL_CERT_DIR", "NUGET_PACKAGES", "NUGET_HTTP_CACHE_PATH", "NUGET_PLUGINS_CACHE_PATH"];

    private string _root = null!;
    private WebApplication _feed = null!;
    private int _port;
    private readonly ConcurrentBag<(string Host, string? Authorization)> _requests = [];
    private readonly Dictionary<string, string?> _savedEnvironment = new();

    [TestInitialize]
    public async Task StartFeed()
    {
        if (OperatingSystem.IsWindows())
            Assert.Inconclusive("Trusts the fixture certificate through SSL_CERT_FILE, which only OpenSSL honours.");

        _root = Path.Combine(Path.GetTempPath(), "sextant-private-feed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var certificate = CreateCertificate();
        var pem = Path.Combine(_root, "fixture-ca.pem");
        File.WriteAllText(pem, certificate.ExportCertificatePem());

        // The restore child inherits this process's environment: trust only the fixture certificate, and keep
        // NuGet's package, HTTP and plugin caches inside the test directory so no earlier run can answer for it.
        foreach (var name in IsolatedVariables)
            _savedEnvironment[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable("SSL_CERT_FILE", pem);
        Environment.SetEnvironmentVariable("SSL_CERT_DIR", Path.Combine(_root, "no-cert-dir"));

        var nupkg = CreatePackage();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(IPAddress.Loopback, 0, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
                listen.UseHttps(certificate);
            }));
        _feed = builder.Build();
        _feed.Use(async (context, next) =>
        {
            var authorization = context.Request.Headers.Authorization.ToString();
            _requests.Add((context.Request.Host.Host, authorization.Length == 0 ? null : authorization));
            var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}"));
            if (authorization != expected)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Basic realm=\"fixture\"";
                return;
            }
            await next(context);
        });
        var lower = PackageId.ToLowerInvariant();
        _feed.MapGet("/v3/index.json", (HttpContext context) => Results.Text(
            $$"""{"version":"3.0.0","resources":[{"@id":"https://{{context.Request.Host}}/v3/flat/","@type":"PackageBaseAddress/3.0.0"}]}""",
            "application/json"));
        _feed.MapGet($"/v3/flat/{lower}/index.json", () => Results.Text("""{"versions":["1.0.0"]}""", "application/json"));
        _feed.MapGet($"/v3/flat/{lower}/1.0.0/{lower}.1.0.0.nupkg", () => Results.Bytes(nupkg, "application/octet-stream"));
        await _feed.StartAsync();
        _port = new Uri(_feed.Urls.Single()).Port;
    }

    [TestCleanup]
    public async Task StopFeed()
    {
        if (_feed is not null)
            await _feed.DisposeAsync();
        foreach (var (name, value) in _savedEnvironment)
            Environment.SetEnvironmentVariable(name, value);
        if (_root is not null)
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [TestMethod]
    public async Task WithTheHostCredential_ThePrivatePackageRestoresFully()
    {
        var (checkout, solution) = CreateConsumer("localhost", "with");
        var runner = Runner(new PackageSourceCredential("localhost", Username, Password) { Port = _port });

        var outcome = await runner.RunAsync(checkout, [solution], limit: null, CancellationToken.None, Scratch("with"));

        Assert.AreEqual(0, outcome.Projects.Count, string.Join("\n", outcome.Notes()));
        Assert.AreEqual(1, outcome.SolutionsSucceeded);
        StringAssert.Contains(File.ReadAllText(Path.Combine(checkout, "Consumer", "obj", "project.assets.json")), $"{PackageId}/1.0.0");
        Assert.IsTrue(_requests.Any(r => r.Authorization is not null && r.Authorization.Contains(
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}")), StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task WithoutACredential_TheSourceIsUnreachable_AndThePackageMissing()
    {
        var (checkout, solution) = CreateConsumer("localhost", "without");

        var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromMinutes(4))
            .RunAsync(checkout, [solution], limit: null, CancellationToken.None, Scratch("without"));

        var project = outcome.Projects.Single();
        CollectionAssert.Contains(project.MissingPackages.ToList(), PackageId);
        Assert.IsTrue(project.SourceUnreachable || outcome.SourceUnreachableGeneral, string.Join(",", project.Codes));
    }

    [TestMethod]
    public async Task ARepositoryThatPointsTheSourceKeyAtAnotherHost_NeverReceivesTheCredential()
    {
        // Same server, another host name (the certificate covers both): the credential is for localhost only.
        var (checkout, solution) = CreateConsumer("127.0.0.1", "redirect");
        var runner = Runner(new PackageSourceCredential("localhost", Username, Password) { Port = _port });

        var outcome = await runner.RunAsync(checkout, [solution], limit: null, CancellationToken.None, Scratch("redirect"));

        CollectionAssert.Contains(outcome.Projects.Single().MissingPackages.ToList(), PackageId);
        Assert.IsTrue(_requests.Any(r => r.Host == "127.0.0.1"), "the restore did ask the other host");
        Assert.IsFalse(_requests.Any(r => r.Authorization?.Contains(
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}")), StringComparison.Ordinal) == true),
            "and never sent it the credential");
    }

    private PackageRestoreRunner Runner(PackageSourceCredential credential)
        => new(timeout: TimeSpan.FromMinutes(4), sourceCredentials: [credential],
            credentialPluginPath: Path.Combine(AppContext.BaseDirectory, "Sextant.Cli.dll"));

    private string Scratch(string name)
    {
        var scratch = Path.Combine(_root, "scratch-" + name);
        Directory.CreateDirectory(scratch);
        Environment.SetEnvironmentVariable("NUGET_PACKAGES", Path.Combine(scratch, "packages"));
        Environment.SetEnvironmentVariable("NUGET_HTTP_CACHE_PATH", Path.Combine(scratch, "http-cache"));
        Environment.SetEnvironmentVariable("NUGET_PLUGINS_CACHE_PATH", Path.Combine(scratch, "plugins-cache"));
        return scratch;
    }

    private (string Checkout, string Solution) CreateConsumer(string sourceHost, string name)
    {
        var checkout = Path.Combine(_root, "checkout-" + name);
        Directory.CreateDirectory(Path.Combine(checkout, "Consumer"));
        File.WriteAllText(Path.Combine(checkout, "nuget.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="private" value="https://{sourceHost}:{_port}/v3/index.json" />
              </packageSources>
              <packageSourceCredentials>
                <private>
                  <add key="Username" value="{Username}" />
                  <add key="ClearTextPassword" value="%SEXTANT_FIXTURE_UNSET_FEED_TOKEN%" />
                </private>
              </packageSourceCredentials>
            </configuration>
            """);
        File.WriteAllText(Path.Combine(checkout, "Consumer", "Consumer.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net{Environment.Version.Major}.0</TargetFramework></PropertyGroup>
              <ItemGroup><PackageReference Include="{PackageId}" Version="1.0.0" /></ItemGroup>
            </Project>
            """);
        var solution = Path.Combine(checkout, "All.slnx");
        File.WriteAllText(solution, """<Solution><Project Path="Consumer/Consumer.csproj" /></Solution>""");
        return (checkout, solution);
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Kestrel needs a key it can use from the store-less export.
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null);
    }

    private static byte[] CreatePackage()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry($"{PackageId}.nuspec").Open()))
            {
                writer.Write($"""
                    <?xml version="1.0" encoding="utf-8"?>
                    <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                      <metadata>
                        <id>{PackageId}</id>
                        <version>1.0.0</version>
                        <authors>sextant</authors>
                        <description>Issue #231 fixture.</description>
                      </metadata>
                    </package>
                    """);
            }
            using (var writer = new StreamWriter(zip.CreateEntry("lib/netstandard2.0/_._").Open()))
                writer.Write(string.Empty);
        }
        return stream.ToArray();
    }
}
