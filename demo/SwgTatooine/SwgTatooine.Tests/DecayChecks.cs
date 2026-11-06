using System;
using System.Linq;
using NUnit.Framework;
using SwgTatooine.Server;
using SwgTatooine.Server.Accounts;
using Typhon.Engine;
using Typhon.Protocol;

namespace SwgTatooine.Tests;

/// <summary>
/// Decay and repair (crafting-build.md C3, crafting-catalog.md § 9) through the real Workshop: the wear counted on a body written into what it wears; an
/// item broken at 0 with a repair left — the fist instead of the weapon — and destroyed with none, a <c>decay</c> row; falls; the wear a body takes with it
/// at logout; repair at a station with a kit and a fee, and its refusals; salvage. The item and credit audits clean throughout.
/// </summary>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
internal sealed class DecayChecks : ServedChecks
{
    private const ushort Blaster = ItemCatalog.Blaster;
    private const ushort Gloves = ItemCatalog.PaddedHelmet + 4;

    private TatooineSim Start()
    {
        var config = Worlds.Served(Dir);
        config.CommandRateScale = Worlds.FastCommands;
        config.WorkerCount = 1;
        config.WelcomePack = true;
        var sim = new TatooineSim(config);
        sim.Initialize();
        Harness = new SessionHarness(sim);
        return sim;
    }

    private sealed record Body(FakeLink Link, EntityId Id, EntityId Character)
    {
        public long Key => unchecked((long)Character.RawValue);
    }

