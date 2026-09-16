using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// #954 / SUB-09 — the cluster-drain hook under the PARALLEL fence, where the finalize head runs on the worker pool rather than on the tick driver.
/// </summary>
/// <remarks>
/// <para>
/// The sibling fixture drives a bare <c>DatabaseEngine</c> and calls <c>WriteTickFence</c> on the test thread, so everything — attaching blocks and draining
/// them — happens on one thread. That is the easy half. With <c>EnableParallelFence</c> the fence's per-archetype finalize work is dispatched
/// (<c>FenceExecSystem</c>), so the drain can run on a different thread from the one that populated the directory.
/// </para>
/// <para>
/// <b>This fixture exists to find out whether that is safe.</b> The pool, directory and identity allocator are single-threaded by contract and carry a
/// DEBUG owning-thread assert that adopts the first caller and rejects every other. If the fence drains on a worker while the directory was populated
/// elsewhere, that assert fires and the tick fails — which would mean either the guard is too strict or the hook is in the wrong place. Either way it is a
/// design answer, and it is invisible under the serial fence.
/// </para>
/// <para>
/// A fence phase that throws does not reach the caller by default — it is recorded in per-system telemetry and every later phase is skipped. Since #890 it
/// also reaches <c>UnhandledExceptionCallback</c>, which is what this fixture asserts on, because a silently skipped fence would otherwise look like a pass.
/// </para>
/// </remarks>
[TestFixture]
unsafe class ReplicationDrainHookParallelFenceTests : TestBase<ReplicationDrainHookParallelFenceTests>
{
    private const float CellSize = 100f;
    private const int EntityCount = 200;
    private const long AmpleBudget = 16L * 1024 * 1024;

    private static ClCohPos PointAt(float x, float y) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Mass = 1.0f };

    [Test]
    [Property("CacheSize", 64 * 1024 * 1024)]
    [CancelAfter(120_000)]
    public void TheDrainHookIsSafeWhenTheFenceRunsOnTheWorkerPool()
    {
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClCohPos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(1000f, 1000f), CellSize));
        dbe.InitializeArchetypes();

        var archetypeId = Archetype<ClCohUnit>.Metadata.ArchetypeId;
        var cs = dbe._archetypeStates[archetypeId].ClusterState;

        var registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "DrainHookParallelTests" });
        var allocator = new MemoryAllocator(registry, new MemoryAllocatorOptions { Name = "DrainHookParallelAllocator" });
        ArchetypeReplicationState replication = null;

        try
        {
            replication = new ArchetypeReplicationState("Creature", registry.Runtime, allocator,
                new ReplicationBlockLayout(cs.Layout.ClusterSize), new SubscriptionsOptions { StatePoolBudgetBytes = AmpleBudget });
            // AttachTo rather than a raw field assignment, so disposal detaches automatically.
            replication.AttachTo(cs);

            var spawned = new List<EntityId>(EntityCount);
            var ticks = 0;
            var blocksAttached = 0;
            var watchedAtPeak = 0;
            Exception fromDag = null;

            var runtime = TyphonRuntime.Create(dbe, schedule =>
            {
                var dag = schedule.PublicTrack.DeclareDag("DrainHook");
                dag.CallbackSystem("Drive", ctx =>
                {
                    var n = Interlocked.Increment(ref ticks);
                    try
                    {
                        switch (n)
                        {
                            case 1:
                                for (var i = 0; i < EntityCount; i++)
                                {
                                    spawned.Add(ctx.Transaction.Spawn<ClCohUnit>(ClCohUnit.Pos.Set(PointAt(5f + (i % 50), 5f + (i / 50)))));
                                }
                                break;

                            case 2:
                                // Tick 1's fence has run, so the clusters exist. Attaching here is deliberate: it happens on whichever thread the DAG
                                // callback runs on, which is not necessarily the thread the fence will later drain on.
                                var live = cs.ReadActiveClusterList(out var liveCount);
                                for (var i = 0; i < liveCount; i++)
                                {
                                    if (replication.TryAttachBlock(live[i], out _))
                                    {
                                        blocksAttached++;
                                    }
                                }
                                watchedAtPeak = replication.WatchedClusterCount;
                                break;

                            case 3:
                                foreach (var id in spawned)
                                {
                                    ctx.Transaction.Destroy(id);
                                }
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Interlocked.CompareExchange(ref fromDag, ex, null);
                    }
                });
            }, new RuntimeOptions { WorkerCount = 8, BaseTickRate = 100, EnableParallelFence = true });

            using (runtime)
            {
                Exception unhandled = null;
                runtime.Scheduler.UnhandledExceptionCallback = (_, _, ex) => Interlocked.CompareExchange(ref unhandled, ex, null);

                runtime.Start();
                // Several ticks past the destroy so the deferred drain and its finalize head both run.
                SpinWait.SpinUntil(() => Volatile.Read(ref ticks) >= 8, TimeSpan.FromSeconds(30));
                runtime.Shutdown();

                Assert.Multiple(() =>
                {
                    Assert.That(fromDag, Is.Null, $"the DAG callback threw: {fromDag}");
                    Assert.That(unhandled, Is.Null,
                        $"the parallel fence threw — if this is a ReplicationThreadAffinity violation, the drain ran on a different thread from the "
                        + $"attach and the single-threaded contract does not hold as written. Got: {unhandled}");
                    Assert.That(ticks, Is.GreaterThanOrEqualTo(8), "the runtime must have ticked past the destroy and its drain");
                });
            }

            cs.ReadActiveClusterList(out var liveAfter);

            Assert.Multiple(() =>
            {
                Assert.That(blocksAttached, Is.GreaterThan(0), "no block was ever attached, so this test proves nothing about releasing them");
                Assert.That(watchedAtPeak, Is.EqualTo(blocksAttached));
                Assert.That(liveAfter, Is.Zero, "every cluster should have drained");
                Assert.That(replication.WatchedClusterCount, Is.Zero, "the drain hook must have released every block under the parallel fence too");
                Assert.That(replication.Pool.FreeBlockCount, Is.EqualTo(replication.Pool.BlockCount), "every released block must be back on the free list");
                Assert.That(replication.DrainFaults, Is.Zero,
                    $"a drain release was refused or faulted under the parallel fence — this is where a concurrency-guard violation would show up rather "
                    + $"than as a thrown tick, since the hook swallows to protect the drain loop. Last: {replication.LastDrainFault}");
            });
        }
        finally
        {
            replication?.Dispose();
            allocator.Dispose();
            registry.Dispose();
        }
    }
}
