using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SwgTatooine.Server;
using SwgTatooine.Server.Accounts;
using Typhon.Engine;
using Typhon.Protocol;

namespace SwgTatooine.Tests;

/// <summary>
/// Items with stats (crafting-build.md § 2.1, C1): standard issue bought from the vendor, then listed, bought, collected and sold to the vendor through the
/// real commands — one entity from mint to destroy, its stats intact, never merged — with the item, credit and market audits clean at every step.
/// </summary>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
internal sealed class InstanceChecks : ServedChecks
{
    // The cheapest standard issue: padded gloves, ⌊6 340 × 6 % × 2⌋ = 760 cr — within the 1 000 cr grant.
    private const ushort Gloves = ItemCatalog.PaddedHelmet + 4;
    private const ushort Blaster = ItemCatalog.Blaster;

    private TatooineSim Start(bool persist = false)
    {
        var config = Worlds.Served(Dir);
        config.CommandRateScale = Worlds.FastCommands;
        config.WorkerCount = 1;
        config.Persist = persist;
        var sim = new TatooineSim(config);
        sim.Initialize();
        Harness = new SessionHarness(sim);
        return sim;
    }

    private sealed record Trader(FakeLink Link, EntityId Body, EntityId Character)
    {
        public long Key => unchecked((long)Character.RawValue);
    }

