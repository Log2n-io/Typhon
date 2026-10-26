using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using NUnit.Framework;
using SwgTatooine.Server;
using SwgTatooine.Server.Accounts;
using Typhon.Engine;
using Typhon.Protocol;

namespace SwgTatooine.Tests;

/// <summary>
/// The mission desk (claude/design/SwgTatooine/design/missions.md § 7): terminals list offers, one taker wins, a held mission's lair is drawn round its
/// anchor while its holder is online, and it ends when its holder breaks the lair or cancels.
/// </summary>
/// <remarks>
/// <b>The crowd is kept off the terminals</b> (<see cref="SimConfig.CrowdTakesMissions"/>): a case that takes one particular offer must not find a scripted
/// body took it first. The crowd's own missions are <see cref="CausalityLoopTests"/>' — a measured run where the crowd takes them, fights and completes.
/// </remarks>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
internal sealed class MissionDeskChecks : ServedChecks
{
    private SimConfig Config(bool persist = false)
    {
        var config = Worlds.Served(Dir);
        config.WorkerCount = 1;
        config.Persist = persist;
        return config;
    }

    private TatooineSim Start(bool persist = false)
    {
        var sim = new TatooineSim(Config(persist));
        sim.Initialize();
        Harness = new SessionHarness(sim);
        return sim;
    }

