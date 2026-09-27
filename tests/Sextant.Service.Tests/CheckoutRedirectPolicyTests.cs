using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #125 security review: the checkout token travels as an env-scoped <c>http.extraheader</c>, which git
/// copies onto EVERY request — including requests it rebases onto a redirect target after following the first
/// redirect (git's default <c>http.followRedirects=initial</c>). So an authenticated git environment must
/// disable redirects, or a redirect on the repository host (a path an untrusted <c>.gitmodules</c> can pick)
/// would hand the token to another host. These tests use two raw loopback TCP servers — no network.
/// </summary>
[TestClass]
public class CheckoutRedirectPolicyTests
{
    private readonly List<string> _tempDirs = [];

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var dir in _tempDirs)
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }

    [TestMethod]
    public void AuthenticatedEnvironments_DisableRedirects_AnonymousOnesKeepGitsDefault()
    {
        var provider = NewProvider(token: "tok");
        const string url = "https://github.com/org/repo.git";

        foreach (var env in new[] { provider.TopLevelEnvironment("github.com", url), provider.SubmoduleFetchEnvironment("github.com", url) })
        {
            Assert.AreEqual("false", env.Config.Last(kv => kv.Key == "http.followRedirects").Value,
                "an environment carrying the token header must never follow a redirect");
            Assert.AreEqual("false", env.Config.Last(kv => kv.Key == $"http.{url}.followRedirects").Value,
                "… not even when a more specific url-scoped setting is inherited (the exact url is the most specific)");
        }

        // No header ⇒ nothing to leak ⇒ git's default redirect policy is kept (e.g. an anonymous allowlisted host).
        Assert.IsFalse(provider.TopLevelEnvironment(null, url).Config.Any(kv => kv.Key.EndsWith("followRedirects", StringComparison.Ordinal)));
        Assert.IsFalse(provider.SubmoduleFetchEnvironment(null, url).Config.Any(kv => kv.Key.EndsWith("followRedirects", StringComparison.Ordinal)));
        Assert.IsFalse(NewProvider(token: null).TopLevelEnvironment("github.com", url).Config
            .Any(kv => kv.Key.EndsWith("followRedirects", StringComparison.Ordinal)));
    }

    /// <summary>How the git config for one redirect scenario is assembled.</summary>
    public enum RedirectScenario
    {
        /// <summary>The provider's authenticated environment, nothing inherited.</summary>
        Provider,
        /// <summary>An operator's inherited path-scoped <c>followRedirects=true</c>, then the provider's environment.</summary>
        ProviderUnderInheritedPrefixOverride,
        /// <summary>An operator's inherited exact-url <c>followRedirects=true</c>, then the provider's environment.</summary>
        ProviderUnderInheritedExactOverride,
        /// <summary>Control: the header with git's default redirect policy.</summary>
        ControlNoRedirectPolicy,
        /// <summary>Control: only the GLOBAL <c>followRedirects=false</c>, under a path-scoped override.</summary>
        ControlGlobalKeyOnlyUnderPrefixOverride,
    }

    [TestMethod]
    [DataRow(RedirectScenario.Provider, false)]
    [DataRow(RedirectScenario.ProviderUnderInheritedPrefixOverride, false)]
    [DataRow(RedirectScenario.ProviderUnderInheritedExactOverride, false)]
    [DataRow(RedirectScenario.ControlNoRedirectPolicy, true)]
    [DataRow(RedirectScenario.ControlGlobalKeyOnlyUnderPrefixOverride, true)]
    public async Task Git_WithTheAuthenticatedEnvironment_NeverContactsARedirectTarget(
        RedirectScenario scenario, bool expectTargetContacted)
    {
        using var target = new RecordingServer(_ => "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var origin = new RecordingServer(request =>
        {
            var path = request.Split(' ', 3)[1];
            return $"HTTP/1.1 301 Moved Permanently\r\nLocation: http://127.0.0.1:{target.Port}{path}\r\n"
                   + "Content-Length: 0\r\nConnection: close\r\n\r\n";
        });

        var authority = $"127.0.0.1:{origin.Port}";
        // The provider's REAL authenticated config, with its url-scoped keys re-scoped from https:// to the
        // plain-http loopback origin (a test server cannot present a trusted TLS certificate). The controls
        // drop redirect entries, proving the test would detect a redirect being followed.
        var providerConfig = NewProvider(token: "SENTINEL-TOKEN-redirect")
            .TopLevelEnvironment(authority, $"https://{authority}/org/repo.git").Config
            .Where(kv => scenario switch
            {
                RedirectScenario.ControlNoRedirectPolicy => !kv.Key.EndsWith(".followRedirects", StringComparison.Ordinal),
                RedirectScenario.ControlGlobalKeyOnlyUnderPrefixOverride =>
                    !kv.Key.EndsWith(".followRedirects", StringComparison.Ordinal) || kv.Key == "http.followRedirects",
                _ => true
            })
            .Select(kv => new KeyValuePair<string, string>(
                kv.Key.Replace($"http.https://{authority}/", $"http.http://{authority}/", StringComparison.Ordinal), kv.Value));
        IEnumerable<KeyValuePair<string, string>> inherited = scenario switch
        {
            RedirectScenario.ProviderUnderInheritedPrefixOverride or RedirectScenario.ControlGlobalKeyOnlyUnderPrefixOverride =>
                [new($"http.http://{authority}/org/.followRedirects", "true")],
            RedirectScenario.ProviderUnderInheritedExactOverride =>
                [new($"http.http://{authority}/org/repo.git.followRedirects", "true")],
            _ => []
        };
        var config = inherited.Concat(providerConfig).ToList();

        var (exitCode, stderr) = await RunGitAsync(config, "ls-remote", "--end-of-options", $"http://{authority}/org/repo.git");
        Console.WriteLine($"git stderr: {stderr}");

        Assert.AreNotEqual(0, exitCode, "neither server serves a repository");
        Assert.IsTrue(origin.Requests.Any(r => r.Contains("authorization: basic", StringComparison.OrdinalIgnoreCase)),
            "the header is scoped to (and sent to) the repository host itself: " + string.Join(" | ", origin.Requests));
        if (!expectTargetContacted)
        {
            Assert.AreEqual(0, target.Requests.Count,
                $"the redirect target must never be contacted by an authenticated fetch. git: {stderr}");
            Assert.IsTrue(CloningCheckoutProvider.MatchesDeterministicGitError(stderr),
                $"an unfollowed redirect is permanent (never burns the transient retry budget). git: {stderr}");
        }
        else
            Assert.IsTrue(target.Requests.Count > 0,
                $"control: without the provider's redirect policy the redirect IS followed (so the assertion above is meaningful). git: {stderr}");
    }

    private CloningCheckoutProvider NewProvider(string? token)
    {
        var dataRoot = ServiceTestFixtures.NewDataRoot();
        _tempDirs.Add(dataRoot);
        var paths = new ServicePaths(ServiceVolumes.Rooted(dataRoot));
        return new CloningCheckoutProvider(new PersistentVolumeCheckoutProvider(paths), paths, token: token);
    }

    private async Task<(int ExitCode, string Stderr)> RunGitAsync(
        IReadOnlyList<KeyValuePair<string, string>> config, params string[] args)
    {
        var cwd = Path.Combine(Path.GetTempPath(), $"sextant_redirect_{Guid.NewGuid():N}");
        Directory.CreateDirectory(cwd);
        _tempDirs.Add(cwd);
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("GIT_CONFIG", StringComparison.OrdinalIgnoreCase)).ToList())
            psi.Environment.Remove(key);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_CONFIG_COUNT"] = config.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        for (var i = 0; i < config.Count; i++)
        {
            psi.Environment[$"GIT_CONFIG_KEY_{i}"] = config[i].Key;
            psi.Environment[$"GIT_CONFIG_VALUE_{i}"] = config[i].Value;
        }

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        await stdout;
        return (process.ExitCode, await stderr);
    }

    /// <summary>A minimal loopback HTTP/1.1 server that records each request's head and answers from a callback.</summary>
    private sealed class RecordingServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private readonly List<string> _requests = [];

        public RecordingServer(Func<string, string> respond)
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                    catch (Exception) { return; }
                    using (client)
                    {
                        try
                        {
                            var stream = client.GetStream();
                            var head = await ReadHeadAsync(stream, _stop.Token);
                            lock (_requests)
                                _requests.Add(head);
                            var bytes = Encoding.ASCII.GetBytes(respond(head));
                            await stream.WriteAsync(bytes, _stop.Token);
                        }
                        catch (Exception) { /* a client that hung up early */ }
                    }
                }
            });
        }

        public int Port { get; }

        public IReadOnlyList<string> Requests
        {
            get { lock (_requests) return [.. _requests]; }
        }

        private static async Task<string> ReadHeadAsync(NetworkStream stream, CancellationToken ct)
        {
            var buffer = new byte[16 * 1024];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total), ct);
                if (read == 0)
                    break;
                total += read;
                if (Encoding.ASCII.GetString(buffer, 0, total).Contains("\r\n\r\n", StringComparison.Ordinal))
                    break;
            }
            return Encoding.ASCII.GetString(buffer, 0, total);
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            try { _loop.Wait(TimeSpan.FromSeconds(5)); } catch { /* shutting down */ }
            _stop.Dispose();
        }
    }
}
