using Sextant.Service.Restore;

namespace Sextant.Service.Tests;

/// <summary>Issue #349: restore error codes are parsed with sanitized messages and classified deterministic or transient.</summary>
[TestClass]
public class RestoreErrorClassificationTests
{
    private static readonly string Checkout = Path.Combine(Path.GetTempPath(), "sextant-restore-classify");

    private static string Project(string name) => Path.Combine(Checkout, name, name + ".csproj");

    [TestMethod]
    public void Parser_RecordsNetSdkAndMsBuildCodes_PerProject()
    {
        var parser = new RestoreOutputParser(Checkout);
        parser.Accept($"{Project("Mobile")} : error NETSDK1147: To build this project, the following workloads must be installed: ios [{Checkout}/All.slnx]");
        parser.Accept($"{Project("Win")} : error NETSDK1100: To build a project targeting Windows on this operating system, set EnableWindowsTargeting.");
        parser.Accept($"{Project("Odd")} : error MSB4019: The imported project was not found.");

        var projects = parser.Projects().ToDictionary(p => p.Project);
        CollectionAssert.AreEqual(new[] { "NETSDK1147" }, projects["Mobile/Mobile.csproj"].Codes.ToArray());
        CollectionAssert.AreEqual(new[] { "NETSDK1100" }, projects["Win/Win.csproj"].Codes.ToArray());
        CollectionAssert.AreEqual(new[] { "MSB4019" }, projects["Odd/Odd.csproj"].Codes.ToArray());
        Assert.AreEqual(0, parser.GeneralCodes().Count);
        StringAssert.Contains(projects["Mobile/Mobile.csproj"].Messages["NETSDK1147"], "workloads must be installed");
    }

    [TestMethod]
    public void Parser_MessagesAreSanitized_NoUrlNoMaskedSourceNoCheckoutPathNoTrailingProject()
    {
        var parser = new RestoreOutputParser(Checkout);
        parser.Accept($"{Project("a")} : error NU1301: Unable to load the service index for source ******feed.example.test/v3/index.json. [{Checkout}/All.slnx]");
        parser.Accept($"{Project("b")} : error NU1101: Unable to find package P in https://user:hunter2@feed.example.test/v3/index.json at {Checkout}{Path.DirectorySeparatorChar}b");

        foreach (var issue in parser.Projects())
        {
            foreach (var message in issue.Messages.Values)
            {
                Assert.IsFalse(message.Contains("feed.example.test", StringComparison.Ordinal), message);
                Assert.IsFalse(message.Contains("hunter2", StringComparison.Ordinal), message);
                Assert.IsFalse(message.Contains(Checkout, StringComparison.Ordinal), message);
                Assert.IsFalse(message.Contains('[', StringComparison.Ordinal), message);
                Assert.IsTrue(message.Length <= RestoreErrorClassifier.MaxMessageLength, message);
            }
        }
    }

    [TestMethod]
    [DataRow("Unable to find package at /home/alice/.nuget/packages/x/1.0.0/x.nupkg", "alice")]
    [DataRow("Cannot read /var/lib/sextant/cache/nuget-packages/repo/x", "sextant")]
    [DataRow("Cannot read C:\\Users\\bob\\.nuget\\packages\\x", "bob")]
    [DataRow("Cannot read C:/Users/bob/.nuget/packages/x", "bob")]
    [DataRow("Cannot read \\\\fileserver\\share\\carol\\x.nupkg", "carol")]
    [DataRow("Cannot read ~/.nuget/packages/dave/x", "dave")]
    [DataRow("Failed for erin:hunter2@feed.example.test/index.json", "hunter2")]
    [DataRow("Failed for erin@feed.example.test", "feed.example.test")]
    public void Sanitize_RedactsPathsAndUserInfoWithoutAScheme(string message, string secret)
    {
        var text = RestoreErrorClassifier.Sanitize(message, Checkout);
        Assert.IsFalse(text.Contains(secret, StringComparison.Ordinal), text);
    }

    [TestMethod]
    public void Sanitize_KeepsCodeAndPackageId()
    {
        var text = RestoreErrorClassifier.Sanitize("NU1101: Unable to find package Acme.Lib. No packages exist with this id.", Checkout);
        StringAssert.Contains(text, "NU1101");
        StringAssert.Contains(text, "Acme.Lib");
    }

    [TestMethod]
    public void Sanitize_BoundsLongMessages()
    {
        var text = RestoreErrorClassifier.Sanitize(new string('x', 1000), Checkout);
        Assert.AreEqual(RestoreErrorClassifier.MaxMessageLength, text.Length);
    }

