// Systems for the SWG Light guide example — the tick-loop logic that turns the schema into a running world.
// Together with Program.cs this is the pair the `typhon new` scaffold emits as its starter template.
//
// Four systems, one per shape the runtime offers (ch.5):
//   • SpawnSystem       — CallbackSystem: non-entity work (periodically deploy a new drone).
//   • RoamSystem        — parallel QuerySystem: integrate Drift into Position (a lock-free SingleVersion write).
//   • FootprintSyncSystem — parallel cluster-native QuerySystem: keep the spatial Footprint coherent after movement (WriteSpatial barrier).
//   • HarvestSystem     — QuerySystem: accumulate Cargo each tick (a Versioned, transactional write).

using System;
using System.Numerics;
using Typhon.Engine;
using Typhon.Samples.Swg;
using Typhon.Schema.Definition;

namespace SwgGuide;

/// <summary>Non-entity work: every 30 ticks, deploy a fresh harvester drone at the origin. A CallbackSystem gets no
/// entity set — it runs once per tick and does whatever global/spawn work the frame needs.</summary>
internal sealed class SpawnSystem : CallbackSystem
{
    protected override void Configure(SystemBuilder b) => b
        .Name("Spawn")
        .Phase(Phase.Input)
        .Writes<Position>().Writes<Footprint>().Writes<Cargo>().Writes<Drift>().Writes<Extractor>();

    protected override void Execute(TickContext ctx)
    {
        if (ctx.TickNumber == 0 || ctx.TickNumber % 30 != 0)
        {
            return;
        }
        ctx.Transaction.Spawn<Harvester>(
            Harvester.Position.Set(new Position { P = new Point2F { X = 0f, Y = 0f } }),
            Harvester.Footprint.Set(new Footprint { Box = new AABB2F { MinX = 0f, MaxX = 0f, MinY = 0f, MaxY = 0f } }),
            Harvester.Cargo.Set(new Cargo { Amount = 0, Capacity = 1000 }),
            Harvester.Drift.Set(new Drift { Dx = 4f, Dy = 2f }),
            Harvester.Extractor.Set(new Extractor { ResourceKind = 1, Rate = 5 }));
        // no Commit — the scheduler commits this system's transaction at tick end.
    }
}

/// <summary>Move every drone. A parallel QuerySystem: the engine fans this body across workers, each handling a slice
/// of <c>ctx.Entities</c>. Position is SingleVersion, so the writes go through the per-worker <c>ctx.Accessor</c> —
/// no locks, no MVCC overhead.</summary>
internal sealed class RoamSystem : QuerySystem
{
    private readonly EcsView<Harvester> _drones;

    public RoamSystem(EcsView<Harvester> drones) => _drones = drones;

    protected override void Configure(SystemBuilder b) => b
        .Name("Roam")
        .Phase(Phase.Simulation)
        .Input(() => _drones)
        .Parallel()
        .Reads<Drift>()
        .Writes<Position>();

    protected override void Execute(TickContext ctx)
    {
        foreach (EntityId id in ctx.Entities)
        {
            var e = ctx.Accessor.OpenMut(id);
            ref readonly var d = ref e.Read(Harvester.Drift);
            ref var p = ref e.Write(Harvester.Position);
            p.P = new Point2F { X = p.P.X + d.Dx * ctx.DeltaTime, Y = p.P.Y + d.Dy * ctx.DeltaTime };
        }
    }
}

/// <summary>Keep the spatial index coherent after movement. Footprint carries the <c>[SpatialIndex]</c>, so it must be
/// written through the <c>WriteSpatial</c> barrier (a plain field write would trip the spatial analyzer). Cluster-native
/// loop — the high-throughput shape for touching a whole archetype.</summary>
internal sealed class FootprintSyncSystem : QuerySystem
{
    private readonly EcsView<Harvester> _drones;

    public FootprintSyncSystem(EcsView<Harvester> drones) => _drones = drones;

    protected override void Configure(SystemBuilder b) => b
        .Name("FootprintSync")
        .Phase(Phase.Simulation)
        .Input(() => _drones)
        .Parallel()
        .After("Roam")
        .ReadsFresh<Position>()   // this tick's moved positions
        .Writes<Footprint>();

    protected override void Execute(TickContext ctx)
    {
        using var clusters = ctx.ClusterIds != null
            ? ctx.Accessor.GetClusterEnumerator<Harvester>(ctx.ClusterIds, ctx.StartClusterIndex, ctx.EndClusterIndex)
            : ctx.Accessor.GetClusterEnumerator<Harvester>(ctx.StartClusterIndex, ctx.EndClusterIndex);

        foreach (var cluster in clusters)
        {
            var bits = cluster.OccupancyBits;
            if (bits == 0)
            {
                continue;
            }

            var positions = cluster.GetReadOnlySpan(Harvester.Position);
            while (bits != 0)
            {
                int idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                var p = positions[idx].P;
                cluster.WriteSpatial(Harvester.Footprint, idx, new Footprint { Box = new AABB2F { MinX = p.X, MaxX = p.X, MinY = p.Y, MaxY = p.Y } });
            }
        }
    }
}

/// <summary>Accumulate yield every tick — a Versioned write, so it goes through the transaction. No access declared on
/// Position: Harvest doesn't touch it, so it has no conflict with RoamSystem and runs alongside it for free.</summary>
internal sealed class HarvestSystem : QuerySystem
{
    private readonly EcsView<Harvester> _drones;

    public HarvestSystem(EcsView<Harvester> drones) => _drones = drones;

    protected override void Configure(SystemBuilder b) => b
        .Name("Harvest")
        .Phase(Phase.Simulation)
        .Input(() => _drones)
        .Reads<Extractor>()
        .Writes<Cargo>();

    protected override void Execute(TickContext ctx)
    {
        foreach (EntityId id in ctx.Entities)
        {
            var e = ctx.Transaction.OpenMut(id);
            ref readonly var ex = ref e.Read(Harvester.Extractor);
            ref var c = ref e.Write(Harvester.Cargo);
            if (c.Amount < c.Capacity)
            {
                c.Amount = Math.Min(c.Capacity, c.Amount + ex.Rate);
            }
        }
    }
}
