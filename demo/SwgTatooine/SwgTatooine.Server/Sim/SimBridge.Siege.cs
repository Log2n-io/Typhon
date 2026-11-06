using System;
using System.Collections.Generic;
using System.Threading;
using SwgTatooine.Navigation;
using Typhon.Engine;

namespace SwgTatooine.Server;

/// <summary>The raider clusters a horde's radar shows (horde.md § 10), as <see cref="HordeRadar"/> carries them; null when there are none.</summary>
/// <param name="X">The fortress, east — what its guards defend.</param>
/// <param name="Z">The fortress, north.</param>
/// <param name="Attackers">The bodies that struck the fortress in the last <see cref="SimBridge.HordeFortAttackerS"/>.</param>
internal sealed record FortGuard(float X, float Z, EntityId[] Attackers);

/// <summary>
/// The siege (Loïc, 2026-11-04; horde.md § 10): a horde's town hall and its fortress both have hit points. Raiders that reach the town hall with nobody to
/// fight break it — at zero the town falls and the event is lost; defenders break the fortress — at zero the raid is beaten and its raiders go with it.
/// The fortress's guards turn on whoever attacks it, before anything else. And a radar: where the raiders are, for everyone, every few seconds.
/// </summary>
/// <remarks>
/// <para>
/// <b>The hit points are the event's, not the buildings'.</b> They live in <see cref="HordeCore"/>: a town hall is a city's building, persisted with the world,
/// and a broken one would have to be repaired by something; the event's pool is gone with the event, which is what "restored at the end" means (Loïc).
/// </para>
/// <para>
/// <b>The fortress is hit through the combat drain</b> (<see cref="CombatTargetKind.Siege"/>): a player shoots it as it shoots a lair, and the drain adds
/// the blow to <see cref="_siegeFortDamage"/>, which the horde's pass takes. <b>The town hall is hit by the horde's pass</b>: a raider in reach of it with
/// no target strikes it at its weapon's mean rate, because a creature's target is a player by construction (<see cref="CreatureBrain.Target"/>).
/// </para>
/// </remarks>
public sealed partial class SimBridge
{
    /// <summary>A raider this close to the town hall's centre, with nobody to fight, is besieging it: metres.</summary>
    internal const float HordeSiegeM = 50f;

    /// <summary>
    /// A garrison raider this close to its fortress turns on whoever attacks it, over any other target (Loïc: defend the fortress first — the garrison, not
    /// the whole horde: at 250 m and every raider, a wave fresh out of the gate swarmed the assault and the fortress lost 0.3 % in a raid).
    /// </summary>
    internal const float HordeFortGuardM = 120f;

    /// <summary>An attacker of the fortress is remembered this long after its last blow, seconds.</summary>
    internal const int HordeFortAttackerS = 6;

    /// <summary>The siege's hit points are this many attackers for <see cref="HordeSiegeSeconds"/> (Loïc, 2026-11-04: "~3 min").</summary>
    internal const int HordeSiegeAttackers = 60;

    internal const int HordeSiegeSeconds = 180;

    /// <summary>The radar is sent this often during a raid, seconds.</summary>
    internal const int HordeRadarEveryS = 5;

    /// <summary>The radar's cell, metres: raiders in one are one blip.</summary>
    internal const float HordeRadarCellM = 60f;

    // The fortress's damage since the horde's last pass, from the combat drain (Interlocked: another phase, another worker).
    private long _siegeFortDamage;

    // Who struck it in that time: the drain adds (one thread, the Resolve phase), the horde's pass takes (a later phase).
    private readonly List<EntityId> _fortHitters = [];
    private readonly Dictionary<EntityId, long> _fortAttackers = [];
    private readonly List<EntityId> _fortGone = [];
    private FortGuard _fortGuard;

    // The siege's figures this pass, for the report.
    private int _hallSiegers;
    private long _hallDamage;
    private long _fortDamage;
    private long _radarDueTick;
    private readonly Dictionary<long, (double X, double Z, int N)> _radarCells = [];

    /// <summary>The fortress's guard, as CreatureThink reads it (parallel): null with nobody attacking it.</summary>
    internal FortGuard FortGuardNow => Volatile.Read(ref _fortGuard);

    /// <summary>
    /// A blow on the fortress, from the combat drain (the Apply phase, serial): added to the pool the horde's pass takes later in the same tick (Economy,
    /// serial), and its striker remembered. Tick-thread only.
    /// </summary>
    internal void SiegeBlow(EntityId attacker, int amount)
    {
        Interlocked.Add(ref _siegeFortDamage, Math.Max(0, amount));
        _fortHitters.Add(attacker);
    }

    /// <summary>
    /// The share of the defenders asked for that assaults the fortress: the bots' <c>BotBrain.AssaultPercent</c> (30 %), which the fortress's pool is sized
    /// on. The two must move together.
    /// </summary>
    internal const double HordeAssaultShare = 0.30d;