    [TestMethod]
    [DataRow("NETSDK1147", true)]
    [DataRow("NETSDK1005", true)]
    [DataRow("NU1100", true)]
    [DataRow("NU1101", true)]
    [DataRow("NU1202", true)]
    [DataRow("NU1301", false)]
    [DataRow("NU1801", false)]
    [DataRow("NU9999", false)]
    [DataRow("MSB4018", false)]
    public void Code_IsDeterministicOnlyWhenItFollowsFromTheInputs(string code, bool deterministic)
        => Assert.AreEqual(deterministic, RestoreErrorClassifier.IsDeterministicCode(code));

    [TestMethod]
    [DataRow("The HTTP request to 'GET x' has timed out after 100000ms.", true)]
    [DataRow("Response status code does not indicate success: 503 (Service Unavailable).", true)]
    [DataRow("Unable to load the service index for source.", true)]
    [DataRow("An error occurred while sending the request.", true)]
    [DataRow("Unable to find package Acme.ClassLibrary. No packages exist with this id in source(s): local", false)]
    [DataRow("Package X 1.0.0 is not compatible with net10.0.", false)]
    public void Message_IsTransientWhenItReadsLikeANetworkOrServerFailure(string message, bool transient)
        => Assert.AreEqual(transient, RestoreErrorClassifier.IsTransientMessage(message));

    [TestMethod]
    public void Issue_IsDeterministicOnlyWithRecognisedCodesAndNoNetworkSign()
    {
        var parser = new RestoreOutputParser(Checkout);
        parser.Accept($"{Project("det")} : error NU1202: Package X 1.0.0 is not compatible with net10.0.");
        parser.Accept($"{Project("notfound")} : error NU1101: Unable to find package Y. No packages exist with this id in source(s): local");
        parser.Accept($"{Project("feed")} : error NU1101: Unable to find package Z. No packages exist with this id in source(s): nuget.org");
        parser.Accept($"{Project("feed")} : error NU1301: Unable to load the service index for source.");
        parser.Accept($"{Project("timeout")} : error NU1101: Response status code does not indicate success: 503 (Service Unavailable).");
        parser.Accept($"{Project("unknown")} : error NU4242: Something unrecognised.");

        var byProject = parser.Projects().ToDictionary(p => p.Project, p => p.Deterministic);
        Assert.IsTrue(byProject["det/det.csproj"]);
        Assert.IsTrue(byProject["notfound/notfound.csproj"]);
        Assert.IsFalse(byProject["feed/feed.csproj"], "an unreachable source makes the failure transient");
        Assert.IsFalse(byProject["timeout/timeout.csproj"], "a 5xx message makes the failure transient");
        Assert.IsFalse(byProject["unknown/unknown.csproj"], "an unrecognised code is transient");
    }

    [TestMethod]
    public void Outcome_SummarisesTopCodesWithCounts_AndFlagsNonDeterministicFailures()
    {
        var parser = new RestoreOutputParser(Checkout);
        for (var i = 0; i < 3; i++)
            parser.Accept($"{Project("m" + i)} : error NETSDK1147: workloads must be installed: ios");
        parser.Accept($"{Project("n")} : error NU1202: Package X is not compatible with net10.0.");

        var deterministic = new PackageRestoreOutcome { SolutionsAttempted = 1, Projects = parser.Projects() };
        var codes = deterministic.ErrorCodes();
        Assert.AreEqual("NETSDK1147", codes[0].Code);
        Assert.AreEqual(3, codes[0].Projects);
        Assert.IsTrue(codes[0].Deterministic);
        Assert.IsFalse(deterministic.HasNonDeterministicFailure);
        var line = deterministic.ErrorSummaryLine()!;
        StringAssert.Contains(line, "NETSDK1147 x3");
        StringAssert.Contains(line, "NU1202 x1");
        StringAssert.Contains(deterministic.Summary(), "error_codes=NETSDK1147x3,NU1202x1");

        parser.Accept($"{Project("t")} : error NU1301: Unable to load the service index.");
        var mixed = new PackageRestoreOutcome { SolutionsAttempted = 1, Projects = parser.Projects() };
        Assert.IsTrue(mixed.HasNonDeterministicFailure);
        StringAssert.Contains(mixed.ErrorSummaryLine()!, "never cached as failing");
        Assert.IsNull(new PackageRestoreOutcome { SolutionsAttempted = 1 }.ErrorSummaryLine());
    }

    [TestMethod]
    public void Outcome_BoundsTheCodeList()
    {
        var parser = new RestoreOutputParser(Checkout);
        for (var i = 0; i < 20; i++)
            parser.Accept($"{Project("p" + i)} : error NETSDK{1000 + i}: message {i}");
        var outcome = new PackageRestoreOutcome { SolutionsAttempted = 1, Projects = parser.Projects() };
        Assert.AreEqual(8, outcome.ErrorCodes().Count);
    }
}
