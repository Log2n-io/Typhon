using System;

namespace SwgTatooine.Server;

/// <summary>A material attribute (crafting-catalog.md § 3.2): toughness, energy, durability, 0-100.</summary>
public static class Attr
{
    public const int TG = 0;
    public const int EN = 1;
    public const int DU = 2;
}

/// <summary>
/// The materials' attributes (crafting-catalog.md § 3.2): <c>round(base[tier] × profile[family]) / 100</c>, fixed per (family, Mk), so every Mk III Metal in
/// the world is identical. Ore and Water are bulk only: no attributes.
/// </summary>
public static class Materials
{
    private static readonly int[] Base = [0, 25, 45, 65, 82, 100];

    // Indexed by material template 1..11: TG, EN, DU × 10.
    private static readonly int[][] Profile =
    [
        [0, 0, 0],
        [0, 0, 0],    // Ore
        [0, 0, 0],    // Water
        [10, 4, 8],   // Metal
        [3, 10, 6],   // Crystal
        [0, 9, 3],    // Gas
        [2, 7, 7],    // Chemical
        [6, 1, 9],    // Fiber
        [8, 1, 7],    // Hide
        [9, 2, 5],    // Bone
        [0, 9, 6],    // Gland
        [0, 10, 4],   // Venom
    ];

    /// <summary>A material's attribute at a Mk, 0..1; 0 for anything that is not a material at Mk I-V.</summary>
    public static double Of(ushort template, byte mk, int attr)
    {
        if (template is < 1 or > 11 || mk is < 1 or > 5)
        {
            return 0d;
        }

        return Math.Round(Base[mk] * Profile[template][attr] / 10d, MidpointRounding.AwayFromZero) / 100d;
    }
}

/// <summary>What fills a schematic's slot (crafting-catalog.md § 1 rule 2): a quality material, a bulk material, a part, a core, or nothing.</summary>
public enum SlotKind : byte
{
    /// <summary>A material whose attributes set the result: the crafter picks its Mk.</summary>
    Quality,

    /// <summary>A material in quantity: any Mk, the server takes the lowest that covers it.</summary>
    Bulk,

    /// <summary>Crafted parts (instances with a grade), <see cref="SchematicSlot.Count"/> of them.</summary>
    Part,

    /// <summary>A core (a stack): its grade is its Mk's (§ 3.3: Mk I .20 … Mk V 1.00), and it sets the item's band.</summary>
    Core,
}

/// <summary>One input slot: what fills it and how many.</summary>
public readonly record struct SchematicSlot(SlotKind Kind, ushort Template, int Count);

/// <summary>What a term reads of a slot: one attribute of its material, or its grade (a part's, a core's), or a segment's grade times its bias.</summary>
public enum TermSource : byte
{
    Toughness,
    Energy,
    Durability,
    Grade,
    GradeKinetic,
    GradeEnergy,
}

/// <summary>One weighted term of a line's ceiling: <c>weight × source(slot)</c>.</summary>
public readonly record struct Term(int Slot, TermSource Source, double Weight);

/// <summary>
/// One stat line (crafting-catalog.md § 3.4): <c>min + (max − min) × q × f</c>, or <c>max − …</c> for a line where lower is better (a delay). <c>q</c> is
/// the weighted inputs (the ceiling), <c>f</c> how far the craft got.
/// </summary>
public readonly record struct Line(string Name, double Min, double Max, bool Lower, Term[] Terms, bool Enhanced = false);

/// <summary>What a schematic makes.</summary>
public enum SchematicKind : byte
{
    Part,
    Core,
    Weapon,
    Armor,
    Potion,
    Kit,
}

/// <summary>A schematic (crafting-catalog.md § 13): its output, its slots, its lines and its experimentation points (§ 3.4: 2 on a part, 6 on an item).</summary>
public sealed record Schematic(ushort Id, string Name, SchematicKind Kind, ushort Output, SchematicSlot[] Slots, Line[] Lines, int Points)
{
    /// <summary>Whether the result is an instance with stats; else a stack (a kit, a crafted Mk I core, a module of bulk only).</summary>
    public bool MakesInstance => Lines.Length > 0;
}