    /// <summary>
    /// The siege's hit points (Loïc: ~3 minutes): the town hall's, three minutes of sixty of this raid's raiders, their hit included; the fortress's, three
    /// minutes of the assault — <see cref="HordeAssaultShare"/> of the defenders asked for. It was sixty attackers whatever the attendance, and the
    /// assault of a 942-defender raid broke it in two minutes (night of 2026-11-04).
    /// </summary>
    internal static (int Hall, int Fort) SiegeHitPoints(int level, IReadOnlyList<int> templates, int defenderLevel, int hitPct, int targetDefenders)
    {
        double hit = 0;
        foreach (var t in templates)
        {
            hit += Scaled(ContentLevels.CreatureHit(level, CreatureTemplates.IsCamp(t)), hitPct);
        }

        hit /= Math.Max(1, templates.Count);
        var raiderDps = hit / ((TatooineData.MinAttackDelaySec + TatooineData.MaxAttackDelaySec) / 2.0);

        // A player kills an on-level beast in 8 s (content-levels.md): its damage a second is that beast's health over 8.
        var playerDps = ContentLevels.CreatureHealth(Math.Max(1, defenderLevel), false) / 8.0;
        var assault = Math.Max(1d, Math.Max(1, targetDefenders) * HordeAssaultShare);
        return ((int)Math.Max(1000, HordeSiegeAttackers * HordeSiegeSeconds * raiderDps),
            (int)Math.Min(int.MaxValue, Math.Max(1000, assault * HordeSiegeSeconds * playerDps)));
    }

    /// <summary>The town hall a city's raiders march on: its entity and centre, from the city's structures; null without one.</summary>
    private (EntityId Id, float X, float Z)? TownHallOf(Transaction tx, int city, float cx, float cz)
    {
        var structures = _cityStructures?[0];
        if (structures == null || city >= structures.Length)
        {
            return null;
        }

        SeenStructure? hall = null;
        var best = float.MaxValue;
        foreach (var s in structures[city])
        {
            var d = ((s.X - cx) * (s.X - cx)) + ((s.Z - cz) * (s.Z - cz));
            if (s.Kind == StructureKind.TownHall && d < best)
            {
                best = d;
                hall = s;
            }
        }

        if (hall is not { } h)
        {
            return null;
        }

        var sphere = new BSphere2F { CenterX = h.X, CenterY = h.Z, Radius = 2f };
        var e = Dbe.ClusterSpatialQuery<WorldObject>(new RealmId(0)).Radius(in sphere);
        try
        {
            while (e.MoveNext())
            {
                var id = e.Current.Entity;
                if (tx.TryOpen(id, out var s) && s.Read(WorldObject.Struct).Kind == StructureKind.TownHall)
                {
                    return (id, h.X, h.Z);
                }
            }
        }
        finally
        {
            e.Dispose();
        }

        return null;
    }

    /// <summary>
    /// The siege's pass, after the survey: the fortress's blows taken and its attackers published to its guards; the town hall struck by every raider at it
    /// with nobody to fight. Returns whether the core changed.
    /// </summary>
    private bool Siege(TickContext ctx, Transaction tx, long now, int hz, ref HordeCore c)
    {
        var dirty = false;
        var blows = Interlocked.Exchange(ref _siegeFortDamage, 0);
        if (blows > 0 && c.FortHp > 0)
        {
            c.FortHp = (int)Math.Max(0, c.FortHp - blows);
            _fortDamage += blows;
            dirty = true;
        }

        foreach (var hitter in _fortHitters)
        {
            _fortAttackers[hitter] = now;
        }

        _fortHitters.Clear();
        var stale = now - (HordeFortAttackerS * (long)hz);
        var gone = _fortGone;
        gone.Clear();
        foreach (var (who, at) in _fortAttackers)
        {
            if (at < stale)
            {
                gone.Add(who);
            }
        }

        foreach (var who in gone)
        {
            _fortAttackers.Remove(who);
        }

        Volatile.Write(ref _fortGuard, _fortAttackers.Count == 0 ? null : new FortGuard(c.FortX, c.FortZ, [.. _fortAttackers.Keys]));

        // The town hall: every raider within reach of it, alive and with nobody to fight, strikes it at its weapon's mean rate. A dozen of the blows are
        // drawn, so a client sees who is breaking it; the rest are only counted.
        _hallSiegers = 0;
        if (c.Hall != 0 && c.HallHp > 0)
        {
            var hall = EntityId.FromRawValue((ulong)c.Hall);
            var perSecond = 1.0 / ((TatooineData.MinAttackDelaySec + TatooineData.MaxAttackDelaySec) / 2.0);
            // At the hall, or at the square before it the raiders march on (the first live siege: they reached the square from the shuttleport's side, held
            // off the hall by its 46 m of walls, and none ever stood within reach of the hall itself).
            var toTarget = MathF.Sqrt(((c.TargetX - c.HallX) * (c.TargetX - c.HallX)) + ((c.TargetZ - c.HallZ) * (c.TargetZ - c.HallZ)));
            var reach = MathF.Max(HordeSiegeM, toTarget + HordeRallyM);
            var sphere = new BSphere2F { CenterX = c.HallX, CenterY = c.HallZ, Radius = reach };
            var e = Dbe.ClusterSpatialQuery<Creature>(new RealmId(0)).Radius(in sphere);
            double damage = 0;
            var drawn = 0;
            try
            {
                while (e.MoveNext())
                {
                    var id = e.Current.Entity;
                    if (!tx.TryOpen(id, out var raider))
                    {
                        continue;
                    }

                    var ai = raider.Read(Creature.Ai);
                    var vitals = raider.Read(Creature.Vitals);
                    if (ai.March != RaiderMarches || vitals.Health <= 0 || ai.Mode is AiMode.Pursue or AiMode.Fighting or AiMode.Dead)
                    {
                        continue;
                    }

                    var at = raider.Read(Creature.Bounds);
                    var hx = at.X - c.HallX;
                    var hdz = at.Z - c.HallZ;
                    var tx2 = at.X - c.TargetX;
                    var tz2 = at.Z - c.TargetZ;
                    if ((hx * hx) + (hdz * hdz) > HordeSiegeM * HordeSiegeM && (tx2 * tx2) + (tz2 * tz2) > HordeRallyM * HordeRallyM)
                    {
                        continue;
                    }

                    _hallSiegers++;
                    damage += vitals.AttackDamage * perSecond;
                    if (drawn < 12 && (now + id.EntityKey) % 2 == 0)
                    {
                        drawn++;
                        var amount = (ushort)Math.Clamp(vitals.AttackDamage, 0, ushort.MaxValue);
                        TatooineReplication.Strike(ctx, new Attack { Attacker = id, Target = hall, Amount = amount });
                    }
                }
            }
            finally
            {
                e.Dispose();
            }

            if (damage > 0)
            {
                var d = (int)Math.Ceiling(damage);
                c.HallHp = Math.Max(0, c.HallHp - d);
                _hallDamage += d;
                dirty = true;
            }
        }

        return dirty;
    }

