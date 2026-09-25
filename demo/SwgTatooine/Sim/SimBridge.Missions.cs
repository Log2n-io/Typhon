using System;
using System.Numerics;
using System.Threading;

namespace SwgTatooine;

/// <summary>
/// Destroy missions: the loop that puts a target in front of a player and takes it away again.
/// </summary>
/// <remarks>
/// <para><b>The algorithm is Core3's, not an invention.</b> <c>MissionManagerImplementation::randomizeGenericDestroyMission</c>
/// picks a point at <c>base + difficultyFactor * level + random(randomDistance) + random(difficultyRandomDistance * level)</c>
/// from the player at a random bearing; the shipped configuration sets base and randomDistance to 1 000 each and BOTH
/// difficulty terms to zero, so it reduces to a uniform 1 000-2 000 m and mission difficulty does not move the target
/// further away. Twenty tries; the point must be in bounds, on dry land, and outside every city region.</para>
/// <para><b>A destroy-mission target is one object, not a camp.</b> The first version of this workload assumed a cluster
/// of tents and props — the source says otherwise: a single <c>LairObject</c> with
/// <c>difficulty * (900 + random(200))</c> hit points, with its defenders arriving as mobiles around it in three waves
/// gated on the lair's damage rather than on a timer. Once those defenders die they stay dead; <c>checkRespawn</c>
/// short-circuits for a destroy-mission lair, unlike a wild one.</para>
/// <para><b>Why it runs from the lair's side and out of a pool.</b> Cross-archetype writes are legal API, but in this
/// workload they stalled the tick loop when it was written (#907, still open), so a fixed set of mission lairs is created
/// at world build and recycled: a dormant one that finds a player without a mission TELEPORTS to a fresh point 1-2 km from them and becomes
/// live. That teleport is the most violent thing in the simulation from the index's point of view — a lair and its
/// defenders jumping a kilometre and a half, forcing a cell change and a cluster-bound recomputation for every one of
/// them — which makes it the most useful part of the workload rather than a compromise.</para>
/// </remarks>
public sealed partial class SimBridge
{
    /// <summary>[CORE3] How often a dormant mission lair looks for a player to serve. Ties to the 5 s spawn-area cooldown.</summary>
    private const int MissionOfferIntervalTicks = 50;

    /// <summary>Radius within which a dormant lair will look for a player to build a mission around.</summary>
    private const float MissionSeekRadiusM = 3000f;

