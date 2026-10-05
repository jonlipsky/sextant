using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Sextant.Core;
using Sextant.Service;
using Sextant.Service.Host;
using Sextant.Store;

namespace Sextant.Integration.Tests;

/// <summary>
/// Issue #244 on a REAL clone-mode service: source text served for a published snapshot comes from that
/// snapshot's own indexed files, never from the repository's shared checkout. Commit A is indexed on
/// <c>main</c>; then commit B (another branch) is indexed, which re-provisions the ONE per-repository checkout at
/// B. B shifts and rewrites the lines of a file A references and deletes another. Every remote tool that serves
/// source text must still answer A's snapshot with A's exact text: before the fix the hash-gated snippet read
/// came back null for the changed and deleted files, and the raw <c>include_source</c> reads served B's lines
/// under A's line numbers.
/// </summary>
public partial class SubmoduleCheckoutIntegrationTests
{
    private const string WidgetCs = "namespace Lib;\npublic class Widget\n{\n    public int Make() => 42;\n}\n";
    private const string UserA = "namespace Lib;\npublic class User\n{\n    public int UseA() => new Widget().Make();\n}\n";
    private const string GoneA = "namespace Lib;\npublic class Gone\n{\n    public int UseGone() => new Widget().Make() + 1;\n}\n";
    private const string UserB =
        "namespace Lib;\n// moved by B\n// moved by B\npublic class User\n{\n    public int Changed() => 7;\n" +
        "    public int UseB() => new Widget().Make() * 2;\n}\n";

