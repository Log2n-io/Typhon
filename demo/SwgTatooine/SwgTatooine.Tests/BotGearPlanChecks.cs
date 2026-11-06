using System.Linq;
using System.Collections.Generic;
using NUnit.Framework;
using SwgTatooine.Bots;
using SwgTatooine.Bots.Brain;
using SwgTatooine.Server;

namespace SwgTatooine.Tests;

/// <summary>
/// A bot's look at its gear (<see cref="GearPlan"/>, Loïc 2026-11-04): down to its fist when its weapon broke, the spare it carries and keeps, a spare worth
/// buying, the trip the assessment raises — and, in the brain, a fist that closes in to strike and runs from what bites.
/// </summary>
[TestFixture]
internal sealed class BotGearPlanChecks
{
    private const ushort Blaster = ItemCatalog.Blaster;

    private static readonly List<StandardOffer> Offers = MarketJson.Standard(MarketEndpoints.StandardJson());

    private static GearItem Blast(ulong id, int slot, int mk = 1, int condition = 1000, bool broken = false, int repairs = 0)
        => new(id, Blaster, slot, EquipSlot.Weapon, 0, "blaster_dl44", mk) { Condition = condition, MaxCondition = 1000, Broken = broken, Repairs = repairs };

    private static HeldStack WeaponKits(int n) => new(MarketPlan.KitWeapon, "kit_weapon_repair", MarketCategory.Other, 0, n);

    private static GearItem StimOf(ulong id, int doses = 3, int count = 1)
        => new(id, GearPlan.Stim, 10 + (int)id, 0, 0, GearPlan.StimItem, 0) { Condition = doses, MaxCondition = 3, Count = count };

    /// <summary>
    /// A stim is drunk below half health while something is on it — the one with the fewest doses, a crate last — and never out of a fight, nor
    /// above half (Loïc, 2026-11-05: the bots drink too).
    /// </summary>
    [Test]
    public void AStim_IsDrunkHurtAndAttacked_TheEmptiestFirst_ACrateLast()
    {
        var full = StimOf(1);
        var half = StimOf(2, doses: 1);
        var crate = StimOf(3, count: 4);
        Assert.Multiple(() =>
        {
            Assert.That(GearPlan.StimToDrink([full, half, crate], 0.3f, attacked: true), Is.EqualTo(2UL), "the emptiest");
            Assert.That(GearPlan.StimToDrink([crate, full], 0.3f, attacked: true), Is.EqualTo(1UL), "a loose one before a crate");
            Assert.That(GearPlan.StimToDrink([crate], 0.3f, attacked: true), Is.EqualTo(3UL), "the crate when it is all");
            Assert.That(GearPlan.StimToDrink([full], 0.3f, attacked: false), Is.Zero, "out of a fight it rests");
            Assert.That(GearPlan.StimToDrink([full], 0.6f, attacked: true), Is.Zero, "above half");
            Assert.That(GearPlan.StimToDrink([full], 0f, attacked: true), Is.Zero, "down");
            Assert.That(GearPlan.StimToDrink([Blast(4, 3)], 0.3f, attacked: true), Is.Zero, "no stim");
            Assert.That(GearPlan.StimsHeld([full, half, crate]), Is.EqualTo(6));
        });
    }

