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

    private static int Count(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
