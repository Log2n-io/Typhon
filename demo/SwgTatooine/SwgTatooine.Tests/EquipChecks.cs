using System.Linq;
using NUnit.Framework;
using SwgTatooine.Server;
using SwgTatooine.Server.Accounts;
using Typhon.Engine;
using Typhon.Protocol;

namespace SwgTatooine.Tests;

/// <summary>
/// The workshop's equip and unequip (crafting-build.md C1, § 2.2-2.3): an item with stats moved between the grid and its slot, the body's gear written from
/// what is worn, the CL gate (C1-2), and the gear rebuilt from the equipped slots after a reopen (C1-4) — with the item audit clean throughout.
/// </summary>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
internal sealed class EquipChecks : ServedChecks
{
    private const ushort Blaster = ItemCatalog.Blaster;
    private const ushort Gloves = ItemCatalog.PaddedHelmet + 4;
    private const ushort Torso = ItemCatalog.PaddedHelmet + 1;

    private TatooineSim Start(bool persist = false)
    {
        var config = Worlds.Served(Dir);
        config.WorkerCount = 1;
        config.Persist = persist;
        config.WelcomePack = true;
        var sim = new TatooineSim(config);
        sim.Initialize();
        Harness = new SessionHarness(sim);
        return sim;
    }

    private sealed record Player(FakeLink Link, EntityId Body, EntityId Character)
    {
        public long Key => unchecked((long)Character.RawValue);
    }

