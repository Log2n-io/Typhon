using System;
using System.Collections.Concurrent;
using System.Threading;
using Typhon.Engine;

namespace SwgTatooine.Server;

/// <summary>
/// Writes to the world staged from a thread outside the tick, and applied inside it by <see cref="StagedWriteSystem"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists: a thread outside the DAG must never write a spatial archetype</b> (<c>rules/runtime-scheduling.md</c> EW-02). The checks
/// beside this demo used to park players with a quick transaction from the test thread while the server ticked, and at 50 Hz the fence caught
/// it: two pending migrations naming one source slot, CR-05, tick aborted. The engine was right; the harness was racing it.
/// </para>
/// <para>
/// <b>One side transaction per write</b>, so a write that throws rolls back alone and its exception goes back to the caller that staged it. It
/// must not reach the tick: under <see cref="SystemExceptionPolicy.AbortTickAndStop"/> a throw ends the server.
/// </para>
/// <para>Only a serving run drains the queue (<see cref="TatooineSim.WriteInTick"/>); a measured run has no outside caller.</para>
/// </remarks>
public sealed class StagedWrites
{
    private readonly ConcurrentQueue<Item> _queue = new();

    // The cause is captured on the staging thread and entered on the tick's: the request's trace id lives in its Activity, which the tick thread never
    // sees, so without it the write's engine work shows in the Workbench as uncaused (#1517).
    private sealed class Item(Action<Transaction, long> write, CausalId cause)
    {
        public readonly Action<Transaction, long> Write = write;
        public readonly CausalId Cause = cause;
        public readonly ManualResetEventSlim Done = new();
        public Exception Fault;
        public long Tick = -1;
    }

    /// <summary>Applies every staged write, each in its own side transaction. Called by <see cref="StagedWriteSystem"/>, never throws.</summary>
    /// <param name="ctx">The tick.</param>
    internal void Drain(TickContext ctx)
    {
        while (_queue.TryDequeue(out var item))
        {
            try
            {
                using var cause = CausalScope.Enter(item.Cause);
                using var tx = ctx.CreateSideTransaction(DurabilityMode.GroupCommit, CommitDiscipline.Commit);
                item.Write(tx, ctx.TickNumber);
                if (!tx.Commit())
                {
                    item.Fault = new InvalidOperationException($"a staged write did not commit on tick {ctx.TickNumber}");
                }
            }
            catch (Exception e)
            {
                // The staging caller's fault, handed back to it: see the remarks for why it must not reach the tick.
                item.Fault = e;
            }

            item.Tick = ctx.TickNumber;
            item.Done.Set();
        }
    }

    /// <summary>Stages a write and waits until a tick has applied it.</summary>
    /// <param name="write">The write, given the tick's side transaction (committed for it) and the tick number.</param>
    /// <param name="stopped">The reason no tick will ever come — an aborted tick — or <see langword="null"/> while the runtime runs.</param>
    /// <param name="timeout">How long to wait for a tick.</param>
    /// <returns>The tick that applied it.</returns>
    internal long Apply(Action<Transaction, long> write, Func<Exception> stopped, TimeSpan timeout)
    {
        var item = new Item(write, CausalScope.Capture());
        _queue.Enqueue(item);
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (!item.Done.Wait(20))
        {
            if (stopped() is { } reason)
            {
                throw new InvalidOperationException("the runtime stopped before it applied a staged write", reason);
            }

            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException($"no tick applied a staged write within {timeout.TotalSeconds:F0} s");
            }
        }

        item.Done.Dispose();
        if (item.Fault != null)
        {
            throw new InvalidOperationException($"a staged write failed on tick {item.Tick}: {item.Fault.Message}", item.Fault);
        }

        return item.Tick;
    }
}
