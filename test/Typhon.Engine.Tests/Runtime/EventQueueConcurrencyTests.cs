using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// #859 / #861 — a parallel producer must not lose or duplicate events.
/// </summary>
/// <remarks>
/// The defect these cover is not hypothetical: <c>AntUpdateSystem</c> in the AntHill demo is declared <c>.Parallel().ChunksPerWorker(2f)</c> and
/// <c>.WritesEvents(...)</c> on three queues, and pushed straight into a single-producer buffer from every chunk worker — an unsynchronised
/// <c>_buffer[_count++]</c> across ~8 threads. The assertion that matters is <b>exact multiset equality</b> after a drain: a lost event and a
/// double-written slot both survive a count-only check under the old implementation, because two racing increments can produce the right total.
/// </remarks>
[TestFixture]
class EventQueueConcurrencyTests : TestBase<EventQueueConcurrencyTests>
{
    private const int WorkerCount = 4;
    private const int EntityCount = 512;
    private const int MinChunkSize = 16;

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

    [Test]
    [NonParallelizable]
    [VerifiesRule("EQ-01")]
    public void ParallelProducer_EveryEventArrivesExactlyOnce()
    {
        using var dbe = SetupEngine();
        SpawnEntities(dbe, EntityCount);

        using var viewTx = dbe.CreateQuickTransaction();
        var view = viewTx.Query<EcsUnit>().ToView();

        EventQueue<int> queue = null;
        var drained = new List<int>();
        var consumerRuns = 0;
        const int PerChunk = 250;

        // What the producer ACTUALLY did, recorded by the producer. Deriving the expected set from the drained count instead — the obvious way to write
        // this — makes total loss of the highest-numbered chunk invisible: 3 of 4 chunks arriving yields chunkCount = 3, and every assertion then passes
        // against a set that silently excludes the missing 250 events. That is exactly the whole-slot-loss case EQ-01 marks [silent].
        var pushedChunks = new ConcurrentDictionary<int, byte>();
        var pushFailures = 0;

        // Rendezvous: hold every chunk until all of them have arrived, so the chunks genuinely overlap. Without it the free-for-all chunk-grab loop can
        // hand every chunk to one worker, and a [fatal][silent] data-race rule gets certified by a fully serial run.
        var pending = -1;
        var release = new ManualResetEventSlim(false);

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            queue = dag.CreateEventQueue<int>("Race", capacity: 4096);

            dag.QuerySystem("ParallelProducer", ctx =>
            {
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
                    release.Wait(TimeSpan.FromMilliseconds(250));
                }

                var w = ctx.Writer(queue);
                var baseId = ctx.ChunkIndex * PerChunk;
                for (var i = 0; i < PerChunk; i++)
                {
                    // Never Assert inside a system body: DagScheduler catches every chunk exception into _systemFailed and the default Isolate policy
                    // never surfaces it, so an assertion here can NEVER fail the test. Record and assert on the test thread.
                    if (!w.Push(baseId + i))
                    {
                        Interlocked.Increment(ref pushFailures);
                    }
                }

                pushedChunks[ctx.ChunkIndex] = 1;
            }, input: () => view, parallel: true);

            dag.CallbackSystem("Consumer", _ =>
            {
                if (Volatile.Read(ref consumerRuns) > 0)
                {
                    return;
                }

                var buf = new int[queue.Count];
                var n = queue.Drain(buf);
                for (var i = 0; i < n; i++)
                {
                    drained.Add(buf[i]);
                }

                Interlocked.Increment(ref consumerRuns);
            }, after: "ParallelProducer");

            dag.Produces("ParallelProducer", queue);
            dag.Consumes("Consumer", queue);

