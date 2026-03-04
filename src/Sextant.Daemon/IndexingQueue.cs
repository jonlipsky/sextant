using System.Threading.Channels;

namespace Sextant.Daemon;

public enum WorkPriority
{
    Immediate,
    Background
}

public sealed class WorkItem
{
    public required WorkPriority Priority { get; init; }
    public required IReadOnlyList<string> FilePaths { get; init; }
    public string? Description { get; init; }
}

public sealed class IndexingQueue
{
    private readonly Channel<WorkItem> _immediateChannel = Channel.CreateUnbounded<WorkItem>();
    private readonly Channel<WorkItem> _backgroundChannel = Channel.CreateUnbounded<WorkItem>();

    public int ImmediateCount { get; private set; }
    public int BackgroundCount { get; private set; }

    public void Enqueue(WorkItem item)
    {
        if (item.Priority == WorkPriority.Immediate)
        {
            _immediateChannel.Writer.TryWrite(item);
            Interlocked.Increment(ref _immediateCount);
        }
        else
        {
            _backgroundChannel.Writer.TryWrite(item);
            Interlocked.Increment(ref _backgroundCount);
        }
    }

    private int _immediateCount;
    private int _backgroundCount;

    public async Task<WorkItem?> DequeueAsync(CancellationToken ct)
    {
        // Try immediate first
        if (_immediateChannel.Reader.TryRead(out var immediate))
        {
            Interlocked.Decrement(ref _immediateCount);
            return immediate;
        }

        // Try background if no immediate
        if (_backgroundChannel.Reader.TryRead(out var background))
        {
            Interlocked.Decrement(ref _backgroundCount);
            return background;
        }

        // Wait for either channel
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var immediateTask = _immediateChannel.Reader.WaitToReadAsync(cts.Token).AsTask();
        var backgroundTask = _backgroundChannel.Reader.WaitToReadAsync(cts.Token).AsTask();

        await Task.WhenAny(immediateTask, backgroundTask);

        if (_immediateChannel.Reader.TryRead(out immediate))
        {
            Interlocked.Decrement(ref _immediateCount);
            return immediate;
        }

        if (_backgroundChannel.Reader.TryRead(out background))
        {
            Interlocked.Decrement(ref _backgroundCount);
            return background;
        }

        return null;
    }

    public void Complete()
    {
        _immediateChannel.Writer.TryComplete();
        _backgroundChannel.Writer.TryComplete();
    }

    public int GetImmediateCount() => Volatile.Read(ref _immediateCount);
    public int GetBackgroundCount() => Volatile.Read(ref _backgroundCount);
}
