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
/// The market desk (bazaar.md § 3, BZ-1…BZ-11): list, buy, cancel, collect and vendor through the real commands, each a ledger row of both ledgers, the
/// one buyer of a listing settled by the desk's order, and the market audit naming every rule a corruption breaks.
/// </summary>
/// <remarks>
/// Inventories are filled by staged transfers through the real <c>ItemTransfer</c> (<see cref="SimBridge.StageTransfer"/>). Players are parked at the bazaar
/// beside planet 0's first shuttleport (home) or at one of another city (elsewhere), or in the far south-west, away from every terminal.
/// </remarks>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
internal sealed class MarketDeskChecks : ServedChecks
{
    private const ushort Hide = ItemCatalog.Hide;
    private const ushort Bone = ItemCatalog.Bone;

    private TatooineSim Start(bool persist = false)
    {
        var config = Worlds.Served(Dir);
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

    /// <summary>A player logged in, parked at a point, its wallet open.</summary>
    private Trader LogIn(TatooineSim sim, ulong account, (float X, float Z) at, long bindings)
    {
        var link = Harness.Connect(TatooineReplication.PlayerKind, EntityId.FromRawValue(account));
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= bindings, "the avatar to be spawned");
        Harness.Ticks(2);
        var body = Worlds.Park(sim, link.Session, at.X, at.Z);
        Assert.That(CharacterStore.TryFind(sim.Dbe, EntityId.FromRawValue(account), out var character, out _), Is.True, "the account has no character");
        SessionHarness.Until(() => Ledgers.TryWallet(sim, character, out _), "the wallet to be opened");
        Harness.Ticks(2);
        return new Trader(link, body, character);
    }

    private static MissionTerminalRef Home(TatooineSim sim)
    {
        Assert.That(sim.Index.Bazaars, Is.Not.Empty, "the premise: the world has a bazaar");
        return sim.Index.Bazaars[0];
    }

    private static MissionTerminalRef Elsewhere(TatooineSim sim)
    {
        var home = Home(sim);
        var other = sim.Index.Bazaars.FindIndex(b => b.Realm == home.Realm && b.Region != home.Region);
        Assert.That(other, Is.GreaterThanOrEqualTo(0), "the premise: a bazaar in a second city");
        return sim.Index.Bazaars[other];
    }

    private static (float X, float Z) At(MissionTerminalRef bazaar) => (bazaar.X + 2.5f, bazaar.Z);

    private static (float X, float Z) Away(TatooineSim sim)
    {
        var reach = sim.Config.WorldEdgeM * 0.5f * WorldBuilder.InsideEdge * 0.9f;
        return (-reach, -reach);
    }

    private void MoveTo(TatooineSim sim, Trader t, (float X, float Z) at)
    {
        Worlds.Park(sim, t.Body, at.X, at.Z);
        Harness.Ticks(2);
    }

    /// <summary>Mints units into a character's inventory through the real <c>ItemTransfer</c>, and waits for them.</summary>
    private void Mint(TatooineSim sim, Trader t, ushort template, byte mk, int count)
    {
        var want = Market.Units(sim, t.Character, template, mk) + count;
        sim.Bridge.StageTransfer(ItemLedger.World, t.Key, template, mk, count, ItemMoveKind.Kill, 1);
        SessionHarness.Until(() => Market.Units(sim, t.Character, template, mk) == want, "the staged units");
        Harness.Ticks(1);
    }

    /// <summary>Fills a character's free slots with one unit each of keys no case trades (crystal, gas, chemical, fiber, gland, venom; Mk I-IV), but <paramref name="leave"/>.</summary>
    private void Fill(TatooineSim sim, Trader t, int leave = 0)
    {
        var free = ItemLedger.Slots - Items.Stacks(sim, t.Character).Count - leave;
        ushort[] templates = [ItemCatalog.Crystal, ItemCatalog.Gas, ItemCatalog.Chemical, ItemCatalog.Fiber, ItemCatalog.Gland, ItemCatalog.Venom, ItemCatalog.Metal, ItemCatalog.CoreRanged];
        var n = 0;
        foreach (var template in templates)
        {
            for (byte mk = 1; mk <= 4 && n < free; mk++, n++)
            {
                sim.Bridge.StageTransfer(ItemLedger.World, t.Key, template, mk, 1, ItemMoveKind.Kill, 1);
            }
        }

        var want = ItemLedger.Slots - leave;
        SessionHarness.Until(() => Items.Stacks(sim, t.Character).Count == want, "the inventory to be filled");
        Harness.Ticks(1);
    }

    private static int Count(FakeLink link, byte reason) => Ledgers.Refusals(link).Count(r => r == reason);

    /// <summary>Waits for one more refusal of <paramref name="reason"/> on the link than it had.</summary>
    private void Refused(FakeLink link, byte reason, int before, string what)
    {
        SessionHarness.Until(() => Count(link, reason) > before, what);
        Harness.Ticks(2);
    }

    /// <summary>The one listing a character has that matches, or fails.</summary>
    private static (long Id, Listing L) ListingOf(TatooineSim sim, Trader seller, Func<Listing, bool> keep = null)
    {
        var mine = Market.Listings(sim).Where(kv => kv.Value.Seller == seller.Key && (keep == null || keep(kv.Value))).ToList();
        Assert.That(mine, Has.Count.EqualTo(1), "not exactly one listing of the seller's");
        return (mine[0].Key, mine[0].Value);
    }

    /// <summary>Lists and waits for the listing; a second for the rate bucket first.</summary>
    private (long Id, Listing L) ListAndWait(TatooineSim sim, Trader seller, ushort template, byte mk, int quantity, uint unitPrice)
    {
        var before = Market.Listings(sim).Keys.ToHashSet();
        Market.List(seller.Link, template, mk, quantity, unitPrice);
        SessionHarness.Until(() => Market.Listings(sim).Keys.Any(k => !before.Contains(k)), "the listing");
        Harness.Ticks(2);
        var id = Market.Listings(sim).Keys.First(k => !before.Contains(k));
        return (id, Market.Listings(sim)[id]);
    }

    /// <summary>A rate bucket's worth of patience: two commands a second, a burst of four.</summary>
    private void Breathe() => Harness.Ticks(Worlds.ServedHz / 2 + 1);

    private static void AuditsOk(TatooineSim sim, string when)
    {
        var (ok, why) = Market.Audits(sim);
        Assert.That(ok, Is.True, $"{when}: {why}");
    }

