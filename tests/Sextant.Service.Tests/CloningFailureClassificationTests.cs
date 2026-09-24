using GitStage = Sextant.Service.CloningCheckoutProvider.GitStage;
using GitFailureKind = Sextant.Service.CloningCheckoutProvider.GitFailureKind;

namespace Sextant.Service.Tests;

/// <summary>
/// Unit tests for the transient-vs-deterministic git-failure classifier that decides whether a clone-on-miss
/// failure is RETRYABLE (the service requeues the identity) or PERMANENT (cached as a terminal
/// <c>unsupported</c>). This is the safety-critical seam for the idempotency-poisoning fix: mis-classifying a
/// transient network failure as deterministic permanently poisons that commit's identity, while
/// mis-classifying a permanent failure as transient only costs a bounded number of retries.
/// </summary>
[TestClass]
public class CloningFailureClassificationTests
{
    [TestMethod]
    public void Timeout_IsAlwaysTransient()
    {
        foreach (GitStage stage in Enum.GetValues<GitStage>())
            Assert.IsTrue(
                CloningCheckoutProvider.IsTransientGitFailure(stage, GitFailureKind.Timeout, string.Empty),
                $"a timeout at {stage} is a network/latency failure and must be retryable");
    }

    [TestMethod]
    public void SpawnFailure_IsAlwaysDeterministic()
    {
        foreach (GitStage stage in Enum.GetValues<GitStage>())
            Assert.IsFalse(
                CloningCheckoutProvider.IsTransientGitFailure(stage, GitFailureKind.SpawnFailure, string.Empty),
                $"git failing to start at {stage} is a permanent capability fault, not a retryable blip");
    }

    [TestMethod]
    public void SetupStages_AreTransient()
    {
        Assert.IsTrue(CloningCheckoutProvider.IsTransientGitFailure(GitStage.Init, GitFailureKind.NonZeroExit, "boom"));
        Assert.IsTrue(CloningCheckoutProvider.IsTransientGitFailure(GitStage.RemoteAdd, GitFailureKind.NonZeroExit, "boom"));
    }

    [TestMethod]
    public void CheckoutAndRevParse_AfterSuccessfulFetch_AreDeterministic()
    {
        // A fresh temp repo that fetched but cannot resolve/checkout the requested object means the commit
        // is not really present — a wrong-revision request, not a transient failure.
        Assert.IsFalse(CloningCheckoutProvider.IsTransientGitFailure(GitStage.Checkout, GitFailureKind.NonZeroExit, "boom"));
        Assert.IsFalse(CloningCheckoutProvider.IsTransientGitFailure(GitStage.RevParse, GitFailureKind.NonZeroExit, "boom"));
    }

    [TestMethod]
    public void Fetch_UnknownError_IsTransient()
    {
        // An unrecognized fetch error → transient (bounded by the attempt cap); better a few needless
        // retries than permanently poisoning a recoverable commit.
        Assert.IsTrue(CloningCheckoutProvider.IsTransientGitFailure(
            GitStage.Fetch, GitFailureKind.NonZeroExit, "fatal: unable to access: server hung up unexpectedly"));
    }

    [TestMethod]
    public void Fetch_AmbiguousErrors_StayTransient()
    {
        // Deliberately NOT in the permanent allowlist — a bare 403 or an aborted transfer is frequently a
        // rate-limit or a mid-transfer blip, so it must remain retryable.
        Assert.IsTrue(CloningCheckoutProvider.IsTransientGitFailure(
            GitStage.Fetch, GitFailureKind.NonZeroExit, "error: RPC failed; HTTP 403 curl 22"));
        Assert.IsTrue(CloningCheckoutProvider.IsTransientGitFailure(
            GitStage.Fetch, GitFailureKind.NonZeroExit, "fatal: the remote end hung up unexpectedly"));
        Assert.IsTrue(CloningCheckoutProvider.IsTransientGitFailure(
            GitStage.Fetch, GitFailureKind.NonZeroExit, "fatal: early EOF: did not send all necessary objects"));
    }

    [DataTestMethod]
    [DataRow("remote: Repository not found.")]
    [DataRow("fatal: repository 'https://x/y' does not appear to be a git repository")]
    [DataRow("fatal: Authentication failed for 'https://host/repo'")]
    [DataRow("remote: Invalid username or password.")]
    [DataRow("fatal: could not read Username for 'https://host': terminal prompts disabled")]
    [DataRow("fatal: couldn't find remote ref deadbeef")]
    [DataRow("fatal: reference is not a tree: deadbeef")]
    public void Fetch_UnambiguousPermanentErrors_AreDeterministic(string stderr)
    {
        Assert.IsFalse(CloningCheckoutProvider.IsTransientGitFailure(GitStage.Fetch, GitFailureKind.NonZeroExit, stderr),
            "an unambiguous repo/commit-not-found or auth-refused is permanent for this identity");
        Assert.IsTrue(CloningCheckoutProvider.MatchesDeterministicGitError(stderr));
    }

    [TestMethod]
    public void MatchesDeterministicGitError_IsCaseInsensitive_AndEmptyIsNotDeterministic()
    {
        Assert.IsTrue(CloningCheckoutProvider.MatchesDeterministicGitError("REMOTE: REPOSITORY NOT FOUND."));
        Assert.IsFalse(CloningCheckoutProvider.MatchesDeterministicGitError(string.Empty));
        Assert.IsFalse(CloningCheckoutProvider.MatchesDeterministicGitError("   "));
    }
}
