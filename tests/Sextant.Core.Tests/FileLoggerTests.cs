using System.Text.RegularExpressions;
using Sextant.Core;

namespace Sextant.Core.Tests;

[TestClass]
public class FileLoggerTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void TestInitialize()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sextant_log_test_{Guid.NewGuid():N}");
    }

    [TestCleanup]
    public void TestCleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [TestMethod]
    public void Open_CreatesDirectoryAndFile()
    {
        using var logger = FileLogger.Open(_tempDir, "test.log");

        Assert.IsTrue(Directory.Exists(_tempDir));
        Assert.IsTrue(File.Exists(Path.Combine(_tempDir, "test.log")));
    }

    [TestMethod]
    public void Write_AppendsTimestampedLine()
    {
        using var logger = FileLogger.Open(_tempDir, "test.log");

        logger.Write("hello world");
        logger.Dispose(); // flush

        var content = File.ReadAllText(Path.Combine(_tempDir, "test.log"));
        StringAssert.Contains(content, "hello world");
        // Verify ISO 8601 timestamp prefix
        Assert.IsTrue(Regex.IsMatch(content, @"\[\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}"));
    }

    [TestMethod]
    public void CreateCallback_WritesToFileAndPassthrough()
    {
        using var logger = FileLogger.Open(_tempDir, "test.log");
        var passthroughMessages = new List<string>();

        var callback = logger.CreateCallback(msg => passthroughMessages.Add(msg));
        callback("message one");
        callback("message two");
        logger.Dispose();

        var content = File.ReadAllText(Path.Combine(_tempDir, "test.log"));
        StringAssert.Contains(content, "message one");
        StringAssert.Contains(content, "message two");
        CollectionAssert.AreEqual(new[] { "message one", "message two" }, passthroughMessages.ToArray());
    }

    [TestMethod]
    public void Rotation_OccursWhenFileSizeExceedsLimit()
    {
        var smallLimit = 1024L; // 1KB for fast test
        using var logger = FileLogger.Open(_tempDir, "test.log", smallLimit);

        // Write enough to exceed limit
        var bigMessage = new string('x', 200);
        for (var i = 0; i < 20; i++)
            logger.Write(bigMessage);

        logger.Dispose();

        var logPath = Path.Combine(_tempDir, "test.log");
        var rotatedPath = logPath + ".1";

        Assert.IsTrue(File.Exists(logPath), "Current log file should exist");
        Assert.IsTrue(File.Exists(rotatedPath), "Rotated log file should exist");
        // Both files should have content
        Assert.IsTrue(new FileInfo(logPath).Length > 0);
        Assert.IsTrue(new FileInfo(rotatedPath).Length > 0);
    }

    [TestMethod]
    public void Rotation_DeletesOldRotatedFile()
    {
        var smallLimit = 512L;
        using var logger = FileLogger.Open(_tempDir, "test.log", smallLimit);

        var bigMessage = new string('y', 200);

        // Write enough to trigger multiple rotations
        for (var i = 0; i < 50; i++)
            logger.Write(bigMessage);

        logger.Dispose();

        var rotatedPath = Path.Combine(_tempDir, "test.log.1");
        Assert.IsTrue(File.Exists(rotatedPath));
        // Only .1 exists, no .2
        Assert.IsFalse(File.Exists(Path.Combine(_tempDir, "test.log.2")));
    }

    [TestMethod]
    public void Write_IsThreadSafe()
    {
        using var logger = FileLogger.Open(_tempDir, "test.log");

        var threads = new Thread[10];
        for (var t = 0; t < threads.Length; t++)
        {
            var threadId = t;
            threads[t] = new Thread(() =>
            {
                for (var i = 0; i < 100; i++)
                    logger.Write($"thread-{threadId}-msg-{i}");
            });
        }

        foreach (var thread in threads)
            thread.Start();
        foreach (var thread in threads)
            thread.Join();

        logger.Dispose();

        // Read all lines from both log and any rotated file
        var logPath = Path.Combine(_tempDir, "test.log");
        var allLines = new List<string>(File.ReadAllLines(logPath));
        var rotatedPath = logPath + ".1";
        if (File.Exists(rotatedPath))
            allLines.InsertRange(0, File.ReadAllLines(rotatedPath));

        // All 1000 messages should be present
        Assert.AreEqual(1000, allLines.Count);
        // Spot check a few
        Assert.IsTrue(allLines.Any(l => l.Contains("thread-0-msg-0")));
        Assert.IsTrue(allLines.Any(l => l.Contains("thread-9-msg-99")));
    }
}