    /// <summary>
    /// Its stims kept: three, the part-drunk ones always; a fourth full one listed. A crafter keeps one and sells the rest. Short of two, a stim is bought —
    /// the board's cheapest full one under the vendor's price.
    /// </summary>
    [Test]
    public void Stims_AreKept_ThreeOfThem_TheRestSold_AndBoughtWhenShort()
    {
        var prices = new MarketPrices();
        var rng = 7u;
        // The part-drunk one first in the grid: it is kept, and spends none of the keeping (review, 2026-11-05).
        var five = new List<GearItem> { StimOf(3, doses: 2), StimOf(1), StimOf(2), StimOf(4), StimOf(5) };
        var listed = MarketPlan.Surplus(five, prices, 0, 1, ref rng);
        var crafter = MarketPlan.Surplus(five, prices, 0, 1, ref rng, stimsKept: GearPlan.CrafterStimsKept);
        var crated = new List<GearItem> { StimOf(1), StimOf(6, count: 4) };
        var vendor = GearPlan.PriceOf(Offers, GearPlan.StimItem);
        BrowseRow Row(ulong id, long unit, int condition = 3)
            => new(id, 1, GearPlan.StimItem, MarketCategory.Other, 0, 1, unit, unit, 99) { Condition = condition, MaxCondition = 3 };
        Assert.Multiple(() =>
        {
            Assert.That(listed.Count(c => c.Step == MarketStep.List), Is.EqualTo(1), "a bot keeps three full ones");
            Assert.That(listed.Any(c => c.Item == 3) || crafter.Any(c => c.Item == 3), Is.False, "a part-drunk stim was listed");
            Assert.That(crafter.Count(c => c.Step == MarketStep.List), Is.EqualTo(3), "a crafter keeps one");
            Assert.That(CraftPlan.ForSale(five, [], 0, 0), Is.EqualTo(crafter.Count(c => c.Step == MarketStep.List)), "ForSale and Surplus disagree");
            Assert.That(CraftPlan.ForSale(crated, [], 0, 0), Is.EqualTo(1), "a crate is one listing");
            Assert.That(MarketPlan.Surplus(crated, prices, 0, 1, ref rng, stimsKept: GearPlan.CrafterStimsKept).Count(c => c.Step == MarketStep.List),
                Is.EqualTo(1), "the crate past the one kept is listed whole");
            Assert.That(vendor, Is.EqualTo(Equipment.StandardPrice(ItemCatalog.Stim)), "the vendor sells one");
            Assert.That(GearPlan.StimWanted([StimOf(1)], 1_000_000, vendor), Is.True, "one held");
            Assert.That(GearPlan.StimWanted([StimOf(1), StimOf(2)], 1_000_000, vendor), Is.False, "two held");
            Assert.That(GearPlan.StimWanted([StimOf(1, doses: 1), StimOf(2, doses: 1)], 1_000_000, vendor), Is.True, "two of one dose each");
            Assert.That(GearPlan.StimWanted([], vendor, vendor), Is.False, "its reserve");
            Assert.That(GearPlan.ChooseStim([Row(1, vendor - 10), Row(2, vendor - 50), Row(3, vendor - 90, condition: 2), Row(4, vendor + 5)], vendor,
                1_000_000, []), Is.EqualTo(Row(2, vendor - 50)), "the cheapest full one under the vendor's");
            Assert.That(GearPlan.ChooseStim([Row(7, vendor - 90) with { Quantity = 4 }], vendor, 1_000_000, []), Is.Null, "a crate bought");
            Assert.That(CraftPlan.Lines, Does.Contain(CraftLine.Potion), "nobody crafts stims");
            Assert.That(CraftPlan.Targets(CraftLine.Potion), Is.EqualTo(new[] { GearPlan.Stim }));
            Assert.That(MarketCatalog.ModelValue(GearPlan.StimItem, 0), Is.EqualTo(MarketValues.StimMilli / 1000d), "the bot's copy of the stim's value");
        });
    }

    /// <summary>A sound weapon worn is a weapon; a broken one, none at all, or another kind than the bot fights with, is its fist.</summary>
    [Test]
    public void ABrokenWeapon_IsTheFist()
    {
        var sword = new GearItem(3, ItemCatalog.Sword, EquipSlot.Weapon, EquipSlot.Weapon, 0, "sword_vibroblade", 1) { Condition = 1000, MaxCondition = 1000 };
        Assert.Multiple(() =>
        {
            Assert.That(GearPlan.Unarmed([Blast(1, 101)], Blaster), Is.False);
            Assert.That(GearPlan.Unarmed([Blast(1, 101, condition: 0, broken: true)], Blaster), Is.True, "broken");
            Assert.That(GearPlan.Unarmed([Blast(1, 101, condition: 50)], Blaster), Is.False, "worn down but working");
            Assert.That(GearPlan.Unarmed([], Blaster), Is.True, "nothing worn");
            Assert.That(GearPlan.Unarmed([Blast(1, 4)], Blaster), Is.True, "carried, not worn");
            Assert.That(GearPlan.Unarmed([sword], Blaster), Is.True, "not the weapon it fights with");
        });
    }

