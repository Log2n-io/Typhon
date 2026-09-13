using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// Regression tests for #860 — <see cref="TickContext.WorkerId"/> was left at its default 0 on four of the six context construction sites, so every
/// worker claimed slot 0 and per-worker partitioning by <c>WorkerId</c> aliased instead of separating.
/// </summary>
/// <remarks>
/// The decisive assertion is the <b>thread ↔ worker bijection</b>: distinct OS threads must report distinct <c>WorkerId</c>s, and a given thread must
/// report the same one every time. Under the bug, N threads all reported 0, which the bijection catches without depending on how the scheduler happened
/// to distribute chunks. Range checks alone would NOT have caught it — 0 is in range.
/// </remarks>
[TestFixture]
class TickContextWorkerIdTests : TestBase<TickContextWorkerIdTests>
{
    private const int WorkerCount = 4;
    private const int MinChunkSize = 16;
    private const int EntityCount = 256;

    /// <summary>Bounded wait for the chunk rendezvous — the happy path releases immediately, this only caps a starved dispatch.</summary>
    private static readonly TimeSpan RendezvousTimeout = TimeSpan.FromMilliseconds(250);

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<EcsPosition>();
        dbe.RegisterComponentFromAccessor<EcsVelocity>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static void SpawnEntities(DatabaseEngine dbe, int count)
    {
        using var seedTx = dbe.CreateQuickTransaction();
        var pos = new EcsPosition(0, 0, 0);
        var vel = new EcsVelocity(0, 0, 0);
        for (var i = 0; i < count; i++)
        {
            seedTx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
        }

        seedTx.Commit();
    }

    private static RuntimeOptions Options() => new()
    {
        WorkerCount = WorkerCount,
        BaseTickRate = 1000,
        ParallelQueryMinChunkSize = MinChunkSize,

        // The entity rule: one chunk count for every tick, whatever the bodies cost. The cost rule would fold these empty bodies into one chunk from
        // the second dispatch on, and the tests below compare chunks across ticks.
        CostBasedChunking = false
    };

    /// <summary>One observation of a context as it reached user system code.</summary>
    private readonly record struct Observation(int WorkerId, int ThreadId, int ChunkIndex, int ChunkCount);

