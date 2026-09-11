using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Typhon.Engine.Internals;
using Typhon.Schema.Definition;

namespace Typhon.Engine.Tests;

/// <summary>
/// #910 T0: a cell crossing is classified where it is filed — a step into a neighbour, or a jump further — a destination clamped into an edge cell is
/// counted and warned about, and the drain prefix is put in destination-cell order once, in the Prep tail, where the size of each arrival is read off
/// its runs.
/// </summary>
[TestFixture]
[NonParallelizable]   // drives the static PrepQueueProbe
class SmartTeleportationTests : TestBase<SmartTeleportationTests>
{
    // ClusterMigrationTests' geometry: 10×10 cells of 100 over a 1000×1000 flat world.
    private const float CellSize = 100f;
    private const float WorldMax = 1000f;

    private WarningCapture _warnings;

    public override void Setup()
    {
        base.Setup();

        // Rebuilt with a capturing provider beside the fixture's own, so the engine's ILogger<DatabaseEngine> reaches it. The first provider has resolved
        // nothing an engine depends on.
        _warnings = new WarningCapture();
        ServiceCollection.AddSingleton<ILoggerProvider>(_warnings);
        (ServiceProvider as IDisposable)?.Dispose();
        ServiceProvider = ServiceCollection.BuildServiceProvider();
        Logger = ServiceProvider.GetRequiredService<ILogger<SmartTeleportationTests>>();
    }

    private static ushort ArchetypeId => Archetype<ClMigUnit>.Metadata.ArchetypeId;

    private static ClMigPos PointAt(float x, float y) => new() { Bounds = new AABB2F { MinX = x, MinY = y, MaxX = x, MaxY = y } };

    private DatabaseEngine SetupEngine() =>
        SetupEngine(SpatialGridConfig.Flat(worldMin: new Vector2(0, 0), worldMax: new Vector2(WorldMax, WorldMax), cellSize: CellSize));

    private DatabaseEngine SetupEngine(SpatialGridConfig config)
    {
        var dbe = ServiceProvider.GetRequiredService<DatabaseEngine>();
        dbe.RegisterComponentFromAccessor<ClMigPos>();
        dbe.RegisterComponentFromAccessor<ClMigScratch>();
        dbe.ConfigureSpatialGrid(config);
        dbe.InitializeArchetypes();
        return dbe;
    }

    private static EntityId[] Spawn(DatabaseEngine dbe, params (float X, float Y)[] points)
    {
        var ids = new EntityId[points.Length];
        using var tx = dbe.CreateQuickTransaction();
        for (var i = 0; i < points.Length; i++)
        {
            ids[i] = tx.Spawn<ClMigUnit>(ClMigUnit.Pos.Set(PointAt(points[i].X, points[i].Y)), ClMigUnit.Scratch.Set(default));
        }

        tx.Commit();
        return ids;
    }

    /// <summary>
    /// One transaction of writes: through <c>OpenMut</c> (the fence's dirty-bit scan files the crossing) or the spatial barrier (the drain does).
    /// </summary>
    private static void Move(DatabaseEngine dbe, bool throughBarrier, params (EntityId Id, float X, float Y)[] moves)
    {
        if (!throughBarrier)
        {
            using var tx = dbe.CreateQuickTransaction();
            foreach (var (id, x, y) in moves)
            {
                var eref = tx.OpenMut(id);
                eref.Write(ClMigUnit.Pos).Bounds = PointAt(x, y).Bounds;
            }

            tx.Commit();
            return;
        }

        var located = moves.Select(m => (Location: Locate(dbe, m.Id), m.X, m.Y)).ToArray();
        using (var tx = dbe.CreateQuickTransaction())
        {
            var accessor = tx.For<ClMigUnit>();
            foreach (var cluster in accessor.GetClusterEnumerator())
            {
                foreach (var (location, x, y) in located)
                {
                    if (location.ChunkId == cluster.ChunkId)
                    {
                        cluster.WriteSpatial(ClMigUnit.Pos, location.Slot, PointAt(x, y));
                    }
                }
            }

            accessor.Dispose();
            tx.Commit();
        }
    }

    private static unsafe (int ChunkId, int Slot) Locate(DatabaseEngine dbe, EntityId id)
    {
        var cs = dbe._archetypeStates[ArchetypeId].ClusterState;
        using var epoch = EpochGuard.Enter(dbe.EpochManager);
        var accessor = cs.ClusterSegment.CreateChunkAccessor();
        try
        {
            for (var i = 0; i < cs.ActiveClusterCount; i++)
            {
                var chunkId = cs.ActiveClusterIds[i];
                var clusterBase = accessor.GetChunkAddress(chunkId);
                var occupancy = *(ulong*)clusterBase;
                while (occupancy != 0)
                {
                    var slot = BitOperations.TrailingZeroCount(occupancy);
                    occupancy &= occupancy - 1;
                    if (*(long*)(clusterBase + cs.Layout.EntityIdsOffset + slot * 8) == (long)id.RawValue)
                    {
                        return (chunkId, slot);
                    }
                }
            }
        }
        finally
        {
            accessor.Dispose();
        }

        return (-1, -1);
    }