    /// <summary>The spare: in the grid, its weapon, the best sound one; a worn-down one only when it holds a kit to repair it; never a crate.</summary>
    [Test]
    public void TheSpare_IsTheBestSoundOne_OrOneItCanRepair()
    {
        var worn = Blast(1, 101);
        var mk1 = Blast(2, 3, 1, 900);
        var mk2 = Blast(3, 4, 2, 400);
        var down = Blast(4, 5, 1, 0, broken: true);
        var crate = Blast(5, 6) with { Count = 3 };
        Assert.Multiple(() =>
        {
            Assert.That(GearPlan.Spare([worn, mk1, mk2, down, crate], Blaster), Is.EqualTo(3UL), "the highest Mk");
            Assert.That(GearPlan.Spare([worn, down, crate], Blaster), Is.Zero, "a broken one, no kit; a crate");
            Assert.That(GearPlan.Spare([worn, down], Blaster, [WeaponKits(1)]), Is.EqualTo(4UL), "a broken one it can repair");
            Assert.That(GearPlan.SoundSpare([worn, down], Blaster, [WeaponKits(1)]), Is.False, "but not one to put on now");
            Assert.That(GearPlan.Spare([worn, down with { Repairs = MarketPlan.MaxRepairs }], Blaster, [WeaponKits(1)]), Is.Zero, "past its repairs");
        });
    }

    /// <summary>
    /// Urgent is down to its fist with nothing to put on and nothing to repair; a spare worth buying is armed, carrying none, and affordable over
    /// <see cref="GearPlan.SpareReserve"/>.
    /// </summary>
    [Test]
    public void AWeaponIsUrgent_ASpareIsBoughtWhenAffordable()
    {
        var broken = Blast(1, 101, condition: 0, broken: true);
        var spare = Blast(2, 3);
        const long price = 1_812;
        Assert.Multiple(() =>
        {
            Assert.That(GearPlan.Urgent([broken], Blaster), Is.True);
            Assert.That(GearPlan.Urgent([broken, spare], Blaster), Is.False, "a spare to put on");
            Assert.That(GearPlan.Urgent([broken], Blaster, [WeaponKits(1)]), Is.False, "a kit to repair it: the station, not the bazaar");
            Assert.That(GearPlan.Urgent([Blast(1, 101)], Blaster), Is.False);

            Assert.That(GearPlan.SpareWanted([Blast(1, 101)], Blaster, [], price + GearPlan.SpareReserve, price), Is.True);
            Assert.That(GearPlan.SpareWanted([Blast(1, 101)], Blaster, [], price + GearPlan.SpareReserve - 1, price), Is.False, "into its reserve");
            Assert.That(GearPlan.SpareWanted([Blast(1, 101), spare], Blaster, [], 1_000_000, price), Is.False, "it carries one");
            Assert.That(GearPlan.SpareWanted([broken], Blaster, [], 1_000_000, price), Is.False, "unarmed: a weapon first, not a spare");
        });
    }