    /// <summary>
    /// Asserts every <c>WorkerId</c> is a usable slot AND that the thread ↔ worker mapping is one-to-one in both directions.
    /// </summary>
    private static void AssertBijection(IReadOnlyCollection<Observation> obs)
    {
        Assert.That(obs, Is.Not.Empty, "no contexts were observed — the system never ran");

        var workerToThread = new Dictionary<int, int>();
        var threadToWorker = new Dictionary<int, int>();

        foreach (var o in obs)
        {
            // [0, WorkerCount] inclusive: WorkerCount is the dispatcher (timer) thread's reserved slot.
            Assert.That(o.WorkerId, Is.InRange(0, WorkerCount), $"WorkerId {o.WorkerId} outside [0, {WorkerCount}]");

            if (workerToThread.TryGetValue(o.WorkerId, out var boundThread))
            {
                Assert.That(o.ThreadId, Is.EqualTo(boundThread), $"WorkerId {o.WorkerId} was reported by two different threads");
            }
            else
            {
                workerToThread[o.WorkerId] = o.ThreadId;
            }

            if (threadToWorker.TryGetValue(o.ThreadId, out var boundWorker))
            {
                Assert.That(o.WorkerId, Is.EqualTo(boundWorker), $"thread {o.ThreadId} reported both WorkerId {boundWorker} and {o.WorkerId}");
            }
            else
            {
                threadToWorker[o.ThreadId] = o.WorkerId;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Parallel QuerySystem — the path that motivated #860
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void ParallelQuery_ChunksReportDistinctWorkerIds()
    {
        using var dbe = SetupEngine();
        SpawnEntities(dbe, EntityCount);

        using var viewTx = dbe.CreateQuickTransaction();
        var view = viewTx.Query<EcsUnit>().ToView();

        var observations = new ConcurrentBag<Observation>();
        var ticksSeen = 0;

        // Rendezvous: every chunk of the first tick parks here until all of them have arrived, which forces them onto distinct worker threads.
        // Without it, one fast worker could sequentially drain every chunk and the test would prove nothing about separation.
        //
        // Counted with a plain Interlocked rather than a CountdownEvent sized from WorkerCount: the chunk count is a function of entity count,
        // ParallelQueryMinChunkSize and chunksPerWorker, so it only equals WorkerCount by coincidence of today's config. A CountdownEvent that runs
        // out of signals throws InvalidOperationException inside the system body, which the scheduler swallows into _systemFailed — a flake that
        // presents as "no contexts were observed" rather than as the sizing mistake it is.
        var pending = -1;
        var release = new ManualResetEventSlim(false);

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Tick", _ => Interlocked.Increment(ref ticksSeen));
            dag.QuerySystem("Parallel", ctx =>
            {
                observations.Add(new Observation(ctx.WorkerId, Environment.CurrentManagedThreadId, ctx.ChunkIndex, ctx.ChunkCount));

                // Volatile.Read of the chunk count: every chunk of a system carries the same value, and the first one to arrive publishes it.
                if (Volatile.Read(ref pending) < 0)
                {
                    Interlocked.CompareExchange(ref pending, ctx.ChunkCount, -1);
                }

                if (!release.IsSet && Interlocked.Decrement(ref pending) <= 0)
                {
                    release.Set();
                }
                else if (!release.IsSet)
                {
                    release.Wait(RendezvousTimeout);
                }
            }, input: () => view, parallel: true, after: "Tick");
        }, Options());

        try
        {
            runtime.Start();
            SpinWait.SpinUntil(() => Volatile.Read(ref ticksSeen) >= 2, TimeSpan.FromSeconds(5));
            runtime.Shutdown();
            view.Dispose();

            var obs = observations.ToArray();
            AssertBijection(obs);

            var distinctWorkers = new HashSet<int>();
            foreach (var o in obs)
            {
                distinctWorkers.Add(o.WorkerId);
            }

            // The regression: every chunk used to report 0 regardless of which thread ran it.
            Assert.That(distinctWorkers.Count, Is.GreaterThan(1),
                "all chunks reported the same WorkerId — contexts are not carrying the executing worker");
        }
        finally
        {
            // Unpark anything still at the rendezvous before disposing, so a failed assertion above cannot strand a worker.
            release.Set();
            release.Dispose();
        }
    }

    /// <param name="writesVersioned">
    /// Selects the dispatch path: <c>false</c> routes <c>OnParallelQueryChunk</c> to <c>ExecuteChunkWithAccessor</c>, <c>true</c> to the Versioned
    /// fallback <c>ExecuteChunkWithTransaction</c>. Both left <c>ChunkIndex</c>/<c>ChunkCount</c> at their defaults before #860, and the Versioned one
    /// left <c>WorkerId</c> at 0 as well — so both need covering, not just whichever one the default flags happen to pick.
    /// </param>
    [TestCase(false)]
    [TestCase(true)]
    public void ParallelQuery_ChunksReportTheirOwnChunkIndex(bool writesVersioned)
    {
        using var dbe = SetupEngine();
        SpawnEntities(dbe, EntityCount);

        using var viewTx = dbe.CreateQuickTransaction();
        var view = viewTx.Query<EcsUnit>().ToView();

        var observations = new ConcurrentBag<Observation>();
        var ticksSeen = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Tick", _ => Interlocked.Increment(ref ticksSeen));
            dag.QuerySystem("Parallel",
                ctx => observations.Add(new Observation(ctx.WorkerId, Environment.CurrentManagedThreadId, ctx.ChunkIndex, ctx.ChunkCount)),
                input: () => view, parallel: true, writesVersioned: writesVersioned, after: "Tick");
        }, Options());

        runtime.Start();
        SpinWait.SpinUntil(() => Volatile.Read(ref ticksSeen) >= 1, TimeSpan.FromSeconds(5));
        runtime.Shutdown();
        view.Dispose();

        var obs = observations.ToArray();
        Assert.That(obs, Is.Not.Empty);

        var chunkCount = obs[0].ChunkCount;
        Assert.That(chunkCount, Is.GreaterThan(1), "expected the parallel system to be split into several chunks");

        var seenChunks = new HashSet<int>();
        foreach (var o in obs)
        {
            Assert.That(o.ChunkCount, Is.EqualTo(chunkCount), "ChunkCount must be identical for every chunk of a system");
            Assert.That(o.ChunkIndex, Is.InRange(0, chunkCount - 1));
            seenChunks.Add(o.ChunkIndex);
        }

        // Fixed alongside #860: both parallel chunk paths left ChunkIndex/ChunkCount at their defaults, so every chunk claimed to be chunk 0 of 0.
        Assert.That(seenChunks.Count, Is.GreaterThan(1), "every chunk reported the same ChunkIndex");