    /// <summary>Logs an account in, parks its body at the terminal, and waits for the terminal's offers to reach its view.</summary>
    private FakeLink LogInAt(TatooineSim sim, EntityId account, MissionTerminalRef terminal, long bindings = 1)
    {
        var link = Harness.Connect(TatooineReplication.PlayerKind, account);
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= bindings, "the avatar to be spawned");
        Harness.Ticks(2);
        Worlds.Park(sim, link.Session, terminal.X + 2f, terminal.Z, out var parked);
        var first = Offers(sim, terminal)[0].Id;
        SessionHarness.UntilNamed(link.Session, first, "the terminal's offers to reach the client's view", parked);
        return link;
    }

    private static EntityId AnAccount(ulong id) => EntityId.FromRawValue(id);

    private static MissionTerminalRef FirstTerminal(TatooineSim sim)
    {
        Assert.That(sim.Index.Terminals, Is.Not.Empty, "the premise: the world has a mission terminal");
        return sim.Index.Terminals[0];
    }

    /// <summary>The offers listed at a terminal, in no particular order.</summary>
    /// <remarks>
    /// <b>Read on the tick, not beside it.</b> An offer is SingleVersion: read from the test thread while the desk's commit published, a cluster walk yielded
    /// the offer just taken — its occupancy bit not yet cleared — while point access already said it was gone, and the case took a taken offer (5 runs in
    /// 10). Between ticks the two agree.
    /// </remarks>
    private static List<(EntityId Id, MissionOfferInfo Info)> Offers(TatooineSim sim, MissionTerminalRef terminal)
    {
        var rows = new List<(EntityId, MissionOfferInfo)>();
        sim.WriteInTick(tx =>
        {
            foreach (var cluster in tx.For<MissionOffer>().GetClusterEnumerator())
            {
                var infos = cluster.GetReadOnlySpan(MissionOffer.Info);
                for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    if (infos[slot].Terminal == terminal.Id)
                    {
                        rows.Add((cluster.GetEntityId(slot), infos[slot]));
                    }
                }
            }
        });

        return rows;
    }

    private static EntityId CharacterOf(TatooineSim sim, EntityId account)
    {
        Assert.That(CharacterStore.TryFind(sim.Dbe, account, out var character, out _), Is.True, "the account has no character");
        return character;
    }

    /// <summary>The missions a holder has, read the way the journal reads them: through the index.</summary>
    private static List<(EntityId Id, HeldMission Mission)> Held(TatooineSim sim, EntityId holder)
    {
        var rows = new List<(EntityId, HeldMission)>();
        var key = unchecked((long)holder.RawValue);
        var indexRef = sim.Dbe.GetIndexRef<HeldMission, EntityId>(m => m.Holder);
        using var tx = sim.Dbe.CreateQuickTransaction();
        using var found = tx.EnumerateIndex<HeldMission, long>(indexRef, key, key);
        while (found.MoveNext())
        {
            rows.Add((EntityId.FromRawValue((ulong)found.CurrentEntityPK), found.CurrentComponent));
        }

        return rows;
    }

    private static void Take(FakeLink link, EntityId offer, uint serial)
        => link.SendCommand(nameof(TakeMission), new RecordValues
        {
            ["offer"] = FieldValue.Of(SessionHarness.NetIdOf(link.Session, offer)),
            ["serial"] = FieldValue.Of(serial),
        });

    /// <summary>Takes an offer and waits until the character holds one more mission than before, its lair drawn.</summary>
    private (EntityId Id, HeldMission Mission) TakeAndHold(TatooineSim sim, FakeLink link, EntityId character, (EntityId Id, MissionOfferInfo Info) offer)
    {
        var before = Held(sim, character).Count;
        Take(link, offer.Id, offer.Info.Serial);
        try
        {
            SessionHarness.Until(() => Held(sim, character).Count > before, "the mission to be held");
        }
        catch (AssertionException)
        {
            throw new AssertionException(
                $"the mission was not held; refusals sent: [{string.Join(", ", Refusals(link))}]; held {Held(sim, character).Count} (before {before}); "
                + $"offer exists {Exists(sim, offer.Id)}, netId now {SessionHarness.NetIdOf(link.Session, offer.Id)}, refused {sim.Bridge.MissionTakesRefused}");
        }
        var held = Held(sim, character).Find(h => h.Mission.Kind == offer.Info.Kind && h.Mission.AnchorX == offer.Info.AnchorX);
        Assert.That(held.Id.IsNull, Is.False, "the mission held is not the offer taken");
        SessionHarness.Until(() => !Held(sim, character).Find(h => h.Id == held.Id).Mission.Lair.IsNull, "the mission's lair to be drawn");
        return Held(sim, character).Find(h => h.Id == held.Id);
    }

    /// <summary>The session's character's wallet balance, and its body's journal revision.</summary>
    private static (int Credits, int JournalRev) BodyOf(TatooineSim sim, SessionId session)
    {
        var body = TatooineReplication.ControlledBy(sim.Dbe, session);
        Assert.That(TatooineReplication.TryBodyOfSession(session, out _, out var character), Is.True, "the session has no character");
        var credits = (int)Ledgers.Balance(sim, character);
        using var tx = sim.Dbe.CreateQuickTransaction();
        var player = tx.Open(body);
        return (credits, player.Read(Player.State).JournalRev);
    }

    private static bool Exists(TatooineSim sim, EntityId id)
    {
        using var tx = sim.Dbe.CreateQuickTransaction();
        return tx.TryOpen(id, out _);
    }

    private static byte[] Refusals(FakeLink link) => Ledgers.Refusals(link);

    // ── M-1: the lists ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// M-1: every terminal lists its offers, each 0.5-2 km out, outside every city, its direction and distance true of its anchor to within an eighth and
    /// 50 m.
    /// </summary>
    [Test]
    [CancelAfter(90_000)]
    public void EveryTerminalListsItsOffers_EachAboutWhereItSays()
    {
        using var sim = Start();
        Harness.Ticks(3);
        var scale = sim.Config.ContentScale;
        Assert.Multiple(() =>
        {
            foreach (var terminal in sim.Index.Terminals)
            {
                var offers = Offers(sim, terminal);
                Assert.That(offers, Has.Count.EqualTo(TatooineData.MissionOffersPerTerminal), $"terminal at {terminal.X:F0},{terminal.Z:F0}");
                foreach (var (_, info) in offers)
                {
                    var dx = info.AnchorX - terminal.X;
                    var dz = info.AnchorZ - terminal.Z;
                    var d = MathF.Sqrt((dx * dx) + (dz * dz));
                    Assert.That(d, Is.InRange(TatooineData.MissionMinDistanceM * scale - 1f, TatooineData.MissionMaxDistanceM * scale + 1f), "anchor distance");
                    Assert.That(MathF.Abs(info.DistanceM - d), Is.LessThanOrEqualTo(50f), "the listed distance is the anchor's, rounded to 100 m");
                    Assert.That(info.Octant, Is.EqualTo(TatooineData.OctantOf(dx, dz)), "the listed direction is the anchor's");
                    Assert.That(info.Difficulty, Is.EqualTo(1));
                    Assert.That(info.Reward, Is.EqualTo(TatooineData.MissionReward(1, d / scale)).Within(5), "the reward follows the distance");
                    foreach (var city in sim.Index.Cities)
                    {
                        var cx = info.AnchorX - city.X;
                        var cz = info.AnchorZ - city.Z;
                        Assert.That((cx * cx) + (cz * cz), Is.GreaterThanOrEqualTo(city.Radius * city.Radius), "an anchor inside a city");
                    }
                }
            }
        });
    }

    /// <summary>The direction an offer reads is the eighth of the compass its anchor lies in: north is +Z, east +X, clockwise.</summary>
    [TestCase(0f, 1f, 0)]
    [TestCase(1f, 1f, 1)]
    [TestCase(1f, 0f, 2)]
    [TestCase(1f, -1f, 3)]
    [TestCase(0f, -1f, 4)]
    [TestCase(-1f, -1f, 5)]
    [TestCase(-1f, 0f, 6)]
    [TestCase(-1f, 1f, 7)]
    [TestCase(0.38f, 1f, 0)]
    [TestCase(0.42f, 1f, 1)]
    public void TheOctantIsTheCompassEighth(float dx, float dz, int octant) => Assert.That(TatooineData.OctantOf(dx, dz), Is.EqualTo(octant));

    /// <summary>The reward: per difficulty, a base and as much again per kilometre, to the nearest 5.</summary>
    [Test]
    public void TheRewardGrowsWithDistanceAndDifficulty()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TatooineData.MissionReward(1, 500f), Is.EqualTo(150));
            Assert.That(TatooineData.MissionReward(1, 2000f), Is.EqualTo(300));
            Assert.That(TatooineData.MissionReward(2, 1000f), Is.EqualTo(400));
            Assert.That(TatooineData.MissionReward(1, 1234f) % 5, Is.Zero);
        });
    }

    // ── M-2, M-3: taking ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>M-2: two players take the same offer: exactly one holds it, the other is told it is taken, and the terminal lists its 20 again.</summary>
    [Test]
    [CancelAfter(120_000)]
    public void TwoTakersOfOneOffer_OneHoldsIt_TheOtherIsToldItIsTaken()
    {
        using var sim = Start();
        var terminal = FirstTerminal(sim);
        var a = LogInAt(sim, AnAccount(0x6001), terminal, bindings: 1);
        var b = LogInAt(sim, AnAccount(0x6002), terminal, bindings: 2);
        var ca = CharacterOf(sim, AnAccount(0x6001));
        var cb = CharacterOf(sim, AnAccount(0x6002));
        var offer = Offers(sim, terminal)[0];
        SessionHarness.UntilNamed(b.Session, offer.Id, "the offer to reach the second view");

        Take(a, offer.Id, offer.Info.Serial);
        Take(b, offer.Id, offer.Info.Serial);
        SessionHarness.Until(() => Held(sim, ca).Count + Held(sim, cb).Count >= 1 && sim.Bridge.MissionTakesRefused >= 1, "both takes to be answered");
        Harness.Ticks(3);

        var held = Held(sim, ca).Count + Held(sim, cb).Count;
        var loser = Held(sim, ca).Count == 1 ? b : a;
        Assert.Multiple(() =>
        {
            Assert.That(held, Is.EqualTo(1), "one offer, two holders");
            Assert.That(Refusals(loser), Contains.Item(TatooineReplication.MissionTaken), "the second taker was not told the mission is taken");
            Assert.That(Exists(sim, offer.Id), Is.False, "a taken offer is still listed");
            Assert.That(Offers(sim, terminal), Has.Count.EqualTo(TatooineData.MissionOffersPerTerminal), "the terminal did not list a replacement");
        });
    }

    /// <summary>M-3: a take from away from the terminal is refused as too far, and a fourth held mission as over the cap.</summary>
    [Test]
    [CancelAfter(120_000)]
    public void TooFarFromTheTerminal_OrPastThreeHeld_IsRefused()
    {
        using var sim = Start();
        var terminal = FirstTerminal(sim);
        var link = LogInAt(sim, AnAccount(0x6003), terminal);
        var character = CharacterOf(sim, AnAccount(0x6003));

        // 10 m out: inside the view, past the terminal's reach.
        Worlds.Park(sim, link.Session, terminal.X + 10f, terminal.Z);
        Harness.Ticks(3);
        var far = Offers(sim, terminal)[0];
        Take(link, far.Id, far.Info.Serial);
        SessionHarness.Until(() => sim.Bridge.MissionTakesRefused >= 1, "the take from afar to be refused");
        Harness.Ticks(3);
        Assert.That(Refusals(link), Contains.Item(TatooineReplication.MissionTooFar));
        Assert.That(Held(sim, character), Is.Empty, "a take from afar was granted");

        Worlds.Park(sim, link.Session, terminal.X + 2f, terminal.Z, out var back);
        Harness.Ticks(3);
        for (var i = 0; i < TatooineData.MaxConcurrentMissions; i++)
        {
            var offer = Offers(sim, terminal)[0];
            SessionHarness.UntilNamed(link.Session, offer.Id, "the next offer to reach the view", back);
            TakeAndHold(sim, link, character, offer);
        }

        var fourth = Offers(sim, terminal)[0];
        SessionHarness.UntilNamed(link.Session, fourth.Id, "the fourth offer to reach the view");

        // A second for the command's bucket (2 a second, a burst of 4): this is the fifth take in a few, and the engine would refuse it for its rate before
        // the desk ever saw it — a refusal, but not the one this case is about.
        Harness.Ticks(Worlds.ServedHz);
        Take(link, fourth.Id, fourth.Info.Serial);
        SessionHarness.Until(() => sim.Bridge.MissionTakesRefused >= 2, "the fourth take to be refused");
        Harness.Ticks(3);
        Assert.Multiple(() =>
        {
            Assert.That(Refusals(link), Contains.Item(TatooineReplication.MissionCap));
            Assert.That(Held(sim, character), Has.Count.EqualTo(TatooineData.MaxConcurrentMissions));
            Assert.That(Exists(sim, fourth.Id), Is.True, "a refused take took the offer off the list");
        });
    }

    // ── M-4: the lair and the journal ───────────────────────────────────────────────────────────────────────────────

    /// <summary>M-4: a taken mission's lair stands round its anchor with its occupants, and the journal says where — its revision moved.</summary>
    [Test]
    [CancelAfter(120_000)]
    public void ATakenMissionsLairStandsRoundItsAnchor_AndTheJournalSaysWhere()
    {
        using var sim = Start();
        var terminal = FirstTerminal(sim);
        var link = LogInAt(sim, AnAccount(0x6004), terminal);
        var character = CharacterOf(sim, AnAccount(0x6004));
        var revBefore = BodyOf(sim, link.Session).JournalRev;
        var held = TakeAndHold(sim, link, character, Offers(sim, terminal)[0]);

        Lair spawner;
        LairPlacement place;
        using (var tx = sim.Dbe.CreateQuickTransaction())
        {
            var lair = tx.Open(held.Mission.Lair);
            spawner = lair.Read(CreatureLair.Spawner);
            place = lair.Read(CreatureLair.Bounds);
        }

        var journal = MissionJournal.Json(sim.Dbe, character);
        var dx = place.X - held.Mission.AnchorX;
        var dz = place.Z - held.Mission.AnchorZ;
        Assert.Multiple(() =>
        {
            Assert.That(MathF.Sqrt((dx * dx) + (dz * dz)), Is.LessThanOrEqualTo(TatooineData.MissionScatterM * sim.Config.ContentScale + 0.01f));
            Assert.That(spawner.CreatureTemplate, Is.EqualTo(held.Mission.Kind), "the lair is not the kind the offer named");
            Assert.That(spawner.AliveCount, Is.EqualTo(spawner.SpawnLimit).And.GreaterThan(0), "the lair has no occupants");
            Assert.That(spawner.Mission, Is.EqualTo(held.Id), "the lair does not name its mission");
            Assert.That(spawner.Site, Is.Zero, "a mission's lair belongs to no site, or the lair director would retire it");
            Assert.That(BodyOf(sim, link.Session).JournalRev, Is.GreaterThan(revBefore), "the journal's revision did not move");
            Assert.That(journal, Does.Contain($"\"id\":\"{held.Id.RawValue}\""));
            Assert.That(journal, Does.Contain($"\"x\":{held.Mission.LairX.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}"));
            Assert.That(journal, Does.StartWith("{\"missions\":[{").And.EndWith("\"completed\":[]}"));
        });
    }

    // ── M-5, M-6: the lair broken, or gone ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// M-5: the holder shoots its lair down, occupants already gone: paid the reward once, the mission and its lair gone, the journal moved.
    /// </summary>
    /// <remarks>
    /// Staged at the lair: the body parked beside it, its occupants taken away and its health one blow, so the case is the desk and not a fight.
    /// </remarks>
    [Test]
    [CancelAfter(120_000)]
    public void TheHolderBreaksItsLair_PaidOnce_TheMissionEnds()
    {
        using var sim = Start();
        var terminal = FirstTerminal(sim);
        var link = LogInAt(sim, AnAccount(0x6005), terminal);
        var character = CharacterOf(sim, AnAccount(0x6005));
        var held = TakeAndHold(sim, link, character, Offers(sim, terminal)[0]);
        var lair = held.Mission.Lair;

        Clear(sim, lair, health: 5);
        Worlds.Park(sim, link.Session, held.Mission.LairX + 10f, held.Mission.LairZ, out var parked);
        SessionHarness.UntilNamed(link.Session, lair, "the lair to reach the client's view", parked);
        var before = BodyOf(sim, link.Session);
        link.SendCommand(nameof(SetTarget), ClientSays.SetTarget(SessionHarness.NetIdOf(link.Session, lair)));
        link.SendCommand(nameof(SetStance), ClientSays.SetStance(Stances.Pistol));
        SessionHarness.Until(() => Held(sim, character).Count == 0, "the mission to end");

        // Past the next pass, or a second payment by it would go unseen.
        Harness.Ticks((Worlds.ServedHz / SimBridge.MissionPassHz) + 2);

        var after = BodyOf(sim, link.Session);
        var journal = MissionJournal.Json(sim.Dbe, character);

        // IL-4: its cache, minted in the desk's transaction that paid the reward and ended the mission: the same tick, once.
        var cache = Items.Rows(sim);
        var rewardTick = Ledgers.Rows(sim).Find(r => r.Kind == TransferKind.MissionReward).WorldTick;
        var cacheParts = cache.FindAll(r => ItemCatalog.TryGet(r.Template, out var t) && t.Category is ItemCategory.Core or ItemCategory.Addon);
        Assert.Multiple(() =>
        {
            Assert.That(cache, Is.Not.Empty, "the holder's broken lair dropped no cache");
            Assert.That(cache.TrueForAll(r => r.Kind == ItemMoveKind.Mission && r.Ref == (long)held.Id.RawValue && r.To == (long)character.RawValue), Is.True,
                "a cache row of another kind, mission or owner");
            Assert.That(cache.TrueForAll(r => r.WorldTick == rewardTick), Is.True, "the cache was not minted with the reward");
            Assert.That(cacheParts, Has.Count.EqualTo(1), "a cache holds exactly one core or add-on");
            Assert.That(cache.ConvertAll(r => r.Count).Sum() - 1, Is.EqualTo(LootTables.MissionCacheUnits));
            Assert.That(sim.Bridge.ItemDropsBy(DropSource.MissionCache), Is.EqualTo(1), "the cache was minted more than once");
            Assert.That(ItemEndpoints.Audit(sim.Dbe).Ok, Is.True, "the item audit after a cache");
        });

        Assert.Multiple(() =>
        {
            Assert.That(after.Credits - before.Credits, Is.EqualTo(held.Mission.Reward), "paid other than the reward, or more than once");
            var paid = Ledgers.Rows(sim).FindAll(r => r.Kind == TransferKind.MissionReward);
            Assert.That(paid, Has.Count.EqualTo(1), "one reward row per completion (E0-4)");
            Assert.That(paid[0].Ref, Is.EqualTo((long)held.Id.RawValue), "the row names the mission it paid");
            Assert.That(paid[0].To, Is.EqualTo((long)character.RawValue));
            Assert.That(paid[0].From, Is.EqualTo(EconomyLedger.World));
            Assert.That(paid[0].Amount, Is.EqualTo(held.Mission.Reward));
            Assert.That(EconomyEndpoints.Audit(sim.Dbe).Ok, Is.True, "the audit after a reward");
            Assert.That(Exists(sim, lair), Is.False, "the broken lair still stands");
            Assert.That(Exists(sim, held.Id), Is.False, "the mission is still held");
            Assert.That(after.JournalRev, Is.GreaterThan(before.JournalRev), "the journal did not move");
            Assert.That(journal, Does.StartWith("{\"missions\":[]"), "a completed mission is still in the journal");
            Assert.That(journal, Does.Contain($"\"completed\":[{{\"id\":\"{held.Id.RawValue}\""), "the journal does not say it was completed");
            Assert.That(journal, Does.Contain($"\"reward\":{held.Mission.Reward}}}"));
        });
    }

    /// <summary>
    /// lair-objectives.md B-1, B-3, from the frames a session is sent: at a terminal a bot's eye finds an offer listed there with its serial, the one whose
    /// lair is nearest; near a held mission's lair, it finds that lair with its occupants standing.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void ABotsEye_FindsTheOfferAtItsTerminal_AndItsMissionsLair()
    {
        using var sim = Start();
        var terminal = FirstTerminal(sim);
        var link = LogInAt(sim, AnAccount(0x6013), terminal);
        var character = CharacterOf(sim, AnAccount(0x6013));
        var offers = Offers(sim, terminal);
        Harness.Ticks(4);

        var store = new Typhon.Client.WorldStore(link.Plan);
        var eyes = new SwgTatooine.Bots.Brain.Perception();
        Replay(link, store);
        var offer = eyes.BestOffer(store, terminal.X + 2f, terminal.Z, 6f, out var serial);
        var nearest = int.MaxValue;
        foreach (var o in offers)
        {
            nearest = Math.Min(nearest, o.Info.DistanceM);
        }

        Assert.Multiple(() =>
        {
            Assert.That(offer, Is.Not.Zero, "no offer was seen at the terminal the bot stands at");
            Assert.That(offers.Exists(o => o.Info.Serial == serial && o.Info.DistanceM == nearest), Is.True, "the offer taken is not the nearest lair's");
            Assert.That(eyes.BestOffer(store, terminal.X + 200f, terminal.Z, 6f, out _), Is.Zero, "an offer was taken from 200 m away");
        });

        var held = TakeAndHold(sim, link, character, offers[0]);
        Worlds.Park(sim, link.Session, held.Mission.LairX + 15f, held.Mission.LairZ, out var parked);
        SessionHarness.UntilNamed(link.Session, held.Mission.Lair, "the lair in the bot's view", parked);
        Harness.Ticks(2);
        Replay(link, store);
        var lair = eyes.LairAt(store, held.Mission.LairX, held.Mission.LairZ, 30f, out var guards);
        Assert.Multiple(() =>
        {
            Assert.That(lair, Is.EqualTo(SessionHarness.NetIdOf(link.Session, held.Mission.Lair)), "the lair seen is not the mission's");
            Assert.That(guards, Is.GreaterThan(0), "a fresh mission lair's occupants were not seen standing");
        });

        // Its own stance, read rather than remembered: a swoop, a door, a flight or a clone puts the weapon away, and a bot that remembered drawing it
        // named targets for minutes without a shot (2026-10-26).
        var unarmed = eyes.Read(store, new SwgTatooine.Bots.Brain.FacilityMap()).Stance;
        link.SendCommand(nameof(SetStance), ClientSays.SetStance(Stances.Pistol));
        Harness.Ticks(4);
        Replay(link, store);
        var armed = eyes.Read(store, new SwgTatooine.Bots.Brain.FacilityMap()).Stance;
        Assert.Multiple(() =>
        {
            Assert.That(unarmed, Is.EqualTo(Stances.None), "the premise: no weapon drawn yet");
            Assert.That(armed, Is.EqualTo(Stances.Pistol), "the bot does not see the pistol it drew");
        });
    }

    /// <summary>
    /// CL-11, from real frames: <c>level</c> is on the wire, on enter, for every offer at the terminal, for a mission's lair and for every one of its
    /// defenders, and each decoded value is the stored one.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void TheLevel_IsOnTheWire()
    {
        using var sim = Start();
        var terminal = FirstTerminal(sim);
        var link = LogInAt(sim, AnAccount(0x6014), terminal);
        var character = CharacterOf(sim, AnAccount(0x6014));
        var offers = Offers(sim, terminal);
        Harness.Ticks(4);
        var store = new Typhon.Client.WorldStore(link.Plan);
        Replay(link, store);
        var seen = 0;
        Assert.Multiple(() =>
        {
            foreach (var (id, info) in offers)
            {
                var wire = LevelOnTheWire(store, SessionHarness.NetIdOf(link.Session, id));
                if (wire >= 0)
                {
                    seen++;
                    Assert.That(wire, Is.EqualTo(info.Level), $"offer {info.Serial}");
                }
            }
        });
        Assert.That(seen, Is.GreaterThan(0), "no offer's level was decoded");

        var held = TakeAndHold(sim, link, character, offers[0]);
        Worlds.Park(sim, link.Session, held.Mission.LairX + 15f, held.Mission.LairZ, out var parked);
        SessionHarness.UntilNamed(link.Session, held.Mission.Lair, "the lair in the view", parked);
        Harness.Ticks(4);
        store = new Typhon.Client.WorldStore(link.Plan);
        Replay(link, store);
        var defenders = new List<(EntityId Id, int Level)>();
        using (var tx = sim.Dbe.CreateQuickTransaction())
        {
            foreach (var cluster in tx.For<Creature>().GetClusterEnumerator())
            {
                var homes = cluster.GetReadOnlySpan(Creature.Home);
                var brains = cluster.GetReadOnlySpan(Creature.Ai);
                for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    EntityId home = homes[slot].Lair;
                    if (home == held.Mission.Lair)
                    {
                        defenders.Add((cluster.GetEntityId(slot), brains[slot].Level));
                    }
                }
            }
        }

        Assert.Multiple(() =>
        {
            Assert.That(LevelOnTheWire(store, SessionHarness.NetIdOf(link.Session, held.Mission.Lair)), Is.EqualTo(held.Mission.Level), "the lair's level");
            Assert.That(defenders, Is.Not.Empty, "the lair has no defenders");
            foreach (var (id, level) in defenders)
            {
                Assert.That(LevelOnTheWire(store, SessionHarness.NetIdOf(link.Session, id)), Is.EqualTo(level), $"defender {id}");
            }
        });
    }

    /// <summary>
    /// CL-13: at a terminal, a bot takes the nearest-lair offer within its CL + 3 when one is listed, and the nearest of all only when none is. STAGED
    /// before the session first sees the offers, so the two rules pick different ones on every draw: the nearest offers are made level 30, the rest 1.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void ABot_PrefersAnOfferWithinItsLevel()
    {
        using var sim = Start();
        Harness.Ticks(3);
        var terminal = FirstTerminal(sim);
        var offers = Offers(sim, terminal);
        offers.Sort((a, b) => a.Info.DistanceM.CompareTo(b.Info.DistanceM));
        var nearestM = offers[0].Info.DistanceM;
        Assume.That(offers[^1].Info.DistanceM, Is.GreaterThan(nearestM), "every offer at the terminal is as near as the nearest");
        sim.WriteInTick(tx =>
        {
            foreach (var (id, info) in offers)
            {
                var o = tx.OpenMut(id);
                var staged = o.Read(MissionOffer.Info);
                staged.Level = (byte)(info.DistanceM == nearestM ? 30 : 1);
                o.Set(MissionOffer.Info, staged);
            }
        });

        var link = LogInAt(sim, AnAccount(0x6015), terminal);
        Harness.Ticks(4);
        var store = new Typhon.Client.WorldStore(link.Plan);
        Replay(link, store);
        var inReachM = offers.Find(o => o.Info.DistanceM != nearestM).Info.DistanceM;

        var weak = new SwgTatooine.Bots.Brain.Perception { CombatLevel = 1 };
        var strong = new SwgTatooine.Bots.Brain.Perception { CombatLevel = 30 };
        Assert.Multiple(() =>
        {
            Assert.That(weak.BestOffer(store, terminal.X + 2f, terminal.Z, 6f, out var serial), Is.Not.Zero, "no offer was seen at the terminal");
            Assert.That(offers.Exists(o => o.Info.Serial == serial && o.Info.DistanceM == inReachM), Is.True,
                "a CL-1 bot did not take the nearest offer within its level");
            strong.BestOffer(store, terminal.X + 2f, terminal.Z, 6f, out var any);
            Assert.That(offers.Exists(o => o.Info.Serial == any && o.Info.DistanceM == nearestM), Is.True, "a CL-30 bot did not take the nearest offer");
        });
    }

    /// <summary>
    /// CL-8, staged: a wild lair above level 1 loses a creature and is owed it now; the creature the director refills it with carries the lair's level and
    /// the level's stats — not the ramp's at the gate, not level 1.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void ARefilledCreature_TakesItsLairsLevel()
    {
        using var sim = Start();
        Harness.Ticks(3);
        var lair = EntityId.Null;
        var level = 0;
        var gone = EntityId.Null;
        var before = new HashSet<EntityId>();
        using (var tx = sim.Dbe.CreateQuickTransaction())
        {
            foreach (var cluster in tx.For<CreatureLair>().GetClusterEnumerator())
            {
                var spawners = cluster.GetReadOnlySpan(CreatureLair.Spawner);
                for (var bits = cluster.OccupancyBits; bits != 0 && lair.IsNull; bits &= bits - 1)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    if (spawners[slot].Site != 0 && spawners[slot].Level > 1 && spawners[slot].AliveCount == spawners[slot].SpawnLimit)
                    {
                        lair = cluster.GetEntityId(slot);
                        level = spawners[slot].Level;
                    }
                }
            }

            foreach (var cluster in tx.For<Creature>().GetClusterEnumerator())
            {
                var homes = cluster.GetReadOnlySpan(Creature.Home);
                for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    EntityId home = homes[slot].Lair;
                    if (home == lair)
                    {
                        before.Add(cluster.GetEntityId(slot));
                        gone = cluster.GetEntityId(slot);
                    }
                }
            }
        }

        Assert.That(lair.IsNull, Is.False, "no full wild lair above level 1 to stage");
        sim.WriteInTick((tx, tick) =>
        {
            tx.Destroy(gone);
            var l = tx.OpenMut(lair);
            var s = l.Read(CreatureLair.Spawner);
            s.AliveCount--;
            s.Guards--;
            s.NextSpawnTick = tick + 1;
            l.Set(CreatureLair.Spawner, s);
        });
        before.Remove(gone);

        CreatureBrain brain = default;
        CreatureVitals vitals = default;
        SessionHarness.Until(
            () =>
            {
                using var tx = sim.Dbe.CreateQuickTransaction();
                foreach (var cluster in tx.For<Creature>().GetClusterEnumerator())
                {
                    var homes = cluster.GetReadOnlySpan(Creature.Home);
                    for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
                    {
                        var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                        EntityId home = homes[slot].Lair;
                        if (home == lair && !before.Contains(cluster.GetEntityId(slot)))
                        {
                            brain = cluster.GetReadOnlySpan(Creature.Ai)[slot];
                            vitals = cluster.GetReadOnlySpan(Creature.Vitals)[slot];
                            return true;
                        }
                    }
                }

                return false;
            },
            "the lair to refill its creature");
        var foe = CreatureTemplates.IsCamp(brain.Template);
        Assert.Multiple(() =>
        {
            Assert.That(brain.Level, Is.EqualTo(level), "the refilled creature is not at its lair's level");
            Assert.That(vitals.MaxHealth, Is.EqualTo(ContentLevels.CreatureHealth(level, foe)), "its health is not its level's");
            Assert.That(vitals.AttackDamage, Is.EqualTo(ContentLevels.CreatureHit(level, foe)), "its hit is not its level's");
        });
    }

    /// <summary>The <c>level</c> a store decoded for an entity, or -1 when it holds none.</summary>
    private static int LevelOnTheWire(Typhon.Client.WorldStore store, uint netId)
    {
        if (netId == 0 || !store.TryLocate(netId, out var archetype, out var slot))
        {
            return -1;
        }

        var set = store.Archetypes[archetype];
        var field = Array.FindIndex(set.Plan.Fields, f => f.Name == "level");
        return field < 0 || set.Numbers[field] == null ? -1 : (int)set.Numbers[field][slot];
    }

    private static void Replay(FakeLink link, Typhon.Client.WorldStore store)
    {
        var applier = new Typhon.Client.FrameApplier(store);
        foreach (var frame in link.Sent)
        {
            if (frame.Length > 0 && frame[0] == Typhon.Protocol.MessageTypes.Tick)
            {
                applier.Apply(frame);
            }
        }
    }

    /// <summary>
    /// lair-objectives.md L-5: the crowd never fights a mission's lair — a stranger's break would only redraw it (M-6). A scripted fighter is held in
    /// <c>Combat</c> ten metres from an unshielded mission lair for three seconds, and the lair is never hit.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void TheCrowd_NeverFightsAMissionsLair()
    {
        using var sim = Start();
        var terminal = FirstTerminal(sim);
        var link = LogInAt(sim, AnAccount(0x6012), terminal);
        var character = CharacterOf(sim, AnAccount(0x6012));
        var held = TakeAndHold(sim, link, character, Offers(sim, terminal)[0]);
        var lair = held.Mission.Lair;
        Clear(sim, lair, health: 5);

        var crowd = EntityId.Null;
        sim.WriteInTick(tx =>
        {
            foreach (var cluster in tx.For<Player>().GetClusterEnumerator())
            {
                var controls = cluster.GetReadOnlySpan(Player.Control);
                for (var bits = cluster.OccupancyBits; bits != 0 && crowd.IsNull; bits &= bits - 1)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    if (cluster.Realm.Value == 0 && controls[slot].Kind == ControllerKind.Scripted)
                    {
                        crowd = cluster.GetEntityId(slot);
                    }
                }
            }
        });
        Assert.That(crowd.IsNull, Is.False, "no scripted body to put beside the lair");

        // Through both paths: the target pick (in Combat beside it), and the objective — the mission's lair handed to the fighter as one, which the
        // once-a-second check must refuse to keep.
        for (var i = 0; i < 6; i++)
        {
            Worlds.Park(sim, crowd, held.Mission.LairX + 10f, held.Mission.LairZ, activity: PlayerActivity.Combat);
            sim.WriteInTick(tx =>
            {
                var body = tx.OpenMut(crowd);
                var state = body.Read(Player.State);
                state.MissionX = held.Mission.LairX;
                state.MissionZ = held.Mission.LairZ;
                body.Set(Player.State, state);
                var route = body.Read(Player.Route);
                route.Objective = 1;
                body.Set(Player.Route, route);
            });
            Harness.Ticks(Worlds.ServedHz / 2);
        }

        var keeps = true;
        sim.WriteInTick(tx =>
        {
            var body = tx.Open(crowd);
            var s = body.Read(Player.State);
            keeps = body.Read(Player.Route).Objective == 1 && s.MissionX == held.Mission.LairX && s.MissionZ == held.Mission.LairZ;
        });

        var health = 0;
        var ownerHit = -1;
        sim.WriteInTick(tx =>
        {
            if (tx.TryOpen(lair, out var e))
            {
                health = e.Read(CreatureLair.Vitals).Health;
                ownerHit = e.Read(CreatureLair.Spawner).OwnerHit;
            }
        });
        Assert.Multiple(() =>
        {
            Assert.That(Exists(sim, lair), Is.True, "the mission's lair was broken and drawn again: the crowd fought it");
            Assert.That(health, Is.EqualTo(5), "the crowd damaged a mission's lair");
            Assert.That(ownerHit, Is.Zero);
            Assert.That(Held(sim, character), Has.Count.EqualTo(1), "the mission was ended");
            Assert.That(keeps, Is.False, "the fighter kept a mission's lair as its objective");
        });
    }

    /// <summary>
    /// A fight with no lair objective — a fight-back, the world's first fighters — is not ended by the objective check, whose stale <c>MissionX</c> names no
    /// lair (review, 2026-10-26: it ended every fight-back within a second, and counted it as a fallen objective).
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void AFightWithNoObjective_IsNotEndedByTheObjectiveCheck()
    {
        using var sim = Start();
        var crowd = EntityId.Null;
        sim.WriteInTick(tx =>
        {
            foreach (var cluster in tx.For<Player>().GetClusterEnumerator())
            {
                var controls = cluster.GetReadOnlySpan(Player.Control);
                for (var bits = cluster.OccupancyBits; bits != 0 && crowd.IsNull; bits &= bits - 1)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    if (cluster.Realm.Value == 0 && controls[slot].Kind == ControllerKind.Scripted)
                    {
                        crowd = cluster.GetEntityId(slot);
                    }
                }
            }
        });

        var (x, z) = (sim.Config.WorldEdgeM * 0.4f, sim.Config.WorldEdgeM * 0.4f);
        Worlds.Park(sim, crowd, x, z, activity: PlayerActivity.Combat);
        var fallen = sim.Bridge.ObjectivesFallen;
        Harness.Ticks(Worlds.ServedHz * 3);
        var activity = -1;
        sim.WriteInTick(tx => activity = tx.Open(crowd).Read(Player.State).Activity);
        Assert.Multiple(() =>
        {
            Assert.That(activity, Is.EqualTo(PlayerActivity.Combat), "a fight with no objective was ended by the objective check");
            Assert.That(sim.Bridge.ObjectivesFallen, Is.EqualTo(fallen), "a fight with no objective was counted as an objective fallen");
        });
    }

    /// <summary>
    /// A held mission's lair stands with its occupants round it, and has a life. The pool missions replaced teleported a lair to its player and left them
    /// where they stood: 381 of 386 live mission camps had nobody near them (measured, 2026-10-25). Moved here from <c>LairLifeChecks</c> when the crowd stopped
    /// taking missions: a person's is the only kind there is.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void AHeldMissionsLair_StandsWithItsDefendersRoundIt_AndHasALife()
    {
        using var sim = Start();
        var terminal = FirstTerminal(sim);
        var link = LogInAt(sim, AnAccount(0x6011), terminal);
        var character = CharacterOf(sim, AnAccount(0x6011));
        var held = TakeAndHold(sim, link, character, Offers(sim, terminal)[0]);
        var lair = held.Mission.Lair;
        Harness.Ticks(3);

        var near = 0;
        var far = 0;
        var life = 0L;
        sim.WriteInTick(tx =>
        {
            var at = tx.Open(lair).Read(CreatureLair.Bounds);
            life = tx.Open(lair).Read(CreatureLair.Spawner).ExpireTick;
            foreach (var cluster in tx.For<Creature>().GetClusterEnumerator())
            {
                var homes = cluster.GetReadOnlySpan(Creature.Home);
                var places = cluster.GetReadOnlySpan(Creature.Bounds);
                for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    if (homes[slot].Lair.Id == lair)
                    {
                        var dx = places[slot].X - at.X;
                        var dz = places[slot].Z - at.Z;
                        if (MathF.Sqrt((dx * dx) + (dz * dz)) <= 30f)
                        {
                            near++;
                        }
                        else
                        {
                            far++;
                        }
                    }
                }
            }
        });
        Assert.Multiple(() =>
        {
            Assert.That(near, Is.GreaterThan(0), "a live mission lair has no defender");
            Assert.That(far, Is.Zero, $"{far} defenders stand far from their live mission lair, {near} near it");
            Assert.That(life, Is.Not.Zero, "a mission's lair has no life, so it would never move");
        });
    }

    /// <summary>M-6: a lair broken without its holder's blow is drawn again, the mission still held and unpaid.</summary>
    [Test]
    [CancelAfter(120_000)]
    public void ALairBrokenBySomebodyElse_IsDrawnAgain_TheMissionStillHeld()
    {
        using var sim = Start();
        var terminal = FirstTerminal(sim);
        var link = LogInAt(sim, AnAccount(0x6006), terminal);
        var character = CharacterOf(sim, AnAccount(0x6006));
        var held = TakeAndHold(sim, link, character, Offers(sim, terminal)[0]);
        var credits = BodyOf(sim, link.Session).Credits;

        sim.WriteInTick(tx =>
        {
            var l = tx.OpenMut(held.Mission.Lair);
            var v = l.Read(CreatureLair.Vitals);
            v.Health = 0;
            l.Set(CreatureLair.Vitals, v);
        });
        SessionHarness.Until(() => Held(sim, character)[0].Mission.Lair != held.Mission.Lair, "a new lair to be drawn");
        var now = Held(sim, character)[0];
        Assert.Multiple(() =>
        {
            Assert.That(now.Id, Is.EqualTo(held.Id), "the mission was replaced, not its lair");
            Assert.That(Exists(sim, held.Mission.Lair), Is.False, "the broken lair still stands");
            Assert.That(Exists(sim, now.Mission.Lair), Is.True);
            Assert.That(BodyOf(sim, link.Session).Credits, Is.EqualTo(credits), "paid for a lair somebody else broke");
            Assert.That(sim.Bridge.MissionLairsRedrawn, Is.GreaterThanOrEqualTo(1));
        });
    }

    /// <summary>
    /// CL-9: every offer carries the content level of its anchor — the ramp's exactly, its band beyond — and a kind whose range covers it.
    /// </summary>
    [Test]
    [CancelAfter(90_000)]
    public void AnOffer_CarriesTheLevelOfItsAnchor()
    {
        using var sim = Start();
        Harness.Ticks(3);
        var levels = sim.Index.Levels;
        var offers = 0;
        Assert.Multiple(() =>
        {
            foreach (var terminal in sim.Index.Terminals)
            {
                foreach (var (_, info) in Offers(sim, terminal))
                {
                    offers++;
                    Assert.That(levels.Agrees(info.AnchorX, info.AnchorZ, info.Level), Is.True,
                        $"an offer of level {info.Level} at {levels.EdgeDistance(info.AnchorX, info.AnchorZ):F0} m from a city's edge");
                    Assert.That(CreatureTemplates.CoversLevel(info.Kind, info.Level), Is.True,
                        $"{CreatureTemplates.Name(info.Kind)} offered at level {info.Level}");
                }
            }
        });
        Assert.That(offers, Is.GreaterThan(0), "no offer was listed");
    }

    /// <summary>
    /// CL-9, CL-11: a taken mission holds its offer's level, its lair and the lair's defenders are spawned at it, and the journal says it; drawn again after
    /// a stranger breaks it, the lair keeps the MISSION's level — set here to one no spot near the anchor has, so the redraw cannot have read the map.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void AMissionLair_KeepsItsLevel_WhenRedrawn()
    {
        using var sim = Start();
        var terminal = FirstTerminal(sim);
        var link = LogInAt(sim, AnAccount(0x6011), terminal);
        var character = CharacterOf(sim, AnAccount(0x6011));
        var offer = Offers(sim, terminal)[0];
        var held = TakeAndHold(sim, link, character, offer);
        Assert.Multiple(() =>
        {
            Assert.That(held.Mission.Level, Is.EqualTo(offer.Info.Level).And.GreaterThan(0), "the held mission is not at its offer's level");
            Assert.That(LairLevels(sim, held.Mission.Lair), Is.EqualTo((offer.Info.Level, offer.Info.Level)), "the lair or its defenders are off level");
            Assert.That(MissionJournal.Json(sim.Dbe, character), Does.Contain($"\"level\":{offer.Info.Level},"), "the journal does not say the level");
        });

        const byte promised = 47;
        sim.WriteInTick(tx =>
        {
            var m = tx.OpenMut(held.Id);
            var mission = m.Read(HeldMissionArch.Mission);
            mission.Level = promised;
            m.Set(HeldMissionArch.Mission, mission);
            var l = tx.OpenMut(held.Mission.Lair);
            var v = l.Read(CreatureLair.Vitals);
            v.Health = 0;
            l.Set(CreatureLair.Vitals, v);
        });
        SessionHarness.Until(() => Held(sim, character)[0].Mission.Lair != held.Mission.Lair, "a new lair to be drawn");
        var now = Held(sim, character)[0].Mission;
        Assert.That(LairLevels(sim, now.Lair), Is.EqualTo(((int)promised, (int)promised)), "the redrawn lair is not at the mission's level");
    }

    /// <summary>A lair's level, and its creatures' — the lowest of them, which is theirs when they agree.</summary>
    private static (int Lair, int Creatures) LairLevels(TatooineSim sim, EntityId lairId)
    {
        using var tx = sim.Dbe.CreateQuickTransaction();
        var lair = tx.Open(lairId).Read(CreatureLair.Spawner).Level;
        var lowest = int.MaxValue;
        var highest = 0;
        foreach (var cluster in tx.For<Creature>().GetClusterEnumerator())
        {
            var homes = cluster.GetReadOnlySpan(Creature.Home);
            var brains = cluster.GetReadOnlySpan(Creature.Ai);
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                EntityId home = homes[slot].Lair;
                if (home == lairId)
                {
                    lowest = Math.Min(lowest, brains[slot].Level);
                    highest = Math.Max(highest, brains[slot].Level);
                }
            }
        }

        return (lair, lowest == highest ? lowest : -1);
    }

    /// <summary>M-6: a lair whose life runs out moves — drawn again round the same anchor.</summary>
    [Test]
    [CancelAfter(120_000)]
    public void ALairWhoseLifeRunsOut_IsDrawnAgainRoundTheSameAnchor()
    {
        using var sim = Start();
        var terminal = FirstTerminal(sim);
        var link = LogInAt(sim, AnAccount(0x6007), terminal);
        var character = CharacterOf(sim, AnAccount(0x6007));
        var held = TakeAndHold(sim, link, character, Offers(sim, terminal)[0]);

        sim.WriteInTick(tx =>
        {
            var l = tx.OpenMut(held.Mission.Lair);
            var s = l.Read(CreatureLair.Spawner);
            s.ExpireTick = 1;
            l.Set(CreatureLair.Spawner, s);
        });
        SessionHarness.Until(() => Held(sim, character)[0].Mission.Lair != held.Mission.Lair, "the expired lair to move");
        var now = Held(sim, character)[0].Mission;
        var dx = now.LairX - now.AnchorX;
        var dz = now.LairZ - now.AnchorZ;
        Assert.Multiple(() =>
        {
            Assert.That(Exists(sim, held.Mission.Lair), Is.False);
            Assert.That(MathF.Sqrt((dx * dx) + (dz * dz)), Is.LessThanOrEqualTo(TatooineData.MissionScatterM * sim.Config.ContentScale + 0.01f));
        });
    }

    // ── M-7: offline, and across a restart ──────────────────────────────────────────────────────────────────────────

    /// <summary>M-7: a logout takes the lair down and keeps the mission; the next login draws it again — and both survive a restart.</summary>
    [Test]
    [CancelAfter(180_000)]
    public void ALogoutTakesTheLairDown_ALoginDrawsItAgain_AcrossARestart()
    {
        var account = AnAccount(0x6008);
        EntityId mission;
        using (var first = Start(persist: true))
        {
            var terminal = FirstTerminal(first);
            var link = LogInAt(first, account, terminal);
            var character = CharacterOf(first, account);
            var held = TakeAndHold(first, link, character, Offers(first, terminal)[0]);
            mission = held.Id;

            link.Close(CloseCodes.Normal, "logging out");
            SessionHarness.Until(() => TatooineReplication.AvatarsDestroyed >= 1, "the avatar to be destroyed");
            SessionHarness.Until(() => Held(first, character)[0].Mission.Lair.IsNull, "the offline holder's lair to go");
            Assert.That(Exists(first, held.Mission.Lair), Is.False, "an offline holder's lair still stands");

            // Back in: drawn again.
            Harness.Connect(TatooineReplication.PlayerKind, account);
            SessionHarness.Until(() => !Held(first, character)[0].Mission.Lair.IsNull, "the lair to be drawn again at login");
            Harness.Release();
        }

        TatooineReplication.ResetSessionAccounting();
        TatooineReplication.ResetIntentAccounting();

        using var second = Start(persist: true);
        Assert.That(second.Reopened, Is.True, "the second run must reopen the first's database, or this proves nothing");
        var reopened = CharacterOf(second, account);
        Harness.Ticks(2 * (Worlds.ServedHz / SimBridge.MissionPassHz));
        Assert.That(Held(second, reopened), Has.Count.EqualTo(1), "the held mission did not survive the restart");
        Assert.That(Held(second, reopened)[0].Id, Is.EqualTo(mission));
        Assert.That(Held(second, reopened)[0].Mission.Lair.IsNull, Is.True, "nobody is online, so no lair stands for it");

        Harness.Connect(TatooineReplication.PlayerKind, account);
        SessionHarness.Until(() => !Held(second, reopened)[0].Mission.Lair.IsNull, "the lair to be drawn at the first login after the restart");
    }

    // ── M-8: cancelling ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// M-8: a cancel destroys the mission and its lair, and is not stated as a completion; a cancel of somebody else's real mission, or of none, is refused.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void ACancelEndsTheMissionAndItsLair_AndAStrangersIsRefused()
    {
        using var sim = Start();
        var terminal = FirstTerminal(sim);
        var link = LogInAt(sim, AnAccount(0x6009), terminal, bindings: 1);
        var stranger = LogInAt(sim, AnAccount(0x600A), terminal, bindings: 2);
        var character = CharacterOf(sim, AnAccount(0x6009));
        var held = TakeAndHold(sim, link, character, Offers(sim, terminal)[0]);

        // The stranger names the real mission: the holder check, not the lookup, must refuse it.
        stranger.SendCommand(nameof(AbandonMission), new RecordValues { ["mission"] = FieldValue.OfUInt64(held.Id.RawValue) });
        SessionHarness.Until(() => Array.IndexOf(Refusals(stranger), TatooineReplication.MissionUnknown) >= 0, "the stranger's cancel to be refused");
        Assert.That(Held(sim, character), Has.Count.EqualTo(1), "a stranger cancelled somebody else's mission");

        link.SendCommand(nameof(AbandonMission), new RecordValues { ["mission"] = FieldValue.OfUInt64(held.Id.RawValue + 1_000_000UL) });
        link.SendCommand(nameof(AbandonMission), new RecordValues { ["mission"] = FieldValue.OfUInt64(held.Id.RawValue) });
        SessionHarness.Until(() => Held(sim, character).Count == 0, "the mission to be cancelled");
        Harness.Ticks(3);
        Assert.Multiple(() =>
        {
            Assert.That(Exists(sim, held.Mission.Lair), Is.False, "a cancelled mission's lair still stands");
            Assert.That(Refusals(link), Contains.Item(TatooineReplication.MissionUnknown), "a cancel of nobody's mission was not refused");
            Assert.That(Offers(sim, terminal), Has.Count.EqualTo(TatooineData.MissionOffersPerTerminal), "a cancel put nothing back, and took nothing");
            Assert.That(MissionJournal.Json(sim.Dbe, character), Is.EqualTo(MissionJournal.Empty), "a cancel was stated as a completion");

            // E0-4: taking cost nothing and cancelling paid nothing — the wallet holds its grant and no more, and no reward row was written.
            Assert.That(Ledgers.Balance(sim, character), Is.EqualTo(EconomyLedger.StartingCredits), "a take or a cancel moved credits");
            Assert.That(Ledgers.Rows(sim).FindAll(r => r.Kind == TransferKind.MissionReward), Is.Empty, "a cancelled mission was paid");
        });
    }

    /// <summary>
    /// A cancel and a take sent together by a character at the cap swap one mission: the cancel goes first (missions.md § 2, one session's order).
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void ACancelAndATakeSentTogetherAtTheCap_SwapOneMission()
    {
        using var sim = Start();
        var terminal = FirstTerminal(sim);
        var link = LogInAt(sim, AnAccount(0x600B), terminal);
        var character = CharacterOf(sim, AnAccount(0x600B));
        (EntityId Id, HeldMission Mission) first = default;
        for (var i = 0; i < TatooineData.MaxConcurrentMissions; i++)
        {
            var offer = Offers(sim, terminal)[0];
            SessionHarness.UntilNamed(link.Session, offer.Id, "the next offer to reach the view");
            var held = TakeAndHold(sim, link, character, offer);
            first = i == 0 ? held : first;
        }

        var fourth = Offers(sim, terminal)[0];
        SessionHarness.UntilNamed(link.Session, fourth.Id, "the fourth offer to reach the view");
        Harness.Ticks(Worlds.ServedHz);   // the take's bucket (2 a second, a burst of 4), as in M-3

        // Take sent BEFORE the cancel: arriving in one tick, the desk must still run the cancel first.
        Take(link, fourth.Id, fourth.Info.Serial);
        link.SendCommand(nameof(AbandonMission), new RecordValues { ["mission"] = FieldValue.OfUInt64(first.Id.RawValue) });
        SessionHarness.Until(() => !Exists(sim, first.Id), "the cancel to land");
        Harness.Ticks(3);
        Assert.Multiple(() =>
        {
            Assert.That(Refusals(link), Does.Not.Contain(TatooineReplication.MissionCap), "the take was judged before the cancel");
            Assert.That(Exists(sim, fourth.Id), Is.False, "the fourth offer was not taken");
            Assert.That(Held(sim, character), Has.Count.EqualTo(TatooineData.MaxConcurrentMissions));
        });
    }

    /// <summary>A take whose serial is not the offer's — the netId reused, or a list gone stale — is refused as taken, and the offer stays listed.</summary>
    [Test]
    [CancelAfter(90_000)]
    public void ATakeWithTheWrongSerial_IsRefusedTaken()
    {
        using var sim = Start();
        var terminal = FirstTerminal(sim);
        var link = LogInAt(sim, AnAccount(0x600C), terminal);
        var character = CharacterOf(sim, AnAccount(0x600C));
        var offer = Offers(sim, terminal)[0];

        Take(link, offer.Id, offer.Info.Serial + 1_000u);
        SessionHarness.Until(() => sim.Bridge.MissionTakesRefused >= 1, "the take to be refused");
        Harness.Ticks(3);
        Assert.Multiple(() =>
        {
            Assert.That(Refusals(link), Contains.Item(TatooineReplication.MissionTaken));
            Assert.That(Held(sim, character), Is.Empty);
            Assert.That(Exists(sim, offer.Id), Is.True, "a refused take took the offer off the list");
        });
    }

    // ── Offline, broken, dropped ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A holder who breaks the lair and logs out before the desk's next pass is paid at the next login: the broken lair waits, it is not taken down and
    /// drawn again unpaid.
    /// </summary>
    /// <remarks>
    /// The world is paused (the desk is gated, the sessions are not) so that the lair is broken and the body gone before the desk looks — the order a fast
    /// logout gives, staged rather than raced for.
    /// </remarks>
    [Test]
    [CancelAfter(180_000)]
    public void BrokenThenLoggedOutBeforeThePass_PaidAtTheNextLogin()
    {
        using var sim = Start();
        var terminal = FirstTerminal(sim);
        var account = AnAccount(0x600D);
        var link = LogInAt(sim, account, terminal);
        var character = CharacterOf(sim, account);
        var held = TakeAndHold(sim, link, character, Offers(sim, terminal)[0]);
        var credits = BodyOf(sim, link.Session).Credits;

        var god = Harness.Connect(TatooineReplication.GodKind);
        SessionHarness.Until(() => god.Received(MessageTypes.Welcome), "WELCOME");
        god.SendCommand("SetPaused", new RecordValues { ["paused"] = FieldValue.Of(1) });
        SessionHarness.Until(() => TatooineReplication.SimulationPaused, "the pause");
        sim.WriteInTick(tx =>
        {
            var l = tx.OpenMut(held.Mission.Lair);
            var s = l.Read(CreatureLair.Spawner);
            s.OwnerHit = 1;
            l.Set(CreatureLair.Spawner, s);
            var v = l.Read(CreatureLair.Vitals);
            v.Health = 0;
            l.Set(CreatureLair.Vitals, v);
        });
        var destroyed = TatooineReplication.AvatarsDestroyed;
        link.Close(CloseCodes.Normal, "logging out");
        SessionHarness.Until(() => TatooineReplication.AvatarsDestroyed > destroyed, "the avatar to be destroyed");
        god.SendCommand("SetPaused", new RecordValues { ["paused"] = FieldValue.Of(0) });
        SessionHarness.Until(() => !TatooineReplication.SimulationPaused, "the resume");

        Harness.Ticks(2 * (Worlds.ServedHz / SimBridge.MissionPassHz));
        Assert.Multiple(() =>
        {
            Assert.That(Held(sim, character), Has.Count.EqualTo(1), "the mission ended with nobody to pay");
            Assert.That(Exists(sim, held.Mission.Lair), Is.True, "the broken lair was taken down while its holder was away");
        });

        var back = Harness.Connect(TatooineReplication.PlayerKind, account);
        SessionHarness.Until(() => Held(sim, character).Count == 0, "the mission to complete at the login");
        Harness.Ticks(3);
        Assert.Multiple(() =>
        {
            Assert.That(BodyOf(sim, back.Session).Credits - credits, Is.EqualTo(held.Mission.Reward), "not paid at the login");
            Assert.That(Exists(sim, held.Mission.Lair), Is.False);
            Assert.That(MissionJournal.Json(sim.Dbe, character), Does.Contain($"\"completed\":[{{\"id\":\"{held.Id.RawValue}\""));
        });
    }

    /// <summary>
    /// A link that drops is not a logout (missions.md, Q6): the body is held for its resume, and the mission's lair stands through it, damage and all.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void ADroppedLink_KeepsTheLairStanding()
    {
        using var sim = Start();
        var terminal = FirstTerminal(sim);
        var link = LogInAt(sim, AnAccount(0x600E), terminal);
        var character = CharacterOf(sim, AnAccount(0x600E));
        var held = TakeAndHold(sim, link, character, Offers(sim, terminal)[0]);

        var holds = TatooineReplication.ResumesHeld;
        link.Close(CloseCodes.GoingAway, "the link died");
        SessionHarness.Until(() => TatooineReplication.ResumesHeld > holds, "the body to be held for a resume");
        Harness.Ticks(2 * (Worlds.ServedHz / SimBridge.MissionPassHz));
        Assert.Multiple(() =>
        {
            Assert.That(Held(sim, character)[0].Mission.Lair, Is.EqualTo(held.Mission.Lair), "the lair was taken down, or moved, for a dropped link");
            Assert.That(Exists(sim, held.Mission.Lair), Is.True);
        });
    }

    /// <summary>
    /// A reopened world's tick count starts again at 0, so stored expiries are clamped to their longest life from the reopen: an offer listed late in a long
    /// run does not stand for that run's length again. And a mission lair no held mission names is destroyed, not left standing for ever. (A held mission has
    /// no expiry since the crowd stopped taking them.)
    /// </summary>
    [Test]
    [CancelAfter(180_000)]
    public void AReopenedWorld_ClampsTheExpiriesItStored()
    {
        var account = AnAccount(0x600F);
        EntityId offerId;
        var orphan = EntityId.Null;
        const long FarFuture = 1L << 40;
        using (var first = Start(persist: true))
        {
            var terminal = FirstTerminal(first);
            var link = LogInAt(first, account, terminal);
            var character = CharacterOf(first, account);
            _ = TakeAndHold(first, link, character, Offers(first, terminal)[0]);
            offerId = Offers(first, terminal)[0].Id;
            first.WriteInTick(tx =>
            {
                var o = tx.OpenMut(offerId);
                var info = o.Read(MissionOffer.Info);
                info.ExpireTick = FarFuture;
                o.Set(MissionOffer.Info, info);

                // A wild lair made to look like a mission's whose mission is gone: what LoadDesk's sweep is for.
                foreach (var cluster in tx.For<CreatureLair>().GetClusterEnumerator())
                {
                    var spawners = cluster.GetReadOnlySpan(CreatureLair.Spawner);
                    for (var bits = cluster.OccupancyBits; bits != 0 && orphan.IsNull; bits &= bits - 1)
                    {
                        var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                        if (spawners[slot].Site != 0)
                        {
                            orphan = cluster.GetEntityId(slot);
                        }
                    }
                }

                var o2 = tx.OpenMut(orphan);
                var sp = o2.Read(CreatureLair.Spawner);
                sp.Mission = offerId;
                o2.Set(CreatureLair.Spawner, sp);
            });
            Harness.Release();
        }

        TatooineReplication.ResetSessionAccounting();
        TatooineReplication.ResetIntentAccounting();

        using var second = Start(persist: true);
        Assert.That(second.Reopened, Is.True, "the second run must reopen the first's database, or this proves nothing");
        Harness.Ticks(3);
        var hz = Worlds.ServedHz;
        var offerLife = (long)((TatooineData.MissionOfferLifeMinSeconds + TatooineData.MissionOfferLifeSpanSeconds) * hz);
        var now = second.Runtime.CurrentTickNumber;
        using var tx2 = second.Dbe.CreateQuickTransaction();
        Assert.Multiple(() =>
        {
            Assert.That(orphan.IsNull, Is.False, "the premise: a wild lair to orphan");
            Assert.That(tx2.TryOpen(orphan, out _), Is.False, "a mission lair no mission names still stands");
            Assert.That(second.Bridge.MissionLairsOrphaned, Is.EqualTo(1));
            Assert.That(tx2.Open(offerId).Read(MissionOffer.Info).ExpireTick, Is.LessThanOrEqualTo(now + offerLife), "the offer's expiry was not clamped");
        });
    }

    // ── The journal over HTTP ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>GET /swg/missions</c>: no token, a bad one, or an expired one from an account not playing is a 401; a good one answers its own character's journal
    /// — the bearer names the account, nothing in the request names a character — and an expired one is still good while its account plays.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void TheJournalEndpoint_AnswersItsBearersOwn()
    {
        using var sim = Start();
        var terminal = FirstTerminal(sim);
        var owner = AnAccount(0x6010);
        var link = LogInAt(sim, owner, terminal);
        var held = TakeAndHold(sim, link, CharacterOf(sim, owner), Offers(sim, terminal)[0]);

        (int Status, string Body) Get(string authorization)
        {
            var context = new DefaultHttpContext();
            if (authorization != null)
            {
                context.Request.Headers.Authorization = authorization;
            }

            return MissionJournal.GetForChecks(sim.Dbe, context) switch
            {
                ContentHttpResult c => (c.StatusCode ?? 200, c.ResponseContent),
                IStatusCodeHttpResult s => (s.StatusCode ?? 0, null),
                var other => throw new AssertionException($"unexpected result {other}"),
            };
        }

        var good = Get("Bearer " + AuthToken.Mint(owner));
        var lower = Get("bearer " + AuthToken.Mint(owner));
        var expiredPlaying = Get("Bearer " + AuthToken.Mint(owner, TimeSpan.FromSeconds(-10)));
        var idle = AnAccount(0x6011);
        Assert.Multiple(() =>
        {
            Assert.That(Get(null).Status, Is.EqualTo(401), "no token");
            Assert.That(Get("Bearer not-a-token").Status, Is.EqualTo(401), "a bad token");
            Assert.That(Get("Bearer " + AuthToken.Mint(idle, TimeSpan.FromSeconds(-10))).Status, Is.EqualTo(401), "an expired token, its account away");
            Assert.That(Get("Bearer " + AuthToken.Mint(idle)).Body, Is.EqualTo(MissionJournal.Empty), "an account with no character");
            Assert.That(good.Status, Is.EqualTo(200));
            Assert.That(good.Body, Does.Contain($"\"id\":\"{held.Id.RawValue}\""), "the bearer's own mission is missing");
            Assert.That(lower.Body, Is.EqualTo(good.Body), "the scheme is case-insensitive");
            Assert.That(expiredPlaying.Status, Is.EqualTo(200), "an expired token refused while its account plays");
        });
    }

    /// <summary>A lair's occupants taken away and its guard count with them, its health set: one blow from broken, nothing to fight it for.</summary>
    private static void Clear(TatooineSim sim, EntityId lair, int health)
        => sim.WriteInTick(tx =>
        {
            foreach (var cluster in tx.For<Creature>().GetClusterEnumerator())
            {
                var homes = cluster.GetReadOnlySpan(Creature.Home);
                for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    if ((EntityId)homes[slot].Lair == lair)
                    {
                        tx.Destroy(cluster.GetEntityId(slot));
                    }
                }
            }

            var l = tx.OpenMut(lair);
            var s = l.Read(CreatureLair.Spawner);
            s.Guards = 0;
            s.AliveCount = 0;
            l.Set(CreatureLair.Spawner, s);
            var v = l.Read(CreatureLair.Vitals);
            v.Health = health;
            l.Set(CreatureLair.Vitals, v);
        });
}
