using Sextant.Core;
using Sextant.Service.Restore;

namespace Sextant.Service.Tests;

/// <summary>
/// The worker's NuGet restore step (the live incident's root cause: without a restore there is no assets file,
/// so a project's transitive ProjectReferences never reach the compiler). Covers the output parser, the outcome's
/// notes and load issues, the runner on a real solution and on its failure paths, and the identity contract.
/// </summary>
[TestClass]
public class PackageRestoreTests
{
    private static readonly string Checkout = Path.Combine(Path.GetTempPath(), "sextant-restore-parser");

    // ==== RestoreOutputParser ==========================================================================

    [TestMethod]
    public void Parser_RecordsProjectErrors_MissingPackages_AndUnreachableSources_Relativized()
    {
        var parser = new RestoreOutputParser(Checkout);
        var api = Path.Combine(Checkout, "src", "Api", "Api.csproj");
        var web = Path.Combine(Checkout, "src", "Web", "Web.csproj");

        parser.Accept($"    {api} : error NU1101: Unable to find package Acme.Auth. No packages exist with this id in source(s): nuget.org [{Checkout}/All.slnx]");
        parser.Accept($"{api} : error NU1301: Unable to load the service index for source https://user:secret@nuget.example.test/v3/index.json. [{Checkout}/All.slnx]");
        parser.Accept($"{web} : error NU1102: Unable to find package Acme.UI with version (>= 2.0.0)");
        parser.Accept($"{web} : error NU1103: Unable to find a stable package Acme.Beta with version (>= 1.0.0)");
        parser.Accept("  Determining projects to restore...");
        parser.Accept("");

        var projects = parser.Projects();
        Assert.AreEqual(2, projects.Count);
        var apiIssue = projects[0];
        Assert.AreEqual("src/Api/Api.csproj", apiIssue.Project);
        CollectionAssert.AreEqual(new[] { "NU1101", "NU1301" }, apiIssue.Codes.ToArray());
        CollectionAssert.AreEqual(new[] { "Acme.Auth" }, apiIssue.MissingPackages.ToArray());
        Assert.IsTrue(apiIssue.SourceUnreachable);
        var webIssue = projects[1];
        Assert.AreEqual("src/Web/Web.csproj", webIssue.Project);
        CollectionAssert.AreEqual(new[] { "Acme.Beta", "Acme.UI" }, webIssue.MissingPackages.ToArray());
        Assert.IsFalse(webIssue.SourceUnreachable);
        Assert.AreEqual(0, parser.GeneralCodes().Count);
    }

    [TestMethod]
    public void Parser_NeverKeepsTheRawMessage_SoASourceUrlOrCredentialCannotLeak()
    {
        var parser = new RestoreOutputParser(Checkout);
        parser.Accept($"{Path.Combine(Checkout, "a", "A.csproj")} : error NU1301: Unable to load the service index for source https://user:hunter2@feed.example.test/index.json.");

        var outcome = new PackageRestoreOutcome { SolutionsAttempted = 1, Projects = parser.Projects() };
        var text = string.Join("\n", outcome.Notes()) + "\n" + string.Join("\n", outcome.ProjectLoadIssues().Values);
        Assert.IsFalse(text.Contains("hunter2", StringComparison.Ordinal), text);
        Assert.IsFalse(text.Contains("feed.example.test", StringComparison.Ordinal), text);
    }