    private Player LogIn(TatooineSim sim, ulong account, long bindings)
    {
        var link = Harness.Connect(TatooineReplication.PlayerKind, TestAccounts.For(sim.Dbe, account));
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= bindings, "the avatar to be spawned");
        Harness.Ticks(2);
        var home = sim.Index.Bazaars[0];
        var body = Worlds.Park(sim, link.Session, home.X + 2.5f, home.Z);
        Assert.That(CharacterStore.TryFind(sim.Dbe, TestAccounts.For(sim.Dbe, account), out var character, out _), Is.True);
        SessionHarness.Until(() => Ledgers.TryWallet(sim, character, out _), "the wallet to be opened");
        SessionHarness.Until(() => Items.Stacks(sim, character).Count > 0, "the welcome pack");
        Harness.Ticks(4);
        return new Player(link, body, character);
    }

    private static void Equip(FakeLink link, EntityId item)
        => link.SendCommand(nameof(Server.Equip), new RecordValues { ["item"] = FieldValue.OfUInt64(item.RawValue) });

    private static void Unequip(FakeLink link, byte slot)
        => link.SendCommand(nameof(Server.Unequip), new RecordValues { ["slot"] = FieldValue.Of(slot) });

    private static (PlayerGear Gear, int Damage) GearOf(TatooineSim sim, EntityId body)
    {
        var gear = default(PlayerGear);
        var damage = 0;
        sim.WriteInTick(tx => gear = tx.Open(body).Read(Server.Player.Gear));
        damage = gear.Damage;
        return (gear, damage);
    }

    private static (EntityId Id, ItemStack Stack) Of(TatooineSim sim, Player p, ushort template)
        => Items.Stacks(sim, p.Character).First(s => s.Stack.Template == template);

    private static void AuditOk(TatooineSim sim, string when)
    {
        var audit = ItemEndpoints.Audit(sim.Dbe);
        Assert.That(audit.Ok, Is.True, $"{when}: {string.Join("; ", audit.Violations)}");
    }

    private static ItemInstance StatsOf(ushort template)
    {
        Equipment.TryStandard(template, out var issue);
        return SimBridge.StandardStats(in issue);
    }

    private static int Count(FakeLink link, byte reason) => Ledgers.Refusals(link).Count(r => r == reason);

    private static int StanceOf(TatooineSim sim, EntityId body)
    {
        var stance = 0;
        sim.WriteInTick(tx => stance = tx.Open(body).Read(Server.Player.State).Stance);
        return stance;
    }

    /// <summary>
    /// A new character's pack blaster is worn (crafting-build.md O-1): the body's damage, delay, reach, type and stance are its. Unequipped, the fist; worn
    /// again, the blaster; gloves and a torso add Σ share × protection. Every move a slot change and no ledger row.
    /// </summary>
    [Test]
    [CancelAfter(180_000)]
    public void Equipping_WritesTheBodysGear_FromWhatIsWorn()
    {
        using var sim = Start();
        var a = LogIn(sim, 0x9401, 1);
        Equipment.TryStandard(Blaster, out var issue);
        SessionHarness.Until(() => GearOf(sim, a.Body).Gear.Stance == Stances.Pistol, "the pack's blaster worn");
        var (armed, damage) = GearOf(sim, a.Body);
        Assert.Multiple(() =>
        {
            Assert.That(Of(sim, a, Blaster).Stack.Slot, Is.EqualTo(EquipSlot.Weapon), "the pack's blaster is not worn");
            Assert.That(damage, Is.EqualTo(issue.Stat0));
            Assert.That((armed.DelayMs, armed.RangeDm, armed.Stance, armed.DamageType),
                Is.EqualTo(((short)issue.Stat1, (short)issue.Stat2, Stances.Pistol, DamageType.Energy)));
        });

        var rows = Items.Rows(sim).Count;
        a.Link.SendCommand(nameof(SetStance), ClientSays.SetStance(Stances.Pistol));
        SessionHarness.Until(() => StanceOf(sim, a.Body) == Stances.Pistol, "the pistol stance");
        Unequip(a.Link, EquipSlot.Weapon);
        SessionHarness.Until(() => GearOf(sim, a.Body).Gear.Stance == Stances.Unarmed, "the fist");
        Harness.Ticks(1);
        Assert.That(StanceOf(sim, a.Body), Is.EqualTo(Stances.Unarmed), "the stance stayed on a weapon no longer worn: a body that cannot fight");
        var (fist, fistDamage) = GearOf(sim, a.Body);
        Assert.That((fistDamage, fist.DelayMs, fist.RangeDm), Is.EqualTo((40, (short)1200, (short)10)), "the fist's numbers");
        Equip(a.Link, Of(sim, a, Blaster).Id);
        SessionHarness.Until(() => GearOf(sim, a.Body).Gear.Stance == Stances.Pistol, "the blaster worn again");
        Assert.That(Items.Rows(sim), Has.Count.EqualTo(rows), "equipping wrote a ledger row");

        sim.Bridge.StageInstance(a.Key, Gloves, 1, StatsOf(Gloves));
        sim.Bridge.StageInstance(a.Key, Torso, 1, StatsOf(Torso));
        SessionHarness.Until(() => Items.Stacks(sim, a.Character).Count == 3, "the armour");
        Harness.Ticks(1);
        Equip(a.Link, Of(sim, a, Gloves).Id);
        Equip(a.Link, Of(sim, a, Torso).Id);
        SessionHarness.Until(() => GearOf(sim, a.Body).Gear.MitKinetic > 0 && Of(sim, a, Torso).Stack.Slot == EquipSlot.Torso, "the armour equipped");
        Harness.Ticks(2);
        var (armoured, _) = GearOf(sim, a.Body);
        Assert.That((armoured.MitKinetic, armoured.MitEnergy), Is.EqualTo(((short)((60 + 330) * 120 / 1000), (short)((60 + 330) * 100 / 1000))),
            "Σ share × protection over the gloves and the torso");
        AuditOk(sim, "with three items worn");

        AuditOk(sim, "after the armour");
    }

    /// <summary>
    /// C1-2: equipping Mk II at CL 1 is refused <c>EquipLevel</c>; a second item of an occupied slot swaps with it; an unequip with a full grid is refused
    /// <c>EquipNoRoom</c>; an equip of a stack is refused <c>EquipNotYours</c>.
    /// </summary>
    [Test]
    [CancelAfter(180_000)]
    public void TheGates_LevelSwapRoomAndStacks()
    {
        using var sim = Start();
        var a = LogIn(sim, 0x9411, 1);
        sim.Bridge.StageInstance(a.Key, Blaster, 2, StatsOf(Blaster));
        sim.Bridge.StageInstance(a.Key, ItemCatalog.Sword, 1, StatsOf(ItemCatalog.Sword));
        sim.Bridge.StageTransfer(ItemLedger.World, a.Key, ItemCatalog.Hide, 1, 5, ItemMoveKind.Kill, 1);
        SessionHarness.Until(() => Items.Stacks(sim, a.Character).Count == 4, "the items");
        Harness.Ticks(1);

        var before = Count(a.Link, TatooineReplication.EquipLevel);
        Equip(a.Link, Items.Stacks(sim, a.Character).First(s => s.Stack.Template == Blaster && s.Stack.Mk == 2).Id);
        SessionHarness.Until(() => Count(a.Link, TatooineReplication.EquipLevel) > before, "the level gate");

        var stack = Items.Stacks(sim, a.Character).First(s => s.Stack.Template == ItemCatalog.Hide).Id;
        before = Count(a.Link, TatooineReplication.EquipNotYours);
        Equip(a.Link, stack);
        SessionHarness.Until(() => Count(a.Link, TatooineReplication.EquipNotYours) > before, "a stack refused");

        // The pack's blaster, worn, then the sword in its place: a swap.
        var mkOne = Items.Stacks(sim, a.Character).First(s => s.Stack.Template == Blaster && s.Stack.Mk == 1);
        SessionHarness.Until(() => Items.Stacks(sim, a.Character).Any(s => s.Id == mkOne.Id && s.Stack.Slot == EquipSlot.Weapon), "the blaster worn");
        var sword = Of(sim, a, ItemCatalog.Sword);
        Equip(a.Link, sword.Id);
        SessionHarness.Until(() => Of(sim, a, ItemCatalog.Sword).Stack.Slot == EquipSlot.Weapon, "the sword worn");
        Harness.Ticks(2);
        Assert.Multiple(() =>
        {
            Assert.That(Items.Stacks(sim, a.Character).Single(s => s.Id == mkOne.Id).Stack.Slot, Is.EqualTo(sword.Stack.Slot), "the blaster took the sword's slot");
            Assert.That(GearOf(sim, a.Body).Gear.Stance, Is.EqualTo(Stances.OneHanded));
        });

        // A full grid: the sword cannot come off.
        var held = Items.Stacks(sim, a.Character).Select(s => (s.Stack.Template, s.Stack.Mk)).ToHashSet();
        var free = ItemLedger.Slots - Items.Stacks(sim, a.Character).Count(s => s.Stack.Slot <= ItemLedger.Slots);
        for (ushort template = 1; template <= ItemCatalog.Venom && free > 0; template++)
        {
            for (byte mk = 1; mk <= 4 && free > 0; mk++)
            {
                if (held.Add((template, mk)))
                {
                    sim.Bridge.StageTransfer(ItemLedger.World, a.Key, template, mk, 1, ItemMoveKind.Kill, 1);
                    free--;
                }
            }
        }

        SessionHarness.Until(() => Items.Stacks(sim, a.Character).Count(s => s.Stack.Slot <= ItemLedger.Slots) == ItemLedger.Slots, "a full grid");
        before = Count(a.Link, TatooineReplication.EquipNoRoom);
        Unequip(a.Link, EquipSlot.Weapon);
        SessionHarness.Until(() => Count(a.Link, TatooineReplication.EquipNoRoom) > before, "no room");
        AuditOk(sim, "after the gates");
    }

    /// <summary>C1-4: a body killed by a stop — no logout — comes back, on the next login after the reopen, with the gear its equipped slots make.</summary>
    [Test]
    [CancelAfter(240_000)]
    public void TheGear_IsRebuiltFromTheEquippedSlots_AfterAReopen()
    {
        PlayerGear before;
        int damage;
        using (var first = Start(persist: true))
        {
            var a = LogIn(first, 0x9421, 1);
            first.Bridge.StageInstance(a.Key, Torso, 1, StatsOf(Torso));
            SessionHarness.Until(() => Items.Stacks(first, a.Character).Count == 2, "the torso");
            Harness.Ticks(1);
            Equip(a.Link, Of(first, a, Torso).Id);
            SessionHarness.Until(() => GearOf(first, a.Body).Gear.MitKinetic > 0 && GearOf(first, a.Body).Gear.Stance == Stances.Pistol, "both worn");
            Harness.Ticks(3);
            (before, damage) = GearOf(first, a.Body);
            Harness.Release();
        }

        TatooineReplication.ResetSessionAccounting();
        TatooineReplication.ResetIntentAccounting();
        using var second = Start(persist: true);
        Assert.That(second.Reopened, Is.True);
        var again = LogIn(second, 0x9421, 1);
        SessionHarness.Until(() => GearOf(second, again.Body).Gear.Stance == Stances.Pistol, "the gear rebuilt on bind");
        Harness.Ticks(2);
        var (after, damageAfter) = GearOf(second, again.Body);
        Assert.Multiple(() =>
        {
            Assert.That((after.DelayMs, after.RangeDm, after.MitKinetic, after.MitEnergy, after.Stance, after.DamageType),
                Is.EqualTo((before.DelayMs, before.RangeDm, before.MitKinetic, before.MitEnergy, before.Stance, before.DamageType)));
            Assert.That(damageAfter, Is.EqualTo(damage));
        });
        AuditOk(second, "after the reopen");
    }
}