/// <summary>
/// The D2 schematics (crafting-catalog.md § 5, § 13) and the crafting luck (§ 3.4), as pure functions: what the workshop, the client's twin and the checks
/// all compute the same way.
/// </summary>
/// <remarks>
/// <para><b>Ids are the output template's</b>, but for the two segments and the stocks, whose output templates are their own, and the armour pieces, whose
/// one schematic makes the padded or the composite piece by the segments it is given (§ 13.6).</para>
/// <para><b>The carbine waits for its stance</b> (O-4), and the swoop, the structures and their modules for C5-C6: their schematics are not here yet.</para>
/// </remarks>
public static class Schematics
{
    /// <summary>A part's experimentation points (§ 3.4).</summary>
    public const int PartPoints = 2;

    /// <summary>A final item's.</summary>
    public const int ItemPoints = 6;

    /// <summary>What an add-on of Mk k adds to an enhanced line, on top of the ceiling: 3 % × k of its range (§ 3.4 step 3).</summary>
    public const double EnhancePerMk = 0.03;

    /// <summary>The bench fee: 3 % of the item's market value (crafting-catalog.md § 15.4, C5).</summary>
    public const int BenchFeePerMille = 30;

    /// <summary>The crits' mean gain, by which a weapon's damage range is divided once (§ 9.3).</summary>
    public static readonly double CritGain = 1 + (Equipment.CritChance * (Equipment.CritMultiplier - 1));

    // Templates of the parts (ItemCatalog ids).
    public const ushort BarrelPistol = 20;
    public const ushort PowerCell = 22;
    public const ushort StockBone = 23;
    public const ushort StockFiber = 24;
    public const ushort Grip = 25;
    public const ushort Blade = 26;
    public const ushort KnucklePlate = 27;
    public const ushort VibroUnit = 28;
    public const ushort PaddedSegment = 29;
    public const ushort CompositeSegment = 30;
    public const ushort Restorative = 31;
    public const ushort Stimulant = 32;
    public const ushort Hardener = 33;
    public const ushort Vital = 34;
    public const ushort Vial = 35;

    /// <summary>The armour schematics' ids, one per piece: 44-49, the padded templates' — the segments choose padded or composite.</summary>
    public const ushort ArmorFirst = ItemCatalog.PaddedHelmet;

    private const ushort Metal = ItemCatalog.Metal;
    private const ushort Crystal = ItemCatalog.Crystal;
    private const ushort Gas = ItemCatalog.Gas;
    private const ushort Chemical = ItemCatalog.Chemical;
    private const ushort Fiber = ItemCatalog.Fiber;
    private const ushort Hide = ItemCatalog.Hide;
    private const ushort Bone = ItemCatalog.Bone;
    private const ushort Gland = ItemCatalog.Gland;
    private const ushort Venom = ItemCatalog.Venom;
    private const ushort Water = 2;

    private static SchematicSlot Q(ushort family, int n) => new(SlotKind.Quality, family, n);

    private static SchematicSlot B(ushort family, int n) => new(SlotKind.Bulk, family, n);

    private static SchematicSlot P(ushort part, int n = 1) => new(SlotKind.Part, part, n);

    private static SchematicSlot C(ushort core) => new(SlotKind.Core, core, 1);

    private static Term T(int slot, TermSource source, double weight) => new(slot, source, weight);

    // A part's one line: its grade, 0..1, stored per mille.
    private static Line GradeLine(params Term[] terms) => new("grade", 0, 1000, false, terms);

    private static Schematic Part(ushort id, string name, SchematicSlot[] slots, params Term[] grade)
        => new(id, name, SchematicKind.Part, id, slots, [GradeLine(grade)], PartPoints);

    // A weapon's lines: damage (§ 14.5's solved range ÷ the crits' gain), delay (ms), reach (dm), condition (combat-ms, § 9.1: 60-360 min).
    private static Line Damage(double min, double max, params Term[] t) => new("damage", min / CritGain, max / CritGain, false, t, Enhanced: true);

    private static Line Delay(double slowS, double fastS, params Term[] t) => new("delayMs", fastS * 1000, slowS * 1000, true, t);

    private static Line Reach(double minM, double maxM, params Term[] t) => new("rangeDm", minM * 10, maxM * 10, false, t);

    private static Line WeaponCondition(params Term[] t) => new("condition", 60 * 60_000, 360 * 60_000, false, t);

    private static readonly Schematic[] All = Build();