            // The entity rule, so every tick runs the same chunks: the expected set is built from the chunk indices pushed in any tick, and only the
            // first tick is drained. Under the cost rule the first tick's rendezvous wait is measured as cost, a later tick gets more chunks, and their
            // events — never drained — would read as lost.
        }, new RuntimeOptions { WorkerCount = WorkerCount, BaseTickRate = 1000, ParallelQueryMinChunkSize = MinChunkSize, CostBasedChunking = false });

        try
        {
            runtime.Start();
            SpinWait.SpinUntil(() => Volatile.Read(ref consumerRuns) >= 1, TimeSpan.FromSeconds(5));
            runtime.Shutdown();
        }
        finally
        {
            release.Set();
            release.Dispose();
        }

        view.Dispose();

        Assert.That(consumerRuns, Is.GreaterThanOrEqualTo(1), "the consumer never ran");
        Assert.That(Volatile.Read(ref pushFailures), Is.Zero, "capacity was sized to make drops impossible here");
        Assert.That(pushedChunks, Has.Count.GreaterThan(1), "the producer was not split across chunks — the test would prove nothing");

        // Expected set built from what the producer recorded, so a whole chunk vanishing is a failure rather than a smaller expectation.
        var expected = new HashSet<int>();
        foreach (var chunk in pushedChunks.Keys)
        {
            for (var i = 0; i < PerChunk; i++)
            {
                expected.Add(chunk * PerChunk + i);
            }
        }

        var seen = new HashSet<int>();
        foreach (var v in drained)
        {
            Assert.That(seen.Add(v), Is.True, $"event {v} was delivered twice — a slot was written by two workers");
        }

        Assert.That(seen, Is.EquivalentTo(expected), "events were lost or invented");
        Assert.That(queue.OverflowCount, Is.Zero);
    }

    [Test]
    public void ParallelProducer_TelemetryCountsEveryWorkersPushes()
    {
        using var dbe = SetupEngine();
        SpawnEntities(dbe, EntityCount);

        using var viewTx = dbe.CreateQuickTransaction();
        var view = viewTx.Query<EcsUnit>().ToView();

        EventQueue<int> queue = null;
        var produced = 0u;
        var peak = 0u;
        var depth = 0;
        var sampled = 0;
        const int PerChunk = 100;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            queue = dag.CreateEventQueue<int>("Counted", capacity: 4096);

            dag.QuerySystem("ParallelProducer", ctx =>
            {
                var w = ctx.Writer(queue);
                for (var i = 0; i < PerChunk; i++)
                {
                    w.Push(i);
                }
            }, input: () => view, parallel: true);

            dag.CallbackSystem("Sampler", _ =>
            {
                if (Volatile.Read(ref sampled) > 0)
                {
                    return;
                }

                // Read the accumulators the QueueTickEnd wire record carries, folded across every worker slot.
                Volatile.Write(ref produced, queue.Produced);
                Volatile.Write(ref peak, queue.PeakDepth);
                Volatile.Write(ref depth, queue.Count);
                Interlocked.Increment(ref sampled);
            }, after: "ParallelProducer");

            dag.Produces("ParallelProducer", queue);
        }, new RuntimeOptions { WorkerCount = WorkerCount, BaseTickRate = 1000, ParallelQueryMinChunkSize = MinChunkSize });

        runtime.Start();
        SpinWait.SpinUntil(() => Volatile.Read(ref sampled) >= 1, TimeSpan.FromSeconds(5));
        runtime.Shutdown();
        view.Dispose();

        Assert.That(sampled, Is.GreaterThanOrEqualTo(1));
        var p = Volatile.Read(ref produced);

        Assert.Multiple(() =>
        {
            Assert.That(p % PerChunk, Is.Zero, "Produced must be a whole number of chunks — a partial count means pushes were lost");
            Assert.That(p, Is.GreaterThan(PerChunk), "more than one chunk should have contributed");
            Assert.That((uint)Volatile.Read(ref depth), Is.EqualTo(p), "nothing was drained, so depth must equal Produced");
            Assert.That(Volatile.Read(ref peak), Is.EqualTo(p), "PeakDepth folds every slot's contribution");
        });
    }

    [Test]
    [VerifiesRule("EQ-01")]
    public void LifecycleHookContext_CannotProduce()
    {
        using var dbe = SetupEngine();

        EventQueue<int> queue = null;
        Exception firstTickFailure = null;
        var ticks = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            queue = dag.CreateEventQueue<int>("Hook", capacity: 16);
            dag.CallbackSystem("Tick", _ => Interlocked.Increment(ref ticks));
        }, new RuntimeOptions { WorkerCount = WorkerCount, BaseTickRate = 1000 });

        runtime.OnFirstTick += ctx =>
        {
            // OnFirstTick carries NonWorkerId (#860): it owns no segment, so producing from it must fail loudly rather than alias worker 0.
            try
            {
                ctx.Writer(queue).Push(1);
            }
            catch (Exception ex)
            {
                Volatile.Write(ref firstTickFailure, ex);
            }
        };

        runtime.Start();
        SpinWait.SpinUntil(() => Volatile.Read(ref ticks) >= 1, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        Assert.That(Volatile.Read(ref firstTickFailure), Is.TypeOf<ArgumentOutOfRangeException>(),
            "a lifecycle hook has no worker slot and must not be able to produce events");
    }
}
