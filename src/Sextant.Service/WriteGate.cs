namespace Sextant.Service;

/// <summary>
/// The service's single-writer turn gate (issue #333). Waiting turns are granted one at a time, first-in first-out within
/// a lane, and a release hands the gate DIRECTLY to the next waiter (the gate never becomes free in between, so a
/// newcomer cannot barge in). Maintenance writes use a PRIORITY lane and take precedence over indexing. Priority is
/// bounded so productions still progress: when a priority turn ends while a lower-lane waiter is queued, that turn's
/// WHOLE hold counts toward <see cref="PriorityBurstBudget"/>; once the counted holds since the last normal turn reach
/// it, the next turn goes to a lower lane. A priority turn that ends with no lower-lane waiter counts nothing.
/// Between them sits an EXPEDITED lane (issue #403) for ensures that claim their repository's default branch: it goes
/// before the normal lane (feature branches). Both lanes are FIFO; after a bounded burst of expedited turns while
/// normal work waits, one normal turn runs.
/// </summary>
internal sealed class WriteGate
{
    /// <summary>How long consecutive priority turns may hold the gate while a normal waiter is queued.</summary>
    public static readonly TimeSpan DefaultPriorityBurstBudget = TimeSpan.FromSeconds(30);

    /// <summary>Consecutive default-branch turns allowed while a feature-branch turn waits.</summary>
    public const int DefaultExpeditedStreakLimit = 8;

    private readonly Lock _lock = new();
    private readonly LinkedList<Waiter> _normal = new();
    private readonly LinkedList<Waiter> _priority = new();
    private readonly LinkedList<Waiter> _expedited = new();
    private readonly TimeProvider _time;
    private bool _held;
    private bool _holderIsPriority;
    private bool _holderIsExpedited;
    private DateTimeOffset _holderSince;
    private TimeSpan _priorityBurst;
    private int _expeditedStreak;

    public WriteGate(TimeProvider? time = null, TimeSpan? priorityBurstBudget = null, int? expeditedStreakLimit = null)
    {
        _time = time ?? TimeProvider.System;
        PriorityBurstBudget = priorityBurstBudget ?? DefaultPriorityBurstBudget;
        ExpeditedStreakLimit = Math.Max(1, expeditedStreakLimit ?? DefaultExpeditedStreakLimit);
    }

    public TimeSpan PriorityBurstBudget { get; }
    public int ExpeditedStreakLimit { get; }

    /// <summary>Turns waiting in the expedited lane.</summary>
    public int ExpeditedWaiters
    {
        get
        {
            lock (_lock)
                return _expedited.Count;
        }
    }

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
    public Task WaitAsync(CancellationToken cancellationToken = default, bool priority = false, bool expedited = false)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);

        Waiter waiter;
        lock (_lock)
        {
            if (!_held)
            {
                Take(priority, expedited);
                return Task.CompletedTask;
            }
            waiter = new Waiter(priority, expedited);
            waiter.Node = (priority ? _priority : expedited ? _expedited : _normal).AddLast(waiter);
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
    public void Wait(CancellationToken cancellationToken = default, bool priority = false, bool expedited = false) =>
        WaitAsync(cancellationToken, priority, expedited).GetAwaiter().GetResult();

    /// <summary>Ends the current turn and hands the gate to the next waiter, if any.</summary>
    public void Release()
    {
        Waiter? next;
        lock (_lock)
        {
            if (!_held)
                throw new SemaphoreFullException("The write gate was released more often than it was taken.");

            if (_holderIsPriority && _normal.Count + _expedited.Count > 0)
                _priorityBurst += _time.GetUtcNow() - _holderSince;

            if (_normal.Count == 0)
                _expeditedStreak = 0;
            else if (_holderIsExpedited)
                _expeditedStreak++;

            next = PickNext();
            if (next is null)
            {
                _held = false;
                _holderIsPriority = false;
                _holderIsExpedited = false;
                _priorityBurst = TimeSpan.Zero;
                _expeditedStreak = 0;
            }
            else
            {
                next.Node!.List!.Remove(next.Node);
                next.Node = null;
                Take(next.Priority, next.Expedited);
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
        var lower = PickLower();
        var priority = _priority.First?.Value;
        if (priority is null)
            return lower;
        if (lower is null || _priorityBurst < PriorityBurstBudget)
            return priority;
        return lower;
    }

    private Waiter? PickLower()
    {
        var expedited = _expedited.First?.Value;
        var normal = _normal.First?.Value;
        if (normal is null)
            return expedited;
        if (expedited is null || _expeditedStreak >= ExpeditedStreakLimit)
            return normal;
        return expedited;
    }

    // Caller holds _lock.
    private void Take(bool priority, bool expedited = false)
    {
        _held = true;
        _holderIsPriority = priority;
        _holderIsExpedited = !priority && expedited;
        _holderSince = _time.GetUtcNow();
        if (priority)
            return;
        _priorityBurst = TimeSpan.Zero;
        if (!expedited)
            _expeditedStreak = 0;
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

    private sealed class Waiter(bool priority, bool expedited)
    {
        public bool Priority { get; } = priority;
        public bool Expedited { get; } = expedited;
        public TaskCompletionSource Turn { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<Waiter>? Node { get; set; }
        public CancellationTokenRegistration Registration { get; set; }
    }
}
