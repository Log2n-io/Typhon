using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Typhon.Client;
using Typhon.Engine.Internals;
using Typhon.Protocol;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests.Runtime;

// ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// P1-10's own schema. It is not ProjectionTestSchema's: the motion rule needs a component carrying a two-axis velocity so `VelocityFrom` has something to
// resolve against, and ProjBounds' single `Speed` float is one axis short. Everything else is deliberately minimal — one spatial component, one byte of state
// so an enter record has a body, and nothing whose change could be mistaken for motion.
// ══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════

[Component("Typhon.Test.Motion.Body", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct MotionBody
{
    [Field]
    [SpatialIndex]
    public AABB2F Bounds;

    [Field]
    public byte Tag;
}

/// <summary>Two consecutive floats, which is what <c>VelocityFrom</c> names when the position has two axes.</summary>
[Component("Typhon.Test.Motion.Velocity", 1, StorageMode = StorageMode.SingleVersion)]
[StructLayout(LayoutKind.Sequential, Pack = 4)]
struct MotionVelocity
{
    [Field]
    public float Vx;

    [Field]
    public float Vy;
}

[Archetype]
partial class MotionMover : Archetype<MotionMover>
{
    public static readonly Comp<MotionBody> Body = Register<MotionBody>();
    public static readonly Comp<MotionVelocity> Velocity = Register<MotionVelocity>();
}

/// <summary>
/// P1-10 — the motion rule: when a position becomes a segment, what that segment's velocity is fitted from, and what a client rebuilds from it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Positions are written straight into the cluster column</b>, not through a transaction. That is the write path SUB-10 exists for — it sets no dirty bit
/// and signals nothing — and it is also the only way to place an entity on an exact coordinate on an exact tick, which every threshold here is measured
/// against. A transaction per tick would add a commit to each of several hundred ticks for no property under test.
/// </para>
/// <para>
/// <b>The thresholds are the declaration's, restated here so a case reads as arithmetic rather than as magic.</b> At ±8 192 m over 24 bits the position
/// quantum is 2⁻¹⁰ m; the tolerance is 5 cm, the teleport threshold 20 m/s over a 0.1 s tick (so 2 m of step), and the heartbeat 5 s (so 50 ticks).
/// </para>
/// </remarks>
[TestFixture]
[NonParallelizable]
unsafe class MotionSegmentTests : TestBase<MotionSegmentTests>
{
    private const long AmpleBudget = 16L * 1024 * 1024;

    /// <summary>The world the grid spans: ±8 192 m, so 24 bits per axis is a 2⁻¹⁰ m position quantum — SWG's numbers.</summary>
    private const float WorldExtentM = 8192f;

    /// <summary>The position quantum that follows: 2⁻¹⁰ m.</summary>
    private const double PositionStepM = 1.0 / 1024.0;

    /// <summary>The tick period every case runs at, and the one the plans are compiled against: 10 Hz.</summary>
    private const double TickPeriodSeconds = 0.1;

    private const int TickPeriodUs = 100_000;

    /// <summary>The declared extrapolation tolerance.</summary>
    private const double ToleranceM = 0.05;

    /// <summary>The declared teleport threshold, in metres per second: 2 m of step at 10 Hz.</summary>
    private const double TeleportMps = 20.0;

    /// <summary>The declared heartbeat: 5 s, which is 50 ticks at 10 Hz.</summary>
    private const double MaxAgeSeconds = 5.0;

    private const uint MaxAgeTicks = 50;

    private static readonly string[] SystemNames = [];

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class Harness : IDisposable
    {
        public DatabaseEngine Engine;
        public CompiledProjectionPlan Plan;
        public SubscriptionsRegistry Registry;
        public ArchetypeReplicationState State;
        public NetIdAllocator NetIds;
        public ArchetypeClusterState ClusterState;
        public int ChunkId;
        private ResourceRegistry _registry;

        public ReplicationBlockLayout Layout => Plan.BlockLayout;

        public CompiledPosition Position => Plan.Position;

        /// <summary>The block describing the one cluster every case spawns into.</summary>
        public ReplicationBlockHeader* Block
        {
            get
            {
                State.Directory.TryGetBlock(ChunkId, out var block);
                return block;
            }
        }

        public ReplicationHotEntry* Hot(int slot) =>
            (ReplicationHotEntry*)((byte*)Block + Layout.HotOffset + (slot * Layout.HotStride));

        public byte* Cold(int slot) => (byte*)Block + Layout.ColdOffset + (slot * Layout.ColdStride);

        /// <summary>Marks every occupied slot watched for <paramref name="tick"/> and runs the pass: one tick of the track for this archetype.</summary>
        public void Tick(uint tick)
        {
            using var guard = EpochGuard.Enter(Engine.EpochManager);
            using var accessor = ClusterState.ClusterSegment.CreateChunkAccessor();

            var clusterBase = accessor.GetChunkAddress(ChunkId);
            var occupancy = *(ulong*)clusterBase;
            State.WatchedBlocks.ClearMasks();
            State.BeginWatchedBlocks(tick);
            var block = Block;
            var bits = occupancy;
            while (bits != 0)
            {
                var slot = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                State.WatchedBlocks.Mark(block, slot);
            }

            State.BeginProjectTick(workers: 1);
            ProjectionPass.ProjectBlock(Plan, 0, State, 0, block, clusterBase, null, tick);
        }

        /// <summary>Writes one slot's spatial value directly into its column — the write path that signals nothing (SUB-10).</summary>
        public void PlaceAt(int slot, double x, double y)
        {
            using var guard = EpochGuard.Enter(Engine.EpochManager);
            using var accessor = ClusterState.ClusterSegment.CreateChunkAccessor();
            var at = accessor.GetChunkAddress(ChunkId) + Position.ComponentOffsetInCluster + (slot * Position.ComponentSize)
                   + Position.FieldOffsetInComponent;
            var half = 0.5f;
            var box = new AABB2F
            {
                MinX = (float)x - half,
                MinY = (float)y - half,
                MaxX = (float)x + half,
                MaxY = (float)y + half,
            };
            *(AABB2F*)at = box;
        }

        /// <summary>Writes one slot's declared velocity column, in metres per second.</summary>
        public void SetDeclaredVelocity(int slot, double vx, double vy)
        {
            using var guard = EpochGuard.Enter(Engine.EpochManager);
            using var accessor = ClusterState.ClusterSegment.CreateChunkAccessor();
            var at = accessor.GetChunkAddress(ChunkId) + Position.VelocityComponentOffsetInCluster + (slot * Position.VelocityComponentSize)
                   + Position.VelocityFieldOffsetInComponent;
            ((float*)at)[0] = (float)vx;
            ((float*)at)[1] = (float)vy;
        }

        // ── Reading a segment back ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

        private byte* Segment(int slot) => (byte*)Hot(slot) + Layout.SegmentOffsetInHotEntry;

        private int PosBytes => Position.Pos.Bits / 8;

        private int VelBytes => Position.Vel == null ? 0 : Position.Vel.Bits / 8;

        /// <summary>The segment's start position in metres, axis by axis, decoded exactly as a client decodes it.</summary>
        public double P0(int slot, int axis) =>
            WireMath.DecodeQuant(ReadCode(Segment(slot) + (axis * PosBytes), PosBytes), Position.Pos.Min[axis], Position.Pos.Max[axis], Position.Pos.Bits);

        /// <summary>The segment's velocity code on one axis, signed, as it sits on the wire.</summary>
        public int VelocityCode(int slot, int axis) =>
            ReadSigned(Segment(slot) + (Position.Dims * PosBytes) + (axis * VelBytes), VelBytes);

        /// <summary>The segment's velocity on one axis, in metres per tick.</summary>
        public double Velocity(int slot, int axis) =>
            WireMath.DecodeVel(VelocityCode(slot, axis), (int)Position.Vel.UnitExp, Position.Vel.Bits);

        /// <summary>The segment's <c>t0</c> as it travels: the low 16 bits of its start tick (W9).</summary>
        public ushort TickLo(int slot)
        {
            var at = Segment(slot) + (Position.Dims * PosBytes) + (Position.Dims * VelBytes);
            return (ushort)(at[0] | (at[1] << 8));
        }

        /// <summary>The segment's motion epoch.</summary>
        public byte Epoch(int slot) => Segment(slot)[Layout.SegmentBytes - 1];

        /// <summary>The segment's absolute start tick, which the hot entry's motion tick slot holds in full.</summary>
        public uint SegmentTick(int slot) => Hot(slot)->GroupTicks[0];

        /// <summary>The tick the current run started at, from the cold entry's run-start pair.</summary>
        public uint RunStartTick(int slot) => ReadCode(Cold(slot) + Layout.RunStartOffsetInColdEntry + (Position.Dims * PosBytes), 4);

        public long Segments => State.SegmentsEmitted;

        public long Shadow => State.ShadowSegmentsEmitted;

        // ── Construction ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

        public static Harness Create(IServiceProvider services, Action<MotionBuilder> motion = null, int entities = 1,
            Func<CompiledProjectionPlan, CompiledProjectionPlan> rewrite = null)
        {
            var engine = services.GetRequiredService<DatabaseEngine>();
            engine.RegisterComponentFromAccessor<MotionBody>();
            engine.RegisterComponentFromAccessor<MotionVelocity>();
            engine.ConfigureSpatialGrid(SpatialGridConfig.Flat(
                worldMin: new Vector2(-WorldExtentM, -WorldExtentM),
                worldMax: new Vector2(WorldExtentM, WorldExtentM),
                cellSize: 256f));
            engine.InitializeArchetypes();

            var subs = new SubscriptionsRegistry();
            subs.Archetype<MotionMover>(a => a
                .Motion(MotionMover.Body, motion ?? DefaultMotion)
                .Field(MotionMover.Body, b => b.Tag, Codec.U8, name: "tag"));

            var plan = ProjectionCompiler.Compile(subs, engine, TickPeriodSeconds, largestTickMultiplier: 1)[0];
            if (rewrite != null)
            {
                plan = rewrite(plan);
            }

            var registry = new ResourceRegistry(new ResourceRegistryOptions { Name = "MotionSegmentTests" });
            var netIds = new NetIdAllocator("NetIds", registry.Runtime);
            var state = new ArchetypeReplicationState("Mover", registry.Runtime, engine.MemoryAllocator, plan.BlockLayout,
                new SubscriptionsOptions { StatePoolBudgetBytes = AmpleBudget }, netIds)
            {
                TickPeriodSeconds = TickPeriodSeconds,
            };

            var harness = new Harness
            {
                Engine = engine,
                Plan = plan,
                Registry = subs,
                State = state,
                NetIds = netIds,
                _registry = registry,
                ClusterState = engine._archetypeStates[plan.ArchetypeCatalogId].ClusterState,
            };

            harness.Spawn(entities);
            harness.AttachBlock();
            return harness;
        }

        private void Spawn(int count)
        {
            using var tx = Engine.CreateQuickTransaction();
            for (var i = 0; i < count; i++)
            {
                var body = new MotionBody { Bounds = new AABB2F { MinX = -0.5f, MinY = -0.5f, MaxX = 0.5f, MaxY = 0.5f }, Tag = 1 };
                var velocity = new MotionVelocity();
                tx.Spawn<MotionMover>(MotionMover.Body.Set(in body), MotionMover.Velocity.Set(in velocity));
            }

            tx.Commit();
        }

        private void AttachBlock()
        {
            using var guard = EpochGuard.Enter(Engine.EpochManager);
            var ids = ClusterState.ReadActiveClusterList(out var count);
            Assert.That(count, Is.EqualTo(1), "every case here spawns into one cluster, so one block describes the whole fixture");
            ChunkId = ids[0];
            Assert.That(State.TryAttachBlock(ChunkId, out _), Is.True);
        }

        public void Dispose()
        {
            State?.Dispose();
            NetIds?.Dispose();
            _registry?.Dispose();
            Engine?.Dispose();
        }
    }

    private static void DefaultMotion(MotionBuilder m) => m.Tolerance(ToleranceM).Teleport(TeleportMps).MaxAge(MaxAgeSeconds);

    private static uint ReadCode(byte* at, int bytes)
    {
        uint value = 0;
        for (var i = 0; i < bytes; i++)
        {
            value |= (uint)at[i] << (8 * i);
        }

        return value;
    }

    private static int ReadSigned(byte* at, int bytes)
    {
        var raw = ReadCode(at, bytes);
        var shift = 32 - (bytes * 8);
        return shift == 0 ? (int)raw : (int)(raw << shift) >> shift;
    }

    // ── The triggers ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A straight run at constant speed refits at most once per <c>MaxAge</c>: the heartbeat is the only thing that fires, because the run-start window makes
    /// the fitted velocity good enough that the 5 cm tolerance is never reached.
    /// </summary>
    [Test]
    public void AStraightRunRefitsAtMostOncePerMaxAge()
    {
        using var harness = Harness.Create(ServiceProvider);
        const int ticks = 300;
        const double speed = 0.1;                        // metres per tick, a 1 m/s walk at 10 Hz

        for (uint t = 1; t <= ticks; t++)
        {
            harness.PlaceAt(0, 100 + (speed * t), 200);
            harness.Tick(t);
        }

        // One enter, one refit on the second tick (the enter segment is stationary, so the first real step exceeds the tolerance at once), then a heartbeat
        // every MaxAge ticks and nothing else.
        var ceiling = 2 + ((ticks / (long)MaxAgeTicks) + 1);
        Assert.Multiple(() =>
        {
            Assert.That(harness.Segments, Is.LessThanOrEqualTo(ceiling),
                $"a straight run should refit only on the heartbeat; {harness.Segments} segments over {ticks} ticks");
            Assert.That(harness.Segments, Is.GreaterThanOrEqualTo(ticks / (long)MaxAgeTicks), "a moving segment still gets its heartbeat");
            Assert.That(harness.Velocity(0, 0), Is.EqualTo(speed).Within(PositionStepM), "the fitted velocity is the run's");
            Assert.That(harness.Velocity(0, 1), Is.EqualTo(0d).Within(PositionStepM));
        });

        TestContext.Out.WriteLine($"straight run: {harness.Segments} segments / {ticks} ticks, shadow {harness.Shadow}");
    }

    /// <summary>
    /// A gradual arc reaches a client as a segment inside the turn band 02 § 4 states — before the extrapolation it is holding has walked off the path.
    /// </summary>
    /// <remarks>
    /// Under the shipped policy the two triggers race, and which of them wins is a property of the turn RATE: the extrapolation error grows as
    /// <c>v·θ²/2δ</c> while the run departure grows as <c>v·θ</c>, so a slow turn crosses the 5 cm tolerance first and a fast one leaves the run first.
    /// What a client is promised is the same either way, and it is what this asserts. Which trigger fires on its own is
    /// <see cref="TheRunDepartureTestCatchesAGradualArcInsideTheTurnBand"/>.
    /// </remarks>
    [Test]
    public void AGradualArcTriggersInsideTheTurnBand()
    {
        using var harness = Harness.Create(ServiceProvider);
        var arc = RunArc(harness, speed: 0.5, degreesPerTick: 1.0);

        TestContext.Out.WriteLine($"arc at 0.5 m/tick, 1 deg/tick, 5 cm tolerance: segment after {arc.SegmentTurn} deg, run restart after {arc.RestartTurn} "
            + $"deg; {harness.Segments} segments / {ArcTicks} ticks, shadow {harness.Shadow}");
        Assert.Multiple(() =>
        {
            Assert.That(arc.SegmentTurn, Is.InRange(1, 11),
                "a turning entity must produce a segment no later than the top of 02 § 4's band, or the client is drawing a straight line through a corner");
            Assert.That(arc.RestartTurn, Is.InRange(1, 11), "and the run must have restarted by then, so the refit measures the new heading");
        });
    }

    /// <summary>
    /// The run-departure test on its own: with the tolerance widened so it cannot pre-empt, the step leaving the run's mean velocity is what catches a gradual
    /// arc, and it does so at the ≈ 6–11° of accumulated turn 02 § 4 states.
    /// </summary>
    /// <remarks>
    /// This is the number the design doc puts in writing, and it is worth pinning on its own: it is what a per-tick comparison — this step against the last —
    /// cannot see, because one degree of turn moves the step by 8 mm at these speeds and a gradual arc never produces a step that looks wrong beside its
    /// immediate neighbour. Measuring against the RUN is what accumulates it.
    /// </remarks>
    [Test]
    public void TheRunDepartureTestCatchesAGradualArcInsideTheTurnBand()
    {
        using var harness = Harness.Create(ServiceProvider, m => m.Tolerance(1.0).Teleport(TeleportMps).MaxAge(MaxAgeSeconds));
        var arc = RunArc(harness, speed: 0.5, degreesPerTick: 1.0);

        TestContext.Out.WriteLine(
            $"arc at 0.5 m/tick, 1 deg/tick, tolerance held off: run restart after {arc.RestartTurn} deg, segment after {arc.SegmentTurn} deg");
        Assert.That(arc.RestartTurn, Is.InRange(6, 11), "02 § 4 states the run-departure test catches a gradual arc at roughly 6 to 11 degrees of turn");
    }

    /// <summary>
    /// Twelve straight ticks then a constant-rate turn, reporting the accumulated turn at which the run restarted and at which a new segment was emitted.
    /// </summary>
    private static (int RestartTurn, int SegmentTurn) RunArc(Harness harness, double speed, double degreesPerTick)
    {
        var turnPerTick = degreesPerTick * Math.PI / 180;
        double x = 0, y = 0;
        uint tick = 1;
        harness.PlaceAt(0, x, y);
        harness.Tick(tick);
        for (tick = 2; tick <= 12; tick++)
        {
            x += speed;
            harness.PlaceAt(0, x, y);
            harness.Tick(tick);
        }

        var runBefore = harness.RunStartTick(0);
        var segmentBefore = harness.SegmentTick(0);
        var turned = 0;
        var heading = 0d;
        var restartTurn = -1;
        var segmentTurn = -1;
        for (; tick <= ArcTicks; tick++)
        {
            heading += turnPerTick;
            turned++;
            x += speed * Math.Cos(heading);
            y += speed * Math.Sin(heading);
            harness.PlaceAt(0, x, y);
            harness.Tick(tick);
            if (restartTurn < 0 && harness.RunStartTick(0) != runBefore)
            {
                restartTurn = turned;
            }

            if (segmentTurn < 0 && harness.SegmentTick(0) != segmentBefore)
            {
                segmentTurn = turned;
            }
        }

        return (restartTurn, segmentTurn);
    }

    /// <summary>The tick the arc cases run to, so the segments they count are comparable with the straight and jittery runs.</summary>
    private const uint ArcTicks = 212;

    /// <summary>A step beyond the teleport threshold is a discontinuity: the epoch advances so nothing interpolates across it, and the run restarts.</summary>
    [Test]
    public void ATeleportBumpsTheEpochAndRestartsTheRun()
    {
        using var harness = Harness.Create(ServiceProvider);

        for (uint t = 1; t <= 10; t++)
        {
            harness.PlaceAt(0, 100 + (0.1 * t), 200);
            harness.Tick(t);
        }

        var epochBefore = harness.Epoch(0);

        // 50 m in one tick, against a threshold of 20 m/s x 0.1 s = 2 m.
        harness.PlaceAt(0, 151, 200);
        harness.Tick(11);

        Assert.Multiple(() =>
        {
            Assert.That(harness.Epoch(0), Is.EqualTo((byte)(epochBefore + 1)), "a teleport advances the epoch");
            Assert.That(harness.SegmentTick(0), Is.EqualTo(11u), "a teleport always emits");
            Assert.That(harness.RunStartTick(0), Is.EqualTo(11u), "and the run begins again where the entity landed");
            Assert.That(harness.VelocityCode(0, 0), Is.Zero, "nothing is known about the velocity on the far side of a teleport");
            Assert.That(harness.P0(0, 0), Is.EqualTo(151d).Within(PositionStepM));
        });
    }

    /// <summary>A stationary entity cannot drift, so it gets no heartbeat: its enter segment stands for as long as it stands still.</summary>
    [Test]
    public void AStationaryEntityNeverGetsAHeartbeat()
    {
        using var harness = Harness.Create(ServiceProvider);
        harness.PlaceAt(0, 40, 60);

        for (uint t = 1; t <= 5 * MaxAgeTicks; t++)
        {
            harness.Tick(t);
        }

        Assert.Multiple(() =>
        {
            Assert.That(harness.Segments, Is.EqualTo(1), "the enter segment, and nothing after it");
            Assert.That(harness.SegmentTick(0), Is.EqualTo(1u), "the motion tick never moved, so no session is ever sent a segment again");
            Assert.That(harness.VelocityCode(0, 0), Is.Zero);
        });
    }

    /// <summary>
    /// <c>t0</c> travels as the low 16 bits of its tick, so a segment emitted at 65 536 carries 0 — and the rule's own arithmetic never meets that wrap,
    /// because it reads the absolute tick out of the hot entry instead (W9).
    /// </summary>
    [Test]
    public void TheSegmentTickSurvivesTheSixteenBitWrap()
    {
        using var harness = Harness.Create(ServiceProvider);
        const uint wrap = 65536;

        // A metre a tick, then a tenth of one exactly on the wrap tick: the extrapolation is out by 0.9 m against a 5 cm tolerance, so the segment that lands
        // on 65 536 is the one whose low 16 bits are zero. The step itself is 0.1 m, far under the 2 m teleport threshold, so this is a refit and not a jump.
        for (var t = wrap - 6; t <= wrap + 4; t++)
        {
            var travelled = t - (wrap - 6);
            var x = 10 + (travelled <= 5 ? travelled * 1.0 : 5.0 + ((travelled - 5) * 0.1));
            harness.PlaceAt(0, x, 20);
            harness.Tick(t);
        }

        Assert.Multiple(() =>
        {
            Assert.That(harness.SegmentTick(0), Is.EqualTo(wrap), "the refit lands on the wrap tick itself");
            Assert.That(harness.TickLo(0), Is.Zero, "65535 -> 0: the wire carries the tick's low 16 bits and nothing else (W9)");
            Assert.That(WireMath.DecodeTickLo(harness.TickLo(0), wrap + 4), Is.EqualTo(wrap),
                "and a client four ticks later rebuilds the absolute tick from them");
            Assert.That(WireMath.DecodeTickLo(harness.TickLo(0), wrap), Is.EqualTo(wrap), "as does one on the tick itself");
        });
    }

    /// <summary>
    /// <c>VelocityFrom</c> replaces the measurement exactly: the emitted code is the declared field scaled to displacement per tick and quantized, with nothing
    /// of the observed displacement in it.
    /// </summary>
    [Test]
    public void VelocityFromBypassesTheMeasurementExactly()
    {
        using var harness = Harness.Create(ServiceProvider,
            m => m.Tolerance(ToleranceM).Teleport(TeleportMps).MaxAge(MaxAgeSeconds).VelocityFrom(MotionMover.Velocity, v => v.Vx));

        Assert.That(harness.Position.VelocityIsDeclared, Is.True, "the declaration resolved to a column");

        const double declaredX = 3.25;   // metres per second
        const double declaredY = -1.5;
        harness.SetDeclaredVelocity(0, declaredX, declaredY);

        // The entity is moved a long way in one tick — far enough to refit, and NOT the declared velocity. What comes out must be the declaration's number.
        harness.PlaceAt(0, 500, 500);
        harness.Tick(1);
        harness.PlaceAt(0, 500.9, 500);
        harness.Tick(2);

        var expectedX = WireMath.EncodeVel(declaredX * TickPeriodSeconds, (int)harness.Position.Vel.UnitExp, harness.Position.Vel.Bits);
        var expectedY = WireMath.EncodeVel(declaredY * TickPeriodSeconds, (int)harness.Position.Vel.UnitExp, harness.Position.Vel.Bits);

        Assert.Multiple(() =>
        {
            Assert.That(harness.SegmentTick(0), Is.EqualTo(2u), "the step is 0.9 m against a 5 cm tolerance, so it refits");
            Assert.That(harness.VelocityCode(0, 0), Is.EqualTo(expectedX), "the declared velocity, quantized — not the 0.9 m the entity actually moved");
            Assert.That(harness.VelocityCode(0, 1), Is.EqualTo(expectedY), "and on an axis the entity did not move along at all");
        });
    }

    /// <summary>
    /// A <c>model: none</c> archetype's segment is a position sample: it carries <c>p0</c>, <c>t0</c> and an epoch and no velocity at all, and a new one is
    /// sent when the sample the client holds is more than the tolerance from where the entity is.
    /// </summary>
    [Test]
    public void AModelNoneArchetypeEmitsAPositionSampleWithNoVelocity()
    {
        using var harness = Harness.Create(ServiceProvider, rewrite: WithoutVelocity);

        Assert.That(harness.Position.Linear, Is.False, "the none model carries no velocity");
        Assert.That(harness.Layout.SegmentBytes, Is.EqualTo((harness.Position.Dims * 3) + 3), "p0 | t0 | epoch, and nothing between them");

        harness.PlaceAt(0, 300, 300);
        harness.Tick(1);

        // Well inside the tolerance: the sample the client holds is still good, so nothing is sent.
        harness.PlaceAt(0, 300.01, 300);
        harness.Tick(2);
        Assert.That(harness.SegmentTick(0), Is.EqualTo(1u), "a sample inside the tolerance is not resent");

        harness.PlaceAt(0, 300.4, 300);
        harness.Tick(3);
        Assert.Multiple(() =>
        {
            Assert.That(harness.SegmentTick(0), Is.EqualTo(3u), "beyond the tolerance, a new sample");
            Assert.That(harness.P0(0, 0), Is.EqualTo(300.4).Within(PositionStepM));
            Assert.That(harness.Segments, Is.EqualTo(2), "one enter sample, one update; no heartbeat, because a sample has no velocity to drift");
        });

        for (uint t = 4; t <= 3 + (3 * MaxAgeTicks); t++)
        {
            harness.Tick(t);
        }

        Assert.That(harness.Segments, Is.EqualTo(2), "and standing still for three heartbeats produces nothing");
    }

    /// <summary>
    /// The velocity codec's width was derived from the teleport threshold, so the two have to agree at the boundary: every displacement the rule will ever fit
    /// a velocity from fits the codec, and the emitted code never reaches its clamp.
    /// </summary>
    [Test]
    public void TheMeasuredVelocityNeverExceedsTheDerivedCodecLimit()
    {
        using var harness = Harness.Create(ServiceProvider);
        var position = harness.Position;
        var limit = WireMath.SymmetricLimit(position.Vel.Bits);
        var carried = Math.ScaleB(limit, (int)position.Vel.UnitExp);
        var teleportStep = TeleportMps * TickPeriodSeconds;

        Assert.That(carried, Is.GreaterThanOrEqualTo(teleportStep),
            $"the codec is {position.Vel.Bits} bits at 2^{position.Vel.UnitExp} m, carrying {carried} m per tick against a teleport step of "
          + $"{teleportStep} m — W5 derives the width from exactly this number");

        // Run at just under the threshold on both axes, which is the fastest thing the rule will ever measure without calling it a teleport.
        var step = (teleportStep * 0.99) / Math.Sqrt(2);
        var worst = 0;
        for (uint t = 1; t <= 40; t++)
        {
            harness.PlaceAt(0, 0 + (step * t), 0 + (step * t));
            harness.Tick(t);
            for (var a = 0; a < position.Dims; a++)
            {
                worst = Math.Max(worst, Math.Abs(harness.VelocityCode(0, a)));
            }
        }

        Assert.That(worst, Is.LessThan(limit), $"the widest code the run produced was {worst} against the codec's limit of {limit}");
    }

    // ── The shadow counter (AC-21) ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The rejected "the quantized velocity changed" trigger, counted beside the adopted one on the same run and from the same binary: on a straight path with
    /// a millimetre of jitter it fires on nearly every tick, which is the reason it was rejected.
    /// </summary>
    [Test]
    public void TheShadowCounterOutrunsTheAdoptedTriggerOnAJitteryRun()
    {
        using var harness = Harness.Create(ServiceProvider);
        const int ticks = 200;
        var random = new Random(20260917);

        for (uint t = 1; t <= ticks; t++)
        {
            // A straight walk with a millimetre of noise on it — float32's own rounding near the map edge, reproduced deterministically.
            var jitter = (random.NextDouble() - 0.5) * 0.002;
            harness.PlaceAt(0, 1000 + (0.1 * t) + jitter, 2000 + jitter);
            harness.Tick(t);
        }

        TestContext.Out.WriteLine($"jittery run: adopted {harness.Segments}, shadow {harness.Shadow} over {ticks} ticks");
        Assert.Multiple(() =>
        {
            Assert.That(harness.Segments, Is.GreaterThan(0), "the adopted trigger moves");
            Assert.That(harness.Shadow, Is.GreaterThan(0), "and so does the shadow");
            Assert.That(harness.Segments, Is.LessThan(harness.Shadow), "the adopted trigger is the smaller one, which is the whole of AC-21's claim");
        });
    }

    // ── Cost ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Steady-state motion allocates nothing on the managed heap: every byte the rule reads and writes is in the block.</summary>
    [Test]
    public void SteadyStateMotionAllocatesNothing()
    {
        using var harness = Harness.Create(ServiceProvider, entities: 32);
        for (uint t = 1; t <= 8; t++)
        {
            for (var i = 0; i < 32; i++)
            {
                harness.PlaceAt(i, 100 + (0.1 * t) + i, 200 + i);
            }

            harness.Tick(t);
        }

        // Entities keep moving inside the measured window: a tick that emits segments has to allocate as little as one that does not, and the arc below
        // makes several of them refit.
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (uint t = 9; t <= 120; t++)
        {
            for (var i = 0; i < 32; i++)
            {
                var heading = t * 0.02;
                harness.PlaceAt(i, 100 + (0.1 * t * Math.Cos(heading)) + i, 200 + (0.1 * t * Math.Sin(heading)) + i);
            }

            harness.Tick(t);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        TestContext.Out.WriteLine($"steady state: {allocated} bytes over 112 ticks of 32 moving entities, {harness.Segments} segments");
        Assert.That(allocated, Is.Zero, $"the projection pass allocated {allocated} bytes over 112 ticks of 32 moving entities");
    }

    // ── The client half ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// AC-11's shape in miniature: the segments this rule produces, fed through the client's own <see cref="MotionEvaluator"/>, rebuild a position that stays
    /// inside the declared tolerance plus one position quantum of the simulated truth for the whole run.
    /// </summary>
    /// <remarks>
    /// The client's maths is the client's: nothing here re-implements the extrapolation, so the two halves cannot quietly drift apart. What crosses between
    /// them is exactly what crosses on the wire — <c>p0</c>, <c>v</c>, <c>t0</c> and the epoch, decoded with the catalog the engine emitted.
    /// </remarks>
    [Test]
    public void TheClientReconstructsThePositionWithinTolerance()
    {
        using var harness = Harness.Create(ServiceProvider);
        var catalog = CatalogBuilder.Build(harness.Registry, [harness.Plan], CatalogBuilder.DefaultAppName, appRevision: 0, TickPeriodUs, SystemNames);
        var world = new WorldStore(CatalogPlan.Compile(catalog.Canonical));
        var store = world.Archetypes[0];

        Assert.That(store.Linear, Is.True, "the catalog carries the linear model this rule fits velocities for");

        const int ticks = 400;
        var truth = new double[ticks + 1, 2];
        var lastSegment = 0u;
        var received = 0;
        Span<double> segment = stackalloc double[2];
        Span<double> velocity = stackalloc double[2];
        Span<double> evaluated = stackalloc double[MotionEvaluator.MaxStride];
        var worst = 0d;

        for (uint t = 1; t <= ticks; t++)
        {
            // A patrol: two straight legs and a slow arc between them, which exercises every trigger the rule has bar the teleport.
            var s = t * 0.25;
            var x = t < 150 ? 300 + s : t < 250 ? 337.5 + (0.25 * (t - 150) * Math.Cos((t - 150) * 0.01)) + 0.0 : 362.5 + (0.25 * (t - 250));
            var y = t < 150 ? 700d : t < 250 ? 700 + (0.25 * (t - 150) * Math.Sin((t - 150) * 0.01)) : 700 + (0.25 * 100 * Math.Sin(100 * 0.01));
            truth[t, 0] = x;
            truth[t, 1] = y;

            harness.PlaceAt(0, x, y);
            harness.Tick(t);

            // The wire's own contract: a session is handed a segment exactly when the motion tick moved.
            if (harness.SegmentTick(0) != lastSegment)
            {
                lastSegment = harness.SegmentTick(0);
                received++;
                for (var a = 0; a < 2; a++)
                {
                    segment[a] = harness.P0(0, a);
                    velocity[a] = harness.Velocity(0, a);
                }

                var t0 = WireMath.DecodeTickLo(harness.TickLo(0), t);
                if (received == 1)
                {
                    store.Segments.Reset(0, segment, velocity, t0, harness.Epoch(0));
                }
                else
                {
                    store.Segments.Push(0, segment, velocity, t0, harness.Epoch(0));
                }
            }

            // Render time is the tick just replicated, which is what a client with no render delay would draw.
            MotionEvaluator.EvaluateSlot(store, 0, t, 0, evaluated);
            var dx = evaluated[0] - x;
            var dy = evaluated[1] - y;
            worst = Math.Max(worst, Math.Sqrt((dx * dx) + (dy * dy)));
        }

        var budget = ToleranceM + PositionStepM;
        TestContext.Out.WriteLine($"client cross-check: worst reconstruction error {worst:F5} m over {ticks} ticks, {received} segments, budget {budget:F5} m");
        Assert.That(worst, Is.LessThanOrEqualTo(budget),
            "every sampled client position must sit within the tolerance plus one quantum of the truth — AC-11's claim, in miniature");
    }

    // ── Plan surgery ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The compiled plan with its velocity taken away: the <c>none</c> model. The declaration API has no way to ask for it yet (the builder always derives a
    /// velocity codec for a moving archetype), so the plan is rewritten here rather than the wire rule going untested until it does.
    /// </summary>
    private static CompiledProjectionPlan WithoutVelocity(CompiledProjectionPlan plan)
    {
        var from = plan.Position;
        var positionBytes = from.Dims * (from.Pos.Bits / 8);
        var position = new CompiledPosition
        {
            Moving = from.Moving,
            Linear = false,
            Dims = from.Dims,
            ComponentSlot = from.ComponentSlot,
            ComponentOffsetInCluster = from.ComponentOffsetInCluster,
            ComponentSize = from.ComponentSize,
            FieldOffsetInComponent = from.FieldOffsetInComponent,
            SpatialFieldType = from.SpatialFieldType,
            Pos = from.Pos,
            Vel = null,
            PositionStep = from.PositionStep,
            FinestPositionStep = from.FinestPositionStep,
            ToleranceMetres = from.ToleranceMetres,
            TeleportMaxSpeedMps = from.TeleportMaxSpeedMps,
            MaxAgeSeconds = from.MaxAgeSeconds,
            IgnoresTickDilation = from.IgnoresTickDilation,
            SizedForTickMultiplier = from.SizedForTickMultiplier,
            VelocityComponentSlot = from.VelocityComponentSlot,
            VelocityComponentOffsetInCluster = from.VelocityComponentOffsetInCluster,
            VelocityComponentSize = from.VelocityComponentSize,
            VelocityFieldOffsetInComponent = from.VelocityFieldOffsetInComponent,
            SegmentBytes = positionBytes + 3,
        };

        var runStartBytes = (positionBytes + 4 + 3) & ~3;
        return new CompiledProjectionPlan
        {
            Name = plan.Name,
            ArchetypeType = plan.ArchetypeType,
            ArchetypeCatalogId = plan.ArchetypeCatalogId,
            DeclarationIndex = plan.DeclarationIndex,
            IsStatic = plan.IsStatic,
            ClusterLayout = plan.ClusterLayout,
            SlotCount = plan.SlotCount,
            Fields = plan.Fields,
            OnEnter = plan.OnEnter,
            Groups = plan.Groups,
            OwnerFields = plan.OwnerFields,
            OwnerGroups = plan.OwnerGroups,
            Position = position,
            BlockLayout = ReplicationBlockLayout.ForArchetype(plan.SlotCount, position.SegmentBytes, plan.MaxStateBodyBytes, positionBytes, runStartBytes,
                plan.OwnerEntrySize),
            OwnerEntrySize = plan.OwnerEntrySize,
            MaxStateBodyBytes = plan.MaxStateBodyBytes,
            TickSlotCount = plan.TickSlotCount,
        };
    }
}