        AssertBijection(obs);
    }

    // ═══════════════════════════════════════════════════════════════
    // Non-parallel dispatch — WorkerId must still be a real slot
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void NonParallelSystems_ReportAValidWorkerId()
    {
        using var dbe = SetupEngine();
        SpawnEntities(dbe, EntityCount);

        using var viewTx = dbe.CreateQuickTransaction();
        var view = viewTx.Query<EcsUnit>().ToView();

        var callbackObs = new ConcurrentBag<Observation>();
        var queryObs = new ConcurrentBag<Observation>();
        var ticksSeen = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Callback", ctx =>
            {
                callbackObs.Add(new Observation(ctx.WorkerId, Environment.CurrentManagedThreadId, ctx.ChunkIndex, ctx.ChunkCount));
                Interlocked.Increment(ref ticksSeen);
            });
            dag.QuerySystem("Query",
                ctx => queryObs.Add(new Observation(ctx.WorkerId, Environment.CurrentManagedThreadId, ctx.ChunkIndex, ctx.ChunkCount)),
                input: () => view, after: "Callback");
        }, Options());

        runtime.Start();
        SpinWait.SpinUntil(() => Volatile.Read(ref ticksSeen) >= 2, TimeSpan.FromSeconds(5));
        runtime.Shutdown();
        view.Dispose();

        AssertBijection(callbackObs.ToArray());
        AssertBijection(queryObs.ToArray());

        // ChunkCount is documented as "always 1" for non-chunked systems. It was left at the default 0, which makes the slicing formula the
        // ChunkedCallbackSystem docs recommend (start = ChunkIndex * len / ChunkCount) divide by zero on any single-invocation system.
        foreach (var o in callbackObs)
        {
            Assert.That(o.ChunkCount, Is.EqualTo(1), "a CallbackSystem is one chunk");
            Assert.That(o.ChunkIndex, Is.Zero);
        }

        foreach (var o in queryObs)
        {
            Assert.That(o.ChunkCount, Is.EqualTo(1), "a non-parallel QuerySystem is one chunk");
            Assert.That(o.ChunkIndex, Is.Zero);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Lifecycle hooks — no worker slot belongs to them
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void LifecycleHooks_CarryNonWorkerId()
    {
        using var dbe = SetupEngine();

        var firstTickWorkerId = int.MinValue;
        var shutdownWorkerId = int.MinValue;
        var ticksSeen = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Tick", _ => Interlocked.Increment(ref ticksSeen));
        }, Options());

        runtime.OnFirstTick += ctx => Volatile.Write(ref firstTickWorkerId, ctx.WorkerId);
        runtime.OnShutdown += ctx => Volatile.Write(ref shutdownWorkerId, ctx.WorkerId);

        runtime.Start();
        SpinWait.SpinUntil(() => Volatile.Read(ref ticksSeen) >= 1, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.Multiple(() =>
        {
            Assert.That(Volatile.Read(ref firstTickWorkerId), Is.EqualTo(TickContext.NonWorkerId),
                "OnFirstTick runs on the tick thread before workers wake — it must not claim a worker slot");
            Assert.That(Volatile.Read(ref shutdownWorkerId), Is.EqualTo(TickContext.NonWorkerId),
                "OnShutdown runs on the caller's thread — it must not claim a worker slot");
        });
    }

    [Test]
    public void NonWorkerId_IsNotAValidWorkerSlot()
    {
        // The sentinel is negative on purpose: indexing per-worker storage with it throws rather than silently aliasing worker 0.
        Assert.That(TickContext.NonWorkerId, Is.LessThan(0));
    }

    // ═══════════════════════════════════════════════════════════════
    // Dispatcher slot — a skipped root chains its successor inline on the timer thread
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public void SkippedRoot_InlineSuccessor_UsesTheDispatcherSlot()
    {
        using var dbe = SetupEngine();

        var observations = new ConcurrentBag<Observation>();
        var ticksSeen = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");

            // Root is skipped every tick, so OnSystemComplete chains "Successor" into ExecuteInline on the timer thread rather than dispatching it
            // to a worker. That path used to hand the system body a context with WorkerId defaulted to 0 — indistinguishable from worker 0.
            dag.CallbackSystem("SkippedRoot", _ => { }, shouldRun: () => false);
            dag.CallbackSystem("Successor", ctx =>
            {
                observations.Add(new Observation(ctx.WorkerId, Environment.CurrentManagedThreadId, ctx.ChunkIndex, ctx.ChunkCount));
                Interlocked.Increment(ref ticksSeen);
            }, after: "SkippedRoot");
        }, Options());

        runtime.Start();
        SpinWait.SpinUntil(() => Volatile.Read(ref ticksSeen) >= 2, TimeSpan.FromSeconds(5));
        var dispatcherSlot = runtime.Scheduler.DispatcherWorkerId;
        var slotCount = runtime.Scheduler.WorkerSlotCount;
        runtime.Shutdown();

        var obs = observations.ToArray();
        Assert.That(obs, Is.Not.Empty, "the successor of a skipped root must still run");
        foreach (var o in obs)
        {
            Assert.That(o.ChunkCount, Is.EqualTo(1), "an inline successor is one chunk");
        }

        Assert.Multiple(() =>
        {
            Assert.That(dispatcherSlot, Is.EqualTo(WorkerCount), "the dispatcher slot sits one past the last real worker");
            Assert.That(slotCount, Is.EqualTo(WorkerCount + 1), "per-worker arrays must be sized for the dispatcher slot too");
        });

        // Deterministic: MarkTrackRootsReady runs on the timer thread, sees the root skipped, and OnSystemComplete's fan-out takes the
        // `succ.Type == CallbackSystem` branch straight into ExecuteInline with the scheduler's internal workerId of -1.
        foreach (var o in obs)
        {
            Assert.That(o.WorkerId, Is.EqualTo(dispatcherSlot),
                "an inline successor of a skipped root runs on the timer thread and must report the dispatcher slot, not a worker slot");
        }

        AssertBijection(obs);
    }
}
