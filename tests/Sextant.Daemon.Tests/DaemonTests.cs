using System.Net.Http;
using Sextant.Daemon;

namespace Sextant.Daemon.Tests;

[TestClass]
public class FileWatcherServiceTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sextant_watcher_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void TestCleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    [TestMethod]
    public async Task DebouncesMergesRapidSavesIntoSingleBatch()
    {
        var batches = new List<IReadOnlyList<string>>();
        var batchReceived = new TaskCompletionSource<bool>();

        using var watcher = new FileWatcherService(_tempDir, batch =>
        {
            batches.Add(batch);
            batchReceived.TrySetResult(true);
        }, debounceMs: 200);
        watcher.Start();

        // Write 3 files rapidly (< 200ms apart)
        File.WriteAllText(Path.Combine(_tempDir, "file1.cs"), "// v1");
        await Task.Delay(50);
        File.WriteAllText(Path.Combine(_tempDir, "file2.cs"), "// v1");
        await Task.Delay(50);
        File.WriteAllText(Path.Combine(_tempDir, "file3.cs"), "// v1");

        // Wait for debounce window + processing
        var completed = await Task.WhenAny(batchReceived.Task, Task.Delay(3000));
        Assert.IsTrue(completed == batchReceived.Task, "Batch should have been received");

        // Should have received exactly 1 batch with all 3 files (or some subset due to timing)
        Assert.AreEqual(1, batches.Count);
        Assert.IsTrue(batches[0].Count >= 2, $"Expected at least 2 files in batch, got {batches[0].Count}");
    }

    [TestMethod]
    public void IgnoresObjBinGitDirectories()
    {
        var batches = new List<IReadOnlyList<string>>();

        // Create obj and bin directories
        Directory.CreateDirectory(Path.Combine(_tempDir, "obj"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "bin"));
        Directory.CreateDirectory(Path.Combine(_tempDir, ".git"));

        using var watcher = new FileWatcherService(_tempDir, batch =>
        {
            batches.Add(batch);
        }, debounceMs: 100);
        watcher.Start();

        // Write files in ignored directories
        File.WriteAllText(Path.Combine(_tempDir, "obj", "test.cs"), "// ignored");
        File.WriteAllText(Path.Combine(_tempDir, "bin", "test.cs"), "// ignored");

        // Wait a bit to ensure no batch fires
        Thread.Sleep(500);
        Assert.AreEqual(0, batches.Count);
    }
}

[TestClass]
public class StatusServerTests
{
    private StatusServer? _server;

    [TestCleanup]
    public void TestCleanup()
    {
        _server?.Dispose();
    }

    [TestMethod]
    public async Task HealthEndpoint_Returns200()
    {
        _server = new StatusServer(() => new StatusInfo
        {
            State = "idle",
            QueuedFiles = 0,
            BackgroundTasks = 0,
            LastIndexedAt = 1000
        });
        _server.Start();

        using var client = new HttpClient();
        var response = await client.GetAsync($"http://localhost:{_server.Port}/health");
        Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.AreEqual("OK", content);
    }

    [TestMethod]
    public async Task StatusEndpoint_ReportsState()
    {
        _server = new StatusServer(() => new StatusInfo
        {
            State = "indexing",
            QueuedFiles = 3,
            BackgroundTasks = 1,
            LastIndexedAt = 5000
        });
        _server.Start();

        using var client = new HttpClient();
        var response = await client.GetAsync($"http://localhost:{_server.Port}/status");
        Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.AreEqual("indexing", doc.RootElement.GetProperty("state").GetString());
        Assert.AreEqual(3, doc.RootElement.GetProperty("queued_files").GetInt32());
        Assert.AreEqual(1, doc.RootElement.GetProperty("background_tasks").GetInt32());
        Assert.AreEqual(5000, doc.RootElement.GetProperty("last_indexed_at").GetInt64());
    }
}

[TestClass]
public class IndexingQueueTests
{
    [TestMethod]
    public async Task ImmediatePriorityDequeuedBeforeBackground()
    {
        var queue = new IndexingQueue();

        queue.Enqueue(new WorkItem
        {
            Priority = WorkPriority.Background,
            FilePaths = ["bg.cs"],
            Description = "background"
        });

        queue.Enqueue(new WorkItem
        {
            Priority = WorkPriority.Immediate,
            FilePaths = ["immediate.cs"],
            Description = "immediate"
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = await queue.DequeueAsync(cts.Token);
        Assert.IsNotNull(first);
        Assert.AreEqual("immediate", first!.Description);

        var second = await queue.DequeueAsync(cts.Token);
        Assert.IsNotNull(second);
        Assert.AreEqual("background", second!.Description);
    }

    [TestMethod]
    public async Task DequeueAsync_ReturnsNullOnCancellation()
    {
        var queue = new IndexingQueue();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var result = await queue.DequeueAsync(cts.Token);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void CountsTrackEnqueueAndDequeue()
    {
        var queue = new IndexingQueue();

        queue.Enqueue(new WorkItem { Priority = WorkPriority.Immediate, FilePaths = ["a.cs"], Description = "a" });
        queue.Enqueue(new WorkItem { Priority = WorkPriority.Background, FilePaths = ["b.cs"], Description = "b" });

        Assert.AreEqual(1, queue.GetImmediateCount());
        Assert.AreEqual(1, queue.GetBackgroundCount());
    }
}
