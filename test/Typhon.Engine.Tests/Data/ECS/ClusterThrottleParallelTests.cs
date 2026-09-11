using System;
using System.Numerics;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// <c>AC-11.6</c> — the throttle's decisions do not depend on worker count (#872 step 11).
/// </summary>
/// <remarks>
/// <para><b>Why this needs a real runtime, and why a <c>WriteTickFence</c> loop would prove nothing.</b>
/// <c>ClusterMigrationTests</c> records the measurement: ablating the parallel fence's plan emission left <b>5 692 tests green</b>, because almost every
/// migration fixture drives the SERIAL fence. Anything asserted about parallel behaviour from a serial driver is asserted about code that did not run.</para>
/// <para><b>What makes determinism true here, rather than merely observed.</b> The throttle is a single-threaded, order-preserving partition at the tail of
/// Prep, and the repair queue is ranked on the same thread. Nothing about either decision is reached by more than one worker, so worker count cannot enter
/// it.</para>
/// <para><b>What is asserted is the RULE at every W, not an identical packing — and the difference is not a weakening.</b> Cross-arm packing equality
/// is what <c>ClusterRepairParallelTests</c> checks, and it can only check it because a repair PINS its destination slot. A step-10 relocation deliberately
/// does not: it names a destination cluster and lets <c>ClaimSlotInCell</c> pick the slot. The drain is then sorted on the cell key alone (unstable until
/// #889; <c>ArchetypeClusterState.OrderDrainAndMeasureArrivals</c> since #910), and the worker-local drifter buffers are merged in completion order, so
/// which entity wins a contested slot legitimately varies with W. <c>MigrationRequest.DestSlotIndex</c> documents exactly this,
/// and it is why the repair path pins and the relocation path does not.</para>
/// <para>So a fixture demanding identical packings across W would be asserting something the design explicitly declines to promise, and the first version of
/// this one did — reporting a throttle bug for behaviour that belongs to the sort. What <c>AC-11.6</c> is actually about is whether the ADMISSION DECISION
/// depends on scheduling, and that is checkable exactly: at every W, no tick may admit more than the budget pays for, and every detected drifter must be
/// accounted for as admitted, throttled or unplaced. A throttle that consulted anything worker-dependent would break one of those at W = 8 and not at
/// W = 1.</para>
/// </remarks>
[TestFixture]
[NonParallelizable]
class ClusterThrottleParallelTests : TestBase<ClusterThrottleParallelTests>
{
    /// <summary>Sentinel worker count meaning "drive the serial <c>WriteTickFence</c> instead of a runtime".</summary>
    private const int SerialArm = -1;

    private const float CellSize = 100f;
    private const float WorldMax = 1000f;

    /// <summary>Enough that the budget below cannot admit every drifter, which is what puts the throttle in the path at all.</summary>
    private const int Population = 900;

    /// <summary>The per-tick re-clustering budget, in milliseconds. Named because the per-tick assertion recomputes the affordance from it.</summary>
    private const float BudgetMs = 0.3f;