    [TestCase(false)]
    [TestCase(true)]
    [VerifiesRule("SO-01")]
    public void ACornerNeighbourIsAStepAndThreeCellsIsAJump(bool throughBarrier)
    {
        using var dbe = SetupEngine();
        var ids = Spawn(dbe, (50f, 50f), (60f, 60f));
        dbe.WriteTickFence(1);

        // (0,0) → (1,1) is a corner neighbour: a step. (0,0) → (3,0) is three cells on X: a jump.
        Move(dbe, throughBarrier, (ids[0], 150f, 150f), (ids[1], 350f, 60f));
        dbe.WriteTickFence(2);

        var t = dbe.GetSpatialTelemetry(ArchetypeId);
        Assert.Multiple(() =>
        {
            Assert.That(t.MigrationCount, Is.EqualTo(2), "precondition: both crossed");
            Assert.That(t.JumpCrossings, Is.EqualTo(1));
            Assert.That(t.ClampedDestinations, Is.Zero);
            Assert.That(t.ArrivalCellsTouched, Is.EqualTo(2));
            Assert.That(t.LargestArrivalRun, Is.EqualTo(1));
        });
    }

    [Test]
    [VerifiesRule("SO-01")]
    public void AnOutOfWorldTeleportLandsInTheEdgeCellAndIsCountedAndWarnedOncePerWindow()
    {
        using var dbe = SetupEngine();
        var ids = Spawn(dbe, (550f, 550f));
        dbe.WriteTickFence(1);

        Move(dbe, false, (ids[0], -300f, 550f));
        dbe.WriteTickFence(2);
        var first = dbe.GetSpatialTelemetry(ArchetypeId);
        var cs = dbe._archetypeStates[ArchetypeId].ClusterState;
        var landedIn = cs.ClusterCellMap[Locate(dbe, ids[0]).ChunkId];

        // Inside the rate window: counted again, not warned again. (0,5) → (0,7), the X still outside the world.
        Move(dbe, false, (ids[0], -300f, 750f));
        dbe.WriteTickFence(3);
        var second = dbe.GetSpatialTelemetry(ArchetypeId);

        Assert.Multiple(() =>
        {
            Assert.That(landedIn, Is.EqualTo(dbe.SpatialGrid.WorldToCellKey(50f, 550f, 0f)), "clamping is unchanged: the entity lands in the edge cell");
            Assert.That(first.ClampedDestinations, Is.EqualTo(1));
            Assert.That(first.JumpCrossings, Is.EqualTo(1), "(5,5) → (0,5) is five cells");
            Assert.That(second.ClampedDestinations, Is.EqualTo(1));
            Assert.That(_warnings.Matching("clamped into an edge cell"), Is.EqualTo(1), "one warning per window, not one per tick");
        });
    }

    /// <summary>
    /// A 2D field's centre reports Z = 0, and on a grid whose Z extent excludes zero every such entity is filed in the clamped plane by construction. That
    /// is the convention (see <c>SpatialGrid.FlatPlaneZ</c>), not a position written outside the world, and must not count as one.
    /// </summary>
    [Test]
    public void ATwoDimensionalCrossingOnAGridWhoseDepthExcludesZeroIsNotClamped()
    {
        using var dbe = SetupEngine(new SpatialGridConfig(new Vector3D(0d, 0d, 500d), new Vector3D(WorldMax, WorldMax, 500d + CellSize), CellSize));
        var ids = Spawn(dbe, (50f, 50f));
        dbe.WriteTickFence(1);

        Move(dbe, false, (ids[0], 150f, 50f));
        dbe.WriteTickFence(2);

        var t = dbe.GetSpatialTelemetry(ArchetypeId);
        Assert.Multiple(() =>
        {
            Assert.That(t.MigrationCount, Is.EqualTo(1), "precondition: it crossed");
            Assert.That(t.ClampedDestinations, Is.Zero);
        });
    }

