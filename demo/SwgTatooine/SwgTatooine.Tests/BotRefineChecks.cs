using System.Collections.Generic;
using NUnit.Framework;
using SwgTatooine.Bots;
using SwgTatooine.Server;

namespace SwgTatooine.Tests;

/// <summary>
/// What a bot refines (<see cref="RefinePlan"/>, crafting-catalog.md § 11.1): its cores and add-ons below its band, held N times, not what its next craft
/// takes; materials left to its crafts. Its numbers are the server's <see cref="Refining"/>'s.
/// </summary>
[TestFixture]
internal sealed class BotRefineChecks
{
    [Test]
    public void TheBotsRatios_AreTheServers()
    {
        for (byte mk = 0; mk <= 5; mk++)
        {
            Assert.That(RefinePlan.RatioOf(mk), Is.EqualTo(Refining.RatioOf(ItemCatalog.CoreRanged, mk)), $"Mk {mk}");
            Assert.That(RefinePlan.RatioOf(mk), Is.EqualTo(Refining.RatioOf(ItemCatalog.AddonArmor, mk)), $"Mk {mk}");
        }

        Assert.That(RefinePlan.MaxPerAction, Is.EqualTo(Refining.MaxOutputs(ItemCatalog.CoreRanged)));
    }

    /// <summary>The fee is 5 % of the output, rounded down, and a credit at least: refines split small cannot pay nothing (review, 2026-11-03).</summary>
    [Test]
    public void TheFee_IsACreditAtLeast()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Refining.FeeOf(ItemCatalog.CoreRanged, 1, 1), Is.EqualTo(77));
            Assert.That(Refining.FeeOf(ItemCatalog.Metal, 1, 1), Is.EqualTo(1));
            Assert.That(Refining.FeeOf(ItemCatalog.Metal, 1, 99), Is.EqualTo(13));
        });
    }

    /// <summary>A craft's input is counted by the Mk holding the most, as a slot is filled from one Mk — not summed across Mks (review, 2026-11-03).</summary>
    [Test]
    public void ACraftsDeficit_CountsOneMk_NotTheSumOfAll()
    {
        var book = CraftBook.Parse(CraftEndpoints.SchematicsJson());
        var held = new List<HeldStack>
        {
            new(ItemCatalog.Metal, "mat_metal", MarketCategory.Material, 1, 15),
            new(ItemCatalog.Metal, "mat_metal", MarketCategory.Material, 2, 10),
        };
        var deficits = CraftPlan.Deficits(book, CraftPlan.Blaster, held, []);
        Assert.That(deficits.TryGetValue(ItemCatalog.Metal, out var metal) ? metal : 0, Is.GreaterThan(0), "15 + 10 read as enough for a slot of 20");
    }

    [Test]
    public void ABotRefines_ItsCoresAndAddOnsBelowItsBand_NotWhatItsCraftTakes_NorMaterials()
    {
        var held = new List<HeldStack>
        {
            new(ItemCatalog.CoreRanged, "core_ranged", MarketCategory.Core, 1, 20),
            new(ItemCatalog.CoreRanged, "core_ranged", MarketCategory.Core, 1, 20),
            new(ItemCatalog.CoreMelee, "core_melee", MarketCategory.Core, 1, 2),
            new(ItemCatalog.AddonArmor, "addon_armor", MarketCategory.Addon, 2, 9),
            new(ItemCatalog.CoreArmor, "core_armor", MarketCategory.Core, 3, 30),
            new(ItemCatalog.AddonWeapon, "addon_weapon", MarketCategory.Addon, 1, 6),
            new(ItemCatalog.Metal, "mat_metal", MarketCategory.Material, 1, 99),
        };

        var due = RefinePlan.Due(held, band: 3, keep: [(ItemCatalog.AddonWeapon, 1)]);

        Assert.That(due, Is.EqualTo(new List<(ushort, int, int)>
        {
            (ItemCatalog.CoreRanged, 1, 10),   // 40 held, 13 makeable, one action's 10
            (ItemCatalog.AddonArmor, 2, 2),    // 9 / 4
        }));
    }
}