    [TestMethod]
    public void Parser_RecordsTheNU1801Warning_ThatIgnoreFailedSourcesReports_EvenWhenItsMessageSaysError()
    {
        // The shape `dotnet restore --ignore-failed-sources` prints for an unreachable source (captured from a real
        // restore against https://127.0.0.1:9): a project-scoped WARNING, never the error NU1301.
        var parser = new RestoreOutputParser(Checkout);
        var bad = Path.Combine(Checkout, "Bad", "Bad.csproj");
        parser.Accept($"{bad} : warning NU1801: Unable to load the service index for source https://127.0.0.1:9/v3/index.json. [{Checkout}\\All.slnx]");
        var api = Path.Combine(Checkout, "Api", "Api.csproj");
        parser.Accept($"{api} : warning NU1801: Unable to load the service index for source https://user:hunter2@feed.example.test/index.json. An error occurred while sending the request.");
        parser.Accept($"{api} : warning NU1603: Acme.Core 1.0.0 depends on Foo (>= 1.0.0) but Foo 1.0.0 was not found.");

        var projects = parser.Projects();
        CollectionAssert.AreEqual(new[] { "Api/Api.csproj", "Bad/Bad.csproj" }, projects.Select(p => p.Project).ToArray());
        Assert.IsTrue(projects.All(p => p.SourceUnreachable));
        CollectionAssert.AreEqual(new[] { "NU1801" }, projects[0].Codes.ToArray(), "other warnings are not restore failures");
        Assert.AreEqual(0, parser.GeneralCodes().Count, "the word 'error' in an NU1801 message is not an error line");
        Assert.IsFalse(parser.SourceUnreachableGeneral);

        var outcome = new PackageRestoreOutcome { SolutionsAttempted = 1, SolutionsSucceeded = 1, Projects = projects };
        Assert.IsFalse(outcome.Clean, "a restore that skipped a source did not restore everything");
        var text = string.Join("\n", outcome.Notes()) + "\n" + string.Join("\n", outcome.ProjectLoadIssues().Values);
        StringAssert.Contains(text, "A configured package source could not be reached while restoring 2 project(s)");
        Assert.IsFalse(text.Contains("hunter2", StringComparison.Ordinal), text);
        Assert.IsFalse(text.Contains("example.test", StringComparison.Ordinal), text);
        Assert.IsFalse(text.Contains("127.0.0.1", StringComparison.Ordinal), text);
    }

    [TestMethod]
    public void Parser_AnNU1801WarningThatNamesNoProject_IsAGeneralSourceFailure()
    {
        var parser = new RestoreOutputParser(Checkout);
        parser.Accept("  warning NU1801: Unable to load the service index for source https://feed.example.test/index.json.");

        Assert.IsTrue(parser.SourceUnreachableGeneral);
        Assert.AreEqual(0, parser.Projects().Count);
        var outcome = new PackageRestoreOutcome
        {
            SolutionsAttempted = 1, SolutionsSucceeded = 1, SourceUnreachableGeneral = parser.SourceUnreachableGeneral
        };
        Assert.IsFalse(outcome.Clean);
        CollectionAssert.AreEqual(new[]
        {
            "A configured package source could not be reached during package restore; packages only that source provides were not restored."
        }, outcome.Notes().ToArray());
    }

    [TestMethod]
    public void Parser_APathOutsideTheCheckout_KeepsOnlyItsFileName()
    {
        var parser = new RestoreOutputParser(Checkout);
        var outside = Path.Combine(Path.GetTempPath(), "elsewhere-" + Guid.NewGuid().ToString("N"), "Secret.csproj");
        parser.Accept($"{outside} : error NU1101: Unable to find package Foo.");

        Assert.AreEqual("Secret.csproj", parser.Projects().Single().Project);
    }

    [TestMethod]
    public void Parser_KeepsGeneralCodes_ThatNameNoProject_Capped()
    {
        var parser = new RestoreOutputParser(Checkout);
        parser.Accept("MSBUILD : error MSB1009: Project file does not exist.");
        for (var i = 0; i < RestoreOutputParser.MaxGeneralCodes + 5; i++)
            parser.Accept($"error XYZ{1000 + i}: something");

        var codes = parser.GeneralCodes();
        Assert.AreEqual(RestoreOutputParser.MaxGeneralCodes, codes.Count);
        Assert.IsTrue(codes.Contains("MSB1009"));
        Assert.AreEqual(0, parser.Projects().Count);
    }

