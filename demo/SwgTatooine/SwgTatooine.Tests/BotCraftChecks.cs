using System.Collections.Generic;
using NUnit.Framework;
using SwgTatooine.Bots;
using SwgTatooine.Server;

namespace SwgTatooine.Tests;

/// <summary>
/// The crafters' plan (crafting-build.md § 6, C2): what a crafter makes next from what it holds — parts before the item, the Mk each slot takes, its points
/// and add-on — what it keeps from the bazaar, what input it buys and what it lists. Pure functions over the server's own schematics answer.
/// </summary>
[TestFixture]
internal sealed class BotCraftChecks
{
    private static readonly CraftBook Book = CraftBook.Parse(CraftEndpoints.SchematicsJson());

    private static HeldStack Units(ushort template, int mk, int count) => new(template, "x" + template, MarketCategory.Material, mk, count);

    [Test]
    public void TheBook_IsEverySchematic_WithTheLinesAPointMoves()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Book.Count, Is.EqualTo(Schematics.Every.Length));
            Assert.That(Book.Of(ItemCatalog.Knuckler).Rolled, Is.EqualTo(new[] { 0, 1, 3 }), "a knuckler's reach is 1 m whatever the roll");
            Assert.That(Book.Of(ItemCatalog.Blaster).Points, Is.EqualTo(Schematics.ItemPoints));
            Assert.That(Book.Of(ItemCatalog.CoreRanged).Rolled, Is.Empty, "a Mk I core is a stack: no line");
        });
    }

    [Test]
    public void ACrafter_MakesThePartItLacks_BeforeTheItem()
    {
        // Metal and Crystal for a barrel, nothing else: the blaster cannot be made, its barrel can.
        var held = new List<HeldStack> { Units(ItemCatalog.Metal, 2, 12), Units(ItemCatalog.Metal, 4, 12), Units(ItemCatalog.Crystal, 3, 4) };
        var job = CraftPlan.Next(Book, [ItemCatalog.Blaster], 0, held, []);
        Assert.Multiple(() =>
        {
            Assert.That(job?.Schematic, Is.EqualTo(Schematics.BarrelPistol));
            Assert.That(job?.Mks, Is.EqualTo(new byte[] { 4, 3 }), "a quality slot takes the highest Mk that covers it");
            Assert.That(job?.Parts, Is.Empty);
            Assert.That(job?.Points, Is.EqualTo(new byte[] { 0, 0 }), "a part's two points on its one line");
        });
    }

    [Test]
    public void ACrafter_HoldingEveryInput_MakesTheItem_PointsSpread_AddOnIn()
    {
        var held = new List<HeldStack>
        {
            Units(ItemCatalog.CoreRanged, 3, 1), Units(ItemCatalog.Metal, 1, 25), Units(ItemCatalog.Metal, 4, 30), Units(ItemCatalog.AddonWeapon, 2, 1),
        };
        List<HeldPart> parts =
        [
            new(1, Schematics.BarrelPistol, 300), new(2, Schematics.BarrelPistol, 600), new(3, Schematics.PowerCell, 400), new(4, Schematics.Grip, 500),
        ];
        var job = CraftPlan.Next(Book, [ItemCatalog.Blaster], 0, held, parts);
        Assert.Multiple(() =>
        {
            Assert.That(job?.Schematic, Is.EqualTo(ItemCatalog.Blaster));
            Assert.That(job?.Mks, Is.EqualTo(new byte[] { 3, 1 }), "the core's Mk, then the bulk Metal's lowest that covers it");
            Assert.That(job?.Parts, Is.EqualTo(new ulong[] { 2, 3, 4 }), "the best-graded barrel");
            Assert.That(job?.Points, Is.EqualTo(new byte[] { 0, 1, 2, 3, 0, 1 }), "in turn over the four rolled lines");
            Assert.That(job?.AddonMk, Is.EqualTo((byte)2));
        });
    }

    [Test]
    public void ACoreItLacks_IsCraftedFromBulk_AndTwoSlotsOfOneUnitMustBothBeCovered()
    {
        // 25 Metal Mk I: the core takes 10 — but the Crystal is missing, so no core; with it, the core (whose 10 Metal leave 15, short of the blaster's 20).
        var held = new List<HeldStack> { Units(ItemCatalog.Metal, 1, 25) };
        List<HeldPart> parts = [new(1, Schematics.BarrelPistol, 300), new(3, Schematics.PowerCell, 400), new(4, Schematics.Grip, 500)];
        Assert.That(CraftPlan.Next(Book, [ItemCatalog.Blaster], 0, held, parts), Is.Null);

        held.Add(Units(ItemCatalog.Crystal, 1, 5));
        var job = CraftPlan.Next(Book, [ItemCatalog.Blaster], 0, held, parts);
        Assert.That(job?.Schematic, Is.EqualTo(ItemCatalog.CoreRanged));
        Assert.That(job?.Mks, Is.EqualTo(new byte[] { 1, 1 }));
    }

    [Test]
    public void AnArmourPiece_TakesEitherSegment_AndTheTargetsAreTriedInTurn()
    {
        var held = new List<HeldStack> { Units(ItemCatalog.CoreArmor, 2, 1), Units(ItemCatalog.Fiber, 1, 30) };
        List<HeldPart> parts = [new(7, Schematics.CompositeSegment, 500)];
        Assert.Multiple(() =>
        {
            Assert.That(CraftPlan.Next(Book, CraftPlan.Targets(CraftLine.Armor), 4, held, parts)?.Schematic, Is.EqualTo((ushort)48), "gloves: one segment");
            Assert.That(CraftPlan.Next(Book, CraftPlan.Targets(CraftLine.Armor), 5, held, parts)?.Schematic, Is.EqualTo((ushort)49), "boots, next turn");
        });
    }

    [Test]
    public void WhatItLacks_IsTheFirstMaterialDownTheTree()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CraftPlan.Wants(Book, ItemCatalog.Blaster, [], [])?.Template, Is.EqualTo(ItemCatalog.Metal), "the core's Metal first");
            var withCore = new List<HeldStack> { Units(ItemCatalog.CoreRanged, 1, 1), Units(ItemCatalog.Metal, 1, 40) };
            Assert.That(CraftPlan.Wants(Book, ItemCatalog.Blaster, withCore, [])?.Template, Is.EqualTo(ItemCatalog.Crystal), "then the barrel's Crystal");
            Assert.That(CraftPlan.Wants(Book, ItemCatalog.Blaster, withCore, [])?.Item, Is.EqualTo("mat_crystal"), "named as the browse names it");
        });
    }

    [Test]
    public void ACrafter_KeepsTwoStacksOfEachInput_AndTradesTheRest()
    {
        var keeps = CraftPlan.Keeps(Book, CraftPlan.Targets(CraftLine.Weapon));
        var held = new List<HeldStack>
        {
            Units(ItemCatalog.Metal, 1, 10), Units(ItemCatalog.Metal, 2, 50), Units(ItemCatalog.Metal, 3, 50), Units(ItemCatalog.Venom, 1, 40),
        };
        var trade = CraftPlan.Tradeable(held, keeps);
        Assert.Multiple(() =>
        {
            Assert.That(keeps, Does.Contain(ItemCatalog.Metal).And.Contain(ItemCatalog.CoreRanged).And.Contain(ItemCatalog.AddonWeapon));
            Assert.That(keeps, Does.Not.Contain(ItemCatalog.Venom), "a weapon's tree takes no Venom");
            Assert.That(trade, Is.EquivalentTo(new[] { Units(ItemCatalog.Metal, 1, 10), Units(ItemCatalog.Venom, 1, 40) }));
        });
    }

    [Test]
    public void AnInput_IsBoughtAtTheCheapestUnit_WithinOneAndAHalfItsValue()
    {
        var prices = MarketPrices.None.With("mat_metal", 1, 10d).With("mat_metal", 2, 20d);
        var want = new BookSlot("bulk", ItemCatalog.Metal, "mat_metal", 20);
        BrowseRow Row(ulong id, int mk, int qty, long unit) => new(id, 1, "mat_metal", MarketCategory.Material, mk, qty, unit, qty * unit, 99);
        List<BrowseRow> rows = [Row(1, 1, 10, 5), Row(2, 1, 30, 16), Row(3, 2, 20, 25), Row(4, 1, 20, 14), Row(5, 2, 40, 29)];
        Assert.Multiple(() =>
        {
            Assert.That(CraftPlan.ChooseInput(rows, prices, want, 10_000, [])?.Id, Is.EqualTo(4UL), "1 is too few, 2 and 5 past 1.5 × value, 3 dearer");
            Assert.That(CraftPlan.ChooseInput(rows, prices, want, 10_000, [4UL])?.Id, Is.EqualTo(3UL), "never its own");
            Assert.That(CraftPlan.ChooseInput(rows, prices, want, 900, [])?.Id, Is.Null, "past 30 % of the wallet");
        });
    }

    /// <summary>
    /// § 6, Surplus — every trading bot's: an item with stats it will not use is listed (a Mk II blaster it cannot wear at CL 1, a potion), but not a part,
    /// nor the piece it puts on next, nor an upgrade its band lets it wear.
    /// </summary>
    [Test]
    public void WhatItWillNotUse_IsListed_ButNotItsPartsNorWhatItWearsNext()
    {
        var prices = MarketPrices.None.With("blaster_dl44", 2, 1_000d).With("potion_stim", 1, 50d);
        var gear = new List<GearItem>
        {
            new(1, ItemCatalog.Blaster, EquipSlot.Weapon, EquipSlot.Weapon, 0, "blaster_dl44", 1),
            new(2, ItemCatalog.Blaster, 3, EquipSlot.Weapon, 0, "blaster_dl44", 2),
            new(3, Schematics.Grip, 4, 0, 500, "part_grip", 0),
            new(4, 56, 5, 0, 0, "potion_stim", 1),
            new(5, ItemCatalog.PaddedHelmet, 6, EquipSlot.Head, 0, "armor_padded_helmet", 1),
        };
        var rng = 7u;
        var plan = MarketPlan.Surplus(gear, prices, 0, 1, ref rng);
        Assert.Multiple(() =>
        {
            Assert.That(plan.ConvertAll(c => c.Item), Is.EqualTo(new ulong[] { 2, 4 }), "the helmet goes on next; the grip is crafted with");
            Assert.That(plan[0].UnitPrice, Is.InRange(900L, 1_100L));
            Assert.That(plan[0].Quantity, Is.EqualTo(1));
            Assert.That(MarketPlan.Surplus(gear, prices, MarketPlan.MaxListed - 1, 1, ref rng), Has.Count.EqualTo(1), "up to the bot's listing cap");
            Assert.That(MarketPlan.Surplus(gear, prices, 0, 2, ref rng).ConvertAll(c => c.Item), Is.EqualTo(new ulong[] { 4 }), "at band II, an upgrade");
        });
    }

    [Test]
    public void ACraft_IsSentAsThePanelSendsIt()
    {
        var commands = BotSwarm.Encode(new CraftJob(ItemCatalog.Blaster, [3, 1], [11, 12, 13], [0, 1], 2));
        Assert.Multiple(() =>
        {
            Assert.That(
                commands.ConvertAll(c => c.Name),
                Is.EqualTo(new[] { "CraftStart", "CraftExperiment", "CraftExperiment", "CraftEnhance", "CraftFinish" }));
            Assert.That(commands[0].Values["mk0"].Numbers[0], Is.EqualTo(3d));
            Assert.That(commands[0].Values["part2"].Integers[0], Is.EqualTo(13UL));
            Assert.That(commands[0].Values["part3"].Integers[0], Is.Zero);
            Assert.That(commands[^1].Values["schematic"].Numbers[0], Is.EqualTo((double)ItemCatalog.Blaster));
        });
    }
}
