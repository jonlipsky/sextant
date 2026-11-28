using System.Text.Json;
using ModelContextProtocol.Protocol;
using Sextant.Mcp.Tools;
using Sextant.Store;

namespace Sextant.Mcp.Tests;

[TestClass]
public sealed class QueryReadAdmissionTests
{
    [TestMethod]
    public void RepeatedReads_CheckReadinessOnceForThePublishedGeneration()
    {
        using var fixture = new McpTestFixture();
        var readinessChecks = 0;
        fixture.DbProvider.ReadinessCheckForTesting = database =>
        {
            Interlocked.Increment(ref readinessChecks);
            return database.CheckReadiness();
        };

        for (var i = 0; i < 8; i++)
        {
            Assert.IsTrue(fixture.DbProvider.TryBeginRead(out _, out _, out var failure), failure);
        }

        Assert.AreEqual(1, readinessChecks);
    }

    [TestMethod]
    public void ReadinessCache_InvalidatesWhenASecondPublishedGenerationIsObserved()
    {
        using var fixture = new McpTestFixture();
        var readinessChecks = 0;
        fixture.DbProvider.ReadinessCheckForTesting = database =>
        {
            Interlocked.Increment(ref readinessChecks);
            return database.CheckReadiness();
        };
        Assert.IsTrue(fixture.DbProvider.TryBeginRead(out _, out _, out var failure), failure);
        fixture.DbProvider.ObserveReadGeneration(41);
        fixture.DbProvider.ObserveReadGeneration(42);

        Assert.IsTrue(fixture.DbProvider.TryBeginRead(out _, out _, out failure), failure);
        Assert.AreEqual(2, readinessChecks);
    }

    [TestMethod]
    public async Task ReadSlotTimeout_ReturnsRetryableIndexBusyError()
    {
        var clock = new ManualClock();
        using var provider = new DatabaseProvider(
            TestTempRoot.NewFilePath("query_admission", ".db"),
            readConcurrency: 1,
            readAdmissionTimeout: TimeSpan.FromSeconds(5),
            timeProvider: clock);
        using var occupied = await provider.TryAcquireReadSlotAsync();
        Assert.IsNotNull(occupied);

        var waitingRead = provider.TryAcquireReadSlotAsync().AsTask();
        clock.Advance(TimeSpan.FromSeconds(5));
        using var acquired = await waitingRead.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsNull(acquired);

        using var response = JsonDocument.Parse(ResponseBuilder.BuildIndexBusy());
        var error = response.RootElement.GetProperty("meta").GetProperty("error");
        Assert.AreEqual("index_busy", error.GetProperty("code").GetString());
        Assert.AreEqual(1, error.GetProperty("retry_after_seconds").GetInt32());
        var toolError = QueryReadAdmissionFilter.BusyResult();
        Assert.IsTrue(toolError.IsError);
        Assert.AreEqual("index_busy", JsonDocument.Parse(((TextContentBlock)toolError.Content.Single()).Text!)
            .RootElement.GetProperty("meta").GetProperty("error").GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task QueryCompletesWhileIndexingHoldsTheWriterGate()
    {
        using var fixture = new McpTestFixture();
        var indexingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseIndexing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var indexing = Task.Run(async () =>
        {
            using var writeSession = fixture.Db.BeginWriteSession();
            writeSession.Begin();
            indexingStarted.SetResult();
            await releaseIndexing.Task.ConfigureAwait(false);
            writeSession.Release();
        });

        await indexingStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            using var readSlot = await fixture.DbProvider.TryAcquireReadSlotAsync();
            Assert.IsNotNull(readSlot);
            var query = FindSymbolTool.FindSymbol(fixture.DbProvider, "BaseService");
            var result = await query.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsTrue(result.Contains("BaseService", StringComparison.Ordinal));
        }
        finally
        {
            releaseIndexing.TrySetResult();
            await indexing;
        }
    }
}