    /// <summary>
    /// The assessment raises a trip for no weapon whatever the wallet, for a piece to replace or better only when it can pay for it, and for a spare it can
    /// afford; a bot of its band with a spare and nothing worn down stays on its day.
    /// </summary>
    [Test]
    public void TheAssessment_RaisesATrip_ForWhatItCanPayFor()
    {
        var armour = new List<GearItem> { Blast(1, 101), Blast(2, 3) };
        for (var slot = EquipSlot.Head; slot <= EquipSlot.Feet; slot++)
        {
            armour.Add(new GearItem((ulong)(10 + slot), ItemCatalog.PaddedHelmet, slot, slot, 0, "armor_padded_helmet", 1)
            {
                Condition = 1000,
                MaxCondition = 1000,
            });
        }

        // Two stims held: short of them is a trip of its own (Stims_AreKept_…).
        armour.Add(StimOf(40));
        armour.Add(StimOf(41));
        var helmetDown = new List<GearItem>(armour);
        helmetDown[2] = helmetDown[2] with { Condition = 100 };
        Assert.Multiple(() =>
        {
            Assert.That(GearPlan.Due([Blast(1, 101, condition: 0, broken: true)], [], Offers, 1, 1_000_000, Blaster), Is.True, "no weapon: due");
            Assert.That(GearPlan.Due([Blast(1, 101, condition: 0, broken: true)], [], Offers, 1, 0, Blaster), Is.False, "but not with nothing to pay");
            Assert.That(GearPlan.Due(armour, [], Offers, 1, 1_000_000, Blaster), Is.False, "its band, a spare, nothing worn down");
            var helmets = new MarketPrices().WithBoard("armor_padded_helmet", 1, 2, 700);
            Assert.That(GearPlan.Due(helmetDown, [], Offers, 1, 1_000_000, Blaster, helmets), Is.True, "a helmet under 20 %, no kit, one listed: replaced");
            Assert.That(GearPlan.Due(helmetDown, [], Offers, 1, 1_000_000, Blaster, new MarketPrices()), Is.False,
                "none listed, and the vendor replaces only a broken one: no trip");
            Assert.That(GearPlan.Due(helmetDown, [], Offers, 1, MarketPlan.StandardReserve, Blaster, helmets), Is.False, "and only when it can pay for one");
            Assert.That(GearPlan.Due(armour.GetRange(0, 1), [], Offers, 1, 1_000_000, Blaster), Is.True, "no armour, and no spare: due");
            Assert.That(GearPlan.Due(armour.FindAll(g => g.Template != GearPlan.Stim), [], Offers, 1, 1_000_000, Blaster), Is.True, "no stim: due");
        });
    }

    /// <summary>
    /// At the bazaar the spare is kept, never listed; what a replacement took off goes to the vendor at its condition, or — broken, worth nothing to it —
    /// is thrown away rather than kept in a slot; a worn-down spare it can repair is repaired with the worn pieces.
    /// </summary>
    [Test]
    public void TheSpare_IsKept_TheBrokenThrownAway_TheRepairableRepaired()
    {
        var prices = MarketPrices.None.With("blaster_dl44", 1, 1_000d);
        var worn = Blast(1, 101);
        var spare = Blast(2, 3, 1, 900);
        var extra = Blast(3, 4, 1, 800);
        var broken = Blast(4, 5, 1, 0, broken: true);
        var low = Blast(5, 6, 1, 100);
        var rng = 1u;
        var plan = MarketPlan.Surplus([worn, spare, extra, broken, low], prices, 0, 1, ref rng, Blaster, false, []);
        Assert.Multiple(() =>
        {
            Assert.That(plan.Exists(c => c.Item == 2UL), Is.False, "the spare went to the board or the vendor");
            Assert.That(plan.Find(c => c.Item == 3UL).Step, Is.EqualTo(MarketStep.List), "a second spare is surplus");
            Assert.That(plan.Find(c => c.Item == 4UL).Step, Is.EqualTo(MarketStep.Discard), "a broken one, thrown away");
            Assert.That(plan.Find(c => c.Item == 5UL).Step, Is.EqualTo(MarketStep.Vendor), "one at 10 %, sold at its condition");
            Assert.That(BotSwarm.Encode(plan.Find(c => c.Item == 4UL)).Command, Is.EqualTo(nameof(DiscardItem)), "sent as the inventory window's discard");

            var downSpare = Blast(2, 3, 1, 0, broken: true);
            Assert.That(MarketPlan.RepairsDue([worn, downSpare], [WeaponKits(1)], Blaster), Is.EqualTo(new ulong[] { 2 }), "the spare, repaired");
            Assert.That(MarketPlan.RepairsDue([worn, downSpare], [WeaponKits(1)]), Is.Empty, "the premise: without its weapon, only what it wears");
        });
    }

