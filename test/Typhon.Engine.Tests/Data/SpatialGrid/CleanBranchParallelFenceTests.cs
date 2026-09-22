using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

[Component("Typhon.Test.Cbp.Pos", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential)]
struct CbpPos
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;
}

[Archetype]
partial class CbpUnit : Archetype<CbpUnit>
{
    public static readonly Comp<CbpPos> Pos = Register<CbpPos>();
}

/// <summary>
/// MD-02 on the PARALLEL fence: a barrier-only archetype publishes no change list, and the Migrate phase still gets the
/// buffer it writes its dirty-bit deltas into (#963, follow-up to #939).
/// </summary>
/// <remarks>
/// <para><b>The gap this closes, stated narrowly.</b> MD-02 already has parallel verifiers —
/// <c>CellTreeDensityTransitionTests</c> runs the parallel fence with real migrations. But that fixture never calls
/// <c>SetSpatialBarrierOnly</c>, so its archetype takes branch 2 and always has a change list. #939 made the array
/// legitimately ABSENT on branch 1, and every test of that absence drives the SERIAL fence
/// (<c>CleanBranchChangeListTests</c> → <c>WriteTickFence</c>). The uncovered case is therefore precise: a **null change
/// list reaching the parallel Migrate path**, where <c>ExecuteMigrations</c> dereferences the buffer that
/// <c>PreSizeMigrationBuffers</c> is now only conditionally responsible for allocating.</para>
/// <para><b>Why the sibling parallel fixture does not cover it.</b> <c>CellTreeParallelFenceTests</c> does run
/// barrier-only under the parallel fence, but its motion is a rotation about each cluster's own cell centre — "no entity
/// ever leaves its cell" — so its Migrate phase is empty by construction. It exercises AabbRefresh slicing with a null
/// list; it never reaches the buffer.</para>
/// <para><b>Observed at Prep's end, not after the tick.</b> A non-null array read after the fence proves nothing: the
/// on-demand <c>GrowFenceDirtyBitsForChunkId</c> inside <c>ExecuteMigrations</c> explains it just as well as the
/// pre-size. <c>PrepQueueProbe</c> fires last in Prep's tail, deliberately — "so the probe observes the archetype
/// exactly as the Migrate phase will find it, including the pre-size" — which is the one instant where the two
/// explanations differ. The probe fires on FENCE WORKER threads here, so every capture is interlocked and every
/// assertion is made afterwards on the main thread; throwing inside the probe would fault the fence rather than fail
/// the test.</para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class CleanBranchParallelFenceTests : TestBase<CleanBranchParallelFenceTests>
{
    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private const float CellSize = 1_000f;
    private const float WorldExtent = 4_000f;

    /// <summary>64 entities fill a cluster, so this is ~47 clusters — enough for the Migrate phase to pack several items.</summary>
    private const int EntityCount = 3_000;

    private const int MotionTicks = 40;
    private const int WorkerCount = 4;

    /// <summary>Per-tick drift along +X. Larger than the hysteresis margin, so crossings are real and frequent.</summary>
    private const float DriftStep = 60f;

    private static CbpPos PointAt(float x, float y) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y } };

    private static ArchetypeClusterState ClusterStateOf(DatabaseEngine dbe) =>
        dbe._archetypeStates[Archetype<CbpUnit>.Metadata.ArchetypeId].ClusterState;

    private static int ArchetypeId => Archetype<CbpUnit>.Metadata.ArchetypeId;

    /// <summary>Drifts every entity along +X through <c>WriteSpatial</c> — the barrier API, which is what makes the fence see a crossing at all.</summary>
    private static void DriftEveryEntity(Transaction tx)
    {
        var accessor = tx.For<CbpUnit>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                // TYPHON009 flags un-barriered spatial mutation through a span. This span is READ only — the write goes through WriteSpatial.
#pragma warning disable TYPHON009
                var positions = cluster.GetSpan(CbpUnit.Pos);
#pragma warning restore TYPHON009
                ulong bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    int slotIndex = BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;

                    ref readonly var b = ref positions[slotIndex].Bounds;
                    float x = b.MinX + DriftStep;
                    if (x >= WorldExtent - 1f)
                    {
                        x = 1f;
                    }

                    cluster.WriteSpatial(CbpUnit.Pos, slotIndex, PointAt(x, b.MinY));
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }
    }

    [Test]
    [VerifiesRule("MD-02")]
    [CancelAfter(180_000)]
    public void TheParallelMigratePhaseGetsItsBuffer_WithNoChangeListPublished()
    {
        ServiceProvider.EnsureFileDeleted<ManagedPagedMMFOptions>();
        using var scope = ServiceProvider.CreateScope();
        using var dbe = scope.ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<CbpPos>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(new Vector2(0, 0), new Vector2(WorldExtent, WorldExtent), CellSize));
        dbe.InitializeArchetypes();
        dbe.SetSpatialBarrierOnly<CbpUnit>();

        var rng = new Random(963_939);
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < EntityCount; i++)
            {
                var x = 1f + ((float)rng.NextDouble() * (CellSize - 2f));
                var y = 1f + ((float)rng.NextDouble() * ((2f * CellSize) - 2f));
                tx.Spawn<CbpUnit>(CbpUnit.Pos.Set(PointAt(x, y)));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
        var cs = ClusterStateOf(dbe);
        Assert.That(cs.ActiveClusterCount, Is.GreaterThan(1), "precondition: the fixture must build several clusters");

        // Every capture below is written from fence worker threads.
        var migrationTicksWithBuffer = 0;      // migrations queued AND the pre-size left an adequately sized buffer
        var missingBuffer = 0;                 // migrations queued but no buffer — the defect this test exists for
        var undersizedBuffer = 0;              // buffer present but smaller than the bound the Migrate phase was sized against
        var quietTicksWithNoList = 0;          // no migrations: the change list must be absent
        var listPublishedOnCleanBranch = 0;    // branch 1 must never publish a change list for a barrier-only archetype
        var offBranchTicks = 0;

        ArchetypeClusterState.PrepQueueProbe = (state, _) =>
        {
            if (state.ArchetypeId != ArchetypeId)
            {
                return;
            }

            if (state.FenceBranchPath != 1)
            {
                Interlocked.Increment(ref offBranchTicks);
                return;
            }

            var bits = state.FenceDirtyBits;
            if (state.PendingMigrationCount > 0)
            {
                if (bits == null)
                {
                    Interlocked.Increment(ref missingBuffer);
                }
                else if (bits.Length < state.LastPreSizeUpperBound)
                {
                    Interlocked.Increment(ref undersizedBuffer);
                }
                else
                {
                    Interlocked.Increment(ref migrationTicksWithBuffer);
                }
            }
            else
            {
                if (bits == null)
                {
                    Interlocked.Increment(ref quietTicksWithNoList);
                }
                else
                {
                    Interlocked.Increment(ref listPublishedOnCleanBranch);
                }
            }
        };

        var ticks = 0;
        var stopping = 0;
        Exception unhandled = null;
        Exception teardown = null;
        long migrationsBefore = cs.TotalMigrationCount;

        try
        {
            using (var runtime = TyphonRuntime.Create(dbe, schedule =>
            {
                schedule.PublicTrack.DeclareDag("Drift").CallbackSystem("Drift", ctx =>
                {
                    // Alternate ticks during the motion phase, then stop. Drifting every tick — the first version of this fixture — made the "no migrations,
                    // so no buffer is allocated" half unreachable by construction. Alternating was not enough either (#999): a tick that does not drift still
                    // inherits the migrations the previous one queued but did not drain — measured 10 to 500 per quiet tick, with only 0 to 6 of ~21 quiet
                    // ticks reaching Prep's tail empty, and none at all in about one run in five. So the quiet half is observed after the motion stops,
                    // once the backlog has drained, rather than hoped for between drifts.
                    var n = Interlocked.Increment(ref ticks);
                    if (n <= MotionTicks && (n & 1) == 1)
                    {
                        DriftEveryEntity(ctx.Transaction);
                    }
                });
            }, new RuntimeOptions
            {
                WorkerCount = WorkerCount,
                // 100 Hz for the measured reason the sibling spatial fixtures carry: a tick that rewrites every spatial field does not fit a 1 ms budget,
                // and an overrun makes two ticks share one system transaction, which faults in teardown and says nothing about the fence.
                BaseTickRate = 100,
                EnableParallelFence = true,
            }))
            {
                runtime.Scheduler.UnhandledExceptionCallback = (_, _, ex) =>
                {
                    if (Volatile.Read(ref stopping) != 0)
                    {
                        Interlocked.CompareExchange(ref teardown, ex, null);
                        return;
                    }

                    Interlocked.CompareExchange(ref unhandled, ex, null);
                };
                runtime.Start();
                SpinWait.SpinUntil(() => Volatile.Read(ref ticks) >= MotionTicks, TimeSpan.FromSeconds(90));

                // Motion has stopped: keep ticking until the backlog drains and a tick reaches Prep's tail with nothing queued. The cap only bounds a
                // backlog that never drains, which the quiet-tick assertion below then reports.
                SpinWait.SpinUntil(() => Volatile.Read(ref quietTicksWithNoList) > 0, TimeSpan.FromSeconds(30));
                var reached = Volatile.Read(ref ticks);
                SpinWait.SpinUntil(() => Volatile.Read(ref ticks) >= reached + 3, TimeSpan.FromSeconds(10));
                Volatile.Write(ref stopping, 1);
                runtime.Shutdown();
            }
        }
        finally
        {
            ArchetypeClusterState.PrepQueueProbe = null;
        }

        if (teardown != null)
        {
            TestContext.Out.WriteLine($"ignored a teardown-window fault — {teardown.GetType().Name}: {teardown.Message}");
        }

        Assert.Multiple(() =>
        {
            Assert.That(unhandled, Is.Null, $"a fence phase threw while the runtime was running — {unhandled}");
            Assert.That(Volatile.Read(ref ticks), Is.GreaterThanOrEqualTo(MotionTicks), "the runtime did not complete its motion ticks");
            Assert.That(cs.TotalMigrationCount, Is.GreaterThan(migrationsBefore),
                "no entity ever crossed a cell, so the Migrate phase never ran and this fixture tested nothing");

            // Non-vacuity: the case under test has to have OCCURRED, not merely not-failed.
            Assert.That(migrationTicksWithBuffer, Is.GreaterThan(0),
                "no tick reached Prep's tail with migrations queued, so the pre-size gate was never exercised on the parallel path");
            Assert.That(quietTicksWithNoList, Is.GreaterThan(0),
                "no tick reached Prep's tail without migrations, so the absence half of the invariant was never observed");

            // The invariant itself.
            Assert.That(missingBuffer, Is.Zero,
                "the parallel Migrate phase would have started with no buffer for its dirty-bit deltas");
            Assert.That(undersizedBuffer, Is.Zero,
                "the buffer was smaller than the bound the Migrate phase was sized against — it was grown on demand rather than pre-sized");
            Assert.That(listPublishedOnCleanBranch, Is.Zero,
                "a barrier-only archetype published a change list on the clean branch, which nothing on that path reads");
            Assert.That(offBranchTicks, Is.Zero,
                "the archetype left the clean branch, so the run stopped testing the path this fixture is about");
        });
    }
}
