using System;
using System.Linq;
using NUnit.Framework;
using SwgTatooine.Server;
using SwgTatooine.Server.Accounts;
using Typhon.Engine;
using Typhon.Protocol;

namespace SwgTatooine.Tests;

/// <summary>
/// A potion drunk (crafting-catalog.md § 5.4, crafting-build.md C2; Loïc, 2026-11-05): a stim heals its potency within what the body lacks, spends a dose, and
/// its last dose is a <c>consume</c> row; the next waits 15 s; one drunk from a crate comes out of it first; refused where it would do nothing. Everyone who
/// knows the body hears it (<see cref="Healed"/>). The audits clean throughout.
/// </summary>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
internal sealed class PotionChecks : ServedChecks
{
    // The stim's cooldown here, seconds: a served world ticks on a paced clock, and 15 s of it is past the harness's wait.
    private const float Cooldown = 1f;

    private TatooineSim Start()
    {
        var config = Worlds.Served(Dir);
        config.CommandRateScale = Worlds.FastCommands;
        config.WorkerCount = 1;
        config.WelcomePack = false;
        config.StimCooldownSec = Cooldown;
        var sim = new TatooineSim(config);
        sim.Initialize();
        Harness = new SessionHarness(sim);
        return sim;
    }

    private (FakeLink Link, EntityId Character, EntityId Body) LogIn(TatooineSim sim, ulong account)
    {
        var link = Harness.Connect(TatooineReplication.PlayerKind, TestAccounts.For(sim.Dbe, account));
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= 1, "the avatar to be spawned");
        var characterAccount = TestAccounts.For(sim.Dbe, account);
        var character = EntityId.Null;
        SessionHarness.Until(() => CharacterStore.TryFind(sim.Dbe, characterAccount, out character, out _), "the account's character");
        SessionHarness.Until(() => Ledgers.TryWallet(sim, character, out _), "the wallet to be opened");
        var body = EntityId.Null;
        SessionHarness.Until(() => TatooineReplication.TryBodyOfSession(link.Session, out body, out _), "the body");
        Harness.Ticks(2);
        return (link, character, body);
    }

    private void Breathe() => Harness.Ticks(Worlds.FastCommandGapTicks);

    private static void Drink(FakeLink link, EntityId item)
        => link.SendCommand(nameof(ConsumeItem), new RecordValues { ["item"] = FieldValue.OfUInt64(item.RawValue) });

    private static int Refused(FakeLink link, byte reason) => Ledgers.Refusals(link).Count(r => r == reason);

    private static void AuditsOk(TatooineSim sim, string when)
    {
        var (ok, why) = Market.Audits(sim);
        Assert.That(ok, Is.True, $"{when}: {why}");
    }

    /// <summary>A standard-issue stim staged into the grid: 204 hp, three doses.</summary>
    private static EntityId StageStim(TatooineSim sim, EntityId character)
    {
        Equipment.TryStandard(ItemCatalog.Stim, out var issue);
        var stats = SimBridge.StandardStats(in issue);
        var before = Items.Stacks(sim, character).Select(s => s.Id).ToHashSet();
        sim.Bridge.StageInstance(unchecked((long)character.RawValue), ItemCatalog.Stim, 0, in stats);
        SessionHarness.Until(() => Items.Stacks(sim, character).Any(s => !before.Contains(s.Id)), "the staged stim");
        return Items.Stacks(sim, character).Single(s => !before.Contains(s.Id)).Id;
    }

    private static ItemInstance StatsOf(TatooineSim sim, EntityId item)
    {
        using var tx = sim.Dbe.CreateQuickTransaction();
        return tx.Open(item).Read(ItemInstanceArch.Stats);
    }

    private static bool Exists(TatooineSim sim, EntityId item)
    {
        using var tx = sim.Dbe.CreateQuickTransaction();
        return tx.TryOpen(item, out _);
    }

    private static PlayerVitals VitalsOf(TatooineSim sim, EntityId body)
    {
        using var tx = sim.Dbe.CreateQuickTransaction();
        return tx.Open(body).Read(Player.Vitals);
    }

    /// <summary>The body's health set outright, through the tick (EW-02).</summary>
    private static void Hurt(TatooineSim sim, EntityId body, int health)
        => sim.WriteInTick((tx, _) =>
        {
            var player = tx.OpenMut(body);
            var v = player.Read(Player.Vitals);
            v.Health = health;
            player.Set(Player.Vitals, v);
        });

    private static void Down(TatooineSim sim, EntityId body)
        => sim.WriteInTick((tx, _) =>
        {
            var player = tx.OpenMut(body);
            var v = player.Read(Player.Vitals);
            v.Health = 0;
            v.Down = Downed.KnockedOut;
            v.UpTick = int.MaxValue;
            player.Set(Player.Vitals, v);
        });

    /// <summary>
    /// A stim drunk hurt heals its 204 hp at once and keeps two doses; a second at once is refused for its cooldown; after 15 s another heals, within
    /// what the body lacks; the third empties it — a <c>consume</c> row from the owner to World, the item gone. The drinker's session hears each heal.
    /// </summary>
    [Test]
    [CancelAfter(180_000)]
    public void AStim_Heals_SpendsADose_WaitsItsCooldown_AndItsLastDoseIsAConsumeRow()
    {
        using var sim = Start();
        var (link, character, body) = LogIn(sim, 0xD701);
        var stim = StageStim(sim, character);
        var max = VitalsOf(sim, body).MaxHealth;
        Hurt(sim, body, 300);

        Drink(link, stim);
        SessionHarness.Until(() => sim.Bridge.StimsDrunk == 1, "the first dose drunk");
        Breathe();
        Assert.Multiple(() =>
        {
            Assert.That(VitalsOf(sim, body).Health, Is.EqualTo(300 + Equipment.StimMkIPotency), "healed its potency");
            Assert.That(StatsOf(sim, stim).Condition, Is.EqualTo(2), "a dose spent");
            Assert.That(Heard.Of(link).Count(h => h == Equipment.StimMkIPotency), Is.EqualTo(1), "the drinker heard its heal");
        });

        Drink(link, stim);
        SessionHarness.Until(() => Refused(link, TatooineReplication.ConsumeCooldown) == 1, "the second refused within the cooldown");
        Assert.That(StatsOf(sim, stim).Condition, Is.EqualTo(2), "a refused drink spent a dose");

        // Past the cooldown, a body short of less than the potency: healed to its maximum, no further.
        Harness.Ticks((int)(Cooldown * sim.Config.TickRateHz) + 2);
        Hurt(sim, body, max - 50);
        Drink(link, stim);
        SessionHarness.Until(() => sim.Bridge.StimsDrunk == 2, "the second dose drunk");
        Breathe();
        Assert.That(VitalsOf(sim, body).Health, Is.EqualTo(max), "healed past its maximum");

        Harness.Ticks((int)(Cooldown * sim.Config.TickRateHz) + 2);
        Hurt(sim, body, 300);
        var rows = Items.Rows(sim).Count;
        Drink(link, stim);
        SessionHarness.Until(() => sim.Bridge.PotionsEmptied == 1, "the last dose drunk");
        Breathe();
        var row = Items.Rows(sim).Skip(rows).Single();
        Assert.Multiple(() =>
        {
            Assert.That(Exists(sim, stim), Is.False, "the empty stim is still held");
            Assert.That((row.Kind, row.From, row.To, row.Count, row.Template), Is.EqualTo(
                (ItemMoveKind.Consume, unchecked((long)character.RawValue), ItemLedger.World, 1, ItemCatalog.Stim)));
            Assert.That(sim.Bridge.HealthHealed, Is.EqualTo((2 * Equipment.StimMkIPotency) + 50));
        });
        AuditsOk(sim, "after the stim");
    }

    /// <summary>
    /// The Workshop's transaction failed: the drink is refused <c>CraftFailed</c>, the dose kept, nothing healed and no cooldown started — the next drink is
    /// taken. Two drinks sent together: one heals, the other waits its cooldown.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void AFailedPass_SpendsNothing_AndTwoDrinksAtOnce_HealOnce()
    {
        using var sim = Start();
        var (link, character, body) = LogIn(sim, 0xD704);
        var a = StageStim(sim, character);
        var b = StageStim(sim, character);
        Hurt(sim, body, 300);

        sim.Bridge.FailNextWorkshopWriteForChecks();
        Drink(link, a);
        SessionHarness.Until(() => Refused(link, TatooineReplication.CraftFailed) == 1, "the failed pass refused");
        Breathe();
        Assert.Multiple(() =>
        {
            Assert.That(StatsOf(sim, a).Condition, Is.EqualTo(3), "a failed pass spent a dose");
            Assert.That(VitalsOf(sim, body).Health, Is.EqualTo(300), "a failed pass healed");
            Assert.That(sim.Bridge.StimsDrunk, Is.Zero);
        });

        Drink(link, a);
        Drink(link, b);
        SessionHarness.Until(() => sim.Bridge.StimsDrunk == 1 && Refused(link, TatooineReplication.ConsumeCooldown) == 1, "one healed, one waited");
        Breathe();
        Assert.Multiple(() =>
        {
            Assert.That(StatsOf(sim, a).Condition + StatsOf(sim, b).Condition, Is.EqualTo(5), "one dose spent between them");
            Assert.That(VitalsOf(sim, body).Health, Is.EqualTo(300 + Equipment.StimMkIPotency), "healed once");
            Assert.That(sim.Bridge.HealsDropped, Is.Zero);
        });
        AuditsOk(sim, "after the failed pass");
    }

    /// <summary>Full, down, or drinking what is not a stim: refused, nothing spent.</summary>
    [Test]
    [CancelAfter(120_000)]
    public void ADrink_IsRefused_AtFullHealth_Down_AndOfWhatIsNotAStim()
    {
        using var sim = Start();
        var (link, character, body) = LogIn(sim, 0xD702);
        var stim = StageStim(sim, character);
        Equipment.TryStandard(ItemCatalog.Blaster, out var issue);
        var blasterStats = SimBridge.StandardStats(in issue);
        sim.Bridge.StageInstance(unchecked((long)character.RawValue), ItemCatalog.Blaster, 1, in blasterStats);
        SessionHarness.Until(() => Items.Stacks(sim, character).Any(s => s.Stack.Template == ItemCatalog.Blaster), "the staged blaster");
        var blaster = Items.Stacks(sim, character).Single(s => s.Stack.Template == ItemCatalog.Blaster).Id;

        Drink(link, stim);
        SessionHarness.Until(() => Refused(link, TatooineReplication.ConsumeFull) == 1, "refused at full health");

        Hurt(sim, body, 300);
        Drink(link, blaster);
        SessionHarness.Until(() => Refused(link, TatooineReplication.ConsumeBad) == 1, "a blaster drunk");

        Down(sim, body);
        Drink(link, stim);
        SessionHarness.Until(() => Refused(link, TatooineReplication.ConsumeBad) == 2, "drunk while down");
        Assert.Multiple(() =>
        {
            Assert.That(StatsOf(sim, stim).Condition, Is.EqualTo(3), "a refused drink spent a dose");
            Assert.That(sim.Bridge.StimsDrunk, Is.Zero);
        });
        AuditsOk(sim, "after the refusals");
    }

    /// <summary>
    /// Two identical stims packed into a crate; the crate drunk: one comes out into a free slot — an <c>unpack</c> row — and that one is drunk, two doses
    /// left; the crate holds the other, full.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void AStimDrunkFromACrate_IsUnpackedFirst_TheCrateStaysFull()
    {
        using var sim = Start();
        var (link, character, body) = LogIn(sim, 0xD703);
        var a = StageStim(sim, character);
        var crate = StageStim(sim, character);
        link.SendCommand(nameof(PackItem), new RecordValues { ["item"] = FieldValue.OfUInt64(a.RawValue), ["into"] = FieldValue.OfUInt64(crate.RawValue) });
        SessionHarness.Until(() => Items.Stacks(sim, character).Any(s => s.Id == crate && s.Stack.Count == 2), "the crate packed");
        Breathe();
        Hurt(sim, body, 300);

        Drink(link, crate);
        SessionHarness.Until(() => sim.Bridge.StimsDrunk == 1, "a dose drunk from the crate");
        Breathe();
        var held = Items.Stacks(sim, character).Where(s => s.Stack.Template == ItemCatalog.Stim).ToList();
        var single = held.Single(s => s.Id != crate);
        Assert.Multiple(() =>
        {
            Assert.That(held.Single(s => s.Id == crate).Stack.Count, Is.EqualTo(1), "the crate gave one");
            Assert.That(StatsOf(sim, crate).Condition, Is.EqualTo(3), "the crate's own stim was drunk");
            Assert.That(StatsOf(sim, single.Id).Condition, Is.EqualTo(2), "the unpacked stim was not the one drunk");
            Assert.That(Items.Rows(sim).Count(r => r.Kind == ItemMoveKind.Unpack), Is.EqualTo(1), "one unpack row");
            Assert.That(VitalsOf(sim, body).Health, Is.EqualTo(300 + Equipment.StimMkIPotency));
        });
        AuditsOk(sim, "after the crate");
    }

    /// <summary>The <c>Healed</c> events a session's frames carried: their amounts.</summary>
    private static class Heard
    {
        public static int[] Of(FakeLink link)
        {
            var healed = link.Plan.EventByName(nameof(Healed));
            var sink = new HealSink { Healed = healed, Amounts = [] };
            var frame = default(RealmFrame);
            var sent = link.Sent;
            for (var i = 0; i < sent.Length; i++)
            {
                if (sent[i].Length > 0 && sent[i][0] == MessageTypes.Tick)
                {
                    TickReader.Read(sent[i], link.Plan, ref frame, ref sink);
                }
            }

            return [.. sink.Amounts];
        }
    }

    private struct HealSink : ITickSink
    {
        public MessagePlan Healed;
        public System.Collections.Generic.List<int> Amounts;
        private bool _in;

        public void Number(FieldPlan field, scoped ReadOnlySpan<double> components)
        {
            if (_in && field.Name == "amount" && components.Length > 0)
            {
                Amounts.Add((int)components[0]);
            }
        }

        public void Event(MessagePlan type) => _in = type == Healed;

        public void Integer64(FieldPlan field, scoped ReadOnlySpan<ulong> components)
        {
        }

        public void Text(FieldPlan field, scoped ReadOnlySpan<byte> utf8)
        {
        }

        public void Bytes(FieldPlan field, scoped ReadOnlySpan<byte> bytes)
        {
        }

        public void List(FieldPlan field, int count, scoped ReadOnlySpan<double> components)
        {
        }

        public void BeginTick(uint tick, TickFlags flags, uint periodUs) => _in = false;

        public void Realm(RealmFrame frame)
        {
        }

        public void BeginEntities(ArchetypePlan archetype) => _in = false;

        public void Enter(uint netId, scoped ReadOnlySpan<double> position, scoped ReadOnlySpan<double> velocity, uint t0, byte epoch) => _in = false;

        public void Segment(uint netId, scoped ReadOnlySpan<double> position, scoped ReadOnlySpan<double> velocity, uint t0, byte epoch) => _in = false;

        public void State(uint netId, byte groupMask) => _in = false;

        public void Leave(uint netId) => _in = false;

        public void Self(ArchetypePlan archetype, uint netId, ushort lastSeq, byte ownerMask) => _in = false;

        public void Ack(ushort seq, byte reason) => _in = false;

        public void Source(ushort requestId, byte status, ushort code) => _in = false;

        public void BeginAggregate(CatalogGrid grid, bool reset) => _in = false;

        public void AggregateCell(uint cell, scoped ReadOnlySpan<uint> counts)
        {
        }

        public void Metric(MetricPlan metric, int valueIndex, double value)
        {
        }

        public void Debug(byte subType, scoped ReadOnlySpan<byte> payload)
        {
        }

        public void Ext(uint appTypeId, scoped ReadOnlySpan<byte> payload)
        {
        }

        public void UnknownBlock(byte blockType) => _in = false;

        public void EndTick() => _in = false;
    }
}