    /// <summary>
    /// Dormant mission lairs look for work; live ones check whether they have been destroyed.
    /// </summary>
    public void MissionTick(TickContext ctx)
    {
        var tick = ctx.TickNumber;
        var scale = _config.ContentScale;
        var minD = TatooineData.DestroyMissionMinDistanceM * scale;
        var spanD = (TatooineData.DestroyMissionMaxDistanceM - TatooineData.DestroyMissionMinDistanceM) * scale;
        var seek = MissionSeekRadiusM * scale;
        var half = _config.WorldEdgeM * 0.5f;
        long issued = 0;
        long completed = 0;

        using var clusters = ctx.ClusterIds != null
            ? ctx.Accessor.GetClusterEnumerator<CreatureLair>(ctx.ClusterIds, ctx.StartClusterIndex, ctx.EndClusterIndex)
            : ctx.Accessor.GetClusterEnumerator<CreatureLair>(ctx.StartClusterIndex, ctx.EndClusterIndex);

        foreach (var cluster in clusters)
        {
            var bits0 = cluster.OccupancyBits;
            if (bits0 == 0)
            {
                continue;
            }

            var places = cluster.GetReadOnlySpan(CreatureLair.Bounds);
            var spawners = cluster.GetSpan(CreatureLair.Spawner);
            var vitals = cluster.GetSpan(CreatureLair.Vitals);
            var chunk = cluster.ChunkId;

            var bits = bits0;
            while (bits != 0)
            {
                var idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;

                ref var lair = ref spawners[idx];
                if (lair.CreatureTemplate != CreatureTemplates.MissionDefender)
                {
                    // A wild lair. Not part of this loop.
                    continue;
                }

                ref var v = ref vitals[idx];

                if (lair.MissionId != 0)
                {
                    // Live. A destroyed lair ends its mission and goes back in the pool.
                    if (v.Health <= 0)
                    {
                        lair.MissionId = 0;
                        v.Health = v.MaxHealth;
                        completed++;
                    }

                    continue;
                }

                // Dormant. Look for a player to build a mission around, on the offer cadence.
                if (--lair.RespawnCooldown > 0)
                {
                    continue;
                }

                lair.RespawnCooldown = MissionOfferIntervalTicks;

                var lx = places[idx].X;
                var lz = places[idx].Z;
                var sphere = new BSphere2F { CenterX = lx, CenterY = lz, Radius = seek };
                var e = Dbe.ClusterSpatialQuery<Player>().Radius(in sphere);
                var foundPlayer = false;
                float px = 0f, pz = 0f;
                try
                {
                    if (e.MoveNext())
                    {
                        var hit = e.Current;
                        px = (float)((hit.MinX + hit.MaxX) * 0.5);   // f64 world frame (#914) to the simulation's f32
                        pz = (float)((hit.MinY + hit.MaxY) * 0.5);
                        foundPlayer = true;
                    }
                }
                finally
                {
                    e.Dispose();
                }

                if (!foundPlayer)
                {
                    continue;
                }

                // Core3's twenty tries at a uniform 1-2 km, rejecting anything inside a city region.
                for (var attempt = 0; attempt < 20; attempt++)
                {
                    var ang = Hash01(Salt(tick, chunk, idx, 0xB5297A4Du + (uint)attempt)) * MathF.PI * 2f;
                    var dist = minD + (Hash01(Salt(tick, chunk, idx, 0x68E31DA4u + (uint)attempt)) * spanD);
                    var tx = px + (MathF.Cos(ang) * dist);
                    var tz = pz + (MathF.Sin(ang) * dist);
                    if (tx < -half || tx > half || tz < -half || tz > half || InsideCity(tx, tz))
                    {
                        continue;
                    }

                    // Difficulty 1-9, and Core3's hit points for it.
                    var difficulty = 1 + (int)(Hash01(Salt(tick, chunk, idx, 0x9E3779B7u)) * 9f);
                    v.MaxHealth = difficulty * (900 + (int)(Hash01(Salt(tick, chunk, idx, 0x1E35A7BDu)) * 200));
                    v.Health = v.MaxHealth;
                    lair.MissionId = difficulty;
                    lair.AliveCount = 1;

                    // The teleport, done here rather than handed to a placement system: this walk owns CreatureLair, so
                    // it can write the placement directly through the spatial barrier. A lair jumping one to two
                    // kilometres is the most violent thing in this simulation from the index's point of view — a cell
                    // change and a cluster-bound recomputation in one write — and it is the point rather than a cost.
                    var nb = default(LairPlacement);
                    nb.SetAt(tx, tz, places[idx].HalfExtent);
                    cluster.WriteSpatial(CreatureLair.Bounds, idx, nb);
                    issued++;
                    break;
                }
            }
        }

        if (issued != 0)
        {
            Interlocked.Add(ref _missionsIssued, issued);
        }

        if (completed != 0)
        {
            Interlocked.Add(ref _missionsCompleted, completed);
        }
    }

    /// <summary>Is a point inside one of the NPC cities? [CORE3] a destroy mission may not place its target in one.</summary>
    private bool InsideCity(float x, float z)
    {
        for (var i = 0; i < _index.Cities.Count; i++)
        {
            var c = _index.Cities[i];
            var dx = c.X - x;
            var dz = c.Z - z;
            if ((dx * dx) + (dz * dz) < c.Radius * c.Radius)
            {
                return true;
            }
        }

        return false;
    }
}
