using System;

namespace SwgTatooine.Server;

/// <summary>Where a drop comes from (inventory-loot.md § 4): a kill, a broken wild lair's cache, a mission lair's cache.</summary>
public enum DropSource : byte
{
    Kill = 1,
    WildCache = 2,
    MissionCache = 3,
}

/// <summary>One line of a drop: so many units of one template at one Mk.</summary>
public readonly record struct DropLine(ushort Template, byte Mk, int Count);

/// <summary>A rolled drop: at most one core or add-on, first, then up to four material families (inventory-loot.md § 4).</summary>
public readonly struct Drop
{
    /// <summary>The most lines a drop has: one part and four families.</summary>
    public const int MaxLines = 5;

    private readonly DropLine _l0;
    private readonly DropLine _l1;
    private readonly DropLine _l2;
    private readonly DropLine _l3;
    private readonly DropLine _l4;

    internal Drop(ReadOnlySpan<DropLine> lines)
    {
        Lines = Math.Min(lines.Length, MaxLines);
        _l0 = Lines > 0 ? lines[0] : default;
        _l1 = Lines > 1 ? lines[1] : default;
        _l2 = Lines > 2 ? lines[2] : default;
        _l3 = Lines > 3 ? lines[3] : default;
        _l4 = Lines > 4 ? lines[4] : default;
    }

    /// <summary>How many lines it has.</summary>
    public int Lines { get; }

    /// <summary>Line <paramref name="i"/>, in placement order: the core or add-on first.</summary>
    public DropLine this[int i] => i switch
    {
        0 => _l0,
        1 => _l1,
        2 => _l2,
        3 => _l3,
        4 => _l4,
        _ => default,
    };

    /// <summary>Units in the drop, every line together.</summary>
    public int Units
    {
        get
        {
            var n = 0;
            for (var i = 0; i < Lines; i++)
            {
                n += this[i].Count;
            }

            return n;
        }
    }
}

/// <summary>
/// What drops, and how good it is (inventory-loot.md § 4): data, not code. Pure and deterministic: a drop is a function of the victim, the tick, its
/// species, its level and its source.
/// </summary>
/// <remarks>
/// <para>
/// <b>The numbers are the balance model's</b> (<c>claude/design/SwgTatooine/design/balance/balance_model.py</c>): the Mk roll is <c>loot_mk</c> and
/// <c>MK_ROLL</c>; the kill and mission-cache core rates are <c>supply(b)</c>'s <c>per_kill</c> and <c>per_mission</c> (90 kills/h from
/// <c>kills_per_hour()</c>, 2 missions/h, supply 1.3 × use, half from mission caches); the unit counts are <c>loot_units_per_kill</c> and
/// <c>cache_units</c>. <c>LootTableChecks</c> pins them. Marked [EST] what the model does not hold: add-ons, wild caches, which core and which add-on.
/// </para>
/// <para><b>Mk V never drops in the wild</b> (crafting-catalog § 14.3): only a mission cache reaches it.</para>
/// </remarks>
public static class LootTables
{
    /// <summary>Material units a kill drops (<c>loot_units_per_kill</c>).</summary>
    public const int LootKillUnits = 4;

    /// <summary>Material units in a broken wild lair's cache. [EST]: between a kill's and a mission lair's larger one.</summary>
    public const int WildCacheUnits = 6;

    /// <summary>Material units in a mission lair's cache (<c>cache_units</c>).</summary>
    public const int MissionCacheUnits = 10;

    /// <summary>How far, in metres from the victim, a killer's body may stand and still loot it: past the carbine's 50 m, the longest reach.</summary>
    public const float LootReachM = 64f;

    // supply(b)[2] (per kill) and supply(b)[1] (per mission) of balance_model.py, Mk I..V, to 4 significant digits. A kill never rolls Mk V.
    private static readonly float[] CoreKill = [0.004157f, 0.003116f, 0.002492f, 0.002076f, 0f];

