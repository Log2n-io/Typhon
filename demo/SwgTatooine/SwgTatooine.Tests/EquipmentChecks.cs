using System;
using NUnit.Framework;
using SwgTatooine.Server;

namespace SwgTatooine.Tests;

/// <summary>
/// The equipment tables (crafting-build.md C1, crafting-catalog.md § 5, § 9.3, § 15.3) pinned against <c>balance_model.py</c>: standard issue is a typical
/// Mk I craft, the vendor sells it at twice its value, the CL gate, and the combat rolls' distribution.
/// </summary>
[TestFixture]
internal sealed class EquipmentChecks
{
    // balance_model.py, run 2026-11-02: a typical Mk I craft of each weapon — damage per hit, delay in seconds (weapon_q(1), solve_damage_ranges()).
    private static readonly (ushort Template, double Damage, double Delay)[] ModelMkI =
    [
        (ItemCatalog.Knuckler, 100.3, 1.061),
        (ItemCatalog.Sword, 148.8, 1.503),
        (ItemCatalog.Blaster, 82.7, 1.021),
    ];

    // balance_model.py: item_values(b), the typical weapon and armour set, credits.
    private static readonly double[] ModelWeapon = [0, 905.850, 1795.068, 3456.878, 6498.354, 11976.792];
    private static readonly double[] ModelArmorSet = [0, 6340.067, 13255.242, 26232.035, 50055.593, 93069.962];

    [Test]
    public void StandardIssueWeapons_AreATypicalMkICraft_LessTheCritsMeanGain()
    {
        var critGain = 1 + (Equipment.CritChance * (Equipment.CritMultiplier - 1));
        foreach (var (template, damage, delay) in ModelMkI)
        {
            Assert.That(Equipment.TryStandard(template, out var s), Is.True, $"template {template} is standard issue");
            Assert.That(s.Stat0, Is.EqualTo(damage / critGain).Within(0.6), $"template {template}: damage ÷ {critGain}");
            Assert.That(s.Stat1, Is.EqualTo(delay * 1000).Within(1), $"template {template}: delay ms");
            Assert.That(s.Stat0 * critGain * 1000 / s.Stat1, Is.EqualTo(damage / delay).Within(1), $"template {template}: § 14.5's typical Mk I DPS");
        }
    }

    [Test]
    public void StandardIssue_IsWeaponsAndBothArmourSets_ButNoCarbine()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Equipment.Standard.Length, Is.EqualTo(3 + 12));
            foreach (var s in Equipment.Standard)
            {
                Assert.That(Equipment.SlotOf(s.Template), Is.Not.Zero, $"template {s.Template} is worn somewhere");
                Assert.That(s.MaxCondition, Is.Positive, $"template {s.Template} has a condition");
                Assert.That(ItemCatalog.TryGet(s.Template, out var t) && ItemCatalog.MkFits(in t, 1), Is.True, $"template {s.Template} takes Mk I");
            }

            Assert.That(ItemCatalog.IdOf("weapon_carbine"), Is.Zero, "the carbine waits for its stance (O-4)");
        });
    }

    [Test]
    public void TheVendor_SellsStandardIssue_AtTwiceItsValue()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Equipment.StandardPrice(ItemCatalog.Blaster), Is.EqualTo(1_812), "§ 15.3: a standard weapon is 1 812 cr");
            Assert.That(Equipment.StandardPrice(ItemCatalog.PaddedHelmet + 1), Is.EqualTo(4_184), "the torso: 6 340 × 33 % × 2");
            Assert.That(Equipment.StandardPrice(ItemCatalog.CoreArmor), Is.Zero, "a core is not standard issue");
            for (byte mk = 1; mk <= 5; mk++)
            {
                Assert.That(MarketValues.MilliOf(ItemCatalog.Sword, mk), Is.EqualTo(ModelWeapon[mk] * 1000).Within(500), $"weapon Mk {mk}");
                var set = 0L;
                for (var piece = 0; piece < 6; piece++)
                {
                    set += MarketValues.MilliOf((ushort)(ItemCatalog.CompositeHelmet + piece), mk);
                }

                Assert.That(set, Is.EqualTo(ModelArmorSet[mk] * 1000).Within(1_000), $"armour set Mk {mk}: the pieces add up to the set");
            }
        });
    }

    [Test]
    public void TheArmourShares_AddUpToTheSet()
    {
        var total = 0;
        foreach (var share in EquipSlot.SharePerMille)
        {
            total += share;
        }

        Assert.That(total, Is.EqualTo(1000));
    }

    [Test]
    public void EquippingMkK_NeedsCL10kMinus9()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Equipment.LevelFor(1), Is.EqualTo(1));
            Assert.That(Equipment.LevelFor(2), Is.EqualTo(11));
            Assert.That(Equipment.LevelFor(5), Is.EqualTo(41));
        });
    }

    [Test]
    public void CreatureDamageTypes_SoldiersAreEnergy_EverythingElseKinetic()
    {
        Assert.That(CreatureTemplates.Damage, Has.Length.EqualTo(CreatureTemplates.Count));
        for (var k = 0; k < CreatureTemplates.Count; k++)
        {
            var energy = k is CreatureTemplates.Soldier or CreatureTemplates.BlueSoldier;
            Assert.That(CreatureTemplates.Damage[k], Is.EqualTo(energy ? DamageType.Energy : DamageType.Kinetic), CreatureTemplates.Name(k));
        }
    }

    /// <summary>C1-3's distribution, on the pure function: 10 000 seeded rolls average damage × (1 + c(m − 1)) within 1 %, and 5 % ± 0.5 % are crits.</summary>
    [Test]
    public void TenThousandRolls_MeanAndCritRate_AreTheCatalogues()
    {
        const int n = 10_000;
        const int damage = 1_000;
        var rng = new Random(20261102);
        long sum = 0;
        var crits = 0;
        for (var i = 0; i < n; i++)
        {
            sum += Equipment.Roll(damage, rng.NextSingle(), rng.NextSingle(), out var crit);
            crits += crit ? 1 : 0;
        }

        var expected = damage * (1 + (Equipment.CritChance * (Equipment.CritMultiplier - 1)));
        Assert.Multiple(() =>
        {
            Assert.That((double)sum / n, Is.EqualTo(expected).Within(expected * 0.01));
            Assert.That((double)crits / n, Is.EqualTo(Equipment.CritChance).Within(0.005));
        });
    }

    [Test]
    public void Mitigation_IsCappedAt75Percent_AndNeverBelowOne()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Equipment.Mitigate(100, 120), Is.EqualTo(88));
            Assert.That(Equipment.Mitigate(100, 900), Is.EqualTo(25), "capped at 750 ‰");
            Assert.That(Equipment.Mitigate(1, 750), Is.EqualTo(1), "the floor");
            Assert.That(Equipment.Mitigate(100, 0), Is.EqualTo(100));
        });
    }
}
