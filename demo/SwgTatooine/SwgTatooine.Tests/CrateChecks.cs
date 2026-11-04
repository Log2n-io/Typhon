using System.Linq;
using NUnit.Framework;
using SwgTatooine.Server;
using SwgTatooine.Server.Accounts;
using Typhon.Engine;
using Typhon.Protocol;

namespace SwgTatooine.Tests;

/// <summary>
/// Crates (crafting-catalog.md § 11.2): identical pristine items packed into one instance whose count is how many — one slot, listed whole, bought whole;
/// unpacked into new instances; a worn, different or overfull pack refused; a crate unpacked before it is worn. The audits clean throughout.
/// </summary>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
internal sealed class CrateChecks : ServedChecks
{
    private const ushort Blaster = ItemCatalog.Blaster;

    private TatooineSim Start()
    {
        var config = Worlds.Served(Dir);
        config.CommandRateScale = Worlds.FastCommands;
        config.WorkerCount = 1;
        var sim = new TatooineSim(config);
        sim.Initialize();
        Harness = new SessionHarness(sim);
        return sim;
    }

    private (FakeLink Link, EntityId Character) LogIn(TatooineSim sim, ulong account, long bindings = 1)
    {
        var link = Harness.Connect(TatooineReplication.PlayerKind, TestAccounts.For(sim.Dbe, account));
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= bindings, "the avatar to be spawned");
        // Waited for, not asserted: on CI's runner the character was not found yet, two ticks after the possession, in five fixtures (PR #1590).
        var characterAccount = TestAccounts.For(sim.Dbe, account);
        var character = EntityId.Null;
        SessionHarness.Until(() => CharacterStore.TryFind(sim.Dbe, characterAccount, out character, out _), "the account's character");
        SessionHarness.Until(() => Ledgers.TryWallet(sim, character, out _), "the wallet to be opened");
        Harness.Ticks(2);
        return (link, character);
    }

    private void Breathe() => Harness.Ticks(Worlds.FastCommandGapTicks);

    private static ItemInstance Pristine(int damage = 0)
    {
        Equipment.TryStandard(Blaster, out var issue);
        var s = SimBridge.StandardStats(in issue);
        s.Stat0 += damage;
        return s;
    }

    /// <summary>Stages blasters with these stats into the grid; their ids, in no order.</summary>
    private static EntityId[] Stage(TatooineSim sim, EntityId character, params ItemInstance[] stats)
    {
        var key = unchecked((long)character.RawValue);
        var before = Items.Stacks(sim, character).Select(s => s.Id).ToHashSet();
        foreach (var s in stats)
        {
            sim.Bridge.StageInstance(key, Blaster, 1, in s);
        }

        SessionHarness.Until(() => Items.Stacks(sim, character).Count(s => !before.Contains(s.Id)) == stats.Length, "the staged blasters");
        return [.. Items.Stacks(sim, character).Where(s => !before.Contains(s.Id)).Select(s => s.Id)];
    }

    private static int CountOf(TatooineSim sim, EntityId character, EntityId item) => Items.Stacks(sim, character).Single(s => s.Id == item).Stack.Count;

    private static void Pack(FakeLink link, EntityId item, EntityId into)
        => link.SendCommand(
            nameof(PackItem),
            new RecordValues { ["item"] = FieldValue.OfUInt64(item.RawValue), ["into"] = FieldValue.OfUInt64(into.RawValue) });

    private static void Unpack(FakeLink link, EntityId item, int count)
        => link.SendCommand(nameof(UnpackItem), new RecordValues { ["item"] = FieldValue.OfUInt64(item.RawValue), ["count"] = FieldValue.Of(count) });

    private static int Count(FakeLink link, byte reason) => Ledgers.Refusals(link).Count(r => r == reason);

    private static void AuditsOk(TatooineSim sim, string when)
    {
        var (ok, why) = Market.Audits(sim);
        Assert.That(ok, Is.True, $"{when}: {why}");
    }

    /// <summary>
    /// Three identical blasters packed one into another, then the third into that: one crate of three in one slot, two <c>pack</c> rows; one unpacked into a
    /// slot of its own; the crate of two listed as one listing of two, and bought whole into one slot of the buyer's.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void IdenticalItems_PackIntoOneCrate_UnpackAndTradeWhole()
    {
        using var sim = Start();
        var (seller, sellerChar) = LogIn(sim, 0xC401);
        var ids = Stage(sim, sellerChar, Pristine(), Pristine(), Pristine());
        var rows = Items.Rows(sim).Count;

        Pack(seller, ids[0], ids[1]);
        Breathe();
        Pack(seller, ids[2], ids[1]);
        SessionHarness.Until(() => sim.Bridge.Packs == 2, "both packs applied");
        Breathe();
        var packs = Items.Rows(sim).Skip(rows).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(CountOf(sim, sellerChar, ids[1]), Is.EqualTo(3));
            Assert.That(Items.Stacks(sim, sellerChar).Any(s => s.Id == ids[0] || s.Id == ids[2]), Is.False, "the packed items are the crate now");
            Assert.That(packs.Select(r => (r.Kind, r.Count, r.ToAfter, r.From == r.To)), Is.EqualTo(new[]
            {
                (ItemMoveKind.Pack, 1, 2, true), (ItemMoveKind.Pack, 1, 3, true),
            }));
        });
        AuditsOk(sim, "after the packs");

        Unpack(seller, ids[1], 1);
        SessionHarness.Until(() => sim.Bridge.Unpacked == 1, "the unpack applied");
        Breathe();
        Assert.That(CountOf(sim, sellerChar, ids[1]), Is.EqualTo(2));
        Assert.That(Market.Units(sim, sellerChar, Blaster, 1), Is.EqualTo(3), "three blasters still held");
        AuditsOk(sim, "after the unpack");

        var bazaar = sim.Index.Bazaars[0];
        Worlds.Park(sim, seller.Session, bazaar.X + 2.5f, bazaar.Z);
        Harness.Ticks(2);
        Market.List(seller, Blaster, 1, 2, 500, ids[1].RawValue);
        SessionHarness.Until(() => Market.Listings(sim).Values.Any(l => l.Quantity == 2), "the crate listed");
        var listing = Market.Listings(sim).Single(l => l.Value.Quantity == 2).Key;
        AuditsOk(sim, "after the listing");

        var (buyer, buyerChar) = LogIn(sim, 0xC402, 2);
        Worlds.Park(sim, buyer.Session, bazaar.X + 2.5f, bazaar.Z);
        Harness.Ticks(2);
        var before = Items.Stacks(sim, buyerChar).Count;
        Market.Buy(buyer, listing, 500);
        Breathe();
        Market.Collect(buyer, listing);
        SessionHarness.Until(() => Items.Stacks(sim, buyerChar).Any(s => s.Id == ids[1]), "the crate collected");
        Assert.Multiple(() =>
        {
            Assert.That(CountOf(sim, buyerChar, ids[1]), Is.EqualTo(2), "the crate, whole");
            Assert.That(Items.Stacks(sim, buyerChar), Has.Count.EqualTo(before + 1), "in one slot");
        });
        AuditsOk(sim, "after the buy");
    }

    /// <summary>
    /// What does not pack is refused and writes nothing: a worn item, two that differ, an item into itself, past a crate's ten; an unpack of all of a crate
    /// or of an item that is not one; and a crate worn before it is unpacked.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void APackOfWhatDoesNotPack_IsRefusedAndWritesNothing()
    {
        using var sim = Start();
        var (link, character) = LogIn(sim, 0xC403);
        var worn = Pristine();
        worn.Condition -= 1;
        worn.Flags &= unchecked((byte)~ItemFlags.Pristine);
        var ten = Enumerable.Range(0, 11).Select(_ => Pristine()).ToArray();
        var ids = Stage(sim, character, ten);
        var others = Stage(sim, character, worn, Pristine(damage: 1));
        for (var i = 1; i < 10; i++)
        {
            Pack(link, ids[i], ids[0]);
            Breathe();
        }

        SessionHarness.Until(() => sim.Bridge.Packs == 9, "a crate of ten");
        var rows = Items.Rows(sim).Count;

        Pack(link, others[0], ids[10]);
        Breathe();
        Pack(link, others[1], ids[10]);
        Breathe();
        Pack(link, ids[10], ids[10]);
        Breathe();
        Pack(link, ids[10], ids[0]);
        Breathe();
        Unpack(link, ids[0], 10);
        Breathe();
        Unpack(link, ids[10], 1);
        Breathe();
        link.SendCommand(nameof(Equip), new RecordValues { ["item"] = FieldValue.OfUInt64(ids[0].RawValue) });
        Breathe();
        Harness.Ticks(3);

        Assert.Multiple(() =>
        {
            Assert.That(Count(link, TatooineReplication.PackBad), Is.EqualTo(4), "worn, different, itself, past ten");
            Assert.That(Count(link, TatooineReplication.UnpackBad), Is.EqualTo(2), "all of a crate, an item");
            Assert.That(Count(link, TatooineReplication.ItemCrated), Is.EqualTo(1), "a crate worn");
            Assert.That(Items.Rows(sim), Has.Count.EqualTo(rows), "a refusal writes no row");
            Assert.That(CountOf(sim, character, ids[0]), Is.EqualTo(10));
        });
        AuditsOk(sim, "after the refusals");
    }

    /// <summary>
    /// A crate sold to the vendor is sold whole: its count destroyed, paid for its count, each item noted sold; a crate is refused for a repair and for an
    /// operator's stats rewrite, as for a wear (ItemCrated). The audits clean.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void ACrate_IsSoldWhole_AndRefusedForARepairOrARewrite()
    {
        using var sim = Start();
        var (link, character) = LogIn(sim, 0xC404);
        var ids = Stage(sim, character, Pristine(), Pristine(), Pristine());
        Pack(link, ids[0], ids[1]);
        Breathe();
        Pack(link, ids[2], ids[1]);
        SessionHarness.Until(() => sim.Bridge.Packs == 2, "a crate of three");

        var station = sim.Index.Stations[0];
        Worlds.Park(sim, link.Session, station.X + 2f, station.Z);
        Harness.Ticks(2);
        link.SendCommand(nameof(RepairItem), new RecordValues { ["item"] = FieldValue.OfUInt64(ids[1].RawValue) });
        Breathe();
        var rewrite = sim.Bridge.Admin(new AdminRequest
        {
            Action = AdminAction.ItemStats,
            Character = unchecked((long)character.RawValue),
            Item = unchecked((long)ids[1].RawValue),
            HasStats = true,
            Stats = Pristine(),
        });
        SessionHarness.Until(() => rewrite.IsCompleted, "the rewrite answered");

        var bazaar = sim.Index.Bazaars[0];
        Worlds.Park(sim, link.Session, bazaar.X + 2.5f, bazaar.Z);
        Harness.Ticks(2);
        var balance = Ledgers.Balance(sim, character);
        var sold = sim.Bridge.GearEnds(GearEnd.Sold);
        Market.Vendor(link, Blaster, 1, 3, ids[1].RawValue);
        SessionHarness.Until(() => Items.Stacks(sim, character).All(s => s.Id != ids[1]), "the crate sold");
        Breathe();
        Assert.Multiple(() =>
        {
            Assert.That(Count(link, TatooineReplication.ItemCrated), Is.EqualTo(1), "the repair of a crate");
            Assert.That(rewrite.Result.Ok, Is.False);
            Assert.That(rewrite.Result.Message, Does.Contain("crate"));
            Assert.That(Ledgers.Balance(sim, character) - balance, Is.EqualTo(MarketValues.VendorPayout(Blaster, 1, 3)), "paid for three");
            Assert.That(sim.Bridge.GearEnds(GearEnd.Sold) - sold, Is.EqualTo(3), "three items sold");
            Assert.That(Market.Units(sim, character, Blaster, 1), Is.Zero);
        });
        AuditsOk(sim, "after the sale");
    }

    /// <summary>The item audit catches what crates and refines must not be: a crate of a worn item, a refine whose ratio is wrong.</summary>
    [Test]
    [CancelAfter(120_000)]
    public void TheAudit_CatchesAWornCrate_AndARefineOffItsRatio()
    {
        using var sim = Start();
        var (link, character) = LogIn(sim, 0xC405);
        var ids = Stage(sim, character, Pristine(), Pristine());
        Pack(link, ids[0], ids[1]);
        SessionHarness.Until(() => sim.Bridge.Packs == 1, "a crate of two");
        sim.Bridge.StageTransfer(ItemLedger.World, unchecked((long)character.RawValue), ItemCatalog.CoreRanged, 1, 6, ItemMoveKind.Kill, 1);
        SessionHarness.Until(() => Market.Units(sim, character, ItemCatalog.CoreRanged, 1) == 6, "six cores");
        var station = sim.Index.Stations[0];
        Worlds.Park(sim, link.Session, station.X + 2f, station.Z);
        Harness.Ticks(2);
        link.SendCommand(nameof(RefineItems), new RecordValues
        {
            ["template"] = FieldValue.Of(ItemCatalog.CoreRanged), ["mk"] = FieldValue.Of((byte)1), ["count"] = FieldValue.Of(2),
        });
        SessionHarness.Until(() => sim.Bridge.Refines == 1, "the refine");
        Breathe();
        AuditsOk(sim, "before the corruption");

        sim.WriteInTick(tx =>
        {
            var e = tx.OpenMut(ids[1]);
            var s = e.Read(ItemInstanceArch.Stats);
            s.Condition -= 1;
            e.Set(ItemInstanceArch.Stats, s);
        });
        var worn = ItemEndpoints.Audit(sim.Dbe);
        sim.WriteInTick(tx =>
        {
            var e = tx.OpenMut(ids[1]);
            var s = e.Read(ItemInstanceArch.Stats);
            s.Condition += 1;
            e.Set(ItemInstanceArch.Stats, s);
        });

        var rows = Items.Rows(sim, out var rowIds);
        var made = rows.FindLastIndex(r => r.Kind == ItemMoveKind.Refine && r.Mk == 2);
        sim.WriteInTick(tx =>
        {
            var e = tx.OpenMut(rowIds[made]);
            var r = e.Read(ItemLedgerArch.Entry);
            r.Count = 3;
            r.ToAfter = 3;
            e.Set(ItemLedgerArch.Entry, r);
        });
        var ratio = ItemEndpoints.Audit(sim.Dbe);

        Assert.Multiple(() =>
        {
            Assert.That(worn.Violations, Has.Some.Contains("not pristine"));
            Assert.That(ratio.Violations, Has.Some.Contains("the ratio is 3"));
        });
    }
}
