using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Typhon.Engine.Internals;

namespace Typhon.Engine.Tests.Runtime;

/// <summary>
/// EP-04 (#1532): a thread holds an epoch pin only for a bounded scope. A DagScheduler worker that ran a parallel (PointInTimeAccessor) QuerySystem chunk
/// used to keep one after the chunk, the system and the tick were over: parked between ticks it held <c>MinActiveEpoch</c> back, so under PS-01 every page
/// tagged since its pin stayed unevictable, and a tick that stalled on a page never saw one freed.
/// </summary>
[TestFixture]
class WorkerEpochPinTests : TestBase<WorkerEpochPinTests>
{
    private const int EntityCount = 256;
    private const int WorkerCount = 4;

    // "Typhon.Worker-0#12 slot 3 epoch 123 (5 behind) depth 2"
    private static readonly Regex PinEntry = new(
        @"^(?<name>.+?)#(?<tid>\d+)(?: \(dead\))? slot (?<slot>\d+) epoch (?<epoch>\d+) \((?<behind>-?\d+) behind\) depth (?<depth>\d+)$",
        RegexOptions.Compiled);

    private readonly record struct Pin(string Name, int ThreadId, long Epoch, int Depth);

    private static List<Pin> ParsePins(string describe)
    {
        var pins = new List<Pin>();
        var colon = describe.IndexOf("pinned: ", StringComparison.Ordinal);
        if (colon < 0)
        {
            return pins;
        }

        foreach (var part in describe.Substring(colon + "pinned: ".Length).Split("; ", StringSplitOptions.RemoveEmptyEntries))
        {
            var m = PinEntry.Match(part.Trim());
            if (m.Success)
            {
                pins.Add(new Pin(m.Groups["name"].Value, int.Parse(m.Groups["tid"].Value), long.Parse(m.Groups["epoch"].Value),
                    int.Parse(m.Groups["depth"].Value)));
            }
        }

        return pins;
    }

    private DatabaseEngine SetupEngine(out EntityId[] ids) => SetupEngine(out ids, EntityCount);

    private DatabaseEngine SetupEngine(out EntityId[] ids, int entityCount)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<EcsPosition>();
        dbe.RegisterComponentFromAccessor<EcsVelocity>();
        dbe.InitializeArchetypes();

        ids = new EntityId[entityCount];
        var pos = new EcsPosition(1, 2, 3);
        var vel = new EcsVelocity(0, 0, 0);
        for (var start = 0; start < entityCount; start += 2_000)
        {
            using var seedTx = dbe.CreateQuickTransaction();
            for (var i = start; i < Math.Min(entityCount, start + 2_000); i++)
            {
                ids[i] = seedTx.Spawn<EcsUnit>(EcsUnit.Position.Set(in pos), EcsUnit.Velocity.Set(in vel));
            }

            seedTx.Commit();
        }

