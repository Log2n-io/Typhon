using Typhon.Engine;

namespace SimpleSpaceBattle;

/// <summary>
/// Phase <see cref="BattlePhases.Move"/> — integrate velocity and reflect off the six world walls. Pure SoA
/// arithmetic: no queries, no cross-entity access, no branches beyond the reflection test.
///
/// <para><b>Why <c>GetSpan</c> and not <c>WriteSpatial</c>.</b> <c>ClusterRef.WriteSpatial</c> is the canonical O(1)
/// spatial write barrier, but it accepts <c>AABB2F</c> only — <c>AABB3F</c> throws <c>NotSupportedException</c>
/// (<c>ClusterRef.cs:250-258</c>), so it is unavailable to every 3D archetype. The fallback is correct because
/// <c>RecomputeDirtyClusterAabbs</c> discards its <c>dirtyBits</c> argument and rescans every active cluster
/// unconditionally while <c>SpatialBarrierOnly</c> is false, recomputing AABBs from stored values and enqueueing
/// migrations. <b>Do not call <c>SetSpatialBarrierOnly&lt;Ship&gt;</c></b> — it would make these writes invisible to
/// spatial maintenance and silently freeze the index.</para>
///
/// <para>No <c>MarkDirty</c> either: the spatial rescan ignores dirty bits, this archetype emits no fence WAL
/// (<c>ClusterDurability.Checkpoint</c>), and no system uses a change filter.</para>
/// </summary>
internal sealed class MovementSystem : CallbackSystem
{
    private readonly BattleWorld _world;

    public MovementSystem(BattleWorld world) => _world = world;

    protected override void Configure(SystemBuilder b) => b
        .Name("Movement")
        .ShouldRun(() => !_world.IsTerminal)
        .Phase(BattlePhases.Move)
        .ChunkedParallel(_world.WorkerCount)
        .Reads<VitalsComponent>()
        .Writes<HullComponent>()
        .Writes<MotionComponent>();

    protected override void Execute(TickContext ctx)
    {
        ClusterWork.Range(_world, in ctx, out int startCluster, out int endCluster);
        if (endCluster <= startCluster)
        {
            return;
        }

        // No transaction here: this system issues no spatial query, so it needs no ambient epoch scope, and a
        // per-chunk transaction cost 8.9 ms of wall-clock for ~50 000 multiply-adds (see ClusterWork.Range).
        EntityAccessor accessor = _world.Accessor.GetWorkerAccessor(ctx.WorkerId);

        SimulationConfig cfg = _world.Config;
        // Config.DeltaTime, NOT ctx.DeltaTime: the runtime's DeltaTime is WALL-CLOCK elapsed time between
        // ticks (TyphonRuntime.cs:1857), so using it makes the simulation depend on how fast the machine ran.
        // A fixed timestep is what makes the run reproducible (DESIGN.md §9).
        float dt = cfg.DeltaTime;
        float maxX = cfg.WorldX;
        float maxY = cfg.WorldY;
        float maxZ = cfg.WorldZ;

        using ClusterEnumerator<Ship> clusters = accessor.GetClusterEnumerator<Ship>(startCluster, endCluster);

        foreach (ClusterRef<Ship> cluster in clusters)
        {
            ulong bits = cluster.OccupancyBits;
            if (bits == 0)
            {
                continue;
            }

            // TYPHON009 fires here and is deliberately suppressed: the analyzer's suggested fix, WriteSpatial,
            // accepts AABB2F only and throws NotSupportedException for the AABB3F this archetype uses
            // (ClusterRef.cs:250-258). The mutation IS visible to spatial maintenance because SpatialBarrierOnly
            // stays false, which makes the fence rescan every active cluster unconditionally
            // (ArchetypeClusterState.cs:2050-2095). See the class comment and DESIGN.md §3.2b.
#pragma warning disable TYPHON009
            Span<HullComponent> hulls = cluster.GetSpan(Ship.Hull);
#pragma warning restore TYPHON009
            Span<MotionComponent> motions = cluster.GetSpan(Ship.Motion);

            while (bits != 0)
            {
                int i = System.Numerics.BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;

                ref HullComponent hull = ref hulls[i];
                ref MotionComponent motion = ref motions[i];

                float x = hull.Bounds.MinX + motion.X * dt;
                float y = hull.Bounds.MinY + motion.Y * dt;
                float z = hull.Bounds.MinZ + motion.Z * dt;

                Reflect(ref x, ref motion.X, maxX);
                Reflect(ref y, ref motion.Y, maxY);
                Reflect(ref z, ref motion.Z, maxZ);

                // Point-form AABB: min == max on every axis.
                hull.Bounds.MinX = x;
                hull.Bounds.MaxX = x;
                hull.Bounds.MinY = y;
                hull.Bounds.MaxY = y;
                hull.Bounds.MinZ = z;
                hull.Bounds.MaxZ = z;
            }
        }
    }

    /// <summary>
    /// Mirror the coordinate back inside <c>[0, max]</c> and negate the velocity component. Mirroring rather than
    /// clamping keeps the motion reversible and prevents ships from piling up on a wall, which would wreck the
    /// uniform density every range in §3.3 is derived from.
    /// </summary>
    private static void Reflect(ref float position, ref float velocity, float max)
    {
        if (position < 0f)
        {
            position = -position;
            velocity = -velocity;
        }
        else if (position > max)
        {
            position = max + max - position;
            velocity = -velocity;
        }

        // A ship that overshoots the far wall in a single step after mirroring (only reachable with an absurd
        // speed/dt) is clamped rather than left outside the grid, where WorldToCell would fold it into an edge cell.
        if (position < 0f)
        {
            position = 0f;
        }
        else if (position > max)
        {
            position = max;
        }
    }
}
