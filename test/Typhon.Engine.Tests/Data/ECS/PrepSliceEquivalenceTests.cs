using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// #886 lead D. The tick fence's Prep phase runs as slices when an archetype is big enough: the serial head snapshots, N <c>PrepSlice</c> items mask, drain,
/// widen and detect over disjoint word ranges, and the serial tail concatenates the crossings in slice order before the throttle sees them. What this
/// fixture pins: the queue the throttle sees is BIT-IDENTICAL to the one the unsliced path builds (TH-01 admits the same first N), the work items cover
/// every dirty word exactly once, and the index and zone maps a sliced tick leaves behind agree with the data. Every arm — the serial
/// <c>WriteTickFence</c>, and the parallel fence at W = 1, 2 and 8 — starts from the same seeded world and applies the same writes.
/// </summary>
/// <remarks>
/// The membership of entities in clusters after Migrate is NOT compared across arms and must not be: the parallel Migrate phase places concurrently, so two
/// worker counts can legitimately leave an entity in different slots. That is why the queue is snapshotted from inside the fence, by
/// <see cref="ArchetypeClusterState.PrepQueueProbe"/>, on the tick whose input state is identical across arms — the first fence after the writes. Since
/// #910 the probe observes the prefix after the drain-order sort, so the order pinned is destination cell first and detector order inside one cell.
/// </remarks>
[TestFixture]
[NonParallelizable]
class PrepSliceEquivalenceTests : TestBase<PrepSliceEquivalenceTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private const float CellSize = 100f;
    private const int CellsPerSide = 16;
    private const float WorldMax = CellSize * CellsPerSide;
    private const int EntityCount = 16_384;            // 64 per cell, 256 cells: well past PrepSliceMinClusters at any placement
    private const int SerialArm = 0;

    private static ushort ArchetypeId => Archetype<ClMigUnit>.Metadata.ArchetypeId;

    private static ArchetypeClusterState ClusterStateOf(DatabaseEngine dbe) => dbe._archetypeStates[ArchetypeId].ClusterState;

    private static ClMigPos PointAt(float x, float y, int tag) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Tag = tag };

    /// <summary>One engine per DI scope: every arm gets a fresh in-memory world, and the arms can run inside one test.</summary>
    private static DatabaseEngine SetupEngine(IServiceScope scope)
    {
        var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClMigPos>();
        dbe.RegisterComponentFromAccessor<ClMigScratch>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(WorldMax, WorldMax), CellSize, reclusterBudgetMs: 0f));
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>Seeded, so every arm builds the same world: positions uniform over the grid, tags unique.</summary>
    private static EntityId[] Spawn(DatabaseEngine dbe)
    {
        var rng = new Random(886);
        var ids = new EntityId[EntityCount];
        const int batch = 2048;   // several commits, so no single transaction pins the whole world's pages under one epoch
        for (var start = 0; start < EntityCount; start += batch)
        {
            using var tx = dbe.CreateQuickTransaction();
            for (var i = start; i < Math.Min(EntityCount, start + batch); i++)
            {
                var x = (float)(rng.NextDouble() * (WorldMax - 2f)) + 1f;
                var y = (float)(rng.NextDouble() * (WorldMax - 2f)) + 1f;
                ids[i] = tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(x, y, i)));
            }

            tx.Commit();
        }

        return ids;
    }

    /// <summary>
    /// The same write set in every arm: a quarter of the entities, in a scrambled order, each moved by up to half a cell (many cross), and every other one
    /// of those also changes its indexed <c>Tag</c>, so the drain has both no-op entries and real B+Tree moves to replay.
    /// </summary>
    private static void ApplyWrites(Transaction tx, EntityId[] ids)
    {
        var rng = new Random(4242);
        var order = new int[EntityCount];
        for (var i = 0; i < EntityCount; i++)
        {
            order[i] = i;
        }

        for (var i = EntityCount - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        for (var k = 0; k < EntityCount / 4; k++)
        {
            var i = order[k];
            // Absolute values from the seed, never derived from what Write() hands back: the two arms write through different transaction kinds, and
            // whether the ref is a copy of the current value or a fresh staging buffer is not this fixture's business.
            var x = (float)(rng.NextDouble() * (WorldMax - 2f)) + 1f;
            var y = (float)(rng.NextDouble() * (WorldMax - 2f)) + 1f;
            ref var pos = ref tx.OpenMut(ids[i]).Write(ClMigUnit.Pos);
            pos.Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y };
            pos.Tag = (k & 1) == 0 ? EntityCount + k : i;
        }
    }

    private readonly record struct Request(int Chunk, int Slot, int Cell, long Entity);

    /// <summary>The one 4-byte indexed field of the archetype: <c>ClMigPos.Tag</c>.</summary>
    private static (int Slot, int Offset) TagField(ArchetypeClusterState state)
    {
        for (var s = 0; s < state.IndexSlots.Length; s++)
        {
            for (var f = 0; f < state.IndexSlots[s].Fields.Length; f++)
            {
                if (state.IndexSlots[s].Fields[f].FieldSize == sizeof(int))
                {
                    return (state.IndexSlots[s].Slot, state.IndexSlots[s].Fields[f].FieldOffset);
                }
            }
        }

        throw new InvalidOperationException("ClMigPos.Tag is not indexed");
    }

    private static List<(long Entity, int Cell)> Crossings(List<Request> queue)
    {
        var list = new List<(long, int)>(queue.Count);
        foreach (var r in queue)
        {
            list.Add((r.Entity, r.Cell));
        }

        return list;
    }

    /// <summary>
    /// The order <see cref="ArchetypeClusterState.PrepQueueProbe"/> observes since #910: ascending destination cell — the drain-order sort runs before the
    /// probe — and inside one cell ascending (cluster, slot), the order the detector appends in and the slices' crossings are concatenated in, which the
    /// sort, being stable, keeps.
    /// </summary>
    private static bool IsInDrainOrder(List<Request> queue)
    {
        for (var i = 1; i < queue.Count; i++)
        {
            var (a, b) = (queue[i - 1], queue[i]);
            if (b.Cell < a.Cell || (b.Cell == a.Cell && (b.Chunk < a.Chunk || (b.Chunk == a.Chunk && b.Slot <= a.Slot))))
            {
                return false;
            }
        }

        return true;
    }

    private sealed class Outcome
    {
        public List<Request> Queue = [];
        public int SlicesRun;
        public int MigrationsExecuted;
        public (int Jumps, int Largest, int Cells) Arrivals;
        public List<(int Start, int Count)> PrepItems = [];
        public List<int> DirtyWords = [];
    }

    private Outcome RunArmOn(int workerCount, bool checkZoneMaps = false)
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = SetupEngine(scope);
        var ids = Spawn(dbe);
        dbe.WriteTickFence(1);

        var outcome = new Outcome();
        var cs = ClusterStateOf(dbe);
        var captured = 0;
        ArchetypeClusterState.PrepQueueProbe = (state, _) =>
        {
            if (!ReferenceEquals(state, cs) || state.PendingMigrationDrainCount == 0 || Interlocked.Exchange(ref captured, 1) != 0)
            {
                return;
            }

            // The entity's TAG as well as (chunk, slot): two engines in one process neither place a seeded spawn into the same slots nor number entities
            // from the same id, so the queues of two arms are compared by WHICH entity (its unique tag) crosses to WHICH cell, and the ordering contract
            // is checked structurally within each arm.
            var accessor = state.ClusterSegment.CreateChunkAccessor();
            try
            {
                unsafe
                {
                    var layout = state.Layout;
                    var (tagSlot, tagOffset) = TagField(state);
                    var compOffset = layout.ComponentOffset(tagSlot);
                    var compSize = layout.ComponentSize(tagSlot);
                    for (var i = 0; i < state.PendingMigrationDrainCount; i++)
                    {
                        var r = state.PendingMigrations[i];
                        var tag = *(int*)(accessor.GetChunkAddress(r.SourceClusterChunkId) + compOffset + r.SourceSlotIndex * compSize + tagOffset);
                        outcome.Queue.Add(new Request(r.SourceClusterChunkId, r.SourceSlotIndex, r.DestCellKey, tag));
                    }

                    // The words the slices had to cover, read here — after the slices, before Migrate sets any destination bit.
                    var bits = state.FenceDirtyBits;
                    for (var w = 0; w < bits.Length; w++)
                    {
                        if (bits[w] != 0)
                        {
                            outcome.DirtyWords.Add(w);
                        }
                    }
                }
            }
            finally
            {
                accessor.Dispose();
            }
        };

        try
        {
            var slicesBefore = Volatile.Read(ref ArchetypeClusterState.PrepSlicesRun);
            if (workerCount == SerialArm)
            {
                using (var tx = dbe.CreateQuickTransaction())
                {
                    ApplyWrites(tx, ids);
                    tx.Commit();
                }

                dbe.WriteTickFence(2);
                var t = dbe.GetSpatialTelemetry(ArchetypeId);
                outcome.MigrationsExecuted = t.MigrationCount;
                outcome.Arrivals = (t.JumpCrossings, t.LargestArrivalRun, t.ArrivalCellsTouched);
            }
            else
            {
                RunParallel(dbe, workerCount, ids, outcome);
            }

            outcome.SlicesRun = Volatile.Read(ref ArchetypeClusterState.PrepSlicesRun) - slicesBefore;
        }
        finally
        {
            ArchetypeClusterState.PrepQueueProbe = null;
        }

        IndexDataOracle.AssertIndexAgreesWithData<ClMigUnit>(dbe, $"after the fence at W={workerCount}");
        if (checkZoneMaps)
        {
            AssertZoneMapsContainTheirData(dbe, workerCount);
        }

        return outcome;
    }

    private static void RunParallel(DatabaseEngine dbe, int workerCount, EntityId[] ids, Outcome outcome)
    {
        var ticks = 0;
        TyphonRuntime runtime = null;
        runtime = TyphonRuntime.Create(dbe, schedule =>
        {
            var dag = schedule.PublicTrack.DeclareDag("Test");
            dag.CallbackSystem("Sample", _ =>
            {
                var n = Interlocked.Increment(ref ticks);
                if (n == 2)
                {
                    // Tick 1's fence just ran: its Prep plan is still the one on the exec system, and its telemetry is the last tick's.
                    var t = dbe.GetSpatialTelemetry(ArchetypeId);
                    outcome.MigrationsExecuted = t.MigrationCount;
                    outcome.Arrivals = (t.JumpCrossings, t.LargestArrivalRun, t.ArrivalCellsTouched);
                    var plan = runtime.FencePrepExec.PlanForTest;
                    for (var i = 0; i < plan.ItemCount; i++)
                    {
                        ref var item = ref plan.Items[i];
                        if (item.Kind == FenceWorkKind.PrepSlice && item.TargetId == ArchetypeId)
                        {
                            outcome.PrepItems.Add((item.SliceStart, item.SliceCount));
                        }
                    }
                }
            });
            dag.CallbackSystem("Write", ctx =>
            {
                if (Volatile.Read(ref ticks) == 1)
                {
                    ApplyWrites(ctx.Transaction, ids);
                }
            }, after: "Sample");
        }, new RuntimeOptions { WorkerCount = workerCount, BaseTickRate = 100, EnableParallelFence = true });

        using (runtime)
        {
            Exception unhandled = null;
            runtime.Scheduler.UnhandledExceptionCallback = (_, _, ex) => Interlocked.CompareExchange(ref unhandled, ex, null);
            runtime.Start();
            SpinWait.SpinUntil(() => Volatile.Read(ref ticks) >= 3, TimeSpan.FromSeconds(30));
            runtime.Shutdown();
            Assert.That(unhandled, Is.Null, $"the parallel fence must not throw at W={workerCount}. Got: {unhandled}");
            Assert.That(ticks, Is.GreaterThanOrEqualTo(3), "the runtime must have ticked past the write tick and its fence");
        }
    }

    /// <summary>Widen-only zone maps may be wider than the data, never narrower: every occupant's key lies inside its cluster's bound.</summary>
    private static unsafe void AssertZoneMapsContainTheirData(DatabaseEngine dbe, int workerCount)
    {
        var cs = ClusterStateOf(dbe);
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = cs.ClusterSegment.CreateChunkAccessor();
        try
        {
            var layout = cs.Layout;
            for (var s = 0; s < cs.IndexSlots.Length; s++)
            {
                ref var ixSlot = ref cs.IndexSlots[s];
                var compOffset = layout.ComponentOffset(ixSlot.Slot);
                var compSize = layout.ComponentSize(ixSlot.Slot);
                for (var f = 0; f < ixSlot.Fields.Length; f++)
                {
                    ref var field = ref ixSlot.Fields[f];
                    if (field.ZoneMap == null || field.FieldSize != sizeof(int))
                    {
                        continue;
                    }

                    for (var c = 0; c < cs.ActiveClusterCount; c++)
                    {
                        var chunkId = cs.ActiveClusterIds[c];
                        var clusterBase = accessor.GetChunkAddress(chunkId);
                        var occupancy = *(ulong*)clusterBase;
                        if (occupancy == 0)
                        {
                            continue;
                        }

                        Assert.That(field.ZoneMap.TryGetBounds(chunkId, out var zmin, out var zmax), Is.True,
                            $"W={workerCount}: cluster {chunkId} holds entities but has no zone-map bound for slot {ixSlot.Slot} field {f}");
                        var keys = new List<(int slot, long key)>();
                        while (occupancy != 0)
                        {
                            var slot = BitOperations.TrailingZeroCount(occupancy);
                            occupancy &= occupancy - 1;
                            keys.Add((slot, *(int*)(clusterBase + compOffset + slot * compSize + field.FieldOffset)));
                        }

                        foreach (var (slot, key) in keys)
                        {
                            var k = key;
                            var sl = slot;
                            Assert.That(k, Is.InRange(zmin, zmax), () =>
                                $"W={workerCount}: cluster {chunkId} (cell {cs.ClusterCellMap[chunkId]}) slot {sl} holds key {k} outside its zone map "
                                + $"[{zmin}, {zmax}] — a false negative. Occupants: {string.Join(" ", keys)}");
                        }
                    }
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }
    }

    // Three methods rather than one [Values] case: TestBase reads the CacheSize property off TestContext.CurrentContext.Test, and a parameterised
    // test case does not carry its method's [Property] there — the 8 MiB default then times out the spawn's page-cache back-pressure after 5 s.

    /// <summary>At one worker the parallel fence keeps the atomic item — slicing starts at two — and must still match the serial fence.</summary>
    [Test]
    [CancelAfter(120_000)]
    [Property("CacheSize", 64 * 1024 * 1024)]   // 16 384 spatial entities and their index pages do not fit the 8 MiB default
    [VerifiesRule("CR-01")]
    public void ParallelFence_AtOneWorker_IsNotSliced_AndMatchesTheSerialFence() => AssertArmMatchesSerial(1, expectSlices: false);

    [Test]
    [CancelAfter(120_000)]
    [Property("CacheSize", 64 * 1024 * 1024)]
    [VerifiesRule("CR-01")]
    public void SlicedPrep_BuildsTheSameQueueAsTheUnslicedPath_AtTwoWorkers() => AssertArmMatchesSerial(2);

    /// <summary>Four workers, ~20 slices. Quarantined against #887 from 2026-09-04 to 2026-09-05, when the drain that caused it left the slices; see the
    /// W = 8 arm for the evidence.</summary>
    [Test]
    [CancelAfter(120_000)]
    [Property("CacheSize", 64 * 1024 * 1024)]
    [VerifiesRule("CR-01")]
    public void SlicedPrep_BuildsTheSameQueueAsTheUnslicedPath_AtFourWorkers() => AssertArmMatchesSerial(4);

    /// <summary>
    /// This arm is what found #887. It failed ~45 % of runs at W = 8 — entities the index no longer listed, leaves naming unoccupied slots — and the
    /// ablations located it exactly: 21 runs clean with Prep slicing off, 14 clean with only the shadow drain serialised. The defect was under the fence, in
    /// <c>BTree.MoveValue</c>'s pessimistic fallback (<c>BTreeMoveValueConcurrencyTests</c> reproduces both of its faces with no fence at all); the fence's
    /// part was merely calling it from W workers, which #886's slicing introduced. The drain was moved to one thread for a day, the tree was fixed, and the
    /// drain is back in the slices — 12 of 12 runs of this fixture clean on the fixed tree. Keep this arm un-quarantined: it is the fence-side guard for
    /// IXW-06, and if it reddens the tree's own verifier is the first thing to run.
    /// <para>
    /// <b>This CONTRADICTS what the doc here said before, and the contradiction is the point.</b> The 2026-09-04 note claimed the disagreement
    /// reproduced with slicing switched off, which is why #887 was filed as "not caused by #886". It does not: 21 clean runs say so. The earlier reading was
    /// taken while #890's defect was live — <c>FenceMigrateExecSystem.Prepare</c> was throwing on every dirty tick, so Index, EntityMap, AabbRefresh and
    /// Finalize were all being skipped as <c>DependencyFailed</c> and every arm was comparing against a fence that stopped half way. Measurements from that
    /// window are not evidence about anything else, which is the general lesson and the reason #890 was worth fixing first.
    /// </para>
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    [Property("CacheSize", 64 * 1024 * 1024)]
    [VerifiesRule("CR-01")]
    public void SlicedPrep_BuildsTheSameQueueAsTheUnslicedPath_AtEightWorkers()
        => AssertArmMatchesSerial(8);

    private void AssertArmMatchesSerial(int w, bool expectSlices = true)
    {
        // 16-word slices instead of the production 128: a 16 384-entity world yields only 2–3 slices at 128, which makes the ordering assertion below a
        // coin flip against a concatenate-in-completion-order defect. At 16 there are ~20, and the test is a test.
        var savedWords = FenceWorkPlan.PrepSliceWords;
        FenceWorkPlan.PrepSliceWords = 16;
        try
        {
            AssertArmMatchesSerialCore(w, expectSlices);
        }
        finally
        {
            FenceWorkPlan.PrepSliceWords = savedWords;
        }
    }


    /// <summary>
    /// Whether <see cref="ArchetypeClusterState.PrepSlicesRun"/> actually counts in this build. It is bumped by a <c>[Conditional("DEBUG")]</c> method —
    /// deliberately, so Release does not share one cache line across every engine and worker for a test counter — so in Release it stays at 0 however many
    /// slices run, and an assertion that reads it as proof of execution fails in Release and only in Release.
    /// </summary>
    /// <remarks>
    /// Asked by bumping it and looking, rather than by <c>#if DEBUG</c>, so this cannot drift from the engine's own attribute. Costs one increment at class
    /// init; every reader of the counter takes a delta, so the offset is invisible. What replaces the assertion in Release is the emitted work PLAN, which
    /// is real in both configurations: <c>PrepItems</c> comes from <c>FencePrepExec.PlanForTest</c> and is asserted non-empty on every sliced arm.
    /// </remarks>
    private static readonly bool SliceCounterCounts = ProbeSliceCounter();

    private static bool ProbeSliceCounter()
    {
        var before = Volatile.Read(ref ArchetypeClusterState.PrepSlicesRun);
        ArchetypeClusterState.NotePrepSliceRun();
        return Volatile.Read(ref ArchetypeClusterState.PrepSlicesRun) != before;
    }

    private void AssertArmMatchesSerialCore(int w, bool expectSlices)
    {
        var serial = RunArmOn(SerialArm);
        Assert.That(serial.Queue, Is.Not.Empty, "the write set must cross cells, or the queue comparison is vacuous");
        Assert.That(IsInDrainOrder(serial.Queue), Is.True,
            "the serial fence's prefix is in drain order: by destination cell, and in the detector's (cluster, slot) order inside one (#910)");
        Assert.That(serial.Arrivals.Jumps, Is.GreaterThan(0), "precondition: uniformly re-scattered entities jump, so the classification is exercised");
        Assert.That(serial.SlicesRun, Is.EqualTo(0), "the serial fence never slices");

        var arm = RunArmOn(w);
        Assert.Multiple(() =>
        {
            if (expectSlices)
            {
                // Release cannot count slices (see SliceCounterCounts); the PrepItems assertion below is the proof that survives, and it reads the plan the
                // fence actually executed rather than a counter.
                if (SliceCounterCounts)
                {
                    Assert.That(arm.SlicesRun, Is.GreaterThan(1), $"W={w}: the sliced path must actually have run — a world this size qualifies");
                }
            }
            else
            {
                Assert.That(arm.SlicesRun, Is.EqualTo(0), $"W={w}: one worker keeps the atomic item");
            }

            Assert.That(Crossings(arm.Queue), Is.EquivalentTo(Crossings(serial.Queue)),
                $"W={w}: the same entities must cross to the same cells whichever path detected them");
            Assert.That(IsInDrainOrder(arm.Queue), Is.True,
                $"W={w}: the prefix Migrate slices must be in drain order — ascending destination cell (#910's sort, which the slice planner carves on), "
                + "and inside one cell the order the detector appends in and the slices' crossings are concatenated in (TH-01 admits the first N)");
            Assert.That(arm.MigrationsExecuted, Is.EqualTo(serial.MigrationsExecuted), $"W={w}: same crossings, same migrations");
            Assert.That(arm.Arrivals, Is.EqualTo(serial.Arrivals),
                $"W={w}: the same crossings classify and group the same way whichever producer filed them — head drain, slice sink or serial scan");
            Assert.That(arm.PrepItems, expectSlices ? Is.Not.Empty : Is.Empty, $"W={w}: PrepSlice items emitted only when slicing is on");
        });

        if (!expectSlices)
        {
            return;
        }

        // AC-6: disjoint, ordered, and covering — every word that was dirty when the tail ran lies inside exactly one item.
        arm.PrepItems.Sort(static (x, y) => x.Start.CompareTo(y.Start));
        for (var i = 1; i < arm.PrepItems.Count; i++)
        {
            Assert.That(arm.PrepItems[i].Start, Is.GreaterThanOrEqualTo(arm.PrepItems[i - 1].Start + arm.PrepItems[i - 1].Count),
                $"W={w}: slices overlap at item {i}");
        }

        Assert.That(arm.DirtyWords, Is.Not.Empty, $"W={w}: sanity — the write tick left dirty words for the slices to cover");
        foreach (var word in arm.DirtyWords)
        {
            var covered = false;
            foreach (var (start, count) in arm.PrepItems)
            {
                if (word >= start && word < start + count)
                {
                    covered = true;
                    break;
                }
            }

            Assert.That(covered, Is.True, $"W={w}: dirty word {word} lies in no PrepSlice item");
        }
    }

    /// <summary>
    /// Widen-only zone maps may be wider than the data and never narrower, checked over every active cluster after a parallel fence that executed cell
    /// crossings. Quarantined against #888 from 2026-09-04 to 2026-09-05 and un-quarantined when that failure proved not to reproduce — 27 clean runs at
    /// <c>53771f51</c> and 5 at <c>a0cb5980</c>, the commit it was filed from, with this file byte-identical between the two. It stays in the suite as the
    /// guard that would reopen #888.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    [Property("CacheSize", 64 * 1024 * 1024)]
    public void ZoneMaps_ContainTheirData_AfterAParallelFenceWithMigrations() => RunArmOn(1, checkZoneMaps: true);

    /// <summary>A world below the threshold keeps the one-item-per-archetype path — no slices, same behaviour as before #886.</summary>
    [Test]
    public void SmallWorld_IsNotSliced()
    {
        using var scope = ServiceProvider.CreateScope();
        var dbe = SetupEngine(scope);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < 300; i++)
            {
                tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(10f + i, 10f, i)));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
        Assert.That(ClusterStateOf(dbe).ActiveClusterCount, Is.LessThan(DatabaseEngine.PrepSliceMinClusters), "sanity: the world is below the threshold");
        var before = Volatile.Read(ref ArchetypeClusterState.PrepSlicesRun);
        var ticks = 0;
        using (var runtime = TyphonRuntime.Create(dbe, schedule =>
               {
                   schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Tick", ctx =>
                   {
                       Interlocked.Increment(ref ticks);
                   });
               }, new RuntimeOptions { WorkerCount = 4, BaseTickRate = 100, EnableParallelFence = true }))
        {
            runtime.Start();
            SpinWait.SpinUntil(() => Volatile.Read(ref ticks) >= 3, TimeSpan.FromSeconds(15));
            runtime.Shutdown();
        }

        Assert.That(Volatile.Read(ref ArchetypeClusterState.PrepSlicesRun) - before, Is.EqualTo(0));
    }
}
