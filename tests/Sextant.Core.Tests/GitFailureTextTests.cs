using Sextant.Core;

namespace Sextant.Core.Tests;

[TestClass]
public class GitFailureTextTests
{
    [TestMethod]
    public void Describe_CarriesTheExitCodeAndTheFirstStderrLine()
    {
        var text = GitFailureText.Describe(128, "fatal: not a git repository\nsecond line\n");

        Assert.AreEqual("exit code 128: not a git repository", text);
    }

    [TestMethod]
    public void Describe_StripsUrlUserInfo_FromTheStderrLine()
    {
        var text = GitFailureText.Describe(
            128, "fatal: unable to access 'https://user:s3cr3tTOKEN@github.com/org/repo.git/': The requested URL returned error: 403");

        Assert.IsFalse(text.Contains("s3cr3tTOKEN", StringComparison.Ordinal), text);
        Assert.IsFalse(text.Contains("user:", StringComparison.Ordinal), text);
        StringAssert.Contains(text, "https://github.com/org/repo.git/");
        StringAssert.Contains(text, "exit code 128");
    }

    [TestMethod]
    public void Describe_BoundsTheLine_AndHandlesNoStderrOrNoExitCode()
    {
        Assert.IsTrue(GitFailureText.FirstLine(new string('x', 1000)).Length <= GitFailureText.MaxLength + 1);
        Assert.AreEqual("exit code 1, no stderr", GitFailureText.Describe(1, "  \n"));
        Assert.AreEqual("git did not run, no stderr", GitFailureText.Describe(null, null));
    }

    [TestMethod]
    public void FirstLine_PrefersTheFatalOrErrorLine_OverLeadingAdvice()
    {
        const string stderr = "hint: Using 'master' as the name for the initial branch.\nwarning: redirecting to x\nfatal: bad object HEAD\nerror: later\n";

        Assert.AreEqual("bad object HEAD", GitFailureText.FirstLine(stderr));
        Assert.AreEqual("error: refusing", GitFailureText.FirstLine("hint: advice\nerror: refusing\nfatal: after"));
        Assert.AreEqual("hint: only advice", GitFailureText.FirstLine("hint: only advice\nwarning: more"));
    }

    [TestMethod]
    public void FirstLine_StillRedactsAndBounds_ThePreferredLine()
    {
        var redacted = GitFailureText.FirstLine("hint: x\nfatal: unable to access 'https://user:s3cr3tTOKEN@github.com/o/r.git/'");
        Assert.IsFalse(redacted.Contains("s3cr3tTOKEN", StringComparison.Ordinal), redacted);
        Assert.IsTrue(GitFailureText.FirstLine("hint: x\nfatal: " + new string('y', 1000)).Length <= GitFailureText.MaxLength + 1);
    }
}
