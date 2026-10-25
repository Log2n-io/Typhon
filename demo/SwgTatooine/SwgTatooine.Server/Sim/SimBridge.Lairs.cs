using SwgTatooine.Server.Terrain;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SwgTatooine.Server;

/// <summary>A creature whose corpse has lain long enough: pushed by <c>CreatureThink</c>, which may not destroy, and destroyed by the lair director.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CorpseEvent
{
    public EntityId Creature;
}

/// <summary>
/// The lair director: lairs and the creatures in them are TRANSIENT — spawned, lived in, and destroyed (Loïc, 2026-10-24).
/// </summary>
/// <remarks>
/// <para>
/// <b>Three lives, one system.</b>
/// </para>
/// <list type="bullet">
///   <item><b>A creature</b> dies (<c>CombatResolve</c>), lies as a corpse for <see cref="SimConfig.CorpseSeconds"/> so its death can be seen, and is then
///   DESTROYED here. Its lair spawns a new one <see cref="SimConfig.RespawnSeconds"/> later. It used to be revived and teleported home: one entity, forever.</item>
///   <item><b>A wild lair</b> is retired when it is broken (health 0) or when its life runs out (<see cref="SimConfig.LairLifeMinSeconds"/>), and its creatures
///   go with it through the engine's foreign-key cascade (<see cref="CreatureHome"/>) — one <c>Destroy</c>, in one transaction.</item>
///   <item><b>A site</b> keeps the number of lairs it was built with: a retired lair is replaced by a new one somewhere else in the same disc after
///   <see cref="SimConfig.LairReplaceSeconds"/>, of a kind drawn from the site's mix. The world's population is stationary; its entities are not.</item>
/// </list>
/// <para>
/// <b>A mission's lair is refilled here like any other</b>, and nothing more: it has no site, so it is never retired here. Its life — drawn at the take,
/// broken, moved, destroyed at its holder's logout — is the mission desk's (<c>SimBridge.Missions.cs</c>), which runs after this system in the same phase.
/// </para>
/// <para>
/// <b>Serial, in the Economy phase, through a side transaction</b> — the arrangement <c>DungeonTick</c> already spawns and destroys with. Serial because a
/// spawn and a destroy are structural changes nothing else in the phase makes; after <c>CreatureThink</c> (Think) and <c>CombatResolve</c> (Apply) so a
/// corpse pushed and a lair broken this tick are handled this tick. The lair walk runs at <see cref="LairPassHz"/>, not every tick: the cheapest thing it
/// does is read two thousand lairs to find that nothing is due.
/// </para>
/// </remarks>
public sealed partial class SimBridge
{
    /// <summary>How often the director walks the lairs, per second. Expiry and respawn are measured in minutes; half a second of lag costs nothing.</summary>
    internal const int LairPassHz = 2;

    /// <summary>Tries at a point inside a site and outside every city before a replacement waits for the next pass.</summary>
    private const int LairPlacementTries = 8;

    /// <summary>The corpses <c>CreatureThink</c> hands over. Created on the schedule, like <see cref="CombatQueue"/>.</summary>
    internal EventQueue<CorpseEvent> CorpseQueue { get; set; }

    // Per-pass work lists. Serial system, so one set suffices; kept across ticks so a pass allocates nothing once warm.
    private readonly List<(EntityId Lair, ushort Realm, int Site)> _retiring = [];
    private readonly List<EntityId> _refilling = [];

    private long _corpsesRemoved;
    private long _lairsRetired;
    private long _lairsSpawned;
    private long _lairCommits;
    private long _lairEntities;

    /// <summary>
    /// Ticks on which the director committed a spawn or a destroy. Each is one WAL record: a structural change is logged whatever the archetype's cluster
    /// durability, so it is a durable writer beside loot, rewards and door crossings (<c>CausalityChecks.AC2</c>).
    /// </summary>
    public long LairCommits => _lairCommits;