    private static Schematic[] Build()
    {
        const TermSource tg = TermSource.Toughness;
        const TermSource en = TermSource.Energy;
        const TermSource du = TermSource.Durability;
        const TermSource g = TermSource.Grade;
        var list = new System.Collections.Generic.List<Schematic>
        {
            // § 13.4: crafted parts, each assembly + 2 points on its grade.
            Part(BarrelPistol, "Pistol Barrel", [Q(Metal, 12), Q(Crystal, 4)], T(0, tg, .6), T(0, du, .2), T(1, en, .2)),
            Part(PowerCell, "Power Cell", [Q(Gas, 8), Q(Crystal, 4), B(Metal, 4)], T(0, en, .6), T(1, en, .4)),
            Part(StockBone, "Bone Stock", [Q(Bone, 10), B(Metal, 6)], T(0, tg, .5), T(0, du, .5)),
            Part(StockFiber, "Fiber Stock", [Q(Fiber, 10), B(Metal, 6)], T(0, tg, .5), T(0, du, .5)),
            Part(Grip, "Grip", [Q(Hide, 6), B(Metal, 4)], T(0, tg, .5), T(0, du, .5)),
            Part(Blade, "Blade", [Q(Metal, 20), B(Crystal, 4)], T(0, tg, .7), T(0, du, .3)),
            Part(KnucklePlate, "Knuckle Plate", [Q(Metal, 10)], T(0, tg, .7), T(0, du, .3)),
            Part(VibroUnit, "Vibro Unit", [P(PowerCell), Q(Crystal, 4)], T(0, g, .6), T(1, en, .4)),
            Part(PaddedSegment, "Padded Segment", [Q(Hide, 8), B(Fiber, 6)], T(0, tg, .6), T(0, du, .4)),
            Part(CompositeSegment, "Composite Segment", [Q(Metal, 8), Q(Crystal, 4), B(Fiber, 4)], T(0, tg, .5), T(0, du, .2), T(1, en, .3)),
            Part(Restorative, "Restorative Compound", [Q(Gland, 6), B(Water, 10)], T(0, en, 1)),
            Part(Stimulant, "Stimulant Compound", [Q(Venom, 6), B(Chemical, 4)], T(0, en, 1)),
            Part(Hardener, "Hardener Compound", [Q(Bone, 6), B(Chemical, 4)], T(0, tg, .5), T(0, en, .5)),
            Part(Vital, "Vital Compound", [Q(Gland, 4), Q(Hide, 4), B(Water, 10)], T(0, en, .6), T(1, du, .4)),
            Part(Vial, "Vial", [Q(Crystal, 4), Q(Chemical, 4)], T(0, du, .5), T(1, du, .5)),

            // § 13.2: the three Mk I cores, from bulk: a stack at Mk I, grade .20 like a looted Mk I.
            new(ItemCatalog.CoreRanged, "Ranged Weapon Core", SchematicKind.Core, ItemCatalog.CoreRanged, [B(Metal, 10), B(Crystal, 5)], [], 0),
            new(ItemCatalog.CoreMelee, "Melee Weapon Core", SchematicKind.Core, ItemCatalog.CoreMelee, [B(Metal, 12), B(Bone, 4)], [], 0),
            new(ItemCatalog.CoreArmor, "Armor Core", SchematicKind.Core, ItemCatalog.CoreArmor, [B(Metal, 8), B(Fiber, 8)], [], 0),

            // § 13.5 with § 14.5's solved damage ranges: the knuckler, the sword, the blaster.
            new(ItemCatalog.Knuckler, "Vibro Knuckler", SchematicKind.Weapon, ItemCatalog.Knuckler,
                [C(ItemCatalog.CoreMelee), P(KnucklePlate), P(VibroUnit), B(Metal, 15)],
                [
                    Damage(68, 301, T(0, g, .5), T(2, g, .3), T(1, g, .2)),
                    Delay(1.10, 0.80, T(2, g, .5), T(0, g, .5)),
                    Reach(1, 1),
                    WeaponCondition(T(1, g, 1)),
                ],
                ItemPoints),
            new(ItemCatalog.Sword, "Vibroblade", SchematicKind.Weapon, ItemCatalog.Sword,
                [C(ItemCatalog.CoreMelee), P(Blade), P(Grip), P(VibroUnit), B(Metal, 25)],
                [
                    Damage(100, 460, T(0, g, .5), T(1, g, .3), T(3, g, .2)),
                    Delay(1.55, 1.15, T(2, g, .5), T(0, g, .5)),
                    Reach(3, 3),
                    WeaponCondition(T(1, g, .6), T(2, g, .4)),
                ],
                ItemPoints),
            new(ItemCatalog.Blaster, "DL-44 Blaster", SchematicKind.Weapon, ItemCatalog.Blaster,
                [C(ItemCatalog.CoreRanged), P(BarrelPistol), P(PowerCell), P(Grip), B(Metal, 20)],
                [
                    Damage(55, 259, T(0, g, .5), T(2, g, .3), T(1, g, .2)),
                    Delay(1.05, 0.80, T(3, g, .5), T(0, g, .5)),
                    Reach(25, 35, T(1, g, .6), T(0, g, .4)),
                    WeaponCondition(T(1, g, .5), T(3, g, .5)),
                ],
                ItemPoints),

            // § 13.7: potions — potency from the compound, duration from the vial.
            Potion(56, "Stim", Restorative, Water, 10, 150, 600, 0, 0),
            Potion(57, "Regen Draught", Restorative, Water, 10, 5, 25, 20, 40),
            Potion(58, "Fury Tonic", Stimulant, Chemical, 5, 8, 35, 60, 240),
            Potion(59, "Haste Tonic", Stimulant, Chemical, 5, 5, 20, 60, 180),
            Potion(60, "Ironhide Tonic", Hardener, Chemical, 5, 5, 25, 60, 240),
            Potion(61, "Vigor Tonic", Vital, Water, 10, 5, 25, 120, 480),

            // § 13.10: kits, bulk only.
            new(62, "Weapon Repair Kit", SchematicKind.Kit, 62, [B(Metal, 10), B(Chemical, 5)], [], 0),
            new(63, "Armour Repair Kit", SchematicKind.Kit, 63, [B(Hide, 10), B(Fiber, 10)], [], 0),
            new(64, "Vehicle Repair Kit", SchematicKind.Kit, 64, [B(Metal, 15), B(Gas, 5)], [], 0),
        };

        // § 13.6: the six armour pieces, one schematic each; Armor Core, n segments, Fiber in bulk.
        int[] segments = [2, 4, 3, 3, 1, 1];
        int[] fiber = [10, 20, 15, 15, 5, 5];
        string[] names = ["Helmet", "Torso", "Arms", "Legs", "Gloves", "Boots"];
        for (var i = 0; i < 6; i++)
        {
            list.Add(new Schematic(
                (ushort)(ArmorFirst + i),
                names[i],
                SchematicKind.Armor,
                (ushort)(ArmorFirst + i),
                [C(ItemCatalog.CoreArmor), new SchematicSlot(SlotKind.Part, 0, segments[i]), B(Fiber, fiber[i])],
                [
                    new Line("kinetic", 50, 600, false, [T(0, g, .5), T(1, TermSource.GradeKinetic, .5)], Enhanced: true),
                    new Line("energy", 50, 600, false, [T(0, g, .5), T(1, TermSource.GradeEnergy, .5)], Enhanced: true),
                    new Line("condition", 0, 1, false, [T(1, g, .7), T(0, g, .3)]),
                ],
                ItemPoints));
        }

        return [.. list];
    }