    [TestMethod]
    public void Parser_CapsProjectsAndValuesPerProject()
    {
        var parser = new RestoreOutputParser(Checkout);
        for (var i = 0; i < RestoreOutputParser.MaxProjects + 3; i++)
            parser.Accept($"{Path.Combine(Checkout, $"P{i:D4}", $"P{i:D4}.csproj")} : error NU1101: Unable to find package Pkg{i}.");
        var one = Path.Combine(Checkout, "P0000", "P0000.csproj");
        for (var i = 0; i < RestoreOutputParser.MaxValuesPerProject + 5; i++)
            parser.Accept($"{one} : error NU1101: Unable to find package Extra{i:D2}.");

        Assert.AreEqual(RestoreOutputParser.MaxProjects, parser.Projects().Count);
        Assert.AreEqual(3, parser.ProjectsDropped);
        Assert.AreEqual(RestoreOutputParser.MaxValuesPerProject,
            parser.Projects().Single(p => p.Project == "P0000/P0000.csproj").MissingPackages.Count);
    }

    // ==== PackageRestoreOutcome =======================================================================

    [TestMethod]
    public void Outcome_Clean_HasNoNotesOrIssues()
    {
        var outcome = new PackageRestoreOutcome { SolutionsAttempted = 2, SolutionsSucceeded = 2 };

        Assert.IsTrue(outcome.Clean);
        Assert.AreEqual(0, outcome.Notes().Count);
        Assert.AreEqual(0, outcome.ProjectLoadIssues().Count);
        Assert.IsTrue(PackageRestoreOutcome.Disabled.Clean);
        Assert.AreEqual(0, PackageRestoreOutcome.Disabled.Notes().Count, "a node with restore off says nothing per job");
    }

    [TestMethod]
    public void Outcome_Notes_NameMissingPackages_UnreachableSources_TimeoutsAndStartFailures()
    {
        var outcome = new PackageRestoreOutcome
        {
            SolutionsAttempted = 3,
            SolutionsSucceeded = 0,
            SolutionsNotStarted = 1,
            TimedOut = true,
            Timeout = TimeSpan.FromSeconds(300),
            Projects =
            [
                new PackageRestoreProjectIssue
                {
                    Project = "src/Api/Api.csproj", Codes = ["NU1101", "NU1301"], MissingPackages = ["Acme.Auth"],
                    SourceUnreachable = true
                },
                new PackageRestoreProjectIssue { Project = "src/Odd/Odd.csproj", Codes = ["NU1202"] }
            ]
        };

        Assert.IsFalse(outcome.Clean);
        CollectionAssert.AreEqual(new[]
        {
            "Package restore could not be started for 1 solution(s), so their projects were loaded without restored packages and code that uses packages may not bind.",
            "Package restore did not finish within 300s and was stopped; projects it had not restored were loaded without their packages, so code in them may not bind.",
            "Package restore could not find 1 package(s) (Acme.Auth) for 1 project(s); code that uses them may not bind.",
            "A configured package source could not be reached while restoring 1 project(s); packages only that source provides were not restored.",
            "Package restore reported errors (NU1202) for 1 project(s)."
        }, outcome.Notes().ToArray());

        var issues = outcome.ProjectLoadIssues();
        Assert.AreEqual("restore: package(s) not found: Acme.Auth; a package source was unreachable", issues["src/Api/Api.csproj"]);
        Assert.AreEqual("restore: errors NU1202", issues["src/Odd/Odd.csproj"]);
    }

    [TestMethod]
    public void Outcome_AFailureWithNoRecognizedCode_StillLeavesANote()
    {
        var outcome = new PackageRestoreOutcome { SolutionsAttempted = 3, SolutionsSucceeded = 1 };

        Assert.AreEqual(2, outcome.SolutionsFailed);
        Assert.IsFalse(outcome.Clean);
        CollectionAssert.AreEqual(new[]
        {
            "Package restore failed for 2 solution(s) without a recognized error code; their projects may have been loaded without restored packages, so code in them may not bind."
        }, outcome.Notes().ToArray());

        var timedOut = new PackageRestoreOutcome
        {
            SolutionsAttempted = 1, TimedOut = true, Timeout = TimeSpan.FromSeconds(10)
        };
        Assert.AreEqual(0, timedOut.SolutionsFailed, "the solution a timeout stopped is reported as the timeout");
        Assert.AreEqual(1, timedOut.Notes().Count);
    }

