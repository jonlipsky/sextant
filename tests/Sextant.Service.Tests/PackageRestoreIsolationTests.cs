using Sextant.Indexer;
using Sextant.Service.Restore;

namespace Sextant.Service.Tests;

/// <summary>
/// Issue #296: a restore warning must not fail an index restore, and one project's restore-graph error must not leave
/// the rest of its solution unrestored. The unit tests drive the runner through a scripted <c>dotnet</c> host; the
/// integration tests restore real projects.
/// </summary>
[TestClass]
public class PackageRestoreIsolationTests
{
    private static readonly string Tfm = $"net{Environment.Version.Major}.0";

    // ==== scripted dotnet host ==========================================================================

    private sealed class FakeDotnet
    {
        public required string Path { get; init; }
        public required string CallLog { get; init; }
        public required string TraversalCopy { get; init; }

        public IReadOnlyList<string> Calls => File.Exists(CallLog) ? File.ReadAllLines(CallLog) : [];
        public string Traversal => File.Exists(TraversalCopy) ? File.ReadAllText(TraversalCopy) : string.Empty;
    }

    // restoreBody / msbuildBody are shell snippets for the "restore" and "msbuild" verbs.
    private static FakeDotnet CreateFakeDotnet(string dir, string restoreBody, string msbuildBody)
    {
        Directory.CreateDirectory(dir);
        var script = System.IO.Path.Combine(dir, "dotnet");
        var callLog = System.IO.Path.Combine(dir, "calls.txt");
        var copy = System.IO.Path.Combine(dir, "traversal.proj");
        File.WriteAllText(script, $"""
            #!/bin/sh
            echo "$@" >> "{callLog}"
            case "$1" in
              restore) {restoreBody} ;;
              msbuild) cp "$2" "{copy}"; {msbuildBody} ;;
            esac
            """.Replace("\r\n", "\n"));
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new FakeDotnet { Path = script, CallLog = callLog, TraversalCopy = copy };
    }

