namespace Sextant.Service;

/// <summary>
/// The service's single-writer turn gate (issue #333). Waiting turns are granted one at a time, first-in first-out within
/// a lane, and a release hands the gate DIRECTLY to the next waiter (the gate never becomes free in between, so a
/// newcomer cannot barge in). There are two lanes: the normal lane (productions, control writes) and a PRIORITY lane for
/// the service's own maintenance writes. On release the priority lane goes first, so a maintenance write takes the next
/// turn after the current holder instead of queueing behind every production admitted earlier. Priority is bounded so
/// productions still progress: when a priority turn ends while a normal waiter is queued, that turn's WHOLE hold counts
/// toward <see cref="PriorityBurstBudget"/>; once the counted holds since the last normal turn reach it, the next turn
/// goes to the normal lane. A priority turn that ends with no normal waiter counts nothing.
/// </summary>
internal sealed class WriteGate
{
    /// <summary>How long consecutive priority turns may hold the gate while a normal waiter is queued.</summary>
    public static readonly TimeSpan DefaultPriorityBurstBudget = TimeSpan.FromSeconds(30);

    private readonly Lock _lock = new();
    private readonly LinkedList<Waiter> _normal = new();
    private readonly LinkedList<Waiter> _priority = new();
    private readonly TimeProvider _time;
    private bool _held;
    private bool _holderIsPriority;
    private DateTimeOffset _holderSince;
    private TimeSpan _priorityBurst;

    public WriteGate(TimeProvider? time = null, TimeSpan? priorityBurstBudget = null)
    {
        _time = time ?? TimeProvider.System;
        PriorityBurstBudget = priorityBurstBudget ?? DefaultPriorityBurstBudget;
    }

    public TimeSpan PriorityBurstBudget { get; }

    /// <summary>1 while the gate is free, 0 while a turn holds it (the <see cref="SemaphoreSlim"/> reading it replaces).</summary>
    public int CurrentCount
    {
        get
        {
            lock (_lock)
                return _held ? 0 : 1;
        }
    }

    /// <summary>Turns waiting in the priority lane.</summary>
    public int PriorityWaiters
    {
        get
        {
            lock (_lock)
                return _priority.Count;
        }
    }

    /// <summary>Turns waiting in the normal lane.</summary>
    public int NormalWaiters
    {
        get
        {
            lock (_lock)
                return _normal.Count;
        }
    }

    /// <summary>
    /// Queues a turn. The returned task completes when the caller owns the gate, which it must then <see cref="Release"/>
    /// exactly once. Cancelling the token removes a turn that is still queued; a turn already granted stays the caller's.
    /// </summary>
    public Task WaitAsync(CancellationToken cancellationToken = default, bool priority = false)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);

        Waiter waiter;
        lock (_lock)
        {
            if (!_held)
            {
                Take(priority);
                return Task.CompletedTask;
            }
            waiter = new Waiter(priority);
            waiter.Node = (priority ? _priority : _normal).AddLast(waiter);
            // Registered under the lock, so a grant (Release disposes it after taking the lock) can never run before it
            // is assigned. A token cancelled in the meantime runs Cancel inline (the lock is re-entrant).
            if (cancellationToken.CanBeCanceled)
            {
                waiter.Registration = cancellationToken.Register(() => Cancel(waiter));
                if (waiter.Node is null)
                    waiter.Registration.Dispose();
            }
        }
        return waiter.Turn.Task;
    }

    /// <summary>Blocks until the caller owns the gate (synchronous callers only).</summary>
    public void Wait(CancellationToken cancellationToken = default, bool priority = false) =>
        WaitAsync(cancellationToken, priority).GetAwaiter().GetResult();

    /// <summary>Ends the current turn and hands the gate to the next waiter, if any.</summary>
    public void Release()
    {
        Waiter? next;
        lock (_lock)
        {
            if (!_held)
                throw new SemaphoreFullException("The write gate was released more often than it was taken.");

            if (_holderIsPriority && _normal.Count > 0)
                _priorityBurst += _time.GetUtcNow() - _holderSince;

            next = PickNext();
            if (next is null)
            {
                _held = false;
                _holderIsPriority = false;
                _priorityBurst = TimeSpan.Zero;
            }
            else
            {
                next.Node!.List!.Remove(next.Node);
                next.Node = null;
                Take(next.Priority);
            }
        }
        if (next is not null)
        {
            next.Registration.Dispose();
            next.Turn.TrySetResult();
        }
    }

    // Caller holds _lock. Priority first, unless it has used up its burst while a normal waiter is queued.
    private Waiter? PickNext()
    {
        var normal = _normal.First?.Value;
        var priority = _priority.First?.Value;
        if (priority is null)
            return normal;
        if (normal is null || _priorityBurst < PriorityBurstBudget)
            return priority;
        return normal;
    }

    // Caller holds _lock.
    private void Take(bool priority)
    {
        _held = true;
        _holderIsPriority = priority;
        _holderSince = _time.GetUtcNow();
        if (!priority)
            _priorityBurst = TimeSpan.Zero;
    }

    private void Cancel(Waiter waiter)
    {
        lock (_lock)
        {
            if (waiter.Node is null)
                return;
            waiter.Node.List!.Remove(waiter.Node);
            waiter.Node = null;
        }
        waiter.Registration.Dispose();
        waiter.Turn.TrySetCanceled();
    }

    private sealed class Waiter(bool priority)
    {
        public bool Priority { get; } = priority;
        public TaskCompletionSource Turn { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<Waiter>? Node { get; set; }
        public CancellationTokenRegistration Registration { get; set; }
    }
}