    [TestMethod]
    public async Task RemoteMcp_SnapshotSourceText_ComesFromTheSnapshot_NotTheMovedCheckout()
    {
        var (url, commitA, commitB) = CreateMovingRepo();

        var dbPath = Path.Combine(_tempDir, "catalog.db");
        var db = new IndexDatabase(dbPath, IndexWriteOptions.Default);
        db.RunMigrations();
        var config = new SextantConfiguration();
        var options = new ServiceOptions
        {
            CatalogDbPath = dbPath,
            Volumes = ServiceVolumes.Rooted(Path.Combine(_tempDir, "service")),
            DefaultConfigHash = IndexProfileDescriptor.FromConfiguration(config).ConfigurationHash,
            ControlToken = "control-secret",
            QueryToken = QueryToken,
            RequireRepositorySelection = true,
            RepositoryUrlPolicy = new RepositoryUrlPolicy([RepositoryUrlPolicy.DefaultHost]) { AllowFileTransportForTesting = true }
        };
        var paths = new ServicePaths(options.Volumes);
        var checkouts = new CloningCheckoutProvider(new PersistentVolumeCheckoutProvider(paths), paths, log: _log.Add)
        {
            AllowFileTransportForTesting = true
        };
        var worker = new LocalIndexerSnapshotWorker(
            db, config, checkouts, _log.Add, sourceTexts: new SourceTextStore(paths.SourceTextRoot, _log.Add));
        var service = SnapshotService.Start(options, worker, db);
        WebApplication? app = null;
        try
        {
            var ensuredA = await service.EnsureSnapshotAsync(new EnsureSnapshotRequest
            {
                RepositoryRemoteUrl = url, CommitSha = commitA, BranchName = "main", IsDefaultBranch = true
            });
            Assert.AreEqual("complete", ensuredA.Status, $"{ensuredA.Reason}\n{string.Join("\n", _log)}");

            // Index B on another branch: the repository's one checkout is re-provisioned at B.
            var ensuredB = await service.EnsureSnapshotAsync(new EnsureSnapshotRequest
            {
                RepositoryRemoteUrl = url, CommitSha = commitB, BranchName = "feature"
            });
            Assert.AreEqual("complete", ensuredB.Status, $"{ensuredB.Reason}\n{string.Join("\n", _log)}");
            var checkoutDir = Path.Combine(paths.CheckoutRoot, ServicePaths.RepoDirectoryName(url));
            Assert.AreEqual(UserB, File.ReadAllText(Path.Combine(checkoutDir, "Lib", "User.cs")).ReplaceLineEndings("\n"),
                "precondition: the shared checkout now holds commit B");
            Assert.IsFalse(File.Exists(Path.Combine(checkoutDir, "Lib", "Gone.cs")), "precondition: B deleted Gone.cs");

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            ServiceApp.RegisterServices(builder, options, service);
            app = builder.Build();
            ServiceApp.MapEndpoints(app, options);
            await app.StartAsync();
            using var client = app.GetTestClient();

            // find_references on A's snapshot (main): A's exact snippets, for the changed file and the deleted one.
            var refsA = await CallAsync(client, "find_references", url,
                new JsonObject { ["symbol_fqn"] = "global::Lib.Widget", ["include_source"] = true });
            var userRef = SingleRow(refsA, "Lib/User.cs");
            Assert.AreEqual(4, userRef.GetProperty("line").GetInt32(), refsA.GetRawText());
            Assert.AreEqual(Line(UserA, 4).Trim(), userRef.GetProperty("context_snippet").GetString(), refsA.GetRawText());
            Assert.AreEqual(Context(UserA, 4, 2), userRef.GetProperty("source_context").GetString(), refsA.GetRawText());
            var goneRef = SingleRow(refsA, "Lib/Gone.cs");
            Assert.AreEqual(Line(GoneA, 4).Trim(), goneRef.GetProperty("context_snippet").GetString(),
                "a file deleted in B still serves A's snippet: " + refsA.GetRawText());
            Assert.AreEqual(Context(GoneA, 4, 2), goneRef.GetProperty("source_context").GetString(), refsA.GetRawText());

            // find_symbol include_source on A's snapshot: A's declaration lines, not B's lines at A's numbers.
            var symbolA = await CallAsync(client, "find_symbol", url,
                new JsonObject { ["name"] = "Lib.User.UseA", ["include_source"] = true });
            var declaration = Results(symbolA).Single().GetProperty("source_context");
            Assert.AreEqual(JsonValueKind.Object, declaration.ValueKind, symbolA.GetRawText());
            CollectionAssert.AreEqual(new[] { Line(UserA, 4) }, SourceLines(declaration), symbolA.GetRawText());

            // get_call_hierarchy include_source on A's snapshot: the call site's context is A's.
            var callersA = await CallAsync(client, "get_call_hierarchy", url,
                new JsonObject { ["symbol_fqn"] = "Lib.Widget.Make", ["direction"] = "callers", ["include_source"] = true });
            var userCall = Results(callersA).Single(r => r.GetProperty("call_site_file").GetString() == "Lib/User.cs");
            var callContext = userCall.GetProperty("source_context");
            Assert.AreEqual(JsonValueKind.Object, callContext.ValueKind, callersA.GetRawText());
            CollectionAssert.AreEqual(Lines(UserA, 2, 5), SourceLines(callContext), callersA.GetRawText());
            var goneCall = Results(callersA).Single(r => r.GetProperty("call_site_file").GetString() == "Lib/Gone.cs");
            CollectionAssert.AreEqual(Lines(GoneA, 2, 5), SourceLines(goneCall.GetProperty("source_context")), callersA.GetRawText());

            // B's snapshot (feature) serves B's text for the same file at B's lines.
            var refsB = await CallAsync(client, "find_references", url,
                new JsonObject { ["symbol_fqn"] = "global::Lib.Widget", ["branch"] = "feature" });
            var userRefB = SingleRow(refsB, "Lib/User.cs");
            Assert.AreEqual(7, userRefB.GetProperty("line").GetInt32(), refsB.GetRawText());
            Assert.AreEqual(Line(UserB, 7).Trim(), userRefB.GetProperty("context_snippet").GetString(), refsB.GetRawText());
            Assert.IsFalse(Results(refsB).Any(r => r.GetProperty("file_path").GetString() == "Lib/Gone.cs"), refsB.GetRawText());
        }
        finally
        {
            if (app is not null)
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }
            service.Dispose();
            db.Dispose();
        }
    }

    /// <summary>A one-project repository: commit A on <c>main</c>, then commit B on <c>feature</c>.</summary>
    private (string url, string commitA, string commitB) CreateMovingRepo()
    {
        var dir = InitRepo("moving");
        Write(dir, ".gitignore", "bin/\nobj/\n");
        Write(dir, "App.slnx", "<Solution>\n  <Project Path=\"Lib/Lib.csproj\" />\n</Solution>\n");
        Write(dir, "Lib/Lib.csproj", Csproj());
        Write(dir, "Lib/Widget.cs", WidgetCs);
        Write(dir, "Lib/User.cs", UserA);
        Write(dir, "Lib/Gone.cs", GoneA);
        Git(dir, "add", "-A");
        Git(dir, "commit", "--quiet", "-m", "A");
        var commitA = GitOut(dir, "rev-parse", "HEAD").Trim();

        Git(dir, "checkout", "--quiet", "-b", "feature");
        Write(dir, "Lib/User.cs", UserB);
        File.Delete(Path.Combine(dir, "Lib", "Gone.cs"));
        Git(dir, "add", "-A");
        Git(dir, "commit", "--quiet", "-m", "B");
        var commitB = GitOut(dir, "rev-parse", "HEAD").Trim();
        Git(dir, "checkout", "--quiet", "main");
        return (new Uri(dir).AbsoluteUri, commitA, commitB);
    }

    private static JsonElement SingleRow(JsonElement response, string filePath)
    {
        var rows = Results(response).Where(r => r.GetProperty("file_path").GetString() == filePath).ToList();
        Assert.AreEqual(1, rows.Count, $"one reference in {filePath}: {response.GetRawText()}");
        return rows[0];
    }

    private static string Line(string text, int line) => text.Split('\n')[line - 1];

    private static string[] Lines(string text, int first, int last) => text.Split('\n')[(first - 1)..last];

    // The ±context lines find_references returns, clipped to the file (the trailing newline adds no line).
    private static string Context(string text, int line, int context)
    {
        var lines = text.TrimEnd('\n').Split('\n');
        var start = Math.Max(0, line - 1 - context);
        var end = Math.Min(lines.Length - 1, line - 1 + context);
        return string.Join("\n", lines[start..(end + 1)]);
    }

    private static string?[] SourceLines(JsonElement sourceContext) =>
        sourceContext.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("content").GetString()).ToArray();
}