    private static Schematic Potion(ushort id, string name, ushort compound, ushort solvent, int solventN, double potencyMin, double potencyMax,
        double durationMinS, double durationMaxS)
    {
        var lines = durationMaxS > 0
            ? new[]
            {
                new Line("potency", potencyMin, potencyMax, false, [new Term(0, TermSource.Grade, 1)], Enhanced: true),
                new Line("durationMs", durationMinS * 1000, durationMaxS * 1000, false, [new Term(1, TermSource.Grade, 1)]),
            }
            : [new Line("potency", potencyMin, potencyMax, false, [new Term(0, TermSource.Grade, 1)], Enhanced: true)];
        return new Schematic(id, name, SchematicKind.Potion, id, [P(compound), P(Vial), B(solvent, solventN)], lines, ItemPoints);
    }

    /// <summary>Every schematic.</summary>
    public static ReadOnlySpan<Schematic> Every => All;

    /// <summary>A schematic by id; <see langword="false"/> for none.</summary>
    public static bool TryGet(ushort id, out Schematic schematic)
    {
        foreach (var s in All)
        {
            if (s.Id == id)
            {
                schematic = s;
                return true;
            }
        }

        schematic = null;
        return false;
    }

    /// <summary>A potion's doses (§ 5.4): a stim 3, a regen draught 2, a tonic 1.</summary>
    public static int DosesOf(ushort template) => template switch
    {
        56 => 3,
        57 => 2,
        _ => 1,
    };

    /// <summary>A segment's biases (§ 5.1): padded Kinetic 1.0 / Energy 0.6, composite the mirror.</summary>
    public static (double Kinetic, double Energy) BiasOf(ushort segment)
        => segment == CompositeSegment ? (0.6, 1.0) : (1.0, 0.6);

