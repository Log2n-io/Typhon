using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using NUnit.Framework;
using SwgTatooine.Bots;
using SwgTatooine.Server;
using SwgTatooine.Server.Accounts;
using Typhon.Client;
using Typhon.Engine;
using Typhon.Protocol;

namespace SwgTatooine.Tests;

/// <summary>
/// The Horde (horde.md): an operator's raid on a city — its event, fortress, waves of marching raiders, the answers to its call, capped and decayed, its
/// status to every session, its end. Against a served world with real sessions.
/// </summary>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
internal sealed class HordeChecks : ServedChecks
{
    private const string City = "Bestine";

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

    private sealed record Player(FakeLink Link, EntityId Character)
    {
        public long Key => unchecked((long)Character.RawValue);
    }

    private Player LogIn(TatooineSim sim, ulong id, long bindings)
    {
        var account = TestAccounts.For(sim.Dbe, id);
        var link = Harness.Connect(TatooineReplication.PlayerKind, account);
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= bindings, "the avatar to be spawned");
        var character = EntityId.Null;
        SessionHarness.Until(() => CharacterStore.TryFind(sim.Dbe, account, out character, out _), "the account's character");
        SessionHarness.Until(() => Ledgers.TryWallet(sim, character, out _), "the wallet to be opened");
        Harness.Ticks(2);
        return new Player(link, character);
    }

    private static int Hz => Worlds.ServedHz;

    /// <summary>Asks for a horde on <see cref="City"/> and waits for its answer.</summary>
    private static HordeOutcome Horde(TatooineSim sim, Action<HordeRequest> knobs = null)
    {
        var request = new HordeRequest { City = City, StartsInS = 600, Waves = 2, WaveSize = 6, WavePeriodS = 3, TargetDefenders = 2, DurationS = 600 };
        knobs?.Invoke(request);
        var done = sim.Bridge.RequestHorde(request);
        SessionHarness.Until(() => done.IsCompleted, "the horde request's answer");
        return done.Result;
    }

    private static HordeCore Core(TatooineSim sim, long id)
    {
        using var tx = sim.Dbe.CreateQuickTransaction();
        return tx.Open(EntityId.FromRawValue((ulong)id)).Read(HordeArch.Core);
    }

    /// <summary>Every raider standing: its brain, by entity.</summary>
    private static List<(EntityId Id, CreatureBrain Ai, float X, float Z)> Raiders(TatooineSim sim)
    {
        var raiders = new List<(EntityId, CreatureBrain, float, float)>();
        using var tx = sim.Dbe.CreateQuickTransaction();
        foreach (var cluster in tx.For<Creature>().GetClusterEnumerator())
        {
            var ai = cluster.GetReadOnlySpan(Creature.Ai);
            var at = cluster.GetReadOnlySpan(Creature.Bounds);
            for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
            {
                var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                if (ai[slot].March != 0)
                {
                    raiders.Add((cluster.GetEntityId(slot), ai[slot], at[slot].X, at[slot].Z));
                }
            }
        }

        return raiders;
    }

    private static bool Exists(TatooineSim sim, long raw)
    {
        using var tx = sim.Dbe.CreateQuickTransaction();
        return raw != 0 && tx.TryOpen(EntityId.FromRawValue((ulong)raw), out _);
    }

    private static BotEvents Heard(FakeLink link)
    {
        var events = new BotEvents();
        var applier = new FrameApplier(new WorldStore(link.Plan), events);
        foreach (var message in link.Sent)
        {
            if (message.Length > 0 && message[0] == MessageTypes.Tick)
            {
                applier.Apply(message);
            }
        }

        return events;
    }

    private static List<BotHordeStatus> Statuses(FakeLink link) => [.. Heard(link).Hordes];

    private static List<BotHordeRally> Rallies(FakeLink link) => [.. Heard(link).Rallies];

    private static void Answer(FakeLink link, long hordeEvent)
        => link.SendCommand(nameof(AnswerCall), new RecordValues { ["event"] = FieldValue.OfUInt64((ulong)hordeEvent) });

    private static int Refused(FakeLink link, byte reason) => Ledgers.Refusals(link).Count(r => r == reason);

    /// <summary>
    /// The attendance is a wave's strength under the square law (Loïc, 2026-11-05: stronger raiders, fewer defenders): on level, a wave of 100 asks
    /// ⌈100 √(1 / 9.375) × 1.5⌉ = 49; raiders three times as healthy and half again as hard-hitting — 4.5 times the strength — ask 2.1 times as many, where
    /// 4.5 times the raiders ask 4.5 times; twice the wave, twice; never fewer than one nor past the cap.
    /// </summary>
    [Test]
    public void TheDefendersAskedFor_AreAWavesStrength_UnderTheSquareLaw()
    {
        int[] beasts = [CreatureTemplates.Rat, CreatureTemplates.Viper];
        int[] foes = [CreatureTemplates.Ninja, CreatureTemplates.Soldier];
        var onLevel = SimBridge.HordeDefendersFor(100, 10, beasts, 10, 100, 100);
        var stronger = SimBridge.HordeDefendersFor(100, 10, beasts, 10, 300, 150);
        var asMany = SimBridge.HordeDefendersFor(450, 10, beasts, 10, 100, 100);
        var twice = SimBridge.HordeDefendersFor(200, 10, beasts, 10, 100, 100);
        var campFoes = SimBridge.HordeDefendersFor(100, 10, foes, 10, 100, 100);
        Assert.Multiple(() =>
        {
            Assert.That(onLevel, Is.EqualTo(49), "on level, a wave of 100");
            Assert.That(SimBridge.HordeDefendersFor(100, 10, beasts, 10, 0, 0), Is.EqualTo(onLevel), "0 % is as it is (an event written before the knobs)");
            Assert.That(stronger, Is.InRange(onLevel * 20 / 10, onLevel * 22 / 10), "4.5 times the strength asks √4.5 times the defenders");
            Assert.That(stronger, Is.LessThan(asMany / 2), "stronger raiders ask fewer defenders than as much strength in weaker ones");
            Assert.That(twice, Is.InRange((onLevel * 2) - 1, onLevel * 2), "twice the wave asks twice the defenders");
            Assert.That(campFoes, Is.InRange(onLevel * 13 / 10, onLevel * 15 / 10), "a camp foe is 1.5 × the hit points and 1.3 × the damage: √1.95");
            Assert.That(SimBridge.HordeDefendersFor(1, 1, beasts, 50, 100, 100), Is.EqualTo(1));
            Assert.That(SimBridge.HordeDefendersFor(10_000, 50, foes, 1, 1000, 1000), Is.EqualTo(SimBridge.HordeMaxTargetDefenders));
        });
    }

    /// <summary>
    /// The fortress's pool is three minutes of the assault — 30 % of the defenders asked for — so ten times the attendance stands ten times as long a
    /// pool; the town hall's is three minutes of sixty of the raid's raiders, their hit included.
    /// </summary>
    [Test]
    public void TheFortress_StandsThreeMinutesOfTheAssault_WhateverTheAttendance()
    {
        int[] beasts = [CreatureTemplates.Rat, CreatureTemplates.Viper];
        var (hall, few) = SimBridge.SiegeHitPoints(28, beasts, 20, 100, 100);
        var (hardHall, many) = SimBridge.SiegeHitPoints(28, beasts, 20, 150, 1000);
        var playerDps = ContentLevels.CreatureHealth(20, false) / 8.0;
        Assert.Multiple(() =>
        {
            Assert.That(few, Is.EqualTo((int)(100 * SimBridge.HordeAssaultShare * SimBridge.HordeSiegeSeconds * playerDps)).Within(1),
                "30 defenders for 3 min");
            Assert.That(many, Is.EqualTo(few * 10).Within(10), "ten times the attendance, ten times the pool");
            Assert.That(hardHall, Is.EqualTo(hall * 3 / 2).Within(hall / 50), "raiders that hit half as hard again break the hall half as fast again");
        });
    }

    /// <summary>
    /// An answer's own window to arrive (horde.md § 5): twice its trip at the swoop's speed and a minute, at least two minutes, at most the event's timeout
    /// — which a timeout shorter than two minutes is the whole of.
    /// </summary>
    [Test]
    public void AnAnswer_IsGivenTwiceItsTrip_WithinTheTimeout()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SimBridge.AnswerWindowS(0f, 600), Is.EqualTo(SimBridge.HordeMinAnswerS), "next door: the floor");
            Assert.That(SimBridge.AnswerWindowS(8000f, 900), Is.EqualTo((int)((2 * 8000 / SimBridge.HordeTravelMps) + 60)),
                "8 km: twice the ride and a minute");
            Assert.That(SimBridge.AnswerWindowS(30_000f, 600), Is.EqualTo(600), "past the timeout: the timeout");
            Assert.That(SimBridge.AnswerWindowS(0f, 5), Is.EqualTo(5), "a timeout under the floor is the whole window");
        });
    }

    /// <summary>
    /// The siege (horde.md § 10): a horde stands its town hall's and its fortress's hit points, says them in its status; blows on the fortress take its
    /// pool down, and the fortress broken, the raid is won — its fortress and raiders gone; raiders at the town hall with nobody to fight strike it.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void TheSiege_TheFortressBroken_WinsTheRaid_AndRaidersAtTheTownHall_StrikeIt()
    {
        using var sim = Start();
        var started = Horde(sim, r =>
        {
            r.StartsInS = 0;
            r.Waves = 1;
            r.WaveSize = 6;
        });
        Assert.That(started.Ok, Is.True, started.Message);
        SessionHarness.Until(() => Raiders(sim).Count >= 6, "the wave");
        var core = Core(sim, started.Event);
        Assert.Multiple(() =>
        {
            Assert.That(core.HallMaxHp, Is.GreaterThan(1000), "no town hall pool");
            Assert.That(core.FortMaxHp, Is.GreaterThan(1000), "no fortress pool");
            Assert.That(core.Hall, Is.Not.Zero, "no town hall found for the city");
            Assert.That(sim.Bridge.HordeStatusJson, Does.Contain("\"hall\":255").And.Contain("\"fort\":255"), "the status does not say both whole");
        });

        // A raider at the town hall, with nobody to fight: the hall is struck.
        var raider = Raiders(sim)[0].Id;
        sim.WriteInTick(tx =>
        {
            var c = tx.OpenMut(raider);
            var b = c.Read(Creature.Bounds);
            b.SetAt(core.HallX + 20f, core.HallZ, b.Y, (b.Bounds.MaxX - b.Bounds.MinX) * 0.5f);
            c.Set(Creature.Bounds, b);
        });
        SessionHarness.Until(() => Core(sim, started.Event).HallHp < core.HallMaxHp, "the town hall to be struck");

        // The fortress's whole pool in one blow: the raid is won, and its fortress and raiders go.
        sim.WriteInTick(_ => sim.Bridge.SiegeBlow(EntityId.Null, core.FortMaxHp));
        SessionHarness.Until(() => Core(sim, started.Event).Phase == HordePhase.Won, "the raid won with its fortress broken");
        Harness.Ticks(Hz);
        Assert.Multiple(() =>
        {
            Assert.That(Core(sim, started.Event).FortHp, Is.Zero);
            Assert.That(Exists(sim, core.Fortress), Is.False, "the fortress still stands");
            Assert.That(Raiders(sim).Count(r => r.Ai.Mode != AiMode.Dead), Is.Zero, "raiders outlived their fortress");
        });
    }

    /// <summary>
    /// The garrison (horde.md § 10, Loïc 2026-11-04): a share of each wave holds the fortress — born at its gate, it stays round it while the rest march;
    /// and a fortress nobody breaks asks for more defenders once the call is full.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void AGarrison_HoldsTheFortress_AndAFortressNotFalling_AsksForMore()
    {
        using var sim = Start();
        sim.Bridge.HordeAdvanceWindowS = 2;
        var a = LogIn(sim, 0xB801, 1);
        var b = LogIn(sim, 0xB802, 2);
        var started = Horde(sim, r =>
        {
            r.StartsInS = 0;
            r.Waves = 1;
            r.WaveSize = 40;
            r.GarrisonPercent = 50;
        });
        Assert.That(started.Ok, Is.True, started.Message);
        Answer(a.Link, started.Event);
        Answer(b.Link, started.Event);
        SessionHarness.Until(() => Raiders(sim).Count >= 40, "the wave");
        var core = Core(sim, started.Event);
        Harness.Ticks(Hz * 6);

        var raiders = Raiders(sim);
        var garrison = raiders.Where(r => r.Ai.March == SimBridge.RaiderGarrison).ToList();
        static float Dist(float x, float z, float tx, float tz) => MathF.Sqrt(((x - tx) * (x - tx)) + ((z - tz) * (z - tz)));
        SessionHarness.Until(() => Core(sim, started.Event).TargetDefenders > 2, "the attendance raised: the fortress is not falling");
        Assert.Multiple(() =>
        {
            Assert.That(garrison.Count, Is.InRange(10, 30), "about half the wave holds the fortress");
            Assert.That(raiders.Count(r => r.Ai.March == SimBridge.RaiderMarches), Is.InRange(10, 30), "the rest march");
            Assert.That(garrison.All(r => Dist(r.X, r.Z, core.FortX, core.FortZ) <= 32f + SimBridge.HordeGarrisonLeashM + 5f), Is.True,
                "a garrison raider left the fortress");
            Assert.That(garrison.All(r => r.Ai.HomeX != core.TargetX || r.Ai.HomeZ != core.TargetZ), Is.True, "a garrison raider's home is the town");
        });
    }

    /// <summary>A whole wave in the garrison — nobody marches — and the fortress untouched: the call full, more defenders are asked for all the same.</summary>
    [Test]
    [CancelAfter(120_000)]
    public void AFortressNotFalling_AsksForMore_WithNoMarchToJudge()
    {
        using var sim = Start();
        sim.Bridge.HordeAdvanceWindowS = 2;
        var a = LogIn(sim, 0xB901, 1);
        var b = LogIn(sim, 0xB902, 2);
        var started = Horde(sim, r =>
        {
            r.StartsInS = 0;
            r.Waves = 1;
            r.WaveSize = 10;
            r.GarrisonPercent = 100;
        });
        Assert.That(started.Ok, Is.True, started.Message);
        Answer(a.Link, started.Event);
        Answer(b.Link, started.Event);
        SessionHarness.Until(() => Raiders(sim).Count >= 10, "the wave");
        Assert.That(Raiders(sim).All(r => r.Ai.March == SimBridge.RaiderGarrison), Is.True, "a raider marches");
        SessionHarness.Until(() => Core(sim, started.Event).TargetDefenders > 2, "the attendance raised by the fortress's pace alone");
    }

    /// <summary>
    /// A raid's people are armed and notice far (Loïc, 2026-11-04: "some should have a blaster"; killing comes first): about half a blaster, the rest a
    /// blade or their fists, and every raider turns on a player within <see cref="SimBridge.RaiderNoticeM"/>.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void Raiders_AreArmed_AndNoticeFar()
    {
        using var sim = Start();
        var started = Horde(sim, r =>
        {
            r.StartsInS = 0;
            r.Waves = 1;
            r.WaveSize = 120;
            r.TemplateMask = (1 << CreatureTemplates.Ninja) | (1 << CreatureTemplates.Soldier);
        });
        Assert.That(started.Ok, Is.True, started.Message);
        SessionHarness.Until(() => Raiders(sim).Count >= 120, "the wave");
        var raiders = Raiders(sim);
        var blasters = raiders.Count(r => r.Ai.Stance == Stances.Pistol);
        Assert.Multiple(() =>
        {
            Assert.That(raiders.All(r => r.Ai.Stance is Stances.Pistol or Stances.OneHanded or Stances.Unarmed), Is.True, "an unarmed camp raider");
            Assert.That(blasters, Is.InRange(40, 80), "about half carry a blaster");
            Assert.That(raiders.Count(r => r.Ai.Stance == Stances.OneHanded), Is.GreaterThan(15), "some carry a blade");
            Assert.That(raiders.All(r => r.Ai.AggroRadius >= SimBridge.RaiderNoticeM), Is.True, "a raider that notices less than 60 m");
        });
    }

    /// <summary>
    /// Raiders collide with structures (Loïc, 2026-11-04: they walked through the town hall, then stood stuck inside the fortress): a wave is born clear of
    /// the fortress's walls and marches; and one put behind the fortress, whose march runs straight at it, is turned by its walls and never stands inside.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void Raiders_AreBornClearOfTheFortress_AndCollideWithIt()
    {
        using var sim = Start();
        var started = Horde(sim, r =>
        {
            r.StartsInS = 0;
            r.Waves = 1;
            r.WaveSize = 12;
        });
        Assert.That(started.Ok, Is.True, started.Message);
        SessionHarness.Until(() => Raiders(sim).Count >= 12, "the wave");
        var core = Core(sim, started.Event);
        var born = Raiders(sim);
        static bool InFootprint(float x, float z, in HordeCore c) => MathF.Abs(x - c.FortX) < 16f && MathF.Abs(z - c.FortZ) < 16f;
        static float Dist(float x, float z, float tx, float tz) => MathF.Sqrt(((x - tx) * (x - tx)) + ((z - tz) * (z - tz)));

        // Behind the fortress, on the line from the target through it: the march goes through the walls, or round them.
        var dx = core.FortX - core.TargetX;
        var dz = core.FortZ - core.TargetZ;
        var d = MathF.Sqrt((dx * dx) + (dz * dz));
        var (bx, bz) = (core.FortX + (dx / d * 24f), core.FortZ + (dz / d * 24f));
        var behind = born[0].Id;
        sim.WriteInTick(tx =>
        {
            var c = tx.OpenMut(behind);
            var b = c.Read(Creature.Bounds);
            b.SetAt(bx, bz, b.Y, (b.Bounds.MaxX - b.Bounds.MinX) * 0.5f);
            c.Set(Creature.Bounds, b);
        });

        var contactsBefore = sim.Bridge.RaiderContacts;
        var inside = 0;
        for (var s = 0; s < 16; s++)
        {
            Harness.Ticks(Hz / 2);
            var it = Raiders(sim).First(r => r.Id == behind);
            inside += InFootprint(it.X, it.Z, in core) ? 1 : 0;
        }

        var later = Raiders(sim);
        Assert.Multiple(() =>
        {
            Assert.That(born.Count(r => InFootprint(r.X, r.Z, in core)), Is.Zero, "a raider born inside the fortress's footprint: it could not walk out");
            Assert.That(sim.Bridge.RaiderContacts, Is.GreaterThan(contactsBefore), "the march behind the fortress met no wall");
            Assert.That(inside, Is.Zero, "the raider behind the fortress walked into it");
            Assert.That(later.Where(r => r.Id != behind).Average(r => Dist(r.X, r.Z, core.TargetX, core.TargetZ)),
                Is.LessThan(born.Where(r => r.Id != behind).Average(r => Dist(r.X, r.Z, core.TargetX, core.TargetZ))), "the raiders stopped marching");
        });
    }

    /// <summary>
    /// A horde nobody slows asks for more help (Loïc, 2026-11-04): raiders marching unopposed make most of their walk over a window, so the attendance asked
    /// for grows — and the status says so.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void AnUnopposedMarch_RaisesTheDefendersAskedFor()
    {
        using var sim = Start();
        sim.Bridge.HordeAdvanceWindowS = 2;
        var a = LogIn(sim, 0xB701, 1);
        var b = LogIn(sim, 0xB702, 2);
        var started = Horde(sim, r =>
        {
            r.StartsInS = 0;
            r.Waves = 1;
        });
        Assert.That(started.Ok, Is.True, started.Message);

        // The call full: the attendance is what holds the defence back, so a march that is not slowed raises it (an open call would not).
        Answer(a.Link, started.Event);
        Answer(b.Link, started.Event);
        SessionHarness.Until(() => Core(sim, started.Event).TargetDefenders > 2, "the attendance to be raised");

        var report = JsonDocument.Parse(sim.Bridge.HordeReportJson).RootElement.GetProperty("defenders");
        Assert.Multiple(() =>
        {
            Assert.That(report.GetProperty("raises").GetInt32(), Is.GreaterThanOrEqualTo(1));
            Assert.That(report.GetProperty("advance").GetDouble(), Is.GreaterThan(SimBridge.HordeUnslowedShare), "the march was judged slowed");
            Assert.That(Core(sim, started.Event).TargetDefenders, Is.EqualTo(2 + 5), "a quarter, at least five");
        });
    }

    /// <summary>
    /// An operator's distance (<see cref="HordeRequest.FortressM"/>) stands the fortress that far beyond the city's edge, and its gather point with it.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void AFortressAskedFarther_StandsThatFarBeyondTheCitysEdge()
    {
        using var sim = Start();
        var started = Horde(sim, r => r.FortressM = 500);
        Assert.That(started.Ok, Is.True, started.Message);
        var core = Core(sim, started.Event);
        var city = sim.Bridge.CityForTests(City);
        var (rx, rz) = sim.Bridge.RallyOf(core);
        Assert.Multiple(() =>
        {
            Assert.That(Dist(core.FortX, core.FortZ, city.X, city.Z), Is.InRange(city.Radius + 500f, city.Radius + 650f), "500-650 m beyond the edge");
            Assert.That(Dist(rx, rz, city.X, city.Z), Is.GreaterThan(city.Radius + 500f - SimBridge.HordeRallyFromFortM - 1f), "the gather point went with it");
        });

        static float Dist(float x, float z, float tx, float tz) => MathF.Sqrt(((x - tx) * (x - tx)) + ((z - tz) * (z - tz)));
    }

    /// <summary>
    /// AC-1, AC-2: the event stands its fortress beyond the city's edge, facing it, mails every character, and tells every session its status; a second
    /// horde is refused while it runs; a steady countdown sends nothing more.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void AHorde_StandsItsFortress_MailsEveryone_AndTellsEverySession()
    {
        using var sim = Start();
        var a = LogIn(sim, 0xB101, 1);
        var started = Horde(sim);
        Assert.That(started.Ok, Is.True, started.Message);
        Harness.Ticks(Hz * 2);

        var core = Core(sim, started.Event);
        var city = sim.Bridge.CityForTests(City);
        var fromCity = MathF.Sqrt(((core.FortX - city.X) * (core.FortX - city.X)) + ((core.FortZ - city.Z) * (core.FortZ - city.Z)));
        var statuses = Statuses(a.Link);
        var second = Horde(sim);
        var sent = TatooineReplication.HordeStatusesSent;
        Harness.Ticks(Hz * 3);

        var inbox = JsonDocument.Parse(MailEndpoints.InboxJson(sim.Dbe, a.Key)).RootElement.GetProperty("mails").EnumerateArray().ToList();
        Assert.Multiple(() =>
        {
            Assert.That(core.Phase, Is.EqualTo(HordePhase.Incoming), "announced, not started");
            Assert.That(Exists(sim, core.Fortress) && Exists(sim, core.LairA) && Exists(sim, core.LairB), Is.True, "the fortress and its two lairs");
            Assert.That(fromCity, Is.InRange(city.Radius + 250f, city.Radius + 400f), "the fortress stands 250-400 m beyond the city's edge");
            Assert.That(core.TargetZ - city.Z, Is.InRange(15f, 40f), "the target is the square between the shuttleport, at the centre, and the town hall 54 m north");
            Assert.That(inbox.Any(m => m.GetProperty("kind").GetString() == "horde"), Is.True, "the character was mailed");
            Assert.That(statuses.Select(s => (s.Event, s.Phase)), Does.Contain(((ulong)started.Event, (int)HordePhase.Incoming)), "the session heard it");
            Assert.That(statuses.Last().StartsInS, Is.InRange(590, 600));
            Assert.That(second.Ok, Is.False, "a second horde while one runs");
            Assert.That(TatooineReplication.HordeStatusesSent, Is.EqualTo(sent), "a steady countdown sent statuses (AC-2)");
            Assert.That(sim.Bridge.HordeStatusJson, Does.Contain("\"cityName\":\"Whitestone\""), "GET /swg/horde, for a client that connects in the middle");
        });
    }

    /// <summary>AC-5: the waves follow the knobs — size, period, count — and each raider marches on the target from the gate, its leash past the fortress.</summary>
    [Test]
    [CancelAfter(120_000)]
    public void TheWaves_FollowTheKnobs_AndTheirRaidersMarchOnTheTarget()
    {
        using var sim = Start();
        var started = Horde(sim, r => r.StartsInS = 0);
        Assert.That(started.Ok, Is.True, started.Message);
        SessionHarness.Until(() => Raiders(sim).Count >= 6, "the first wave");
        var first = Raiders(sim);
        var core = Core(sim, started.Event);
        Harness.Ticks(Hz * 2);
        var walked = Raiders(sim);
        SessionHarness.Until(() => Raiders(sim).Count >= 12, "the second wave, a period later");
        Harness.Ticks(Hz * 5);

        var fort = MathF.Sqrt(((core.TargetX - core.FortX) * (core.TargetX - core.FortX)) + ((core.TargetZ - core.FortZ) * (core.TargetZ - core.FortZ)));
        float Gap(float x, float z) => MathF.Sqrt(((core.TargetX - x) * (core.TargetX - x)) + ((core.TargetZ - z) * (core.TargetZ - z)));
        Assert.Multiple(() =>
        {
            Assert.That(first, Has.Count.EqualTo(6), "the first wave is six");
            Assert.That(first.All(r => r.Ai.HomeX == core.TargetX && r.Ai.HomeZ == core.TargetZ), Is.True, "a raider's home is the target");
            Assert.That(first.All(r => r.Ai.LeashRadius > fort), Is.True, "its leash reaches back past the fortress");
            Assert.That(first.All(r => Gap(r.X, r.Z) < fort + 30f), Is.True, "born at the gate");
            Assert.That(walked.Average(r => Gap(r.X, r.Z)), Is.LessThan(first.Average(r => Gap(r.X, r.Z)) - 1f), "two seconds on, closer to the target");
            Assert.That(Raiders(sim), Has.Count.EqualTo(12), "two waves, no third");
            Assert.That(Core(sim, started.Event).WavesSpawned, Is.EqualTo(2));
        });

        // Stronger raiders (Loïc, 2026-11-05): the default knobs, three times the health and half again the hit of a creature of their level.
        using var tx = sim.Dbe.CreateQuickTransaction();
        foreach (var (id, ai, _, _) in Raiders(sim))
        {
            var vitals = tx.Open(id).Read(Creature.Vitals);
            var foe = CreatureTemplates.IsCamp(ai.Template);
            Assert.That((vitals.MaxHealth, vitals.AttackDamage),
                Is.EqualTo((ContentLevels.CreatureHealth(ai.Level, foe) * 3, SimBridge.Scaled(ContentLevels.CreatureHit(ai.Level, foe), 150))),
                "a raider's health and hit are not its raid's power");
        }
    }

    /// <summary>
    /// AC-3 (D2): an answer is committed at once and told its rally point; past the attendance asked for, an answer is refused "enough"; a second answer
    /// from the same character is refused; the status says the help needed.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void AnAnswer_IsCommittedAtOnce_AndThoseAfterTheTarget_AreRefused()
    {
        using var sim = Start();
        var a = LogIn(sim, 0xB201, 1);
        var b = LogIn(sim, 0xB202, 2);
        var started = Horde(sim, r => r.TargetDefenders = 1);
        Harness.Ticks(Hz);
        Answer(a.Link, started.Event);
        SessionHarness.Until(() => Rallies(a.Link).Count > 0, "A's rally point");
        Answer(b.Link, started.Event);
        Answer(a.Link, started.Event);
        SessionHarness.Until(() => Refused(b.Link, TatooineReplication.HordeEnough) == 1 && Refused(a.Link, TatooineReplication.HordeAnswered) == 1,
            "B refused enough, A refused a second answer");
        Harness.Ticks(Hz * 2);

        var rally = Rallies(a.Link)[0];
        var core = Core(sim, started.Event);
        var city = sim.Bridge.CityForTests(City);
        static float Dist(float x, float z, float tx, float tz) => MathF.Sqrt(((x - tx) * (x - tx)) + ((z - tz) * (z - tz)));
        Assert.Multiple(() =>
        {
            Assert.That((rally.Event, rally.State), Is.EqualTo(((ulong)started.Event, (int)ResponderState.Committed)));
            Assert.That((rally.X, rally.Z), Is.EqualTo(sim.Bridge.RallyOf(in core)), "the gather point");
            Assert.That(Rallies(a.Link), Has.Count.GreaterThanOrEqualTo(2), "a second answer, refused, is told its gather point again (a session that reconnected)");
            Assert.That(Dist(rally.X, rally.Z, core.FortX, core.FortZ), Is.EqualTo(SimBridge.HordeRallyFromFortM).Within(0.01f), "the gather point's distance from the fortress");
            Assert.That(Dist(rally.X, rally.Z, city.X, city.Z) + SimBridge.HordeRallyFromFortM,
                Is.EqualTo(Dist(core.FortX, core.FortZ, city.X, city.Z)).Within(0.05f), "on the line from the fortress to the city's centre");
            Assert.That(Dist(core.TargetX, core.TargetZ, city.X, city.Z), Is.LessThan(60f), "the raiders' target is the square by the shuttleport");
            Assert.That(Statuses(b.Link).Last().Help, Is.EqualTo(255), "one of one: enough");
            Assert.That(Statuses(b.Link).Last().Committed, Is.EqualTo(1));
        });
    }

    /// <summary>AC-4: a commitment that does not arrive by its timeout decays out of the count, is told so, and waits its cooldown before it may answer again.</summary>
    [Test]
    [CancelAfter(120_000)]
    public void ACommitmentThatDoesNotArrive_Decays_AndWaitsItsCooldown()
    {
        using var sim = Start();
        var a = LogIn(sim, 0xB301, 1);
        var started = Horde(sim, r =>
        {
            r.CommitTimeoutS = 5;
            r.CooldownS = 60;
        });
        Harness.Ticks(Hz);
        Answer(a.Link, started.Event);
        SessionHarness.Until(() => Rallies(a.Link).Any(r => r.State == ResponderState.Committed), "committed");
        SessionHarness.Until(() => Rallies(a.Link).Any(r => r.State == ResponderState.Decayed), "decayed after its 5 s");
        Harness.Ticks(Hz * 2);
        Answer(a.Link, started.Event);
        SessionHarness.Until(() => Refused(a.Link, TatooineReplication.HordeCooldown) == 1, "refused while its cooldown runs");
        using var report = JsonDocument.Parse(sim.Bridge.HordeReportJson);
        Assert.Multiple(() =>
        {
            Assert.That(Statuses(a.Link).Last().Committed, Is.Zero, "out of the count");
            Assert.That(report.RootElement.GetProperty("defenders").GetProperty("decay").GetProperty("late").GetInt32(), Is.EqualTo(1), "not counted late");
        });
    }

    /// <summary>
    /// Loïc, 2026-11-05 (the second 4K raid: 1 688 answers decayed late, most of them on their way): a commitment past its window does not decay while
    /// the defender keeps coming — HordeProgressM nearer the fight within HordeProgressS — and arrives as any other.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void ACommitmentPastItsWindow_IsKept_WhileTheDefenderKeepsComing()
    {
        using var sim = Start();
        var a = LogIn(sim, 0xB311, 1);
        var started = Horde(sim, r => r.CommitTimeoutS = 5);
        Harness.Ticks(Hz);
        var (rx, rz) = sim.Bridge.RallyOf(Core(sim, started.Event));
        Move(sim, a, rx + 400f, rz);
        Harness.Ticks(Hz);
        Answer(a.Link, started.Event);
        SessionHarness.Until(() => Rallies(a.Link).Any(r => r.State == ResponderState.Committed), "committed");
        for (var m = 375f; m > 0f; m -= 25f)
        {
            Move(sim, a, rx + m, rz);
            Harness.Ticks(Hz);
        }

        SessionHarness.Until(() => Rallies(a.Link).Any(r => r.State == ResponderState.Arrived), "arrived, 15 s into a 5 s window");
        Assert.That(Rallies(a.Link).Any(r => r.State == ResponderState.Decayed), Is.False, "it decayed on its way");
    }

    /// <summary>A character's answer, as persisted.</summary>
    private static HordeResponder Responder(TatooineSim sim, Player who)
    {
        using var tx = sim.Dbe.CreateQuickTransaction();
        foreach (var found in tx.Query<HordeResponderArch>())
        {
            var r = found.Read(HordeResponderArch.Core);
            if (r.Character == who.Key)
            {
                return r;
            }
        }

        Assert.Fail("no answer persisted for the character");
        return default;
    }

    /// <summary>Puts a character's body at a point, on the tick, as a teleport.</summary>
    private static void Move(TatooineSim sim, Player who, float x, float z)
    {
        sim.WriteInTick(tx =>
        {
            var bodies = new Dictionary<EntityId, EntityId>();
            TatooineReplication.FillCharacterBodies(bodies);
            var body = bodies[who.Character];
            var at = default(PlayerPlacement);
            at.SetAt(x, z, 0f, tx.Open(body).Read(SwgTatooine.Server.Player.Bounds).HalfExtent);
            tx.Teleport(body, SwgTatooine.Server.Player.Bounds, RealmId.Default, in at);
        });
    }

    /// <summary>
    /// An arrived defender away from the fight — downed out there, or woken at its clone — is committed again, with the event's timeout to come back,
    /// rather than out of the count (Loïc, 2026-11-05); back at the gather point it has arrived again, and the report times its first arrival only.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void AnArrivedDefender_AwayFromTheFight_IsCommittedAgain_NotDecayed()
    {
        using var sim = Start();
        var a = LogIn(sim, 0xB401, 1);
        var started = Horde(sim, r => r.CommitTimeoutS = 600);
        Harness.Ticks(Hz);
        Answer(a.Link, started.Event);
        SessionHarness.Until(() => Rallies(a.Link).Any(r => r.State == ResponderState.Committed), "committed");
        var (rx, rz) = sim.Bridge.RallyOf(Core(sim, started.Event));
        var answered = Responder(sim, a);
        Move(sim, a, rx, rz);
        SessionHarness.Until(() => Rallies(a.Link).Any(r => r.State == ResponderState.Arrived), "arrived at the gather point");
        Move(sim, a, rx + 400f, rz);
        SessionHarness.Until(() => Rallies(a.Link).Count(r => r.State == ResponderState.Committed) >= 2, "committed again, away from the fight");
        var sentBack = Responder(sim, a);
        Move(sim, a, rx, rz);
        SessionHarness.Until(() => Rallies(a.Link).Count(r => r.State == ResponderState.Arrived) >= 2, "arrived again");
        Harness.Ticks(Hz * 2);

        using var report = JsonDocument.Parse(sim.Bridge.HordeReportJson);
        var defenders = report.RootElement.GetProperty("defenders");
        Assert.Multiple(() =>
        {
            Assert.That(Rallies(a.Link).Any(r => r.State == ResponderState.Decayed), Is.False, "the defender away from the fight decayed");
            Assert.That(Statuses(a.Link).Last().Committed, Is.EqualTo(1), "out of the count");
            Assert.That(defenders.GetProperty("recommitted").GetInt32(), Is.EqualTo(1));
            Assert.That(defenders.GetProperty("arrived").GetInt32(), Is.EqualTo(1), "its return was timed as a second arrival");
            Assert.That(defenders.GetProperty("decayed").GetInt32(), Is.Zero);
            Assert.That(answered.DeadlineTick - answered.CommittedTick, Is.InRange(SimBridge.HordeMinAnswerS * (long)Hz, 600L * Hz),
                "an answer's own window is twice its trip and a minute, within the timeout");
            Assert.That(answered.DeadlineTick - answered.CommittedTick, Is.LessThan(600L * Hz), "a near answer was given the whole timeout");
            Assert.That(sentBack.DeadlineTick, Is.GreaterThan(answered.DeadlineTick), "sent back, its deadline was not the timeout from then");
        });
    }

    /// <summary>
    /// The event over, a defender in the town is told to stand down, and that is the last it is told: the pass that ended the event no longer judged it
    /// as one over — near the gather point alone — and committed it again after the stand-down (Loïc, 2026-11-05: 571 bots left defending by the town
    /// hall once the 4K battle was lost).
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void TheEventOver_ADefenderInTheTown_IsToldToStandDown_AndNothingAfter()
    {
        using var sim = Start();
        var a = LogIn(sim, 0xB402, 1);
        var started = Horde(sim, r =>
        {
            r.StartsInS = 0;
            r.DurationS = 8;
            r.CommitTimeoutS = 600;
        });
        Harness.Ticks(Hz);
        Answer(a.Link, started.Event);
        SessionHarness.Until(() => Rallies(a.Link).Any(r => r.State == ResponderState.Committed), "committed");
        var core = Core(sim, started.Event);
        var (rx, rz) = sim.Bridge.RallyOf(core);
        Move(sim, a, rx, rz);
        SessionHarness.Until(() => Rallies(a.Link).Any(r => r.State == ResponderState.Arrived), "arrived at the gather point");

        // Into the town, on the raiders' route: still in the fight while the raid is on.
        Move(sim, a, core.TargetX, core.TargetZ);
        Harness.Ticks(Hz * 2);
        Assert.That(Rallies(a.Link).Last().State, Is.EqualTo(ResponderState.Arrived), "the premise: at the target, it is in the fight");

        SessionHarness.Until(() => HordePhase.IsOver(Core(sim, started.Event).Phase), "the event over");
        Harness.Ticks(Hz * 2);
        using var report = JsonDocument.Parse(sim.Bridge.HordeReportJson);
        Assert.Multiple(() =>
        {
            Assert.That(Rallies(a.Link).Last().State, Is.Zero, "the last rally it was told was not the stand-down");
            Assert.That(report.RootElement.GetProperty("defenders").GetProperty("recommitted").GetInt32(), Is.Zero, "committed again as the event ended");
        });
    }

    /// <summary>
    /// The event over, its answers go with it — the report keeps their count (2026-11-06: one row per responder per raid was kept for good, 18 620 with no
    /// raid running).
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void TheEventOver_ItsAnswersGo_TheReportKeepsTheirCount()
    {
        using var sim = Start();
        var a = LogIn(sim, 0xB403, 1);
        var started = Horde(sim, r =>
        {
            r.StartsInS = 0;
            r.DurationS = 5;
            r.CommitTimeoutS = 600;
        });
        Harness.Ticks(Hz);
        Answer(a.Link, started.Event);
        SessionHarness.Until(() => Rallies(a.Link).Any(r => r.State == ResponderState.Committed), "committed");
        Assert.That(Answers(sim), Is.EqualTo(1), "the premise: the answer persisted");

        SessionHarness.Until(() => HordePhase.IsOver(Core(sim, started.Event).Phase), "the event over");
        Harness.Ticks(Hz);
        using var report = JsonDocument.Parse(sim.Bridge.HordeReportJson);
        Assert.Multiple(() =>
        {
            Assert.That(Answers(sim), Is.Zero, "an answer outlived its event");
            Assert.That(report.RootElement.GetProperty("defenders").GetProperty("answers").GetInt32(), Is.EqualTo(1));
        });
    }

    /// <summary>A restart sweeps the answers of events over, kept by a server before the fix; the running event's stay (ARestart_KeepsTheEvent…).</summary>
    [Test]
    [CancelAfter(180_000)]
    public void ARestart_SweepsTheAnswersOfEventsOver()
    {
        using (var first = Start(persist: true))
        {
            first.WriteInTick(tx =>
            {
                for (var i = 0; i < 3; i++)
                {
                    var stale = new HordeResponder { Event = 0x5EED + i, Character = 0xB404 + i, State = ResponderState.Arrived };
                    tx.Spawn<HordeResponderArch>(HordeResponderArch.Core.Set(in stale));
                }
            });
            Assert.That(Answers(first), Is.EqualTo(3), "the premise: three answers of events long over");
            Harness.Release();
        }

        using var second = Start(persist: true);
        Assert.That(second.Reopened, Is.True, "the second run did not reopen the first's database");
        Harness.Ticks(Hz * 2);
        Assert.That(Answers(second), Is.Zero);
    }

    /// <summary>The answers persisted, of any event.</summary>
    private static int Answers(TatooineSim sim)
    {
        using var tx = sim.Dbe.CreateQuickTransaction();
        var n = 0;
        foreach (var _ in tx.Query<HordeResponderArch>())
        {
            n++;
        }

        return n;
    }

    /// <summary>AC-1 (D3): a restart keeps the event, its countdown and its answers; the fortress still stands.</summary>
    [Test]
    [CancelAfter(180_000)]
    public void ARestart_KeepsTheEvent_ItsCountdown_AndItsAnswers()
    {
        long id;
        HordeCore before;
        using (var first = Start(persist: true))
        {
            var a = LogIn(first, 0xB401, 1);
            var started = Horde(first);
            id = started.Event;
            Harness.Ticks(Hz);
            Answer(a.Link, started.Event);
            SessionHarness.Until(() => Rallies(a.Link).Count > 0, "committed");
            Harness.Ticks(Hz * 2);
            before = Core(first, id);
            Harness.Release();
        }

        using var second = Start(persist: true);
        Assert.That(second.Reopened, Is.True, "the second run did not reopen the first's database");
        Harness.Ticks(Hz * 2);
        var after = Core(second, id);
        Assert.Multiple(() =>
        {
            Assert.That(after.Phase, Is.EqualTo(HordePhase.Incoming));
            Assert.That(after.StartTick, Is.EqualTo(before.StartTick), "the countdown runs from the stored tick");
            Assert.That(Exists(second, after.Fortress), Is.True, "the fortress stands");
            Assert.That(second.Bridge.HordeStatusJson, Does.Contain("\"committed\":1"), "the answer is still counted");
        });
    }

    /// <summary>AC-5, D4: every wave spawned and killed is won — the fortress, its lairs and their raiders go; respawn off refills nobody.</summary>
    [Test]
    [CancelAfter(120_000)]
    public void EveryWaveKilled_IsWon_AndTheFortressGoes()
    {
        using var sim = Start();
        var started = Horde(sim, r =>
        {
            r.StartsInS = 0;
            r.Waves = 1;
            r.WaveSize = 4;
        });
        SessionHarness.Until(() => Raiders(sim).Count == 4, "the wave");

        // Killed as combat kills: dead, health 0, the corpse's countdown at its last tick — CreatureThink hands it to the lair director.
        var raiders = Raiders(sim).Select(r => r.Id).ToList();
        sim.WriteInTick(tx =>
        {
            foreach (var id in raiders)
            {
                var e = tx.OpenMut(id);
                var ai = e.Read(Creature.Ai);
                ai.Mode = AiMode.Dead;
                e.Set(Creature.Ai, ai);
                var v = e.Read(Creature.Vitals);
                v.Health = 0;
                e.Set(Creature.Vitals, v);
                var t = e.Read(Creature.Timers);
                t.ThinkCooldown = 1;
                e.Set(Creature.Timers, t);
            }
        });
        SessionHarness.Until(() => Core(sim, started.Event).Phase == HordePhase.Won, "won");
        Harness.Ticks(Hz);

        var core = Core(sim, started.Event);
        Assert.Multiple(() =>
        {
            Assert.That(core.Killed, Is.EqualTo(4));
            Assert.That(core.Spawned, Is.EqualTo(4), "respawn off: nothing refilled");
            Assert.That(Exists(sim, core.Fortress) || Exists(sim, core.LairA) || Exists(sim, core.LairB), Is.False, "the fortress and its lairs are gone");
            Assert.That(Raiders(sim), Is.Empty);
            Assert.That(sim.Bridge.HordeStatusJson, Is.Null, "no horde runs");
        });
    }
}