    /// <summary>A character with its welcome pack — a worn Mk I blaster, a weapon repair kit and three stims — standing 2 m from a crafting station.</summary>
    private Body LogIn(TatooineSim sim, ulong account)
    {
        var link = Harness.Connect(TatooineReplication.PlayerKind, TestAccounts.For(sim.Dbe, account));
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= 1, "the avatar to be spawned");
        Harness.Ticks(2);
        var station = sim.Index.Stations[0];
        var body = Worlds.Park(sim, link.Session, station.X + 2f, station.Z);
        // Waited for, not asserted: on CI's runner the character was not found yet, two ticks after the possession, in five fixtures (PR #1590).
        var characterAccount = TestAccounts.For(sim.Dbe, account);
        var character = EntityId.Null;
        SessionHarness.Until(() => CharacterStore.TryFind(sim.Dbe, characterAccount, out character, out _), "the account's character");
        SessionHarness.Until(() => Items.Stacks(sim, character).Count == 2 + SimBridge.WelcomeStims, "the welcome pack");
        SessionHarness.Until(() => GearOf(sim, body).Stance == Stances.Pistol, "the pack's blaster worn");
        Harness.Ticks(2);
        return new Body(link, body, character);
    }

    private static PlayerGear GearOf(TatooineSim sim, EntityId body)
    {
        var gear = default(PlayerGear);
        sim.WriteInTick(tx => gear = tx.Open(body).Read(Server.Player.Gear));
        return gear;
    }

    /// <summary>The body's counters set by the Workshop, the gear's writer; written into the items at once when <paramref name="due"/>.</summary>
    private void SetWear(TatooineSim sim, EntityId body, int weaponUses, int armour, int uses, bool due = true)
    {
        var before = GearOf(sim, body);
        sim.Bridge.StageWear(body, weaponUses, armour, uses, due);
        if (!due)
        {
            SessionHarness.Until(() => GearOf(sim, body).Uses == uses && !before.Equals(GearOf(sim, body)) || uses == before.Uses, "the counters staged");
        }
    }

    private static (EntityId Id, ItemStack Stack) Of(TatooineSim sim, Body b, ushort template)
        => Items.Stacks(sim, b.Character).Where(s => s.Stack.Template == template).Select(s => (s.Id, s.Stack)).FirstOrDefault();

    private static ItemInstance StatsOf(TatooineSim sim, EntityId item)
    {
        using var tx = sim.Dbe.CreateQuickTransaction();
        return tx.Open(item).Read(ItemInstanceArch.Stats);
    }

    private static int Count(FakeLink link, byte reason) => Ledgers.Refusals(link).Count(r => r == reason);

    private void Breathe() => Harness.Ticks(Worlds.FastCommandGapTicks);

    private static void AuditsOk(TatooineSim sim, string when)
    {
        var (ok, why) = Market.Audits(sim);
        Assert.That(ok, Is.True, $"{when}: {why}");
    }

    private EntityId StageWorn(TatooineSim sim, Body b, ushort template, byte mk, in ItemInstance stats)
    {
        var before = Items.Stacks(sim, b.Character).Select(s => s.Id).ToHashSet();
        sim.Bridge.StageInstance(b.Key, template, mk, in stats);
        EntityId id = default;
        SessionHarness.Until(
            () =>
            {
                id = Items.Stacks(sim, b.Character).FirstOrDefault(s => s.Stack.Template == template && !before.Contains(s.Id)).Id;
                return !id.IsNull;
            },
            "the staged item");
        Harness.Ticks(1);
        b.Link.SendCommand(nameof(Server.Equip), new RecordValues { ["item"] = FieldValue.OfUInt64(id.RawValue) });
        SessionHarness.Until(() => Items.Stacks(sim, b.Character).Single(s => s.Id == id).Stack.Slot > 100, "the staged item worn");
        Breathe();
        return id;
    }

    private static ItemInstance Standard(ushort template, int condition, byte repairs = 0, long crafter = 0)
    {
        Equipment.TryStandard(template, out var issue);
        var s = SimBridge.StandardStats(in issue);
        s.Condition = condition;
        s.Repairs = repairs;
        s.Crafter = crafter;
        s.Serial = crafter == 0 ? 0 : 1;
        return s;
    }

    private static void Repair(FakeLink link, EntityId item)
        => link.SendCommand(nameof(RepairItem), new RecordValues { ["item"] = FieldValue.OfUInt64(item.RawValue) });

    // ── Wear ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// § 9.1: fifty uses of the blaster cost fifty of its delays; the armour's damage is shared among the pieces worn by their shares — the gloves' 60 ‰.
    /// Written when the uses reach the flush, then the body's counters are zero.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void TheWearCountedOnTheBody_IsWrittenIntoWhatItWears()
    {
        using var sim = Start();
        var b = LogIn(sim, 0xC301);
        Equipment.TryStandard(Gloves, out var glovesIssue);
        var gloves = StageWorn(sim, b, Gloves, 1, Standard(Gloves, glovesIssue.MaxCondition));
        var blaster = Of(sim, b, Blaster).Id;
        var before = sim.Bridge.WearWrites;

        SetWear(sim, b.Id, SimBridge.WearFlushUses, 2_000, SimBridge.WearFlushUses);
        SessionHarness.Until(() => sim.Bridge.WearWrites > before, "the wear written");
        Harness.Ticks(2);

        Equipment.TryStandard(Blaster, out var blasterIssue);
        Assert.Multiple(() =>
        {
            Assert.That(StatsOf(sim, blaster).Condition, Is.EqualTo(blasterIssue.MaxCondition - SimBridge.WearFlushUses * blasterIssue.Stat1));
            var glovesShare = EquipSlot.SharePerMille[EquipSlot.Hands - EquipSlot.First];
            Assert.That(StatsOf(sim, gloves).Condition, Is.EqualTo(glovesIssue.MaxCondition - (2_000 * glovesShare / 1000)));
            Assert.That((GearOf(sim, b.Id).WeaponUses, GearOf(sim, b.Id).ArmorWear, GearOf(sim, b.Id).Uses), Is.EqualTo(((byte)0, (ushort)0, (byte)0)));
            Assert.That(StatsOf(sim, blaster).Flags & ItemFlags.Pristine, Is.Zero, "a used item is no longer pristine");
        });
        AuditsOk(sim, "after the wear");
    }

    /// <summary>
    /// § 9.2: at 0 with a repair left an item is broken — still owned and worn, but the body fights with its fist; with none left it is destroyed, one
    /// <c>decay</c> row to World, and the slot is empty.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void AnItemAtZero_BreaksWithARepairLeft_AndIsDestroyedWithNone()
    {
        using var sim = Start();
        var b = LogIn(sim, 0xC302);
        var tired = StageWorn(sim, b, Blaster, 1, Standard(Blaster, 500));
        SetWear(sim, b.Id, 1, 0, 1);
        SessionHarness.Until(() => sim.Bridge.ItemsBroken == 1, "the blaster broken");
        SessionHarness.Until(() => GearOf(sim, b.Id).Stance == Stances.Unarmed, "the fist");
        Assert.Multiple(() =>
        {
            Assert.That(StatsOf(sim, tired).Flags & ItemFlags.Broken, Is.EqualTo(ItemFlags.Broken));
            Assert.That(StatsOf(sim, tired).Condition, Is.Zero);
            Assert.That(Items.Stacks(sim, b.Character).Single(s => s.Id == tired).Stack.Slot, Is.EqualTo(EquipSlot.Weapon), "a broken item stays worn");
        });

        var spent = StageWorn(sim, b, Blaster, 1, Standard(Blaster, 500, repairs: SimBridge.MaxRepairs));
        SessionHarness.Until(() => GearOf(sim, b.Id).Stance == Stances.Pistol, "the spent blaster worn");
        SetWear(sim, b.Id, 1, 0, 1);
        SessionHarness.Until(() => sim.Bridge.ItemsDecayed == 1, "the spent blaster destroyed");
        Harness.Ticks(2);
        var decay = Items.Rows(sim).Single(r => r.Kind == ItemMoveKind.Decay);
        Assert.Multiple(() =>
        {
            Assert.That(Items.Stacks(sim, b.Character).Any(s => s.Id == spent), Is.False);
            Assert.That((decay.From, decay.To, decay.Template, decay.Count), Is.EqualTo((b.Key, ItemLedger.World, Blaster, 1)));
            Assert.That(sim.Bridge.ItemsDecayedOfBand(1), Is.EqualTo(1));
            Assert.That(GearOf(sim, b.Id).Stance, Is.EqualTo(Stances.Unarmed), "the slot is empty: the fist");
        });
        AuditsOk(sim, "after the decay");
    }

    /// <summary>§ 9.1 "Falling": killed, every item worn loses 10 % of its max; knocked out, 3 %.</summary>
    [Test]
    [CancelAfter(120_000)]
    public void AFall_CostsEveryWornItemAShareOfItsMax()
    {
        using var sim = Start();
        var b = LogIn(sim, 0xC303);
        var blaster = Of(sim, b, Blaster).Id;
        var max = StatsOf(sim, blaster).MaxCondition;
        var before = sim.Bridge.WearWrites;
        sim.Bridge.FallDue(b.Id, dead: true);
        SessionHarness.Until(() => sim.Bridge.WearWrites > before, "the death");
        sim.Bridge.FallDue(b.Id, dead: false);
        SessionHarness.Until(() => sim.Bridge.WearWrites > before + 1, "the knockout");
        Harness.Ticks(1);
        var lost = (max * SimBridge.DeathWearPerMille / 1000) + (max * SimBridge.KnockoutWearPerMille / 1000);
        Assert.That(StatsOf(sim, blaster).Condition, Is.EqualTo(max - lost));
        AuditsOk(sim, "after the falls");
    }

    /// <summary>§ 9.1 "at logout": the wear a body counted goes with it into its items when it logs out — nothing waits on a body that is gone.</summary>
    [Test]
    [CancelAfter(120_000)]
    public void TheWearABodyCounted_IsWrittenWhenItLogsOut()
    {
        using var sim = Start();
        var b = LogIn(sim, 0xC304);
        var blaster = Of(sim, b, Blaster).Id;
        var max = StatsOf(sim, blaster).MaxCondition;
        SetWear(sim, b.Id, 7, 0, 7, due: false);
        var before = sim.Bridge.WearWrites;
        b.Link.Close(CloseCodes.Normal, "logging out");
        SessionHarness.Until(() => sim.Bridge.WearWrites > before, "the logout's wear");
        Harness.Ticks(1);
        Assert.That(StatsOf(sim, blaster).Condition, Is.EqualTo(max - 7 * StatsOf(sim, blaster).Stat1));
        AuditsOk(sim, "after the logout");
    }

    // ── Repair ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// § 9.2: a broken blaster repaired at a station with the pack's kit: back to its max × 0.75, one repair used, the kit a <c>repair</c> row, the fee 10 %
    /// of its value a <c>repair</c> credit row — and the body fights with it again. Then the refusals: no kit, two repairs used, nothing of its own, no
    /// station.
    /// </summary>
    [Test]
    [CancelAfter(180_000)]
    public void ARepair_RestoresALowerMax_WithAKitAndAFee_AndHasItsRefusals()
    {
        using var sim = Start();
        var b = LogIn(sim, 0xC305);
        var blaster = StageWorn(sim, b, Blaster, 1, Standard(Blaster, 500));
        SetWear(sim, b.Id, 1, 0, 1);
        Equipment.TryStandard(Blaster, out var issue);
        SessionHarness.Until(() => GearOf(sim, b.Id).Stance == Stances.Unarmed, "the blaster broken");
        Assert.That(StatsOf(sim, blaster).Flags & ItemFlags.Broken, Is.EqualTo(ItemFlags.Broken), "the premise: broken");

        var balance = Ledgers.Balance(sim, b.Character);
        Repair(b.Link, blaster);
        SessionHarness.Until(() => sim.Bridge.Repairs == 1, "the repair");
        SessionHarness.Until(() => GearOf(sim, b.Id).Stance == Stances.Pistol, "the blaster back");
        var repaired = StatsOf(sim, blaster);
        var fee = SimBridge.RepairFee(Blaster, 1);
        Assert.Multiple(() =>
        {
            Assert.That(repaired.MaxCondition, Is.EqualTo((int)((long)issue.MaxCondition * SimBridge.RepairMaxPerMille / 1000)));
            Assert.That(repaired.Condition, Is.EqualTo(repaired.MaxCondition));
            Assert.That(repaired.Repairs, Is.EqualTo((byte)1));
            Assert.That(repaired.Flags & ItemFlags.Broken, Is.Zero);
            Assert.That(Of(sim, b, ItemCatalog.KitWeaponRepair).Id.IsNull, Is.True, "the kit was not consumed");
            Assert.That(Items.Rows(sim).Single(r => r.Kind == ItemMoveKind.Repair).Ref, Is.EqualTo(unchecked((long)blaster.RawValue)));
            Assert.That(fee, Is.EqualTo(90), "10 % of a Mk I weapon's 906 cr");
            Assert.That(Ledgers.Balance(sim, b.Character), Is.EqualTo(balance - fee));
        });

        void Expect(Action send, byte reason, string what)
        {
            var before = Count(b.Link, reason);
            send();
            SessionHarness.Until(() => Count(b.Link, reason) > before, what);
            Breathe();
        }

        Breathe();
        Expect(() => Repair(b.Link, blaster), TatooineReplication.RepairNotWorn, "just repaired: a repair would lower it");
        var worn = StageWorn(sim, b, Blaster, 1, Standard(Blaster, 100));
        Expect(() => Repair(b.Link, worn), TatooineReplication.RepairNoKit, "no kit left");
        var spent = StageWorn(sim, b, Blaster, 1, Standard(Blaster, 100, repairs: SimBridge.MaxRepairs));
        Expect(() => Repair(b.Link, spent), TatooineReplication.RepairSpent, "two repairs used");
        Expect(() => Repair(b.Link, EntityId.FromRawValue(12345)), TatooineReplication.RepairBadItem, "not its own");
        Worlds.Park(sim, b.Link.Session, sim.Index.Stations[0].X + 60f, sim.Index.Stations[0].Z);
        Harness.Ticks(2);
        Expect(() => Repair(b.Link, blaster), TatooineReplication.CraftNotAtStation, "away from any station");
        AuditsOk(sim, "after the repairs");
    }

    // ── Salvage ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Q10: a crafted Mk III blaster salvaged for a quarter of its tree's quality materials at Mk III — Metal 3, Crystal 2, Gas 2, Hide 1 — the blaster
    /// gone, every row a <c>salvage</c> naming it. Standard issue, made by no one, is refused.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void ACraftedItem_SalvagesForAQuarterOfItsQualityMaterials()
    {
        using var sim = Start();
        var b = LogIn(sim, 0xC306);
        var before = Items.Stacks(sim, b.Character).Select(s => s.Id).ToHashSet();
        sim.Bridge.StageInstance(b.Key, Blaster, 3, Standard(Blaster, 1_000, crafter: b.Key));
        EntityId crafted = default;
        SessionHarness.Until(
            () =>
            {
                crafted = Items.Stacks(sim, b.Character).FirstOrDefault(s => s.Stack.Template == Blaster && !before.Contains(s.Id)).Id;
                return !crafted.IsNull;
            },
            "the crafted blaster");
        Harness.Ticks(1);

        b.Link.SendCommand(nameof(SalvageItem), new RecordValues { ["item"] = FieldValue.OfUInt64(crafted.RawValue) });
        SessionHarness.Until(() => sim.Bridge.Salvages == 1, "the salvage");
        Harness.Ticks(2);
        var back = Items.Stacks(sim, b.Character)
            .Where(s => !before.Contains(s.Id))
            .Select(s => (s.Stack.Template, s.Stack.Mk, s.Stack.Count))
            .OrderBy(x => x.Template);
        Assert.Multiple(() =>
        {
            Assert.That(Items.Stacks(sim, b.Character).Any(s => s.Id == crafted), Is.False);
            Assert.That(back, Is.EqualTo(new[]
            {
                (ItemCatalog.Metal, (byte)3, 3), (ItemCatalog.Crystal, (byte)3, 2), (ItemCatalog.Gas, (byte)3, 2), (ItemCatalog.Hide, (byte)3, 1),
            }));
            Assert.That(Items.Rows(sim).Where(r => r.Kind == ItemMoveKind.Salvage).All(r => r.Ref == unchecked((long)crafted.RawValue)), Is.True);
        });

        var standard = Of(sim, b, Blaster).Id;
        var refused = Count(b.Link, TatooineReplication.SalvageBadItem);
        b.Link.SendCommand(nameof(Server.Unequip), new RecordValues { ["slot"] = FieldValue.Of(EquipSlot.Weapon) });
        Breathe();
        b.Link.SendCommand(nameof(SalvageItem), new RecordValues { ["item"] = FieldValue.OfUInt64(standard.RawValue) });
        SessionHarness.Until(() => Count(b.Link, TatooineReplication.SalvageBadItem) > refused, "standard issue refused");
        AuditsOk(sim, "after the salvage");
    }

    /// <summary>
    /// The vendor pays an item with stats by the condition it has left (Loïc, 2026-11-03): a blaster at half its condition sells for half its payout, a
    /// <c>vendor</c> row with its credit row; a broken one is taken for nothing — scrapped, a <c>decay</c> row and no credit row — and both ends are counted
    /// (<see cref="GearEnd"/>); the audits clean.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void TheVendor_PaysForTheConditionLeft_AndScrapsWhatIsBroken()
    {
        using var sim = Start();
        var b = LogIn(sim, 0xDE51);
        var bazaar = sim.Index.Bazaars[0];
        Worlds.Park(sim, b.Link.Session, bazaar.X + 2.5f, bazaar.Z);
        Harness.Ticks(2);

        Equipment.TryStandard(Blaster, out var issue);
        var halfStats = Standard(Blaster, issue.MaxCondition / 2);
        var brokenStats = Standard(Blaster, 0, repairs: 1);
        brokenStats.Flags |= ItemFlags.Broken;
        var before = Items.Stacks(sim, b.Character).Select(x => x.Id).ToHashSet();
        sim.Bridge.StageInstance(b.Key, Blaster, 1, in halfStats);
        sim.Bridge.StageInstance(b.Key, Blaster, 1, in brokenStats);
        SessionHarness.Until(() => Items.Stacks(sim, b.Character).Count(x => !before.Contains(x.Id)) == 2, "the two staged blasters");
        var staged = Items.Stacks(sim, b.Character).Where(x => !before.Contains(x.Id)).Select(x => x.Id).ToList();
        var half = staged.Single(id => StatsOf(sim, id).Condition > 0);
        var broken = staged.Single(id => id != half);

        var balance = Ledgers.Balance(sim, b.Character);
        var credits = Ledgers.Rows(sim).Count;
        Market.Vendor(b.Link, Blaster, 1, 1, half.RawValue);
        SessionHarness.Until(() => Items.Stacks(sim, b.Character).All(x => x.Id != half), "the worn blaster sold");
        Breathe();
        var paid = Ledgers.Balance(sim, b.Character) - balance;
        Market.Vendor(b.Link, Blaster, 1, 1, broken.RawValue);
        SessionHarness.Until(() => Items.Stacks(sim, b.Character).All(x => x.Id != broken), "the broken blaster taken");
        Breathe();

        var full = MarketValues.VendorPayout(Blaster, 1, 1);
        var scrapRow = Items.Rows(sim).Last();
        Assert.Multiple(() =>
        {
            Assert.That(paid, Is.EqualTo(full * halfStats.Condition / halfStats.MaxCondition), "half its condition, half its payout");
            Assert.That(Ledgers.Balance(sim, b.Character) - balance, Is.EqualTo(paid), "the broken one pays nothing");
            Assert.That(Ledgers.Rows(sim), Has.Count.EqualTo(credits + 1), "one credit row: the sale's, none for the scrap");
            Assert.That((scrapRow.Kind, scrapRow.From, scrapRow.To, scrapRow.Count), Is.EqualTo((ItemMoveKind.Decay, b.Key, ItemLedger.World, 1)));
            Assert.That(sim.Bridge.GearEnds(GearEnd.Sold), Is.EqualTo(1));
            Assert.That(sim.Bridge.GearEnds(GearEnd.Scrapped), Is.EqualTo(1));
        });
        AuditsOk(sim, "after the sale and the scrap");
    }
}