    /// <summary>A core's grade by its Mk (§ 3.3): Mk I .20 … Mk V 1.00.</summary>
    public static double CoreGrade(byte mk) => Math.Clamp(mk, (byte)0, (byte)5) * 0.2;

    /// <summary>
    /// One slot as the craft reads it: a material's Mk (quality and bulk slots), a core's Mk, or the parts' grades and biases, averaged over the slot's
    /// count (§ 5.3: "segments may be mixed; their grade and their biases average").
    /// </summary>
    public readonly record struct Input(ushort Template, byte Mk, double Grade, double BiasKinetic, double BiasEnergy);

    /// <summary>What a term reads of its slot's input.</summary>
    public static double ValueOf(in Term term, ReadOnlySpan<Input> inputs, ReadOnlySpan<SchematicSlot> slots)
    {
        var i = inputs[term.Slot];
        return term.Source switch
        {
            TermSource.Toughness => Materials.Of(i.Template, i.Mk, Attr.TG),
            TermSource.Energy => Materials.Of(i.Template, i.Mk, Attr.EN),
            TermSource.Durability => Materials.Of(i.Template, i.Mk, Attr.DU),
            TermSource.Grade => slots[term.Slot].Kind == SlotKind.Core ? CoreGrade(i.Mk) : i.Grade,
            TermSource.GradeKinetic => i.Grade * i.BiasKinetic,
            TermSource.GradeEnergy => i.Grade * i.BiasEnergy,
            _ => 0d,
        };
    }

    /// <summary>A line's ceiling <c>q</c>: its weighted inputs, 0..1. A line with no terms (a fixed reach) is 1.</summary>
    public static double Ceiling(in Line line, ReadOnlySpan<Input> inputs, ReadOnlySpan<SchematicSlot> slots)
    {
        if (line.Terms.Length == 0)
        {
            return 1d;
        }

        var q = 0d;
        foreach (var t in line.Terms)
        {
            q += t.Weight * ValueOf(in t, inputs, slots);
        }

        return Math.Clamp(q, 0d, 1d);
    }

    /// <summary>
    /// A line's value: <c>min + (max − min) × q × f</c>, or <c>max − …</c> for a lower-is-better line; plus an enhancement of Mk <paramref name="addonMk"/>
    /// on an enhanced line, 3 % × Mk of its range on top of the ceiling, never past its best.
    /// </summary>
    public static double ValueOf(in Line line, double q, double f, byte addonMk)
    {
        var span = line.Max - line.Min;
        var reached = Math.Clamp(q * f, 0d, 1d);
        var extra = line.Enhanced ? EnhancePerMk * addonMk : 0d;
        var share = Math.Min(1d, reached + extra);
        return line.Lower ? line.Max - (span * share) : line.Min + (span * share);
    }

    // ── The luck (§ 3.4) ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The assembly roll: Amazing .85 (5 %) · Great .70 (20 %) · Good .60 (55 %) · Poor .45 (20 %).</summary>
    public static double Assemble(double u) => u < 0.05 ? 0.85 : u < 0.25 ? 0.70 : u < 0.80 ? 0.60 : 0.45;

    /// <summary>
    /// One experimentation point on a line at <paramref name="f"/>: Amazing +40 % of the gap to the ceiling (5 %) · Great +25 % (20 %) · Good +15 % (50 %)
    /// · Fail 0 (20 %) · Critical −10 % of what the line had (5 %).
    /// </summary>
    public static double Experiment(double f, double u)
    {
        var gap = 1d - f;
        return u < 0.05 ? f + (0.40 * gap)
            : u < 0.25 ? f + (0.25 * gap)
            : u < 0.75 ? f + (0.15 * gap)
            : u < 0.95 ? f
            : f * 0.90;
    }

    /// <summary>
    /// A roll of a craft's step: <c>hash(crafter, serial, step)</c> to [0, 1) (§ 1 rule 4), so the server, the client's twin and the checks agree. Step 0
    /// is the assembly; step <c>n</c> the n-th experimentation point.
    /// </summary>
    public static double Roll(long crafter, long serial, int step)
    {
        var h = unchecked((ulong)crafter * 0x9E3779B97F4A7C15UL) ^ unchecked((ulong)serial * 0xC2B2AE3D27D4EB4FUL) ^ unchecked((ulong)step * 0x165667B19E3779F9UL);
        h ^= h >> 33;
        h *= 0xFF51AFD7ED558CCDUL;
        h ^= h >> 33;
        h *= 0xC4CEB9FE1A85EC53UL;
        h ^= h >> 33;
        return (h >> 11) * (1d / (1UL << 53));
    }
}
