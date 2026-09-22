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
/// <c>AC-10.6</c> — intra-cell drift detection produces the same drifter set under the SERIAL fence and under the PARALLEL
/// fence at <c>W ∈ {1, 2, 8}</c> (#872 step 10).
/// </summary>
/// <remarks>
/// <para><b>Driven by a real <see cref="TyphonRuntime"/>, and that is the whole point of the fixture.</b>
/// <c>ClusterMigrationTests</c> records the trap in its own words: ablating the parallel EntityMap phase left 5 692 tests
/// green, because ~24 migration tests drive <c>WriteTickFence</c> — the SERIAL drain — and exactly one built a runtime.
/// Detection lives in <c>RecomputeDirtyClusterAabbsSlice</c>, which the serial fence reaches through a single whole-range
/// call and the parallel fence reaches through N slices on N workers. A test that asserted "the parallel result" while
/// driving <c>WriteTickFence</c> would be asserting about the serial path under another name, and would survive any
/// slicing bug at all.</para>
///
/// <para><b>Every arm is compared to <see cref="ClusterDriftOracle"/>, not to another arm.</b> Diffing serial against
/// parallel directly goes green whenever both are wrong the same way — and they share every line of detection, so that is
/// the LIKELY failure here, not a remote one. Routing both through an independent reference gives serial ≡ oracle ≡
/// parallel, which implies the equality the AC asks for and additionally pins the rule itself.</para>
///
/// <para><b>Two quantities, because the queue is not the drifter set.</b> The first version of this test compared the
/// pending queue against the oracle and failed 169 against 202 on every arm — including the serial one, which is how it
/// was clear the test and not the engine was wrong. The queue records what PLACEMENT homed, and 240 entities pack into
/// clusters of 64/64/64/48, so three of the four are full: a drifter whose only candidates are full is detected, counted,
/// and then has nowhere to go. That gap between <c>DriftersDetected</c> and <c>MigrationCount</c> is a documented signal,
/// not an error, so the assertions separate what it conflates.</para>
/// <para><i>Detection</i> is pinned exactly, by count, against the oracle. It is slice-local by construction — a cluster's
/// drifters are decided from its own freshly computed bound and its own entities, both produced by the slice that owns it —
/// so no correct slicing can change it and an incorrect one shows up immediately. <i>Placement</i> is pinned by set
/// containment: every source the queue names must be an entity the rule actually rejected. A slice that mixed up which
/// cluster it was walking would enqueue a slot that is not in the oracle's set, and containment catches that while
/// remaining true under a full destination cell.</para>
/// <para>Destinations are NOT compared across worker counts, and the reason is a real property of the design rather than a
/// gap in the test. <c>ChooseRelocationTarget</c> reads <c>ClusterAabbs</c> for candidate clusters while the AabbRefresh
/// phase is concurrently WRITING that array for the clusters other slices own, so a candidate's box may be read before or
/// after its own refresh and under <c>W &gt; 1</c> which one you get depends on scheduling. That is tolerable precisely
/// because a pinned destination is advisory: <c>MigrationRequest</c> documents it as a preference, execution validates it
/// against <c>ClusterCellMap</c> and re-checks occupancy at claim time, and the fallback is the ordinary first-fit claim.
/// Losing the race costs a slightly worse box, never a wrong one. Asserting destination equality would be asserting a
/// scheduling accident.</para>
///
/// <para><b>Why the population is moved before the runtime starts.</b> Moving from inside a system would put a transaction
/// on the tick thread and make the result depend on where in the tick that system was scheduled. Dirty bits set by a
/// commit survive until a fence consumes them, so a move committed before <c>Start()</c> is picked up by the first fence
/// the runtime runs — the same input the serial arm hands to <c>WriteTickFence</c> — and nothing moves for the rest of the
/// run.</para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class ClusterDriftParallelTests : TestBase<ClusterDriftParallelTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private const float CellSize = 100f;
    private const float WorldMax = 1000f;
    private const int EntityCount = 240;

    /// <summary>P4's default: the target region is a quarter of the cell, so 25 world units here.</summary>
    private const float TargetExtent = CellSize * 0.25f;

    /// <summary>The intra-cell dead zone: 5 world units at the default ratio.</summary>
    private const float DriftMargin = CellSize * 0.05f;

    /// <summary>The <c>workerCount</c> value that selects the serial <c>WriteTickFence</c> arm rather than a runtime.</summary>
    private const int SerialArm = 0;

    private static ClMigPos PointAt(float x, float y, int tag = 0) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Tag = tag };

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClMigPos>();
        dbe.RegisterComponentFromAccessor<ClMigScratch>();
        // #872 step 12 turned the repair path on by default, and these fixtures are about the DELTA path. Repair legitimately
        // preempts relocation — a cell it re-packs comes out tight, so the drift gate stops firing and the deliberately
        // distinct clusters this fixture builds are collapsed into a Morton packing before a single placement decision is
        // made. Three placement tests and one shrink test went red that way, all of them correctly. Pinning the budget to
        // zero scopes each fixture to the mechanism it is written to measure; it is not a workaround for a defect.
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(WorldMax, WorldMax), CellSize,
            reclusterBudgetMs: 0f,
            // Constant-mode target (step 14): the oracle defines the drifter set against the configured ratio.
            clusterTargetPackingSlack: 0f));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static ushort ArchetypeId => Archetype<ClMigUnit>.Metadata.ArchetypeId;

    private static ArchetypeClusterState ClusterStateOf(DatabaseEngine dbe) =>
        dbe._archetypeStates[ArchetypeId].ClusterState;

    /// <summary>
    /// The post-spread position of the entity at <paramref name="chunkId"/>/<paramref name="slot"/> — a pure function, not
    /// a sequential RNG.
    /// </summary>
    /// <remarks>
    /// A seeded <c>Random</c> pulled inside the enumeration loop would make each entity's destination depend on the ORDER
    /// clusters and slots were visited in, which is an implementation detail of the enumerator. Hashing the coordinates
    /// instead makes the input world a function of the layout alone, so every arm of this test spreads into the identical
    /// world however it iterates.
    /// </remarks>
    private static (float x, float y) SpreadPosition(int chunkId, int slot)
    {
        // Two decorrelated 32-bit mixes (splitmix-style finalisers) of one key, mapped into the interior of cell (0,0).
        uint h = (uint)((chunkId * 0x9E3779B1) ^ (slot * 0x85EBCA6B));
        h ^= h >> 15;
        h *= 0x2C1B3C6D;
        h ^= h >> 12;
        uint g = h * 0x27D4EB2F;
        g ^= g >> 15;

        return (4f + (h % 10_000) * (92f / 10_000f), 4f + (g % 10_000) * (92f / 10_000f));
    }

    private static unsafe void SpreadEveryEntity(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<ClMigUnit>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                ulong bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    int slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    var (x, y) = SpreadPosition(cluster.ChunkId, slot);
                    cluster.WriteSpatial(ClMigUnit.Pos, slot, PointAt(x, y));
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }
        tx.Commit();
    }

    private static void Spawn(DatabaseEngine dbe)
    {
        using var tx = dbe.CreateQuickTransaction();
        for (int i = 0; i < EntityCount; i++)
        {
            tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(40f, 40f, i)));
        }
        tx.Commit();
    }

    /// <summary>One tick's detection result: how many drifters the rule rejected, and which slots placement managed to queue.</summary>
    /// <remarks>
    /// Sampled as a PAIR, and from the same instant. Both halves are written by one AabbRefresh phase and consumed by the
    /// next tick's Prep and Migrate, so reading them together before a fence gives a coherent view; reading the counter on
    /// one tick and the queue on another would silently compare two different passes.
    /// </remarks>
    private readonly record struct Detection(int DriftersDetected, List<(int Chunk, int Slot)> Queued);

    /// <summary>The pending queue's source slots, sorted — an order-independent value <c>Is.SubsetOf</c> can compare.</summary>
    private static List<(int Chunk, int Slot)> SnapshotQueue(ArchetypeClusterState cs)
    {
        var list = new List<(int, int)>();
        var queue = cs.PendingMigrations;
        int count = cs.PendingMigrationCount;
        for (int i = 0; i < count && queue != null && i < queue.Length; i++)
        {
            list.Add((queue[i].SourceClusterChunkId, queue[i].SourceSlotIndex));
        }

        list.Sort(static (a, b) => a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : a.Item2.CompareTo(b.Item2));
        return list;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    [Test]
    [VerifiesRule("CR-03")]
    [CancelAfter(60_000)]
    public void DriftDetection_YieldsTheRulesDrifterSet_WhicheverFenceRunsIt([Values(SerialArm, 1, 2, 8)] int workerCount)
    {
        using var dbe = SetupEngine();
        Spawn(dbe);
        dbe.WriteTickFence(1);
        SpreadEveryEntity(dbe);

        // Evaluated BEFORE any fence consumes the spread. The world is quiescent here — the commit is done and no tick has
        // run — and it is the exact input every arm's first detecting fence will see, because nothing moves after this
        // point except the migrations detection itself provokes.
        var expected = ClusterDriftOracle.Evaluate(dbe, ClusterStateOf(dbe), TargetExtent, DriftMargin);

        Assert.That(expected.Drifters, Is.Not.Empty,
            "the population must actually produce drifters, or every arm compares two empty sets and passes with detection deleted");

        var actual = workerCount == SerialArm ? RunSerial(dbe) : RunParallel(dbe, workerCount);

        var arm = workerCount == SerialArm ? "the serial WriteTickFence" : $"the parallel fence at W={workerCount}";
        Assert.Multiple(() =>
        {
            Assert.That(actual.DriftersDetected, Is.EqualTo(expected.Drifters.Count),
                $"{arm} detected a different number of drifters than the rule defines");
            Assert.That(actual.Queued, Is.SubsetOf(expected.Drifters),
                $"{arm} queued a relocation for a slot the rule did not reject — a slice walked a cluster it does not own");
            Assert.That(actual.Queued, Is.Not.Empty, $"{arm} queued nothing, so the containment assertion above proves nothing");
        });
    }

    /// <summary>Serial arm: one fence, then read the queue it left behind.</summary>
    /// <remarks>
    /// The queue after this fence holds exactly what its own AabbRefresh filed. Tick 1 queued nothing, so the Finalize
    /// compaction had a zero-length prefix to drop and nothing from an earlier tick survives into the snapshot.
    /// </remarks>
    private static Detection RunSerial(DatabaseEngine dbe)
    {
        dbe.WriteTickFence(2);
        return new Detection(dbe.GetSpatialTelemetry(ArchetypeId).DriftersDetected, SnapshotQueue(ClusterStateOf(dbe)));
    }

    private static Detection RunParallel(DatabaseEngine dbe, int workerCount)
    {
        var ticks = 0;
        var snapshots = new List<Detection>();
        var cs = ClusterStateOf(dbe);

        using (var runtime = TyphonRuntime.Create(dbe, schedule =>
               {
                   schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Sample", _ =>
                   {
                       // The public track runs BEFORE the Engine-Post fence, so tick N samples what tick N-1's AabbRefresh
                       // filed — the queue as detection left it, and before this tick's Migrate phase drains it. Sampling
                       // after the fence would read an emptied queue and the comparison would be against nothing.
                       Interlocked.Increment(ref ticks);
                       var sample = new Detection(dbe.GetSpatialTelemetry(ArchetypeId).DriftersDetected, SnapshotQueue(cs));
                       lock (snapshots)
                       {
                           snapshots.Add(sample);
                       }
                   });
               }, new RuntimeOptions
               {
                   WorkerCount = workerCount,
                   // 100 Hz, not the 1000 Hz the neighbouring fixtures use. AC-4's parallel-fence differential measured tick
                   // overrun at 1000 Hz and at 200 Hz and none at 100 Hz; an overrunning tick disposes accessors under the
                   // next tick's feet and fails for reasons that have nothing to do with what is asserted here.
                   BaseTickRate = 100,
                   EnableParallelFence = true,
               }))
        {
            Exception unhandled = null;
            runtime.Scheduler.UnhandledExceptionCallback = (_, _, ex) => Interlocked.CompareExchange(ref unhandled, ex, null);

            runtime.Start();
            // Both observables, because the assertions below read both and the system callback runs INSIDE the tick: `ticks` reaching 5 says the fifth
            // tick started, while CurrentTickNumber counts ticks that FINISHED (it advances in ComputeAndRecordTelemetry). Waiting only on the counter and
            // then asserting on the clock is a race the test loses whenever the fence is slow — measured once in ~25 runs, always as "But was: 4".
            SpinWait.SpinUntil(() => Volatile.Read(ref ticks) >= 5 && runtime.CurrentTickNumber >= 5, TimeSpan.FromSeconds(15));
            runtime.Shutdown();

            // `unhandled` alone WAS not enough before #890: the scheduler's Prepare catch called RecordSystemFailure and
            // nothing else, so anything a fence phase threw while partitioning or merging never reached this callback. It does
            // now — an engine-track failure is surfaced from the failure funnel — and the clock assertion below is kept as the
            // independent observable it always was.
            Assert.That(unhandled, Is.Null, $"the parallel fence must not throw while detecting drifters. Got: {unhandled}");
            Assert.That(ticks, Is.GreaterThanOrEqualTo(5), "the runtime must actually have ticked, or nothing was measured");
            Assert.That(runtime.CurrentTickNumber, Is.GreaterThanOrEqualTo(5),
                "the runtime clock must have advanced — since #890 a phase that throws in Prepare also stops it, so a stalled clock is a failed fence");
        }

        // The FIRST snapshot that saw a detection is the first fence's. Taken by position rather than by tick number so the
        // test is indifferent to whether the runtime runs a warm-up fence, while still pinning the one pass that saw the
        // same input the oracle read: every later snapshot describes a world the migrations themselves produced.
        lock (snapshots)
        {
            foreach (var snapshot in snapshots)
            {
                if (snapshot.DriftersDetected > 0)
                {
                    return snapshot;
                }
            }
        }

        return new Detection(0, []);
    }
}
