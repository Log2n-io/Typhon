using System.Numerics;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace SimpleSpaceBattle;

/// <summary>
/// Phase <see cref="BattlePhases.Acquire"/> — ships with no lock find one, and every ship publishes its target to
/// the lane.
///
/// <para>Walks every cluster but runs a spatial query only for ships whose lock is <c>Unlocked</c> — in steady state
/// 5-10 % of the fleet, so the expensive part is gated to the churn rather than the population. The lane publish
/// is one 8-byte store per ship and removes every staleness question from the Fire phase.</para>
///
/// <para><b>Access.</b> Reads <c>Hull</c> (self and neighbours — no same-phase writer exists), writes
/// <c>Targeting</c> and the lane, both for the iterating entity only.</para>
/// </summary>
internal sealed class TargetingSystem : CallbackSystem
{
    private readonly BattleWorld _world;

    public TargetingSystem(BattleWorld world) => _world = world;

    protected override void Configure(SystemBuilder b) => b
        .Name("Targeting")
        .ShouldRun(() => !_world.IsTerminal)
        .Phase(BattlePhases.Acquire)
        .ChunkedParallel(_world.WorkerCount)
        .Reads<HullComponent>()
        .Writes<TargetingComponent>()
        .WritesResource("TargetLane");

    protected override void Execute(TickContext ctx)
    {
        using ClusterWork work = ClusterWork.Open(_world, in ctx);
        if (work.IsEmpty)
        {
            return;
        }

        SimulationConfig cfg = _world.Config;
        DatabaseEngine dbe = _world.Dbe;
        TargetLane lane = _world.Lane;
        long[] laneBacking = lane.Backing;
        int clusterSize = lane.ClusterSize;
        float acquisitionRange = cfg.AcquisitionRange;
        ref WorkerCounters counters = ref _world.CountersForWorker(ctx.WorkerId);
        long acquisitions = 0;

        using ClusterEnumerator<Ship> clusters = work.Clusters();

        foreach (ClusterRef<Ship> cluster in clusters)
        {
            ulong bits = cluster.OccupancyBits;
            if (bits == 0)
            {
                continue;
            }

            ReadOnlySpan<HullComponent> hulls = cluster.GetReadOnlySpan(Ship.Hull);
            Span<TargetingComponent> targeting = cluster.GetSpan(Ship.Targeting);
            ReadOnlySpan<long> ids = cluster.EntityIds;
            int laneBase = cluster.ChunkId * clusterSize;

            while (bits != 0)
            {
                int i = System.Numerics.BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;

                long target = targeting[i].TargetRawId;

                if (target == TargetingComponent.Unlocked)
                {
                    target = FindNearest(dbe, in hulls[i], ids[i], acquisitionRange);
                    targeting[i].TargetRawId = target;
                    acquisitions++;
                }

                // Publish unconditionally. Republishing an unchanged lock costs one store and means the Fire phase
                // never has to reason about whether a lane entry is current.
                laneBacking[laneBase + i] = target;
            }
        }

        counters.Acquisitions += acquisitions;
    }

    /// <summary>
    /// Nearest other ship within <paramref name="range"/>, or <see cref="TargetingComponent.Unlocked"/> if none.
    /// <para>
    /// Ties break on the lower raw entity id, never on scan order — enumeration order across clusters depends on
    /// how work was partitioned, so a scan-order tiebreak would make the run worker-count dependent (§9).
    /// </para>
    /// </summary>
    private static long FindNearest(DatabaseEngine dbe, in HullComponent self, long selfId, float range)
    {
        var sphere = new BSphere3F
        {
            CenterX = self.Bounds.MinX,
            CenterY = self.Bounds.MinY,
            CenterZ = self.Bounds.MinZ,
            Radius = range,
        };

        long best = TargetingComponent.Unlocked;
        float bestDistSq = float.MaxValue;

        foreach (ClusterSpatialQueryResult hit in dbe.ClusterSpatialQuery<Ship>().Radius(in sphere))
        {
            if (hit.EntityId == selfId)
            {
                continue;
            }

            float distSq = hit.DistanceSq;
            if (distSq < bestDistSq || (distSq == bestDistSq && hit.EntityId < best))
            {
                bestDistSq = distSq;
                best = hit.EntityId;
            }
        }

        return best;
    }
}