    // ── BZ-1: listing ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// BZ-1: a list at a bazaar moves the units into a new listing — one <c>list</c> row per source stack, highest slot first, the listing's quantity their
    /// count — and charges max(1, ⌊1 %⌋) as one <c>listfee</c> row, in one commit; the listing's city is the terminal's.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void AListing_MovesItsUnitsIntoEscrow_AndPaysItsFee()
    {
        using var sim = Start();
        var home = Home(sim);
        var a = LogIn(sim, 0x8101, At(home), 1);
        Mint(sim, a, Hide, 2, 110);
        var balance = Ledgers.Balance(sim, a.Character);
        var itemRows = Items.Rows(sim).Count;
        var creditRows = Ledgers.Rows(sim).Count;
        var rev = Items.ItemsRev(sim, a.Body);

        var (id, l) = ListAndWait(sim, a, Hide, 2, 99, 7);
        var rows = Items.Rows(sim).Skip(itemRows).ToList();
        var credits = Ledgers.Rows(sim).Skip(creditRows).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(l.State, Is.EqualTo(ListingState.Listed));
            Assert.That(l.Seller, Is.EqualTo(a.Key));
            Assert.That(l.Collector, Is.Zero);
            Assert.That((l.Quantity, l.UnitPrice, l.Template, l.Mk), Is.EqualTo((99, 7L, Hide, (byte)2)));
            Assert.That((l.City, l.Realm), Is.EqualTo(((ushort)home.Region, home.Realm)), "the listing's city is not the terminal's");
            Assert.That(l.ExpireTick - l.ListedTick, Is.EqualTo((long)TatooineData.ListingLifeHours * 3600 * Worlds.ServedHz));
            Assert.That(rows, Has.Count.EqualTo(2), "one row per source stack: 11 from the second, 88 from the first");
            Assert.That(rows.Select(r => r.Count), Is.EqualTo(new[] { 11, 88 }), "not the highest slot first");
            Assert.That(rows.All(r => r.Kind == ItemMoveKind.List && r.From == a.Key && r.To == id && r.ToStack == id && r.Ref == id), Is.True);
            Assert.That(rows.Select(r => r.ToAfter), Is.EqualTo(new[] { 11, 99 }), "the escrow's count after");
            Assert.That((l.LastSeq, l.Rows), Is.EqualTo((rows[^1].Seq, 2)));
            Assert.That(Market.Units(sim, a.Character, Hide, 2), Is.EqualTo(11));
            Assert.That(credits, Has.Count.EqualTo(1), "one fee row");
            Assert.That((credits[0].Kind, credits[0].From, credits[0].To, credits[0].Amount, credits[0].Ref),
                Is.EqualTo((TransferKind.ListFee, a.Key, EconomyLedger.World, 6L, id)), "max(1, 1 % of 693) is 6");
            Assert.That(rows[0].WorldTick, Is.EqualTo(credits[0].WorldTick), "the escrow and the fee are not one commit");
            Assert.That(l.Fee, Is.EqualTo(6));
            Assert.That(Ledgers.Balance(sim, a.Character), Is.EqualTo(balance - 6));
            Assert.That(Items.ItemsRev(sim, a.Body), Is.GreaterThan(rev), "the seller's items revision did not move");
            Assert.That(sim.Bridge.MarketListingsCreated, Is.EqualTo(1));
            Assert.That(sim.Bridge.MarketListFees, Is.EqualTo(6));
            Assert.That(sim.Bridge.MarketEscrowUnits, Is.EqualTo(99));
            Assert.That(sim.Bridge.ItemsHeld(Hide, 2), Is.EqualTo(11), "escrow counted as held in the inventory counters");
        });
        AuditsOk(sim, "after the listing");

        // The fee's floor: a listing worth 1 credit still pays 1.
        Breathe();
        var (_, cheap) = ListAndWait(sim, a, Hide, 2, 1, 1);
        Assert.That(cheap.Fee, Is.EqualTo(1), "the list fee's minimum");
    }

    // ── BZ-2: every refusal ─────────────────────────────────────────────────────────────────────────────────────────

    private string State(TatooineSim sim, params Trader[] traders)
    {
        var parts = new List<string> { $"credits {Ledgers.Rows(sim).Count}", $"items {Items.Rows(sim).Count}" };
        foreach (var (id, l) in Market.Listings(sim).OrderBy(kv => kv.Key))
        {
            parts.Add($"{id}:{l.State}/{l.Collector}/{l.Quantity}/{l.LastSeq}");
        }

        foreach (var t in traders)
        {
            parts.Add($"{t.Key}:{Ledgers.Balance(sim, t.Character)}:{string.Join(",", Items.Stacks(sim, t.Character).Select(s => $"{s.Stack.Template}.{s.Stack.Mk}x{s.Stack.Count}"))}");
        }

        return string.Join(" | ", parts);
    }

    /// <summary>
    /// BZ-2: every refusal of § 3.1, each with its code, writes nothing — rows, balances, stacks and listings unchanged. <c>NotInWorld</c> is the mission
    /// desk's, reused; <c>MarketFailed</c> is staged as a write failing after validation.
    /// </summary>
    [Test]
    [CancelAfter(240_000)]
    public void EveryRefusal_HasItsReason_AndWritesNothing()
    {
        using var sim = Start();
        var home = Home(sim);
        var elsewhere = Elsewhere(sim);
        var a = LogIn(sim, 0x8201, At(home), 1);
        var b = LogIn(sim, 0x8202, At(elsewhere), 2);
        Mint(sim, a, Hide, 2, 30);
        Mint(sim, a, Hide, 1, 1);
        Mint(sim, a, Bone, 3, 3);
        var pricey = ListAndWait(sim, a, Hide, 2, 30, 100).Id;

        void Expect(Trader who, byte reason, string what, Action send)
        {
            var before = State(sim, a, b);
            var n = Count(who.Link, reason);
            Breathe();
            send();
            Refused(who.Link, reason, n, what);
            Assert.That(State(sim, a, b), Is.EqualTo(before), $"{what}: the refusal wrote something");
        }

        // Lists.
        MoveTo(sim, a, Away(sim));
        Expect(a, TatooineReplication.MarketNotAtBazaar, "a list away from a bazaar", () => Market.List(a.Link, Bone, 3, 1, 10));
        MoveTo(sim, a, At(home));
        Expect(a, TatooineReplication.MarketBadListing, "a list of no units", () => Market.List(a.Link, Bone, 3, 0, 10));
        Expect(a, TatooineReplication.MarketBadListing, "a list at no price", () => Market.List(a.Link, Bone, 3, 1, 0));
        Expect(a, TatooineReplication.MarketBadListing, "a list of a Mk the template refuses", () => Market.List(a.Link, Bone, 0, 1, 10));
        Expect(a, TatooineReplication.MarketBadListing, "a list of more than a stack", () => Market.List(a.Link, Bone, 3, 100, 10));
        Expect(a, TatooineReplication.MarketBadListing, "a list above the highest price", () => Market.List(a.Link, Bone, 3, 1, (uint)TatooineData.MaxUnitPrice + 1));
        Expect(a, TatooineReplication.MarketNoItems, "a list of more than held", () => Market.List(a.Link, Bone, 3, 4, 10));
        Expect(a, TatooineReplication.MarketNotEnough, "a list whose fee the wallet does not cover", () => Market.List(a.Link, Bone, 3, 3, 1_000_000));
        sim.Bridge.SetMarketCapsForChecks(1, 1);
        Expect(a, TatooineReplication.MarketListingCap, "a list past the cap", () => Market.List(a.Link, Bone, 3, 1, 10));
        sim.Bridge.SetMarketCapsForChecks(0, 1);

        // Buys.
        Expect(a, TatooineReplication.MarketOwnListing, "a buy of one's own listing", () => Market.Buy(a.Link, pricey, 100));
        Expect(b, TatooineReplication.MarketSold, "a buy at another price", () => Market.Buy(b.Link, pricey, 99));
        Expect(b, TatooineReplication.MarketSold, "a buy of no listing", () => Market.Buy(b.Link, 12345, 100));
        Expect(b, TatooineReplication.MarketNotEnough, "a buy the wallet does not cover", () => Market.Buy(b.Link, pricey, 100));
        MoveTo(sim, b, Away(sim));
        Expect(b, TatooineReplication.MarketNotAtBazaar, "a buy away from a bazaar", () => Market.Buy(b.Link, pricey, 100));
        MoveTo(sim, b, At(elsewhere));

        // Cancels.
        Expect(b, TatooineReplication.MarketNotYours, "a cancel of someone else's listing", () => Market.Cancel(b.Link, pricey));

        // A cheap listing bought from elsewhere: reserved for b at home.
        Breathe();
        var cheap = ListAndWait(sim, a, Bone, 3, 1, 10).Id;
        Breathe();
        Market.Buy(b.Link, cheap, 10);
        SessionHarness.Until(() => Market.Listings(sim)[cheap].State == ListingState.Sold, "the buy from elsewhere");
        Harness.Ticks(2);
        Breathe();
        var other = ListAndWait(sim, a, Bone, 3, 1, 10).Id;
        Expect(b, TatooineReplication.MarketReservedCap, "a buy past the reservation cap", () => Market.Buy(b.Link, other, 10));
        Expect(a, TatooineReplication.MarketSold, "a cancel of a sold listing", () => Market.Cancel(a.Link, cheap));
        Expect(b, TatooineReplication.MarketWrongCity, "a collect at another city's bazaar", () => Market.Collect(b.Link, cheap));
        Expect(a, TatooineReplication.MarketNotYours, "a collect of someone else's purchase", () => Market.Collect(a.Link, cheap));
        MoveTo(sim, b, Away(sim));
        Expect(b, TatooineReplication.MarketNotAtBazaar, "a collect away from a bazaar", () => Market.Collect(b.Link, cheap));
        MoveTo(sim, b, At(home));
        Fill(sim, b);
        Expect(b, TatooineReplication.MarketNoRoom, "a collect into a full inventory", () => Market.Collect(b.Link, cheap));
        Expect(b, TatooineReplication.MarketNoRoom, "a collect of all into a full inventory", () => Market.Collect(b.Link, 0));

        // Vendor sales.
        Expect(a, TatooineReplication.MarketWorthless, "a vendor sale worth nothing", () => Market.Vendor(a.Link, Hide, 1, 1));
        Expect(a, TatooineReplication.MarketNoItems, "a vendor sale of more than held", () => Market.Vendor(a.Link, Hide, 1, 2));
        Expect(a, TatooineReplication.MarketBadListing, "a vendor sale of no item", () => Market.Vendor(a.Link, 0, 1, 1));
        MoveTo(sim, a, Away(sim));
        Expect(a, TatooineReplication.MarketNotAtBazaar, "a vendor sale away from a bazaar", () => Market.Vendor(a.Link, Bone, 3, 1));
        MoveTo(sim, a, At(home));

        // A write failing after its validation passed: disposed uncommitted, refused MarketFailed, counted.
        sim.Bridge.FailNextMarketWriteForChecks();
        Expect(a, TatooineReplication.MarketFailed, "a list whose write failed", () => Market.List(a.Link, Bone, 3, 1, 10));
        Assert.Multiple(() =>
        {
            Assert.That(sim.Bridge.MarketAborted, Is.EqualTo(1));
            Assert.That(sim.Bridge.MarketRefused(TatooineReplication.MarketNoRoom), Is.EqualTo(2));
            Assert.That(sim.Bridge.MarketRefused(TatooineReplication.MarketSold), Is.EqualTo(3));
        });

        // And the desk carries on after it: the same list again is applied.
        Breathe();
        ListAndWait(sim, a, Bone, 3, 1, 10);

        // NotInWorld: a command from a session whose wallet is not open yet — its first login, sent before its avatar is bound, so the desk handles it on
        // the bind's tick at the latest, while the wallet opens only on a later one. A fresh account per try: a command that arrives a tick late is answered
        // NotYours instead, and the try is made again.
        var notInWorld = false;
        for (var i = 0; i < 5 && !notInWorld; i++)
        {
            var before = State(sim, a, b);
            var link = Harness.Connect(TatooineReplication.PlayerKind, EntityId.FromRawValue(0x8210UL + (ulong)i));
            SessionHarness.Until(() => link.Sent.Any(m => m.Length > 0 && m[0] == MessageTypes.Welcome), "the WELCOME");
            Market.Cancel(link, 12345);
            SessionHarness.Until(
                () => Count(link, TatooineReplication.NotInWorld) + Count(link, TatooineReplication.MarketNotYours) > 0, "the cancel to be answered");
            notInWorld = Count(link, TatooineReplication.NotInWorld) == 1;
            Assert.That(State(sim, a, b), Is.EqualTo(before), "a refused command from a session not in the world wrote something");
        }

        Assert.That(notInWorld, Is.True, "five first logins and none was refused NotInWorld");
        AuditsOk(sim, "after every refusal");
    }

    /// <summary>
    /// BZ-2, B9: a returned listing holds its seller's listing slot until it is collected — listing, cancelling and listing again does not grow the book past
    /// the cap.
    /// </summary>
    [Test]
    [CancelAfter(180_000)]
    public void AReturnedListing_HoldsItsSlot_UntilCollected()
    {
        using var sim = Start();
        var home = Home(sim);
        var a = LogIn(sim, 0x8221, At(home), 1);
        Mint(sim, a, Hide, 2, 10);
        sim.Bridge.SetMarketCapsForChecks(2, 0);
        var (one, _) = ListAndWait(sim, a, Hide, 2, 1, 10);
        ListAndWait(sim, a, Hide, 2, 1, 10);
        MoveTo(sim, a, Away(sim));
        Market.Cancel(a.Link, one);
        SessionHarness.Until(() => Market.Listings(sim)[one].State == ListingState.Returned, "the cancel from afar");
        MoveTo(sim, a, At(home));
        var capped = Count(a.Link, TatooineReplication.MarketListingCap);
        Breathe();
        Market.List(a.Link, Hide, 2, 1, 10);
        Refused(a.Link, TatooineReplication.MarketListingCap, capped, "a list while a returned listing holds the slot");
        Assert.That(Market.Listings(sim), Has.Count.EqualTo(2), "a listing past the cap");

        Breathe();
        Market.Collect(a.Link, one);
        SessionHarness.Until(() => !Market.Listings(sim).ContainsKey(one), "the return to be collected");
        Harness.Ticks(2);
        Breathe();
        ListAndWait(sim, a, Hide, 2, 1, 10);
        sim.Bridge.SetMarketCapsForChecks(0, 0);
        AuditsOk(sim, "after the slots");
    }

    // ── BZ-3: one buyer ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// BZ-3 (E5-1 / M5-1 in process): 100 sessions buy one listing at once → exactly one <c>sale</c> row, 99 <c>MarketSold</c>; again across two ticks;
    /// credits and units conserved (the three audits).
    /// </summary>
    [Test]
    [CancelAfter(300_000)]
    public void HundredBuyersOneListing_ExactlyOneWins()
    {
        const int buyers = 100;
        using var sim = Start();
        var home = Home(sim);
        var elsewhere = Elsewhere(sim);
        var seller = LogIn(sim, 0x8300, At(home), 1);
        Mint(sim, seller, Hide, 2, 6);
        var links = new List<FakeLink>();
        for (var i = 0; i < buyers; i++)
        {
            links.Add(Harness.Connect(TatooineReplication.PlayerKind, EntityId.FromRawValue(0x8301UL + (ulong)i)));
        }

        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= buyers + 1, "every buyer's avatar");
        Harness.Ticks(2);
        var characters = new List<EntityId>();
        for (var i = 0; i < buyers; i++)
        {
            // At another city's bazaar: the winner's purchase is reserved, so the listing stays in the book, Sold, for every later buyer to find.
            Worlds.Park(sim, links[i].Session, elsewhere.X + 2.5f + (i % 10 * 0.25f), elsewhere.Z + (i / 10 * 0.25f));
            Assert.That(CharacterStore.TryFind(sim.Dbe, EntityId.FromRawValue(0x8301UL + (ulong)i), out var c, out _), Is.True);
            characters.Add(c);
        }

        SessionHarness.Until(() => characters.All(c => Ledgers.TryWallet(sim, c, out _)), "every buyer's wallet");
        Harness.Ticks(2);

        void Race(long listing, bool split)
        {
            var sales = Ledgers.Rows(sim).Count(r => r.Kind == TransferKind.Sale);
            var sold = links.Sum(l => Count(l, TatooineReplication.MarketSold));
            for (var i = 0; i < buyers; i++)
            {
                if (split && i == buyers / 2)
                {
                    Harness.Ticks(1);
                }

                Market.Buy(links[i], listing, 10);
            }

            SessionHarness.Until(() => links.Sum(l => Count(l, TatooineReplication.MarketSold)) - sold >= buyers - 1, "the losers to be told");
            Harness.Ticks(3);
            var rows = Ledgers.Rows(sim).Where(r => r.Kind == TransferKind.Sale && r.Ref == listing).ToList();
            Assert.Multiple(() =>
            {
                Assert.That(rows, Has.Count.EqualTo(1), "not exactly one sale");
                Assert.That(Ledgers.Rows(sim).Count(r => r.Kind == TransferKind.Sale), Is.EqualTo(sales + 1));
                Assert.That(links.Sum(l => Count(l, TatooineReplication.MarketSold)) - sold, Is.EqualTo(buyers - 1), "not 99 told it was sold");
                var l = Market.Listings(sim)[listing];
                Assert.That((l.State, l.Collector), Is.EqualTo((ListingState.Sold, rows[0].From)), "not reserved for the one buyer who paid");
                Assert.That(characters.Count(c => unchecked((long)c.RawValue) == rows[0].From), Is.EqualTo(1), "the payer is not one of the buyers");
            });
        }

        // One tick: the 100 buys are sent back to back, and the desk counts the most buys one of its ticks handled. A race whose buys straddled two ticks is
        // still a race the desk must settle — checked all the same — but not the one-tick case, so it is run again with a new listing, at most four times.
        var oneTick = false;
        for (var attempt = 0; attempt < 4 && !oneTick; attempt++)
        {
            Breathe();
            sim.Bridge.MostBuysInATickForChecks = 0;
            var first = ListAndWait(sim, seller, Hide, 2, 1, 10).Id;
            Race(first, split: false);
            oneTick = sim.Bridge.MostBuysInATickForChecks == buyers;
        }

        Assert.That(oneTick, Is.True, "four races and none had its 100 buys in one desk tick");
        Breathe();
        var second = ListAndWait(sim, seller, Hide, 2, 1, 10).Id;
        Breathe();
        Race(second, split: true);
        Assert.That(sim.Bridge.MarketSales, Is.EqualTo(2));
        AuditsOk(sim, "after the races");
    }

    // ── BZ-4: buying ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// BZ-4: a buy pays <c>UnitPrice × Quantity</c> buyer → seller and ⌊8 %⌋ seller → World; made away from the listing's city it reserves (Sold, collector
    /// the buyer); collected at that city it delivers; made at a terminal of that city with room it delivers at once.
    /// </summary>
    [Test]
    [CancelAfter(180_000)]
    public void ABuyElsewhere_Reserves_ABuyThere_Delivers()
    {
        using var sim = Start();
        var home = Home(sim);
        var elsewhere = Elsewhere(sim);
        var a = LogIn(sim, 0x8401, At(home), 1);
        var b = LogIn(sim, 0x8402, At(elsewhere), 2);
        Mint(sim, a, Hide, 2, 40);
        var (id, _) = ListAndWait(sim, a, Hide, 2, 20, 30);
        var aBefore = Ledgers.Balance(sim, a.Character);
        var bBefore = Ledgers.Balance(sim, b.Character);

        Market.Buy(b.Link, id, 30);
        SessionHarness.Until(() => Market.Listings(sim)[id].State == ListingState.Sold, "the buy from elsewhere");
        Harness.Ticks(2);
        var l = Market.Listings(sim)[id];
        var credits = Ledgers.Rows(sim).Where(r => r.Ref == id && r.Kind is TransferKind.Sale or TransferKind.Tax).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(l.Collector, Is.EqualTo(b.Key));
            Assert.That(l.ClosedTick, Is.GreaterThan(0));
            Assert.That(credits.Select(r => (r.Kind, r.From, r.To, r.Amount)), Is.EqualTo(new[]
            {
                (TransferKind.Sale, b.Key, a.Key, 600L),
                (TransferKind.Tax, a.Key, EconomyLedger.World, 48L),
            }));
            Assert.That(Ledgers.Balance(sim, b.Character), Is.EqualTo(bBefore - 600));
            Assert.That(Ledgers.Balance(sim, a.Character), Is.EqualTo(aBefore + 600 - 48));
            Assert.That(Market.Units(sim, b.Character, Hide, 2), Is.Zero, "delivered away from the listing's city");
            Assert.That(sim.Bridge.MarketSoldNow, Is.EqualTo(1));
            Assert.That(sim.Bridge.MarketVolume, Is.EqualTo(600));
            Assert.That(sim.Bridge.MarketSalesTax, Is.EqualTo(48));
        });
        AuditsOk(sim, "after the reserving buy");

        MoveTo(sim, b, At(home));
        Market.Collect(b.Link, id);
        SessionHarness.Until(() => !Market.Listings(sim).ContainsKey(id), "the collect");
        Harness.Ticks(2);
        var buyRows = Items.Rows(sim).Where(r => r.Kind == ItemMoveKind.Buy && r.Ref == id).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(Market.Units(sim, b.Character, Hide, 2), Is.EqualTo(20));
            Assert.That(buyRows.Sum(r => r.Count), Is.EqualTo(20));
            Assert.That(buyRows.All(r => r.From == id && r.FromStack == id && r.To == b.Key), Is.True);
            Assert.That(buyRows[^1].FromAfter, Is.Zero, "the escrow is not emptied");
            Assert.That(sim.Bridge.MarketCollected, Is.EqualTo(1));
        });

        // At home with room: bought and handed over in one transaction.
        Breathe();
        var (here, _) = ListAndWait(sim, a, Hide, 2, 2, 5);
        Breathe();
        Market.Buy(b.Link, here, 5);
        SessionHarness.Until(() => !Market.Listings(sim).ContainsKey(here), "the buy at the listing's city");
        Harness.Ticks(2);
        var sale = Ledgers.Rows(sim).Single(r => r.Kind == TransferKind.Sale && r.Ref == here);
        var delivered = Items.Rows(sim).Where(r => r.Kind == ItemMoveKind.Buy && r.Ref == here).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(Market.Units(sim, b.Character, Hide, 2), Is.EqualTo(22));
            Assert.That(delivered, Is.Not.Empty);
            Assert.That(delivered[0].WorldTick, Is.EqualTo(sale.WorldTick), "the sale and the delivery are not one transaction");
            Assert.That(Ledgers.Rows(sim).Any(r => r.Kind == TransferKind.Tax && r.Ref == here), Is.False, "⌊8 % of 10⌋ is 0, which writes no row");
        });
        AuditsOk(sim, "after the buys");
    }

    /// <summary>BZ-4: a seller who logged out is paid all the same — the wallet is the character's, not the body's.</summary>
    [Test]
    [CancelAfter(180_000)]
    public void AnOfflineSeller_IsPaid()
    {
        using var sim = Start();
        var home = Home(sim);
        var a = LogIn(sim, 0x8411, At(home), 1);
        var b = LogIn(sim, 0x8412, At(home), 2);
        Mint(sim, a, Bone, 2, 5);
        var (id, _) = ListAndWait(sim, a, Bone, 2, 5, 40);
        var aBefore = Ledgers.Balance(sim, a.Character);
        a.Link.Close(CloseCodes.Normal, "logging out");
        SessionHarness.Until(() => TatooineReplication.Intents.Releases >= 1, "the seller's body to be released");
        Harness.Ticks(3);

        Market.Buy(b.Link, id, 40);
        SessionHarness.Until(() => !Market.Listings(sim).ContainsKey(id), "the buy");
        Harness.Ticks(2);
        Assert.Multiple(() =>
        {
            Assert.That(Ledgers.Balance(sim, a.Character), Is.EqualTo(aBefore + 200 - 16), "the offline seller was not paid price - tax");
            Assert.That(Market.Units(sim, b.Character, Bone, 2), Is.EqualTo(5));
        });
        AuditsOk(sim, "after the offline sale");
    }

    // ── BZ-5: collecting ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// BZ-5: collect works only at a terminal of the listing's city and only for its collector; "all" collects what fits, oldest first, and leaves the rest
    /// reserved.
    /// </summary>
    [Test]
    [CancelAfter(180_000)]
    public void Collect_IsTheCollectorsAtItsCityOnly()
    {
        using var sim = Start();
        var home = Home(sim);
        var elsewhere = Elsewhere(sim);
        var a = LogIn(sim, 0x8501, At(home), 1);
        var b = LogIn(sim, 0x8502, At(elsewhere), 2);
        Mint(sim, a, Hide, 2, 1);
        Mint(sim, a, Bone, 3, 1);
        var (older, _) = ListAndWait(sim, a, Hide, 2, 1, 10);
        var (newer, _) = ListAndWait(sim, a, Bone, 3, 1, 10);
        Market.Buy(b.Link, older, 10);
        SessionHarness.Until(() => Market.Listings(sim)[older].State == ListingState.Sold, "the first buy");
        Harness.Ticks(1);
        Market.Buy(b.Link, newer, 10);
        SessionHarness.Until(() => Market.Listings(sim)[newer].State == ListingState.Sold, "the second buy");
        Harness.Ticks(2);

        var wrongCity = Count(b.Link, TatooineReplication.MarketWrongCity);
        Breathe();
        Market.Collect(b.Link, 0);
        Refused(b.Link, TatooineReplication.MarketWrongCity, wrongCity, "a collect of all at another city");

        // One free slot: the older fits, the newer — another key — does not.
        MoveTo(sim, b, At(home));
        Fill(sim, b, leave: 1);
        Breathe();
        Market.Collect(b.Link, 0);
        SessionHarness.Until(() => !Market.Listings(sim).ContainsKey(older), "the older purchase to be collected");
        Harness.Ticks(2);
        Assert.Multiple(() =>
        {
            Assert.That(Market.Units(sim, b.Character, Hide, 2), Is.EqualTo(1), "the oldest was not collected first");
            Assert.That(Market.Listings(sim)[newer].State, Is.EqualTo(ListingState.Sold), "what did not fit was not left reserved");
            Assert.That(Ledgers.Refusals(b.Link).Count(r => r == TatooineReplication.MarketNoRoom), Is.Zero, "a partial collect of all was refused");
        });
        AuditsOk(sim, "after the collects");
    }

    // ── BZ-6: cancel and expiry ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// BZ-6: a cancel returns a listed listing to its seller — reserved at its city when made away, delivered when made there with room; the fee is not
    /// refunded; an expiry does the same.
    /// </summary>
    [Test]
    [CancelAfter(180_000)]
    public void ACancel_AndAnExpiry_ReturnToTheSeller()
    {
        using var sim = Start();
        var home = Home(sim);
        var a = LogIn(sim, 0x8601, At(home), 1);
        Mint(sim, a, Hide, 2, 30);
        var (away, _) = ListAndWait(sim, a, Hide, 2, 10, 50);
        var balance = Ledgers.Balance(sim, a.Character);

        MoveTo(sim, a, Away(sim));
        Market.Cancel(a.Link, away);
        SessionHarness.Until(() => Market.Listings(sim)[away].State == ListingState.Returned, "the cancel from afar");
        Harness.Ticks(2);
        var l = Market.Listings(sim)[away];
        Assert.Multiple(() =>
        {
            Assert.That(l.Collector, Is.EqualTo(a.Key), "returned to someone else");
            Assert.That(Market.Units(sim, a.Character, Hide, 2), Is.EqualTo(20), "delivered away from its city");
            Assert.That(Ledgers.Balance(sim, a.Character), Is.EqualTo(balance), "the fee was refunded, or something else moved");
            Assert.That(sim.Bridge.MarketCancelled, Is.EqualTo(1));
        });

        MoveTo(sim, a, At(home));
        Market.Collect(a.Link, away);
        SessionHarness.Until(() => !Market.Listings(sim).ContainsKey(away), "the return to be collected");
        Harness.Ticks(2);
        Assert.Multiple(() =>
        {
            Assert.That(Market.Units(sim, a.Character, Hide, 2), Is.EqualTo(30));
            Assert.That(Items.Rows(sim).Where(r => r.Ref == away && r.Kind == ItemMoveKind.Return).Sum(r => r.Count), Is.EqualTo(10));
        });

        // A cancel at its city: delivered in the same transaction.
        Breathe();
        var (here, _) = ListAndWait(sim, a, Hide, 2, 5, 50);
        Breathe();
        Market.Cancel(a.Link, here);
        SessionHarness.Until(() => !Market.Listings(sim).ContainsKey(here), "the cancel at its city");
        Harness.Ticks(2);
        Assert.That(Market.Units(sim, a.Character, Hide, 2), Is.EqualTo(30));

        // An expiry, staged by a short life: one listed while away is returned and stays reserved; the seller at its city is handed it.
        sim.Bridge.SetListingLifeTicksForChecks(Worlds.ServedHz);
        Breathe();
        var (expiring, _) = ListAndWait(sim, a, Hide, 2, 3, 50);
        MoveTo(sim, a, Away(sim));
        SessionHarness.Until(() => Market.Listings(sim)[expiring].State == ListingState.Returned, "the expiry");
        Harness.Ticks(2);
        Assert.Multiple(() =>
        {
            Assert.That(Market.Listings(sim)[expiring].Collector, Is.EqualTo(a.Key));
            Assert.That(sim.Bridge.MarketExpired, Is.EqualTo(1));
            Assert.That(Ledgers.Rows(sim).Count(r => r.Kind == TransferKind.ListFee && r.Ref == expiring), Is.EqualTo(1), "the fee is not kept");
        });

        MoveTo(sim, a, At(home));
        Breathe();
        var (handed, _) = ListAndWait(sim, a, Hide, 2, 2, 50);
        SessionHarness.Until(() => !Market.Listings(sim).ContainsKey(handed), "the expiry at its city to be handed over");
        Harness.Ticks(2);
        Assert.That(Items.Rows(sim).Count(r => r.Ref == handed && r.Kind == ItemMoveKind.Return), Is.GreaterThan(0));
        sim.Bridge.SetListingLifeTicksForChecks(0);
        AuditsOk(sim, "after the cancels and expiries");
    }

    // ── BZ-7: the vendor ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// BZ-7: the vendor destroys the units (<c>vendor</c> item rows to World) and pays ⌊count × value × 20 %⌋ from World as one <c>vendor</c> credit row, the
    /// two naming each other; a payout of 0 is <c>MarketWorthless</c>.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void TheVendor_DestroysAndPays_TheFloor()
    {
        using var sim = Start();
        var home = Home(sim);
        var a = LogIn(sim, 0x8701, At(home), 1);
        Mint(sim, a, Hide, 2, 120);
        var balance = Ledgers.Balance(sim, a.Character);
        var itemRows = Items.Rows(sim).Count;
        var creditRows = Ledgers.Rows(sim).Count;

        Market.Vendor(a.Link, Hide, 2, 30);
        SessionHarness.Until(() => Market.Units(sim, a.Character, Hide, 2) == 90, "the vendor sale");
        Harness.Ticks(2);
        var rows = Items.Rows(sim).Skip(itemRows).ToList();
        var credit = Ledgers.Rows(sim).Skip(creditRows).Single();
        var payout = 30L * 2_800 * 200 / 1000 / 1000;
        Assert.Multiple(() =>
        {
            Assert.That(payout, Is.EqualTo(16), "the premise: ⌊30 × 2.8 × 20 %⌋");
            Assert.That(rows, Has.Count.EqualTo(2), "21 from the second stack, 9 from the first");
            Assert.That(rows.All(r => r.Kind == ItemMoveKind.Vendor && r.From == a.Key && r.To == ItemLedger.World && r.Ref == credit.Seq), Is.True);
            Assert.That(rows.Sum(r => r.Count), Is.EqualTo(30));
            Assert.That((credit.Kind, credit.From, credit.To, credit.Amount, credit.Ref),
                Is.EqualTo((TransferKind.Vendor, EconomyLedger.World, a.Key, payout, rows[0].Seq)));
            Assert.That(Ledgers.Balance(sim, a.Character), Is.EqualTo(balance + payout));
            Assert.That(sim.Bridge.MarketVendorPaid, Is.EqualTo(payout));
            Assert.That(sim.Bridge.MarketVendorUnits, Is.EqualTo(30));
            Assert.That(sim.Bridge.EconomyMovedBy(TransferKind.Vendor), Is.EqualTo(payout));
            Assert.That(MarketValues.VendorPayout(Hide, 1, 2), Is.Zero, "the premise of the floor: two Mk I units are worth 0.68 cr");
        });
        AuditsOk(sim, "after the vendor sale");
    }

    // ── BZ-8: the buyer's level ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>BZ-8: the <c>Market</c> system reads no <see cref="CharacterProgress"/>, and two buyers of different combat levels pay the same.</summary>
    [Test]
    [CancelAfter(180_000)]
    public void APriceNeverDependsOnTheBuyer()
    {
        using var sim = Start();
        var market = sim.Runtime.Systems.Single(s => s.Name == "Market");
        Assert.That(market.Access.Reads.Contains(typeof(CharacterProgress)) || market.Access.Writes.Contains(typeof(CharacterProgress))
            || market.Access.ReadsFresh.Contains(typeof(CharacterProgress)), Is.False, "the market declares the character's level");

        var home = Home(sim);
        var a = LogIn(sim, 0x8801, At(home), 1);
        var low = LogIn(sim, 0x8802, At(home), 2);
        var high = LogIn(sim, 0x8803, At(home), 3);
        sim.WriteInTick(tx =>
        {
            var c = tx.OpenMut(high.Character);
            var p = c.Read(CharacterArch.Progress);
            p.Level = 40;
            c.Set(CharacterArch.Progress, p);
        });
        Mint(sim, a, Hide, 4, 2);
        var (one, _) = ListAndWait(sim, a, Hide, 4, 1, 300);
        var (two, _) = ListAndWait(sim, a, Hide, 4, 1, 300);
        var lowBefore = Ledgers.Balance(sim, low.Character);
        var highBefore = Ledgers.Balance(sim, high.Character);
        Market.Buy(low.Link, one, 300);
        Market.Buy(high.Link, two, 300);
        SessionHarness.Until(() => !Market.Listings(sim).ContainsKey(one) && !Market.Listings(sim).ContainsKey(two), "both buys");
        Harness.Ticks(2);
        Assert.That(lowBefore - Ledgers.Balance(sim, low.Character), Is.EqualTo(highBefore - Ledgers.Balance(sim, high.Character)).And.EqualTo(300));
    }

    // ── BZ-10: the market audit ─────────────────────────────────────────────────────────────────────────────────────

    private static void Corrupt(TatooineSim sim, Action<Transaction> write)
    {
        using var tx = sim.Dbe.CreateQuickTransaction();
        write(tx);
        Assert.That(tx.Commit(), Is.True, "the corruption did not commit");
    }

    /// <summary>
    /// BZ-10: the market audit is clean after real trading, and names each rule a corruption breaks — a quantity (MA-1), a second sale row (MA-2), a missing
    /// fee (MA-3), a list row into a character (MA-4), a vendor row naming the wrong credit row (MA-5), a seller over the cap (MA-6), a stale board (MA-7).
    /// </summary>
    [Test]
    [CancelAfter(180_000)]
    public void TheMarketAudit_NamesEveryRuleACorruptedMarketBreaks()
    {
        using var sim = Start();
        var home = Home(sim);
        var a = LogIn(sim, 0x8A01, At(home), 1);
        var b = LogIn(sim, 0x8A02, At(home), 2);
        Mint(sim, a, Hide, 2, 40);
        var (listed, _) = ListAndWait(sim, a, Hide, 2, 5, 20);
        var (sold, _) = ListAndWait(sim, a, Hide, 2, 5, 20);
        Market.Buy(b.Link, sold, 20);
        Market.Vendor(a.Link, Hide, 2, 10);
        SessionHarness.Until(() => !Market.Listings(sim).ContainsKey(sold) && sim.Bridge.MarketVendorSales == 1, "the buy and the vendor sale");
        sim.Bridge.PublishMarketNowForChecks();
        Harness.Ticks(3);
        var clean = MarketEndpoints.Audit(sim.Dbe, sim.Bridge.MarketBoard);
        Assert.That(clean.Ok, Is.True, "the premise: " + string.Join("; ", clean.Violations));
        Assert.That((clean.Listings, clean.Listed, clean.Sales, clean.EscrowUnits), Is.EqualTo((1, 1, 1L, 5L)));
        var board = sim.Bridge.MarketBoard;
        var listing = Market.Listings(sim)[listed];
        var listingId = EntityId.FromRawValue(unchecked((ulong)listed));

        void Breaks(string rule, Action<Transaction> corrupt, Action<Transaction> restore)
        {
            Corrupt(sim, corrupt);
            var audit = MarketEndpoints.Audit(sim.Dbe, board);
            Corrupt(sim, restore);
            Assert.That(audit.Breaks(rule), Is.True, $"{rule} not named: {string.Join("; ", audit.Violations)}");
        }

        Breaks("MA-1", tx => tx.OpenMut(listingId).Set(ListingArch.Entry, listing with { Quantity = 4 }), tx => tx.OpenMut(listingId).Set(ListingArch.Entry, listing));
        Breaks("MA-1", tx => tx.OpenMut(listingId).Set(ListingArch.Entry, listing with { Collector = b.Key }), tx => tx.OpenMut(listingId).Set(ListingArch.Entry, listing));

        var extra = EntityId.Null;
        Breaks("MA-2",
            tx => extra = tx.Spawn<LedgerArch>(LedgerArch.Entry.Set(new LedgerEntry { Seq = 1_000_000, Kind = TransferKind.Sale, From = b.Key, To = a.Key, Amount = 100, Ref = sold })),
            tx => tx.Destroy(extra));
        Breaks("MA-2",
            tx => extra = tx.Spawn<LedgerArch>(LedgerArch.Entry.Set(new LedgerEntry { Seq = 1_000_000, Kind = TransferKind.Sale, From = b.Key, To = a.Key, Amount = 1, Ref = listed })),
            tx => tx.Destroy(extra));

        var (feeId, fee) = FindCredit(sim, r => r.Kind == TransferKind.ListFee && r.Ref == listed);
        Breaks("MA-3", tx => tx.OpenMut(feeId).Set(LedgerArch.Entry, fee with { Ref = 7 }), tx => tx.OpenMut(feeId).Set(LedgerArch.Entry, fee));

        // A fee the stored listing and its row agree on, which is not the rule's.
        Breaks("MA-3",
            tx =>
            {
                tx.OpenMut(feeId).Set(LedgerArch.Entry, fee with { Amount = fee.Amount + 1 });
                tx.OpenMut(listingId).Set(ListingArch.Entry, listing with { Fee = listing.Fee + 1 });
            },
            tx =>
            {
                tx.OpenMut(feeId).Set(LedgerArch.Entry, fee);
                tx.OpenMut(listingId).Set(ListingArch.Entry, listing);
            });

        var (listRowId, listRow) = FindItem(sim, r => r.Kind == ItemMoveKind.List && r.Ref == listed);
        Breaks("MA-4", tx => tx.OpenMut(listRowId).Set(ItemLedgerArch.Entry, listRow with { To = b.Key, ToStack = b.Key }),
            tx => tx.OpenMut(listRowId).Set(ItemLedgerArch.Entry, listRow));

        var (vendorId, vendor) = FindCredit(sim, r => r.Kind == TransferKind.Vendor);
        Breaks("MA-5", tx => tx.OpenMut(vendorId).Set(LedgerArch.Entry, vendor with { Ref = vendor.Ref + 1 }), tx => tx.OpenMut(vendorId).Set(LedgerArch.Entry, vendor));
        Breaks("MA-5", tx => tx.OpenMut(vendorId).Set(LedgerArch.Entry, vendor with { Amount = vendor.Amount + 1 }), tx => tx.OpenMut(vendorId).Set(LedgerArch.Entry, vendor));

        var many = new List<EntityId>();
        Breaks("MA-6",
            tx =>
            {
                for (var i = 0; i < TatooineData.MaxListings; i++)
                {
                    many.Add(tx.Spawn<ListingArch>(ListingArch.Entry.Set(listing with { ListedTick = long.MaxValue / 2 })));
                }
            },
            tx =>
            {
                foreach (var id in many)
                {
                    tx.Destroy(id);
                }
            });

        Breaks("MA-7", tx => tx.OpenMut(listingId).Set(ListingArch.Entry, listing with { UnitPrice = 21 }), tx => tx.OpenMut(listingId).Set(ListingArch.Entry, listing));

        var after = MarketEndpoints.Audit(sim.Dbe, board);
        Assert.That(after.Ok, Is.True, "not clean once put back: " + string.Join("; ", after.Violations));
    }

    private static (EntityId Id, LedgerEntry Row) FindCredit(TatooineSim sim, Func<LedgerEntry, bool> keep)
    {
        var rows = Ledgers.Rows(sim, out var ids);
        var i = rows.FindIndex(r => keep(r));
        Assert.That(i, Is.GreaterThanOrEqualTo(0), "the premise: the credit row");
        return (ids[i], rows[i]);
    }

    private static (EntityId Id, ItemLedgerEntry Row) FindItem(TatooineSim sim, Func<ItemLedgerEntry, bool> keep)
    {
        var rows = Items.Rows(sim, out var ids);
        var i = rows.FindIndex(r => keep(r));
        Assert.That(i, Is.GreaterThanOrEqualTo(0), "the premise: the item row");
        return (ids[i], rows[i]);
    }

    // ── BZ-11: across a stop ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// BZ-11 (the reload half of M5-2): list, buy and vendor, then stop and reopen: every applied buy is there, no listing has two sales, the three audits are
    /// clean, and the desk's book reloads equal — the board carries on. The stop is a clean one (the simulation is disposed once the trading has
    /// committed); a kill mid-trade is BZ-18's, live.
    /// </summary>
    [Test]
    [CancelAfter(300_000)]
    public void TheMarket_ReloadsEqual_AcrossAReopen()
    {
        Dictionary<long, Listing> before;
        long aBalance;
        long bBalance;
        EntityId aChar;
        EntityId bChar;
        using (var first = Start(persist: true))
        {
            var home = Home(first);
            var elsewhere = Elsewhere(first);
            var a = LogIn(first, 0x8B01, At(home), 1);
            var b = LogIn(first, 0x8B02, At(elsewhere), 2);
            Mint(first, a, Hide, 2, 60);
            var (reserved, _) = ListAndWait(first, a, Hide, 2, 10, 10);
            ListAndWait(first, a, Hide, 2, 10, 10);
            Market.Buy(b.Link, reserved, 10);
            Market.Vendor(a.Link, Hide, 2, 5);
            SessionHarness.Until(() => Market.Listings(first)[reserved].State == ListingState.Sold && first.Bridge.MarketVendorSales == 1, "the trading");
            Harness.Ticks(3);
            before = Market.Listings(first);
            aChar = a.Character;
            bChar = b.Character;
            aBalance = Ledgers.Balance(first, aChar);
            bBalance = Ledgers.Balance(first, bChar);
            AuditsOk(first, "before the stop");

            // No Close: dropped the way a kill drops it.
            Harness.Release();
        }

        TatooineReplication.ResetSessionAccounting();
        TatooineReplication.ResetIntentAccounting();
        using var second = Start(persist: true);
        Assert.That(second.Reopened, Is.True, "the second run must reopen the first's database");
        Harness.Ticks(3);
        second.Bridge.PublishMarketNowForChecks();
        Harness.Ticks(3);
        var after = Market.Listings(second);
        Assert.Multiple(() =>
        {
            Assert.That(after.Keys, Is.EquivalentTo(before.Keys), "the listings did not survive");
            Assert.That(after.Values.Select(l => (l.State, l.Collector, l.Quantity)), Is.EquivalentTo(before.Values.Select(l => (l.State, l.Collector, l.Quantity))));
            Assert.That(Ledgers.Balance(second, aChar), Is.EqualTo(aBalance));
            Assert.That(Ledgers.Balance(second, bChar), Is.EqualTo(bBalance));
            Assert.That(Ledgers.Rows(second).Where(r => r.Kind == TransferKind.Sale).GroupBy(r => r.Ref).All(g => g.Count() == 1), Is.True, "a listing with two sales");
            Assert.That(second.Bridge.MarketBoard.Total, Is.EqualTo(1), "the board did not reload the one listed listing");
        });
        AuditsOk(second, "after the reopen");

        // The desk carries on: the reserved purchase is collected at home.
        var home2 = Home(second);
        var b2 = LogIn(second, 0x8B02, At(home2), 1);
        var reservedId = before.Single(kv => kv.Value.State == ListingState.Sold).Key;
        Market.Collect(b2.Link, reservedId);
        SessionHarness.Until(() => !Market.Listings(second).ContainsKey(reservedId), "the collect after the reopen");
        Harness.Ticks(2);
        Assert.That(Market.Units(second, bChar, Hide, 2), Is.EqualTo(10));
        AuditsOk(second, "after the collect");
    }
}