    // [EST] = the core's rate: add-on supply is not in the model, and is measured (/swg/stats items.bySource).
    private static readonly float[] AddonKill = [0.004157f, 0.003116f, 0.002492f, 0.002076f, 0f];

    // [EST] = 10 × a kill's: a broken wild lair is worth ten kills of cores.
    private static readonly float[] CoreWildCache = [0.04157f, 0.03116f, 0.02492f, 0.02076f, 0f];

    private static readonly float[] CoreMissionCache = [0.1871f, 0.1402f, 0.1121f, 0.09344f, 0.08009f];

    /// <summary>A kill's chance of a core, by Mk (index Mk − 1).</summary>
    public static ReadOnlySpan<float> PCoreKill => CoreKill;

    /// <summary>A kill's chance of an add-on, by Mk.</summary>
    public static ReadOnlySpan<float> PAddonKill => AddonKill;

    /// <summary>A wild cache's chance that its one part is a core rather than an add-on, by Mk.</summary>
    public static ReadOnlySpan<float> PCoreWildCache => CoreWildCache;

    /// <summary>A mission cache's chance that its one part is a core rather than an add-on, by Mk.</summary>
    public static ReadOnlySpan<float> PCoreMissionCache => CoreMissionCache;

    // MK_ROLL: the loot Mk is the content's band plus −1 (20 %), 0 (65 %), +1 (13 %), +2 (2 %), as cumulative thresholds.
    private const float RollDown = 0.20f;
    private const float RollOn = 0.85f;
    private const float RollUp = 0.98f;

    // Which core: armour 77 %, ranged 9 %, melee 9 %, propulsion 5 % (the model's armour/weapon split × 0.95; propulsion [EST]). Cumulative.
    private static readonly (ushort Template, float Upto)[] Cores =
    [
        (ItemCatalog.CoreArmor, 0.77f),
        (ItemCatalog.CoreRanged, 0.86f),
        (ItemCatalog.CoreMelee, 0.95f),
        (ItemCatalog.CorePropulsion, 1f),
    ];

    // Which add-on: armour 40 %, weapon 30 %, additive 20 %, tuning 10 % [EST]. Cumulative.
    private static readonly (ushort Template, float Upto)[] Addons =
    [
        (ItemCatalog.AddonArmor, 0.40f),
        (ItemCatalog.AddonWeapon, 0.70f),
        (ItemCatalog.AddonAdditive, 0.90f),
        (ItemCatalog.AddonTuning, 1f),
    ];

    // The material families each species drops, with their weights: the midpoints of crafting-catalog § 2's ranges. Index CreatureTemplates.
    private static readonly (ushort Template, float Weight)[][] Families =
    [
        [(ItemCatalog.Hide, 3f), (ItemCatalog.Bone, 1.5f)],
        [(ItemCatalog.Gland, 1f)],
        [(ItemCatalog.Hide, 4f), (ItemCatalog.Venom, 1.5f)],
        [(ItemCatalog.Venom, 3f), (ItemCatalog.Hide, 2f)],
        [(ItemCatalog.Bone, 4f), (ItemCatalog.Fiber, 4.5f), (ItemCatalog.Venom, 2f)],
        [(ItemCatalog.Venom, 1f)],
        [(ItemCatalog.Metal, 6f), (ItemCatalog.Crystal, 2f)],
        [(ItemCatalog.Metal, 7.5f), (ItemCatalog.Crystal, 3f)],
        [(ItemCatalog.Metal, 6f), (ItemCatalog.Gas, 3f), (ItemCatalog.Chemical, 3f)],
        [(ItemCatalog.Metal, 6f), (ItemCatalog.Gas, 3f), (ItemCatalog.Chemical, 3f)],
    ];

    /// <summary>The material families a species drops; empty for an unknown species.</summary>
    public static ReadOnlySpan<(ushort Template, float Weight)> FamiliesOf(int species)
        => (uint)species < (uint)Families.Length ? Families[species] : [];