    [TestMethod]
    public void Outcome_JoinCapped_SummarisesTheRest()
    {
        Assert.AreEqual("a, b", PackageRestoreOutcome.JoinCapped(["a", "b"], 5));
        Assert.AreEqual("a, b, +3 more", PackageRestoreOutcome.JoinCapped(["a", "b", "c", "d", "e"], 2));
    }

    // ==== PackageRestoreRunner ========================================================================

    [TestMethod]
    public async Task Runner_Disabled_RunsNothing()
    {
        var runner = new PackageRestoreRunner(enabled: false) { DotnetPath = "definitely-not-dotnet-" + Guid.NewGuid().ToString("N") };

        var outcome = await runner.RunAsync(Checkout, ["x.sln"], limit: null, CancellationToken.None);

        Assert.AreSame(PackageRestoreOutcome.Disabled, outcome);
        Assert.AreEqual(PackageRestoreRunner.DisabledIdentityComponent, runner.IdentityComponent);
    }

    [TestMethod]
    public async Task Runner_ThatCannotStart_RecordsIt_AndNeverThrows()
    {
        var logs = new List<string>();
        var runner = new PackageRestoreRunner(log: logs.Add)
        {
            DotnetPath = Path.Combine(Path.GetTempPath(), "no-dotnet-" + Guid.NewGuid().ToString("N"), "dotnet")
        };

        var outcome = await runner.RunAsync(Checkout, ["a.sln", "b.sln"], limit: null, CancellationToken.None);

        Assert.AreEqual(2, outcome.SolutionsAttempted);
        Assert.AreEqual(2, outcome.SolutionsNotStarted);
        Assert.IsFalse(outcome.Clean);
        StringAssert.StartsWith(outcome.Notes()[0], "Package restore could not be started for 2 solution(s)");
        Assert.IsTrue(logs.Any(l => l.Contains("could not start", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Runner_Limit_ShorterThanTheTimeout_IsTheDeadline_AndNeverExtendsIt()
    {
        // Issue #245: the worker passes its time plan's restore share as the limit. A missing host makes every
        // started restore return at once, so the outcome shows the deadline each run was given.
        var runner = new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(300))
        {
            DotnetPath = Path.Combine(Path.GetTempPath(), "no-dotnet-" + Guid.NewGuid().ToString("N"), "dotnet")
        };

        var shorter = await runner.RunAsync(Checkout, ["a.sln"], TimeSpan.FromSeconds(7), CancellationToken.None);
        Assert.AreEqual(TimeSpan.FromSeconds(7), shorter.Timeout);
        Assert.AreEqual(1, shorter.SolutionsAttempted);

        var longer = await runner.RunAsync(Checkout, ["a.sln"], TimeSpan.FromMinutes(10), CancellationToken.None);
        Assert.AreEqual(TimeSpan.FromSeconds(300), longer.Timeout, "a longer limit never extends the configured timeout");

        var none = await runner.RunAsync(Checkout, ["a.sln"], limit: null, CancellationToken.None);
        Assert.AreEqual(TimeSpan.FromSeconds(300), none.Timeout);

        foreach (var spent in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(-5) })
        {
            var outcome = await runner.RunAsync(Checkout, ["a.sln", "b.sln"], spent, CancellationToken.None);
            Assert.AreEqual(0, outcome.SolutionsAttempted, $"no time left ({spent}): nothing is started");
            Assert.IsTrue(outcome.TimedOut);
            Assert.AreEqual(TimeSpan.Zero, outcome.Timeout);
            StringAssert.StartsWith(outcome.Notes().Single(), "Package restore did not finish within 0s");
        }
    }

    [TestMethod]
    public void Runner_Arguments_AreBestEffort_AndLeaveNoServerBehind()
    {
        CollectionAssert.AreEqual(
            new[] { "restore", "All.slnx", "-p:DesignTimeBuild=true", "--ignore-failed-sources", "--disable-build-servers", "-nodeReuse:false", "-nologo" },
            PackageRestoreRunner.Arguments("All.slnx").ToArray());
        Assert.AreEqual(PackageRestoreRunner.DefaultTimeout, new PackageRestoreRunner(timeout: TimeSpan.Zero).Timeout);
        Assert.AreEqual(TimeSpan.FromSeconds(7), new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(7)).Timeout);
        Assert.AreEqual(PackageRestoreRunner.MaxTimeout, new PackageRestoreRunner(timeout: TimeSpan.FromDays(400)).Timeout,
            "a huge configured bound is clamped, so arming the deadline can never throw after the child started");
    }

    [TestMethod]
    public void Runner_ChildEnvironment_CarriesNoServiceSetting()
    {
        const string secret = "SEXTANT_SERVICE_CONTROL_TOKEN";
        const string lower = "sextant_peer_query_token";
        var before = (Environment.GetEnvironmentVariable(secret), Environment.GetEnvironmentVariable(lower));
        Environment.SetEnvironmentVariable(secret, "control-token-value");
        Environment.SetEnvironmentVariable(lower, "peer-token-value");
        try
        {
            var startInfo = new PackageRestoreRunner().CreateStartInfo(Path.Combine(Checkout, "All.slnx"));

            Assert.IsFalse(startInfo.Environment.Keys.Any(k => k.StartsWith("SEXTANT_", StringComparison.OrdinalIgnoreCase)),
                string.Join(", ", startInfo.Environment.Keys.Where(k => k.Contains("SEXTANT", StringComparison.OrdinalIgnoreCase))));
            Assert.IsFalse(startInfo.Environment.ContainsKey("MSBUILD_EXE_PATH"));
            Assert.AreEqual("1", startInfo.Environment["MSBUILDDISABLENODEREUSE"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secret, before.Item1);
            Environment.SetEnvironmentVariable(lower, before.Item2);
        }
    }

    [TestMethod]
    public async Task Runner_RestoresARealSolution_WritingTheAssetsFile_AndReportsAMissingPackage()
    {
        var root = Path.Combine(Path.GetTempPath(), "sextant-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // A local-only package source, so the test needs no network: one project restores cleanly, the
            // other asks for a package no source has.
            var feed = Path.Combine(root, "feed");
            Directory.CreateDirectory(feed);
            File.WriteAllText(Path.Combine(root, "nuget.config"), $"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="local" value="{feed}" />
                  </packageSources>
                </configuration>
                """);
            var tfm = $"net{Environment.Version.Major}.0";
            Directory.CreateDirectory(Path.Combine(root, "Good"));
            File.WriteAllText(Path.Combine(root, "Good", "Good.csproj"),
                $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>{tfm}</TargetFramework></PropertyGroup></Project>");
            Directory.CreateDirectory(Path.Combine(root, "Bad"));
            File.WriteAllText(Path.Combine(root, "Bad", "Bad.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>{tfm}</TargetFramework></PropertyGroup>
                  <ItemGroup><PackageReference Include="Sextant.Test.Does.Not.Exist" Version="1.0.0" /></ItemGroup>
                </Project>
                """);
            var solution = Path.Combine(root, "All.slnx");
            File.WriteAllText(solution, """
                <Solution>
                  <Project Path="Good/Good.csproj" />
                  <Project Path="Bad/Bad.csproj" />
                </Solution>
                """);

            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromMinutes(4))
                .RunAsync(root, [solution], limit: null, CancellationToken.None);

            Assert.AreEqual(1, outcome.SolutionsAttempted);
            Assert.IsFalse(outcome.TimedOut);
            Assert.AreEqual(0, outcome.SolutionsNotStarted);
            Assert.IsTrue(File.Exists(Path.Combine(root, "Good", "obj", "project.assets.json")),
                "the clean project is restored even though its sibling is not");
            var bad = outcome.Projects.Single();
            Assert.AreEqual("Bad/Bad.csproj", bad.Project);
            CollectionAssert.Contains(bad.MissingPackages.ToList(), "Sextant.Test.Does.Not.Exist");
            StringAssert.Contains(outcome.Notes().Single(), "Sextant.Test.Does.Not.Exist");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // ==== identity ====================================================================================

    [TestMethod]
    public void RestoreIdentity_IsNullByDefault_OffWhenDisabled_AndTheRequestMatchesTheWorker()
    {
        var options = ServiceTestFixtures.NewOptions(ServiceTestFixtures.NewDbPath());
        Assert.IsTrue(options.PackageRestore);
        Assert.IsNull(options.RestoreIdentityComponent, "the default keeps identities byte-identical");
        Assert.AreEqual("off", (options with { PackageRestore = false }).RestoreIdentityComponent);
        Assert.IsNull(new PackageRestoreRunner().IdentityComponent);
        Assert.AreEqual("off", new PackageRestoreRunner(enabled: false).IdentityComponent);

        var request = ServiceTestFixtures.Request();
        var context = LocalIndexerSnapshotWorker.CreateSnapshotContext(request, capability: null, restorePolicy: "off");
        Assert.AreEqual("off", context.RestorePolicy, "the orchestrator publishes under it");
        Assert.IsNull(LocalIndexerSnapshotWorker.CreateSnapshotContext(request, capability: null).RestorePolicy);

        Assert.AreEqual(request.ToIdentity("cfg", "cap").Hash, request.ToIdentity("cfg", "cap", restorePolicy: null).Hash);
        Assert.AreNotEqual(request.ToIdentity("cfg", "cap").Hash, request.ToIdentity("cfg", "cap", restorePolicy: "off").Hash);
        Assert.AreNotEqual(request.ToIdentity("cfg", "cap", sdkPinPolicy: "off").Hash,
            request.ToIdentity("cfg", "cap", restorePolicy: "off").Hash, "the two policies never collide");
    }

    [TestMethod]
    public void RestoreToggle_DefaultsOn_ParsesTheTimeout_AndFailsClosedOnAMalformedValue()
    {
        const string toggle = "SEXTANT_SERVICE_PACKAGE_RESTORE";
        const string timeout = "SEXTANT_SERVICE_PACKAGE_RESTORE_TIMEOUT_SECONDS";
        var config = new SextantConfiguration { DbPath = ServiceTestFixtures.NewDbPath() };
        Environment.SetEnvironmentVariable(toggle, null);
        Environment.SetEnvironmentVariable(timeout, null);
        try
        {
            var defaults = ServiceOptions.FromEnvironment(config);
            Assert.IsTrue(defaults.PackageRestore);
            Assert.AreEqual(PackageRestoreRunner.DefaultTimeout, defaults.PackageRestoreTimeout);

            Environment.SetEnvironmentVariable(toggle, "false");
            Environment.SetEnvironmentVariable(timeout, "42");
            var set = ServiceOptions.FromEnvironment(config);
            Assert.IsFalse(set.PackageRestore);
            Assert.AreEqual(TimeSpan.FromSeconds(42), set.PackageRestoreTimeout);

            Environment.SetEnvironmentVariable(toggle, "nope");
            Assert.ThrowsExactly<InvalidOperationException>(() => ServiceOptions.FromEnvironment(config));
        }
        finally
        {
            Environment.SetEnvironmentVariable(toggle, null);
            Environment.SetEnvironmentVariable(timeout, null);
        }
    }
}
