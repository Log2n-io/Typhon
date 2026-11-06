using System.Linq;
using NUnit.Framework;
using SwgTatooine.Server;
using SwgTatooine.Server.Accounts;
using Typhon.Engine;
using Typhon.Protocol;

namespace SwgTatooine.Tests;

/// <summary>
/// The inventory window's commands (inventory-loot.md § 10): a stack holds up to 100 000; units split into a free slot and merge into the same units, a row
/// each; a whole stack or an item moved or swapped without one; anything thrown away to World. Refused when the shape is wrong. The audits clean throughout.
/// </summary>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
internal sealed class GridChecks : ServedChecks
{
    private const ushort Ore = ItemCatalog.Ore;

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

    private (FakeLink Link, EntityId Character) LogIn(TatooineSim sim, ulong account)
    {
        var link = Harness.Connect(TatooineReplication.PlayerKind, TestAccounts.For(sim.Dbe, account));
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= 1, "the avatar to be spawned");
        var characterAccount = TestAccounts.For(sim.Dbe, account);
        var character = EntityId.Null;
        SessionHarness.Until(() => CharacterStore.TryFind(sim.Dbe, characterAccount, out character, out _), "the account's character");
        SessionHarness.Until(() => Ledgers.TryWallet(sim, character, out _), "the wallet to be opened");
        Harness.Ticks(2);
        return (link, character);
    }

    private void Breathe() => Harness.Ticks(Worlds.FastCommandGapTicks);

    private static void Move(FakeLink link, EntityId item, int slot, int count)
        => link.SendCommand(
            nameof(MoveItem),
            new RecordValues { ["item"] = FieldValue.OfUInt64(item.RawValue), ["slot"] = FieldValue.Of(slot), ["count"] = FieldValue.Of(count) });

    private static void Discard(FakeLink link, EntityId item, int count)
        => link.SendCommand(nameof(DiscardItem), new RecordValues { ["item"] = FieldValue.OfUInt64(item.RawValue), ["count"] = FieldValue.Of(count) });

    private static void Sort(FakeLink link) => link.SendCommand(nameof(SortItems), new RecordValues { ["order"] = FieldValue.Of(0) });

    private static int Refused(FakeLink link, byte reason) => Ledgers.Refusals(link).Count(r => r == reason);

    private static void AuditsOk(TatooineSim sim, string when)
    {
        var (ok, why) = Market.Audits(sim);
        Assert.That(ok, Is.True, $"{when}: {why}");
    }

    private static (EntityId Id, ItemStack Stack)[] OreStacks(TatooineSim sim, EntityId character)
        => [.. Items.Stacks(sim, character)
            .Where(s => s.Stack.Template == Ore && s.Stack.Mk == 1 && s.Stack.Slot <= ItemLedger.Slots)
            .OrderBy(s => s.Stack.Slot)];

    private static int SlotOf(TatooineSim sim, EntityId character, EntityId id) => Items.Stacks(sim, character).Single(s => s.Id == id).Stack.Slot;

    /// <summary>The highest grid slot nothing is in.</summary>
    private static int LastFreeSlot(TatooineSim sim, EntityId character)
    {
        var used = Items.Stacks(sim, character).Select(s => (int)s.Stack.Slot).ToHashSet();
        return Enumerable.Range(1, ItemLedger.Slots).Last(s => !used.Contains(s));
    }

    private static EntityId StageOre(TatooineSim sim, EntityId character, int count)
    {
        var before = OreStacks(sim, character).Sum(s => s.Stack.Count);
        sim.Bridge.StageTransfer(ItemLedger.World, unchecked((long)character.RawValue), Ore, 1, count, ItemMoveKind.Kill, 1);
        SessionHarness.Until(() => OreStacks(sim, character).Sum(s => s.Stack.Count) == before + count, "the staged ore");
        return OreStacks(sim, character)[^1].Id;
    }

    /// <summary>A standard-issue blaster staged into the grid: an item with stats.</summary>
    private static EntityId StageBlaster(TatooineSim sim, EntityId character)
    {
        Equipment.TryStandard(ItemCatalog.Blaster, out var issue);
        var stats = SimBridge.StandardStats(in issue);
        var before = Items.Stacks(sim, character).Select(s => s.Id).ToHashSet();
        sim.Bridge.StageInstance(unchecked((long)character.RawValue), ItemCatalog.Blaster, 1, in stats);
        SessionHarness.Until(() => Items.Stacks(sim, character).Any(s => !before.Contains(s.Id)), "the staged blaster");
        return Items.Stacks(sim, character).Single(s => !before.Contains(s.Id)).Id;
    }

    /// <summary>
    /// 150 000 Ore minted in two stacks, 100 000 and 50 000; 20 000 split off into the first free slot and merged back, one <c>move</c> row each, the
    /// emptied stack gone; a merge onto the full stack refused; the stack moved whole to another slot, then swapped with another item, without a row.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void Units_StackTo100000_SplitAndMerge_ByARow_AndMoveWithoutOne()
    {
        using var sim = Start();
        var (link, character) = LogIn(sim, 0xC601);
        StageOre(sim, character, 150_000);
        var ore = OreStacks(sim, character);
        Assert.That(ore.Select(s => s.Stack.Count), Is.EqualTo(new[] { ItemLedger.StackMax, 50_000 }), "a stack holds 100 000, then a slot opens");
        var (full, half) = (ore[0].Id, ore[1].Id);
        AuditsOk(sim, "after the mint");

        var rows = Items.Rows(sim).Count;
        Move(link, half, 0, 20_000);
        SessionHarness.Until(() => sim.Bridge.GridSplits == 1, "the split applied");
        Breathe();
        var split = OreStacks(sim, character).Single(s => s.Id != full && s.Id != half);
        Assert.Multiple(() =>
        {
            Assert.That(split.Stack.Count, Is.EqualTo(20_000));
            Assert.That(OreStacks(sim, character).Single(s => s.Id == half).Stack.Count, Is.EqualTo(30_000));
            var row = Items.Rows(sim).Skip(rows).Single();
            Assert.That((row.Kind, row.From == row.To, row.Count, row.FromAfter, row.ToAfter), Is.EqualTo((ItemMoveKind.Move, true, 20_000, 30_000, 20_000)));
        });
        AuditsOk(sim, "after the split");

        Move(link, split.Id, SlotOf(sim, character, half), 20_000);
        SessionHarness.Until(() => sim.Bridge.GridMerges == 1, "the merge applied");
        Breathe();
        Assert.Multiple(() =>
        {
            Assert.That(OreStacks(sim, character).Select(s => (s.Id, s.Stack.Count)), Is.EqualTo(new[] { (full, ItemLedger.StackMax), (half, 50_000) }));
            Assert.That(Items.Rows(sim)[^1].FromAfter, Is.EqualTo(0), "the stack merged away is emptied, and destroyed");
        });
        AuditsOk(sim, "after the merge");

        Move(link, half, OreStacks(sim, character)[0].Stack.Slot, 50_000);
        SessionHarness.Until(() => Refused(link, TatooineReplication.MoveBad) == 1, "a merge onto a full stack refused");

        rows = Items.Rows(sim).Count;
        var far = LastFreeSlot(sim, character);
        Move(link, half, far, 50_000);
        SessionHarness.Until(() => sim.Bridge.GridMoves == 1, "the move applied");
        Breathe();
        Assert.Multiple(() =>
        {
            Assert.That(OreStacks(sim, character).Single(s => s.Id == half).Stack.Slot, Is.EqualTo(far));
            Assert.That(Items.Rows(sim), Has.Count.EqualTo(rows), "a whole move is not a row");
        });

        var blaster = StageBlaster(sim, character);
        rows = Items.Rows(sim).Count;
        var other = Items.Stacks(sim, character).Single(s => s.Id == blaster);
        Move(link, half, other.Stack.Slot, 50_000);
        SessionHarness.Until(() => sim.Bridge.GridSwaps == 1, "the swap applied");
        Breathe();
        Assert.Multiple(() =>
        {
            Assert.That(OreStacks(sim, character).Single(s => s.Id == half).Stack.Slot, Is.EqualTo(other.Stack.Slot));
            Assert.That(Items.Stacks(sim, character).Single(s => s.Id == other.Id).Stack.Slot, Is.EqualTo(far));
            Assert.That(Items.Rows(sim), Has.Count.EqualTo(rows), "a slot change is not a row");
        });
        AuditsOk(sim, "after the move and the swap");
    }

    /// <summary>
    /// A merge past what the stack there holds moves what fits, one row with the rest left where it was; part of a crate is neither moved nor thrown away
    /// — a crate splits by an unpack — and a move or a discard in a Workshop pass that does not commit is refused, its grid read again, nothing of it
    /// written.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void APartialMerge_LeavesTheRest_ACrateDoesNotSplit_AndAFailedPassWritesNothing()
    {
        using var sim = Start();
        var (link, character) = LogIn(sim, 0xC603);
        StageOre(sim, character, ItemLedger.StackMax + 30_000);
        var ore = OreStacks(sim, character);
        var (full, rest) = (ore[0].Id, ore[1].Id);
        Discard(link, full, 10_000);
        SessionHarness.Until(() => OreStacks(sim, character).Single(s => s.Id == full).Stack.Count == ItemLedger.StackMax - 10_000, "room for 10 000");
        Breathe();

        Move(link, rest, SlotOf(sim, character, full), 30_000);
        SessionHarness.Until(() => sim.Bridge.GridMerges == 1, "the partial merge");
        Breathe();
        var row = Items.Rows(sim)[^1];
        Assert.Multiple(() =>
        {
            Assert.That(OreStacks(sim, character).Select(s => (s.Id, s.Stack.Count)), Is.EqualTo(new[] { (full, ItemLedger.StackMax), (rest, 20_000) }));
            Assert.That((row.Kind, row.Count, row.FromAfter, row.ToAfter), Is.EqualTo((ItemMoveKind.Move, 10_000, 20_000, ItemLedger.StackMax)));
        });

        var a = StageBlaster(sim, character);
        var b = StageBlaster(sim, character);
        link.SendCommand(nameof(PackItem), new RecordValues { ["item"] = FieldValue.OfUInt64(a.RawValue), ["into"] = FieldValue.OfUInt64(b.RawValue) });
        SessionHarness.Until(() => sim.Bridge.Packs == 1, "a crate of two");
        Breathe();
        Discard(link, b, 1);
        Breathe();
        Move(link, b, 0, 1);
        SessionHarness.Until(() => Refused(link, TatooineReplication.DiscardBad) == 1 && Refused(link, TatooineReplication.MoveBad) == 1, "part of a crate");

        var rows = Items.Rows(sim).Count;
        var reread = sim.Bridge.ItemOwnersRereadForChecks;
        Breathe();
        sim.Bridge.FailNextWorkshopWriteForChecks();
        Move(link, rest, 0, 5_000);
        SessionHarness.Until(() => Refused(link, TatooineReplication.CraftFailed) == 1, "the split refused with its pass");
        Breathe();
        Assert.Multiple(() =>
        {
            Assert.That(Items.Rows(sim), Has.Count.EqualTo(rows), "a split that never committed wrote a row");
            Assert.That(OreStacks(sim, character).Single(s => s.Id == rest).Stack.Count, Is.EqualTo(20_000));
            Assert.That(sim.Bridge.GridSplits, Is.Zero);
        });

        Move(link, rest, 0, 5_000);
        SessionHarness.Until(() => sim.Bridge.GridSplits == 1, "the split, again");
        Assert.That(sim.Bridge.ItemOwnersRereadForChecks - reread, Is.EqualTo(1), "the grid the failed pass touched is read again, once, at its next use");
        AuditsOk(sim, "after the merge, the crate and the failed pass");
    }

    /// <summary>
    /// Part of a stack thrown away, then an item with stats whole: a <c>discard</c> row each, to World, the units destroyed; part of an item, a count past
    /// the stack, a move of part of an item or to a slot out of the grid refused.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void Discard_DestroysToWorld_AndBadShapesAreRefused()
    {
        using var sim = Start();
        var (link, character) = LogIn(sim, 0xC602);
        var ore = StageOre(sim, character, 1_000);
        var destroyed = sim.Bridge.GridDiscards;

        Discard(link, ore, 400);
        SessionHarness.Until(() => sim.Bridge.GridDiscards == destroyed + 1, "the discard applied");
        Breathe();
        Assert.Multiple(() =>
        {
            Assert.That(OreStacks(sim, character).Single(s => s.Id == ore).Stack.Count, Is.EqualTo(600));
            var row = Items.Rows(sim)[^1];
            Assert.That((row.Kind, row.To, row.Count, row.FromAfter), Is.EqualTo((ItemMoveKind.Discard, ItemLedger.World, 400, 600)));
        });

        var staged = StageBlaster(sim, character);
        var item = Items.Stacks(sim, character).Single(s => s.Id == staged);
        Discard(link, item.Id, 1);
        SessionHarness.Until(() => sim.Bridge.GridDiscards == destroyed + 2, "the item thrown away");
        Breathe();
        Assert.That(Items.Stacks(sim, character).Any(s => s.Id == item.Id), Is.False, "the item is gone");
        AuditsOk(sim, "after the discards");

        Discard(link, ore, 601);
        Move(link, ore, ItemLedger.Slots + 1, 1);
        Move(link, ore, 0, 0);
        SessionHarness.Until(
            () => Refused(link, TatooineReplication.DiscardBad) == 1 && Refused(link, TatooineReplication.MoveBad) == 2,
            "the bad shapes refused");
        Assert.That(OreStacks(sim, character).Single(s => s.Id == ore).Stack.Count, Is.EqualTo(600), "nothing of them was written");
        AuditsOk(sim, "after the refusals");
    }

    /// <summary>
    /// "Sort &amp; stack" (Loïc, 2026-11-04): Ore in three stacks — 100 000, 20 000, and 30 000 split off to the last slot — a Hide stack and a blaster. The
    /// two part stacks merge into the first of them (one <c>move</c> row), the full one is left alone, the blaster never merges, and the grid is laid out
    /// from slot 1 with no gap, by type, name and mark: the Hide before the Ore, the materials before the weapon.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void SortAndStack_MergesTheSameUnits_AndOrdersByTypeNameMark()
    {
        using var sim = Start();
        var (link, character) = LogIn(sim, 0xC603);
        StageOre(sim, character, 150_000);
        var half = OreStacks(sim, character)[1].Id;
        Move(link, half, LastFreeSlot(sim, character), 30_000);
        SessionHarness.Until(() => sim.Bridge.GridSplits == 1, "the split applied");
        Breathe();
        var blaster = StageBlaster(sim, character);
        var hides = Items.Stacks(sim, character).Count(s => s.Stack.Template == ItemCatalog.Hide);
        sim.Bridge.StageTransfer(ItemLedger.World, unchecked((long)character.RawValue), ItemCatalog.Hide, 1, 500, ItemMoveKind.Kill, 2);
        SessionHarness.Until(() => Items.Stacks(sim, character).Count(s => s.Stack.Template == ItemCatalog.Hide) == hides + 1, "the staged hide");
        AuditsOk(sim, "before the sort");

        var rows = Items.Rows(sim).Count;
        Sort(link);
        SessionHarness.Until(() => sim.Bridge.GridSorts == 1, "the sort applied");
        Breathe();

        var grid = Items.Stacks(sim, character).Where(s => s.Stack.Slot <= ItemLedger.Slots).OrderBy(s => s.Stack.Slot).ToArray();
        var keys = grid
            .Select(s => ItemCatalog.TryGet(s.Stack.Template, out var t) ? (t.Category, t.Name, s.Stack.Mk) : (byte.MaxValue, "", s.Stack.Mk))
            .ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(OreStacks(sim, character).Select(s => s.Stack.Count), Is.EqualTo(new[] { ItemLedger.StackMax, 50_000 }), "the part stacks merged");
            Assert.That(grid.Select(s => (int)s.Stack.Slot), Is.EqualTo(Enumerable.Range(1, grid.Length)), "laid out from slot 1, no gap");
            Assert.That(
                keys.Zip(keys.Skip(1)).All(p => p.First.Item1 < p.Second.Item1
                    || (p.First.Item1 == p.Second.Item1 && string.CompareOrdinal(p.First.Item2, p.Second.Item2) < 0)
                    || (p.First.Item1 == p.Second.Item1 && p.First.Item2 == p.Second.Item2 && p.First.Item3 <= p.Second.Item3)),
                Is.True,
                $"ordered by type, name, mark: {string.Join(", ", keys)}");
            Assert.That(
                grid.Select(s => s.Stack.Template).ToList().IndexOf(ItemCatalog.Hide),
                Is.LessThan(grid.Select(s => s.Stack.Template).ToList().IndexOf(Ore)),
                "the Hide before the Ore");
            Assert.That(grid.Any(s => s.Id == blaster), Is.True, "the blaster kept, never merged");
            var row = Items.Rows(sim).Skip(rows).Single();
            Assert.That((row.Kind, row.From == row.To, row.Count, row.ToAfter), Is.EqualTo((ItemMoveKind.Move, true, 30_000, 50_000)), "one merge row");
        });
        AuditsOk(sim, "after the sort");
    }
}
