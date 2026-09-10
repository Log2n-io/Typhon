using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Threading;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// Behavioural tests for the per-system <see cref="SystemDefinition.MinChunkSize"/> override.
/// </summary>
/// <remarks>
/// <para>The knob exists because the global <c>ParallelQueryMinChunkSize</c> is a bet that per-entity work is roughly
/// uniform across the schedule — it applies the same entity count to every system. Where that bet fails, the entity-count
/// cap rather than the worker cap becomes binding, and <see cref="SystemDefinition.ChunksPerWorker"/> — the knob
/// documented for exactly this problem — has no effect at all. <see cref="ChunksPerWorkerCannotLiftTheEntityCap"/> is the
/// test that pins that, because it is the reason the override was added rather than a second oversubscription factor.</para>
/// </remarks>
[TestFixture]
class MinChunkSizeTests : TestBase<MinChunkSizeTests>
{
    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<EcsPosition>();
        dbe.RegisterComponentFromAccessor<EcsVelocity>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>Spawn entities, run the parallel system once, and return the chunk count the runtime resolved.</summary>
    private int RunAndGetTotalChunks(int entityCount, int workerCount, int globalMinChunk, float chunksPerWorker, int systemMinChunk)
    {
        using var dbe = SetupEngine();

        using (var seedTx = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(0, 0, 0);
            var vel = new EcsVelocity(0, 0, 0);
            for (var i = 0; i < entityCount; i++)
            {
                seedTx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            }

            seedTx.Commit();
        }

        using var viewTx = dbe.CreateQuickTransaction();
        var view = viewTx.Query<EcsUnit>().ToView();

        var chunksRun = 0;
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Tick", _ => { });
            dag.QuerySystem("Parallel", _ => Interlocked.Increment(ref chunksRun), input: () => view, parallel: true,
                chunksPerWorker: chunksPerWorker, minChunkSize: systemMinChunk, after: "Tick");
        }, new RuntimeOptions
        {
            WorkerCount = workerCount,
            BaseTickRate = 1000,
            ParallelQueryMinChunkSize = globalMinChunk,
        });

        runtime.Start();

        // Wait on the SUBJECT, not on its predecessor: the system's prepare step writes TotalChunks before any of its chunks runs, and Shutdown() stops new
        // ticks without waiting for the one in flight. Waiting on "Tick" read the default of 1 whenever Shutdown truncated the tick between the two systems —
        // the pattern ChunksPerWorkerTests records failing reliably on a 3-core runner.
        SpinWait.SpinUntil(() => Volatile.Read(ref chunksRun) >= 1, TimeSpan.FromSeconds(5));
        runtime.Shutdown();
        Assert.That(Volatile.Read(ref chunksRun), Is.GreaterThanOrEqualTo(1), "the parallel system never ran, so its chunk count was never resolved");

        var parallelIdx = -1;
        for (var i = 0; i < runtime.Scheduler.SystemCount; i++)
        {
            if (runtime.Scheduler.Systems[i].Name == "Parallel")
            {
                parallelIdx = i;
                break;
            }
        }

        Assert.That(parallelIdx, Is.Not.EqualTo(-1), "Parallel system should be found in scheduler.");
        var totalChunks = runtime.Scheduler.Systems[parallelIdx].TotalChunks;
        view.Dispose();
        return totalChunks;
    }

    /// <summary>Zero means inherit, and inheriting must be bit-identical to not having the knob.</summary>
    [Test]
    public void Zero_InheritsTheGlobalFloor()
    {
        // 8 workers x 2.0 = 16 worker cap; 128 entities / 64 global floor = 2 maxChunks → 2.
        var chunks = RunAndGetTotalChunks(entityCount: 128, workerCount: 8, globalMinChunk: 64, chunksPerWorker: 2f, systemMinChunk: 0);
        Assert.That(chunks, Is.EqualTo(2));
    }

    /// <summary>
    /// The failure the override exists for: with the entity cap binding, the oversubscription factor is inert.
    /// </summary>
    /// <remarks>
    /// Both arms below ask for very different oversubscription and get the SAME two chunks, because
    /// <c>ceil(128 / 64) = 2</c> is smaller than either worker cap. A reader who reaches for <c>ChunksPerWorker</c> on a
    /// small-population system — which is what its own documentation suggests — measures nothing, and the reason is
    /// invisible from the outside.
    /// </remarks>
    [Test]
    public void ChunksPerWorkerCannotLiftTheEntityCap()
    {
        // The maximum factor the builder accepts, against an entity cap of ceil(128 / 64) = 2. If oversubscription could
        // lift that cap this would be 8 x 64 = 512 chunks; it is 2, and Zero_InheritsTheGlobalFloor shows a modest factor
        // gives the same 2. One RunAndGetTotalChunks per test: the fixture's provider hands out a single DatabaseEngine.
        var extreme = RunAndGetTotalChunks(entityCount: 128, workerCount: 8, globalMinChunk: 64, chunksPerWorker: 64f, systemMinChunk: 0);
        Assert.That(extreme, Is.EqualTo(2), "ChunksPerWorker must not be able to lift the entity-count cap.");
    }

    /// <summary>Lowering the floor for one system lifts that cap, and the worker cap governs again.</summary>
    [Test]
    public void Override_LiftsTheEntityCap()
    {
        // Same 128 entities and 8 workers, but a floor of 8: ceil(128 / 8) = 16 maxChunks, worker cap 8 x 2.0 = 16 → 16.
        var chunks = RunAndGetTotalChunks(entityCount: 128, workerCount: 8, globalMinChunk: 64, chunksPerWorker: 2f, systemMinChunk: 8);
        Assert.That(chunks, Is.EqualTo(16));
    }

    /// <summary>The worker cap still bounds it — the override lifts one term, it does not remove the other.</summary>
    [Test]
    public void WorkerCapStillBounds()
    {
        // Floor of 1 → 128 maxChunks, but 4 workers x 1.0 = 4 → 4.
        var chunks = RunAndGetTotalChunks(entityCount: 128, workerCount: 4, globalMinChunk: 64, chunksPerWorker: 1f, systemMinChunk: 1);
        Assert.That(chunks, Is.EqualTo(4));
    }

    /// <summary>A HIGHER floor than the global one coarsens that system, which is the other direction of the same knob.</summary>
    [Test]
    public void Override_CanAlsoCoarsen()
    {
        // 512 entities, global floor 8 would give 64 maxChunks; this system asks for 256-entity chunks → 2.
        var chunks = RunAndGetTotalChunks(entityCount: 512, workerCount: 8, globalMinChunk: 8, chunksPerWorker: 2f, systemMinChunk: 256);
        Assert.That(chunks, Is.EqualTo(2));
    }

    /// <summary>It is per SYSTEM: one system's override must not move another's chunking.</summary>
    [Test]
    public void OverrideIsScopedToOneSystem()
    {
        using var dbe = SetupEngine();

        using (var seedTx = dbe.CreateQuickTransaction())
        {
            var pos = new EcsPosition(0, 0, 0);
            var vel = new EcsVelocity(0, 0, 0);
            for (var i = 0; i < 128; i++)
            {
                seedTx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            }

            seedTx.Commit();
        }

        using var viewTx = dbe.CreateQuickTransaction();
        var view = viewTx.Query<EcsUnit>().ToView();

        var fineRuns = 0;
        var coarseRuns = 0;
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Tick", _ => { });
            dag.QuerySystem("Fine", _ => Interlocked.Increment(ref fineRuns), input: () => view, parallel: true, chunksPerWorker: 2f, minChunkSize: 8,
                after: "Tick");
            dag.QuerySystem("Coarse", _ => Interlocked.Increment(ref coarseRuns), input: () => view, parallel: true, chunksPerWorker: 2f, after: "Fine");
        }, new RuntimeOptions
        {
            WorkerCount = 8,
            BaseTickRate = 1000,
            ParallelQueryMinChunkSize = 64,
        });

        runtime.Start();

        // Both subjects, for the reason RunAndGetTotalChunks gives: a chunk of a system runs only after its prepare step has written TotalChunks.
        SpinWait.SpinUntil(() => Volatile.Read(ref fineRuns) >= 1 && Volatile.Read(ref coarseRuns) >= 1, TimeSpan.FromSeconds(5));
        runtime.Shutdown();
        Assert.That(Volatile.Read(ref coarseRuns), Is.GreaterThanOrEqualTo(1), "the second parallel system never ran, so its chunk count was never resolved");

        var fine = 0;
        var coarse = 0;
        for (var i = 0; i < runtime.Scheduler.SystemCount; i++)
        {
            var s = runtime.Scheduler.Systems[i];
            if (s.Name == "Fine")
            {
                fine = s.TotalChunks;
            }
            else if (s.Name == "Coarse")
            {
                coarse = s.TotalChunks;
            }
        }

        view.Dispose();
        Assert.Multiple(() =>
        {
            Assert.That(fine, Is.EqualTo(16), "The overriding system should be split by its own floor.");
            Assert.That(coarse, Is.EqualTo(2), "Its neighbour should still be split by the global floor.");
        });
    }

    /// <summary>
    /// A negative value is a mistake, not an inheritance signal, and a non-parallel system cannot use one at all.
    /// </summary>
    /// <remarks>
    /// Both assertions share one engine because the fixture's provider hands out a single <see cref="DatabaseEngine"/>.
    /// The parallel arm needs a real input view: "Parallel requires an Input View" is validated first and would
    /// otherwise be the exception under test.
    /// </remarks>
    [Test]
    public void InvalidValuesAreRejectedAtBuild()
    {
        using var dbe = SetupEngine();
        using var viewTx = dbe.CreateQuickTransaction();
        var view = viewTx.Query<EcsUnit>().ToView();

        var negative = Assert.Throws<InvalidOperationException>(() =>
        {
            using var runtime = TyphonRuntime.Create(dbe, schedule =>
            {
                schedule.PublicTrack.DeclareDag("Test")
                    .QuerySystem("Bad", _ => { }, input: () => view, parallel: true, minChunkSize: -1);
            }, new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 });
        });

        var serial = Assert.Throws<InvalidOperationException>(() =>
        {
            using var runtime = TyphonRuntime.Create(dbe, schedule =>
            {
                schedule.PublicTrack.DeclareDag("Test2")
                    .QuerySystem("Serial", _ => { }, input: () => view, parallel: false, minChunkSize: 8);
            }, new RuntimeOptions { WorkerCount = 2, BaseTickRate = 1000 });
        });

        view.Dispose();
        Assert.Multiple(() =>
        {
            Assert.That(negative.Message, Does.Contain("MinChunkSize"));
            Assert.That(serial.Message, Does.Contain("only meaningful for parallel"));
        });
    }
}