    /// <summary>The raider clusters, from the survey's cells: the most crowded <see cref="HordeRadar.Max"/>, for everyone on the planet.</summary>
    private void SendRadar(TickContext ctx, long now, int hz)
    {
        if (now < _radarDueTick)
        {
            return;
        }

        _radarDueTick = now + (HordeRadarEveryS * (long)hz);
        var radar = new HordeRadar { Event = _horde.RawValue };
        Span<int> best = stackalloc int[HordeRadar.Max];
        Span<long> keys = stackalloc long[HordeRadar.Max];
        var n = 0;
        foreach (var (key, cell) in _radarCells)
        {
            // Insertion into the few kept, most raiders first.
            var i = n < HordeRadar.Max ? n++ : HordeRadar.Max - 1;
            if (i == HordeRadar.Max - 1 && n == HordeRadar.Max && cell.N <= best[i])
            {
                continue;
            }

            while (i > 0 && best[i - 1] < cell.N)
            {
                best[i] = best[i - 1];
                keys[i] = keys[i - 1];
                i--;
            }

            best[i] = cell.N;
            keys[i] = key;
        }

        for (var i = 0; i < n; i++)
        {
            var cell = _radarCells[keys[i]];
            radar.Set(i, (float)(cell.X / cell.N), (float)(cell.Z / cell.N), (ushort)Math.Min(cell.N, ushort.MaxValue));
        }

        radar.Count = (byte)n;
        TatooineReplication.Radar(ctx, in radar);
    }

    /// <summary>A raider into the radar's cells, from the survey.</summary>
    private void RadarCount(float x, float z)
    {
        var key = ((long)MathF.Floor(x / HordeRadarCellM) << 32) ^ (uint)(int)MathF.Floor(z / HordeRadarCellM);
        var cell = _radarCells.GetValueOrDefault(key);
        _radarCells[key] = (cell.X + x, cell.Z + z, cell.N + 1);
    }

    /// <summary>
    /// A raider near its fortress while someone attacks it: the attacker nearest it, alive, within <see cref="HordeFortGuardM"/> of the fortress — its
    /// target over any other. False when the fortress is not attacked, the raider is far from it, or it already fights one of its attackers.
    /// </summary>
    private bool FortressDefence(TickContext ctx, FortGuard guard, float x, float z, EntityId current, out EntityId attacker, out float ax, out float az)
    {
        attacker = EntityId.Null;
        ax = 0f;
        az = 0f;
        var gx = x - guard.X;
        var gz = z - guard.Z;
        if ((gx * gx) + (gz * gz) > HordeFortGuardM * HordeFortGuardM)
        {
            return false;
        }

        var best = float.MaxValue;
        foreach (var who in guard.Attackers)
        {
            if (who.IsNull)
            {
                continue;
            }

            if (who == current)
            {
                return false;
            }

            if (!ctx.Accessor.TryOpen(who, out var body) || body.Read(Player.Vitals).Health <= 0)
            {
                continue;
            }

            var at = body.Read(Player.Bounds);
            var d = ((at.X - x) * (at.X - x)) + ((at.Z - z) * (at.Z - z));
            if (d < best)
            {
                best = d;
                attacker = who;
                ax = at.X;
                az = at.Z;
            }
        }

        return !attacker.IsNull;
    }
}