/// <summary>The decay model's numbers (crafting-build.md C3-1, C3-3) and salvage's yield, as pure functions.</summary>
[TestFixture]
internal sealed class DecayModelChecks
{
    /// <summary>C3-1: a Mk I weapon used on-level lasts one combat-hour, its life factor's ±10 % within the ± 15 %.</summary>
    [TestCase(ItemCatalog.Knuckler)]
    [TestCase(ItemCatalog.Sword)]
    [TestCase(ItemCatalog.Blaster)]
    public void AMkIWeapon_LastsOneCombatHour(ushort weapon)
    {
        Equipment.TryStandard(weapon, out var issue);
        foreach (var life in new[] { 900, 1000, 1100 })
        {
            var hours = issue.MaxCondition * (life / 1000d) / 3_600_000d;
            Assert.That(hours, Is.InRange(0.85, 1.15), $"life factor {life}");
        }
    }

    /// <summary>C3-3: an item's life over its two repairs is 1 + 0.75 + 0.75² of its first, 2.3× ± 5 %.</summary>
    [Test]
    public void AnItemsLife_OverItsTwoRepairs_Is2Point3TimesItsFirst()
    {
        double life = 1_000_000, max = life, total = life;
        for (var r = 0; r < SimBridge.MaxRepairs; r++)
        {
            max = Math.Floor(max * SimBridge.RepairMaxPerMille / 1000);
            total += max;
        }

        Assert.That(total / life, Is.InRange(2.3 * 0.95, 2.3 * 1.05));
    }