        return dbe;
    }

    /// <summary>Waits for a tick to complete, so the caller runs inside the idle gap before the next one (BaseTickRate is low).</summary>
    private static long WaitForTickBoundary(TyphonRuntime runtime, long atLeast)
    {
        long n = 0;
        SpinWait.SpinUntil(() => (n = runtime.Scheduler.CurrentTickNumber) >= atLeast, TimeSpan.FromSeconds(5));
        Assert.That(n, Is.GreaterThanOrEqualTo(atLeast), "the runtime never reached the expected tick");

        // Now land just after a boundary: wait for the counter to move once more, which happens at the very end of a tick.
        var start = n;
        SpinWait.SpinUntil(() => (n = runtime.Scheduler.CurrentTickNumber) > start, TimeSpan.FromSeconds(5));
        return n;
    }

    private const string ParkedPinMarker = "parked worker thread(s) still pinned between ticks";
    private const string SlotHoldMarker = "pages still held by slot reference";

    private static TyphonRuntime CreateRuntime(DatabaseEngine dbe, ViewBase view)
    {
        void ReadAll(TickContext ctx)
        {
            foreach (var id in ctx.Entities)
            {
                var e = ctx.Accessor.Open(id);
                _ = e.Read(EcsUnit.Position);
            }
        }

        return TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Begin", _ => { });
            dag.QuerySystem("PA", ReadAll, input: () => view, parallel: true, after: "Begin");
            dag.QuerySystem("PB", ReadAll, input: () => view, parallel: true, after: "PA");
        }, new RuntimeOptions
        {
            WorkerCount = WorkerCount,
            BaseTickRate = 20,               // 50 ms period: an idle gap between ticks to observe parked workers in
            ParallelQueryMinChunkSize = 16,  // 256 / 4 workers = 64 entities per chunk, below EpochRefreshInterval (128): no in-chunk refresh
            CostBasedChunking = false
        });
    }

    /// <summary>
    /// Between ticks no worker runs a system, so no worker may hold an epoch pin. A parked pin freezes <c>MinActiveEpoch</c> while the rest of the
    /// engine keeps advancing the global epoch, and every page read in the meantime stays epoch-protected (PS-01).
    /// </summary>
    [Test]
    [CancelAfter(10_000)]
    [VerifiesRule("EP-04")]
    public void ParkedWorkers_HoldNoEpochPin_BetweenTicks() => ParkedWorkerScenario(pinWorkersPastTheirChunk: false);

    /// <summary>
    /// The <see cref="RuleMutantAttribute"/> companion: each worker keeps the standing pin its first chunk takes — the pre-#1532 worker accessor — and the
    /// verifier must see the parked pins.
    /// </summary>
    [Test]
    [CancelAfter(20_000)]
    [RuleMutant("EP-04")]
    public void ParkedWorkers_KeepingTheirFirstChunksPin_AreRejected() =>
        RuleMutants.AssertDetects("EP-04", ParkedPinMarker, () => ParkedWorkerScenario(pinWorkersPastTheirChunk: true));

    private void ParkedWorkerScenario(bool pinWorkersPastTheirChunk)
    {
        using var dbe = SetupEngine(out var ids);
        ViewBase view;
        using (var viewTx = dbe.CreateQuickTransaction())
        {
            view = viewTx.Query<EcsUnit>().ToView();
        }

        using var runtime = CreateRuntime(dbe, view);
        runtime.PinWorkersPastTheirChunkForTest = pinWorkersPastTheirChunk;
        runtime.Start();
        try
        {
            var em = dbe.EpochManager;
            Assert.That(em.IsCurrentThreadInScope, Is.False, "the test thread must hold no scope of its own");

            string describe = null;
            long globalAfter = 0;
            var measured = false;
            for (var attempt = 0; attempt < 5 && !measured; attempt++)
            {
                var tick = WaitForTickBoundary(runtime, 3);

                // Advance the global epoch from an unrelated thread, as any transaction does: a pin left behind falls further behind it.
                for (var i = 0; i < 20; i++)
                {
                    using var tx = dbe.CreateQuickTransaction();
                    _ = tx.Open(ids[i]).Read(EcsUnit.Position);
                }

                globalAfter = em.GlobalEpoch;
                describe = em.DescribePinnedThreads();

                // Only trust a measurement taken wholly inside one idle gap.
                measured = runtime.Scheduler.CurrentTickNumber == tick;
            }

            Assert.That(measured, Is.True, "could not take a measurement inside an idle gap between ticks");

            var workerPins = ParsePins(describe).FindAll(p => p.Name.StartsWith("Typhon.Worker-", StringComparison.Ordinal));
            var detail = string.Join(", ", workerPins.ConvertAll(p => $"{p.Name} epoch {p.Epoch} ({globalAfter - p.Epoch} behind) depth {p.Depth}"));

            // Workers only: the checkpoint, the timer or a cleanup may hold a short pin of their own at the instant of the measurement.
            Assert.That(workerPins, Is.Empty, $"{workerPins.Count} {ParkedPinMarker}: {detail}. Full registry: {describe}");
        }
        finally
        {
            runtime.Shutdown();
            view.Dispose();
        }
    }

    /// <summary>
    /// A worker accessor reads only under its lease: used after the lease ended, it throws rather than read pages nothing protects. A per-call guard would
    /// cover the entity-map lookup but not the reads after it (EP-04).
    /// </summary>
    [Test]
    [CancelAfter(5_000)]
    [VerifiesRule("EP-04")]
    public void APointInTimeWorker_UsedOutsideItsLease_Throws()
    {
        using var dbe = SetupEngine(out var ids);
        using var pta = PointInTimeAccessor.Create(dbe);
        EntityAccessor worker;
        using (var lease = pta.Enter(0))
        {
            worker = lease.Accessor;
            Assert.That(worker.Open(ids[0]).Read(EcsUnit.Position).X, Is.EqualTo(1f), "premise: the worker reads under its lease");
        }

        var open = Assert.Throws<InvalidOperationException>(() => worker.Open(ids[0]));
        Assert.That(open.Message, Does.Contain("outside its lease"));
        var alive = Assert.Throws<InvalidOperationException>(() => worker.IsAlive(ids[0]));
        Assert.That(alive.Message, Does.Contain("outside its lease"));
    }

    /// <summary>
    /// A lease pins its thread for its length and nothing after: the thread is in scope inside, out of scope after, and the cache's floor is back at the
    /// global epoch. A worker index may be leased from another thread in a later round (a <c>Parallel.For</c>), and that one leaves no pin either.
    /// </summary>
    [Test]
    [CancelAfter(5_000)]
    [VerifiesRule("EP-04")]
    public void ALease_PinsItsThreadForItsLengthOnly_AndAWorkerIndexMayMoveBetweenThreads()
    {
        using var dbe = SetupEngine(out var ids);
        var em = dbe.EpochManager;
        using var pta = PointInTimeAccessor.Create(dbe);
        using (var lease = pta.Enter(0))
        {
            Assert.That(em.IsCurrentThreadInScope, Is.True, "a lease pins its thread");
            _ = lease.Accessor.Open(ids[1]).Read(EcsUnit.Position);
        }

        Assert.That(em.IsCurrentThreadInScope, Is.False, "the lease's pin outlived it");
        Exception onOther = null;
        var other = new Thread(() =>
        {
            try
            {
                using var lease = pta.Enter(0);
                _ = lease.Accessor.Open(ids[2]).Read(EcsUnit.Position);
            }
            catch (Exception ex)
            {
                onOther = ex;
            }
        });
        other.Start();
        Assert.That(other.Join(TimeSpan.FromSeconds(3)), Is.True, "the other thread's lease never returned");
        Assert.That(onOther, Is.Null, $"a later round on another thread failed: {onOther}");

        var ours = ParsePins(em.DescribePinnedThreads()).FindAll(pin => pin.ThreadId == Environment.CurrentManagedThreadId
                                                                        || pin.ThreadId == other.ManagedThreadId);
        Assert.That(ours, Is.Empty, $"a pin outlived its lease: {em.DescribePinnedThreads()}");
    }

    /// <summary>
    /// Every entry point that hands out a walker over pages refuses a worker with no lease open, not only a resolve: a cluster enumerator or an archetype
    /// accessor would read pages for as long as it lives.
    /// </summary>
    [Test]
    [CancelAfter(5_000)]
    [VerifiesRule("EP-04")]
    public void APointInTimeWorker_HandsOutNoWalkerOutsideItsLease()
    {
        using var dbe = SetupEngine(out _);
        using var pta = PointInTimeAccessor.Create(dbe);
        EntityAccessor worker;
        using (var lease = pta.Enter(0))
        {
            worker = lease.Accessor;
        }

        Assert.Multiple(() =>
        {
            Assert.That(() => worker.For<EcsUnit>(), Throws.InvalidOperationException.With.Message.Contains("outside its lease"), "For");
            Assert.That(() => worker.GetClusterEnumerator<EcsUnit>().Dispose(), Throws.InvalidOperationException.With.Message.Contains("outside its lease"),
                "GetClusterEnumerator");
            Assert.That(() => worker.GetClusterEnumerator<EcsUnit>(0, 1).Dispose(), Throws.InvalidOperationException.With.Message.Contains("outside its lease"),
                "GetClusterEnumerator(start, end)");
        });
    }

    /// <summary>
    /// A lease is released however its body ends: an exception thrown inside it leaves no pin. Leases nest — two snapshots on one thread, released in
    /// reverse — and a transaction opened and closed inside a lease leaves it intact.
    /// </summary>
    [Test]
    [CancelAfter(5_000)]
    [VerifiesRule("EP-04")]
    public void ALease_IsReleasedOnAThrow_NestsWithAnother_AndHostsATransaction()
    {
        using var dbe = SetupEngine(out var ids);
        var em = dbe.EpochManager;
        using var first = PointInTimeAccessor.Create(dbe);
        using var second = PointInTimeAccessor.Create(dbe);

        Assert.Throws<InvalidTimeZoneException>(() =>
        {
            using var lease = first.Enter(0);
            _ = lease.Accessor.Open(ids[0]).Read(EcsUnit.Position);
            throw new InvalidTimeZoneException("the body threw");
        });
        Assert.That(em.IsCurrentThreadInScope, Is.False, "a lease whose body threw kept its pin");

        using (var outer = first.Enter(0))
        {
            using (var inner = second.Enter(0))
            {
                _ = inner.Accessor.Open(ids[1]).Read(EcsUnit.Position);
                using (var tx = dbe.CreateQuickTransaction())
                {
                    _ = tx.Open(ids[2]).Read(EcsUnit.Position);
                }

                _ = inner.Accessor.Open(ids[3]).Read(EcsUnit.Position);
            }

            Assert.That(em.IsCurrentThreadInScope, Is.True, "the outer lease lost its pin with the inner one");
            _ = outer.Accessor.Open(ids[4]).Read(EcsUnit.Position);
        }

        Assert.That(em.IsCurrentThreadInScope, Is.False, "nested leases left a pin");
    }

    /// <summary>Which segments the pages held by slot reference belong to, by segment kind: names the holder when a hold is found.</summary>
    private static string DescribeSlotReferencedPages(DatabaseEngine dbe)
    {
        var owner = new Dictionary<int, string>();
        foreach (var seg in dbe.EnumerateStorageSegments())
        {
            foreach (var fp in seg.Pages.Span)
            {
                owner[fp] = $"{seg.Kind}@{seg.RootPageIndex}";
            }
        }

        var byOwner = new SortedDictionary<string, int>();
        var mmf = dbe.MMF;
        for (var i = 0; i < mmf.CacheSlotCount; i++)
        {
            if (mmf.SlotRefCountOf(i) > 0)
            {
                var key = owner.TryGetValue(mmf.GetFilePageIndex(i), out var o) ? o : "?";
                byOwner[key] = byOwner.TryGetValue(key, out var n) ? n + 1 : 1;
            }
        }

        return string.Join(", ", byOwner.Select(kv => $"{kv.Key}: {kv.Value}"));
    }

    /// <summary>
    /// A worker holds no page by slot reference once its system is over either (EP-04): its accessors' windows are released when the system ends. Kept,
    /// the windows of every (parallel system × worker) held their pages unevictable while the runtime idled — a set that grows with the systems and the
    /// cores, not the data. Counted twice: by a system that runs right after the last parallel one, in the same tick (no timing involved), and inside the
    /// idle gap between two ticks.
    /// </summary>
    [Test]
    [CancelAfter(20_000)]
    [VerifiesRule("EP-04")]
    [Property("CacheSize", 64L * 1024 * 1024)]
    public void Workers_HoldNoPageBySlotReference_OnceTheirSystemsEnd()
    {
        const int systems = 6;
        using var dbe = SetupEngine(out _, 8_000);
        ViewBase view;
        using (var viewTx = dbe.CreateQuickTransaction())
        {
            view = viewTx.Query<EcsUnit>().ToView();
        }

        var before = dbe.MMF.CountPagesWithSlotReferences();
        // One tick's count and its owners, published together: the probe runs every tick, and the message must explain the number it sits beside.
        var probeLock = new object();
        var afterSystems = -1;
        string afterSystemsDetail = null;
        using var runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Begin", _ => { });
            var after = "Begin";
            for (var k = 0; k < systems; k++)
            {
                var name = $"P{k}";
                dag.QuerySystem(name, ctx =>
                {
                    foreach (var id in ctx.Entities)
                    {
                        var e = ctx.Accessor.Open(id);
                        _ = e.Read(EcsUnit.Position);
                        _ = e.Read(EcsUnit.Velocity);
                    }
                }, input: () => view, parallel: true, after: after);
                after = name;
            }

            // Runs once every parallel system's chunks and cleanup are done: what the workers still hold now, they hold while parked.
            dag.CallbackSystem("Probe", _ =>
            {
                var count = dbe.MMF.CountPagesWithSlotReferences();
                var detail = DescribeSlotReferencedPages(dbe);
                lock (probeLock)
                {
                    afterSystems = count;
                    afterSystemsDetail = detail;
                }
            }, after: after);
        }, new RuntimeOptions { WorkerCount = WorkerCount, BaseTickRate = 5, ParallelQueryMinChunkSize = 64, CostBasedChunking = false });
        runtime.Start();
        try
        {
            var idle = -1;
            var measured = false;
            for (var attempt = 0; attempt < 3 && !measured; attempt++)
            {
                var tick = WaitForTickBoundary(runtime, 2);
                idle = dbe.MMF.CountPagesWithSlotReferences();
                measured = runtime.Scheduler.CurrentTickNumber == tick;
            }

            Assert.That(measured, Is.True, "could not take a measurement inside an idle gap between ticks");
            int probed;
            string probedDetail;
            lock (probeLock)
            {
                probed = afterSystems;
                probedDetail = afterSystemsDetail;
            }

            Assert.That(probed, Is.GreaterThanOrEqualTo(0), "the probe system never ran: a parallel system failed and skipped it");
            TestContext.Out.WriteLine($"pages held by slot reference ({systems} parallel systems x {WorkerCount} workers): {before} before the first tick, "
                                      + $"{probed} after the systems, {idle} with the runtime idle");
            Assert.Multiple(() =>
            {
                Assert.That(probed, Is.LessThanOrEqualTo(before), $"{SlotHoldMarker} once the parallel systems ended: {probedDetail}");
                Assert.That(idle, Is.LessThanOrEqualTo(before), $"{SlotHoldMarker} with the runtime idle: {DescribeSlotReferencedPages(dbe)}");
            });
        }
        finally
        {
            runtime.Shutdown();
            view.Dispose();
        }
    }
}