    /// <summary>
    /// Entities the director spawned or destroyed, cascaded creatures included: what its WAL records are bounded by. A spawn appends a record per
    /// component — one creature respawned beside one corpse removed was eleven records on one tick — so the bound is per entity, not per commit.
    /// </summary>
    public long LairEntities => _lairEntities;

    /// <summary>Destroys the corpses that have lain long enough, retires spent lairs, refills lairs and sites.</summary>
    public void LairLifeTick(TickContext ctx)
    {
        if (Halted(ctx.TickNumber))
        {
            return;
        }

        var tick = ctx.TickNumber;
        var pending = CorpseQueue.Count;
        var passTicks = Math.Max(1, _config.TickRateHz / LairPassHz);
        var pass = tick % passTicks == 0;
        if (pending == 0 && !pass)
        {
            return;
        }

        var respawnTicks = Math.Max(1L, (long)(_config.RespawnSeconds * _config.TickRateHz));
        var before = _corpsesRemoved + _lairsRetired + _lairsSpawned + _creaturesRespawned;
        using var tx = ctx.CreateSideTransaction(DurabilityMode.GroupCommit, CommitDiscipline.Commit);

        if (pending > 0)
        {
            // Drain THROWS on a short span rather than truncating, so the size comes from Count — see CombatResolveTick.
            Span<CorpseEvent> corpses = pending <= 64 ? stackalloc CorpseEvent[64] : new CorpseEvent[pending];
            var n = CorpseQueue.Drain(corpses);
            for (var i = 0; i < n; i++)
            {
                RemoveCorpse(tx, corpses[i].Creature, tick, respawnTicks);
            }
        }

        if (pass)
        {
            Pass(tx, tick, respawnTicks);
        }

        tx.Commit();
        if (_corpsesRemoved + _lairsRetired + _lairsSpawned + _creaturesRespawned != before)
        {
            _lairCommits++;
        }
    }

    /// <summary>Destroys one corpse and tells its lair it has a creature to replace.</summary>
    private void RemoveCorpse(Transaction tx, EntityId creature, long tick, long respawnTicks)
    {
        // Already gone: its lair was retired since it died, and the cascade took it.
        if (!tx.TryOpen(creature, out var body))
        {
            return;
        }

        EntityId lairId = body.Read(Creature.Home).Lair;
        tx.Destroy(creature);
        _corpsesRemoved++;
        _lairEntities++;

        if (!tx.TryOpenMut(lairId, out var lair))
        {
            return;
        }

        var spawner = lair.Read(CreatureLair.Spawner);
        spawner.AliveCount = Math.Max(0, spawner.AliveCount - 1);
        if (spawner.NextSpawnTick == 0)
        {
            spawner.NextSpawnTick = tick + respawnTicks;
        }

        lair.Set(CreatureLair.Spawner, spawner);
    }

    /// <summary>The lair walk: retirements, creature refills, and the sites' replacements.</summary>
    private void Pass(Transaction tx, long tick, long respawnTicks)
    {
        _retiring.Clear();
        _refilling.Clear();

        foreach (var cluster in tx.For<CreatureLair>().GetClusterEnumerator())
        {
            var spawners = cluster.GetReadOnlySpan(CreatureLair.Spawner);
            var vitals = cluster.GetReadOnlySpan(CreatureLair.Vitals);
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                ref readonly var lair = ref spawners[slot];
                if (lair.Site != 0 && (vitals[slot].Health <= 0 || (lair.ExpireTick != 0 && tick >= lair.ExpireTick)))
                {
                    _retiring.Add((cluster.GetEntityId(slot), cluster.Realm.Value, lair.Site));

                    // The lair and the creatures its cascade takes, which AliveCount counts (LairLifeChecks holds the two equal).
                    _lairEntities += 1 + lair.AliveCount;
                }
                else if (lair.AliveCount < lair.SpawnLimit && lair.NextSpawnTick != 0 && tick >= lair.NextSpawnTick && vitals[slot].Health > 0)
                {
                    _refilling.Add(cluster.GetEntityId(slot));
                }
            }
        }

