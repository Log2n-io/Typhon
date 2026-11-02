using System;
using NUnit.Framework;
using SwgTatooine.Server;

namespace SwgTatooine.Tests;

/// <summary>
/// The crafting numbers (crafting-catalog.md § 3, § 5, crafting-build.md C2) on the pure functions the workshop runs: the attributes, a seeded craft
/// replaying to the same stats (C2-2), and typical crafts landing on the balance model's targets (C2-5, § 14.5; § 5.3's padded set).
/// </summary>
[TestFixture]
internal sealed class CraftingChecks
{
    private const int Crafts = 2_000;

    [Test]
    public void TheAttributes_AreTheTable()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Materials.Of(ItemCatalog.Metal, 3, Attr.TG), Is.EqualTo(0.65), "§ 3.2: Mk III Metal TG 65");
            Assert.That(Materials.Of(ItemCatalog.Metal, 3, Attr.EN), Is.EqualTo(0.26));
            Assert.That(Materials.Of(ItemCatalog.Metal, 3, Attr.DU), Is.EqualTo(0.52));
            Assert.That(Materials.Of(ItemCatalog.Venom, 5, Attr.EN), Is.EqualTo(1.0));
            Assert.That(Materials.Of(2, 3, Attr.EN), Is.Zero, "water is bulk only");
        });
    }

    [Test]
    public void EverySchematic_ReadsOnlySlotsItHas_AndMakesWhatItsCategoryHolds()
    {
        foreach (var s in Schematics.Every)
        {
            Assert.That(ItemCatalog.TryGet(s.Output, out var t), Is.True, s.Name);
            Assert.That(ItemCatalog.HasStats(s.Output), Is.EqualTo(s.MakesInstance || s.Kind == SchematicKind.Armor), $"{s.Name}: instance or stack");
            foreach (var line in s.Lines)
            {
                foreach (var term in line.Terms)
                {
                    Assert.That(term.Slot, Is.InRange(0, s.Slots.Length - 1), $"{s.Name}.{line.Name}");
                }
            }

            Assert.That(s.Lines, Has.Length.LessThanOrEqualTo(Crafting.MaxLines), s.Name);
        }
    }

    /// <summary>C2-2: a seeded craft replays to the same stats; another serial does not.</summary>
    [Test]
    public void ASeededCraft_ReplaysToTheSameStats()
    {
        Schematics.TryGet(ItemCatalog.Blaster, out var blaster);
        var inputs = BlasterInputs(3, 0.5);
        var a = new double[4];
        var b = new double[4];
        var c = new double[4];
        Crafting.Run(blaster, inputs, 0x1234, 77, [3, 2, 1], 0, a);
        Crafting.Run(blaster, inputs, 0x1234, 77, [3, 2, 1], 0, b);
        Crafting.Run(blaster, inputs, 0x1234, 78, [3, 2, 1], 0, c);
        Assert.Multiple(() =>
        {
            Assert.That(b, Is.EqualTo(a));
            Assert.That(c, Is.Not.EqualTo(a), "another serial rolled the same craft");
        });
    }

    /// <summary>
    /// C2-5, every band: a typical craft of each weapon — every input of one Mk, parts crafted typically (2 points on their grade), the weapon 3 points on
    /// damage, 2 on delay, 1 on the third line — averages § 14.5's typical DPS within 5 %, the crits' mean gain included.
    /// </summary>
    [TestCase(ItemCatalog.Blaster, new[] { 81d, 109, 138, 167, 198 })]
    [TestCase(ItemCatalog.Sword, new[] { 99d, 132, 168, 203, 242 })]
    [TestCase(ItemCatalog.Knuckler, new[] { 94d, 126, 160, 194, 231 })]
    public void ATypicalCraft_AveragesTheModelsDps(ushort weapon, double[] dps)
    {
        Schematics.TryGet(weapon, out var s);
        Span<double> v = stackalloc double[4];
        for (byte mk = 1; mk <= 5; mk++)
        {
            double sum = 0;
            for (var i = 0; i < Crafts; i++)
            {
                var inputs = Inputs(s, mk, 1_000 + i);
                Crafting.Run(s, inputs, 99, i, [3, 2, 1], 0, v);
                sum += v[0] * Schematics.CritGain * 1000 / v[1];
            }

            var mean = sum / Crafts;
            Assert.That(mean, Is.EqualTo(dps[mk - 1]).Within(dps[mk - 1] * 0.05), $"{s.Name} Mk {mk}: {mean:F1} DPS against § 14.5's {dps[mk - 1]}");
        }
    }

    /// <summary>§ 5.3: a typical full Mk I padded set protects about 12 % against kinetic and 10 % against energy; Mk V padded about 36 / 31.</summary>
    [TestCase((byte)1, 12d, 10d)]
    [TestCase((byte)5, 36d, 31d)]
    public void ATypicalPaddedPiece_ProtectsAsTheCatalogueSays(byte mk, double kinetic, double energy)
    {
        Schematics.TryGet(Schematics.ArmorFirst + 1, out var torso);
        Span<double> v = stackalloc double[3];
        double k = 0, e = 0;
        for (var i = 0; i < Crafts; i++)
        {
            var inputs = Inputs(torso, mk, 5_000 + i);
            Crafting.Run(torso, inputs, 7, i, [3, 2, 1], 0, v);
            k += v[0];
            e += v[1];
        }

        Assert.Multiple(() =>
        {
            Assert.That(k / Crafts / 10, Is.EqualTo(kinetic).Within(1.5), "kinetic %");
            Assert.That(e / Crafts / 10, Is.EqualTo(energy).Within(1.5), "energy %");
        });
    }

    [Test]
    public void TheLuck_IsTheCataloguesOdds()
    {
        var amazing = 0;
        var poor = 0;
        for (var i = 0; i < 100_000; i++)
        {
            var f = Schematics.Assemble(Schematics.Roll(1, i, 0));
            amazing += f == 0.85 ? 1 : 0;
            poor += f == 0.45 ? 1 : 0;
        }

        Assert.Multiple(() =>
        {
            Assert.That(amazing / 100_000d, Is.EqualTo(0.05).Within(0.005));
            Assert.That(poor / 100_000d, Is.EqualTo(0.20).Within(0.01));
            Assert.That(Schematics.Experiment(0.6, 0.0), Is.EqualTo(0.76).Within(1e-9), "Amazing: +40 % of the gap");
            Assert.That(Schematics.Experiment(0.6, 0.99), Is.EqualTo(0.54).Within(1e-9), "Critical: −10 % of what the line had");
        });
    }

    // Every input of a schematic at one Mk: materials at mk, cores at mk, parts crafted typically from mk inputs (seeded by i).
    private static Schematics.Input[] Inputs(Schematic s, byte mk, long serial)
    {
        var inputs = new Schematics.Input[s.Slots.Length];
        for (var k = 0; k < s.Slots.Length; k++)
        {
            var slot = s.Slots[k];
            inputs[k] = slot.Kind switch
            {
                SlotKind.Part => PartInput(slot.Template == 0 ? Schematics.PaddedSegment : slot.Template, mk, (serial * 31) + k),
                _ => new Schematics.Input(slot.Template, mk, 0, 0, 0),
            };
        }

        return inputs;
    }

    private static Schematics.Input PartInput(ushort part, byte mk, long serial)
    {
        Schematics.TryGet(part, out var p);
        Span<double> v = stackalloc double[1];
        Crafting.Run(p, Inputs(p, mk, serial + 7), 3, serial, [Schematics.PartPoints], 0, v);
        var (bk, be) = Schematics.BiasOf(part);
        return new Schematics.Input(part, 0, v[0] / 1000, bk, be);
    }

    private static Schematics.Input[] BlasterInputs(byte mk, double grade)
        =>
        [
            new(ItemCatalog.CoreRanged, mk, 0, 0, 0),
            new(Schematics.BarrelPistol, 0, grade, 0, 0),
            new(Schematics.PowerCell, 0, grade, 0, 0),
            new(Schematics.Grip, 0, grade, 0, 0),
            new(ItemCatalog.Metal, 1, 0, 0, 0),
        ];
}
