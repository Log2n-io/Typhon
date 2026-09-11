using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

// ══════════════════════════════════════════════════════════════════════════
// #872 step 1 — the spatial-partitioning telemetry surface.
//
// Own archetype rather than a borrowed one: ArchetypeRegistry is process-global
// and unsynchronised across parallel fixtures (#720), so a fixture that shares
// another's archetype inherits its flakes.
// ══════════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.SpTel.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SpTelPos
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;
}

[Archetype]
partial class SpTelUnit : Archetype<SpTelUnit>
{
    public static readonly Comp<SpTelPos> Pos = Register<SpTelPos>();
}

// A SECOND spatial archetype, for the engine-wide fold alone (#911 O2). GetSpatialTelemetryTotal sums most members, MAXES one and takes a sample-weighted
// mean of two others — and with a single archetype in the engine all three arithmetics produce the same number, so a one-archetype fixture cannot tell a
// correct fold from a sum of everything.
[Component("Typhon.Test.SpTel.PosB", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct SpTelPosB
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;
}

[Archetype]
partial class SpTelUnitB : Archetype<SpTelUnitB>
{
    public static readonly Comp<SpTelPosB> Pos = Register<SpTelPosB>();
}

[TestFixture]
[NonParallelizable]
class SpatialMigrationTelemetryTests : TestBase<SpatialMigrationTelemetryTests>
{
    // 100-unit cells over a 1000x1000 world. The hysteresis margin is the default 5 % of cell size = 5 world units, so a
    // crossing landing < 5 units past a boundary is absorbed and one landing further is a migration. Several tests below
    // depend on exactly that split.
    private const float CellSize = 100f;
    private const float WorldMax = 1000f;

    private DatabaseEngine SetupEngineWithGrid()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<SpTelPos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(
            worldMin: new Vector2(0, 0),
            worldMax: new Vector2(WorldMax, WorldMax),
            cellSize: CellSize));
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static SpTelPos PointAt(float x, float y) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y } };

    private static EntityId Spawn(DatabaseEngine dbe, float x, float y)
    {
        using var tx = dbe.CreateQuickTransaction();
        var id = tx.Spawn<SpTelUnit>(SpTelUnit.Pos.Set(PointAt(x, y)));
        tx.Commit();
        return id;
    }

    private static void MoveTo(DatabaseEngine dbe, EntityId id, float x, float y)
    {
        using var tx = dbe.CreateQuickTransaction();
        var eref = tx.OpenMut(id);
        ref var pos = ref eref.Write(SpTelUnit.Pos);
        pos.Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y };
        tx.Commit();
    }

    private static int ArchetypeId => Archetype<SpTelUnit>.Metadata.ArchetypeId;

    /// <summary>
    /// Finds an entity's cluster slot, then moves it through <c>ClusterRef.WriteSpatial</c> — the barrier API. A test that
    /// declares <c>SetSpatialBarrierOnly</c> and then writes through <c>OpenMut</c>/<c>Write</c> has broken the contract it
    /// just declared: the fence skips its legacy scan on the promise that every spatial write goes through this path.
    /// </summary>
    private static unsafe void WriteSpatialTo(DatabaseEngine dbe, EntityId id, float x, float y)
    {
        var (chunkId, slot) = LocateSlot(dbe, id);
        Assert.That(chunkId, Is.GreaterThanOrEqualTo(0), "entity must be resident in a cluster before a barrier write");

        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<SpTelUnit>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                if (cluster.ChunkId != chunkId)
                {
                    continue;
                }

                cluster.WriteSpatial(SpTelUnit.Pos, slot, PointAt(x, y));
            }
        }
        finally
        {
            accessor.Dispose();
        }
        tx.Commit();
    }

    private static unsafe (int ChunkId, int Slot) LocateSlot(DatabaseEngine dbe, EntityId id)
    {
        var cs = dbe._archetypeStates[Archetype<SpTelUnit>.Metadata.ArchetypeId].ClusterState;
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = cs.ClusterSegment.CreateChunkAccessor();
        try
        {
            for (var i = 0; i < cs.ActiveClusterCount; i++)
            {
                var cid = cs.ActiveClusterIds[i];
                var clusterBase = accessor.GetChunkAddress(cid);
                var occupancy = *(ulong*)clusterBase;
                while (occupancy != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    if (*(long*)(clusterBase + cs.Layout.EntityIdsOffset + slot * 8) == (long)id.RawValue)
                    {
                        return (cid, slot);
                    }
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }
        return (-1, 0);
    }

    /// <summary>Collects one observable pass over the spatial instruments, keyed by instrument name.</summary>
    private static (Dictionary<string, long> Longs, Dictionary<string, double> Doubles) ScrapeSpatialInstruments(EcsMetricsExporter exporter)
    {
        var longs = new Dictionary<string, long>();
        var doubles = new Dictionary<string, double>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == EcsMetricsExporter.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) => longs[instrument.Name] = value);
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) => doubles[instrument.Name] = value);
        listener.Start();
        listener.RecordObservableInstruments();

        return (longs, doubles);
    }

    // ══════════════════════════════════════════════════════════════════════
    // AC-1.1 — the numbers appear, on both surfaces
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public void MigratingWorkload_PublishesNonZeroCounters()
    {
        using var dbe = SetupEngineWithGrid();
        var id = Spawn(dbe, 50f, 50f);

        MoveTo(dbe, id, 150f, 250f);   // two cells away — well past the hysteresis margin
        dbe.WriteTickFence(1);

        var t = dbe.GetSpatialTelemetry(ArchetypeId);
        Assert.Multiple(() =>
        {
            Assert.That(t.MigrationCount, Is.EqualTo(1), "one entity crossed a cell boundary, so one migration executed");
            Assert.That(t.TotalMigrations, Is.EqualTo(1), "the cumulative counter tracks the per-tick one on the first tick");
            Assert.That(t.ActiveClusterCount, Is.GreaterThan(0), "a migration implies at least one live cluster");
            Assert.That(t.MigrationExecuteMs, Is.GreaterThanOrEqualTo(0d).And.Not.NaN,
                "duration is measured, not derived — it may round to zero, never to NaN");
        });

        var total = dbe.GetSpatialTelemetryTotal();
        Assert.That(total.MigrationCount, Is.EqualTo(t.MigrationCount), "engine-wide total must include this archetype's migration");
    }

    [Test]
    public void MeterListener_ObservesSameValuesAsAccessor()
    {
        using var dbe = SetupEngineWithGrid();
        var id = Spawn(dbe, 50f, 50f);
        MoveTo(dbe, id, 150f, 250f);
        dbe.WriteTickFence(1);

        var expected = dbe.GetSpatialTelemetry(ArchetypeId);
        // Precondition, not decoration: without it every assertion below degenerates to 0 == 0 and the test passes just as
        // happily against an exporter that reports nothing at all.
        Assert.That(expected.MigrationCount, Is.EqualTo(1), "precondition: the accessor must have a non-zero value to agree ON");

        using var exporter = new EcsMetricsExporter(dbe);
        var (longs, doubles) = ScrapeSpatialInstruments(exporter);

        Assert.Multiple(() =>
        {
            Assert.That(longs, Does.ContainKey("typhon.ecs.spatial.migrations"), "the instrument must be published, not merely defined");
            Assert.That(longs["typhon.ecs.spatial.migrations"], Is.EqualTo(expected.MigrationCount), "OTel and the accessor read the same field");
            Assert.That(longs["typhon.ecs.spatial.migrations_total"], Is.EqualTo(expected.TotalMigrations));
            Assert.That(longs["typhon.ecs.spatial.active_clusters"], Is.EqualTo(expected.ActiveClusterCount));
            Assert.That(doubles, Does.ContainKey("typhon.ecs.spatial.migration_duration_ms"));
            Assert.That(doubles["typhon.ecs.spatial.migration_duration_ms"], Is.EqualTo(expected.MigrationExecuteMs).Within(1e-9));
        });
    }

    [Test]
    public void OpenRebuildTimings_AreReadableAndFinite()
    {
        // A freshly created database has no persisted clusters, so both rebuild passes are skipped and both figures are
        // legitimately zero. What this asserts is that they are READABLE and well-formed at all. The non-zero case needs a
        // reopen with data on disk, which this fixture's harness does not do — it is stated here rather than deferred to a
        // fixture that does not exist.
        using var dbe = SetupEngineWithGrid();

        Assert.Multiple(() =>
        {
            Assert.That(dbe.OpenCellStateRebuildMs, Is.GreaterThanOrEqualTo(0d).And.Not.NaN);
            Assert.That(dbe.OpenClusterAabbRebuildMs, Is.GreaterThanOrEqualTo(0d).And.Not.NaN);
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // AC-1.2 — zero, not stale
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public void PerTickCounters_ResetToZero_OnATickWithoutMigration()
    {
        using var dbe = SetupEngineWithGrid();
        var id = Spawn(dbe, 50f, 50f);

        MoveTo(dbe, id, 150f, 250f);
        dbe.WriteTickFence(1);
        Assert.That(dbe.GetSpatialTelemetry(ArchetypeId).MigrationCount, Is.EqualTo(1), "precondition: tick 1 migrated");

        dbe.WriteTickFence(2);   // nothing moved

        var t = dbe.GetSpatialTelemetry(ArchetypeId);
        Assert.Multiple(() =>
        {
            Assert.That(t.MigrationCount, Is.Zero, "a quiet tick migrated nothing");
            Assert.That(t.HysteresisAbsorbedCount, Is.Zero, "a quiet tick absorbed nothing");
            Assert.That(t.MigrationExecuteMs, Is.Zero, "a quiet tick spent no time migrating");
            Assert.That(t.TotalMigrations, Is.EqualTo(1), "the CUMULATIVE counter must not be reset — that is the whole point of having it");
        });
    }

    [Test]
    public void HysteresisAbsorbed_IsRecomputedEachTick_NotLatched()
    {
        // Note what "absorbed" means, because it is not what the name suggests: an entity parked inside the margin is
        // re-evaluated on EVERY tick, so the count is a standing condition rather than a one-shot event. Bringing it home
        // is what ends the absorption.
        //
        // This is NOT the regression test for the missing fence reset — detection still runs on both ticks here, so the
        // assignment inside DetectClusterMigrations would zero the counter with or without the fix. That case is
        // HysteresisAbsorbed_IsZeroed_WhenDetectionDoesNotRun below, and the distinction is worth keeping explicit.
        using var dbe = SetupEngineWithGrid();
        var id = Spawn(dbe, 50f, 50f);

        MoveTo(dbe, id, 103f, 50f);   // crosses x=100 but lands 3 units in — inside the 5-unit margin
        dbe.WriteTickFence(1);

        var absorbedTick = dbe.GetSpatialTelemetry(ArchetypeId);
        Assert.Multiple(() =>
        {
            Assert.That(absorbedTick.HysteresisAbsorbedCount, Is.EqualTo(1), "a crossing inside the margin is absorbed, not migrated");
            Assert.That(absorbedTick.MigrationCount, Is.Zero, "absorbed means no migration executed");
            Assert.That(absorbedTick.TotalHysteresisAbsorbed, Is.EqualTo(1));
        });

        MoveTo(dbe, id, 50f, 50f);    // back to the middle of its home cell — nothing left to absorb
        dbe.WriteTickFence(2);

        var homeTick = dbe.GetSpatialTelemetry(ArchetypeId);
        Assert.Multiple(() =>
        {
            Assert.That(homeTick.HysteresisAbsorbedCount, Is.Zero, "with the entity home, detection runs and finds nothing to absorb");
            Assert.That(homeTick.TotalHysteresisAbsorbed, Is.EqualTo(1), "the cumulative twin keeps the history the per-tick counter drops");
        });
    }

    [Test]
    public void HysteresisAbsorbed_IsZeroed_WhenDetectionDoesNotRun()
    {
        // The reachable staleness path, and the one that justifies the fence reset. PrepareArchetypeFence gates the
        // clean-bitmap spatial refresh — the branch that calls DetectClusterMigrations on an otherwise quiet tick — on
        // ActiveClusterCount > 0. Empty the archetype and detection stops running entirely, so nothing assigns the counter
        // and, without the fence-time reset, it reports the last tick that HAD entities: a live-looking reading of a
        // population that no longer exists. Ablating the reset reddens this test and nothing else.
        using var dbe = SetupEngineWithGrid();
        var id = Spawn(dbe, 50f, 50f);

        MoveTo(dbe, id, 103f, 50f);
        dbe.WriteTickFence(1);
        Assert.That(dbe.GetSpatialTelemetry(ArchetypeId).HysteresisAbsorbedCount, Is.EqualTo(1), "precondition: tick 1 absorbed a crossing");

        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Destroy(id);
            tx.Commit();
        }

        dbe.WriteTickFence(2);
        dbe.WriteTickFence(3);   // by now the cluster is gone and the detection branch is gated off

        var t = dbe.GetSpatialTelemetry(ArchetypeId);
        Assert.Multiple(() =>
        {
            Assert.That(t.ActiveClusterCount, Is.Zero, "precondition: the archetype is empty, so detection no longer runs");
            Assert.That(t.HysteresisAbsorbedCount, Is.Zero, "an empty archetype absorbs nothing — this must not still read the last populated tick");
            Assert.That(t.MigrationCount, Is.Zero);
            Assert.That(t.TotalHysteresisAbsorbed, Is.EqualTo(1), "the cumulative counter keeps what the per-tick one drops");
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // The barrier-only path — where the counter used to be a structural zero
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public void HysteresisAbsorbed_IsCounted_OnTheBarrierOnlyPath()
    {
        // DetectClusterMigrations only increments inside step (b), its legacy dirty-bits scan, and the SpatialBarrierOnly
        // branch returns before reaching it. Both demos opt into barrier-only, so the one number that tunes
        // MigrationHysteresisRatio read 0/N on precisely the path it was needed for. Counting now happens where the
        // decision is made — at write time, in ClusterRef.MaybeFlagMigration.
        using var dbe = SetupEngineWithGrid();
        dbe.SetSpatialBarrierOnly<SpTelUnit>();

        var id = Spawn(dbe, 50f, 50f);
        dbe.WriteTickFence(1);

        WriteSpatialTo(dbe, id, 103f, 50f);   // crosses x=100 but lands 3 units in — inside the 5-unit margin
        dbe.WriteTickFence(2);

        var t = dbe.GetSpatialTelemetry(ArchetypeId);
        Assert.Multiple(() =>
        {
            Assert.That(t.HysteresisAbsorbedCount, Is.EqualTo(1), "the barrier-only path must count what the margin swallowed");
            Assert.That(t.MigrationCount, Is.Zero, "absorbed means no migration executed");
            Assert.That(t.TotalHysteresisAbsorbed, Is.EqualTo(1), "and the cumulative twin must see it too");
        });
    }

    [Test]
    public void HysteresisAbsorbed_OnBarrierOnly_DoesNotCountAMoveThatStaysWellInsideTheCell()
    {
        // Guards the other half of the definition: "absorbed" is a crossing the margin swallowed, NOT any move that failed
        // to leave the cell. Without the raw-boundary re-test this would count every spatial write in the database.
        using var dbe = SetupEngineWithGrid();
        dbe.SetSpatialBarrierOnly<SpTelUnit>();

        var id = Spawn(dbe, 20f, 20f);
        dbe.WriteTickFence(1);

        WriteSpatialTo(dbe, id, 60f, 60f);   // moved 40 units, never approached a boundary
        dbe.WriteTickFence(2);

        Assert.That(dbe.GetSpatialTelemetry(ArchetypeId).HysteresisAbsorbedCount, Is.Zero,
            "a move that never crossed the raw cell boundary was not absorbed by anything");
    }

    // ══════════════════════════════════════════════════════════════════════
    // Adjacent bug found in review: the pending-migration queue pre-size
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public void PendingMigrationQueue_IsPreSizedFromThePreviousTick_NotFromTheZeroedCounter()
    {
        // PrepareArchetypeFence zeroes LastTickMigrationCount, and DetectClusterMigrations then pre-sized the pending queue
        // by reading that same field — a few hundred lines later in the SAME fence. The estimate was therefore always
        // Max(16, 0), so the queue regrew by doubling from 16 on every migration-heavy tick and the amortisation the
        // pre-size exists to provide never happened.
        //
        // TWO vacuity traps, both of which this test fell into before ablation caught them. (1) Left in place, the array is
        // ALREADY grown from the previous tick's doubling — 40 migrations leaves it at 64 — so the `Length < expected`
        // guard is false under both the fixed and the broken formula. (2) If the measured tick also migrates,
        // EnqueueMigration grows the array during that same tick and the final length is 64 whichever formula sized it.
        // Nulling the array AND choosing a non-migrating nudge is what makes the remaining length the sizing decision.
        using var dbe = SetupEngineWithGrid();
        var meta = Archetype<SpTelUnit>.Metadata;
        var clusterState = dbe._archetypeStates[meta.ArchetypeId].ClusterState;

        const int Population = 40;
        var ids = new EntityId[Population];
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < ids.Length; i++)
            {
                ids[i] = tx.Spawn<SpTelUnit>(SpTelUnit.Pos.Set(PointAt(50f, 50f)));
            }
            tx.Commit();
        }
        dbe.WriteTickFence(1);

        for (var i = 0; i < ids.Length; i++)
        {
            MoveTo(dbe, ids[i], 250f, 250f);
        }
        dbe.WriteTickFence(2);
        Assert.That(dbe.GetSpatialTelemetry(ArchetypeId).MigrationCount, Is.EqualTo(Population), "precondition: tick 2 migrated every entity");

        clusterState.PendingMigrations = null;

        // A nudge inside cell (2,2), which spans 200-300 on both axes — dirty writes, but no migration.
        for (var i = 0; i < ids.Length; i++)
        {
            MoveTo(dbe, ids[i], 260f, 260f);
        }
        dbe.WriteTickFence(3);
        Assert.That(dbe.GetSpatialTelemetry(ArchetypeId).MigrationCount, Is.Zero, "precondition: tick 3 must not migrate, or growth would mask the pre-size");

        const int ExpectedFloor = Population + (Population >> 2);   // the formula: prev + prev/4 = 50
        Assert.Multiple(() =>
        {
            Assert.That(clusterState.PreviousTickMigrationCount, Is.EqualTo(Population),
                "the snapshot must survive the reset that clears LastTickMigrationCount");
            Assert.That(clusterState.PendingMigrations, Is.Not.Null);
            Assert.That(clusterState.PendingMigrations.Length, Is.GreaterThanOrEqualTo(ExpectedFloor),
                $"the queue must be pre-sized for the previous tick's {Population} migrations, not left at the 16-entry floor");
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // AC-1.3 / AC-1.5 — reading costs nothing, degenerate inputs read zero
    // ══════════════════════════════════════════════════════════════════════

    [VerifiesRule("SO-01")]
    [Test]
    public void Accessor_AllocatesNothing()
    {
        using var dbe = SetupEngineWithGrid();
        Spawn(dbe, 50f, 50f);
        dbe.WriteTickFence(1);

        // ── Warm to STEADY STATE, then take the measurement best-of-N ──────────────────────────────────────────────
        //
        // 64 warmup iterations used to be enough and stopped being so: #911 grew both accessors (six new members, extra
        // reads, a second archetype loop), so tier-1 promotion now lands later, and under a loaded full-suite run the
        // JIT's background compilation could still be in flight when the measured loop started — one OSR transition
        // inside the window reported ~3.7 KB and failed a test whose subject allocates nothing at all. Measured: 1 of 3
        // full-suite runs red, 3 of 3 green fixture-scoped, which is the signature of a warmup race rather than a leak.
        //
        // The retry does NOT weaken the invariant, and that distinction is the point: an accessor that really allocated
        // would allocate on EVERY attempt, so requiring one clean pass still fails a genuine regression. What it tolerates
        // is a one-off tier-up landing inside the window — which is a property of the runtime, not of the code under test.
        // SO-01 names this test as its verifier, so a coin-flip here is a coin-flip on the rule.
        for (var i = 0; i < 20_000; i++)
        {
            _ = dbe.GetSpatialTelemetry(ArchetypeId);
            _ = dbe.GetSpatialTelemetryTotal();
        }

        var best = long.MaxValue;
        for (var attempt = 0; attempt < 5 && best != 0; attempt++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++)
            {
                _ = dbe.GetSpatialTelemetry(ArchetypeId);
                _ = dbe.GetSpatialTelemetryTotal();
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            if (allocated < best)
            {
                best = allocated;
            }
        }

        Assert.That(best, Is.Zero,
            "the accessors return a struct read from live fields — nothing on this path may allocate, on any of five attempts");
    }

    [Test]
    public void ZeroActiveClusters_ReadsZero_WithoutThrowing()
    {
        using var dbe = SetupEngineWithGrid();   // registered, initialised, nothing spawned
        dbe.WriteTickFence(1);

        var t = dbe.GetSpatialTelemetry(ArchetypeId);
        Assert.Multiple(() =>
        {
            Assert.That(t.ActiveClusterCount, Is.Zero);
            Assert.That(t.MigrationCount, Is.Zero);
            Assert.That(t.MigrationExecuteMs, Is.Zero.And.Not.NaN, "no clusters must not produce a 0/0");
            Assert.That(t.ReclusterBudgetUsedMs, Is.Zero.And.Not.NaN);
            Assert.That(dbe.GetSpatialTelemetryTotal().ActiveClusterCount, Is.Zero);
        });
    }

    [Test]
    public void OutOfRangeArchetypeId_ReturnsDefault()
    {
        using var dbe = SetupEngineWithGrid();

        Assert.Multiple(() =>
        {
            Assert.That(dbe.GetSpatialTelemetry(-1).MigrationCount, Is.Zero, "a negative id must not index the array");
            Assert.That(dbe.GetSpatialTelemetry(int.MaxValue).MigrationCount, Is.Zero, "an id past the end must not index the array");
            Assert.That(dbe.GetSpatialTelemetry(-1).ActiveClusterCount, Is.Zero);
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // AC-1.6 — the step 10/11 counters are wired and read zero
    // ══════════════════════════════════════════════════════════════════════

    [Test]
    public void FutureCounters_AreDeclaredAndPublishedAsZero()
    {
        // These have no producer yet. Declaring them now means steps 10 and 11 add a WRITER only — no new plumbing, no new
        // instrument, and no window in which the surface is half-built. They must be published AS ZERO, not absent: a
        // consumer that cannot see the series cannot tell "not built" from "the exporter is broken".
        using var dbe = SetupEngineWithGrid();
        var id = Spawn(dbe, 50f, 50f);
        MoveTo(dbe, id, 150f, 250f);
        dbe.WriteTickFence(1);

        var t = dbe.GetSpatialTelemetry(ArchetypeId);
        Assert.Multiple(() =>
        {
            Assert.That(t.ClustersScanned, Is.Zero, "no intra-cell drifter scan exists yet (step 10)");
            Assert.That(t.DriftersDetected, Is.Zero, "no drifter detection exists yet (step 10)");
            Assert.That(t.ReclusterBudgetUsedMs, Is.Zero, "no re-clustering budget exists yet (step 11)");
        });

        using var exporter = new EcsMetricsExporter(dbe);
        var (longs, doubles) = ScrapeSpatialInstruments(exporter);

        Assert.Multiple(() =>
        {
            Assert.That(longs, Does.ContainKey("typhon.ecs.spatial.clusters_scanned"));
            Assert.That(longs["typhon.ecs.spatial.clusters_scanned"], Is.Zero);
            Assert.That(longs, Does.ContainKey("typhon.ecs.spatial.drifters_detected"));
            Assert.That(longs["typhon.ecs.spatial.drifters_detected"], Is.Zero);
            Assert.That(doubles, Does.ContainKey("typhon.ecs.spatial.recluster_budget_ms"));
            Assert.That(doubles["typhon.ecs.spatial.recluster_budget_ms"], Is.Zero);
            Assert.That(doubles, Does.ContainKey("typhon.ecs.open.cellstate_rebuild_ms"));
            Assert.That(doubles, Does.ContainKey("typhon.ecs.open.cluster_aabb_rebuild_ms"));
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // #911 O2 — the counters that were computed every tick and published nowhere
    // ══════════════════════════════════════════════════════════════════════

    private static SpTelPos BoxAt(float minX, float minY, float maxX, float maxY) =>
        new() { Bounds = new AABB2F { MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY } };

    private static EntityId SpawnBox(DatabaseEngine dbe, float minX, float minY, float maxX, float maxY)
    {
        using var tx = dbe.CreateQuickTransaction();
        var id = tx.Spawn<SpTelUnit>(SpTelUnit.Pos.Set(BoxAt(minX, minY, maxX, maxY)));
        tx.Commit();
        return id;
    }

    /// <summary>Slots one of this archetype's clusters holds — the numerator of the packing bound, read rather than assumed.</summary>
    private static int SlotsPerCluster(DatabaseEngine dbe) =>
        BitOperations.PopCount(dbe._archetypeStates[ArchetypeId].ClusterState.Layout.FullMask);

    [Test]
    public void Tightness_IsPublished_ForTheClustersTheFenceActuallyWrote()
    {
        using var dbe = SetupEngineWithGrid();

        // Three points in cell (0,0), well inside it: one cluster, and no crossing to muddy the reading.
        Spawn(dbe, 10f, 10f);
        Spawn(dbe, 20f, 10f);
        var far = Spawn(dbe, 30f, 10f);
        dbe.WriteTickFence(1);

        // A barrier write sets the cluster's process bit, which is what makes the refresh look at it. Still inside cell (0,0),
        // so the extent grows from 20 to 50 world units without a migration. The RIGHTMOST point is the one that moves — moving
        // the leftmost would shrink the box from the other side and measure 0.4, which is a different (and less obvious) claim.
        WriteSpatialTo(dbe, far, 60f, 10f);
        dbe.WriteTickFence(2);

        var t = dbe.GetSpatialTelemetry(ArchetypeId);
        Assert.Multiple(() =>
        {
            Assert.That(t.TightnessSampleCount, Is.EqualTo(1), "exactly one cluster was written, so exactly one contributed a reading");
            Assert.That(t.MeanClusterExtentRatio, Is.EqualTo(0.5d).Within(1e-5),
                "the box spans 10..60 of a 100-unit cell — the extent ratio is the measured half of tightness");
            Assert.That(t.MeanPackingBound, Is.EqualTo(1d).Within(1e-6),
                "three entities fit one cluster, so the bound IS the cell — geometry, not tuning");
            Assert.That(t.MeanTightnessToBound, Is.EqualTo(0.5d).Within(1e-5), "measured extent against what geometry allows");
        });
    }

    [VerifiesRule("SO-01")]
    [Test]
    public void Tightness_ReportsNoSamples_RatherThanAStaleMean_OnAQuietTick()
    {
        using var dbe = SetupEngineWithGrid();
        var a = Spawn(dbe, 10f, 10f);
        Spawn(dbe, 30f, 10f);
        dbe.WriteTickFence(1);
        WriteSpatialTo(dbe, a, 60f, 10f);
        dbe.WriteTickFence(2);
        Assert.That(dbe.GetSpatialTelemetry(ArchetypeId).TightnessSampleCount, Is.GreaterThan(0), "precondition: tick 2 produced a reading");

        dbe.WriteTickFence(3);   // nothing written

        var t = dbe.GetSpatialTelemetry(ArchetypeId);
        Assert.Multiple(() =>
        {
            Assert.That(t.TightnessSampleCount, Is.Zero, "a settled world writes no cluster, so it measures none");
            Assert.That(t.MeanClusterExtentRatio, Is.Zero, "and must not carry the previous tick's mean forward");
            Assert.That(t.MeanPackingBound, Is.Zero);
            Assert.That(t.MeanTightnessToBound, Is.Zero, "zero over zero samples is 'nothing moved', which the sample count is what distinguishes");
        });
    }

    [Test]
    public void PackingBound_DropsBelowOne_OnceACellHoldsMoreThanOneClusterOfEntities()
    {
        using var dbe = SetupEngineWithGrid();
        var slots = SlotsPerCluster(dbe);
        var population = slots * 2;

        // All inside cell (0,0), spread over 40 units so no box is degenerate and nothing crosses.
        var ids = new List<EntityId>(population);
        for (var i = 0; i < population; i++)
        {
            ids.Add(Spawn(dbe, 10f + (i % 40), 10f + (i % 7)));
        }

        dbe.WriteTickFence(1);

        // Touch every cluster so every one of them is sampled — the reading is over WRITTEN clusters by construction.
        for (var i = 0; i < ids.Count; i++)
        {
            WriteSpatialTo(dbe, ids[i], 10f + (i % 40), 12f + (i % 7));
        }

        dbe.WriteTickFence(2);

        var expectedBound = MathF.Sqrt(slots / (float)population);
        var t = dbe.GetSpatialTelemetry(ArchetypeId);
        Assert.Multiple(() =>
        {
            Assert.That(t.TightnessSampleCount, Is.GreaterThan(0), "the writes above put every cluster in the refresh's path");
            Assert.That(t.MeanPackingBound, Is.EqualTo((double)expectedBound).Within(1e-5),
                "every sampled cluster lives in the same cell, so the mean bound is that cell's bound exactly: sqrt(slots / E) in 2D");
            Assert.That(t.MeanPackingBound, Is.LessThan(1d), "a cell holding twice a cluster's slots cannot be packed into one cluster");
            Assert.That(t.MeanClusterExtentRatio, Is.GreaterThan(0d).And.LessThanOrEqualTo(1d));
        });
    }

    [VerifiesRule("SO-01")]
    [Test]
    public void MaxClusterOverhang_IsPublished_AndIsARunningMaximumRatherThanAPerTickValue()
    {
        using var dbe = SetupEngineWithGrid();

        // Centre at (150,150) — cell (1,1) — with a box reaching 10 units past the cell on every side. Membership is decided by
        // the CENTRE, so the entity belongs to cell (1,1) while its geometry does not fit inside it.
        SpawnBox(dbe, 90f, 90f, 210f, 210f);
        dbe.WriteTickFence(1);

        var afterSpawn = dbe.GetSpatialTelemetry(ArchetypeId).MaxClusterOverhang;
        Assert.That(afterSpawn, Is.EqualTo(10f).Within(1e-3f), "the box reaches 10 world units outside its own cell on each axis");

        dbe.WriteTickFence(2);   // quiet

        Assert.That(dbe.GetSpatialTelemetry(ArchetypeId).MaxClusterOverhang, Is.EqualTo(afterSpawn),
            "it is neither per-tick nor cumulative: every kNN ring widens by it, so it never falls and never resets");
    }

    [Test]
    public void CellTreeCounters_ArePublished_AndReadZeroWhenNoHalfPromotes()
    {
        // Promotion is off by default, and even the count gate — 1024 clusters in one cell half AND a mean extent at or below 0.10 of the cell — would
        // not fire on this workload, so both counters are ZERO — and that zero is the finding the issue asks to make visible, not a
        // missing producer. Before #911 there was no way to tell the two apart without a debugger.
        using var dbe = SetupEngineWithGrid();
        var id = Spawn(dbe, 50f, 50f);
        MoveTo(dbe, id, 150f, 250f);
        dbe.WriteTickFence(1);

        var t = dbe.GetSpatialTelemetry(ArchetypeId);
        Assert.Multiple(() =>
        {
            Assert.That(t.CellTreePromotions, Is.Zero);
            Assert.That(t.CellTreeDemotions, Is.Zero);
        });
    }

    [VerifiesRule("SO-01")]
    [Test]
    public void Total_MaxesTheOverhang_AndWeightsTheTightnessMeansBySample()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<SpTelPos>();
        dbe.RegisterComponentFromAccessor<SpTelPosB>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(
            worldMin: new Vector2(0, 0),
            worldMax: new Vector2(WorldMax, WorldMax),
            cellSize: CellSize));
        dbe.InitializeArchetypes();

        // Archetype A: a box overhanging its cell by 10, plus a wide cluster in cell (0,0).
        SpawnBox(dbe, 90f, 90f, 210f, 210f);
        var a = Spawn(dbe, 10f, 10f);
        Spawn(dbe, 20f, 10f);

        // Archetype B: a box overhanging by 2 only.
        using (var tx = dbe.CreateQuickTransaction())
        {
            tx.Spawn<SpTelUnitB>(SpTelUnitB.Pos.Set(new SpTelPosB
            {
                Bounds = new AABB2F { MinX = 298f, MinY = 298f, MaxX = 402f, MaxY = 402f },
            }));
            tx.Commit();
        }

        dbe.WriteTickFence(1);
        WriteSpatialTo(dbe, a, 60f, 10f);
        dbe.WriteTickFence(2);

        var perArchetype = dbe.GetSpatialTelemetry(ArchetypeId);
        var total = dbe.GetSpatialTelemetryTotal();

        Assert.Multiple(() =>
        {
            Assert.That(perArchetype.MaxClusterOverhang, Is.EqualTo(10f).Within(1e-3f), "precondition: archetype A owns the larger overhang");
            Assert.That(total.MaxClusterOverhang, Is.EqualTo(10f).Within(1e-3f),
                "engine-wide overhang is the largest any archetype proved, never the sum — summing would widen every kNN ring by 12");

            // B wrote nothing on tick 2, so it contributes no samples and the weighted mean is A's alone. Summing the two archetypes'
            // MEANS instead of their numerators would divide A's sum by two and halve the reading.
            Assert.That(total.TightnessSampleCount, Is.EqualTo(perArchetype.TightnessSampleCount));
            Assert.That(total.MeanClusterExtentRatio, Is.EqualTo(perArchetype.MeanClusterExtentRatio).Within(1e-9),
                "a quiet archetype contributes no samples and must not drag the mean toward zero");
            Assert.That(total.MeanPackingBound, Is.EqualTo(perArchetype.MeanPackingBound).Within(1e-9));
        });
    }

    /// <summary>
    /// #910's arrival members fold by kind: the jump, clamp and cell counts sum across archetypes, and the largest arrival MAXES — two archetypes'
    /// arrivals into two cells are not one arrival of their combined size.
    /// </summary>
    [VerifiesRule("SO-01")]
    [Test]
    public void Total_MaxesTheLargestArrival_AndSumsTheArrivalCounts()
    {
        using var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<SpTelPos>();
        dbe.RegisterComponentFromAccessor<SpTelPosB>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(
            worldMin: new Vector2(0, 0),
            worldMax: new Vector2(WorldMax, WorldMax),
            cellSize: CellSize));
        dbe.InitializeArchetypes();

        static SpTelPosB PointB(float x, float y) => new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y } };

        // A: three from (0,0) into (5,5), a jump each, and one clamped out of (5,9) into (0,9). B: two from (0,0) into (1,0), a step each, and one
        // clamped out of (5,0) into (0,0). Every member then reads differently summed than maxed.
        var a = new[] { Spawn(dbe, 10f, 10f), Spawn(dbe, 20f, 10f), Spawn(dbe, 30f, 10f), Spawn(dbe, 550f, 950f) };
        var b = new EntityId[3];
        using (var tx = dbe.CreateQuickTransaction())
        {
            b[0] = tx.Spawn<SpTelUnitB>(SpTelUnitB.Pos.Set(PointB(10f, 50f)));
            b[1] = tx.Spawn<SpTelUnitB>(SpTelUnitB.Pos.Set(PointB(12f, 50f)));
            b[2] = tx.Spawn<SpTelUnitB>(SpTelUnitB.Pos.Set(PointB(550f, 50f)));
            tx.Commit();
        }

        dbe.WriteTickFence(1);

        for (var i = 0; i < 3; i++)
        {
            MoveTo(dbe, a[i], 550f, 550f);
        }

        MoveTo(dbe, a[3], -300f, 950f);
        using (var tx = dbe.CreateQuickTransaction())
        {
            foreach (var (id, x) in new[] { (b[0], 150f), (b[1], 150f), (b[2], -300f) })
            {
                var eref = tx.OpenMut(id);
                eref.Write(SpTelUnitB.Pos).Bounds = PointB(x, 50f).Bounds;
            }

            tx.Commit();
        }

        dbe.WriteTickFence(2);

        var ta = dbe.GetSpatialTelemetry(ArchetypeId);
        var tb = dbe.GetSpatialTelemetry(Archetype<SpTelUnitB>.Metadata.ArchetypeId);
        var total = dbe.GetSpatialTelemetryTotal();
        Assert.Multiple(() =>
        {
            Assert.That((ta.JumpCrossings, ta.ClampedDestinations, ta.LargestArrivalRun, ta.ArrivalCellsTouched), Is.EqualTo((4, 1, 3, 2)), "precondition: A");
            Assert.That((tb.JumpCrossings, tb.ClampedDestinations, tb.LargestArrivalRun, tb.ArrivalCellsTouched), Is.EqualTo((1, 1, 2, 2)), "precondition: B");
            Assert.That(total.LargestArrivalRun, Is.EqualTo(3), "the largest arrival any cell received, never the sum — no cell received five");
            Assert.That(total.JumpCrossings, Is.EqualTo(5), "summed");
            Assert.That(total.ClampedDestinations, Is.EqualTo(2), "summed");
            Assert.That(total.ArrivalCellsTouched, Is.EqualTo(4), "summed");
        });
    }

    [Test]
    public void FenceSpanMs_IsZero_WhenTheHostDrivesTheFenceItself()
    {
        // The span is timed by the parallel fence's phase-exec systems. A host calling WriteTickFence directly — which is
        // what this fixture does, and what a runtime-less embedder does — never runs them, so there is no span to report.
        //
        // Zero here means "the parallel fence did not drive this tick", NOT "the fence was free", and the distinction is
        // the whole reason the member is documented rather than left to be inferred. Asserting it pins the contract that a
        // consumer must not read this as a duration of zero.
        using var dbe = SetupEngineWithGrid();
        var id = Spawn(dbe, 50f, 50f);
        MoveTo(dbe, id, 150f, 250f);
        dbe.WriteTickFence(1);

        Assert.Multiple(() =>
        {
            Assert.That(dbe.LastFenceSpanMs, Is.Zero, "no parallel fence ran, so nothing timed the span");
            Assert.That(dbe.GetSpatialTelemetry(ArchetypeId).MigrationCount, Is.EqualTo(1),
                "PRECONDITION: the fence really did the work — otherwise the zero above is trivially true and pins nothing");
        });

        using var exporter = new EcsMetricsExporter(dbe);
        var (_, doubles) = ScrapeSpatialInstruments(exporter);
        Assert.That(doubles, Does.ContainKey("typhon.ecs.spatial.fence_span_ms"),
            "the instrument must be published even when it reads zero — a consumer cannot tell 'not built' from 'broken' otherwise");
        Assert.That(doubles["typhon.ecs.spatial.fence_span_ms"], Is.Zero);
    }

    [Test]
    [VerifiesRule("SO-01")]
    public void FenceStallMs_CoversTheSerialPrep_ThatTheSpanExcludes()
    {
        // SO-01: the span and the stall are not two measurements of one thing, and this pins the containment that makes them
        // readable together.
        //
        // `LastFenceSpanMs` starts at Prep's Prepare. The fence call runs a serial prep BEFORE that, on the tick thread and
        // inside the same epoch fence window — context reset, dormancy drain, ProcessTableFence over every component table —
        // so the span reports less than the host actually waited. `LastFenceStallMs` brackets the whole call.
        //
        // Why it matters enough to test: the serial prep is single-threaded by construction, so no worker count shrinks it. A
        // sweep across worker counts that reads only the span reports a speed-up on a FRACTION of the interruption, and the
        // fraction is invisible unless both numbers are on the surface. Containment is the checkable form of that — a stall
        // that ever came out below its own span would mean one of the two is not measuring the fence.
        using var dbe = SetupEngineWithGrid();
        for (var i = 0; i < 64; i++)
        {
            Spawn(dbe, 50f + i % 8 * 100f, 50f + i / 8 * 100f);
        }

        var moved = 0;
        var stall = 0d;
        var span = 0d;

        using (var runtime = TyphonRuntime.Create(dbe, schedule =>
               {
                   schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Move", _ =>
                   {
                       // Sampled on the tick BEFORE this tick's fence, so what is read is the previous fence's published pair —
                       // never a half-written one from the fence running now.
                       var s = dbe.LastFenceStallMs;
                       if (s > 0d)
                       {
                           stall = s;
                           span = dbe.LastFenceSpanMs;
                       }

                       Interlocked.Increment(ref moved);
                   });
               }, new RuntimeOptions { WorkerCount = 2, BaseTickRate = 100, EnableParallelFence = true }))
        {
            runtime.Start();
            SpinWait.SpinUntil(() => Volatile.Read(ref moved) >= 5 && runtime.CurrentTickNumber >= 5, TimeSpan.FromSeconds(15));
            runtime.Shutdown();
        }

        Assert.Multiple(() =>
        {
            Assert.That(stall, Is.GreaterThan(0d), "the parallel fence ran, so the whole-call stall must have been timed");
            Assert.That(span, Is.GreaterThan(0d),
                "PRECONDITION: the span must be live too — a zero span would make the containment below trivially true");
            Assert.That(stall, Is.GreaterThanOrEqualTo(span),
                "the stall brackets the fence call and the span starts inside it, so the stall can never be the smaller of the two");
        });

        using var exporter = new EcsMetricsExporter(dbe);
        var (_, doubles) = ScrapeSpatialInstruments(exporter);
        Assert.That(doubles, Does.ContainKey("typhon.ecs.spatial.fence_stall_ms"),
            "a consumer budgeting a frame reads the stall, so it must be exported beside the span rather than only readable in-process");
    }

    /// <summary>
    /// The parallelism ratio must be exported beside the summed-CPU gauges, and must be able to report below 1.
    /// </summary>
    /// <remarks>
    /// <para><b>SO-01 binds the export, not only the accessor.</b> <c>migration_duration_ms</c> and its siblings are CPU summed across workers, so they RISE
    /// with the worker count on unchanged work. The rule's remedy is to present the achieved parallelism beside them — but for most of this surface's life
    /// that ratio was <c>internal</c> and ungauged, so an OTel consumer could read the summed figure and not the divisor, which is precisely the position the
    /// rule was written to prevent. Asserting the instrument exists is what keeps the remedy real rather than documented.</para>
    /// <para><b>The sub-unity half is the reading worth having.</b> A phase whose elapsed span exceeded its own summed CPU is dispatch overhead swallowing
    /// the work — what a parallel fence looks like on a population too small to split. A floor at 1 renders that indistinguishable from a healthy serial
    /// tick, so the store must not clamp; the consumer that needs a floor (<c>ObserveMigrationCost</c>) applies its own.</para>
    /// </remarks>
    [Test]
    [VerifiesRule("SO-01")]
    public void MigrationParallelism_IsExportedBesideTheSummedCpuGauges_AndIsNotFlooredAtOne()
    {
        using var dbe = SetupEngineWithGrid();

        using var exporter = new EcsMetricsExporter(dbe);
        var (_, doubles) = ScrapeSpatialInstruments(exporter);

        Assert.Multiple(() =>
        {
            Assert.That(doubles, Does.ContainKey("typhon.ecs.spatial.fence_migration_parallelism"),
                "the divisor that makes the summed-CPU gauges readable must be on the same surface as they are");
            Assert.That(doubles, Does.ContainKey("typhon.ecs.spatial.migration_duration_ms"),
                "PRECONDITION: a summed-CPU gauge is exported, or there is nothing for the ratio to qualify");
            Assert.That(dbe.LastFenceMigrationParallelism, Is.EqualTo(1d),
                "a host driving the fence itself is one thread, so summed CPU IS elapsed");
        });

        // Sub-unity must survive the round trip. Pushed through the same setter the runtime uses rather than asserted against a live parallel fence: making a
        // real fence's span exceed its own CPU needs a workload tuned to be too small to split, which would pin the tuning rather than the contract.
        dbe.SetLastFenceMigrationParallelism(0.4d);
        var (_, afterDoubles) = ScrapeSpatialInstruments(exporter);

        Assert.Multiple(() =>
        {
            Assert.That(dbe.LastFenceMigrationParallelism, Is.EqualTo(0.4d).Within(1e-9),
                "below 1 is dispatch overhead exceeding the work — a real reading, and a clamp here would hide the only case worth acting on");
            Assert.That(afterDoubles["typhon.ecs.spatial.fence_migration_parallelism"], Is.EqualTo(0.4d).Within(1e-9),
                "and the gauge must report it unclamped too, or the export re-introduces the floor the store dropped");
        });
    }

    [Test]
    public void O2Counters_AreExportedAsMetrics_AndAgreeWithTheAccessor()
    {
        using var dbe = SetupEngineWithGrid();
        var a = Spawn(dbe, 10f, 10f);
        Spawn(dbe, 30f, 10f);
        dbe.WriteTickFence(1);
        WriteSpatialTo(dbe, a, 60f, 10f);
        dbe.WriteTickFence(2);

        var expected = dbe.GetSpatialTelemetry(ArchetypeId);
        Assert.That(expected.TightnessSampleCount, Is.GreaterThan(0), "precondition: there is a non-zero reading to agree ON");

        using var exporter = new EcsMetricsExporter(dbe);
        var (longs, doubles) = ScrapeSpatialInstruments(exporter);

        Assert.Multiple(() =>
        {
            Assert.That(longs["typhon.ecs.spatial.tightness_samples"], Is.EqualTo(expected.TightnessSampleCount));
            Assert.That(doubles["typhon.ecs.spatial.cluster_extent_ratio"], Is.EqualTo(expected.MeanClusterExtentRatio).Within(1e-9));
            Assert.That(doubles["typhon.ecs.spatial.packing_bound"], Is.EqualTo(expected.MeanPackingBound).Within(1e-9));
            Assert.That(doubles["typhon.ecs.spatial.tightness_to_bound"], Is.EqualTo(expected.MeanTightnessToBound).Within(1e-9));
            Assert.That(doubles["typhon.ecs.spatial.max_cluster_overhang"], Is.EqualTo((double)expected.MaxClusterOverhang).Within(1e-6));
            Assert.That(longs["typhon.ecs.spatial.cell_tree_promotions"], Is.EqualTo(expected.CellTreePromotions));
            Assert.That(longs["typhon.ecs.spatial.cell_tree_demotions"], Is.EqualTo(expected.CellTreeDemotions));
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // #912 — the members that make MigrationExecuteMs divisible, and the split that makes a per-kind cost attributable.
    //
    // The anomaly these exist for: relocation CPU per entity was recorded as 425 -> 844 -> 1 440 ns at W = 2/4/8 and read as
    // contention in the relocation drain. It is a sum of per-SLICE spans over a per-ENTITY count, and the parallel fence
    // sizes slices from the worker count — so nothing published could tell "the same work in more pieces" from "the work got
    // slower", and the anomaly stood unowned through three steps of the design.
    // ══════════════════════════════════════════════════════════════════════════

    [VerifiesRule("SO-01")]
    [Test]
    public void ExecutedKinds_SumExactlyToTheMigrationCount()
    {
        using var dbe = SetupEngineWithGrid();
        var a = Spawn(dbe, 50f, 50f);
        var b = Spawn(dbe, 60f, 60f);
        var c = Spawn(dbe, 70f, 70f);
        dbe.WriteTickFence(1);

        // Three cell crossings, each well past the 5-unit hysteresis margin. The KIND is what this pins; the count is the
        // control that stops the identity holding vacuously at 0 == 0.
        MoveTo(dbe, a, 150f, 250f);
        MoveTo(dbe, b, 350f, 150f);
        MoveTo(dbe, c, 250f, 350f);
        dbe.WriteTickFence(2);

        var t = dbe.GetSpatialTelemetry(ArchetypeId);
        Assert.That(t.MigrationCount, Is.EqualTo(3), "precondition: three entities crossed, so there is a non-zero split to check");
        Assert.Multiple(() =>
        {
            Assert.That(t.CrossingsExecuted + t.RelocationsExecuted + t.RepairsExecuted, Is.EqualTo(t.MigrationCount),
                "the three kinds partition the executed migrations — the identity is what makes the split checkable rather than trusted");
            Assert.That(t.CrossingsExecuted, Is.EqualTo(3), "every one of these moved to a different cell, so every one is a crossing");
            Assert.That(t.RelocationsExecuted, Is.Zero, "nothing drifted within its cell");
            Assert.That(t.RepairsExecuted, Is.Zero, "no repair budget was configured, so no repair unit can have been admitted");
        });

        var total = dbe.GetSpatialTelemetryTotal();
        Assert.That(total.CrossingsExecuted + total.RelocationsExecuted + total.RepairsExecuted, Is.EqualTo(total.MigrationCount),
            "the engine-wide fold sums each kind, so the identity has to survive it");
    }

    [VerifiesRule("SO-01")]
    [Test]
    public void MigrationSliceCount_IsPublished_AndBoundsThePrologueAndEpilogueWithinTheExecuteSpan()
    {
        using var dbe = SetupEngineWithGrid();
        var id = Spawn(dbe, 50f, 50f);
        MoveTo(dbe, id, 150f, 250f);
        dbe.WriteTickFence(1);

        var t = dbe.GetSpatialTelemetry(ArchetypeId);
        Assert.That(t.MigrationCount, Is.EqualTo(1), "precondition: a slice must actually have run");
        Assert.Multiple(() =>
        {
            // The serial fence takes the whole queue in one call, which is exactly the case where MigrationExecuteMs /
            // MigrationCount IS a per-entity figure. Pinning it here is what gives the parallel reading something to differ FROM.
            Assert.That(t.MigrationSliceCount, Is.EqualTo(1), "WriteTickFence drains the queue in one slice, so one span was summed");
            Assert.That(t.MigrationPrologueMs, Is.GreaterThanOrEqualTo(0d).And.Not.NaN, "measured, not derived — it may round to zero");
            Assert.That(t.MigrationEpilogueMs, Is.GreaterThanOrEqualTo(0d).And.Not.NaN);
            // The containment is the invariant, not the magnitudes: both are PART of the execute span. A prologue larger than
            // the span it is measured inside would mean the marks had been moved out of the bracket, which is the change #911
            // spends a paragraph forbidding at the other end.
            Assert.That(t.MigrationPrologueMs + t.MigrationEpilogueMs, Is.LessThanOrEqualTo(t.MigrationExecuteMs + 1e-9),
                "prologue and epilogue are parts of MigrationExecuteMs, never additional to it");
        });
    }

    [VerifiesRule("SO-01")]
    [Test]
    public void FinalizeLockAcquisitions_CountEveryAcquisition_AndResetPerTick()
    {
        using var dbe = SetupEngineWithGrid();
        Spawn(dbe, 50f, 50f);
        dbe.WriteTickFence(1);

        // A spawn allocates the first cluster, which is the latch's new-cluster slow path — so this tick cannot have taken it
        // zero times. Asserting > 0 rather than an exact number: the count is a diagnostic over a set of call sites that will
        // grow, and pinning it exactly would make every new site a failing test rather than a counted one.
        var busy = dbe.GetSpatialTelemetry(ArchetypeId);
        Assert.That(busy.FinalizeLockAcquisitions, Is.GreaterThan(0L),
            "a tick that allocated a cluster took the finalize latch, and every acquisition goes through the counting wrapper");

        // The reset is the half that a plain accumulate would get wrong, and it is the half that matters: an un-reset counter
        // grows without bound and reads as contention rising over the life of the process.
        dbe.WriteTickFence(2);
        var quiet = dbe.GetSpatialTelemetry(ArchetypeId);
        Assert.That(quiet.FinalizeLockAcquisitions, Is.LessThan(busy.FinalizeLockAcquisitions),
            "the counter is per-tick: a quiet tick reports its own acquisitions, not the previous tick's plus its own");
    }

    [Test]
    public void MeterListener_ObservesTheMigrationDecompositionMembers()
    {
        using var dbe = SetupEngineWithGrid();
        var id = Spawn(dbe, 50f, 50f);
        MoveTo(dbe, id, 150f, 250f);
        dbe.WriteTickFence(1);

        var expected = dbe.GetSpatialTelemetry(ArchetypeId);
        Assert.That(expected.MigrationCount, Is.EqualTo(1), "precondition: the accessor must have a non-zero value to agree ON");

        using var exporter = new EcsMetricsExporter(dbe);
        var (longs, doubles) = ScrapeSpatialInstruments(exporter);

        Assert.Multiple(() =>
        {
            Assert.That(longs["typhon.ecs.spatial.migration_slices"], Is.EqualTo(expected.MigrationSliceCount));
            Assert.That(longs["typhon.ecs.spatial.crossings_executed"], Is.EqualTo(expected.CrossingsExecuted));
            Assert.That(longs["typhon.ecs.spatial.relocations_executed"], Is.EqualTo(expected.RelocationsExecuted));
            Assert.That(longs["typhon.ecs.spatial.repairs_executed"], Is.EqualTo(expected.RepairsExecuted));
            Assert.That(longs["typhon.ecs.spatial.finalize_lock_acquisitions"], Is.EqualTo(expected.FinalizeLockAcquisitions));
            Assert.That(doubles["typhon.ecs.spatial.migration_prologue_ms"], Is.EqualTo(expected.MigrationPrologueMs).Within(1e-9));
            Assert.That(doubles["typhon.ecs.spatial.migration_epilogue_ms"], Is.EqualTo(expected.MigrationEpilogueMs).Within(1e-9));
        });
    }
}