    /// <summary>A level's band: Mk 1 for levels 1-10, … 5 for 41 and above; level 0 (none) reads as 1 (balance_model.band).</summary>
    public static int Band(int level) => Math.Min(5, (Math.Max(1, level) - 1) / 10 + 1);

    /// <summary>The loot Mk of content of <paramref name="level"/> (balance_model.loot_mk): band plus the roll, clamped I-V, never V outside a mission.</summary>
    public static byte RollMk(int level, ref Rng rng, bool mission)
    {
        var r = rng.NextFloat();
        var d = r < RollDown ? -1 : r < RollOn ? 0 : r < RollUp ? 1 : 2;
        var mk = Math.Clamp(Band(level) + d, 1, 5);
        return (byte)(mission ? mk : Math.Min(mk, 4));
    }

    /// <summary>The generator of one drop: seeded by the victim and the tick, so the same death always drops the same.</summary>
    public static Rng RngFor(ulong victim, long worldTick)
    {
        // splitmix64's finaliser over both: neighbouring ids and ticks land far apart.
        var z = victim ^ ((ulong)worldTick * 0x9E3779B97F4A7C15UL);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return new Rng((uint)(z ^ (z >> 32)));
    }

    /// <summary>
    /// Rolls one drop: one Mk for all of it; for a kill, a core or an add-on at their rates (never both) and <see cref="LootKillUnits"/> units; for a
    /// cache, exactly one core or add-on and its units. The core or add-on is the first line, so a nearly full inventory keeps it.
    /// </summary>
    public static Drop Roll(byte species, int level, DropSource source, ulong victim, long worldTick)
    {
        var rng = RngFor(victim, worldTick);
        var mk = RollMk(level, ref rng, source == DropSource.MissionCache);
        Span<DropLine> lines = stackalloc DropLine[Drop.MaxLines];
        var n = 0;
        var part = PartOf(source, mk, ref rng);
        if (part != 0)
        {
            lines[n++] = new DropLine(part, mk, 1);
        }

        var families = FamiliesOf(species);
        if (families.Length > 0)
        {
            var units = source switch
            {
                DropSource.Kill => LootKillUnits,
                DropSource.WildCache => WildCacheUnits,
                _ => MissionCacheUnits,
            };

            Span<int> count = stackalloc int[families.Length];
            var total = 0f;
            foreach (var f in families)
            {
                total += f.Weight;
            }

            for (var u = 0; u < units; u++)
            {
                var pick = rng.NextFloat() * total;
                var k = 0;
                while (k < families.Length - 1 && pick >= families[k].Weight)
                {
                    pick -= families[k].Weight;
                    k++;
                }

                count[k]++;
            }

            for (var k = 0; k < families.Length; k++)
            {
                if (count[k] > 0)
                {
                    lines[n++] = new DropLine(families[k].Template, mk, count[k]);
                }
            }
        }

        return new Drop(lines[..n]);
    }

    /// <summary>The core or add-on of a drop, or 0 for none.</summary>
    private static ushort PartOf(DropSource source, byte mk, ref Rng rng)
    {
        var r = rng.NextFloat();
        var which = rng.NextFloat();
        var i = mk - 1;
        switch (source)
        {
            case DropSource.Kill:
                if (r < CoreKill[i])
                {
                    return Pick(Cores, which);
                }

                return r < CoreKill[i] + AddonKill[i] ? Pick(Addons, which) : (ushort)0;
            case DropSource.WildCache:
                return r < CoreWildCache[i] ? Pick(Cores, which) : Pick(Addons, which);
            default:
                return r < CoreMissionCache[i] ? Pick(Cores, which) : Pick(Addons, which);
        }
    }

    private static ushort Pick((ushort Template, float Upto)[] table, float r)
    {
        foreach (var (template, upto) in table)
        {
            if (r < upto)
            {
                return template;
            }
        }

        return table[^1].Template;
    }
}
