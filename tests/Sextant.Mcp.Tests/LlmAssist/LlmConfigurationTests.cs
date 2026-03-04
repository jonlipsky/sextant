using Sextant.Mcp.LlmAssist;

namespace Sextant.Mcp.Tests.LlmAssist;

[TestClass]
public class LlmConfigurationTests
{
    [TestMethod]
    public void Defaults_AreCorrect()
    {
        var config = new LlmConfiguration();

        Assert.AreEqual("openai-compatible", config.Provider);
        Assert.AreEqual("gpt-4o", config.Model);
        Assert.IsNull(config.BaseUrl);
        Assert.IsNull(config.ApiKey);
        Assert.IsNull(config.ApiKeyEnv);
        Assert.AreEqual(15, config.MaxToolCalls);
        Assert.IsTrue(config.Enabled);
    }

    [TestMethod]
    public void ResolveApiKey_DirectValue_TakesPriority()
    {
        var config = new LlmConfiguration { ApiKey = "direct-key" };
        Assert.AreEqual("direct-key", config.ResolveApiKey());
    }

    [TestMethod]
    public void ResolveApiKey_NoKey_ReturnsNull()
    {
        var config = new LlmConfiguration();
        // Ensure env vars don't interfere
        var savedKey = Environment.GetEnvironmentVariable("SEXTANT_LLM_API_KEY");
        var savedKeyEnv = Environment.GetEnvironmentVariable("SEXTANT_LLM_API_KEY_ENV");
        try
        {
            Environment.SetEnvironmentVariable("SEXTANT_LLM_API_KEY", null);
            Environment.SetEnvironmentVariable("SEXTANT_LLM_API_KEY_ENV", null);
            Assert.IsNull(config.ResolveApiKey());
        }
        finally
        {
            Environment.SetEnvironmentVariable("SEXTANT_LLM_API_KEY", savedKey);
            Environment.SetEnvironmentVariable("SEXTANT_LLM_API_KEY_ENV", savedKeyEnv);
        }
    }

    [TestMethod]
    public void ResolveApiKey_EnvVarDirect_TakesPriority()
    {
        var config = new LlmConfiguration { ApiKey = "config-key" };
        var saved = Environment.GetEnvironmentVariable("SEXTANT_LLM_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("SEXTANT_LLM_API_KEY", "env-key");
            Assert.AreEqual("env-key", config.ResolveApiKey());
        }
        finally
        {
            Environment.SetEnvironmentVariable("SEXTANT_LLM_API_KEY", saved);
        }
    }

    [TestMethod]
    public void ResolveApiKey_Indirection_ReadsNamedEnvVar()
    {
        var config = new LlmConfiguration { ApiKeyEnv = "TEST_SEXTANT_KEY_12345" };
        var savedKey = Environment.GetEnvironmentVariable("SEXTANT_LLM_API_KEY");
        var savedIndirect = Environment.GetEnvironmentVariable("TEST_SEXTANT_KEY_12345");
        try
        {
            Environment.SetEnvironmentVariable("SEXTANT_LLM_API_KEY", null);
            Environment.SetEnvironmentVariable("TEST_SEXTANT_KEY_12345", "indirect-key");
            Assert.AreEqual("indirect-key", config.ResolveApiKey());
        }
        finally
        {
            Environment.SetEnvironmentVariable("SEXTANT_LLM_API_KEY", savedKey);
            Environment.SetEnvironmentVariable("TEST_SEXTANT_KEY_12345", savedIndirect);
        }
    }
}