        var replaceTicks = Math.Max(1L, (long)(_config.LairReplaceSeconds * _config.TickRateHz));
        foreach (var (id, realm, site) in _retiring)
        {
            // ONE destroy: the creatures go with the lair through CreatureHome's cascade.
            tx.Destroy(id);
            _lairsRetired++;
            var index = PlanetIndexes[realm];
            index.Lairs.Remove(id);
            var at = index.Sites[site - 1];
            at.Live--;
            if (at.NextSpawnTick == 0)
            {
                at.NextSpawnTick = tick + replaceTicks;
            }
        }

        var terrain = TerrainField.Shared(_config.ContentScale);
        foreach (var id in _refilling)
        {
            // Gone since the walk chose it: a cluster walk may still yield an entity its own transaction destroyed (#1482), and opening one throws.
            if (!tx.TryOpenMut(id, out var lair))
            {
                continue;
            }

            var spawner = lair.Read(CreatureLair.Spawner);
            var place = lair.Read(CreatureLair.Bounds);
            var realm = lair.Read(CreatureLair.Realm).Value;
            var rng = new Rng(Salt(tick, id.EntityKey, 0x2C1B3C6Du));

            // At the lair's position, round which its occupants stand.
            WorldBuilder.SpawnCreature(tx, ref rng, id, spawner.CreatureTemplate, place.X, place.Z, spawner.AliveCount, _config, terrain, realm);
            _creaturesRespawned++;
            _lairEntities++;
            spawner.AliveCount++;
            spawner.Guards++;
            spawner.NextSpawnTick = spawner.AliveCount < spawner.SpawnLimit ? tick + respawnTicks : 0;
            lair.Set(CreatureLair.Spawner, spawner);
            TatooineReplication.Replicate(in lair);
        }

        // The sites, which keep their density by spawning a replacement somewhere else in the same disc.
        var lifeMin = (long)(_config.LairLifeMinSeconds * _config.TickRateHz);
        var lifeSpan = (long)((_config.LairLifeMaxSeconds - _config.LairLifeMinSeconds) * _config.TickRateHz);
        for (var planet = 0; planet < PlanetIndexes.Length; planet++)
        {
            var index = PlanetIndexes[planet];
            for (var s = 0; s < index.Sites.Count; s++)
            {
                var at = index.Sites[s];
                if (at.Live >= at.Target)
                {
                    at.NextSpawnTick = 0;
                    continue;
                }

                // Owed but never scheduled: a reopened world that came back short.
                if (at.NextSpawnTick == 0)
                {
                    at.NextSpawnTick = tick + replaceTicks;
                    continue;
                }

                if (tick < at.NextSpawnTick)
                {
                    continue;
                }

                var rng = new Rng(Salt(tick, ((long)planet << 32) | (uint)s, 0x7F4A7C15u));
                if (!TryPlaceLair(at, ref rng, out var x, out var z))
                {
                    continue;
                }

                var template = CreatureTemplates.PickLair(at.Templates, ref rng);
                var expire = tick + lifeMin + (long)(rng.NextFloat() * lifeSpan);
                index.Lairs.Add(WorldBuilder.SpawnLair(tx, ref rng, template, x, z, s + 1, expire, _config, terrain, at.Realm));
                _lairsSpawned++;
                _lairEntities += 1 + CreatureTemplates.SpawnLimit[template];
                at.Live++;

                // One per site per pass, so a site that lost several fills over a few passes rather than in one burst.
                at.NextSpawnTick = at.Live < at.Target ? tick + 1 : 0;
            }
        }
    }

    /// <summary>A point inside a site, inside the world and outside every city — the build's own rule, plus the cities a destroy mission avoids.</summary>
    private bool TryPlaceLair(LairSite at, ref Rng rng, out float x, out float z)
    {
        for (var attempt = 0; attempt < LairPlacementTries; attempt++)
        {
            (x, z) = WorldBuilder.Inside(rng.PointInDisc(at.X, at.Z, at.Radius), _config);
            if (!InsideCity(x, z))
            {
                return true;
            }
        }

        x = 0f;
        z = 0f;
        return false;
    }
}