    /// <summary>A bot whose weapon broke puts its spare on at once — kit or no kit, a broken weapon does nothing; a worn piece under 20 % is replaced.
    /// </summary>
    [Test]
    public void ABrokenWeapon_GivesWayToTheSpare_InTheFight()
    {
        var broken = Blast(1, 101, condition: 0, broken: true);
        var spare = Blast(2, 3);
        Assert.Multiple(() =>
        {
            Assert.That(MarketPlan.NextToWear([broken, spare], 1, Blaster), Is.EqualTo(2UL), "without the held stacks");
            Assert.That(MarketPlan.NextToWear([broken, spare], 1, Blaster, [WeaponKits(1)]), Is.EqualTo(2UL), "a kit held");
            Assert.That(MarketPlan.NextToWear([Blast(1, 101, condition: 100), spare], 1, Blaster, []), Is.EqualTo(2UL), "under 20 %, no kit");
            Assert.That(MarketPlan.NextToWear([Blast(1, 101, condition: 300), spare], 1, Blaster, []), Is.Zero, "above the threshold: worn on");
        });
    }

    /// <summary>
    /// Review, 2026-11-04: a spare above its band is no spare (it cannot put it on — it fought with its fist for good); a spare is never packed into a crate;
    /// a broken armour piece with a kit held is not swapped out to be thrown away; an upgrade nobody lists is no trip.
    /// </summary>
    [Test]
    public void TheSpare_IsWithinItsBand_NeverCrated_AndArmourWaitsForItsKit()
    {
        var broken = Blast(1, 101, condition: 0, broken: true);
        var mk3 = Blast(2, 3, 3);
        var twinA = Blast(3, 4) with { PackKey = "dl44-pristine" };
        var twinB = Blast(4, 5) with { PackKey = "dl44-pristine" };
        var helmet = new GearItem(5, ItemCatalog.PaddedHelmet, 102, 102, 0, "armor_padded_helmet", 1) { Condition = 0, MaxCondition = 1000, Broken = true };
        var helmetSpare = new GearItem(6, ItemCatalog.PaddedHelmet, 6, 102, 0, "armor_padded_helmet", 1) { Condition = 1000, MaxCondition = 1000 };
        var armourKit = new HeldStack(MarketPlan.KitArmor, "kit_armor_repair", MarketCategory.Other, 0, 1);
        var mk1 = new GearItem(7, ItemCatalog.PaddedHelmet, 102, 102, 0, "armor_padded_helmet", 1) { Condition = 1000, MaxCondition = 1000 };
        var mk1Set = new List<GearItem> { Blast(1, 101), Blast(2, 3), mk1, StimOf(40), StimOf(41) };
        for (var slot = EquipSlot.Torso; slot <= EquipSlot.Feet; slot++)
        {
            // The rest of the set, Mk I: an empty slot is a trip of its own, to the vendor (Loïc, 2026-11-05).
            mk1Set.Add(new GearItem(20UL + slot, ItemCatalog.PaddedHelmet, slot, slot, 0, "armor_padded_x" + slot, 1)
            {
                Condition = 1000, MaxCondition = 1000,
            });
        }

        Assert.Multiple(() =>
        {
            Assert.That(GearPlan.Spare([broken, mk3], Blaster, [], band: 1), Is.Zero, "a Mk III spare at band 1");
            Assert.That(GearPlan.Urgent([broken, mk3], Blaster, [], band: 1), Is.True, "so a weapon is urgent");
            Assert.That(GearPlan.Spare([broken, mk3], Blaster, [], band: 3), Is.EqualTo(2UL));

            var spare = GearPlan.Spare([Blast(9, 101), twinA, twinB], Blaster, []);
            Assert.That(CratePlan.Packs([Blast(9, 101), twinA, twinB], 0, spare), Is.Empty, "the spare packed with its twin");

            Assert.That(MarketPlan.NextToWear([helmet, helmetSpare], 1, 0, [armourKit]), Is.Zero, "a broken helmet swapped out with its kit held");
            Assert.That(MarketPlan.NextToWear([helmet, helmetSpare], 1, 0, []), Is.EqualTo(6UL), "no kit: replaced");

            Assert.That(GearPlan.Due(mk1Set, [], Offers, 3, 1_000_000, Blaster, new MarketPrices()), Is.False, "below its band, nothing listed: no trip");
            var listed = new MarketPrices().With("blaster_dl44", 2, 1_795d).WithBoard("blaster_dl44", 2, 1, 2_000);
            Assert.That(GearPlan.Due(mk1Set, [], Offers, 3, 1_000_000, Blaster, listed), Is.True, "a Mk II listed: a trip");
        });
    }

