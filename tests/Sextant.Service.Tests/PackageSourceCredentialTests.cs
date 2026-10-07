using System.Text.Json.Nodes;
using Sextant.Core;
using Sextant.Service.Restore;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #231: operator-configured credentials for private package source hosts. Covers the configuration parser
/// (fail closed, never echoing a value), the exact host/port/scheme match, the NuGet plugin protocol the restore
/// child talks to, and the runner's per-restore credentials file and child environment.
/// </summary>
[TestClass]
public class PackageSourceCredentialTests
{
    private const string Secret = "s3cr3t:with-colon";

    // ==== Parsing ==================================================================================

    [TestMethod]
    public void Parse_Unset_IsNone_AndEntriesKeepPasswordColons_LowercaseHosts_AndOptionalPorts()
    {
        Assert.AreEqual(0, PackageSourceCredential.ParseList(null).Count);

        var parsed = PackageSourceCredential.ParseList($" NuGet.PKG.GitHub.com=x:{Secret} ; feed.example.test:8443=bot:pw ");

        Assert.AreEqual(2, parsed.Count);
        Assert.AreEqual("nuget.pkg.github.com", parsed[0].Host);
        Assert.IsNull(parsed[0].Port);
        Assert.AreEqual("x", parsed[0].Username);
        Assert.AreEqual(Secret, parsed[0].Password);
        Assert.AreEqual("feed.example.test", parsed[1].Host);
        Assert.AreEqual(8443, parsed[1].Port);
        Assert.AreEqual("feed.example.test:8443", parsed[1].HostAndPort);

        var explicitDefault = PackageSourceCredential.ParseList("feed.example.test:443=x:pw").Single();
        Assert.IsNull(explicitDefault.Port, "an explicit :443 is the same source as no port");
    }

    [TestMethod]
    [DataRow(";")]
    [DataRow("nuget.pkg.github.com")]
    [DataRow("nuget.pkg.github.com=x")]
    [DataRow("=x:SECRETVALUE")]
    [DataRow("https://nuget.pkg.github.com=x:SECRETVALUE")]
    [DataRow("nuget.pkg.github.com/elevenworks=x:SECRETVALUE")]
    [DataRow("*.github.com=x:SECRETVALUE")]
    [DataRow("user@nuget.pkg.github.com=x:SECRETVALUE")]
    [DataRow("nuget.pkg.github.com:0=x:SECRETVALUE")]
    [DataRow("nuget.pkg.github.com:99999=x:SECRETVALUE")]
    [DataRow("nuget.pkg.github.com:=x:SECRETVALUE")]
    [DataRow("nuget.pkg.github.com=:SECRETVALUE")]
    [DataRow("nuget.pkg.github.com=x:")]
    [DataRow("nuget.pkg.github.com=a b:SECRETVALUE")]
    [DataRow("nuget.pkg.github.com=x:SECRETVALUE;NUGET.pkg.github.com=y:SECRETVALUE")]
    [DataRow("nuget.pkg.github.com=x:SECRETVALUE;nuget.pkg.github.com:443=y:SECRETVALUE")]
    public void Parse_Malformed_FailsClosed_WithoutEchoingTheValue(string value)
    {
        var ex = Assert.ThrowsExactly<FormatException>(() => PackageSourceCredential.ParseList(value));
        Assert.IsFalse(ex.Message.Contains("SECRETVALUE", StringComparison.Ordinal), ex.Message);
        Assert.IsFalse(ex.Message.Contains("github", StringComparison.OrdinalIgnoreCase), ex.Message);
    }

    [TestMethod]
    public void ToString_NeverIncludesThePassword()
    {
        var credential = PackageSourceCredential.ParseList($"nuget.pkg.github.com=x:{Secret}")[0];
        Assert.IsFalse(credential.ToString().Contains(Secret, StringComparison.Ordinal), credential.ToString());
    }

