using Sextant.Core;

namespace Sextant.Service.Tests;

/// <summary>
/// Environment binding for <see cref="ServiceOptions.FromEnvironment"/>. Security-relevant boolean toggles
/// must FAIL CLOSED: a typo (e.g. <c>SANDBOX_ENABLED=tru</c>) must abort startup loudly, never silently
/// fall through to a disabled sandbox. Recognized true/false spellings still work (case-insensitive,
/// trimmed); an unset var keeps the secure default. The bind address follows the same fail-closed
/// convention (a whitespace-only value aborts startup) and defaults to loopback. These mutate a
/// process-global env var, so each restores it in a finally.
/// </summary>
[TestClass]
public class ServiceOptionsEnvTests
{
    private const string SandboxEnabled = "SEXTANT_SERVICE_SANDBOX_ENABLED";
    private const string BindAddress = "SEXTANT_SERVICE_BIND_ADDRESS";
    private const string CheckoutMode = "SEXTANT_SERVICE_CHECKOUT_MODE";
    private const string CheckoutToken = "SEXTANT_SERVICE_CHECKOUT_TOKEN";
    private const string MaxProvisioningAttempts = "SEXTANT_SERVICE_MAX_PROVISIONING_ATTEMPTS";

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

    [TestMethod]
    public void BindAddress_DefaultsToLocalhost_WhenUnset()
    {
        Environment.SetEnvironmentVariable(BindAddress, null);
        var options = ServiceOptions.FromEnvironment(Config());
        Assert.AreEqual("localhost", options.BindAddress,
            "the bind address defaults to loopback so out-of-the-box behavior is unchanged");
    }

    [TestMethod]
    public void BindAddress_RoutableValue_BindsThrough()
    {
        Environment.SetEnvironmentVariable(BindAddress, "0.0.0.0");
        try
        {
            var options = ServiceOptions.FromEnvironment(Config());
            Assert.AreEqual("0.0.0.0", options.BindAddress,
                "a routable bind address flows through so the service can listen on all interfaces");
        }
        finally
        {
            Environment.SetEnvironmentVariable(BindAddress, null);
        }
    }

    [TestMethod]
    public void BindAddress_IsTrimmed()
    {
        Environment.SetEnvironmentVariable(BindAddress, "  127.0.0.1 ");
        try
        {
            var options = ServiceOptions.FromEnvironment(Config());
            Assert.AreEqual("127.0.0.1", options.BindAddress,
                "surrounding whitespace is trimmed so the constructed listen URL is well-formed");
        }
        finally
        {
            Environment.SetEnvironmentVariable(BindAddress, null);
        }
    }