    private static ushort ArchetypeId => Archetype<ClMigUnit>.Metadata.ArchetypeId;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
    }

    private static ClMigPos PointAt(float x, float y, int tag = 0) =>
        new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y }, Tag = tag };

    private DatabaseEngine SetupEngine()
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClMigPos>();
        dbe.RegisterComponentFromAccessor<ClMigScratch>();
        dbe.ConfigureSpatialGrid(SpatialGridConfig.Flat(
            new Vector2(0, 0),
            new Vector2(WorldMax, WorldMax),
            CellSize,
            // Repair is held off (1.19 is under the constructor's 1.2 ceiling and unreachable for a cluster confined to its own cell) so the whole budget
            // is spent by the RELOCATION throttle. Mixing the two would make a disagreement ambiguous between the partition and the planner.
            clusterRepairExtentRatio: 1.19f,
            reclusterBudgetMs: BudgetMs, batchSpawnSortThreshold: 0 /* step 15: this fixture builds its layout by spawn ORDER; the Morton sort would tighten it at birth */,
            // No repair means no valve — see ClusterThrottleBudgetTests.SetupEngine for why the constructor insists the two agree.
            clusterRepairCriticalExtentRatio: 0f));
        dbe.InitializeArchetypes();
        return dbe;
    }

    /// <summary>
    /// Spawn a cell in two tight lobes, so relocation has somewhere BETTER to send a drifter.
    /// </summary>
    /// <remarks>
    /// <b>A uniformly scattered cell produces drifters and no relocations, and a fixture built on one asserts nothing.</b>
    /// <c>ChooseRelocationTarget</c> returns "nowhere" when no candidate cluster would grow less than the source, and in a cell whose clusters are ALL
    /// equally smeared that is every candidate — every drifter is detected and left in place, counted as unplaced. That is the documented reason the repair
    /// path exists at all (§5.2: "a cell whose clusters are all wrong has no good destination to offer"), and the first version of this fixture ran into it:
    /// four arms, zero relocations, four identical snapshots and a green cross-arm comparison that proved only that nothing happened.
    /// <para>Two tight lobes give first fit a chance to build clusters that are individually compact, so an entity moved between lobes has a genuinely
    /// better home to go to and the relocation actually fires.</para>
    /// </remarks>
    private static void SpawnScattered(DatabaseEngine dbe)
    {
        using (var tx = dbe.CreateQuickTransaction())
        {
            for (var i = 0; i < Population; i++)
            {
                var lobe = (i / 60) % 2;
                var x = (lobe == 0 ? 10f : 70f) + ((i * 7) % 17);
                var y = (lobe == 0 ? 10f : 70f) + ((i * 11) % 17);
                tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(x, y, i)));
            }

            tx.Commit();
        }

        dbe.WriteTickFence(1);
    }

    /// <summary>The last tick on which entities are moved. Everything after it is settling, so every arm converges to the same state.</summary>
    private const int LastMotionTick = 8;

    /// <summary>The tick by which every arm must have finished, so the snapshots compare like with like.</summary>
    private const int SettledTick = 14;

    /// <summary>
    /// Where entity <paramref name="tag"/> sits on <paramref name="tick"/> — a pure function, which is what makes the arms comparable.
    /// </summary>
    /// <remarks>
    /// <b>Not a seeded <c>Random</c>, deliberately.</b> A generator's output depends on how many times it has been called, and under a real runtime the
    /// number of motion passes is set by the scheduler rather than by the test. Keying the position on (tick, tag) makes the WORLD a function of the tick
    /// number alone, so an arm that ticks nine times and one that ticks eleven still agree about where everything is — and any disagreement that remains
    /// belongs to the throttle, which is the thing under test.
    /// </remarks>
    private static (float x, float y) PositionAt(int tick, int tag)
    {
        var lobe = ((tag / 60) + tick) % 2;
        var x = (lobe == 0 ? 10f : 70f) + ((tag * 7) % 17);
        var y = (lobe == 0 ? 10f : 70f) + ((tag * 11) % 17);
        return (x, y);
    }

    /// <summary>Move every entity to its position for <paramref name="tick"/>.</summary>
    private static unsafe void MoveTo(DatabaseEngine dbe, int tick)
    {
        using var tx = dbe.CreateQuickTransaction();
        var accessor = tx.For<ClMigUnit>();
        try
        {
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
#pragma warning disable TYPHON009 // The tag is read to derive the new value written back on the next line.
                var positions = cluster.GetSpan(ClMigUnit.Pos);
#pragma warning restore TYPHON009
                var bits = cluster.OccupancyBits;
                while (bits != 0)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    var tag = positions[slot].Tag;
                    var (x, y) = PositionAt(tick, tag);
                    cluster.WriteSpatial(ClMigUnit.Pos, slot, PointAt(x, y, tag));
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        tx.Commit();
    }

    [Test]
    [VerifiesRule("TH-01")]
    [CancelAfter(60_000)]
    public void TheThrottleEnforcesItsBudgetAtEveryWorkerCount([Values(SerialArm, 1, 2, 8)] int workerCount)
    {
        using var dbe = SetupEngine();
        SpawnScattered(dbe);

        var arm = workerCount == SerialArm ? "the serial WriteTickFence" : $"the parallel fence at W={workerCount}";
        var observed = workerCount == SerialArm ? RunSerial(dbe) : RunParallel(dbe, workerCount);

        Assert.Multiple(() =>
        {
            Assert.That(observed.Admitted, Is.GreaterThan(0), $"{arm} relocated nothing, so neither assertion below had anything to constrain");
            Assert.That(observed.Throttled, Is.GreaterThan(0),
                $"{arm} never hit the budget, so 'the budget was respected' held for want of work rather than because the throttle enforced it");
        });
    }

    /// <summary>What one arm observed: totals, plus the per-tick assertions run as they happened.</summary>
    private readonly record struct ArmResult(int Admitted, int Throttled);

    /// <summary>
    /// Check the throttle's contract for one completed tick. Called from both arms, so the two cannot drift apart in what they check.
    /// </summary>
    /// <remarks>
    /// <para><b>The affordance is recomputed from the telemetry the controller itself used</b> — <c>MeasuredNsPerEntity</c> is the estimate the admission
    /// decision was made against, so dividing the budget by it reproduces the decision rather than approximating it. Asserting against wall-clock would
    /// make the threshold a property of the machine.</para>
    /// <para>The identity is checked with a one-tick lag for the reason <c>ClusterThrottleBudgetTests</c> records: detection runs in AabbRefresh, which
    /// follows Migrate, so a drifter found on tick T is admitted or dropped by tick T+1's Prep.</para>
    /// </remarks>
    private static void AssertTickHonoursTheBudget(string arm, int tick, in SpatialMigrationTelemetry t, int previousDetected, int previousUnplaced)
    {
        // Guarded, because the division fails OPEN: a zero estimate makes the quotient +Infinity and a float-to-int conversion SATURATES rather than
        // throwing, so `affordable` becomes int.MaxValue and the bound becomes no bound at all. One line zeroing MeasuredNsPerEntity would leave every
        // budget assertion in this fixture and in ClusterThrottleBudgetTests green while AC-11.1 went unchecked in both.
        Assert.That(t.MeasuredNsPerEntity, Is.GreaterThan(0d),
            $"{arm}, tick {tick}: no per-entity estimate was reported, so the bound below would divide by zero and saturate to int.MaxValue");

        var affordable = (int)((BudgetMs * 1_000_000d) / t.MeasuredNsPerEntity);
        Assert.That(t.MigrationCount, Is.LessThanOrEqualTo(affordable),
            $"{arm}, tick {tick}: executed {t.MigrationCount} migrations against a budget that pays for {affordable} at the "
            + $"{t.MeasuredNsPerEntity:F0} ns/entity it measured");

        if (previousDetected >= 0)
        {
            Assert.That(t.MigrationCount + t.RelocationsThrottled + previousUnplaced, Is.EqualTo(previousDetected),
                $"{arm}, tick {tick}: {previousDetected} drifters detected on the previous tick, but {t.MigrationCount} moved + "
                + $"{t.RelocationsThrottled} throttled + {previousUnplaced} unplaced — a drifter was counted twice or lost");
        }
    }

    private static ArmResult RunSerial(DatabaseEngine dbe)
    {
        var admitted = 0;
        var throttled = 0;
        var previousDetected = -1;
        var previousUnplaced = 0;

        for (var tick = 2; tick <= SettledTick; tick++)
        {
            if (tick <= LastMotionTick)
            {
                MoveTo(dbe, tick);
            }

            dbe.WriteTickFence(tick);

            var t = dbe.GetSpatialTelemetry(ArchetypeId);
            AssertTickHonoursTheBudget("the serial WriteTickFence", tick, in t, previousDetected, previousUnplaced);

            admitted += t.MigrationCount;
            throttled += t.RelocationsThrottled;
            previousDetected = t.DriftersDetected;
            previousUnplaced = t.DriftersUnplaced;
        }

        return new ArmResult(admitted, throttled);
    }

    /// <summary>
    /// Drive the same motion schedule through a real runtime at <paramref name="workerCount"/> workers, checking the contract on every tick.
    /// </summary>
    /// <remarks>
    /// <para>The motion and the assertions both run inside a callback system, so they sit in the same relationship to the fence as the serial arm's loop
    /// does — and the motion is keyed on the tick number rather than on a call count, so the world at tick N is the same world whichever arm produced
    /// it.</para>
    /// <para><b>The assertion runs on the runtime's thread, so its failure is captured rather than thrown.</b> An exception escaping a callback system is
    /// swallowed into <c>UnhandledExceptionCallback</c>; re-raised on the test thread after shutdown, it reports as an ordinary assertion failure.</para>
    /// </remarks>
    private static ArmResult RunParallel(DatabaseEngine dbe, int workerCount)
    {
        var arm = $"the parallel fence at W={workerCount}";
        var ticks = 0;
        Exception assertionFailure = null;

        // Under a lock rather than as plain captured locals. The callback runs on the runtime's worker threads, and although the scheduler's inter-tick
        // barrier plus Shutdown()'s join make plain fields correct in practice on x64, this repository's memory-ordering discipline is explicitly written
        // for arm64, where a plain store carries no release. `ticks` was already Interlocked; these deserve the same honesty, and a lock taken once per
        // tick on a 100 Hz loop costs nothing measurable.
        var gate = new object();
        var admitted = 0;
        var throttled = 0;
        var previousDetected = -1;
        var previousUnplaced = 0;

        using var runtime = TyphonRuntime.Create(dbe, schedule =>
            {
                schedule.PublicTrack.DeclareDag("Test").CallbackSystem("Sample", _ =>
                {
                    var tick = Interlocked.Increment(ref ticks) + 1;
                    if (tick <= LastMotionTick)
                    {
                        MoveTo(dbe, tick);
                    }

                    if (tick > SettledTick)
                    {
                        return;
                    }

                    try
                    {
                        var t = dbe.GetSpatialTelemetry(ArchetypeId);
                        lock (gate)
                        {
                            AssertTickHonoursTheBudget(arm, tick, in t, previousDetected, previousUnplaced);
                            admitted += t.MigrationCount;
                            throttled += t.RelocationsThrottled;
                            previousDetected = t.DriftersDetected;
                            previousUnplaced = t.DriftersUnplaced;
                        }
                    }
                    catch (Exception ex)
                    {
                        Interlocked.CompareExchange(ref assertionFailure, ex, null);
                    }
                });
            }, new RuntimeOptions
            {
                WorkerCount = workerCount,
                // 100 Hz for the reason ClusterDriftParallelTests records: at 1000 Hz the fence overruns its tick and disposes accessors under the next
                // tick's feet, which fails for reasons unrelated to what is asserted.
                BaseTickRate = 100,
                EnableParallelFence = true,
            });

        Exception unhandled = null;
        runtime.Scheduler.UnhandledExceptionCallback = (_, _, ex) => Interlocked.CompareExchange(ref unhandled, ex, null);

        runtime.Start();
        SpinWait.SpinUntil(() => Volatile.Read(ref ticks) >= SettledTick, TimeSpan.FromSeconds(20));
        runtime.Shutdown();

        if (assertionFailure != null)
        {
            throw assertionFailure;
        }

        Assert.Multiple(() =>
        {
            Assert.That(unhandled, Is.Null, $"the parallel fence must not throw while throttling. Got: {unhandled}");
            Assert.That(ticks, Is.GreaterThanOrEqualTo(SettledTick),
                "the runtime did not reach the settled tick, so its world is at a different point in the motion schedule from the other arms");
        });

        lock (gate)
        {
            return new ArmResult(admitted, throttled);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // AC-11.6 — where the determinism half lives, and why it is not here
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    //
    // There is deliberately no repeat-run test in this fixture, and the reason is a measurement rather than a
    // preference. AC-11.6 asks for determinism "under a fixed seed and fixed W", and the ADMITTED COUNT cannot deliver
    // it: the budget buys ReclusterBudgetMs / MeasuredNsPerEntity entities and that estimate is a wall-clock
    // measurement of the previous tick, so two runs of one workload admit slightly different numbers — and a different
    // set of entities moved leaves different bounds to detect against, so the divergence compounds. Two W=1 runs of an
    // identical motion schedule were observed agreeing for four ticks and then separating.
    //
    // What IS deterministic is the partition given its inputs, and that is asserted directly and without timing in
    // ClusterThrottleBudgetTests.ThePartitionIsAPureFunctionOfItsInputs. This fixture covers the other half — that the
    // RULE holds at every worker count — which is what a real runtime is needed for.
}
