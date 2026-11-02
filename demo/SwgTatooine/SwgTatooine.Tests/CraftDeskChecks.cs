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
/// The workshop's crafting through the real commands (crafting-build.md C2): a part and a weapon crafted at a station, the result's stats the pure
/// <see cref="Crafting"/>'s to the bit, the inputs consumed and the result minted as <c>craft</c> and <c>make</c> rows, the bench fee paid; every refusal;
/// and a craft that survives a stop (C2-3) — the audits clean throughout.
/// </summary>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
internal sealed class CraftDeskChecks : ServedChecks
{
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

    private sealed record Crafter(FakeLink Link, EntityId Body, EntityId Character)
    {
        public long Key => unchecked((long)Character.RawValue);
    }

    private Crafter LogIn(TatooineSim sim, ulong account, long bindings, bool atStation = true)
    {
        var link = Harness.Connect(TatooineReplication.PlayerKind, TestAccounts.For(sim.Dbe, account));
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= bindings, "the avatar to be spawned");
        Harness.Ticks(2);
        var station = sim.Index.Stations[0];
        var body = atStation ? Worlds.Park(sim, link.Session, station.X + 2f, station.Z) : Worlds.Park(sim, link.Session, station.X + 60f, station.Z);
        Assert.That(CharacterStore.TryFind(sim.Dbe, TestAccounts.For(sim.Dbe, account), out var character, out _), Is.True);
        SessionHarness.Until(() => Ledgers.TryWallet(sim, character, out _), "the wallet to be opened");
        Harness.Ticks(2);
        return new Crafter(link, body, character);
    }

    private void Breathe() => Harness.Ticks(Worlds.ServedHz / 2 + 1);

    private static void StartCraft(FakeLink link, ushort schematic, byte[] mks, ulong[] parts)
    {
        var values = new RecordValues { ["schematic"] = FieldValue.Of(schematic) };
        for (var i = 0; i < 6; i++)
        {
            values[$"mk{i}"] = FieldValue.Of(i < mks.Length ? mks[i] : (byte)0);
            values[$"part{i}"] = FieldValue.OfUInt64(i < parts.Length ? parts[i] : 0UL);
        }

        link.SendCommand(nameof(CraftStart), values);
    }

    private static void Experiment(FakeLink link, byte line) => link.SendCommand(nameof(CraftExperiment), new RecordValues { ["line"] = FieldValue.Of(line) });

    private static void Enhance(FakeLink link, byte mk) => link.SendCommand(nameof(CraftEnhance), new RecordValues { ["mk"] = FieldValue.Of(mk) });

    private static void Finish(FakeLink link, ushort schematic)
        => link.SendCommand(nameof(CraftFinish), new RecordValues { ["schematic"] = FieldValue.Of(schematic) });

    private static int Count(FakeLink link, byte reason) => Ledgers.Refusals(link).Count(r => r == reason);

    private static ItemInstance StatsOf(TatooineSim sim, EntityId id)
    {
        using var tx = sim.Dbe.CreateQuickTransaction();
        return tx.Open(id).Read(ItemInstanceArch.Stats);
    }

    private static void AuditsOk(TatooineSim sim, string when)
    {
        var (ok, why) = Market.Audits(sim);
        Assert.That(ok, Is.True, $"{when}: {why}");
    }

    private void Stage(TatooineSim sim, Crafter c, ushort template, byte mk, int count)
    {
        var want = Market.Units(sim, c.Character, template, mk) + count;
        sim.Bridge.StageTransfer(ItemLedger.World, c.Key, template, mk, count, ItemMoveKind.Kill, 1);
        SessionHarness.Until(() => Market.Units(sim, c.Character, template, mk) == want, "the staged units");
    }

    private EntityId StagePart(TatooineSim sim, Crafter c, ushort part, int gradePerMille)
    {
        var before = Items.Stacks(sim, c.Character).Where(s => s.Stack.Template == part).Select(s => s.Id).ToHashSet();
        sim.Bridge.StageInstance(c.Key, part, 0, new ItemInstance { Stat0 = gradePerMille, LifeMilli = 1000 });
        EntityId id = default;
        SessionHarness.Until(
            () =>
            {
                id = Items.Stacks(sim, c.Character).FirstOrDefault(s => s.Stack.Template == part && !before.Contains(s.Id)).Id;
                return !id.IsNull;
            },
            "the staged part");
        return id;
    }

    /// <summary>
    /// A grip crafted at a station: Hide Mk II ×6 (quality) and Metal ×4 (bulk) consumed at the start as <c>craft</c> rows naming the craft; two points;
    /// the finish mints one grip instance whose grade is the pure craft's, as a <c>make</c> row — no fee, a part has no market value.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void APart_IsCraftedAtAStation_ItsGradeThePureCrafts()
    {
        using var sim = Start();
        var c = LogIn(sim, 0x9501, 1);
        Stage(sim, c, ItemCatalog.Hide, 2, 6);
        Stage(sim, c, ItemCatalog.Metal, 1, 4);
        Harness.Ticks(1);
        var rows = Items.Rows(sim).Count;

        StartCraft(c.Link, Schematics.Grip, [2, 0], []);
        SessionHarness.Until(() => sim.Bridge.CraftsStarted == 1, "the craft started");
        Harness.Ticks(1);
        Assert.That(sim.Bridge.TryCraftOf(c.Key, out var state), Is.True);
        var consumed = Items.Rows(sim).Skip(rows).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(consumed.All(r => r.Kind == ItemMoveKind.Craft && r.From == c.Key && r.To == ItemLedger.World), Is.True);
            Assert.That(consumed.Sum(r => r.Count), Is.EqualTo(10), "six hide and four metal");
            Assert.That(Market.Units(sim, c.Character, ItemCatalog.Hide, 2) + Market.Units(sim, c.Character, ItemCatalog.Metal, 1), Is.Zero);
        });

        Experiment(c.Link, 0);
        Experiment(c.Link, 0);
        SessionHarness.Until(() => sim.Bridge.CraftPoints == 2, "the points");
        Finish(c.Link, Schematics.Grip);
        SessionHarness.Until(() => sim.Bridge.CraftsFinished == 1, "the finish");
        Harness.Ticks(2);

        Schematics.TryGet(Schematics.Grip, out var grip);
        var expected = new double[1];
        Crafting.Run(grip, [new(ItemCatalog.Hide, 2, 0, 0, 0), new(ItemCatalog.Metal, 1, 0, 0, 0)], c.Key, state.Serial, [2], 0, expected);
        var made = Items.Stacks(sim, c.Character).Single(s => s.Stack.Template == Schematics.Grip);
        var make = Items.Rows(sim).Last();
        Assert.Multiple(() =>
        {
            Assert.That(StatsOf(sim, made.Id).Stat0, Is.EqualTo((int)Math.Round(expected[0])), "the grade is not the pure craft's");
            Assert.That(StatsOf(sim, made.Id).Crafter, Is.EqualTo(c.Key));
            Assert.That((make.Kind, make.From, make.To, make.ToStack), Is.EqualTo((ItemMoveKind.Make, ItemLedger.World, c.Key, unchecked((long)made.Id.RawValue))));
            Assert.That(sim.Bridge.TryCraftOf(c.Key, out _), Is.False, "the craft outlived its finish");
            Assert.That(sim.Bridge.CraftFees, Is.Zero, "a part has no value, so no fee");
        });
        AuditsOk(sim, "after the grip");
    }

    /// <summary>
    /// A crafter bot's plan, through the workshop (crafting-build.md § 6, C2-4's bots): from Metal, Crystal, Gas, Hide and a core, read from the real
    /// inventory answer as a bot reads it, <see cref="CraftPlan.Next"/> makes the barrel, the power cell and the grip, then the blaster — every command the
    /// bot sends accepted, the audits clean.
    /// </summary>
    [Test]
    [CancelAfter(180_000)]
    public void ABotsCraftPlan_IsAcceptedByTheWorkshop_PartsThenTheItem()
    {
        using var sim = Start();
        var c = LogIn(sim, 0x9531, 1);
        Stage(sim, c, ItemCatalog.CoreRanged, 2, 1);
        Stage(sim, c, ItemCatalog.Metal, 1, 40);
        Stage(sim, c, ItemCatalog.Crystal, 1, 8);
        Stage(sim, c, ItemCatalog.Gas, 1, 8);
        Stage(sim, c, ItemCatalog.Hide, 2, 6);
        Harness.Ticks(1);
        var book = Bots.CraftBook.Parse(CraftEndpoints.SchematicsJson());

        var made = new List<ushort>();
        for (var craft = 1; craft <= 4; craft++)
        {
            var held = new List<Bots.HeldStack>();
            var gear = new List<Bots.GearItem>();
            Bots.InventoryView.Parse(ItemEndpoints.InventoryJson(sim.Dbe, c.Character), Bots.InventoryView.None, false, out _, held, gear);
            var job = Bots.CraftPlan.Next(book, [ItemCatalog.Blaster], 0, held, Bots.CraftPlan.PartsOf(gear));
            Assert.That(job, Is.Not.Null, $"craft {craft}: nothing to make");
            made.Add(job.Schematic);
            foreach (var command in Bots.BotSwarm.Encode(job))
            {
                c.Link.SendCommand(command.Name, command.Values);
                Breathe();
            }

            var finished = craft;
            SessionHarness.Until(() => sim.Bridge.CraftsFinished == finished, $"craft {craft} finished");
            Harness.Ticks(2);
        }

        Assert.Multiple(() =>
        {
            Assert.That(made, Is.EqualTo(new[] { Schematics.BarrelPistol, Schematics.PowerCell, Schematics.Grip, ItemCatalog.Blaster }));
            Assert.That(Items.Stacks(sim, c.Character).Count(s => s.Stack.Template == ItemCatalog.Blaster), Is.EqualTo(1));
            Assert.That(Ledgers.Refusals(c.Link), Is.Empty, "a command the bot sent was refused");
        });
        AuditsOk(sim, "after the bot's crafts");
    }

    /// <summary>
    /// A blaster from a Mk III core, three parts, six points (3, 2, 1) and a Mk II enhancement: its stats the pure craft's to the bit, Mk III by its core,
    /// and the bench fee 3 % of a Mk III weapon's value paid to World as one <c>bench</c> row.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void AWeapon_FromACoreAndParts_IsThePureCraft_AndPaysItsBenchFee()
    {
        using var sim = Start();
        var c = LogIn(sim, 0x9511, 1);
        Stage(sim, c, ItemCatalog.CoreRanged, 3, 1);
        Stage(sim, c, ItemCatalog.Metal, 1, 20);
        Stage(sim, c, ItemCatalog.AddonWeapon, 2, 1);
        var barrel = StagePart(sim, c, Schematics.BarrelPistol, 400);
        var cell = StagePart(sim, c, Schematics.PowerCell, 450);
        var grip = StagePart(sim, c, Schematics.Grip, 500);
        Harness.Ticks(1);
        var balance = Ledgers.Balance(sim, c.Character);

        StartCraft(c.Link, ItemCatalog.Blaster, [3, 0], [barrel.RawValue, cell.RawValue, grip.RawValue]);
        SessionHarness.Until(() => sim.Bridge.CraftsStarted == 1, "the craft started");
        Harness.Ticks(1);
        sim.Bridge.TryCraftOf(c.Key, out var state);
        // One point at a time, as a person clicks: a burst past the command's rate is refused.
        var spent = 0;
        foreach (var line in new byte[] { 0, 0, 0, 1, 1, 2 })
        {
            Experiment(c.Link, line);
            spent++;
            Breathe();
            SessionHarness.Until(() => sim.Bridge.CraftPoints == spent, "the point");
        }

        Enhance(c.Link, 2);
        SessionHarness.Until(() => Market.Units(sim, c.Character, ItemCatalog.AddonWeapon, 2) == 0, "the add-on");
        Breathe();
        Finish(c.Link, ItemCatalog.Blaster);
        SessionHarness.Until(() => sim.Bridge.CraftsFinished == 1, "the finish");
        Harness.Ticks(2);

        Schematics.TryGet(ItemCatalog.Blaster, out var s);
        var v = new double[4];
        Crafting.Run(
            s,
            [new(ItemCatalog.CoreRanged, 3, 0, 0, 0), new(Schematics.BarrelPistol, 0, .4, 0, 0), new(Schematics.PowerCell, 0, .45, 0, 0), new(Schematics.Grip, 0, .5, 0, 0), new(ItemCatalog.Metal, 1, 0, 0, 0)],
            c.Key,
            state.Serial,
            [3, 2, 1],
            2,
            v);
        var made = Items.Stacks(sim, c.Character).Single(x => x.Stack.Template == ItemCatalog.Blaster);
        var stats = StatsOf(sim, made.Id);
        var fee = MarketValues.MilliOf(ItemCatalog.Blaster, 3) * 30 / 1000 / 1000;
        var bench = Ledgers.Rows(sim).Single(r => r.Kind == TransferKind.Bench);
        Assert.Multiple(() =>
        {
            Assert.That((stats.Stat0, stats.Stat1, stats.Stat2), Is.EqualTo(((int)Math.Round(v[0]), (int)Math.Round(v[1]), (int)Math.Round(v[2]))));
            Assert.That(made.Stack.Mk, Is.EqualTo((byte)3), "its band is its core's");
            Assert.That(stats.LifeMilli, Is.InRange((short)900, (short)1100));
            Assert.That(stats.Condition, Is.EqualTo(stats.MaxCondition).And.Positive);
            Assert.That((bench.From, bench.To, bench.Amount), Is.EqualTo((c.Key, EconomyLedger.World, fee)));
            Assert.That(fee, Is.EqualTo(103), "⌊3 457 × 3 %⌋");
            Assert.That(Ledgers.Balance(sim, c.Character), Is.EqualTo(balance - fee));
            Assert.That(Market.Units(sim, c.Character, ItemCatalog.AddonWeapon, 2), Is.Zero, "the add-on was not consumed");
        });
        AuditsOk(sim, "after the blaster");
    }

    /// <summary>The refusals: away from a station, a second craft, inputs not held, a point with none left, a finish of another schematic or of none.</summary>
    [Test]
    [CancelAfter(180_000)]
    public void EveryCraftRefusal_HasItsReason()
    {
        using var sim = Start();
        var c = LogIn(sim, 0x9521, 1);
        Stage(sim, c, ItemCatalog.Metal, 2, 30);

        void Expect(Action send, byte reason, string what)
        {
            var before = Count(c.Link, reason);
            send();
            SessionHarness.Until(() => Count(c.Link, reason) > before, what);
            Breathe();
        }

        Expect(() => Finish(c.Link, Schematics.KnucklePlate), TatooineReplication.CraftNone, "no craft to finish");
        Expect(() => StartCraft(c.Link, Schematics.Grip, [2, 0], []), TatooineReplication.CraftBadInputs, "no hide held");
        Expect(() => StartCraft(c.Link, 999, [], []), TatooineReplication.CraftBadInputs, "no such schematic");
        StartCraft(c.Link, Schematics.KnucklePlate, [2], []);
        SessionHarness.Until(() => sim.Bridge.CraftsStarted == 1, "a knuckle plate started");
        Breathe();
        Expect(() => StartCraft(c.Link, Schematics.KnucklePlate, [2], []), TatooineReplication.CraftBusy, "a second craft");
        Expect(() => Experiment(c.Link, 5), TatooineReplication.CraftNoPoints, "no such line");
        Experiment(c.Link, 0);
        Experiment(c.Link, 0);
        SessionHarness.Until(() => sim.Bridge.CraftPoints == 2, "both points");
        Expect(() => Experiment(c.Link, 0), TatooineReplication.CraftNoPoints, "no point left");
        Expect(() => Enhance(c.Link, 1), TatooineReplication.CraftNoAddon, "a part takes no add-on");
        Expect(() => Finish(c.Link, Schematics.Grip), TatooineReplication.CraftNone, "another schematic");

        Worlds.Park(sim, c.Link.Session, sim.Index.Stations[0].X + 60f, sim.Index.Stations[0].Z);
        Harness.Ticks(2);
        Finish(c.Link, Schematics.KnucklePlate);
        SessionHarness.Until(() => sim.Bridge.CraftsFinished == 1, "a finish away from the station: a craft under way finishes anywhere");
        Breathe();
        Expect(() => StartCraft(c.Link, Schematics.KnucklePlate, [2], []), TatooineReplication.CraftNotAtStation, "away from any station");
        AuditsOk(sim, "after the refusals");
    }

    /// <summary>
    /// C2-3: a craft started, its inputs consumed, then the server stopped with no close; reopened, the craft is there with its points, the inputs are
    /// still gone, and it finishes — the inputs and the result never both, never neither.
    /// </summary>
    [Test]
    [CancelAfter(240_000)]
    public void ACraft_SurvivesAStop_AndFinishesAfterTheReopen()
    {
        long key;
        CraftState before;
        using (var first = Start(persist: true))
        {
            var c = LogIn(first, 0x9531, 1);
            Stage(first, c, ItemCatalog.Metal, 3, 10);
            Harness.Ticks(1);
            StartCraft(c.Link, Schematics.KnucklePlate, [3], []);
            SessionHarness.Until(() => first.Bridge.CraftsStarted == 1, "the craft started");
            Experiment(c.Link, 0);
            SessionHarness.Until(() => first.Bridge.CraftPoints == 1, "a point");
            Harness.Ticks(3);
            key = c.Key;
            first.Bridge.TryCraftOf(key, out before);
            AuditsOk(first, "before the stop");
            Harness.Release();
        }

        TatooineReplication.ResetSessionAccounting();
        TatooineReplication.ResetIntentAccounting();
        using var second = Start(persist: true);
        Assert.That(second.Reopened, Is.True);
        var again = LogIn(second, 0x9531, 1);
        Assert.That(Market.Units(second, again.Character, ItemCatalog.Metal, 3), Is.Zero, "the inputs came back");
        Experiment(again.Link, 0);
        SessionHarness.Until(() => second.Bridge.CraftPoints == 1, "the craft resumed");
        Assert.That(second.Bridge.TryCraftOf(key, out var resumed), Is.True);
        Assert.That((resumed.Serial, resumed.Q0, resumed.PointsLeft), Is.EqualTo((before.Serial, before.Q0, (byte)(before.PointsLeft - 1))));
        Finish(again.Link, Schematics.KnucklePlate);
        SessionHarness.Until(() => second.Bridge.CraftsFinished == 1, "the finish");
        Harness.Ticks(2);
        Assert.That(Items.Stacks(second, again.Character).Count(s => s.Stack.Template == Schematics.KnucklePlate), Is.EqualTo(1));
        AuditsOk(second, "after the reopen and the finish");
    }
}