    private static string NewRoot(string name)
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sextant-restore-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string WriteSolution(string root, string name, params string[] projectNames)
    {
        foreach (var project in projectNames)
        {
            Directory.CreateDirectory(System.IO.Path.Combine(root, project));
            File.WriteAllText(System.IO.Path.Combine(root, project, project + ".csproj"),
                $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>{Tfm}</TargetFramework></PropertyGroup></Project>");
        }
        var solution = System.IO.Path.Combine(root, name);
        File.WriteAllText(solution,
            "<Solution>" + string.Join(string.Empty, projectNames.Select(p => $"<Project Path=\"{p}/{p}.csproj\" />")) + "</Solution>");
        return solution;
    }

    private static void RequireUnix()
    {
        if (OperatingSystem.IsWindows())
            Assert.Inconclusive("The scripted dotnet host is a POSIX shell script.");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ==== unit: properties ==============================================================================

    [TestMethod]
    public void Parser_AttributesAnErrorWithoutAProjectPrefix_ToItsTrailingProject()
    {
        var checkout = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sextant-restore-trailing");
        var parser = new RestoreOutputParser(checkout);
        var broken = System.IO.Path.Combine(checkout, "Broken", "Broken.csproj");
        parser.Accept($"/sdk/Microsoft.NET.TargetFrameworkInference.targets(109,5): error NETSDK1013: The TargetFramework value 'bogus' was not recognized. [{broken}]");
        parser.Accept($"/sdk/NuGet.targets(198,5): error MSB4181: The task returned false. [{System.IO.Path.Combine(checkout, "All.slnx")}]");

        Assert.AreEqual("Broken/Broken.csproj", parser.Projects().Single().Project);
        CollectionAssert.AreEqual(new[] { "MSB4181" }, parser.GeneralCodes().ToArray(), "a solution-scoped error stays general");
    }

    [TestMethod]
    public void RestoreOnlyProperties_DisableWarningPromotion_AndAreNotLoadProperties()
    {
        CollectionAssert.AreEqual(
            new[] { "TreatWarningsAsErrors=false", "WarningsAsErrors=" },
            PackageRestoreRunner.RestoreOnlyProperties.Select(p => $"{p.Key}={p.Value}").ToArray());
        CollectionAssert.AreEqual(
            new[] { "restore", "All.slnx", "-p:DesignTimeBuild=true", "-p:TreatWarningsAsErrors=false", "-p:WarningsAsErrors=",
                "--ignore-failed-sources", "--disable-build-servers", "-nodeReuse:false", "-nologo" },
            PackageRestoreRunner.Arguments("All.slnx", PackageRestoreRunner.RestoreOnlyProperties).ToArray());
        Assert.IsFalse(
            Sextant.Indexer.EvaluationGlobalProperties.For([]).Any(p => p.Key is "TreatWarningsAsErrors" or "WarningsAsErrors"),
            "the workspace load keeps its own evaluation: the warning properties are restore-only");
    }

    [TestMethod]
    public async Task PerSolutionRestore_PassesTheWarningProperties()
    {
        RequireUnix();
        var root = NewRoot("props-solution");
        try
        {
            var fake = CreateFakeDotnet(System.IO.Path.Combine(root, "fake"), "exit 0", "exit 0");
            var solution = WriteSolution(root, "All.slnx", "A");

            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(60)) { DotnetPath = fake.Path }
                .RunAsync(root, [solution], limit: null, CancellationToken.None, System.IO.Path.Combine(root, "scratch"));

            Assert.IsTrue(outcome.Clean);
            var call = fake.Calls.Single();
            StringAssert.Contains(call, "-p:TreatWarningsAsErrors=false");
            StringAssert.Contains(call, "-p:WarningsAsErrors=");
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public async Task UnionRestore_PassesTheWarningProperties_ToEveryProject()
    {
        RequireUnix();
        var root = NewRoot("props-union");
        try
        {
            var fake = CreateFakeDotnet(System.IO.Path.Combine(root, "fake"), "exit 0", "exit 0");
            var first = WriteSolution(System.IO.Path.Combine(Directory.CreateDirectory(System.IO.Path.Combine(root, "one")).FullName), "One.slnx", "A");
            var second = WriteSolution(System.IO.Path.Combine(Directory.CreateDirectory(System.IO.Path.Combine(root, "two")).FullName), "Two.slnx", "B");

            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(60)) { DotnetPath = fake.Path }
                .RunAsync(root, [first, second], limit: null, CancellationToken.None, System.IO.Path.Combine(root, "scratch"));

            Assert.IsTrue(outcome.UsedProjectUnion);
            var traversal = fake.Traversal;
            Assert.AreEqual(2, Count(traversal, "TreatWarningsAsErrors=false;WarningsAsErrors="), traversal);
            StringAssert.Contains(traversal, "RestoreRecursive=false", "the union stays non-recursive");
        }
        finally { TryDelete(root); }
    }

    // ==== unit: per-project retry ========================================================================

    [TestMethod]
    public async Task AFailingPerSolutionRestore_IsRetriedProjectByProject_AndRecordsIt()
    {
        RequireUnix();
        var root = NewRoot("retry");
        try
        {
            var fake = CreateFakeDotnet(System.IO.Path.Combine(root, "fake"), "exit 1", "exit 0");
            var solution = WriteSolution(root, "All.slnx", "A", "B");
            var logs = new List<string>();

            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(60), log: logs.Add) { DotnetPath = fake.Path }
                .RunAsync(root, [solution], limit: null, CancellationToken.None, System.IO.Path.Combine(root, "scratch"));

            CollectionAssert.AreEqual(new[] { "restore", "msbuild" }, fake.Calls.Select(c => c.Split(' ')[0]).ToArray());
            Assert.AreEqual(1, outcome.SolutionsRetriedPerProject);
            Assert.AreEqual(2, outcome.ProjectsRetried);
            Assert.AreEqual(1, outcome.SolutionsSucceeded, "every project restored on its own");
            Assert.IsFalse(outcome.UsedProjectUnion, "this is the per-solution path, not the multi-solution union");
            Assert.IsTrue(outcome.Clean);
            StringAssert.Contains(outcome.Notes().Single(), "retried for their 2 project(s) one by one");
            Assert.IsTrue(logs.Any(l => l.Contains("retrying its 2 project(s) one by one", StringComparison.Ordinal)));
            Assert.IsTrue(logs.Any(l => l.Contains("1 solution(s) retried per project (2 project(s))", StringComparison.Ordinal)));

            var traversal = fake.Traversal;
            Assert.AreEqual(2, Count(traversal, "ContinueOnError=\"ErrorAndContinue\""));
            StringAssert.Contains(traversal, "TreatWarningsAsErrors=false;WarningsAsErrors=");
            Assert.IsFalse(traversal.Contains("RestoreRecursive=false", StringComparison.Ordinal),
                "a retried project restores its references too, including ones outside the solution");
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public async Task APerProjectRetry_SeesTheSamePackageFolderAndCredentials_AsTheFirstAttempt()
    {
        RequireUnix();
        var root = NewRoot("retry-env");
        var previous = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        try
        {
            var packages = System.IO.Path.Combine(root, "packages", "repo-a");
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", packages);
            var envLog = System.IO.Path.Combine(root, "env.txt");
            var record = $"echo \"$NUGET_PACKAGES|$NUGET_PLUGIN_PATHS|$RESTORE_FEED_CREDENTIALS_FILE\" >> \"{envLog}\"";
            var fake = CreateFakeDotnet(System.IO.Path.Combine(root, "fake"), $"{record}; exit 1", $"{record}; exit 0");
            var plugin = System.IO.Path.Combine(root, "plugin.dll");
            File.WriteAllText(plugin, "x");
            var solution = WriteSolution(root, "All.slnx", "A", "B");
            var credentials = new[] { new PackageSourceCredential("feed.example.test", "user", "not-a-real-secret") };

            var outcome = await new PackageRestoreRunner(
                    timeout: TimeSpan.FromSeconds(60), sourceCredentials: credentials, credentialPluginPath: plugin)
                { DotnetPath = fake.Path }
                .RunAsync(root, [solution], limit: null, CancellationToken.None, System.IO.Path.Combine(root, "scratch"));

            Assert.AreEqual(1, outcome.SolutionsRetriedPerProject);
            var envs = File.ReadAllLines(envLog);
            Assert.AreEqual(2, envs.Length, "one restore attempt and one per-project retry");
            Assert.AreEqual(envs[0], envs[1], "the retry child inherits exactly the first attempt's environment");
            var parts = envs[0].Split('|');
            Assert.AreEqual(packages, parts[0]);
            Assert.AreEqual(plugin, parts[1]);
            Assert.IsFalse(string.IsNullOrEmpty(parts[2]));
            CollectionAssert.AreEqual(new[] { "restore", "msbuild" }, fake.Calls.Select(c => c.Split(' ')[0]).ToArray());
            StringAssert.Contains(fake.Calls[0], "-p:TreatWarningsAsErrors=false");
            StringAssert.Contains(fake.Traversal, "TreatWarningsAsErrors=false;WarningsAsErrors=");
        }
        finally
        {
            Environment.SetEnvironmentVariable("NUGET_PACKAGES", previous);
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task ARetryThatStillFails_ReportsTheSolutionFailed_WithTheRetrysProjectErrors()
    {
        RequireUnix();
        var root = NewRoot("retry-fails");
        try
        {
            var bad = System.IO.Path.Combine(root, "B", "B.csproj");
            var fake = CreateFakeDotnet(
                System.IO.Path.Combine(root, "fake"),
                $"echo '{bad} : error NU1105: Invalid target framework x.' ; exit 1",
                $"echo '{bad} : error NU1105: Invalid target framework x.' ; exit 1");
            var solution = WriteSolution(root, "All.slnx", "A", "B");

            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(60)) { DotnetPath = fake.Path }
                .RunAsync(root, [solution], limit: null, CancellationToken.None, System.IO.Path.Combine(root, "scratch"));

            Assert.AreEqual(1, outcome.SolutionsRetriedPerProject);
            Assert.AreEqual(0, outcome.SolutionsSucceeded);
            Assert.AreEqual(1, outcome.SolutionsFailed);
            Assert.IsFalse(outcome.Clean);
            Assert.AreEqual("B/B.csproj", outcome.Projects.Single().Project);
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public async Task ATimedOutPerSolutionRestore_IsNotRetried()
    {
        RequireUnix();
        var root = NewRoot("retry-timeout");
        try
        {
            var fake = CreateFakeDotnet(System.IO.Path.Combine(root, "fake"), "exec sleep 60", "exit 0");
            var solution = WriteSolution(root, "All.slnx", "A");

            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(1)) { DotnetPath = fake.Path }
                .RunAsync(root, [solution], limit: null, CancellationToken.None, System.IO.Path.Combine(root, "scratch"));

            Assert.IsTrue(outcome.TimedOut);
            Assert.AreEqual(0, outcome.SolutionsRetriedPerProject);
            CollectionAssert.AreEqual(new[] { "restore" }, fake.Calls.Select(c => c.Split(' ')[0]).ToArray());
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public async Task ACancelledPerSolutionRestore_IsNotRetried()
    {
        RequireUnix();
        var root = NewRoot("retry-cancel");
        try
        {
            var fake = CreateFakeDotnet(System.IO.Path.Combine(root, "fake"), "exec sleep 60", "exit 0");
            var solution = WriteSolution(root, "All.slnx", "A");
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(60)) { DotnetPath = fake.Path }
                    .RunAsync(root, [solution], limit: null, cts.Token, System.IO.Path.Combine(root, "scratch")));

            CollectionAssert.AreEqual(new[] { "restore" }, fake.Calls.Select(c => c.Split(' ')[0]).ToArray());
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public async Task AFailedRestoreThatCannotBeRetried_KeepsItsOwnFindings()
    {
        RequireUnix();
        var root = NewRoot("retry-impossible");
        try
        {
            var a = System.IO.Path.Combine(root, "A", "A.csproj");
            var fake = CreateFakeDotnet(
                System.IO.Path.Combine(root, "fake"), $"echo '{a} : error NU1101: Unable to find package Acme.X. No packages exist' ; exit 1", "exit 0");
            var solution = WriteSolution(root, "All.slnx", "A");
            // The scratch location is a file, so the traversal cannot be staged.
            var scratch = System.IO.Path.Combine(root, "scratch-file");
            File.WriteAllText(scratch, string.Empty);

            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(60)) { DotnetPath = fake.Path }
                .RunAsync(root, [solution], limit: null, CancellationToken.None, scratch);

            Assert.AreEqual(0, outcome.SolutionsRetriedPerProject);
            Assert.AreEqual(1, outcome.SolutionsFailed);
            CollectionAssert.Contains(outcome.Projects.Single().MissingPackages.ToList(), "Acme.X");
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public async Task ACleanPerSolutionRestore_IsNeverRetried()
    {
        RequireUnix();
        var root = NewRoot("no-retry");
        try
        {
            var fake = CreateFakeDotnet(System.IO.Path.Combine(root, "fake"), "exit 0", "exit 0");
            var solution = WriteSolution(root, "All.slnx", "A");

            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(60)) { DotnetPath = fake.Path }
                .RunAsync(root, [solution], limit: null, CancellationToken.None, System.IO.Path.Combine(root, "scratch"));

            Assert.AreEqual(0, outcome.SolutionsRetriedPerProject);
            Assert.AreEqual(1, fake.Calls.Count);
            Assert.AreEqual(0, outcome.Notes().Count);
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public async Task AFailureThatLeftEveryProjectItsAssets_IsNotRetried_AndReportedAsBefore()
    {
        RequireUnix();
        var root = NewRoot("assets-present");
        try
        {
            var a = System.IO.Path.Combine(root, "A", "A.csproj");
            var fake = CreateFakeDotnet(
                System.IO.Path.Combine(root, "fake"), $"echo '{a} : error NU1101: Unable to find package Acme.X. No packages exist' ; exit 1", "exit 0");
            var solution = WriteSolution(root, "All.slnx", "A", "B");
            foreach (var project in new[] { "A", "B" })
            {
                Directory.CreateDirectory(System.IO.Path.Combine(root, project, "obj"));
                File.WriteAllText(System.IO.Path.Combine(root, project, "obj", "project.assets.json"), "{}");
            }

            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(60)) { DotnetPath = fake.Path }
                .RunAsync(root, [solution], limit: null, CancellationToken.None, System.IO.Path.Combine(root, "scratch"));

            CollectionAssert.AreEqual(new[] { "restore" }, fake.Calls.Select(c => c.Split(' ')[0]).ToArray());
            Assert.AreEqual(0, outcome.SolutionsRetriedPerProject);
            Assert.AreEqual(0, outcome.Notes().Count(n => n.Contains("one by one", StringComparison.Ordinal)));
            CollectionAssert.Contains(outcome.Projects.Single().MissingPackages.ToList(), "Acme.X");
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public async Task ARetryThatExceedsTheDeadline_TimesOut_KeepsTheFirstAttemptsFindings_AndRetriesNothingMore()
    {
        RequireUnix();
        var root = NewRoot("retry-deadline");
        try
        {
            var a = System.IO.Path.Combine(root, "A", "A.csproj");
            var fake = CreateFakeDotnet(
                System.IO.Path.Combine(root, "fake"),
                $"echo '{a} : error NU1101: Unable to find package Acme.X. No packages exist' ; exit 1", "exec sleep 60");
            var solution = WriteSolution(root, "All.slnx", "A");

            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(3)) { DotnetPath = fake.Path }
                .RunAsync(root, [solution], limit: null, CancellationToken.None, System.IO.Path.Combine(root, "scratch"));

            Assert.IsTrue(outcome.TimedOut);
            Assert.AreEqual(1, outcome.SolutionsRetriedPerProject, "the retry ran, then hit the deadline");
            CollectionAssert.AreEqual(new[] { "restore", "msbuild" }, fake.Calls.Select(c => c.Split(' ')[0]).ToArray());
            CollectionAssert.Contains(outcome.Projects.Single().MissingPackages.ToList(), "Acme.X");
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public async Task ARetryThatCouldNotStart_IsNotCounted_AndKeepsTheFirstAttemptsFindings()
    {
        RequireUnix();
        var root = NewRoot("retry-notstarted");
        try
        {
            var a = System.IO.Path.Combine(root, "A", "A.csproj");
            // The first call (restore) runs the script; the retry's msbuild verb is the same script, so make it unstartable
            // by removing its execute bit once restore has run.
            var dir = System.IO.Path.Combine(root, "fake");
            var fake = CreateFakeDotnet(dir,
                $"echo '{a} : error NU1101: Unable to find package Acme.X. No packages exist' ; exit 1", "exit 0");
            File.AppendAllText(fake.Path, string.Empty);
            File.WriteAllText(fake.Path, File.ReadAllText(fake.Path).Replace(
                "restore) ", $"restore) chmod 600 \"{fake.Path}\"; ", StringComparison.Ordinal));
            var solution = WriteSolution(root, "All.slnx", "A");

            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(60)) { DotnetPath = fake.Path }
                .RunAsync(root, [solution], limit: null, CancellationToken.None, System.IO.Path.Combine(root, "scratch"));

            Assert.AreEqual(0, outcome.SolutionsRetriedPerProject);
            Assert.AreEqual(0, outcome.ProjectsRetried);
            CollectionAssert.Contains(outcome.Projects.Single().MissingPackages.ToList(), "Acme.X");
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public async Task UnionRestore_AttributesAPrefixlessErrorToItsTrailingProject()
    {
        RequireUnix();
        var root = NewRoot("union-trailing");
        try
        {
            var bad = System.IO.Path.Combine(root, "two", "B", "B.csproj");
            var line = $"/sdk/Microsoft.NET.TargetFrameworkInference.targets(109,5): error NETSDK1013: The TargetFramework value 'bogus' was not recognized. [{bad}]";
            var fake = CreateFakeDotnet(System.IO.Path.Combine(root, "fake"), "exit 0", $"echo '{line}' ; exit 1");
            var first = WriteSolution(Directory.CreateDirectory(System.IO.Path.Combine(root, "one")).FullName, "One.slnx", "A");
            var second = WriteSolution(Directory.CreateDirectory(System.IO.Path.Combine(root, "two")).FullName, "Two.slnx", "B");

            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(60)) { DotnetPath = fake.Path }
                .RunAsync(root, [first, second], limit: null, CancellationToken.None, System.IO.Path.Combine(root, "scratch"));

            Assert.IsTrue(outcome.UsedProjectUnion);
            Assert.AreEqual("two/B/B.csproj", outcome.Projects.Single().Project);
            CollectionAssert.Contains(outcome.Projects.Single().Codes.ToList(), "NETSDK1013");
            Assert.AreEqual(0, outcome.GeneralCodes.Count, "a project-attributed error is not a general one");
        }
        finally { TryDelete(root); }
    }

    // ==== integration: real restores =====================================================================

    private static void WriteOfflineNuGetConfig(string root)
        => File.WriteAllText(System.IO.Path.Combine(root, "nuget.config"), """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration><packageSources><clear /></packageSources></configuration>
            """);

    // A local folder feed holding one empty package, so a PackageDownload of it restores offline.
    private static void WriteLocalFeed(string root, string id, string version)
    {
        var feed = System.IO.Path.Combine(root, "feed");
        Directory.CreateDirectory(feed);
        using (var zip = System.IO.Compression.ZipFile.Open(
                   System.IO.Path.Combine(feed, $"{id}.{version}.nupkg"), System.IO.Compression.ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry($"{id}.nuspec");
            using var writer = new StreamWriter(entry.Open());
            writer.Write($"""
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                  <metadata><id>{id}</id><version>{version}</version><authors>t</authors><description>t</description></metadata>
                </package>
                """);
        }
        File.WriteAllText(System.IO.Path.Combine(root, "nuget.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration><packageSources><clear /><add key="local" value="{feed}" /></packageSources></configuration>
            """);
    }

    private static void WritePromotedWarningsProps(string root)
        => File.WriteAllText(System.IO.Path.Combine(root, "Directory.Build.props"),
            "<Project><PropertyGroup><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup></Project>");

    [TestMethod]
    public async Task APromotedRestoreWarning_NoLongerFailsTheRestore_OfTheSolution()
    {
        // The issue's shape: TreatWarningsAsErrors from a Directory.Build.props turns NU1505 (duplicate PackageDownload)
        // into a graph-level error. Everything is restorable offline, so with the fix the solution restores cleanly
        // on the first attempt and the per-project retry never runs.
        var root = NewRoot("promoted-warning");
        try
        {
            WriteLocalFeed(root, "Sextant.Test.Dup", "1.0.0");
            WritePromotedWarningsProps(root);
            var solution = WriteSolution(root, "All.slnx", "Clean", "Noisy");
            File.WriteAllText(System.IO.Path.Combine(root, "Noisy", "Noisy.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>{Tfm}</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <PackageDownload Include="Sextant.Test.Dup" Version="[1.0.0]" />
                    <PackageDownload Include="Sextant.Test.Dup" Version="[1.0.0]" />
                  </ItemGroup>
                </Project>
                """);

            var logs = new List<string>();
            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromMinutes(4), log: logs.Add)
                .RunAsync(root, [solution], limit: null, CancellationToken.None, System.IO.Path.Combine(root, "scratch"));

            Assert.AreEqual(0, outcome.SolutionsRetriedPerProject, string.Join("\n", logs));
            Assert.IsTrue(outcome.Clean, string.Join("\n", logs));
            Assert.IsTrue(File.Exists(System.IO.Path.Combine(root, "Clean", "obj", "project.assets.json")));
            Assert.IsTrue(File.Exists(System.IO.Path.Combine(root, "Noisy", "obj", "project.assets.json")));
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public async Task ARestoreGraphError_InOneProject_LeavesTheOtherProjectsRestored_AndReportsTheBadOne()
    {
        // NU1105 (an invalid target framework) fails the whole `dotnet restore <solution>` while NuGet reads the
        // projects, so no project of the solution gets an assets file; the per-project retry restores the clean one.
        var root = NewRoot("graph-error");
        try
        {
            WriteOfflineNuGetConfig(root);
            var solution = WriteSolution(root, "All.slnx", "Clean", "Broken");
            File.WriteAllText(System.IO.Path.Combine(root, "Broken", "Broken.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>bogus</TargetFramework></PropertyGroup></Project>");

            var logs = new List<string>();
            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromMinutes(4), log: logs.Add)
                .RunAsync(root, [solution], limit: null, CancellationToken.None, System.IO.Path.Combine(root, "scratch"));

            Assert.AreEqual(1, outcome.SolutionsRetriedPerProject, string.Join("\n", logs));
            Assert.IsFalse(outcome.TimedOut);
            Assert.IsTrue(File.Exists(System.IO.Path.Combine(root, "Clean", "obj", "project.assets.json")),
                "the clean project is restored even though its sibling's restore failed the solution");
            Assert.IsFalse(File.Exists(System.IO.Path.Combine(root, "Broken", "obj", "project.assets.json")));
            Assert.IsFalse(outcome.Clean);
            var broken = outcome.Projects.Single();
            Assert.AreEqual("Broken/Broken.csproj", broken.Project);
            Assert.IsTrue(broken.Codes.Count > 0, "the retry attributes the evaluation error to its project");
            StringAssert.Contains(string.Join("\n", outcome.Notes()), "retried for their 2 project(s) one by one");
            Assert.IsTrue(outcome.ProjectLoadIssues().ContainsKey("Broken/Broken.csproj"),
                "the bad project is reported as a per-project load issue (package_restore_incomplete)");
        }
        finally { TryDelete(root); }
    }

    // ==== issue #290: redundant per-solution restores ====================================================

    // Rewrites project "A" so it references a project outside every selected solution, which forces the per-solution
    // fallback (the union cannot close its reference graph).
    private static void ForcePerSolutionFallback(string root, string extraProperty = "")
    {
        Directory.CreateDirectory(System.IO.Path.Combine(root, "Outside"));
        File.WriteAllText(System.IO.Path.Combine(root, "Outside", "Outside.csproj"),
            $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>{Tfm}</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(System.IO.Path.Combine(root, "A", "A.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>{Tfm}</TargetFramework>{extraProperty}</PropertyGroup>
              <ItemGroup><ProjectReference Include="../Outside/Outside.csproj" /></ItemGroup>
            </Project>
            """);
    }

    // A restore that writes an assets file for every project directory next to the solution.
    private const string WriteAssets =
        "d=$(dirname \"$2\"); for p in \"$d\"/*/; do mkdir -p \"$p/obj\"; echo '{}' > \"$p/obj/project.assets.json\"; done; exit 0";

    private static IEnumerable<string> RestoredSolutions(FakeDotnet fake) =>
        fake.Calls.Where(c => c.StartsWith("restore ", StringComparison.Ordinal))
            .Select(c => System.IO.Path.GetFileName(c.Split(' ')[1]));

    [TestMethod]
    public async Task ASubsetSolution_AfterItsSuperset_IsSkipped_AndRecorded()
    {
        RequireUnix();
        var root = NewRoot("skip-subset");
        try
        {
            var fake = CreateFakeDotnet(System.IO.Path.Combine(root, "fake"), WriteAssets, "exit 0");
            var small = WriteSolution(root, "Small.slnx", "A", "B");
            var big = WriteSolution(root, "Big.slnx", "A", "B", "C");
            ForcePerSolutionFallback(root);
            var logs = new List<string>();

            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(60), log: logs.Add) { DotnetPath = fake.Path }
                .RunAsync(root, [big, small], limit: null, CancellationToken.None, System.IO.Path.Combine(root, "scratch"));

            Assert.IsFalse(outcome.UsedProjectUnion);
            CollectionAssert.AreEqual(new[] { "Big.slnx" }, RestoredSolutions(fake).ToArray(), "the superset restores first in the input order; the subset is skipped");
            Assert.AreEqual(2, outcome.SolutionsSelected);
            Assert.AreEqual(1, outcome.SolutionsAttempted);
            Assert.AreEqual(1, outcome.SolutionsSkippedRedundant);
            CollectionAssert.AreEqual(new[] { "Small.slnx" }, outcome.SkippedSolutionNames.ToArray());
            Assert.IsTrue(outcome.Clean);
            Assert.IsTrue(logs.Any(l => l.Contains("1 solution(s) skipped as already restored (Small.slnx)", StringComparison.Ordinal)), string.Join("\n", logs));

            var recorder = new JobTimingsRecorder();
            recorder.RecordRestore(outcome, persistentPackageFolder: false);
            var restore = recorder.Build().Restore!;
            Assert.AreEqual(1, restore.SolutionsSkippedRedundant);
            CollectionAssert.AreEqual(new[] { "Small.slnx" }, restore.SkippedSolutionNames.ToArray());
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public async Task ASubset_SharingAProjectWithItsOwnNestedGlobalJson_InTheSameDirectory_IsSkipped()
    {
        RequireUnix();
        var root = NewRoot("skip-nested-globaljson");
        try
        {
            var script = "d=$(dirname \"$2\"); for p in \"$d\"/*/ \"$d\"/sub/D/; do mkdir -p \"$p/obj\"; echo '{}' > \"$p/obj/project.assets.json\"; done; exit 0";
            var fake = CreateFakeDotnet(System.IO.Path.Combine(root, "fake"), script, "exit 0");
            WriteSolution(root, "Seed.slnx", "A", "C");
            Directory.CreateDirectory(System.IO.Path.Combine(root, "sub", "D"));
            File.WriteAllText(System.IO.Path.Combine(root, "sub", "D", "D.csproj"),
                $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>{Tfm}</TargetFramework></PropertyGroup></Project>");
            var small = WriteSolutionRefs(root, "Small.slnx", "A/A.csproj", "sub/D/D.csproj");
            var big = WriteSolutionRefs(root, "Big.slnx", "A/A.csproj", "sub/D/D.csproj", "C/C.csproj");
            ForcePerSolutionFallback(root);
            File.WriteAllText(System.IO.Path.Combine(root, "global.json"), "{\"sdk\":{\"version\":\"9.0.100\",\"rollForward\":\"latestMajor\"}}");
            File.WriteAllText(System.IO.Path.Combine(root, "sub", "D", "global.json"), "{\"sdk\":{\"version\":\"8.0.100\",\"rollForward\":\"latestMajor\"}}");

            var logs = new List<string>();
            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(60), log: logs.Add) { DotnetPath = fake.Path }
                .RunAsync(root, [big, small], limit: null, CancellationToken.None, System.IO.Path.Combine(root, "scratch"));

            CollectionAssert.AreEqual(new[] { "Big.slnx" }, RestoredSolutions(fake).ToArray(), string.Join("\n", logs));
            CollectionAssert.AreEqual(new[] { "Small.slnx" }, outcome.SkippedSolutionNames.ToArray());
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public void SameDirectorySensitivity_IgnoresToolsetImports_ButNotSolutionDir()
    {
        var root = NewRoot("sensitivity-toolset");
        try
        {
            var a = WriteSolution(root, "A.slnx", "P", "Q");
            var b = WriteSolution(root, "B.slnx", "P");
            File.WriteAllText(System.IO.Path.Combine(root, "P", "P.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><Import Project=\"$(MSBuildExtensionsPath)\\Xamarin\\Mac\\X.targets\" /></Project>");
            File.WriteAllText(System.IO.Path.Combine(root, "Q", "Q.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><Import Project=\"$(Other)\\X.targets\" /></Project>");
            WriteSolution(root, "Seed.slnx", "R");
            File.WriteAllText(System.IO.Path.Combine(root, "Directory.Build.props"), "<Project />");
            File.WriteAllText(System.IO.Path.Combine(root, "R", "Directory.Build.props"),
                "<Project><PropertyGroup><Parent>$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))</Parent></PropertyGroup>"
                + "<Import Project=\"$(Parent)\" Condition=\"'$(Parent)' != ''\" /></Project>");
            var sensitive = SolutionSensitivity.Find([a, b], [FullProject(root, "P"), FullProject(root, "Q"), FullProject(root, "R")], sameDirectory: true);
            Assert.IsFalse(sensitive.Contains(FullProject(root, "R")), "an import through a GetPathOfFileAbove property resolves");
            Assert.IsFalse(sensitive.Contains(FullProject(root, "P")));
            Assert.IsTrue(sensitive.Contains(FullProject(root, "Q")), "an import that cannot be resolved stays sensitive");
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public async Task ASubset_BeforeItsSuperset_InInputOrder_IsNotSkipped_AndNothingIsReordered()
    {
        RequireUnix();
        var root = NewRoot("order-subset-first");
        try
        {
            var fake = CreateFakeDotnet(System.IO.Path.Combine(root, "fake"), WriteAssets, "exit 0");
            var small = WriteSolution(root, "Small.slnx", "A", "B");
            var big = WriteSolution(root, "Big.slnx", "A", "B", "C", "D");
            ForcePerSolutionFallback(root);

            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(60)) { DotnetPath = fake.Path }
                .RunAsync(root, [small, big], limit: null, CancellationToken.None, System.IO.Path.Combine(root, "scratch"));

            CollectionAssert.AreEqual(new[] { "Small.slnx", "Big.slnx" }, RestoredSolutions(fake).ToArray());
            Assert.AreEqual(0, outcome.SolutionsSkippedRedundant);
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public async Task ASubset_IsNotSkipped_WhenASolutionOfAnotherDirectoryRestoredASharedProjectSince()
    {
        RequireUnix();
        var root = NewRoot("order-last-writer");
        try
        {
            var one = System.IO.Path.Combine(root, "one");
            var fake = CreateFakeDotnet(System.IO.Path.Combine(root, "fake"),
                $"for p in \"{one}\"/*/; do mkdir -p \"$p/obj\"; echo '{{}}' > \"$p/obj/project.assets.json\"; done; exit 0", "exit 0");
            var big = WriteSolution(one, "Big.slnx", "A", "B", "C");
            var other = WriteSolutionRefs(System.IO.Path.Combine(root, "two"), "Other.slnx", "../one/A/A.csproj");
            var small = WriteSolution(one, "Small.slnx", "A", "B");
            ForcePerSolutionFallback(one);

            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(60)) { DotnetPath = fake.Path }
                .RunAsync(root, [big, other, small], limit: null, CancellationToken.None, System.IO.Path.Combine(root, "scratch"));

            CollectionAssert.AreEqual(new[] { "Big.slnx", "Other.slnx", "Small.slnx" }, RestoredSolutions(fake).ToArray(),
                "A was last attempted by the other directory, so Small restores it again");
            Assert.AreEqual(0, outcome.SolutionsSkippedRedundant);
        }
        finally { TryDelete(root); }
    }


    [TestMethod]
    public async Task ASubsetWhoseProjectReadsSolutionDir_IsNotSkipped()
    {
        RequireUnix();
        var root = NewRoot("skip-solutiondir");
        try
        {
            var fake = CreateFakeDotnet(System.IO.Path.Combine(root, "fake"), WriteAssets, "exit 0");
            var small = WriteSolution(root, "Small.slnx", "A");
            var big = WriteSolution(root, "Big.slnx", "A", "B");
            ForcePerSolutionFallback(root, "<Marker>$(SolutionDir)</Marker>");

            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(60)) { DotnetPath = fake.Path }
                .RunAsync(root, [big, small], limit: null, CancellationToken.None, System.IO.Path.Combine(root, "scratch"));

            CollectionAssert.AreEqual(new[] { "Big.slnx", "Small.slnx" }, RestoredSolutions(fake).ToArray());
            Assert.AreEqual(0, outcome.SolutionsSkippedRedundant);
            Assert.AreEqual(0, outcome.SkippedSolutionNames.Count);
        }
        finally { TryDelete(root); }
    }

    private static string SlnxWith(string root, string name, string topLevel, string projectChildren, params string[] projects)
    {
        var path = System.IO.Path.Combine(root, name);
        File.WriteAllText(path, "<Solution>" + topLevel + string.Join(string.Empty,
            projects.Select(p => $"<Project Path=\"{p}/{p}.csproj\">{projectChildren}</Project>")) + "</Solution>");
        return path;
    }

    private static string SlnWith(string root, string name, string mapping, params string[] projects)
    {
        var guids = projects.Select((p, i) => (Name: p, Guid: "{00000000-0000-0000-0000-00000000000" + (i + 1) + "}")).ToList();
        var text = "Microsoft Visual Studio Solution File, Format Version 12.00\n"
            + string.Join(string.Empty, guids.Select(g =>
                $"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"{g.Name}\", \"{g.Name}\\{g.Name}.csproj\", \"{g.Guid}\"\nEndProject\n"))
            + "Global\n\tGlobalSection(SolutionConfigurationPlatforms) = preSolution\n\t\tDebug|Any CPU = Debug|Any CPU\n\t\tRelease|Any CPU = Release|Any CPU\n\tEndGlobalSection\n"
            + "\tGlobalSection(ProjectConfigurationPlatforms) = postSolution\n"
            + string.Join(string.Empty, guids.Select(g =>
                $"\t\t{g.Guid}.Debug|Any CPU.ActiveCfg = {mapping}\n\t\t{g.Guid}.Debug|Any CPU.Build.0 = {mapping}\n"
                + $"\t\t{g.Guid}.Release|Any CPU.ActiveCfg = Release|Any CPU\n"))
            + "\tEndGlobalSection\nEndGlobal\n";
        var path = System.IO.Path.Combine(root, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static string FullProject(string root, string name) => System.IO.Path.GetFullPath(System.IO.Path.Combine(root, name, name + ".csproj"));

    [TestMethod]
    public void ConfigurationMapping_SlnxWithDifferentPlatform_MakesTheSharedProjectSensitive()
    {
        var root = NewRoot("map-slnx");
        try
        {
            WriteSolution(root, "Seed.slnx", "A", "B");
            var big = SlnxWith(root, "Big.slnx", string.Empty, string.Empty, "A", "B");
            var small = SlnxWith(root, "Small.slnx", string.Empty, "<Platform Solution=\"*|*\" Project=\"x64\" />", "A");
            var sensitive = SolutionSensitivity.Find([big, small], [FullProject(root, "A"), FullProject(root, "B")]);
            Assert.IsTrue(sensitive.Contains(FullProject(root, "A")));
            Assert.IsFalse(sensitive.Contains(FullProject(root, "B")));
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public void ConfigurationMapping_SlnxWithDifferentConfigurations_OrIdenticalDefaults()
    {
        var root = NewRoot("map-slnx2");
        try
        {
            WriteSolution(root, "Seed.slnx", "A");
            var projects = new[] { FullProject(root, "A") };
            var plain = SlnxWith(root, "Plain.slnx", string.Empty, string.Empty, "A");
            var plain2 = SlnxWith(root, "Plain2.slnx", string.Empty, string.Empty, "A");
            var configured = SlnxWith(root, "Configured.slnx", "<Configurations><BuildType Name=\"Release\" /></Configurations>", string.Empty, "A");
            var mapped1 = SlnxWith(root, "M1.slnx", string.Empty, "<BuildType Solution=\"Debug|*\" Project=\"Release\" />", "A");
            var mapped2 = SlnxWith(root, "M2.slnx", string.Empty, "<BuildType Solution=\"Debug|*\" Project=\"Release\" />", "A");

            Assert.AreEqual(0, SolutionSensitivity.Find([plain, plain2], projects).Count, "identical default mappings");
            Assert.AreEqual(1, SolutionSensitivity.Find([plain, configured], projects).Count, "different declared configurations");
            Assert.AreEqual(1, SolutionSensitivity.Find([plain, mapped1], projects).Count, "a different Configuration");
            Assert.AreEqual(0, SolutionSensitivity.Find([mapped1, mapped2], projects).Count, "identical explicit mappings");
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public void ConfigurationMapping_Sln_DifferentPlatform_IsSensitive_IdenticalIsNot()
    {
        var root = NewRoot("map-sln");
        try
        {
            WriteSolution(root, "Seed.slnx", "A", "B");
            var projects = new[] { FullProject(root, "A"), FullProject(root, "B") };
            var big = SlnWith(root, "Big.sln", "Debug|Any CPU", "A", "B");
            var same = SlnWith(root, "Same.sln", "Debug|AnyCPU", "A");
            var x64 = SlnWith(root, "X64.sln", "Debug|x64", "A");
            var release = SlnWith(root, "Rel.sln", "Release|Any CPU", "A");

            Assert.AreEqual(0, SolutionSensitivity.Find([big, same], projects).Count, "AnyCPU and Any CPU are the same mapping");
            CollectionAssert.AreEqual(new[] { projects[0] }, SolutionSensitivity.Find([big, x64], projects).ToArray());
            CollectionAssert.AreEqual(new[] { projects[0] }, SolutionSensitivity.Find([big, release], projects).ToArray());
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public async Task ASubset_MappingASharedProjectToADifferentPlatform_IsNotSkipped()
    {
        RequireUnix();
        var root = NewRoot("skip-platform");
        try
        {
            var fake = CreateFakeDotnet(System.IO.Path.Combine(root, "fake"), WriteAssets, "exit 0");
            WriteSolution(root, "Seed.slnx", "A", "B");
            var big = SlnxWith(root, "Big.slnx", string.Empty, string.Empty, "A", "B");
            var small = SlnxWith(root, "Small.slnx", string.Empty, "<Platform Solution=\"*|*\" Project=\"x64\" />", "A");
            ForcePerSolutionFallback(root);

            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(60)) { DotnetPath = fake.Path }
                .RunAsync(root, [big, small], limit: null, CancellationToken.None, System.IO.Path.Combine(root, "scratch"));

            CollectionAssert.AreEqual(new[] { "Big.slnx", "Small.slnx" }, RestoredSolutions(fake).ToArray());
            Assert.AreEqual(0, outcome.SolutionsSkippedRedundant);
        }
        finally { TryDelete(root); }
    }

    // A project that redirects its output (ArtifactsPath) has no obj/project.assets.json, so the per-project retry's
    // silence about it is the evidence that it restored (the rest of the superset failed deterministically).
    [TestMethod]
    public async Task ASubset_WhoseProjectsRestoredInARetry_WithoutAnObjAssetsFile_IsSkipped()
    {
        RequireUnix();
        var root = NewRoot("skip-redirected");
        try
        {
            var bad = System.IO.Path.Combine(root, "C", "C.csproj");
            var ok = string.Join(" ", new[] { "A", "B" }.Select(n => $"echo 'sextant-restore-ok: {System.IO.Path.Combine(root, n, n + ".csproj")}' ;"));
            var line = $"{ok} echo '{bad} : error NU1105: Invalid target framework x.' ; exit 1";
            var fake = CreateFakeDotnet(System.IO.Path.Combine(root, "fake"), "exit 1", line);
            var small = WriteSolution(root, "Small.slnx", "A", "B");
            var big = WriteSolution(root, "Big.slnx", "A", "B", "C");
            ForcePerSolutionFallback(root);

            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(60)) { DotnetPath = fake.Path }
                .RunAsync(root, [big, small], limit: null, CancellationToken.None, System.IO.Path.Combine(root, "scratch"));

            CollectionAssert.AreEqual(new[] { "Big.slnx" }, RestoredSolutions(fake).ToArray());
            Assert.AreEqual(1, outcome.SolutionsSkippedRedundant);
        }
        finally { TryDelete(root); }
    }

    // An aborted traversal: only A printed its sentinel, B was never reached and has no error either.
    [TestMethod]
    public async Task ASubset_WithAnUnreachedProjectOfAnAbortedRetry_IsNotSkipped()
    {
        RequireUnix();
        var root = NewRoot("skip-aborted");
        try
        {
            var bad = System.IO.Path.Combine(root, "C", "C.csproj");
            var line = $"echo 'sextant-restore-ok: {System.IO.Path.Combine(root, "A", "A.csproj")}' ; echo '{bad} : error NU1105: x.' ; exit 1";
            var fake = CreateFakeDotnet(System.IO.Path.Combine(root, "fake"), "exit 1", line);
            var small = WriteSolution(root, "Small.slnx", "A", "B");
            var big = WriteSolution(root, "Big.slnx", "A", "B", "C");
            ForcePerSolutionFallback(root);

            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(60)) { DotnetPath = fake.Path }
                .RunAsync(root, [big, small], limit: null, CancellationToken.None, System.IO.Path.Combine(root, "scratch"));

            CollectionAssert.AreEqual(new[] { "Big.slnx", "Small.slnx" }, RestoredSolutions(fake).ToArray());
            Assert.AreEqual(0, outcome.SolutionsSkippedRedundant);
        }
        finally { TryDelete(root); }
    }

    // A sentinel naming another path (or only another case/directory) never credits the declared project.
    [TestMethod]
    public async Task ASentinel_ForADifferentPath_DoesNotCoverTheProject()
    {
        RequireUnix();
        var root = NewRoot("skip-mismatch");
        try
        {
            var other = System.IO.Path.Combine(root, "other");
            var ok = string.Join(" ", new[] { "A", "B" }.Select(n => $"echo 'sextant-restore-ok: {System.IO.Path.Combine(other, n, n + ".csproj")}' ;"));
            var fake = CreateFakeDotnet(System.IO.Path.Combine(root, "fake"), "exit 1", $"{ok} exit 1");
            var small = WriteSolution(root, "Small.slnx", "A", "B");
            var big = WriteSolution(root, "Big.slnx", "A", "B", "C");
            ForcePerSolutionFallback(root);

            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(60)) { DotnetPath = fake.Path }
                .RunAsync(root, [big, small], limit: null, CancellationToken.None, System.IO.Path.Combine(root, "scratch"));

            Assert.AreEqual(0, outcome.SolutionsSkippedRedundant);
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public async Task ASubset_WhoseProjectFailedInTheEarlierRestore_IsNotSkipped()
    {
        RequireUnix();
        var root = NewRoot("skip-failed");
        try
        {
            var fake = CreateFakeDotnet(System.IO.Path.Combine(root, "fake"), "exit 1", "exit 1");
            var small = WriteSolution(root, "Small.slnx", "A");
            var big = WriteSolution(root, "Big.slnx", "A", "B");
            ForcePerSolutionFallback(root);

            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(60)) { DotnetPath = fake.Path }
                .RunAsync(root, [big, small], limit: null, CancellationToken.None, System.IO.Path.Combine(root, "scratch"));

            CollectionAssert.AreEqual(new[] { "Big.slnx", "Small.slnx" }, RestoredSolutions(fake).ToArray());
            Assert.AreEqual(0, outcome.SolutionsSkippedRedundant);
        }
        finally { TryDelete(root); }
    }

    private static string WriteSolutionRefs(string solutionDir, string name, params string[] projectPaths)
    {
        Directory.CreateDirectory(solutionDir);
        var solution = System.IO.Path.Combine(solutionDir, name);
        File.WriteAllText(solution,
            "<Solution>" + string.Join(string.Empty, projectPaths.Select(p => $"<Project Path=\"{p}\" />")) + "</Solution>");
        return solution;
    }

    [TestMethod]
    public async Task ASubset_InASiblingDirectory_SharingTheProjects_IsNotSkipped()
    {
        RequireUnix();
        var root = NewRoot("skip-sibling");
        try
        {
            var fake = CreateFakeDotnet(System.IO.Path.Combine(root, "fake"),
                $"for p in \"{root}\"/one/*/; do mkdir -p \"$p/obj\"; echo '{{}}' > \"$p/obj/project.assets.json\"; done; exit 0", "exit 0");
            var big = WriteSolution(System.IO.Path.Combine(root, "one"), "Big.slnx", "A", "B", "C");
            ForcePerSolutionFallback(System.IO.Path.Combine(root, "one"));
            var small = WriteSolutionRefs(System.IO.Path.Combine(root, "two"), "Small.slnx", "../one/A/A.csproj", "../one/B/B.csproj");

            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(60)) { DotnetPath = fake.Path }
                .RunAsync(root, [big, small], limit: null, CancellationToken.None, System.IO.Path.Combine(root, "scratch"));

            Assert.IsFalse(outcome.UsedProjectUnion);
            CollectionAssert.AreEqual(new[] { "Big.slnx", "Small.slnx" }, RestoredSolutions(fake).ToArray());
            Assert.AreEqual(0, outcome.SolutionsSkippedRedundant);
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public async Task ASubset_WhoseProjectsHaveAssetsFromAPreviousJob_ButFailedNow_IsNotSkipped()
    {
        RequireUnix();
        var root = NewRoot("skip-stale");
        try
        {
            var fake = CreateFakeDotnet(System.IO.Path.Combine(root, "fake"), "exit 1", "exit 1");
            var small = WriteSolution(root, "Small.slnx", "A");
            var big = WriteSolution(root, "Big.slnx", "A", "B");
            ForcePerSolutionFallback(root);
            foreach (var project in new[] { "A", "B" })
            {
                Directory.CreateDirectory(System.IO.Path.Combine(root, project, "obj"));
                File.WriteAllText(System.IO.Path.Combine(root, project, "obj", "project.assets.json"), "{}");
            }

            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(60)) { DotnetPath = fake.Path }
                .RunAsync(root, [big, small], limit: null, CancellationToken.None, System.IO.Path.Combine(root, "scratch"));

            CollectionAssert.AreEqual(new[] { "Big.slnx", "Small.slnx" }, RestoredSolutions(fake).ToArray());
            Assert.AreEqual(0, outcome.SolutionsSkippedRedundant);
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public async Task ASubset_IsSkipped_AfterTheSupersetSucceededThroughThePerProjectRetry()
    {
        RequireUnix();
        var root = NewRoot("skip-after-retry");
        try
        {
            var fake = CreateFakeDotnet(System.IO.Path.Combine(root, "fake"), "exit 1",
                $"for p in \"{root}\"/*/; do mkdir -p \"$p/obj\"; echo '{{}}' > \"$p/obj/project.assets.json\"; done; exit 0");
            var small = WriteSolution(root, "Small.slnx", "A", "B");
            var big = WriteSolution(root, "Big.slnx", "A", "B", "C");
            ForcePerSolutionFallback(root);

            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(60)) { DotnetPath = fake.Path }
                .RunAsync(root, [big, small], limit: null, CancellationToken.None, System.IO.Path.Combine(root, "scratch"));

            Assert.AreEqual(1, outcome.SolutionsRetriedPerProject);
            CollectionAssert.AreEqual(new[] { "Big.slnx" }, RestoredSolutions(fake).ToArray());
            CollectionAssert.AreEqual(new[] { "Small.slnx" }, outcome.SkippedSolutionNames.ToArray());
        }
        finally { TryDelete(root); }
    }

    private static string AssetsFor(string root, params string[] projects) =>
        string.Join("; ", projects.Select(p => $"mkdir -p \"{root}/{p}/obj\"; echo '{{}}' > \"{root}/{p}/obj/project.assets.json\""));

    [TestMethod]
    public async Task ASubset_IsSkipped_WhenTheSupersetsRetryFailedDeterministicallyForSomeProjects_AndDiagnosticsAppearOnce()
    {
        RequireUnix();
        var root = NewRoot("skip-deterministic");
        try
        {
            var b = System.IO.Path.Combine(root, "B", "B.csproj");
            var c = System.IO.Path.Combine(root, "C", "C.csproj");
            var d = System.IO.Path.Combine(root, "D", "D.csproj");
            var errors = $"echo '{b} : error NU1101: Unable to find package Foo.Bar. No packages exist with this id in source(s): s'; " +
                $"echo '{c} : error NU1102: Unable to find package Baz with version (>= 1.0)'; " +
                $"echo '/sdk/Sdks.targets(1,1): error NETSDK1147: To build this project, the following workloads must be installed: x [{d}]'; ";
            var fake = CreateFakeDotnet(System.IO.Path.Combine(root, "fake"), errors + "exit 1",
                errors + AssetsFor(root, "A") + $"; echo 'sextant-restore-ok: {System.IO.Path.Combine(root, "A", "A.csproj")}'; exit 1");
            var small = WriteSolution(root, "Small.slnx", "A", "B", "C");
            var big = WriteSolution(root, "Big.slnx", "A", "B", "C", "D");
            ForcePerSolutionFallback(root);

            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(60)) { DotnetPath = fake.Path }
                .RunAsync(root, [big, small], limit: null, CancellationToken.None, System.IO.Path.Combine(root, "scratch"));

            Assert.AreEqual(1, outcome.SolutionsRetriedPerProject);
            CollectionAssert.AreEqual(new[] { "Big.slnx" }, RestoredSolutions(fake).ToArray());
            CollectionAssert.AreEqual(new[] { "Small.slnx" }, outcome.SkippedSolutionNames.ToArray());
            CollectionAssert.AreEqual(
                new[] { "B/B.csproj", "C/C.csproj", "D/D.csproj" }, outcome.Projects.Select(x => x.Project).ToArray());
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public async Task ASubset_IsNotSkipped_WhenTheSupersetFailedTransiently()
    {
        RequireUnix();
        var root = NewRoot("skip-transient");
        try
        {
            var b = System.IO.Path.Combine(root, "B", "B.csproj");
            var errors = $"echo '{b} : error NU1301: Unable to load the service index for source https://example.invalid/index.json.'; ";
            var fake = CreateFakeDotnet(System.IO.Path.Combine(root, "fake"), errors + "exit 1",
                errors + AssetsFor(root, "A") + "; exit 1");
            var small = WriteSolution(root, "Small.slnx", "A", "B");
            var big = WriteSolution(root, "Big.slnx", "A", "B", "C");
            ForcePerSolutionFallback(root);

            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(60)) { DotnetPath = fake.Path }
                .RunAsync(root, [big, small], limit: null, CancellationToken.None, System.IO.Path.Combine(root, "scratch"));

            CollectionAssert.AreEqual(new[] { "Big.slnx", "Small.slnx" }, RestoredSolutions(fake).ToArray());
            Assert.AreEqual(0, outcome.SolutionsSkippedRedundant);
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public async Task CaseDistinctProjectPaths_AreNotConflated_OnACaseSensitiveHost()
    {
        RequireUnix();
        var root = NewRoot("skip-case");
        try
        {
            var probe = System.IO.Path.Combine(root, "probe");
            Directory.CreateDirectory(probe);
            if (Directory.Exists(System.IO.Path.Combine(root, "PROBE")))
                Assert.Inconclusive("The file system is case-insensitive.");
            var fake = CreateFakeDotnet(System.IO.Path.Combine(root, "fake"), WriteAssets, "exit 0");
            var big = WriteSolution(root, "Big.slnx", "a", "b", "c");
            var small = WriteSolution(root, "Small.slnx", "A", "B");
            Directory.CreateDirectory(System.IO.Path.Combine(root, "Outside"));
            File.WriteAllText(System.IO.Path.Combine(root, "Outside", "Outside.csproj"),
                $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>{Tfm}</TargetFramework></PropertyGroup></Project>");
            foreach (var project in new[] { "a", "A" })
                File.WriteAllText(System.IO.Path.Combine(root, project, project + ".csproj"), $"""
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup><TargetFramework>{Tfm}</TargetFramework></PropertyGroup>
                      <ItemGroup><ProjectReference Include="../Outside/Outside.csproj" /></ItemGroup>
                    </Project>
                    """);

            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(60)) { DotnetPath = fake.Path }
                .RunAsync(root, [big, small], limit: null, CancellationToken.None, System.IO.Path.Combine(root, "scratch"));

            Assert.IsFalse(outcome.UsedProjectUnion);
            CollectionAssert.AreEqual(new[] { "Big.slnx", "Small.slnx" }, RestoredSolutions(fake).ToArray(), string.Join("|", fake.Calls));
            Assert.AreEqual(0, outcome.SolutionsSkippedRedundant);
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public async Task ASubsetSolution_IsSkippedByARealRestore_AndEveryProjectHasItsAssets()
    {
        var root = NewRoot("skip-real");
        try
        {
            WriteOfflineNuGetConfig(root);
            var small = WriteSolution(root, "Small.slnx", "A", "B");
            var big = WriteSolution(root, "Big.slnx", "A", "B", "C");
            ForcePerSolutionFallback(root);

            var logs = new List<string>();
            var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromMinutes(4), log: logs.Add)
                .RunAsync(root, [big, small], limit: null, CancellationToken.None, System.IO.Path.Combine(root, "scratch"));

            Assert.IsFalse(outcome.UsedProjectUnion, string.Join("\n", logs));
            Assert.AreEqual(1, outcome.SolutionsSkippedRedundant, string.Join("\n", logs));
            CollectionAssert.AreEqual(new[] { "Small.slnx" }, outcome.SkippedSolutionNames.ToArray());
            Assert.AreEqual(1, outcome.SolutionsAttempted);
            Assert.IsTrue(outcome.Clean, string.Join("\n", logs));
            foreach (var project in new[] { "A", "B", "C", "Outside" })
                Assert.IsTrue(File.Exists(System.IO.Path.Combine(root, project, "obj", "project.assets.json")), project);
        }
        finally { TryDelete(root); }
    }

    private static string SetUpSmallInBig(string root, out string small, out string big)
    {
        WriteOfflineNuGetConfig(root);
        small = WriteSolution(root, "Small.slnx", "A", "B", "C");
        big = WriteSolution(root, "Big.slnx", "A", "B", "C", "D");
        ForcePerSolutionFallback(root);
        File.WriteAllText(System.IO.Path.Combine(root, "D", "D.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>{Tfm}</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="../C/C.csproj" /></ItemGroup>
            </Project>
            """);
        return root;
    }

    private static Dictionary<string, string> NormalizedAssets(string root)
        => Directory.EnumerateFiles(root, "project.assets.json", SearchOption.AllDirectories)
            .ToDictionary(
                f => System.IO.Path.GetRelativePath(root, f).Replace('\\', '/'),
                f => File.ReadAllText(f).Replace(root.Replace("\\", "/"), "<ROOT>", StringComparison.Ordinal)
                    .Replace(root, "<ROOT>", StringComparison.Ordinal));

    [TestMethod]
    public async Task APartialSkip_WritesTheSameAssetsFiles_AsRestoringTheSupersetWhole_ByRealRestores()
    {
        var wholeRoot = NewRoot("partial-real-whole");
        var partialRoot = NewRoot("partial-real-partial");
        try
        {
            SetUpSmallInBig(wholeRoot, out _, out var wholeBig);
            var wholeLogs = new List<string>();
            var whole = await new PackageRestoreRunner(timeout: TimeSpan.FromMinutes(4), log: wholeLogs.Add)
                .RunAsync(wholeRoot, [wholeBig], limit: null, CancellationToken.None, System.IO.Path.Combine(wholeRoot, "scratch"));
            Assert.IsTrue(whole.Clean, string.Join("\n", wholeLogs));
            Assert.AreEqual(0, whole.SolutionsPartiallySkipped);

            SetUpSmallInBig(partialRoot, out var small, out var big);
            var logs = new List<string>();
            var partial = await new PackageRestoreRunner(timeout: TimeSpan.FromMinutes(4), log: logs.Add)
                .RunAsync(partialRoot, [small, big], limit: null, CancellationToken.None, System.IO.Path.Combine(partialRoot, "scratch"));
            Assert.IsTrue(partial.Clean, string.Join("\n", logs));
            Assert.AreEqual(1, partial.SolutionsPartiallySkipped, string.Join("\n", logs));
            Assert.AreEqual(1, partial.ProjectsRestoredPartially);

            var expected = NormalizedAssets(wholeRoot);
            var actual = NormalizedAssets(partialRoot);
            Assert.IsTrue(expected.Count >= 5, string.Join(", ", expected.Keys));
            CollectionAssert.AreEquivalent(expected.Keys.ToList(), actual.Keys.ToList());
            foreach (var (path, text) in expected)
                Assert.AreEqual(text, actual[path], path);
        }
        finally
        {
            TryDelete(wholeRoot);
            TryDelete(partialRoot);
        }
    }

    [TestMethod]
    [DataRow("Directory.Solution.props")]
    [DataRow("Directory.Solution.targets")]
    public async Task ASuperset_WithADirectorySolutionFile_InItsDirectoryOrAnAncestor_IsRestoredWhole(string fileName)
    {
        RequireUnix();
        foreach (var inAncestor in new[] { false, true })
        {
            var outer = NewRoot("partial-dirsolution");
            try
            {
                var root = inAncestor ? System.IO.Path.Combine(outer, "sub") : outer;
                Directory.CreateDirectory(root);
                File.WriteAllText(System.IO.Path.Combine(outer, fileName), "<Project />");
                var (outcome, fake, logs) = await RunSubsetThenSupersetAt(
                    outer, root, () => WriteSolution(root, "Big.slnx", "A", "B", "C", "D", "E", "F", "G"));

                CollectionAssert.AreEqual(new[] { "Small.slnx", "Big.slnx" }, RestoredSolutions(fake).ToArray(), fileName);
                Assert.AreEqual(0, outcome.SolutionsPartiallySkipped);
                Assert.IsTrue(logs.Any(l => l.Contains("partial skip declined for 1 solution(s)", StringComparison.Ordinal)), string.Join("\n", logs));
            }
            finally { TryDelete(outer); }
        }
    }

    private static async Task<(PackageRestoreOutcome Outcome, FakeDotnet Fake, List<string> Logs)> RunSubsetThenSupersetAt(
        string checkout, string root, Func<string> superset)
    {
        var fake = CreateFakeDotnet(System.IO.Path.Combine(checkout, "fake"), WriteAssets, "exit 0");
        var small = WriteSolution(root, "Small.slnx", "A", "B", "C", "D");
        var big = superset();
        ForcePerSolutionFallback(root);
        var logs = new List<string>();
        var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(60), log: logs.Add) { DotnetPath = fake.Path }
            .RunAsync(checkout, [small, big], limit: null, CancellationToken.None, System.IO.Path.Combine(checkout, "scratch"));
        return (outcome, fake, logs);
    }

    // ==== issue #290: partial skip =======================================================================

    private static async Task<(PackageRestoreOutcome Outcome, FakeDotnet Fake, List<string> Logs)> RunSubsetThenSuperset(
        string root, string restoreBody, string msbuildBody, Func<string> superset, string? extraProperty = null)
    {
        var fake = CreateFakeDotnet(System.IO.Path.Combine(root, "fake"), restoreBody, msbuildBody);
        var small = WriteSolution(root, "Small.slnx", "A", "B", "C", "D");
        var big = superset();
        ForcePerSolutionFallback(root, extraProperty ?? string.Empty);
        var logs = new List<string>();
        var outcome = await new PackageRestoreRunner(timeout: TimeSpan.FromSeconds(60), log: logs.Add) { DotnetPath = fake.Path }
            .RunAsync(root, [small, big], limit: null, CancellationToken.None, System.IO.Path.Combine(root, "scratch"));
        return (outcome, fake, logs);
    }

    [TestMethod]
    public async Task ASuperset_AfterItsSubset_RestoresOnlyTheUncoveredProjects_ThroughTheTraversal()
    {
        RequireUnix();
        var root = NewRoot("partial-skip");
        try
        {
            var (outcome, fake, logs) = await RunSubsetThenSuperset(
                root, WriteAssets, AssetsFor(root, "E", "F", "G") + "; exit 0",
                () => WriteSolution(root, "Big.slnx", "A", "B", "C", "D", "E", "F", "G"));

            CollectionAssert.AreEqual(new[] { "Small.slnx" }, RestoredSolutions(fake).ToArray(), "Big itself is never restored");
            Assert.AreEqual(1, fake.Calls.Count(c => c.StartsWith("msbuild ", StringComparison.Ordinal)));
            foreach (var project in new[] { "E", "F", "G" })
                StringAssert.Contains(fake.Traversal, $"{project}/{project}.csproj");
            foreach (var project in new[] { "B", "C", "D" })
                Assert.IsFalse(fake.Traversal.Contains($"{project}/{project}.csproj", StringComparison.Ordinal), project);
            Assert.AreEqual(1, outcome.SolutionsPartiallySkipped);
            Assert.AreEqual(3, outcome.ProjectsRestoredPartially);
            CollectionAssert.AreEqual(new[] { "Big.slnx" }, outcome.PartiallySkippedSolutionNames.ToArray());
            Assert.AreEqual(0, outcome.SolutionsSkippedRedundant);
            Assert.AreEqual(2, outcome.SolutionsAttempted);
            Assert.IsTrue(outcome.Clean);
            Assert.IsTrue(logs.Any(l => l.Contains("1 solution(s) restored only for their uncovered projects (3 project(s): Big.slnx)", StringComparison.Ordinal)), string.Join("\n", logs));

            var recorder = new JobTimingsRecorder();
            recorder.RecordRestore(outcome, persistentPackageFolder: false);
            var restore = recorder.Build().Restore!;
            Assert.AreEqual(1, restore.SolutionsPartiallySkipped);
            Assert.AreEqual(3, restore.ProjectsRestoredPartially);
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public async Task ASuperset_WithASolutionSensitiveProject_IsRestoredWhole()
    {
        RequireUnix();
        var root = NewRoot("partial-sensitive");
        try
        {
            var (outcome, fake, _) = await RunSubsetThenSuperset(
                root, WriteAssets, "exit 0",
                () =>
                {
                    var big = WriteSolution(root, "Big.slnx", "A", "B", "C", "D", "E", "F", "G");
                    File.WriteAllText(System.IO.Path.Combine(root, "E", "E.csproj"),
                        $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>{Tfm}</TargetFramework><M>$(SolutionDir)</M></PropertyGroup></Project>");
                    return big;
                });

            CollectionAssert.AreEqual(new[] { "Small.slnx", "Big.slnx" }, RestoredSolutions(fake).ToArray());
            Assert.AreEqual(0, outcome.SolutionsPartiallySkipped);
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public async Task ASuperset_MappingAnUncoveredProjectToANonDefaultPlatform_IsRestoredWhole()
    {
        RequireUnix();
        var root = NewRoot("partial-nondefault");
        try
        {
            var (outcome, fake, _) = await RunSubsetThenSuperset(
                root, WriteAssets, "exit 0",
                () => SlnxWith(root, "Big.slnx", "<Configurations><Platform Name=\"x64\" /></Configurations>", string.Empty,
                    "A", "B", "C", "D", "E", "F", "G"));

            CollectionAssert.AreEqual(new[] { "Small.slnx", "Big.slnx" }, RestoredSolutions(fake).ToArray());
            Assert.AreEqual(0, outcome.SolutionsPartiallySkipped);
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public async Task ASuperset_WithHalfOrMoreProjectsUncovered_IsRestoredWhole()
    {
        RequireUnix();
        var root = NewRoot("partial-half");
        try
        {
            var (outcome, fake, _) = await RunSubsetThenSuperset(
                root, WriteAssets, "exit 0", () => WriteSolution(root, "Big.slnx", "A", "B", "C", "D", "E", "F", "G", "H"));

            CollectionAssert.AreEqual(new[] { "Small.slnx", "Big.slnx" }, RestoredSolutions(fake).ToArray());
            Assert.AreEqual(0, outcome.SolutionsPartiallySkipped);
        }
        finally { TryDelete(root); }
    }

    [TestMethod]
    public async Task APartialRestore_WhoseTraversalFailsForAnUncoveredProject_ReportsItAsBefore()
    {
        RequireUnix();
        var root = NewRoot("partial-failure");
        try
        {
            var f = System.IO.Path.Combine(root, "F", "F.csproj");
            var errors = $"echo '{f} : error NU1101: Unable to find package Foo.Bar. No packages exist with this id in source(s): s'; ";
            var (outcome, fake, _) = await RunSubsetThenSuperset(
                root, WriteAssets,
                errors + AssetsFor(root, "E", "G") + $"; echo 'sextant-restore-ok: {System.IO.Path.Combine(root, "E", "E.csproj")}'; exit 1",
                () => WriteSolution(root, "Big.slnx", "A", "B", "C", "D", "E", "F", "G"));

            CollectionAssert.AreEqual(new[] { "Small.slnx" }, RestoredSolutions(fake).ToArray());
            Assert.AreEqual(1, outcome.SolutionsPartiallySkipped);
            Assert.IsFalse(outcome.Clean);
            Assert.AreEqual("F/F.csproj", outcome.Projects.Single().Project);
            Assert.AreEqual(1, outcome.Projects.Count);
        }
        finally { TryDelete(root); }
    }

    private static string WriteSlnx(string body)
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "slnx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, "App.slnx");
        File.WriteAllText(path, body);
        return path;
    }

    [TestMethod]
    public void SlnxWithPlatformOnlyConfigurations_IsDefaultMapped()
    {
        var path = WriteSlnx("<Solution><Configurations><Platform Name=\"Any CPU\" /><Platform Name=\"x64\" /><Platform Name=\"x86\" /></Configurations>"
            + "<Folder Name=\"/s/\"><Project Path=\"a/a.csproj\" /></Folder></Solution>");
        try { Assert.AreEqual(0, SolutionSensitivity.FindNotDefaultMapped(path).Count); }
        finally { Directory.Delete(System.IO.Path.GetDirectoryName(path)!, true); }
    }

    [TestMethod]
    public void SlnxWithBuildTypesOrProjectOverrides_IsNotDefaultMapped()
    {
        foreach (var body in new[]
        {
            "<Solution><Configurations><BuildType Name=\"Debug\" /><Platform Name=\"Any CPU\" /></Configurations><Project Path=\"a/a.csproj\" /></Solution>",
            "<Solution><Configurations><Platform Name=\"x64\" /></Configurations><Project Path=\"a/a.csproj\" /></Solution>",
            "<Solution><Configurations><Platform Name=\"Any CPU\" /></Configurations><Project Path=\"a/a.csproj\"><Build Solution=\"Debug|*\" Project=\"false\" /></Project></Solution>",
        })
        {
            var path = WriteSlnx(body);
            try { Assert.AreEqual(1, SolutionSensitivity.FindNotDefaultMapped(path).Count, body); }
            finally { Directory.Delete(System.IO.Path.GetDirectoryName(path)!, true); }
        }
    }

    private static int Count(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
