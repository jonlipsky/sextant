namespace Sextant.TestSupport;

[TestClass]
public static class TestRunLifecycle
{
    [AssemblyInitialize]
    public static void Initialize(TestContext _) => TestTempRoot.Initialize();

    [AssemblyCleanup]
    public static void Cleanup() => TestTempRoot.Cleanup();
}
