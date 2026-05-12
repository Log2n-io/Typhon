using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Threading;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// Behavioural tests for the per-system <see cref="SystemDefinition.ChunksPerWorker"/> oversubscription factor.
/// Verifies that <c>ComputeChunkCount</c> applies the multiplier on the worker-count cap.
/// </summary>
[NonParallelizable]
[TestFixture]
class ChunksPerWorkerTests : TestBase<ChunksPerWorkerTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup() => Archetype<EcsUnit>.Touch();

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<EcsPosition>();
        dbe.RegisterComponentFromAccessor<EcsVelocity>();
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>
    /// Spawns <paramref name="entityCount"/> EcsUnit entities, runs the parallel system once, and returns the resolved <c>TotalChunks</c>.
    /// </summary>
    private int RunAndGetTotalChunks(int entityCount, int workerCount, int minChunkSize, float chunksPerWorker)
    {
        using var dbe = SetupEngine();

        // Pre-spawn entities so the view has work for the parallel system.
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

        var ticksSeen = 0;
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.CallbackSystem("Tick", _ => Interlocked.Increment(ref ticksSeen));
            schedule.QuerySystem("Parallel", _ => { }, input: () => view, parallel: true,
                chunksPerWorker: chunksPerWorker, after: "Tick");
        }, new RuntimeOptions
        {
            WorkerCount = workerCount,
            BaseTickRate = 1000,
            ParallelQueryMinChunkSize = minChunkSize
        });

        runtime.Start();
        SpinWait.SpinUntil(() => ticksSeen >= 1, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

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

    [Test]
    public void Default_OneChunkPerWorker()
    {
        // 4 workers, minChunkSize=16, 256 entities → maxChunks = 16, worker-cap = 4 × 1.0 = 4 → result 4.
        var chunks = RunAndGetTotalChunks(entityCount: 256, workerCount: 4, minChunkSize: 16, chunksPerWorker: 1f);
        Assert.That(chunks, Is.EqualTo(4));
    }

    [Test]
    public void TwoX_DoublesChunks()
    {
        // 4 workers × 2.0 = 8 chunk cap, 256 entities / 16 = 16 maxChunks → result 8.
        var chunks = RunAndGetTotalChunks(entityCount: 256, workerCount: 4, minChunkSize: 16, chunksPerWorker: 2f);
        Assert.That(chunks, Is.EqualTo(8));
    }

    [Test]
    public void OnePointFiveX_RoundsToNearest()
    {
        // 4 workers × 1.5 = 6.0 → round → 6 chunks. 256 / 16 = 16 maxChunks → result 6.
        var chunks = RunAndGetTotalChunks(entityCount: 256, workerCount: 4, minChunkSize: 16, chunksPerWorker: 1.5f);
        Assert.That(chunks, Is.EqualTo(6));
    }

    [Test]
    public void EntityCountStillCaps()
    {
        // 4 workers × 2.0 = 8 worker-cap, but only 64 entities / 16 = 4 maxChunks → result 4.
        // Confirms oversubscription does NOT push past the minChunkSize-derived cap.
        var chunks = RunAndGetTotalChunks(entityCount: 64, workerCount: 4, minChunkSize: 16, chunksPerWorker: 2f);
        Assert.That(chunks, Is.EqualTo(4));
    }

    /// <summary>
    /// Regression: per-worker pools (<c>_partitionViews</c>, <c>_tierRangeViews</c>) are sized to <c>WorkerCount</c> but were
    /// historically indexed by <c>chunkIndex</c>. With oversubscription (chunks &gt; workers) that path threw
    /// <c>IndexOutOfRangeException</c> from worker threads — silently in some configurations — and entities were never visited.
    /// This test verifies that every spawned entity is actually iterated when <c>chunksPerWorker = 2</c>.
    /// </summary>
    [Test]
    public void Oversubscribed_AllEntitiesAreVisited()
    {
        const int entityCount = 256;
        const int workerCount = 4;
        const int minChunkSize = 16;

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

        var visited = 0;
        var ticksSeen = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            schedule.CallbackSystem("Tick", _ => Interlocked.Increment(ref ticksSeen));
            schedule.QuerySystem("VisitAll", ctx =>
            {
                foreach (var _ in ctx.Entities)
                {
                    Interlocked.Increment(ref visited);
                }
            }, input: () => view, parallel: true, chunksPerWorker: 2f, after: "Tick");
        }, new RuntimeOptions
        {
            WorkerCount = workerCount,
            BaseTickRate = 1000,
            ParallelQueryMinChunkSize = minChunkSize
        });

        runtime.Start();
        SpinWait.SpinUntil(() => ticksSeen >= 1, TimeSpan.FromSeconds(5));
        runtime.Shutdown();

        // 4 × 2.0 = 8 chunks (capped at 256 / 16 = 16), so totalChunks = 8.
        Assert.That(visited, Is.GreaterThanOrEqualTo(entityCount),
            $"All {entityCount} entities should be visited; got {visited}. " +
            "If lower, the per-worker view pool indexing collapsed under oversubscription.");
        view.Dispose();
    }
}
