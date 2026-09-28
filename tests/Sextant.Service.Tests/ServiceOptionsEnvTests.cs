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
    private const string MaxGrantsPerPrincipal = "SEXTANT_SERVICE_MAX_GRANTS_PER_PRINCIPAL";
    private const string MaxGrantsPerTenant = "SEXTANT_SERVICE_MAX_GRANTS_PER_TENANT";
    private const string SearchMaxWidth = "SEXTANT_SERVICE_SEARCH_MAX_WIDTH";
    private const string SearchMaxHits = "SEXTANT_SERVICE_SEARCH_MAX_HITS";
    private const string RequireRepositorySelection = "SEXTANT_SERVICE_REQUIRE_REPOSITORY_SELECTION";
    private const string RepositoryHosts = "SEXTANT_SERVICE_REPOSITORY_HOSTS";
    private const string RepositoryOwners = "SEXTANT_SERVICE_REPOSITORY_OWNERS";

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

    [TestMethod]
    [DataRow(null, null, 200, 5000)]
    [DataRow("3", " 40 ", 3, 40)]
    [DataRow("0", "-1", 200, 5000)]
    [DataRow("abc", "", 200, 5000)]
    public void MaxGrants_BindOrFallBackToDefault(string? perPrincipal, string? perTenant, int expectedPrincipal, int expectedTenant)
    {
        // SVC-4: a missing, non-positive or unparseable limit keeps the default rather than disabling grants.
        Environment.SetEnvironmentVariable(MaxGrantsPerPrincipal, perPrincipal);
        Environment.SetEnvironmentVariable(MaxGrantsPerTenant, perTenant);
        try
        {
            var options = ServiceOptions.FromEnvironment(Config());
            Assert.AreEqual(expectedPrincipal, options.MaxGrantsPerPrincipal);
            Assert.AreEqual(expectedTenant, options.MaxGrantsPerTenant);
        }
        finally
        {
            Environment.SetEnvironmentVariable(MaxGrantsPerPrincipal, null);
            Environment.SetEnvironmentVariable(MaxGrantsPerTenant, null);
        }
    }

    [TestMethod]
    [DataRow(null, 50)]
    [DataRow("7", 7)]
    [DataRow(" 12 ", 12)]
    [DataRow("100", 100)]
    [DataRow("500", 100)]
    [DataRow("0", 50)]
    [DataRow("-1", 50)]
    [DataRow("abc", 50)]
    public void SearchMaxWidth_BindsClampsOrFallsBackToDefault(string? value, int expected)
    {
        // SVC-F: a missing, non-positive or unparseable width keeps the default; a large one is clamped to the
        // ceiling that keeps a search cursor under its 16 KiB bound.
        Environment.SetEnvironmentVariable(SearchMaxWidth, value);
        try
        {
            Assert.AreEqual(expected, ServiceOptions.FromEnvironment(Config()).SearchMaxWidth);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SearchMaxWidth, null);
        }
    }

    [TestMethod]
    [DataRow(null, 500)]
    [DataRow("250", 250)]
    [DataRow(" 1000 ", 1000)]
    [DataRow("7", 100)]
    [DataRow("100", 100)]
    [DataRow("5000", 5000)]
    [DataRow("99999", 5000)]
    [DataRow("0", 500)]
    [DataRow("-1", 500)]
    [DataRow("abc", 500)]
    public void SearchMaxHits_BindsClampsOrFallsBackToDefault(string? value, int expected)
    {
        // Issue #196: the per-call total-hit cap. A missing, non-positive or unparseable value keeps the default; any
        // other is clamped so every snapshot a page reads still gets at least one hit and one call stays bounded.
        Environment.SetEnvironmentVariable(SearchMaxHits, value);
        try
        {
            Assert.AreEqual(expected, ServiceOptions.FromEnvironment(Config()).SearchMaxHits);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SearchMaxHits, null);
        }
    }

    [TestMethod]
    public void RequireRepositorySelection_DefaultsOff_WhenUnset()
    {
        Environment.SetEnvironmentVariable(RequireRepositorySelection, null);
        var options = ServiceOptions.FromEnvironment(Config());
        Assert.IsFalse(options.RequireRepositorySelection,
            "a query with no repository selector keeps reading the unselected default until the operator opts in");
    }

    [TestMethod]
    [DataRow("true", true)]
    [DataRow(" ON ", true)]
    [DataRow("0", false)]
    public void RequireRepositorySelection_RecognizedValue_Binds(string value, bool expected)
    {
        Environment.SetEnvironmentVariable(RequireRepositorySelection, value);
        try
        {
            var options = ServiceOptions.FromEnvironment(Config());
            Assert.AreEqual(expected, options.RequireRepositorySelection);
        }
        finally
        {
            Environment.SetEnvironmentVariable(RequireRepositorySelection, null);
        }
    }

    [TestMethod]
    public void RequireRepositorySelection_Malformed_FailsClosed_Throws()
    {
        Environment.SetEnvironmentVariable(RequireRepositorySelection, "tru");
        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(
                () => ServiceOptions.FromEnvironment(Config()),
                "a typo must abort startup rather than silently leave selection optional");
        }
        finally
        {
            Environment.SetEnvironmentVariable(RequireRepositorySelection, null);
        }
    }

    [TestMethod]
    public void RepositoryUrlPolicy_UnsetIsGitHubOnly()
    {
        Environment.SetEnvironmentVariable(RepositoryHosts, null);
        Environment.SetEnvironmentVariable(RepositoryOwners, null);
        var policy = ServiceOptions.FromEnvironment(Config()).RepositoryUrlPolicy;
        CollectionAssert.AreEqual(new[] { "github.com" }, policy.Hosts.ToArray());
        Assert.IsNull(policy.Owners);
        Assert.IsTrue(policy.Evaluate("https://github.com/org/app").Ok);
        Assert.AreEqual(RepositoryUrlRejection.HostNotAllowed, policy.Evaluate("https://gitlab.com/org/app").Reason);
    }

    [TestMethod]
    public void RepositoryUrlPolicy_BindsHostsAndOwners()
    {
        Environment.SetEnvironmentVariable(RepositoryHosts, "github.com, git.example.com");
        Environment.SetEnvironmentVariable(RepositoryOwners, "git.example.com/*,github.com/org");
        try
        {
            var policy = ServiceOptions.FromEnvironment(Config()).RepositoryUrlPolicy;
            Assert.IsTrue(policy.Evaluate("https://git.example.com/team/app").Ok);
            Assert.IsTrue(policy.Evaluate("https://github.com/org/app").Ok);
            Assert.AreEqual(RepositoryUrlRejection.OwnerNotAllowed, policy.Evaluate("https://github.com/other/app").Reason);
        }
        finally
        {
            Environment.SetEnvironmentVariable(RepositoryHosts, null);
            Environment.SetEnvironmentVariable(RepositoryOwners, null);
        }
    }

    [TestMethod]
    [DataRow(RepositoryHosts, "github.com,https://evil.example")]
    [DataRow(RepositoryHosts, "10.0.0.1")]
    [DataRow(RepositoryHosts, " , ")]
    [DataRow(RepositoryOwners, "github.com")]
    [DataRow(RepositoryOwners, "gitlab.com/org")]
    public void RepositoryUrlPolicy_MalformedEntry_FailsStartup(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(
                () => ServiceOptions.FromEnvironment(Config()),
                "a malformed repository host/owner allow-list must abort startup (fail closed)");
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    // ==== SVC-3: delegate tokens and caller assertions ==============================================

    private const string DelegateTokens = "SEXTANT_SERVICE_DELEGATE_TOKENS";
    private const string CallerKeys = "SEXTANT_SERVICE_CALLER_KEYS";
    private const string CallerAudience = "SEXTANT_SERVICE_CALLER_AUDIENCE";
    private const string CallerIssuers = "SEXTANT_SERVICE_CALLER_ISSUERS";
    private const string CallerHeader = "SEXTANT_SERVICE_CALLER_HEADER";
    private const string CallerIdps = "SEXTANT_SERVICE_CALLER_IDPS";
    private const string CallerApps = "SEXTANT_SERVICE_CALLER_APPS";
    private const string QueryTokenVar = "SEXTANT_SERVICE_QUERY_TOKEN";
    private const string ControlTokenVar = "SEXTANT_SERVICE_CONTROL_TOKEN";
    private const string ContributeTokenVar = "SEXTANT_SERVICE_CONTRIBUTE_TOKEN";
    private const string ReadPolicyVar = "SEXTANT_SERVICE_READ_POLICY";

    private static readonly string[] CallerVariables =
    [
        DelegateTokens, CallerKeys, CallerAudience, CallerIssuers, CallerHeader, CallerIdps, CallerApps,
        QueryTokenVar, ControlTokenVar, ContributeTokenVar, ReadPolicyVar
    ];

    private static T WithCallerEnv<T>(Dictionary<string, string?> values, Func<T> body)
    {
        foreach (var name in CallerVariables)
            Environment.SetEnvironmentVariable(name, values.GetValueOrDefault(name));
        try
        {
            return body();
        }
        finally
        {
            foreach (var name in CallerVariables)
                Environment.SetEnvironmentVariable(name, null);
        }
    }

    private static Dictionary<string, string?> ValidCallerEnv(byte[] key) => new()
    {
        [DelegateTokens] = "delegate-one; delegate-two",
        [CallerKeys] = CallerAssertionSigner.KeySpec("kid-a", key, "tenant-a"),
        [CallerAudience] = "sextant"
    };

    private static InvalidOperationException AssertStartupFails(Dictionary<string, string?> env) =>
        WithCallerEnv(env, () => Assert.ThrowsExactly<InvalidOperationException>(() => ServiceOptions.FromEnvironment(Config())));

    [TestMethod]
    public void CallerIdentity_UnsetIsOff()
    {
        var options = WithCallerEnv([], () => ServiceOptions.FromEnvironment(Config()));
        Assert.AreEqual(0, options.DelegateTokens.Count);
        Assert.IsFalse(options.CallerAssertion.Enabled);
        Assert.AreEqual("X-ProcessStack-Caller", options.CallerAssertion.Header);
        CollectionAssert.AreEquivalent(new[] { "processstack" }, options.CallerAssertion.Idps.ToArray());
        Assert.AreEqual(0, options.CallerAssertion.Apps.Count);
        Assert.AreEqual(0, options.CallerAssertion.Issuers.Count);
    }

    [TestMethod]
    public void CallerIdentity_BindsEveryVariable()
    {
        var key = CallerAssertionSigner.NewKey();
        var env = ValidCallerEnv(key);
        env[CallerKeys] += ";" + CallerAssertionSigner.KeySpec("kid-b", CallerAssertionSigner.NewKey(), "tenant-b");
        env[CallerIssuers] = "https://platform.example.test, https://platform2.example.test";
        env[CallerHeader] = "X-Caller-Assertion";
        env[CallerIdps] = "processstack,slack";
        env[CallerApps] = "sextant";
        env[QueryTokenVar] = "query-secret";

        var options = WithCallerEnv(env, () => ServiceOptions.FromEnvironment(Config()));

        CollectionAssert.AreEqual(new[] { "delegate-one", "delegate-two" }, options.DelegateTokens.ToArray());
        Assert.AreEqual(2, options.CallerAssertion.Keys.Count);
        Assert.AreEqual("tenant-b", options.CallerAssertion.Keys.TenantOf("kid-b"));
        Assert.AreEqual("sextant", options.CallerAssertion.Audience);
        CollectionAssert.AreEquivalent(
            new[] { "https://platform.example.test", "https://platform2.example.test" }, options.CallerAssertion.Issuers.ToArray());
        Assert.AreEqual("X-Caller-Assertion", options.CallerAssertion.Header);
        CollectionAssert.AreEquivalent(new[] { "processstack", "slack" }, options.CallerAssertion.Idps.ToArray());
        CollectionAssert.AreEquivalent(new[] { "sextant" }, options.CallerAssertion.Apps.ToArray());
    }

    [TestMethod]
    public void CallerIdentity_KeysWithoutDelegateTokens_AreAllowed()
    {
        // Keys alone let control calls carry an assertion (audit actor) with no delegate read path.
        var env = ValidCallerEnv(CallerAssertionSigner.NewKey());
        env.Remove(DelegateTokens);
        var options = WithCallerEnv(env, () => ServiceOptions.FromEnvironment(Config()));
        Assert.IsTrue(options.CallerAssertion.Enabled);
        Assert.AreEqual(0, options.DelegateTokens.Count);
    }

    [TestMethod]
    public void CallerIdentity_DelegateTokensWithoutKeys_FailStartup()
    {
        var env = ValidCallerEnv(CallerAssertionSigner.NewKey());
        env.Remove(CallerKeys);
        env.Remove(CallerAudience);
        AssertStartupFails(env);
    }

    [TestMethod]
    public void CallerIdentity_KeysWithoutAudience_FailStartup()
    {
        var env = ValidCallerEnv(CallerAssertionSigner.NewKey());
        env.Remove(CallerAudience);
        AssertStartupFails(env);
        env[CallerAudience] = "  ";
        AssertStartupFails(env);
    }

    [TestMethod]
    public void CallerIdentity_ShortKey_FailsStartup_WithoutEchoingIt()
    {
        var shortKey = System.Buffers.Text.Base64Url.EncodeToString(CallerAssertionSigner.NewKey()[..31]);
        var env = ValidCallerEnv(CallerAssertionSigner.NewKey());
        env[CallerKeys] = $"kid-a={shortKey}@tenant-a";
        var ex = AssertStartupFails(env);
        Assert.IsFalse(ex.Message.Contains(shortKey, StringComparison.Ordinal), ex.Message);
        StringAssert.Contains(ex.Message, "entry #1");
    }

    [TestMethod]
    public void CallerIdentity_DuplicateKeyId_FailsStartup()
    {
        var env = ValidCallerEnv(CallerAssertionSigner.NewKey());
        env[CallerKeys] += ";" + CallerAssertionSigner.KeySpec("kid-a", CallerAssertionSigner.NewKey(), "tenant-b");
        AssertStartupFails(env);
    }

    [TestMethod]
    [DataRow(CallerIdps, "processstack,Slack")]
    [DataRow(CallerIdps, "processstack,sl ack")]
    [DataRow(CallerIdps, " , ")]
    [DataRow(CallerApps, "sextant,a b")]
    [DataRow(CallerApps, ",")]
    [DataRow(CallerIssuers, " , ")]
    [DataRow(CallerHeader, "Authorization")]
    [DataRow(CallerHeader, "X-Sextant-Repository")]
    [DataRow(CallerHeader, "X Caller")]
    [DataRow(DelegateTokens, " ; ")]
    public void CallerIdentity_MalformedEntry_FailsStartup(string name, string value)
    {
        var env = ValidCallerEnv(CallerAssertionSigner.NewKey());
        env[name] = value;
        AssertStartupFails(env);
    }

    [TestMethod]
    [DataRow(QueryTokenVar)]
    [DataRow(ControlTokenVar)]
    [DataRow(ContributeTokenVar)]
    public void CallerIdentity_DelegateTokenEqualToAnotherToken_FailsStartup_WithoutEchoingIt(string other)
    {
        var env = ValidCallerEnv(CallerAssertionSigner.NewKey());
        env[other] = "delegate-two";
        var ex = AssertStartupFails(env);
        Assert.IsFalse(ex.Message.Contains("delegate-two", StringComparison.Ordinal), ex.Message);
        StringAssert.Contains(ex.Message, "entry #2");
    }

    [TestMethod]
    public void CallerIdentity_DelegateTokenEqualToAReadPolicyPrincipal_FailsStartup()
    {
        var env = ValidCallerEnv(CallerAssertionSigner.NewKey());
        env[ReadPolicyVar] = "delegate-one=https://github.com/acme/widgets";
        AssertStartupFails(env);
    }

    [TestMethod]
    public void CallerIdentity_ValidatedForDirectlyBuiltOptions()
    {
        var options = ServiceTestFixtures.NewOptions(ServiceTestFixtures.NewDbPath(), queryToken: "same") with
        {
            DelegateTokens = ["same"],
            CallerAssertion = new Sextant.Service.CallerIdentity.CallerAssertionOptions
            {
                Keys = Sextant.Service.CallerIdentity.CallerKeyRing.Create([("kid-a", CallerAssertionSigner.NewKey(), "tenant-a")]),
                Audience = "sextant"
            }
        };
        Assert.ThrowsExactly<InvalidOperationException>(options.ValidateCallerIdentity);
    }

    // ==== SX-6d: the control plane needs a token unless explicitly opted out ============================

    private const string InsecureOpenControlPlaneVar = "SEXTANT_SERVICE_INSECURE_OPEN_CONTROL_PLANE";

    private static T WithControlEnv<T>(string? controlToken, string? openControlPlane, Func<T> body)
    {
        Environment.SetEnvironmentVariable(ControlTokenVar, controlToken);
        Environment.SetEnvironmentVariable(InsecureOpenControlPlaneVar, openControlPlane);
        try
        {
            return body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ControlTokenVar, null);
            Environment.SetEnvironmentVariable(InsecureOpenControlPlaneVar, null);
        }
    }

    [TestMethod]
    public void InsecureOpenControlPlane_UnsetIsOff_AndTheOptionsStillBuild()
    {
        // FromEnvironment does not enforce the guard (the offline backup/restore commands use it and serve nothing);
        // ValidateControlPlane, which the host runs before it serves, does.
        var options = WithControlEnv(null, null, () => ServiceOptions.FromEnvironment(Config()));

        Assert.IsFalse(options.InsecureOpenControlPlane);
        Assert.IsFalse(options.ControlPlaneIsOpen);
        var refused = Assert.ThrowsExactly<InvalidOperationException>(options.ValidateControlPlane);
        StringAssert.Contains(refused.Message, "SEXTANT_SERVICE_CONTROL_TOKEN is not set");
        StringAssert.Contains(refused.Message, InsecureOpenControlPlaneVar);
    }

    [TestMethod]
    [DataRow("true")]
    [DataRow(" ON ")]
    [DataRow("1")]
    public void InsecureOpenControlPlane_Set_OpensTheControlPlaneOnPurpose(string value)
    {
        var options = WithControlEnv(null, value, () => ServiceOptions.FromEnvironment(Config()));

        Assert.IsTrue(options.InsecureOpenControlPlane);
        Assert.IsTrue(options.ControlPlaneIsOpen);
        options.ValidateControlPlane();
    }

    [TestMethod]
    [DataRow("false")]
    [DataRow("0")]
    public void InsecureOpenControlPlane_False_StillRefuses(string value)
    {
        var options = WithControlEnv(null, value, () => ServiceOptions.FromEnvironment(Config()));

        Assert.IsFalse(options.ControlPlaneIsOpen);
        Assert.ThrowsExactly<InvalidOperationException>(options.ValidateControlPlane);
    }

    [TestMethod]
    public void InsecureOpenControlPlane_Malformed_FailsClosed()
    {
        WithControlEnv(null, "yes-please", () => Assert.ThrowsExactly<InvalidOperationException>(
            () => ServiceOptions.FromEnvironment(Config()), "a malformed opt-out must abort startup, not open the plane"));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("true")]
    public void ControlToken_Set_PassesTheGuard_AndTheOptOutIsIgnored(string? openControlPlane)
    {
        var options = WithControlEnv("control-secret", openControlPlane, () => ServiceOptions.FromEnvironment(Config()));

        Assert.AreEqual("control-secret", options.ControlToken);
        Assert.IsFalse(options.ControlPlaneIsOpen, "a configured control token always wins over the opt-out");
        options.ValidateControlPlane();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ControlToken_WhitespaceOnly_FailsTheGuard_EvenWithTheOptOut(bool openControlPlane)
    {
        var options = ServiceTestFixtures.NewOptions(ServiceTestFixtures.NewDbPath(), controlToken: "   ") with
        {
            InsecureOpenControlPlane = openControlPlane
        };

        var refused = Assert.ThrowsExactly<InvalidOperationException>(options.ValidateControlPlane);
        StringAssert.Contains(refused.Message, "blank");
    }
}
