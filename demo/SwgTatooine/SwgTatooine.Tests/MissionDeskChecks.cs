using System;
using System.Collections.Generic;
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
        config.CrowdTakesMissions = false;
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
        Worlds.Park(sim, link.Session, terminal.X + 2f, terminal.Z);
        var first = Offers(sim, terminal)[0].Id;
        SessionHarness.Until(() => TatooineReplication.NetIdOf(link.Session, first) != 0, "the terminal's offers to reach the client's view");
        return link;
    }

    private static EntityId AnAccount(ulong id) => EntityId.FromRawValue(id);

    private static MissionTerminalRef FirstTerminal(TatooineSim sim)
    {
        Assert.That(sim.Index.Terminals, Is.Not.Empty, "the premise: the world has a mission terminal");
        return sim.Index.Terminals[0];
    }

    /// <summary>The offers listed at a terminal, in no particular order.</summary>
    private static List<(EntityId Id, MissionOfferInfo Info)> Offers(TatooineSim sim, MissionTerminalRef terminal)
    {
        var rows = new List<(EntityId, MissionOfferInfo)>();
        using var tx = sim.Dbe.CreateQuickTransaction();
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
            ["offer"] = FieldValue.Of(TatooineReplication.NetIdOf(link.Session, offer)),
            ["serial"] = FieldValue.Of(serial),
        });

    /// <summary>Takes an offer and waits until the character holds one more mission than before, its lair drawn.</summary>
    private (EntityId Id, HeldMission Mission) TakeAndHold(TatooineSim sim, FakeLink link, EntityId character, (EntityId Id, MissionOfferInfo Info) offer)
    {
        var before = Held(sim, character).Count;
        Take(link, offer.Id, offer.Info.Serial);
        SessionHarness.Until(() => Held(sim, character).Count > before, "the mission to be held");
        var held = Held(sim, character).Find(h => h.Mission.Kind == offer.Info.Kind && h.Mission.AnchorX == offer.Info.AnchorX);
        Assert.That(held.Id.IsNull, Is.False, "the mission held is not the offer taken");
        SessionHarness.Until(() => !Held(sim, character).Find(h => h.Id == held.Id).Mission.Lair.IsNull, "the mission's lair to be drawn");
        return Held(sim, character).Find(h => h.Id == held.Id);
    }

    private static (int Credits, int JournalRev) BodyOf(TatooineSim sim, SessionId session)
    {
        var body = TatooineReplication.ControlledBy(sim.Dbe, session);
        using var tx = sim.Dbe.CreateQuickTransaction();
        var player = tx.Open(body);
        return ((int)player.Read(Player.Inventory).Credits, player.Read(Player.State).JournalRev);
    }

    private static bool Exists(TatooineSim sim, EntityId id)
    {
        using var tx = sim.Dbe.CreateQuickTransaction();
        return tx.TryOpen(id, out _);
    }

    /// <summary>Every <c>ACKS</c> reason this session was sent.</summary>
    private static byte[] Refusals(FakeLink link)
    {
        var sink = new AckSink { Reasons = [] };
        var frame = default(RealmFrame);
        var sent = link.Sent;
        for (var i = 0; i < sent.Length; i++)
        {
            if (sent[i].Length > 0 && sent[i][0] == MessageTypes.Tick)
            {
                TickReader.Read(sent[i], link.Plan, ref frame, ref sink);
            }
        }

        return [.. sink.Reasons];
    }

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
        SessionHarness.Until(() => TatooineReplication.NetIdOf(b.Session, offer.Id) != 0, "the offer to reach the second view");

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

        Worlds.Park(sim, link.Session, terminal.X + 2f, terminal.Z);
        Harness.Ticks(3);
        for (var i = 0; i < TatooineData.MaxConcurrentMissions; i++)
        {
            var offer = Offers(sim, terminal)[0];
            SessionHarness.Until(() => TatooineReplication.NetIdOf(link.Session, offer.Id) != 0, "the next offer to reach the view");
            TakeAndHold(sim, link, character, offer);
        }

        var fourth = Offers(sim, terminal)[0];
        SessionHarness.Until(() => TatooineReplication.NetIdOf(link.Session, fourth.Id) != 0, "the fourth offer to reach the view");

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
            Assert.That(MissionJournal.Json(sim.Dbe, CharacterOf(sim, AnAccount(0x6004))), Does.StartWith("[{"));
        });
    }

    // ── M-5, M-6: the lair broken, or gone ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// M-5: the holder shoots its lair down, occupants already gone: paid the reward once, the mission and its lair gone, the journal moved.
    /// </summary>
    /// <remarks>Staged at the lair: the body parked beside it, its occupants taken away and its health one blow, so the case is the desk and not a fight.</remarks>
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
        Worlds.Park(sim, link.Session, held.Mission.LairX + 10f, held.Mission.LairZ);
        SessionHarness.Until(() => TatooineReplication.NetIdOf(link.Session, lair) != 0, "the lair to reach the client's view");
        var before = BodyOf(sim, link.Session);
        link.SendCommand(nameof(SetTarget), ClientSays.SetTarget(TatooineReplication.NetIdOf(link.Session, lair)));
        link.SendCommand(nameof(SetStance), ClientSays.SetStance(Stances.Pistol));
        SessionHarness.Until(() => Held(sim, character).Count == 0, "the mission to end");
        Harness.Ticks(3);

        var after = BodyOf(sim, link.Session);
        Assert.Multiple(() =>
        {
            Assert.That(after.Credits - before.Credits, Is.EqualTo(held.Mission.Reward), "paid other than the reward, or more than once");
            Assert.That(Exists(sim, lair), Is.False, "the broken lair still stands");
            Assert.That(Exists(sim, held.Id), Is.False, "the mission is still held");
            Assert.That(after.JournalRev, Is.GreaterThan(before.JournalRev), "the journal did not move");
            Assert.That(MissionJournal.Json(sim.Dbe, character), Is.EqualTo("[]"));
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

    /// <summary>M-8: a cancel destroys the mission and its lair; a cancel naming no mission of the caller's is refused.</summary>
    [Test]
    [CancelAfter(120_000)]
    public void ACancelEndsTheMissionAndItsLair_AndAStrangersIsRefused()
    {
        using var sim = Start();
        var terminal = FirstTerminal(sim);
        var link = LogInAt(sim, AnAccount(0x6009), terminal);
        var character = CharacterOf(sim, AnAccount(0x6009));
        var held = TakeAndHold(sim, link, character, Offers(sim, terminal)[0]);

        link.SendCommand(nameof(AbandonMission), new RecordValues { ["mission"] = FieldValue.OfUInt64(held.Id.RawValue + 1_000_000UL) });
        link.SendCommand(nameof(AbandonMission), new RecordValues { ["mission"] = FieldValue.OfUInt64(held.Id.RawValue) });
        SessionHarness.Until(() => Held(sim, character).Count == 0, "the mission to be cancelled");
        Harness.Ticks(3);
        Assert.Multiple(() =>
        {
            Assert.That(Exists(sim, held.Mission.Lair), Is.False, "a cancelled mission's lair still stands");
            Assert.That(Refusals(link), Contains.Item(TatooineReplication.MissionUnknown), "a cancel of nobody's mission was not refused");
            Assert.That(Offers(sim, terminal), Has.Count.EqualTo(TatooineData.MissionOffersPerTerminal), "a cancel put nothing back, and took nothing");
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