    [Test]
    [VerifiesRule("CR-01")]
    [VerifiesRule("SO-01")]
    public void TheLargestArrivalIsTheLongestDestinationRunOfTheDrainPrefix()
    {
        using var dbe = SetupEngine();
        var points = new (float, float)[20];
        for (var i = 0; i < points.Length; i++)
        {
            points[i] = (5f + i * 4f, 5f + i * 4f);
        }

        var ids = Spawn(dbe, points);
        dbe.WriteTickFence(1);

        // Twelve into (5,5), five into (1,0), three into (9,9) — enqueued interleaved, so only the sort makes each cell one run.
        var moves = new (EntityId, float, float)[ids.Length];
        for (var i = 0; i < ids.Length; i++)
        {
            if (i >= 17)
            {
                moves[i] = (ids[i], 950f, 910f + i);
            }
            else if (i % 4 == 3 || i == 16)
            {
                moves[i] = (ids[i], 150f + i, 50f);
            }
            else
            {
                moves[i] = (ids[i], 510f + i, 550f);
            }
        }

        var cs = dbe._archetypeStates[ArchetypeId].ClusterState;
        List<(MigrationKind Kind, int Cell)> prefix = null;
        ArchetypeClusterState.PrepQueueProbe = (state, _) =>
        {
            if (!ReferenceEquals(state, cs) || state.PendingMigrationDrainCount == 0)
            {
                return;
            }

            prefix = [];
            for (var i = 0; i < state.PendingMigrationDrainCount; i++)
            {
                prefix.Add((state.PendingMigrations[i].Kind, state.PendingMigrations[i].DestCellKey));
            }
        };

        try
        {
            Move(dbe, false, moves);
            dbe.WriteTickFence(2);
        }
        finally
        {
            ArchetypeClusterState.PrepQueueProbe = null;
        }

        var crossings = prefix.Where(r => r.Kind == MigrationKind.CellCrossing).GroupBy(r => r.Cell).ToArray();
        var t = dbe.GetSpatialTelemetry(ArchetypeId);
        Assert.Multiple(() =>
        {
            Assert.That(prefix.Select(r => r.Cell), Is.Ordered, "the drain prefix is in destination-cell order on the serial fence");
            Assert.That(crossings.Sum(g => g.Count()), Is.EqualTo(20), "precondition: every move filed a crossing");
            Assert.That(t.LargestArrivalRun, Is.EqualTo(crossings.Max(g => g.Count())));
            Assert.That(t.LargestArrivalRun, Is.EqualTo(12));
            Assert.That(t.ArrivalCellsTouched, Is.EqualTo(crossings.Length));
            Assert.That(t.ArrivalCellsTouched, Is.EqualTo(3));
            Assert.That(t.JumpCrossings, Is.EqualTo(15), "(0,0) → (5,5) and (0,0) → (9,9) jump; (0,0) → (1,0) steps");
        });
    }

    /// <summary>
    /// The outlier guard files after Migrate, so its crossing is classified in the tick that files it, and drained and grouped in the next (SO-01). A
    /// guarded crossing sits inside the hysteresis band, so it can be a jump only when <c>MigrationHysteresisRatio</c> is 1 or more — which is why this
    /// world uses 1.5.
    /// </summary>
    [Test]
    [VerifiesRule("SO-01")]
    public void AGuardedCrossingIsCountedWhenFiledAndGroupedWhenDrained()
    {
        using var dbe = SetupEngine(SpatialGridConfig.Flat(worldMin: new Vector2(0, 0), worldMax: new Vector2(WorldMax, WorldMax), cellSize: CellSize,
            migrationHysteresisRatio: 1.5f, reclusterBudgetMs: 0f));
        var ids = Spawn(dbe, (110f, 50f), (150f, 50f));
        dbe.WriteTickFence(1);

        // (1,0) → x = 320: inside the 150-unit band, so detection absorbs it, but the cluster now spans 210 > 1.2 × 100 and the guard files it, two
        // cells on.
        Move(dbe, false, (ids[1], 320f, 50f));
        dbe.WriteTickFence(2);
        var filed = dbe.GetSpatialTelemetry(ArchetypeId);

        dbe.WriteTickFence(3);
        var drained = dbe.GetSpatialTelemetry(ArchetypeId);
        var cs = dbe._archetypeStates[ArchetypeId].ClusterState;

        Assert.Multiple(() =>
        {
            Assert.That(filed.CrossingsExecuted, Is.Zero, "precondition: detection absorbed it, so nothing crossed on the tick it was written");
            Assert.That(filed.JumpCrossings, Is.EqualTo(1), "the guard's crossing is counted in the tick that files it");
            Assert.That(drained.CrossingsExecuted, Is.EqualTo(1), "and executes in the next");
            Assert.That(drained.JumpCrossings, Is.Zero, "not counted again when drained");
            Assert.That(drained.LargestArrivalRun, Is.EqualTo(1), "grouped when drained");
            Assert.That(cs.ClusterCellMap[Locate(dbe, ids[1]).ChunkId], Is.EqualTo(dbe.SpatialGrid.WorldToCellKey(320f, 50f, 0f)));
        });
    }

    private sealed class WarningCapture : ILoggerProvider
    {
        private readonly List<string> _warnings = [];

        public int Matching(string fragment)
        {
            lock (_warnings)
            {
                return _warnings.Count(w => w.Contains(fragment, StringComparison.Ordinal));
            }
        }

        public ILogger CreateLogger(string categoryName) => new Capturing(this);

        public void Dispose() { }

        private sealed class Capturing(WarningCapture owner) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                if (logLevel != LogLevel.Warning)
                {
                    return;
                }

                lock (owner._warnings)
                {
                    owner._warnings.Add(formatter(state, exception));
                }
            }
        }
    }
}