    /// <summary>A fist closes in to strike and stands there: no kite at 12 m, which strikes nothing; a lair too.</summary>
    [Test]
    public void AFist_ClosesIn_AndNeverKites()
    {
        var run = Routing.SpeedClassOf(TravelMode.Run);
        var far = BotBrain.Engage(0f, 0f, 7u, 9f, 12f, 15f, mobile: true, melee: true);
        var near = BotBrain.Engage(0f, 0f, 7u, 0.6f, 0.8f, 1f, mobile: true, melee: true);
        var lair = BotBrain.Engage(0f, 0f, 8u, 9f, 12f, 15f, mobile: false, melee: true);
        Assert.Multiple(() =>
        {
            Assert.That((far.NetId, far.SpeedClass), Is.EqualTo((7u, run)), "a creature at 15 m, kite range for a gun: run at");
            Assert.That(far.Heading, Is.EqualTo(System.MathF.Atan2(12f, 9f)).Within(0.001f), "towards it");
            Assert.That((near.NetId, near.SpeedClass), Is.EqualTo((7u, (byte)0)), "within reach: stood and struck");
            Assert.That(lair.SpeedClass, Is.EqualTo(run), "a lair at 15 m: closed on");
        });
    }

    /// <summary>Down to its fist, a bot runs from what attacks it, at any level — where armed, it would fight one of its own level nine times in ten.</summary>
    [Test]
    public void AFist_RunsFromAnAttacker()
    {
        var runs = 0;
        var fights = 0;
        for (var seed = 1u; seed <= 40u; seed++)
        {
            var bitten = new Perceived
            {
                HasBody = true,
                NearestCreature = 42u,
                NearestCreatureX = 8f,
                NearestCreatureZ = 6f,
                NearestCreatureM = 10f,
                NearestCreatureLevel = 1,
                Attacker = 42u,
                AttackerX = 8f,
                AttackerZ = 6f,
                AttackerM = 10f,
                AttackerLevel = 1,
                Health = 1f,
            };
            var armed = new BotBrain(BotMap.Cities, seed).Decide(in bitten, 1);
            var fist = new BotBrain(BotMap.Cities, seed).Decide(bitten with { Unarmed = true }, 1);
            fights += armed.Kind == IntentKind.Attack && armed.NetId == 42u ? 1 : 0;
            runs += fist.Kind == IntentKind.Attack && fist.NetId == 0u && fist.SpeedClass == Routing.SpeedClassOf(TravelMode.Run) ? 1 : 0;
        }

        Assert.Multiple(() =>
        {
            Assert.That(runs, Is.EqualTo(40), "a fist stood to fight");
            Assert.That(fights, Is.GreaterThan(30), "the premise: armed, it fights its own level");
        });
    }
}
