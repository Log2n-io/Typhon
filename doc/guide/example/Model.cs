// Data model + systems for the guide example. Kept in a named namespace so the
// ArchetypeAccessorGenerator can emit Unit.ReadAll / ReadWriteAll (it needs a real namespace).

using System.Numerics;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace SkirmishGuide;

// ════════════════════════════════════════════════════════════════════════
// Data model (ch.2)
// ════════════════════════════════════════════════════════════════════════

[Component("Skirmish.Position", 1, StorageMode = StorageMode.SingleVersion)] // hot, durable, no isolation
public struct Position
{
    public Point2F P;
}

[Component("Skirmish.Bounds", 1, StorageMode = StorageMode.SingleVersion)]   // spatial mirror of the position
public struct Bounds
{
    [SpatialIndex(2f)] public AABB2F Box;
}

[Component("Skirmish.Health", 1, StorageMode = StorageMode.Versioned)]       // ACID gameplay state
public struct Health
{
    public int Current, Max;
    public Health(int current, int max) { Current = current; Max = max; }
}

[Component("Skirmish.Velocity", 1, StorageMode = StorageMode.Transient)]     // per-tick scratch
public struct Velocity
{
    public float Dx, Dy;
    public Velocity(float dx, float dy) { Dx = dx; Dy = dy; }
}

[Component("Skirmish.Team", 1, StorageMode = StorageMode.Versioned)]
public struct Team
{
    [Index(AllowMultiple = true)] public int Id;   // many units per team
}

[Archetype(1)]
public sealed partial class Unit : Archetype<Unit>
{
    public static readonly Comp<Position> Position = Register<Position>();
    public static readonly Comp<Bounds>   Bounds   = Register<Bounds>();
    public static readonly Comp<Health>   Health   = Register<Health>();
    public static readonly Comp<Velocity> Velocity = Register<Velocity>();
    public static readonly Comp<Team>     Team     = Register<Team>();
}

// ════════════════════════════════════════════════════════════════════════
// Systems (ch.5)
// ════════════════════════════════════════════════════════════════════════

// Non-entity work: occasionally spawn a reinforcement.
internal sealed class SpawnSystem : CallbackSystem
{
    protected override void Configure(SystemBuilder b) => b
        .Name("Spawn")
        .Phase(Phase.Input)
        .Writes<Position>().Writes<Bounds>().Writes<Health>().Writes<Velocity>().Writes<Team>();

    protected override void Execute(TickContext ctx)
    {
        if (ctx.TickNumber == 0 || ctx.TickNumber % 30 != 0) return;
        ctx.Transaction.Spawn<Unit>(
            Unit.Position.Set(new Position { P = new Point2F { X = 0f, Y = 0f } }),
            Unit.Bounds.Set(new Bounds { Box = new AABB2F { MinX = 0, MaxX = 0, MinY = 0, MaxY = 0 } }),
            Unit.Health.Set(new Health(100, 100)),
            Unit.Velocity.Set(new Velocity(1f, 0f)),
            Unit.Team.Set(new Team { Id = 2 }));
        // no Commit — the scheduler commits this system's transaction
    }
}

// Move every unit. A parallel QuerySystem: the engine fans this body across workers, each
// handling a slice of ctx.Entities. Position is SingleVersion (non-Versioned), so the writes
// go through the per-worker ctx.Accessor — no locks.
internal sealed class MovementSystem : QuerySystem
{
    private readonly EcsView<Unit> _units;
    public MovementSystem(EcsView<Unit> units) { _units = units; }

    protected override void Configure(SystemBuilder b) => b
        .Name("Movement")
        .Phase(Phase.Simulation)
        .Input(() => _units)
        .Parallel()
        .Reads<Velocity>()
        .Writes<Position>();

    protected override void Execute(TickContext ctx)
    {
        foreach (EntityId id in ctx.Entities)
        {
            var e = ctx.Accessor.OpenMut(id);
            ref readonly var v = ref e.Read(Unit.Velocity);
            ref var p = ref e.Write(Unit.Position);
            p.P = new Point2F { X = p.P.X + v.Dx * ctx.DeltaTime, Y = p.P.Y + v.Dy * ctx.DeltaTime };
        }
    }
}

// Keep the spatial index coherent after movement. Bounds carries the [SpatialIndex], so it must
// be written through the WriteSpatial barrier (a plain field write would trip the spatial analyzer).
// Cluster-native loop — the high-throughput shape for touching a whole archetype.
internal sealed class BoundsSyncSystem : QuerySystem
{
    private readonly EcsView<Unit> _units;
    public BoundsSyncSystem(EcsView<Unit> units) { _units = units; }

    protected override void Configure(SystemBuilder b) => b
        .Name("BoundsSync")
        .Phase(Phase.Simulation)
        .Input(() => _units)
        .Parallel()
        .After("Movement")
        .ReadsFresh<Position>()   // this tick's moved positions
        .Writes<Bounds>();

    protected override void Execute(TickContext ctx)
    {
        using var clusters = ctx.ClusterIds != null
            ? ctx.Accessor.GetClusterEnumerator<Unit>(ctx.ClusterIds, ctx.StartClusterIndex, ctx.EndClusterIndex)
            : ctx.Accessor.GetClusterEnumerator<Unit>(ctx.StartClusterIndex, ctx.EndClusterIndex);

        foreach (var cluster in clusters)
        {
            var bits = cluster.OccupancyBits;
            if (bits == 0) continue;

            var positions = cluster.GetReadOnlySpan(Unit.Position);
            while (bits != 0)
            {
                int idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                var p = positions[idx].P;
                cluster.WriteSpatial(Unit.Bounds, idx, new Bounds { Box = new AABB2F { MinX = p.X, MaxX = p.X, MinY = p.Y, MaxY = p.Y } });
            }
        }
    }
}

// Apply a little attrition every tick — a Versioned write, so it goes through the transaction.
internal sealed class CombatSystem : QuerySystem
{
    private readonly EcsView<Unit> _units;
    public CombatSystem(EcsView<Unit> units) { _units = units; }

    protected override void Configure(SystemBuilder b) => b
        .Name("Combat")
        .Phase(Phase.Simulation)
        .Input(() => _units)
        .ReadsSnapshot<Position>()
        .Writes<Health>();

    protected override void Execute(TickContext ctx)
    {
        foreach (EntityId id in ctx.Entities)
        {
            ref var hp = ref ctx.Transaction.OpenMut(id).Write(Unit.Health);
            if (hp.Current > 0) hp.Current -= 1;
        }
    }
}