    [TestMethod]
    public void BindAddress_WhitespaceOnly_FailsClosed_Throws()
    {
        Environment.SetEnvironmentVariable(BindAddress, "   ");
        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(
                () => ServiceOptions.FromEnvironment(Config()),
                "a whitespace-only bind address must abort startup, not bind to an empty interface");
        }
        finally
        {
            Environment.SetEnvironmentVariable(BindAddress, null);
        }
    }

    [TestMethod]
    [DataRow("bad host")]
    [DataRow("[::1")]
    [DataRow("has\ttab")]
    public void BindAddress_MalformedHost_FailsClosed_Throws(string value)
    {
        // A syntactically invalid host token must fail closed rather than being handed to Kestrel, which
        // would silently widen an unrecognized token to a bind on ALL interfaces.
        Environment.SetEnvironmentVariable(BindAddress, value);
        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(
                () => ServiceOptions.FromEnvironment(Config()),
                "a malformed bind address must abort startup, not silently widen to all interfaces");
        }
        finally
        {
            Environment.SetEnvironmentVariable(BindAddress, null);
        }
    }

    [TestMethod]
    [DataRow("localhost")]
    [DataRow("127.0.0.1")]
    [DataRow("::1")]
    [DataRow("[::1]")]
    [DataRow("index.internal")]
    public void BindAddress_ValidHostTokens_FlowThrough(string value)
    {
        Environment.SetEnvironmentVariable(BindAddress, value);
        try
        {
            var options = ServiceOptions.FromEnvironment(Config());
            Assert.AreEqual(value, options.BindAddress,
                "a recognized host token (localhost, an IP literal, or a hostname) flows through unchanged");
        }
        finally
        {
            Environment.SetEnvironmentVariable(BindAddress, null);
        }
    }

    [TestMethod]
    public void CheckoutMode_DefaultsToLocate_WhenUnset()
    {
        Environment.SetEnvironmentVariable(CheckoutMode, null);
        var options = ServiceOptions.FromEnvironment(Config());
        Assert.AreEqual(ServiceCheckoutMode.Locate, options.CheckoutMode,
            "checkout mode defaults to locate (no outbound git) so behavior is unchanged out of the box");
    }

    [TestMethod]
    [DataRow("clone", ServiceCheckoutMode.Clone)]
    [DataRow("  Clone ", ServiceCheckoutMode.Clone)]
    [DataRow("LOCATE", ServiceCheckoutMode.Locate)]
    public void CheckoutMode_RecognizedValues_AreCaseInsensitiveAndTrimmed(string value, ServiceCheckoutMode expected)
    {
        Environment.SetEnvironmentVariable(CheckoutMode, value);
        try
        {
            var options = ServiceOptions.FromEnvironment(Config());
            Assert.AreEqual(expected, options.CheckoutMode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(CheckoutMode, null);
        }
    }

    [TestMethod]
    public void CheckoutMode_UnknownValue_FailsClosed_Throws()
    {
        Environment.SetEnvironmentVariable(CheckoutMode, "pull");
        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(
                () => ServiceOptions.FromEnvironment(Config()),
                "an unknown checkout mode must abort startup, not silently pick a mode");
        }
        finally
        {
            Environment.SetEnvironmentVariable(CheckoutMode, null);
        }
    }

    [TestMethod]
    public void CheckoutToken_FlowsThrough_WhenSet()
    {
        Environment.SetEnvironmentVariable(CheckoutToken, "ghs_secret");
        try
        {
            var options = ServiceOptions.FromEnvironment(Config());
            Assert.AreEqual("ghs_secret", options.CheckoutToken);
        }
        finally
        {
            Environment.SetEnvironmentVariable(CheckoutToken, null);
        }
    }

    [TestMethod]
    public void MaxProvisioningAttempts_DefaultsToFive_WhenUnset()
    {
        Environment.SetEnvironmentVariable(MaxProvisioningAttempts, null);
        var options = ServiceOptions.FromEnvironment(Config());
        Assert.AreEqual(5, options.MaxProvisioningAttempts,
            "the transient-provisioning retry bound defaults to 5 when unset");
    }

    [TestMethod]
    [DataRow("1", 1)]
    [DataRow("  10 ", 10)]
    [DataRow("100", 100)]
    public void MaxProvisioningAttempts_ValidValues_FlowThrough(string value, int expected)
    {
        Environment.SetEnvironmentVariable(MaxProvisioningAttempts, value);
        try
        {
            var options = ServiceOptions.FromEnvironment(Config());
            Assert.AreEqual(expected, options.MaxProvisioningAttempts);
        }
        finally
        {
            Environment.SetEnvironmentVariable(MaxProvisioningAttempts, null);
        }
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("-3")]
    [DataRow("101")]
    [DataRow("abc")]
    public void MaxProvisioningAttempts_OutOfRangeOrGarbage_FallsBackToDefault(string value)
    {
        // A non-positive, oversized, or unparseable value is clamped to the safe default rather than
        // disabling retries (0) or allowing an unbounded retry ceiling.
        Environment.SetEnvironmentVariable(MaxProvisioningAttempts, value);
        try
        {
            var options = ServiceOptions.FromEnvironment(Config());
            Assert.AreEqual(5, options.MaxProvisioningAttempts);
        }
        finally
        {
            Environment.SetEnvironmentVariable(MaxProvisioningAttempts, null);
        }
    }
}
