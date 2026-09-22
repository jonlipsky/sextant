using Sextant.Core;

namespace Sextant.Service.Tests;

/// <summary>
/// Phase 17 — criterion 2. Security-relevant boolean toggles parsed from the environment must FAIL CLOSED:
/// a typo (e.g. <c>SANDBOX_ENABLED=tru</c>) must abort startup loudly, never silently fall through to a
/// disabled sandbox. Recognized true/false spellings still work (case-insensitive, trimmed); an unset var
/// keeps the secure default. These mutate a process-global env var, so each restores it in a finally.
/// </summary>
[TestClass]
public class ServiceOptionsEnvTests
{
    private const string SandboxEnabled = "SEXTANT_SERVICE_SANDBOX_ENABLED";

    private static SextantConfiguration Config() => new() { DbPath = ServiceTestFixtures.NewDbPath() };

    [TestMethod]
    public void MalformedSecurityToggle_FailsClosed_Throws()
    {
        Environment.SetEnvironmentVariable(SandboxEnabled, "tru");
        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(
                () => ServiceOptions.FromEnvironment(Config()),
                "a malformed security toggle must abort startup, not silently disable the sandbox");
        }
        finally
        {
            Environment.SetEnvironmentVariable(SandboxEnabled, null);
        }
    }

    [TestMethod]
    public void UnsetToggle_KeepsSecureDefault_Enabled()
    {
        Environment.SetEnvironmentVariable(SandboxEnabled, null);
        var options = ServiceOptions.FromEnvironment(Config());
        Assert.IsTrue(options.Sandbox.Enabled, "the sandbox is enabled by default when the toggle is unset");
    }

    [TestMethod]
    public void RecognizedFalse_DisablesSandbox()
    {
        Environment.SetEnvironmentVariable(SandboxEnabled, "false");
        try
        {
            var options = ServiceOptions.FromEnvironment(Config());
            Assert.IsFalse(options.Sandbox.Enabled, "an explicit recognized false value disables the sandbox");
        }
        finally
        {
            Environment.SetEnvironmentVariable(SandboxEnabled, null);
        }
    }

    [TestMethod]
    public void RecognizedTrue_IsCaseInsensitiveAndTrimmed()
    {
        Environment.SetEnvironmentVariable(SandboxEnabled, "  On ");
        try
        {
            var options = ServiceOptions.FromEnvironment(Config());
            Assert.IsTrue(options.Sandbox.Enabled, "recognized true spellings are case-insensitive and trimmed");
        }
        finally
        {
            Environment.SetEnvironmentVariable(SandboxEnabled, null);
        }
    }
}
