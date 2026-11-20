using Sextant.Core;
using Sextant.Service.CallerIdentity;
using Sextant.Service.Contributions;
using Sextant.Service.Restore;

namespace Sextant.Service.Tests;

[TestClass]
public sealed class SecretToStringTests
{
    [TestMethod]
    public void ServiceOptions_ToStringOmitsEverySecretLoadedFromEnvironment()
    {
        const string control = "control-token-marker";
        const string query = "query-token-marker";
        const string contribute = "contribute-token-marker";
        const string checkout = "checkout-token-marker";
        const string delegateToken = "delegate-token-marker";
        const string readToken = "read-policy-token-marker";
        const string packagePassword = "package-password-marker";
        var callerKey = Convert.ToBase64String(Enumerable.Range(0, CallerKeyRing.MinimumKeyBytes).Select(i => (byte)i).ToArray())
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var secretNames = new[]
        {
            "SEXTANT_SERVICE_CONTROL_TOKEN", "SEXTANT_SERVICE_QUERY_TOKEN", "SEXTANT_SERVICE_CONTRIBUTE_TOKEN",
            "SEXTANT_SERVICE_CHECKOUT_TOKEN", "SEXTANT_SERVICE_DELEGATE_TOKENS", "SEXTANT_SERVICE_CALLER_KEYS",
            "SEXTANT_SERVICE_READ_POLICY", "SEXTANT_SERVICE_PACKAGE_SOURCE_CREDENTIALS"
        };
        var environment = new Dictionary<string, string?>();
        foreach (var name in secretNames)
        {
            environment[name] = null;
            environment[name + "_FILE"] = null;
        }
        environment["SEXTANT_SERVICE_CONTROL_TOKEN"] = control;
        environment["SEXTANT_SERVICE_QUERY_TOKEN"] = query;
        environment["SEXTANT_SERVICE_CONTRIBUTE_TOKEN"] = contribute;
        environment["SEXTANT_SERVICE_CHECKOUT_TOKEN"] = checkout;
        environment["SEXTANT_SERVICE_DELEGATE_TOKENS"] = delegateToken;
        environment["SEXTANT_SERVICE_CALLER_KEYS"] = $"key-a={callerKey}@tenant-a";
        environment["SEXTANT_SERVICE_CALLER_AUDIENCE"] = "sextant";
        environment["SEXTANT_SERVICE_READ_POLICY"] = $"{readToken}=https://github.com/acme/widgets";
        environment["SEXTANT_SERVICE_PACKAGE_SOURCE_CREDENTIALS"] = $"nuget.example.com=user:{packagePassword}";

        var options = WithEnvironment(environment, () => ServiceOptions.FromEnvironment(new SextantConfiguration()));
        var text = options.ToString();

        AssertNotContains(text, control, query, contribute, checkout, delegateToken, callerKey, readToken, packagePassword);
    }

    [TestMethod]
    public void ReadPrincipal_ToStringRedactsItsToken()
    {
        const string secret = "read-principal-token-marker";
        var principal = new ReadPrincipal
        {
            Token = secret,
            Repositories = new HashSet<string>(StringComparer.Ordinal) { "https://github.com/acme/widgets" }
        };

        AssertNotContains(principal.ToString(), secret);
    }

    [TestMethod]
    public void ReadAuthorizationPolicy_ToStringRedactsPrincipalTokens()
    {
        const string secret = "read-policy-token-marker";
        var policy = ReadAuthorizationPolicy.Parse($"{secret}=https://github.com/acme/widgets");

        AssertNotContains(policy.ToString(), secret);
    }

    [TestMethod]
    public void CallerAssertionOptions_ToStringRedactsSigningKeyMaterial()
    {
        var encodedKey = Convert.ToBase64String(Enumerable.Range(1, CallerKeyRing.MinimumKeyBytes).Select(i => (byte)i).ToArray())
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var options = new CallerAssertionOptions
        {
            Keys = CallerKeyRing.Parse($"key-a={encodedKey}@tenant-a"),
            Audience = "sextant"
        };

        AssertNotContains(options.ToString(), encodedKey);
    }

    [TestMethod]
    public void SextantConfiguration_ToStringOmitsPeerTokenLoadedFromEnvironment()
    {
        const string secret = "peer-query-token-marker";
        var config = WithEnvironment(
            new Dictionary<string, string?> { ["SEXTANT_PEER_QUERY_TOKEN"] = secret, ["SEXTANT_PEER_QUERY_TOKEN_FILE"] = null },
            SextantConfiguration.FromEnvironment);

        Assert.AreEqual(secret, config.PeerQueryToken);
        AssertNotContains(config.ToString(), secret);
    }

    [TestMethod]
    public void ContributionUploadOptions_ToStringRedactsBearerToken()
    {
        const string secret = "contribution-upload-token-marker";
        var options = new ContributionUploadOptions
        {
            ServiceUrl = new Uri("https://index.example.test"),
            Token = secret
        };

        AssertNotContains(options.ToString(), secret);
    }

    private static void AssertNotContains(string text, params string[] secrets)
    {
        foreach (var secret in secrets)
            Assert.IsFalse(text.Contains(secret, StringComparison.Ordinal), "ToString must not reveal credential values.");
    }

    private static T WithEnvironment<T>(Dictionary<string, string?> values, Func<T> body)
    {
        var previous = values.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var (name, value) in values)
                Environment.SetEnvironmentVariable(name, value);
            return body();
        }
        finally
        {
            foreach (var (name, value) in previous)
                Environment.SetEnvironmentVariable(name, value);
        }
    }
}