    private Trader LogIn(TatooineSim sim, ulong account, (float X, float Z) at, long bindings)
    {
        var link = Harness.Connect(TatooineReplication.PlayerKind, TestAccounts.For(sim.Dbe, account));
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= bindings, "the avatar to be spawned");
        Harness.Ticks(2);
        var body = Worlds.Park(sim, link.Session, at.X, at.Z);
        // Waited for, not asserted: on CI's runner the character was not found yet, two ticks after the possession, in five fixtures (PR #1590).
        var characterAccount = TestAccounts.For(sim.Dbe, account);
        var character = EntityId.Null;
        SessionHarness.Until(() => CharacterStore.TryFind(sim.Dbe, characterAccount, out character, out _), "the account's character");
        SessionHarness.Until(() => Ledgers.TryWallet(sim, character, out _), "the wallet to be opened");
        Harness.Ticks(2);
        return new Trader(link, body, character);
    }

    private static (float X, float Z) At(MissionTerminalRef bazaar) => (bazaar.X + 2.5f, bazaar.Z);

    private static MissionTerminalRef Home(TatooineSim sim) => sim.Index.Bazaars[0];

    private void Breathe() => Harness.Ticks(Worlds.FastCommandGapTicks);

    /// <summary>Credits the honest way: Mk IV armour cores, minted and sold to the vendor at a fifth of 5 950 each.</summary>
    private void Earn(TatooineSim sim, Trader t, int cores)
    {
        var balance = Ledgers.Balance(sim, t.Character);
        sim.Bridge.StageTransfer(ItemLedger.World, t.Key, ItemCatalog.CoreArmor, 4, cores, ItemMoveKind.Kill, 1);
        SessionHarness.Until(() => Market.Units(sim, t.Character, ItemCatalog.CoreArmor, 4) == cores, "the cores");
        Breathe();
        Market.Vendor(t.Link, ItemCatalog.CoreArmor, 4, cores);
        SessionHarness.Until(() => Ledgers.Balance(sim, t.Character) > balance, "the cores sold");
        Breathe();
    }

    private static void BuyStandard(FakeLink link, ushort template, long price, int quantity = 0)
        => link.SendCommand(nameof(Server.BuyStandard), new RecordValues
        {
            ["template"] = FieldValue.Of(template),
            ["price"] = FieldValue.Of((double)price),
            ["quantity"] = FieldValue.Of((double)quantity),
        });

    /// <summary>A character's instances, by id, with their stack and stats.</summary>
    private static List<(EntityId Id, ItemStack Stack, ItemInstance Stats)> Instances(TatooineSim sim, EntityId character)
    {
        var routing = sim.Dbe.ArchetypeIdOf<ItemInstanceArch>();
        var list = new List<(EntityId, ItemStack, ItemInstance)>();
        using var tx = sim.Dbe.CreateQuickTransaction();
        foreach (var (id, s) in Items.Stacks(sim, character).Where(s => s.Id.ArchetypeId == routing))
        {
            list.Add((id, s, tx.Open(id).Read(ItemInstanceArch.Stats)));
        }

        return list;
    }

    private static void AuditsOk(TatooineSim sim, string when)
    {
        var (ok, why) = Market.Audits(sim);
        Assert.That(ok, Is.True, $"{when}: {why}");
    }

    private static int Count(FakeLink link, byte reason) => Ledgers.Refusals(link).Count(r => r == reason);

    private void Refused(FakeLink link, byte reason, int before, string what)
    {
        SessionHarness.Until(() => Count(link, reason) > before, what);
        Harness.Ticks(2);
    }

    /// <summary>
    /// The vendor sells standard issue: one instance minted into a free slot, a typical Mk I craft, new and pristine; the buyer pays World twice its value as
    /// one <c>standard</c> credit row, and the item row names the instance and the credit row. Two of one template are two instances, never a stack.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void TheVendor_SellsMkIBulkMaterials_ByTheUnit()
    {
        using var sim = Start();
        var a = LogIn(sim, 0x9311, At(Home(sim)), 1);
        var balance = Ledgers.Balance(sim, a.Character);
        var creditRows = Ledgers.Rows(sim).Count;
        var price = Equipment.StandardMaterialPrice(ItemCatalog.Metal, 25);

        BuyStandard(a.Link, ItemCatalog.Metal, price, 25);
        SessionHarness.Until(() => Market.Units(sim, a.Character, ItemCatalog.Metal, 1) == 25, "the metal");
        Harness.Ticks(2);
        var row = Items.Rows(sim).Single(r => r.Template == ItemCatalog.Metal);
        var credit = Ledgers.Rows(sim).Skip(creditRows).Single();
        Assert.Multiple(() =>
        {
            Assert.That(price, Is.EqualTo(85), "⌊25 × 1.7 cr × 2⌋");
            Assert.That(
                (row.Kind, row.From, row.To, row.Count, row.Mk, row.Ref),
                Is.EqualTo((ItemMoveKind.Standard, ItemLedger.World, a.Key, 25, (byte)1, credit.Seq)));
            Assert.That((credit.Kind, credit.Amount, credit.Ref), Is.EqualTo((TransferKind.Standard, price, (long)ItemCatalog.Metal)));
            Assert.That(Ledgers.Balance(sim, a.Character), Is.EqualTo(balance - price));
        });

        void Expect(Action send, byte reason, string what)
        {
            var before = Ledgers.Refusals(a.Link).Count(r => r == reason);
            send();
            SessionHarness.Until(() => Ledgers.Refusals(a.Link).Count(r => r == reason) > before, what);
            Breathe();
        }

        Expect(() => BuyStandard(a.Link, ItemCatalog.Crystal, Equipment.StandardMaterialPrice(ItemCatalog.Crystal, 10) + 1, 10), TatooineReplication.MarketSold,
            "another price");
        Expect(() => BuyStandard(a.Link, ItemCatalog.Crystal, 999, 100), TatooineReplication.MarketBadListing, "past the vendor's lot");
        Expect(() => BuyStandard(a.Link, ItemCatalog.Hide, 3, 1), TatooineReplication.MarketBadListing, "a material the beasts drop is not sold");
        AuditsOk(sim, "after the materials");
    }

    [Test]
    [CancelAfter(120_000)]
    public void TheVendor_SellsStandardIssue_AsInstancesThatNeverMerge()
    {
        using var sim = Start();
        var a = LogIn(sim, 0x9301, At(Home(sim)), 1);
        var balance = Ledgers.Balance(sim, a.Character);
        var itemRows = Items.Rows(sim).Count;
        var creditRows = Ledgers.Rows(sim).Count;
        var price = Equipment.StandardPrice(Gloves);

        BuyStandard(a.Link, Gloves, price);
        SessionHarness.Until(() => Instances(sim, a.Character).Count == 1, "the gloves");
        Harness.Ticks(2);
        var (id, stack, stats) = Instances(sim, a.Character).Single();
        var row = Items.Rows(sim).Skip(itemRows).Single();
        var credit = Ledgers.Rows(sim).Skip(creditRows).Single();
        Equipment.TryStandard(Gloves, out var issue);
        Assert.Multiple(() =>
        {
            Assert.That(price, Is.EqualTo(760), "⌊6 340 × 6 % × 2⌋");
            Assert.That((stack.Template, stack.Mk, stack.Count, stack.Slot), Is.EqualTo((Gloves, (byte)1, 1, (byte)1)));
            Assert.That((stats.Stat0, stats.Stat1, stats.Condition, stats.MaxCondition), Is.EqualTo((issue.Stat0, issue.Stat1, issue.MaxCondition, issue.MaxCondition)));
            Assert.That((stats.LifeMilli, stats.Flags, stats.Crafter), Is.EqualTo(((short)1000, ItemFlags.Pristine, 0L)));
            Assert.That((row.Kind, row.From, row.To, row.ToStack, row.Count, row.ToAfter, row.Ref),
                Is.EqualTo((ItemMoveKind.Standard, ItemLedger.World, a.Key, unchecked((long)id.RawValue), 1, 1, credit.Seq)));
            Assert.That((credit.Kind, credit.From, credit.To, credit.Amount, credit.Ref),
                Is.EqualTo((TransferKind.Standard, a.Key, EconomyLedger.World, price, (long)Gloves)));
            Assert.That(Ledgers.Balance(sim, a.Character), Is.EqualTo(balance - price));
            Assert.That(sim.Bridge.StandardSold, Is.EqualTo(1));
        });
        AuditsOk(sim, "after the purchase");

        Earn(sim, a, 2);
        BuyStandard(a.Link, Gloves, price);
        SessionHarness.Until(() => Instances(sim, a.Character).Count == 2, "the second gloves");
        Harness.Ticks(2);
        var two = Instances(sim, a.Character);
        Assert.That(two.Select(i => (i.Stack.Count, i.Stack.Slot)), Is.EquivalentTo(new[] { (1, (byte)1), (1, (byte)2) }), "merged, or not in two slots");
        AuditsOk(sim, "after the second purchase");
    }

    /// <summary>
    /// An instance listed goes into escrow whole — its owner the listing, its quantity 1 — and the buyer receives the same entity with its stats: the listing
    /// shows them, and the audits hold at every step. Then the buyer sells it to the vendor: destroyed, paid a fifth of its value.
    /// </summary>
    [Test]
    [CancelAfter(180_000)]
    public void AnInstance_IsListedBoughtAndVendoredAsItself()
    {
        using var sim = Start();
        var home = Home(sim);
        var a = LogIn(sim, 0x9311, At(home), 1);
        var b = LogIn(sim, 0x9312, (home.X + 2.5f, home.Z + 0.6f), 2);
        BuyStandard(a.Link, Gloves, Equipment.StandardPrice(Gloves));
        SessionHarness.Until(() => Instances(sim, a.Character).Count == 1, "the gloves");
        Breathe();
        var (id, _, stats) = Instances(sim, a.Character).Single();

        Market.List(a.Link, Gloves, 1, 1, 500, id.RawValue);
        SessionHarness.Until(() => Market.Listings(sim).Values.Any(l => l.Seller == a.Key), "the listing");
        Harness.Ticks(2);
        var (listing, l) = Market.Listings(sim).Single(kv => kv.Value.Seller == a.Key);
        // The board is a snapshot published every MarketPublishMs, not the ledger: two ticks after the listing it may not be on it yet. It passed while
        // the commands' half-second gaps happened to straddle a publish (2026-11-02).
        SessionHarness.Until(() => sim.Bridge.MarketBoard.Cities.SelectMany(c => c.Listings).Any(v => v.Id == listing), "the listing on the board");
        Assert.Multiple(() =>
        {
            Assert.That((l.Template, l.Mk, l.Quantity), Is.EqualTo((Gloves, (byte)1, 1)));
            Assert.That(Instances(sim, a.Character), Is.Empty, "the gloves are still in the seller's grid");
            Assert.That(sim.Bridge.EscrowItemOf(listing), Is.EqualTo(id), "escrow holds another entity");
            Assert.That(sim.Bridge.MarketBoard.Cities.SelectMany(c => c.Listings).Single(v => v.Id == listing).Stats.Stat0, Is.EqualTo(stats.Stat0),
                "the board does not show the item's stats");
        });
        AuditsOk(sim, "after the listing");

        Market.Buy(b.Link, listing, 500);
        SessionHarness.Until(() => Instances(sim, b.Character).Count == 1, "the purchase delivered");
        Harness.Ticks(2);
        var (bought, stack, boughtStats) = Instances(sim, b.Character).Single();
        Assert.Multiple(() =>
        {
            Assert.That(bought, Is.EqualTo(id), "the buyer received another entity");
            Assert.That(boughtStats, Is.EqualTo(stats), "the stats did not travel");
            Assert.That((stack.Owner, stack.Count), Is.EqualTo((b.Key, 1)));
            Assert.That(sim.Bridge.EscrowItemOf(listing).IsNull, Is.True);
        });
        AuditsOk(sim, "after the purchase");

        Breathe();
        var balance = Ledgers.Balance(sim, b.Character);
        Market.Vendor(b.Link, Gloves, 1, 1, id.RawValue);
        SessionHarness.Until(() => Instances(sim, b.Character).Count == 0, "the vendor sale");
        Harness.Ticks(2);
        Assert.That(Ledgers.Balance(sim, b.Character), Is.EqualTo(balance + MarketValues.VendorPayout(Gloves, 1, 1)));
        AuditsOk(sim, "after the vendor sale");
    }

    /// <summary>
    /// The refusals: a standard purchase at another price, of what is not standard issue, with no room or too little money; an instance listed two at a
    /// time, or by an id the seller does not hold; sold to the vendor by a wrong id. Each with its code, and nothing written.
    /// </summary>
    [Test]
    [CancelAfter(240_000)]
    public void EveryInstanceRefusal_HasItsReason()
    {
        using var sim = Start();
        var a = LogIn(sim, 0x9321, At(Home(sim)), 1);
        BuyStandard(a.Link, Gloves, Equipment.StandardPrice(Gloves));
        SessionHarness.Until(() => Instances(sim, a.Character).Count == 1, "the gloves");
        Breathe();
        var (id, _, _) = Instances(sim, a.Character).Single();
        var rows = Items.Rows(sim).Count;

        void Expect(System.Action send, byte reason, string what)
        {
            var before = Count(a.Link, reason);
            send();
            Refused(a.Link, reason, before, what);
            Breathe();
        }

        Expect(() => BuyStandard(a.Link, Gloves, 1), TatooineReplication.MarketSold, "another price");
        Expect(() => BuyStandard(a.Link, ItemCatalog.CoreArmor, 1), TatooineReplication.MarketBadListing, "not standard issue");
        Expect(() => BuyStandard(a.Link, Blaster, Equipment.StandardPrice(Blaster)), TatooineReplication.MarketNotEnough, "1 812 cr against 240");
        // Two of one item: a quantity is the item's count — 1, or a crate's (crafting-catalog.md § 11.2) — so two of a single one are not held.
        Expect(() => Market.List(a.Link, Gloves, 1, 2, 500, id.RawValue), TatooineReplication.MarketNoItems, "two of one item");
        Expect(() => Market.List(a.Link, Gloves, 1, 1, 500, 12345), TatooineReplication.MarketNoItems, "an id it does not hold");
        Expect(() => Market.List(a.Link, Gloves, 1, 1, 500), TatooineReplication.MarketNoItems, "no id");
        Expect(() => Market.Vendor(a.Link, Gloves, 1, 1, 12345), TatooineReplication.MarketNoItems, "the vendor, a wrong id");
        Assert.That(Items.Rows(sim), Has.Count.EqualTo(rows), "a refusal wrote an item row");
        AuditsOk(sim, "after the refusals");
    }

    /// <summary>
    /// C1-6: a new character's wallet opens with the welcome pack — its 1 000 cr grant, one standard-issue blaster, a repair kit and three stims, minted in
    /// the same commit as <c>grant</c> item rows — exactly once: a second login opens nothing more.
    /// </summary>
    [Test]
    [CancelAfter(180_000)]
    public void ANewCharacter_GetsTheWelcomePack_ExactlyOnce()
    {
        var config = Worlds.Served(Dir);
        config.CommandRateScale = Worlds.FastCommands;
        config.WorkerCount = 1;
        config.WelcomePack = true;
        using var sim = new TatooineSim(config);
        sim.Initialize();
        Harness = new SessionHarness(sim);
        var a = LogIn(sim, 0x9341, At(Home(sim)), 1);
        SessionHarness.Until(() => Instances(sim, a.Character).Count == 1 + SimBridge.WelcomeStims, "the pack");
        Harness.Ticks(2);
        var (id, stack, _) = Instances(sim, a.Character).Single(i => i.Stack.Template == Blaster);
        var packRows = Items.Rows(sim).Where(r => r.To == a.Key && r.Kind == ItemMoveKind.Grant).ToList();
        var grant = Ledgers.Rows(sim).Single(r => r.To == a.Key && r.Kind == TransferKind.Grant);
        Assert.Multiple(() =>
        {
            Assert.That((stack.Template, stack.Mk), Is.EqualTo((Blaster, (byte)1)));
            Assert.That(
                packRows.Select(r => (r.Template, r.Mk, r.Count)),
                Is.EquivalentTo(new[]
                {
                    (Blaster, (byte)1, 1), (ItemCatalog.KitWeaponRepair, (byte)0, 1),
                    (ItemCatalog.Stim, (byte)0, 1), (ItemCatalog.Stim, (byte)0, 1), (ItemCatalog.Stim, (byte)0, 1),
                }),
                "the pack: the blaster, a weapon repair kit (C3) and three stims (C2)");
            var blasterRow = packRows.Single(r => r.Template == Blaster);
            Assert.That((blasterRow.From, blasterRow.ToStack, blasterRow.Ref), Is.EqualTo((ItemLedger.World, unchecked((long)id.RawValue), 0L)));
            Assert.That(packRows.All(r => r.WorldTick == grant.WorldTick), Is.True, "the pack and the credits are not one commit");
            Assert.That(Ledgers.Balance(sim, a.Character), Is.EqualTo(EconomyLedger.StartingCredits));
        });
        AuditsOk(sim, "after the pack");

        // Out and in again: the wallet exists, so nothing is granted twice.
        a.Link.Close(CloseCodes.Normal, "logging out");
        SessionHarness.Until(() => TatooineReplication.AvatarsDestroyed >= 1, "the avatar to be saved and destroyed");
        Harness.Ticks(2);
        var again = LogIn(sim, 0x9341, At(Home(sim)), 2);
        Harness.Ticks(Worlds.ServedHz);
        Assert.Multiple(() =>
        {
            Assert.That(again.Character, Is.EqualTo(a.Character));
            Assert.That(
                Items.Rows(sim).Count(r => r.To == a.Key && r.Kind == ItemMoveKind.Grant),
                Is.EqualTo(2 + SimBridge.WelcomeStims),
                "the pack (a blaster, a kit and the stims) was granted again");
            Assert.That(Instances(sim, a.Character), Has.Count.EqualTo(1 + SimBridge.WelcomeStims));
        });
    }

    /// <summary>An instance in escrow, and one in a grid, survive a reopen: the same entities, stats and owners, and the audits clean.</summary>
    [Test]
    [CancelAfter(240_000)]
    public void Instances_InEscrowAndInAGrid_SurviveAReopen()
    {
        EntityId inEscrow;
        EntityId inGrid;
        long listing;
        EntityId character;
        using (var first = Start(persist: true))
        {
            var a = LogIn(first, 0x9331, At(Home(first)), 1);
            Earn(first, a, 2);
            BuyStandard(a.Link, Gloves, Equipment.StandardPrice(Gloves));
            BuyStandard(a.Link, (ushort)(Gloves + 1), Equipment.StandardPrice((ushort)(Gloves + 1)));
            SessionHarness.Until(() => Instances(first, a.Character).Count == 2, "the gloves and the boots");
            Breathe();
            var two = Instances(first, a.Character);
            inEscrow = two[0].Id;
            inGrid = two[1].Id;
            Market.List(a.Link, two[0].Stack.Template, 1, 1, 500, inEscrow.RawValue);
            SessionHarness.Until(() => Market.Listings(first).Values.Any(l => l.Seller == a.Key), "the listing");
            Harness.Ticks(3);
            listing = Market.Listings(first).Single().Key;
            character = a.Character;
            AuditsOk(first, "before the stop");
            Harness.Release();
        }

        TatooineReplication.ResetSessionAccounting();
        TatooineReplication.ResetIntentAccounting();
        using var second = Start(persist: true);
        Assert.That(second.Reopened, Is.True);
        Harness.Ticks(3);
        second.Bridge.PublishMarketNowForChecks();
        Harness.Ticks(3);
        Assert.Multiple(() =>
        {
            Assert.That(Instances(second, character).Select(i => i.Id), Is.EqualTo(new[] { inGrid }));
            Assert.That(second.Bridge.EscrowItemOf(listing), Is.EqualTo(inEscrow), "the escrow was not read back");
        });
        AuditsOk(second, "after the reopen");
    }
}