    /// <summary>The fist wears nothing: a body with no weapon, or a broken one, fights with numbers no item has — a knuckler is not a fist.</summary>
    [Test]
    public void TheFist_IsNotAWeaponThatWears()
    {
        Equipment.TryStandard(ItemCatalog.Knuckler, out var knuckler);
        var withKnuckler = new PlayerGear { Stance = Stances.Unarmed, Damage = (short)knuckler.Stat0, DelayMs = (short)knuckler.Stat1 };
        Assert.Multiple(() =>
        {
            Assert.That(PlayerGear.Unarmed.IsFist, Is.True);
            Assert.That(withKnuckler.IsFist, Is.False);
        });
    }

    /// <summary>The armour's wear is shared by share: the torso takes 330 ‰ of the sum, the gloves 60 ‰; the weapon's is its uses times its delay.</summary>
    [Test]
    public void TheWear_IsSharedByShare_AndTheWeaponsIsItsDelays()
    {
        var g = new PlayerGear { ArmorWear = 1_000, WeaponUses = 3 };
        Assert.Multiple(() =>
        {
            Assert.That(SimBridge.WearOf(in g, EquipSlot.Torso - EquipSlot.First, 0), Is.EqualTo(330));
            Assert.That(SimBridge.WearOf(in g, EquipSlot.Hands - EquipSlot.First, 0), Is.EqualTo(60));
            Assert.That(SimBridge.WearOf(in g, 0, 1021), Is.EqualTo(3 * 1021));
            var hit = new PlayerGear();
            Assert.That(SimBridge.CountHit(ref hit, 61_000), Is.True, "a sum past the flush point is due at once");
        });
    }

    /// <summary>Q10's yield for every schematic that makes an item: a quarter of its tree's quality materials, at the item's tier — Mk I for a part.</summary>
    [Test]
    public void Salvage_IsAQuarterOfTheTreesQualityMaterials()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SimBridge.SalvageOf(ItemCatalog.Blaster, 3), Is.EqualTo(new[]
            {
                (ItemCatalog.Metal, (byte)3, 3), (ItemCatalog.Crystal, (byte)3, 2), (ItemCatalog.Gas, (byte)3, 2), (ItemCatalog.Hide, (byte)3, 1),
            }));
            Assert.That(SimBridge.SalvageOf(Schematics.Grip, 0), Is.EqualTo(new[] { (ItemCatalog.Hide, (byte)1, 1) }));
            Assert.That(SimBridge.SalvageOf(ItemCatalog.CompositeHelmet + 1, 2), Is.EqualTo(SimBridge.SalvageOf(ItemCatalog.PaddedHelmet + 1, 2)),
                "a composite piece salvages as its schematic, padded segments assumed");
            Assert.That(SimBridge.SalvageOf(ItemCatalog.Metal, 1), Is.Empty, "a material is made by nothing");
        });
    }
}