    [TestMethod]
    public void ServiceOptions_ParsesTheVariable_AndRefusesToStartOnAMalformedOne_WithoutEchoingIt()
    {
        const string name = "SEXTANT_SERVICE_PACKAGE_SOURCE_CREDENTIALS";
        var config = new SextantConfiguration { DbPath = ServiceTestFixtures.NewDbPath() };
        var before = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, null);
            Assert.AreEqual(0, ServiceOptions.FromEnvironment(config).PackageSourceCredentials.Count);

            Environment.SetEnvironmentVariable(name, $"nuget.pkg.github.com=x:{Secret}");
            Assert.AreEqual("nuget.pkg.github.com", ServiceOptions.FromEnvironment(config).PackageSourceCredentials.Single().Host);

            Environment.SetEnvironmentVariable(name, "https://nuget.pkg.github.com=x:SECRETVALUE");
            var ex = Assert.ThrowsExactly<InvalidOperationException>(() => ServiceOptions.FromEnvironment(config));
            StringAssert.Contains(ex.Message, "PACKAGE_SOURCE_CREDENTIALS");
            Assert.IsFalse(ex.Message.Contains("SECRETVALUE", StringComparison.Ordinal), ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, before);
        }
    }

    // ==== Matching =================================================================================

    [TestMethod]
    [DataRow("https://nuget.pkg.github.com/elevenworks/index.json", true)]
    [DataRow("https://NUGET.PKG.GITHUB.COM/elevenworks/download/x/index.json", true)]
    [DataRow("https://nuget.pkg.github.com:443/elevenworks/index.json", true)]
    [DataRow("http://nuget.pkg.github.com/elevenworks/index.json", false)]
    [DataRow("https://nuget.pkg.github.com:8443/elevenworks/index.json", false)]
    [DataRow("https://nuget.pkg.github.com.attacker.test/index.json", false)]
    [DataRow("https://evil-nuget.pkg.github.com/index.json", false)]
    [DataRow("https://pkg.github.com/index.json", false)]
    [DataRow("https://nuget.pkg.github.com@attacker.test/index.json", false)]
    [DataRow("https://x:y@nuget.pkg.github.com/index.json", false)]
    public void Matches_OnlyHttps_TheExactHost_AndTheDefaultPort(string uri, bool expected)
    {
        var credential = new PackageSourceCredential("nuget.pkg.github.com", "x", Secret);
        Assert.AreEqual(expected, credential.Matches(new Uri(uri)), uri);
    }

    [TestMethod]
    public void Matches_AConfiguredPort_OnlyThatPort()
    {
        var credential = new PackageSourceCredential("feed.example.test", "x", Secret) { Port = 8443 };
        Assert.IsTrue(credential.Matches(new Uri("https://feed.example.test:8443/v3/index.json")));
        Assert.IsFalse(credential.Matches(new Uri("https://feed.example.test/v3/index.json")));
        Assert.IsFalse(credential.Matches(new Uri("https://feed.example.test:9443/v3/index.json")));
    }

    // ==== Plugin protocol ==========================================================================

    private static readonly PackageSourceCredential GitHub = new("nuget.pkg.github.com", "x", Secret);

    private static async Task<List<JsonObject>> RunPluginAsync(
        Func<IReadOnlyList<PackageSourceCredential>> load, params JsonObject[] requests)
    {
        var input = new StringReader(string.Join("\n", requests.Select(r => r.ToJsonString())) + "\n");
        var output = new StringWriter();
        await FeedCredentialPlugin.RunAsync(input, output, load, CancellationToken.None);
        return output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonNode.Parse(line)!.AsObject()).ToList();
    }

    private static JsonObject Request(string id, string method, JsonObject? payload = null) => new()
    {
        ["RequestId"] = id,
        ["Type"] = "Request",
        ["Method"] = method,
        ["Payload"] = payload ?? new JsonObject()
    };

    private static JsonObject ResponseTo(List<JsonObject> messages, string id)
        => messages.Single(m => (string?)m["RequestId"] == id && (string?)m["Type"] == "Response");

    [TestMethod]
    public async Task Plugin_SendsItsHandshake_AnswersTheSessionSetup_AndEndsOnClose()
    {
        var messages = await RunPluginAsync(() => [GitHub],
            Request("h", "Handshake", new JsonObject { ["ProtocolVersion"] = "2.0.0", ["MinimumProtocolVersion"] = "1.0.0" }),
            Request("m", "MonitorNuGetProcessExit", new JsonObject { ["ProcessId"] = 1 }),
            Request("i", "Initialize", new JsonObject { ["ClientVersion"] = "7.9.0", ["Culture"] = "en-US", ["RequestTimeout"] = "00:00:30" }),
            Request("l", "SetLogLevel", new JsonObject { ["LogLevel"] = "Debug" }),
            Request("c", "Close"),
            Request("after", "Initialize"));

        Assert.AreEqual("Request", (string?)messages[0]["Type"]);
        Assert.AreEqual("Handshake", (string?)messages[0]["Method"]);
        Assert.AreEqual("2.0.0", (string?)messages[0]["Payload"]!["ProtocolVersion"]);
        Assert.AreEqual("Success", (string?)ResponseTo(messages, "h")["Payload"]!["ResponseCode"]);
        Assert.AreEqual("2.0.0", (string?)ResponseTo(messages, "h")["Payload"]!["ProtocolVersion"]);
        Assert.AreEqual("NotFound", (string?)ResponseTo(messages, "m")["Payload"]!["ResponseCode"]);
        Assert.AreEqual("Success", (string?)ResponseTo(messages, "i")["Payload"]!["ResponseCode"]);
        Assert.AreEqual("Success", (string?)ResponseTo(messages, "l")["Payload"]!["ResponseCode"]);
        Assert.IsFalse(messages.Any(m => (string?)m["RequestId"] is "c" or "after"), "nothing is answered after Close");
    }

    [TestMethod]
    public async Task Plugin_ClaimsAuthentication_OnlyForAConfiguredHost_OrASourceAgnosticQuery()
    {
        var messages = await RunPluginAsync(() => [GitHub],
            Request("any", "GetOperationClaims"),
            Request("gh", "GetOperationClaims", new JsonObject { ["PackageSourceRepository"] = "https://nuget.pkg.github.com/elevenworks/index.json" }),
            Request("org", "GetOperationClaims", new JsonObject { ["PackageSourceRepository"] = "https://api.nuget.org/v3/index.json" }));

        Assert.AreEqual(1, ResponseTo(messages, "any")["Payload"]!["Claims"]!.AsArray().Count);
        Assert.AreEqual("Authentication", (string?)ResponseTo(messages, "gh")["Payload"]!["Claims"]![0]);
        Assert.AreEqual(0, ResponseTo(messages, "org")["Payload"]!["Claims"]!.AsArray().Count);
    }

    [TestMethod]
    public async Task Plugin_ReturnsTheCredential_OnlyForAMatchingRequest_EvenWhenTheSourceKeyIsTheSame()
    {
        static JsonObject Ask(string id, string uri) => Request(id, "GetAuthenticationCredentials", new JsonObject
        {
            ["Uri"] = uri, ["IsRetry"] = false, ["IsNonInteractive"] = true, ["CanShowDialog"] = false
        });
        var messages = await RunPluginAsync(() => [GitHub],
            Ask("ok", "https://nuget.pkg.github.com/elevenworks/index.json"),
            // A repository's nuget.config pointing the same source key at another host asks with that host's URI.
            Ask("redirected", "https://nuget.pkg.github.com.attacker.test/elevenworks/index.json"),
            Ask("http", "http://nuget.pkg.github.com/elevenworks/index.json"),
            Ask("garbage", "not a uri"));

        var ok = ResponseTo(messages, "ok")["Payload"]!;
        Assert.AreEqual("Success", (string?)ok["ResponseCode"]);
        Assert.AreEqual("x", (string?)ok["Username"]);
        Assert.AreEqual(Secret, (string?)ok["Password"]);
        foreach (var id in new[] { "redirected", "http", "garbage" })
        {
            var payload = ResponseTo(messages, id)["Payload"]!;
            Assert.AreEqual("NotFound", (string?)payload["ResponseCode"], id);
            Assert.IsNull(payload["Password"], id);
        }
    }

    [TestMethod]
    public async Task Plugin_UnreadableCredentials_AnswerNotFound_AndTheSessionContinues()
    {
        var messages = await RunPluginAsync(() => throw new IOException("gone"),
            Request("a", "GetAuthenticationCredentials", new JsonObject { ["Uri"] = "https://nuget.pkg.github.com/x/index.json" }),
            Request("b", "Initialize"));

        Assert.AreEqual("NotFound", (string?)ResponseTo(messages, "a")["Payload"]!["ResponseCode"]);
        Assert.AreEqual("Success", (string?)ResponseTo(messages, "b")["Payload"]!["ResponseCode"]);
    }

    [TestMethod]
    public void CredentialsFile_RoundTrips_HostPortAndUser_AndIsOwnerOnly()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sextant-feedcred-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "credentials.json");
            FeedCredentialPlugin.WriteCredentialsFile(path,
                [GitHub, new PackageSourceCredential("localhost", "bot", "pw") { Port = 5443 }]);

            var read = FeedCredentialPlugin.ReadCredentialsFile(path);
            Assert.AreEqual(2, read.Count);
            Assert.AreEqual(GitHub, read[0]);
            Assert.AreEqual(5443, read[1].Port);
            if (!OperatingSystem.IsWindows())
                Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.AreEqual(0, FeedCredentialPlugin.ReadCredentialsFile(null).Count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void IsPluginInvocation_OnlyForNuGetsArgument()
    {
        Assert.IsTrue(FeedCredentialPlugin.IsPluginInvocation(["-Plugin"]));
        Assert.IsTrue(FeedCredentialPlugin.IsPluginInvocation(["-plugin", "x"]));
        Assert.IsFalse(FeedCredentialPlugin.IsPluginInvocation([]));
        Assert.IsFalse(FeedCredentialPlugin.IsPluginInvocation(["service"]));
    }

    // ==== Runner ===================================================================================

    [TestMethod]
    public void Runner_CredentialsWithoutThePluginAssembly_RefuseToConstruct()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new PackageRestoreRunner(sourceCredentials: [GitHub]));
        Assert.ThrowsExactly<ArgumentException>(() => new PackageRestoreRunner(
            sourceCredentials: [GitHub], credentialPluginPath: Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dll")));
        Assert.AreEqual(0, new PackageRestoreRunner().CredentialHosts.Count);
    }

    [TestMethod]
    public void Runner_WithoutCredentials_LeavesNoPluginVariable_AndDropsAnInheritedCredentialsFile()
    {
        var before = Environment.GetEnvironmentVariable(FeedCredentialPlugin.CredentialsFileVariable);
        Environment.SetEnvironmentVariable(FeedCredentialPlugin.CredentialsFileVariable, "/somewhere/else.json");
        try
        {
            var startInfo = new PackageRestoreRunner().CreateStartInfo(Path.Combine(Path.GetTempPath(), "All.slnx"));
            Assert.IsFalse(startInfo.Environment.ContainsKey(FeedCredentialPlugin.CredentialsFileVariable));
        }
        finally
        {
            Environment.SetEnvironmentVariable(FeedCredentialPlugin.CredentialsFileVariable, before);
        }
    }

    [TestMethod]
    public async Task Runner_WithCredentials_PointsTheChildAtThePluginAndAnOwnerOnlyFile_ThenDeletesIt()
    {
        if (OperatingSystem.IsWindows())
            Assert.Inconclusive("Uses a POSIX shell script as the dotnet host.");

        var root = Path.Combine(Path.GetTempPath(), "sextant-feedcred-run-" + Guid.NewGuid().ToString("N"));
        var scratch = Path.Combine(root, "scratch");
        Directory.CreateDirectory(scratch);
        try
        {
            var plugin = Path.Combine(root, "Plugin.dll");
            File.WriteAllText(plugin, string.Empty);
            var report = Path.Combine(root, "report.txt");
            // Stands in for dotnet: records what the restore child sees, including the credentials file itself.
            var fakeDotnet = Path.Combine(root, "fake-dotnet.sh");
            File.WriteAllText(fakeDotnet, $$"""
                #!/bin/sh
                {
                  echo "plugins=$NUGET_PLUGIN_PATHS"
                  echo "file=${{FeedCredentialPlugin.CredentialsFileVariable}}"
                  echo "cache=$NUGET_PLUGINS_CACHE_PATH"
                  echo "mode=$(stat -c %a "${{FeedCredentialPlugin.CredentialsFileVariable}}")"
                  echo "args=$*"
                  env | grep -c '{{Secret}}' | sed 's/^/envhits=/'
                  grep -c 'nuget.pkg.github.com' "${{FeedCredentialPlugin.CredentialsFileVariable}}" | sed 's/^/filehosts=/'
                } > '{{report}}'
                """);
            File.SetUnixFileMode(fakeDotnet, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var solution = Path.Combine(root, "All.slnx");
            File.WriteAllText(solution, "<Solution />");

            var runner = new PackageRestoreRunner(sourceCredentials: [GitHub], credentialPluginPath: plugin)
            {
                DotnetPath = fakeDotnet
            };
            await runner.RunAsync(root, [solution], limit: null, CancellationToken.None, scratch);

            var lines = File.ReadAllLines(report).ToDictionary(l => l[..l.IndexOf('=')], l => l[(l.IndexOf('=') + 1)..]);
            Assert.AreEqual(plugin, lines["plugins"]);
            var file = lines["file"];
            StringAssert.StartsWith(file, Path.GetFullPath(scratch));
            StringAssert.StartsWith(lines["cache"], Path.GetDirectoryName(file)!);
            Assert.AreEqual("600", lines["mode"]);
            Assert.AreEqual("1", lines["filehosts"]);
            Assert.AreEqual("0", lines["envhits"], "the credential never travels in the child's environment");
            Assert.IsFalse(lines["args"].Contains(Secret, StringComparison.Ordinal), "nor on its command line");
            Assert.IsFalse(File.Exists(file), "the credentials file is deleted when the restore ends");
            Assert.AreEqual(0, Directory.GetDirectories(scratch).Length, "and so is its directory");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // A stand-in dotnet host (POSIX shell) that records what the restore child sees, then runs extra commands.
    private static string FakeDotnet(string root, string report, string then = "")
    {
        var path = Path.Combine(root, "fake-dotnet-" + Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(path, $$"""
            #!/bin/sh
            {
              echo "plugins=${NUGET_PLUGIN_PATHS:-<unset>}"
              echo "file=${{{FeedCredentialPlugin.CredentialsFileVariable}}:-<unset>}"
              echo "args=$*"
            } >> '{{report}}'
            {{then}}
            """);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static string NewRoot(out string scratch, out string plugin)
    {
        var root = Path.Combine(Path.GetTempPath(), "sextant-feedcred-run-" + Guid.NewGuid().ToString("N"));
        scratch = Path.Combine(root, "scratch");
        Directory.CreateDirectory(scratch);
        plugin = Path.Combine(root, "Plugin.dll");
        File.WriteAllText(plugin, string.Empty);
        return root;
    }

    [TestMethod]
    public async Task Runner_CancelledMidRestore_StillDeletesTheCredentials()
    {
        if (OperatingSystem.IsWindows())
            Assert.Inconclusive("Uses a POSIX shell script as the dotnet host.");

        var root = NewRoot(out var scratch, out var plugin);
        try
        {
            var report = Path.Combine(root, "report.txt");
            var solution = Path.Combine(root, "All.slnx");
            File.WriteAllText(solution, "<Solution />");
            var runner = new PackageRestoreRunner(sourceCredentials: [GitHub], credentialPluginPath: plugin)
            {
                DotnetPath = FakeDotnet(root, report, then: "sleep 60")
            };
            using var cancel = new CancellationTokenSource();
            var run = runner.RunAsync(root, [solution], limit: null, cancel.Token, scratch);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!File.Exists(report) && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            Assert.IsTrue(File.Exists(report), "the restore child started");
            Assert.AreEqual(1, Directory.GetDirectories(scratch).Length, "its credentials exist while it runs");

            await cancel.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => run);

            Assert.AreEqual(0, Directory.GetDirectories(scratch).Length, "a cancelled restore leaves no credentials behind");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Runner_ThatCannotStageTheCredentials_RunsWithoutThem_AndReportsIt()
    {
        if (OperatingSystem.IsWindows())
            Assert.Inconclusive("Uses POSIX file modes and a shell script as the dotnet host.");

        var root = NewRoot(out var scratch, out var plugin);
        try
        {
            var report = Path.Combine(root, "report.txt");
            var solution = Path.Combine(root, "All.slnx");
            File.WriteAllText(solution, "<Solution />");
            var runner = new PackageRestoreRunner(sourceCredentials: [GitHub], credentialPluginPath: plugin)
            {
                DotnetPath = FakeDotnet(root, report)
            };
            File.SetUnixFileMode(scratch, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            PackageRestoreOutcome outcome;
            try
            {
                outcome = await runner.RunAsync(root, [solution], limit: null, CancellationToken.None, scratch);
            }
            finally
            {
                File.SetUnixFileMode(scratch, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var lines = File.ReadAllLines(report);
            CollectionAssert.Contains(lines, "plugins=<unset>");
            CollectionAssert.Contains(lines, "file=<unset>");
            Assert.IsTrue(outcome.CredentialsUnavailable);
            Assert.IsFalse(outcome.Clean, "a restore that could not use its credentials is not clean");
            Assert.IsTrue(outcome.Notes().Any(n => n.Contains("credentials", StringComparison.OrdinalIgnoreCase)),
                string.Join("\n", outcome.Notes()));
            Assert.AreEqual(0, Directory.GetDirectories(scratch).Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Runner_UnionRestore_GetsThePluginVariablesToo()
    {
        if (OperatingSystem.IsWindows())
            Assert.Inconclusive("Uses a POSIX shell script as the dotnet host.");

        var root = NewRoot(out var scratch, out var plugin);
        try
        {
            foreach (var name in new[] { "A", "B" })
            {
                Directory.CreateDirectory(Path.Combine(root, name));
                File.WriteAllText(Path.Combine(root, name, $"{name}.csproj"),
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
                File.WriteAllText(Path.Combine(root, $"{name}.slnx"), $"<Solution><Project Path=\"{name}/{name}.csproj\" /></Solution>");
            }
            var report = Path.Combine(root, "report.txt");
            var runner = new PackageRestoreRunner(sourceCredentials: [GitHub], credentialPluginPath: plugin)
            {
                DotnetPath = FakeDotnet(root, report)
            };

            var outcome = await runner.RunAsync(
                root, [Path.Combine(root, "A.slnx"), Path.Combine(root, "B.slnx")], limit: null, CancellationToken.None, scratch);

            Assert.IsTrue(outcome.UsedProjectUnion);
            var lines = File.ReadAllLines(report);
            Assert.IsTrue(lines.Any(l => l.StartsWith("args=msbuild", StringComparison.Ordinal) && l.Contains("RestoreUnion")),
                string.Join("\n", lines));
            CollectionAssert.Contains(lines, $"plugins={plugin}");
            Assert.IsTrue(lines.Any(l => l.StartsWith("file=" + Path.GetFullPath(scratch), StringComparison.Ordinal)));
            Assert.IsFalse(outcome.CredentialsUnavailable);
            Assert.AreEqual(0, Directory.GetDirectories(scratch).Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void Host_WithCredentials_RefusesAnEntryAssemblyThatDoesNotServeThePlugin()
    {
        // Under the test host the entry assembly is neither Sextant.Cli nor Sextant.Service.Host.
        var ex = Assert.ThrowsExactly<InvalidOperationException>(Sextant.Service.Host.ServiceHostRunner.CredentialPluginPath);
        StringAssert.Contains(ex.Message, "PACKAGE_SOURCE_CREDENTIALS");
    }
}
