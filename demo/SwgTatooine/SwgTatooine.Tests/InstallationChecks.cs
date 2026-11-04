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
/// Player installations (crafting-build.md § 10, C5a): an extractor placed from its kit on a 64 m lot outside the cities, making Mk I units of its family
/// while its prepaid upkeep lasts, collected, and picked up back into its kit — every move a ledger row, the audits clean; its marker drawn, rebuilt on a
/// reopen; the lots a character holds counted; and every refusal with its reason, writing nothing.
/// </summary>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
internal sealed class InstallationChecks : ServedChecks
{
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

    private sealed record Owner(FakeLink Link, EntityId Body, EntityId Character)
    {
        public long Key => unchecked((long)Character.RawValue);
    }

    private Owner LogIn(TatooineSim sim, ulong account, long bindings = 1)
    {
        var link = Harness.Connect(TatooineReplication.PlayerKind, TestAccounts.For(sim.Dbe, account));
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= bindings, "the avatar to be spawned");
        // Waited for, not asserted: on CI's runner the character was not found yet, two ticks after the possession, in five fixtures (PR #1590).
        var characterAccount = TestAccounts.For(sim.Dbe, account);
        var character = EntityId.Null;
        SessionHarness.Until(() => CharacterStore.TryFind(sim.Dbe, characterAccount, out character, out _), "the account's character");
        SessionHarness.Until(() => Ledgers.TryWallet(sim, character, out _), "the wallet to be opened");
        Harness.Ticks(2);
        return new Owner(link, TatooineReplication.ControlledBy(sim.Dbe, link.Session), character);
    }

    private void Breathe() => Harness.Ticks(Worlds.FastCommandGapTicks);

    /// <summary>A lot's centre in the wild: the first point east of city 0, lot by lot, that no city's margin reaches.</summary>
    private static (float X, float Z) Wild(TatooineSim sim, int lotsEast = 0)
    {
        var city = sim.Index.Cities[0];
        for (var d = city.Radius + 400f; d < 6_000f; d += Installations.LotM)
        {
            var lot = Installations.LotOf(0, city.X + d, city.Z);
            var (x, z) = Installations.CentreOf(lot);
            var (x2, z2) = Installations.CentreOf(Installations.LotOf(0, x + (lotsEast * Installations.LotM), z));
            if (!sim.Index.Levels.NearCity(x, z, Installations.CityMarginM + 10f) && !sim.Index.Levels.NearCity(x2, z2, Installations.CityMarginM + 10f))
            {
                return (x2, z2);
            }
        }

        Assert.Fail("the premise: wild ground east of city 0");
        return default;
    }

    private EntityId StageKit(TatooineSim sim, Owner o)
    {
        var before = Items.Stacks(sim, o.Character).Select(s => s.Id).ToHashSet();
        sim.Bridge.StageInstance(o.Key, ItemCatalog.Extractor, 0, Installations.StandardKit());
        EntityId id = default;
        SessionHarness.Until(
            () =>
            {
                id = Items.Stacks(sim, o.Character).FirstOrDefault(s => s.Stack.Template == ItemCatalog.Extractor && !before.Contains(s.Id)).Id;
                return !id.IsNull;
            },
            "the staged kit");
        return id;
    }

    private static void Place(FakeLink link, EntityId kit, ushort family)
        => link.SendCommand(
            nameof(PlaceInstallation),
            new RecordValues { ["item"] = FieldValue.OfUInt64(kit.RawValue), ["family"] = FieldValue.Of(family) });

    private static void Collect(FakeLink link, long installation)
        => link.SendCommand(nameof(CollectInstallation), new RecordValues { ["installation"] = FieldValue.OfUInt64(unchecked((ulong)installation)) });

    private static void Pay(FakeLink link, long installation, uint credits)
        => link.SendCommand(
            nameof(PayUpkeep),
            new RecordValues { ["installation"] = FieldValue.OfUInt64(unchecked((ulong)installation)), ["credits"] = FieldValue.Of(credits) });

    private static void PickUp(FakeLink link, long installation)
        => link.SendCommand(nameof(PickUpInstallation), new RecordValues { ["installation"] = FieldValue.OfUInt64(unchecked((ulong)installation)) });

    private static List<(long Key, InstallationCore Core)> Of(TatooineSim sim, Owner o)
    {
        var found = new List<(long, InstallationCore)>();
        var index = sim.Dbe.GetIndexRef<InstallationCore, long>(c => c.Owner);
        using var tx = sim.Dbe.CreateQuickTransaction();
        using var e = tx.EnumerateIndex<InstallationCore, long>(index, o.Key, o.Key);
        while (e.MoveNext())
        {
            found.Add((e.CurrentEntityPK, e.CurrentComponent));
        }

        return found;
    }

    private static List<InstallationView> Markers(TatooineSim sim)
    {
        var views = new List<InstallationView>();
        sim.WriteInTick(tx =>
        {
            foreach (var m in tx.Query<InstallationMarker>())
            {
                views.Add(m.Read(InstallationMarker.View));
            }
        });
        return views;
    }

    private static int Count(FakeLink link, byte reason) => Ledgers.Refusals(link).Count(r => r == reason);

    private static void AuditsOk(TatooineSim sim, string when)
    {
        var (ok, why) = Market.Audits(sim);
        Assert.That(ok, Is.True, $"{when}: {why}");
    }

    private static long Hours(TatooineSim sim, double hours) => (long)(hours * sim.Bridge.TicksPerHour);

    /// <summary>A character's one installation's state, as <c>GET /swg/installations</c> computes it now.</summary>
    private static int StateOf(TatooineSim sim, Owner o)
    {
        using var json = System.Text.Json.JsonDocument.Parse(InstallationEndpoints.Json(sim.Dbe, o.Key));
        return json.RootElement.GetProperty("installations")[0].GetProperty("state").GetInt32();
    }

    /// <summary>
    /// An extractor's life: the kit placed on a wild lot (a <c>place</c> row; one lot used; its marker drawn, unpaid); an hour of upkeep paid (an
    /// <c>upkeep</c> row); half an hour later its hopper holds half the hour's units, collected as Mk I Metal (<c>extract</c> rows); two hours later it has
    /// stopped where its upkeep ran out, unpaid on the marker; a pick-up refused until it is collected, then the kit back (a <c>pickup</c> row), the lot
    /// free, the marker gone. The audits clean at every step.
    /// </summary>
    [Test]
    [CancelAfter(180_000)]
    public void AnExtractor_IsPlaced_Paid_Collected_AndPickedUp()
    {
        using var sim = Start();
        var o = LogIn(sim, 0xE501);
        var kit = StageKit(sim, o);
        var (x, z) = Wild(sim);
        Worlds.Park(sim, o.Link.Session, x + 3f, z);
        Harness.Ticks(2);

        Place(o.Link, kit, ItemCatalog.Metal);
        SessionHarness.Until(() => sim.Bridge.InstallationsPlaced == 1, "the extractor placed");
        Harness.Ticks(3);
        var (key, core) = Of(sim, o).Single();
        Assert.Multiple(() =>
        {
            Assert.That(Items.Stacks(sim, o.Character).Any(s => s.Stack.Template == ItemCatalog.Extractor), Is.False, "the kit became the extractor");
            Assert.That((core.X, core.Z, core.Family, core.RatePerHour, core.Hopper), Is.EqualTo((x, z, ItemCatalog.Metal, 360, 500)));
            Assert.That(sim.Bridge.LotsUsedBy(o.Key), Is.EqualTo(1));
            Assert.That(Markers(sim).Single().State, Is.EqualTo(InstallationState.Unpaid), "unpaid: it makes nothing yet");
        });
        AuditsOk(sim, "after the place");

        var balance = Ledgers.Balance(sim, o.Character);
        Pay(o.Link, key, 100);
        SessionHarness.Until(() => sim.Bridge.UpkeepPaid == 100, "an hour of upkeep");
        sim.Bridge.AdvanceInstallationClockForChecks(Hours(sim, 0.5));
        Breathe();
        Assert.That(StateOf(sim, o), Is.EqualTo(InstallationState.Running), "paid: it runs");

        // C5a-2: running, it is computed, never written — a hundred ticks with no command leave its truth untouched.
        var writes = sim.Bridge.InstallationWrites;
        Harness.Ticks(100);
        Assert.That(sim.Bridge.InstallationWrites, Is.EqualTo(writes), "a running installation was written with no command");

        Collect(o.Link, key);
        SessionHarness.Until(() => sim.Bridge.InstallationCollects == 1, "the collect");
        Breathe();
        var metal = Market.Units(sim, o.Character, ItemCatalog.Metal, 1);
        Assert.Multiple(() =>
        {
            Assert.That(Ledgers.Balance(sim, o.Character), Is.EqualTo(balance - 100));
            Assert.That(metal, Is.InRange(179, 181), "half an hour at 360 an hour");
            Assert.That(Items.Rows(sim).Where(r => r.Kind == ItemMoveKind.Extract).Sum(r => r.Count), Is.EqualTo(metal));
        });
        AuditsOk(sim, "after the collect");

        sim.Bridge.AdvanceInstallationClockForChecks(Hours(sim, 2));
        Breathe();
        Assert.That(StateOf(sim, o), Is.EqualTo(InstallationState.Unpaid), "its upkeep ran out");
        PickUp(o.Link, key);
        SessionHarness.Until(() => Count(o.Link, TatooineReplication.InstallationNotEmpty) == 1, "a pick-up with units in the hopper refused");
        Collect(o.Link, key);
        SessionHarness.Until(() => sim.Bridge.InstallationCollects == 2, "the second collect");
        Breathe();
        Assert.That(Market.Units(sim, o.Character, ItemCatalog.Metal, 1) - metal, Is.InRange(179, 181), "the half hour its upkeep still ran, no more");

        PickUp(o.Link, key);
        SessionHarness.Until(() => sim.Bridge.InstallationsPickedUp == 1, "the pick-up");
        Harness.Ticks(3);
        Assert.Multiple(() =>
        {
            Assert.That(Of(sim, o), Is.Empty);
            Assert.That(sim.Bridge.LotsUsedBy(o.Key), Is.Zero, "the lot given back");
            Assert.That(Markers(sim), Is.Empty, "the marker gone");
            Assert.That(Items.Stacks(sim, o.Character).Count(s => s.Stack.Template == ItemCatalog.Extractor), Is.EqualTo(1), "the kit back");
            Assert.That(Items.Rows(sim).Select(r => r.Kind).Where(k => k is ItemMoveKind.Place or ItemMoveKind.Pickup), Is.EqualTo(new[]
            {
                ItemMoveKind.Place, ItemMoveKind.Pickup,
            }));
            Assert.That(Ledgers.Rows(sim).Count(r => r.Kind == TransferKind.Upkeep), Is.EqualTo(1));
        });
        AuditsOk(sim, "after the pick-up");
    }

    /// <summary>
    /// ML-4 (mail.md § 5): an installation mails its owner once per transition — its upkeep under an hour, then stopped unpaid, then its hopper full — and
    /// not again while it stays so; the state it was placed in, seeded by the first sweep, is not mailed.
    /// </summary>
    [Test]
    [CancelAfter(240_000)]
    public void AnInstallation_MailsItsOwner_OncePerTransition()
    {
        using var sim = Start();
        var o = LogIn(sim, 0xE5A1);
        var kit = StageKit(sim, o);
        var (x, z) = Wild(sim);
        Worlds.Park(sim, o.Link.Session, x + 3f, z);
        Harness.Ticks(2);
        Place(o.Link, kit, ItemCatalog.Metal);
        SessionHarness.Until(() => sim.Bridge.InstallationsPlaced == 1, "the extractor placed");
        var key = Of(sim, o).Single().Key;
        var owner = unchecked((long)o.Character.RawValue);
        sim.Bridge.SetInstallationSweepTicksForChecks(4);
        var sweep = 6;
        List<string> Kinds() => [.. System.Text.Json.JsonDocument.Parse(MailEndpoints.InboxJson(sim.Dbe, owner)).RootElement.GetProperty("mails")
            .EnumerateArray().Select(m => m.GetProperty("kind").GetString()).Reverse()];

        Harness.Ticks(sweep);
        Assert.That(Kinds(), Is.Empty, "placed unpaid: the state it starts in is no news");

        Pay(o.Link, key, 100);
        SessionHarness.Until(() => sim.Bridge.UpkeepPaid == 100, "an hour of upkeep");
        sim.Bridge.AdvanceInstallationClockForChecks(Hours(sim, 0.1));
        Harness.Ticks(sweep);
        Harness.Ticks(sweep);
        Assert.That(Kinds(), Is.EqualTo(new[] { "upkeeplow" }), "under an hour left: told once, not at every sweep");

        sim.Bridge.AdvanceInstallationClockForChecks(Hours(sim, 2));
        Harness.Ticks(sweep);
        Assert.That(Kinds(), Is.EqualTo(new[] { "upkeeplow", "stopped" }));

        Breathe();
        Pay(o.Link, key, 800);
        SessionHarness.Until(() => sim.Bridge.UpkeepPaid == 900, "eight more hours");
        sim.Bridge.AdvanceInstallationClockForChecks(Hours(sim, 2));
        Harness.Ticks(sweep);
        Harness.Ticks(sweep);
        var inbox = System.Text.Json.JsonDocument.Parse(MailEndpoints.InboxJson(sim.Dbe, owner)).RootElement.GetProperty("mails");
        Assert.Multiple(() =>
        {
            Assert.That(Kinds(), Is.EqualTo(new[] { "upkeeplow", "stopped", "hopperfull" }));
            Assert.That(inbox[0].GetProperty("subject").GetString(), Is.EqualTo("Extractor full: Metal"));
            Assert.That(
                inbox[0].GetProperty("ref").GetString(),
                Is.EqualTo(key.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                "it names the installation");
            Assert.That(
                MailEndpoints.MailJson(sim.Dbe, owner, long.Parse(inbox[0].GetProperty("id").GetString())),
                Does.Contain("has filled its hopper (500 / 500)"));
        });
    }

    /// <summary>
    /// What may not be placed or done is refused with its reason and writes nothing: in a city; a family an extractor does not draw; a lot taken; past the
    /// character's 10 lots; a collect out of reach, or of an empty hopper; an upkeep payment past the prepaid cap; another's installation.
    /// </summary>
    [Test]
    [CancelAfter(240_000)]
    public void EveryInstallationRefusal_HasItsReason()
    {
        using var sim = Start();
        var o = LogIn(sim, 0xE502);
        var kits = Enumerable.Range(0, Installations.LotsPerCharacter + 1).Select(_ => StageKit(sim, o)).ToArray();

        var city = sim.Index.Cities[0];
        Worlds.Park(sim, o.Link.Session, city.X, city.Z);
        Harness.Ticks(2);
        Place(o.Link, kits[0], ItemCatalog.Metal);
        SessionHarness.Until(() => Count(o.Link, TatooineReplication.InstallationInTown) == 1, "in a city");

        for (var i = 0; i < Installations.LotsPerCharacter; i++)
        {
            var (x, z) = Wild(sim, i);
            Worlds.Park(sim, o.Link.Session, x, z);
            Harness.Ticks(2);
            if (i == 0)
            {
                Place(o.Link, kits[0], ItemCatalog.Hide);
                SessionHarness.Until(() => Count(o.Link, TatooineReplication.InstallationBad) == 1, "a family it does not draw");
            }

            Place(o.Link, kits[i], ItemCatalog.Ore);
            SessionHarness.Until(() => sim.Bridge.InstallationsPlaced == i + 1, $"extractor {i + 1}");
            Breathe();
        }

        var (lx, lz) = Wild(sim, Installations.LotsPerCharacter);
        Worlds.Park(sim, o.Link.Session, lx, lz);
        Harness.Ticks(2);
        Place(o.Link, kits[^1], ItemCatalog.Ore);
        SessionHarness.Until(() => Count(o.Link, TatooineReplication.NoLotsLeft) == 1, "an eleventh lot");

        var other = LogIn(sim, 0xE503, 2);
        var otherKit = StageKit(sim, other);
        var (fx, fz) = Wild(sim);
        Worlds.Park(sim, other.Link.Session, fx, fz);
        Harness.Ticks(2);
        Place(other.Link, otherKit, ItemCatalog.Ore);
        SessionHarness.Until(() => Count(other.Link, TatooineReplication.LotTaken) == 1, "a lot taken");
        var first = Of(sim, o).Single(i => Math.Abs(i.Core.X - fx) < 1f && Math.Abs(i.Core.Z - fz) < 1f).Key;
        Collect(other.Link, first);
        SessionHarness.Until(() => Count(other.Link, TatooineReplication.InstallationBad) == 1, "another's installation");

        var rows = Items.Rows(sim).Count;
        var credits = Ledgers.Rows(sim).Count;
        Worlds.Park(sim, o.Link.Session, fx + 200f, fz + 200f);
        Harness.Ticks(2);
        Collect(o.Link, first);
        SessionHarness.Until(() => Count(o.Link, TatooineReplication.InstallationNotHere) == 1, "out of reach");
        Worlds.Park(sim, o.Link.Session, fx, fz);
        Harness.Ticks(2);
        Collect(o.Link, first);
        SessionHarness.Until(() => Count(o.Link, TatooineReplication.InstallationEmpty) == 1, "an empty hopper");
        Pay(o.Link, first, (uint)(Installations.ExtractorUpkeepPerHour * (Installations.MaxPrepaidHours + 1)));
        SessionHarness.Until(() => Count(o.Link, TatooineReplication.InstallationBad) == 2, "past the prepaid cap");
        Harness.Ticks(3);

        Assert.Multiple(() =>
        {
            Assert.That(Items.Rows(sim), Has.Count.EqualTo(rows), "a refusal wrote an item row");
            Assert.That(Ledgers.Rows(sim), Has.Count.EqualTo(credits), "a refusal wrote a credit row");
            Assert.That(sim.Bridge.LotsUsedBy(o.Key), Is.EqualTo(Installations.LotsPerCharacter));
            Assert.That(Markers(sim), Has.Count.EqualTo(Installations.LotsPerCharacter));
        });
        AuditsOk(sim, "after the refusals");
    }

    /// <summary>
    /// An installation outlives a stop with no logout (C5a-3): reopened, its truth is the same, its marker is drawn again, and what it collects once its
    /// upkeep has run out is the formula's to the unit — the world's clock carried across the reopen.
    /// </summary>
    [Test]
    [CancelAfter(240_000)]
    public void AnExtractor_SurvivesAStop_AndItsMarkerIsRebuilt()
    {
        InstallationCore before;
        long key;
        float x, z;
        using (var first = Start(persist: true))
        {
            var o = LogIn(first, 0xE504);
            var kit = StageKit(first, o);
            (x, z) = Wild(first);
            Worlds.Park(first, o.Link.Session, x, z);
            Harness.Ticks(2);
            Place(o.Link, kit, ItemCatalog.Water);
            SessionHarness.Until(() => first.Bridge.InstallationsPlaced == 1, "the extractor placed");
            Breathe();
            Pay(o.Link, Of(first, o).Single().Key, 100);
            SessionHarness.Until(() => first.Bridge.UpkeepPaid == 100, "an hour of upkeep");
            Harness.Ticks(3);
            (key, before) = Of(first, o).Single();
            AuditsOk(first, "before the stop");
            Harness.Release();
        }

        TatooineReplication.ResetSessionAccounting();
        TatooineReplication.ResetIntentAccounting();
        using var second = Start(persist: true);
        Assert.That(second.Reopened, Is.True);
        var again = LogIn(second, 0xE504);
        SessionHarness.Until(() => Markers(second).Count == 1, "the marker rebuilt");
        var (reopenedKey, after) = Of(second, again).Single();
        Assert.Multiple(() =>
        {
            Assert.That(reopenedKey, Is.EqualTo(key));
            Assert.That((after.Lot, after.PaidUntil, after.Family, after.T0), Is.EqualTo((before.Lot, before.PaidUntil, before.Family, before.T0)));
            Assert.That(second.Bridge.LotsUsedBy(again.Key), Is.EqualTo(1));
        });

        // Past its paid-until, what it holds no longer depends on the tick: the whole paid hour's units, as the formula has them.
        second.Bridge.AdvanceInstallationClockForChecks(Hours(second, 2));
        Worlds.Park(second, again.Link.Session, x, z);
        Harness.Ticks(2);
        var expected = Installations.Held(in after, after.PaidUntil, second.Bridge.TicksPerHour);
        var water = Market.Units(second, again.Character, ItemCatalog.Water, 1);
        Collect(again.Link, reopenedKey);
        SessionHarness.Until(() => second.Bridge.InstallationCollects == 1, "the collect");
        Breathe();
        Assert.Multiple(() =>
        {
            Assert.That(expected, Is.InRange(359, 360), "an hour at 360 an hour");
            Assert.That(Market.Units(second, again.Character, ItemCatalog.Water, 1) - water, Is.EqualTo(expected), "the formula's, to the unit");
        });
        AuditsOk(second, "after the reopen");
    }
}
