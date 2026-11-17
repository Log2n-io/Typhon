using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Typhon.Engine.Internals;

/// <summary>
/// The work handed to the tick by <see cref="TyphonRuntime.WriteInTick"/> / <see cref="TyphonRuntime.InTick{T}"/> (#1651): the legal alternative to an
/// off-tick write the guard refuses. Drained on the scheduler thread at tick start, before any system dispatches, so nothing runs concurrently with it.
/// Bounded twice: <see cref="RuntimeOptions.InTickQueueCapacity"/> items queued, <see cref="RuntimeOptions.InTickMaxItemsPerTick"/> run per tick.
/// </summary>
internal sealed class InTickQueue
{
    private abstract class Item
    {
        public CausalId Cause;
        public DurabilityMode Durability;

        /// <summary>The drain epoch when the item was queued: an item stamped with the running drain's epoch was queued during it.</summary>
        public long Epoch;

        /// <summary>Run the work in <paramref name="tx"/>, commit it, and complete the item's task with the result or the failed commit.</summary>
        public abstract void Run(Transaction tx, long tick);

        public abstract void Fail(Exception e);
    }

    private sealed class Item<T> : Item
    {
        public Func<Transaction, long, T> Work;

        // RunContinuationsAsynchronously: an awaiting caller's continuation must not run inline on the scheduler thread, inside the tick.
        public readonly TaskCompletionSource<T> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Run(Transaction tx, long tick)
        {
            var value = Work(tx, tick);
            if (tx.Commit())
            {
                Completion.TrySetResult(value);
            }
            else
            {
                Completion.TrySetException(new InvalidOperationException($"In-tick work did not commit on tick {tick}."));
            }
        }

        public override void Fail(Exception e) => Completion.TrySetException(e);
    }

    private readonly ConcurrentQueue<Item> _items = new();
    private readonly int _capacity;
    private readonly int _perTick;
    private int _count;
    private long _drainEpoch;   // written by the drain alone (the scheduler thread), read by every Enqueue
    private Exception _closed;

    public InTickQueue(int capacity, int perTick)
    {
        _capacity = Math.Max(1, capacity);
        _perTick = Math.Max(1, perTick);
    }

    /// <summary>Items waiting.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>Queue <paramref name="work"/>; the task faults at once when the queue is full or closed.</summary>
    public Task<T> Enqueue<T>(Func<Transaction, long, T> work, DurabilityMode durability)
    {
        if (Volatile.Read(ref _closed) is { } closed)
        {
            return Task.FromException<T>(Stopped(closed));
        }
        if (Interlocked.Increment(ref _count) > _capacity)
        {
            Interlocked.Decrement(ref _count);
            return Task.FromException<T>(new TickQueueFullException(_capacity));
        }
        var item = new Item<T> { Work = work, Durability = durability, Cause = CausalScope.Capture(), Epoch = Volatile.Read(ref _drainEpoch) };
        _items.Enqueue(item);
        if (Volatile.Read(ref _closed) is { } late)
        {
            FailAll(late);   // closed while we enqueued: Close's sweep may have missed this item
        }
        return item.Completion.Task;
    }

    /// <summary>
    /// Run the items queued when the drain starts, at most <see cref="RuntimeOptions.InTickMaxItemsPerTick"/>, each in its own runtime-owned transaction
    /// committed after the work returns. Never throws: a fault goes to the item's task, never to the tick.
    /// </summary>
    /// <remarks>
    /// Work queued during the drain waits for the next tick, which is what "at the start of a later tick" promises: an item's task completes inside this
    /// loop, and a caller that queues its next item as soon as the last completes would otherwise be served on the same tick, again and again — 26
    /// sequential calls all reporting one tick number. The drain opens an epoch, every item is stamped with the epoch current when it was queued, and the
    /// drain stops at the first item of its own epoch. The budget alone could not say it: the count is raised before the item lands in the queue, so a
    /// caller caught between the two lends its place to an item queued during the drain. The queue is FIFO, so the items run are the ones there first.
    /// </remarks>
    public void Drain(DatabaseEngine dbe, long tick)
    {
        var budget = Math.Min(_perTick, Volatile.Read(ref _count));
        if (budget <= 0)
        {
            return;
        }
        var epoch = _drainEpoch + 1;
        Interlocked.Exchange(ref _drainEpoch, epoch);   // full fence: published before any item of this drain runs and can queue another
        using var serial = DagThread.Serial();
        // Peek then dequeue: only Close's sweep dequeues concurrently, and an item it takes between the two is one this drain would have run or failed.
        for (var n = 0; n < budget && _items.TryPeek(out var next) && next.Epoch != epoch && _items.TryDequeue(out var item); n++)
        {
            Interlocked.Decrement(ref _count);
            try
            {
                using var cause = CausalScope.Enter(item.Cause);
                using var tx = dbe.CreateQuickTransaction(item.Durability);
                tx.MarkRuntimeOwned();
                item.Run(tx, tick);
            }
            catch (Exception e)
            {
                item.Fail(e);
            }
        }
    }

    /// <summary>No more work: every pending item and every later call faults with <paramref name="reason"/> as the inner exception (null: a plain stop).</summary>
    public void Close(Exception reason)
    {
        var closed = reason ?? new OperationCanceledException("The runtime stopped.");
        Interlocked.CompareExchange(ref _closed, closed, null);
        FailAll(Volatile.Read(ref _closed));
    }

    private void FailAll(Exception reason)
    {
        while (_items.TryDequeue(out var item))
        {
            Interlocked.Decrement(ref _count);
            item.Fail(Stopped(reason));
        }
    }

    private static InvalidOperationException Stopped(Exception reason) =>
        new("The runtime stopped, or a tick aborted it, before it ran this in-tick work.", reason is OperationCanceledException ? null : reason);
}
