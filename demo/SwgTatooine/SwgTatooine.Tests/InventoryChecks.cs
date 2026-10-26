using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using NUnit.Framework;
using SwgTatooine.Server;
using SwgTatooine.Server.Accounts;
using Typhon.Engine;
using Typhon.Protocol;

namespace SwgTatooine.Tests;

/// <summary>
/// Inventory and loot (inventory-loot.md): a kill drops into its killer's inventory as item ledger rows, a broken lair drops a cache, a full inventory loses
/// what does not fit, and the item audit replays the ledger and catches every rule broken.
/// </summary>
/// <remarks>
/// Kills are real where the criterion is about combat (IL-1, IL-3): a creature or a lair set to a few hit points and shot by the player. Elsewhere a kill is
/// staged (<see cref="SimBridge.StageKill"/>) onto the real consumer path, and an inventory is filled by staged transfers through the real
/// <c>ItemTransfer</c>. Players are parked in the planet's far south-west, where nothing else happens to them.
/// </remarks>
[Category("Integration")]
[TestFixture]
[NonParallelizable]
internal sealed class InventoryChecks : ServedChecks
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

    private static (float X, float Z) OpenGround(TatooineSim sim)
    {
        var reach = sim.Config.WorldEdgeM * 0.5f * WorldBuilder.InsideEdge * 0.9f;
        return (-reach, -reach);
    }

    /// <summary>A player logged in, parked at a point, its wallet open; its link, body and character.</summary>
    private (FakeLink Link, EntityId Body, EntityId Character, long Parked) LogIn(TatooineSim sim, ulong account, float x, float z, long bindings = 1)
    {
        var link = Harness.Connect(TatooineReplication.PlayerKind, EntityId.FromRawValue(account));
        SessionHarness.Until(() => TatooineReplication.Intents.Possessions >= bindings, "the avatar to be spawned");
        Harness.Ticks(2);
        var body = Worlds.Park(sim, link.Session, x, z, out var parked);
        Assert.That(CharacterStore.TryFind(sim.Dbe, EntityId.FromRawValue(account), out var character, out _), Is.True, "the account has no character");
        SessionHarness.Until(() => Ledgers.TryWallet(sim, character, out _), "the wallet to be opened");
        Harness.Ticks(2);
        return (link, body, character, parked);
    }

    /// <summary>Stages transfers and waits until the ledger has <paramref name="rows"/> more rows.</summary>
    private void Mint(TatooineSim sim, EntityId character, int rows, params (ushort Template, byte Mk, int Count)[] lines)
    {
        var before = Items.Rows(sim).Count;
        foreach (var (template, mk, count) in lines)
        {
            sim.Bridge.StageTransfer(ItemLedger.World, (long)character.RawValue, template, mk, count, ItemMoveKind.Kill, 1);
        }

        SessionHarness.Until(() => Items.Rows(sim).Count >= before + rows, "the staged transfers to be written");
        Harness.Ticks(1);
    }

    private static List<(EntityId Id, T Value)> Find<T>(TatooineSim sim, Func<Transaction, EntityId, bool> keep, Func<Transaction, EntityId, T> read)
    {
        var found = new List<(EntityId, T)>();
        sim.WriteInTick(tx =>
        {
            if (typeof(T) == typeof(Lair))
            {
                foreach (var cluster in tx.For<CreatureLair>().GetClusterEnumerator())
                {
                    if (cluster.Realm.Value != 0)
                    {
                        continue;
                    }

                    for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
                    {
                        var id = cluster.GetEntityId(System.Numerics.BitOperations.TrailingZeroCount(bits));
                        if (keep(tx, id))
                        {
                            found.Add((id, read(tx, id)));
                        }
                    }
                }

                return;
            }

            foreach (var cluster in tx.For<Creature>().GetClusterEnumerator())
            {
                if (cluster.Realm.Value != 0)
                {
                    continue;
                }

                for (var bits = cluster.OccupancyBits; bits != 0; bits &= bits - 1)
                {
                    var id = cluster.GetEntityId(System.Numerics.BitOperations.TrailingZeroCount(bits));
                    if (keep(tx, id))
                    {
                        found.Add((id, read(tx, id)));
                    }
                }
            }
        });

        return found;
    }

    // ── IL-1, IL-3: real kills ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// IL-1: a creature killed by the player's own shot drops into that character's inventory as rows of kind <c>kill</c>, from World, naming the victim;
    /// the stacks are the pure roll's for that victim and tick; the body's items revision moves; the audit is clean.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void AKill_DropsIntoTheLastHittersInventory_AsLedgerRows()
    {
        using var sim = Start();
        var (x, z) = OpenGround(sim);
        var p = LogIn(sim, 0x7401, x, z);
        var creatures = Find(sim,
            (tx, id) => tx.Open(id).Read(Creature.Vitals).Health > 0 && tx.Open(id).Read(Creature.Ai).Mode != AiMode.Dead,
            (tx, id) => (Brain: tx.Open(id).Read(Creature.Ai), At: tx.Open(id).Read(Creature.Bounds)));
        Assert.That(creatures, Is.Not.Empty, "the premise: a creature on the planet");
        var victim = creatures[0].Id;
        var species = creatures[0].Value.Brain.Template;
        var level = creatures[0].Value.Brain.Level;

        // One blow from dead, standing still where it is, and the player beside it.
        var at = (X: 0f, Z: 0f);
        sim.WriteInTick(tx =>
        {
            var c = tx.OpenMut(victim);
            var v = c.Read(Creature.Vitals);
            v.Health = 1;
            c.Set(Creature.Vitals, v);
            var b = c.Read(Creature.Bounds);
            at = (b.X, b.Z);
        });
        Worlds.Park(sim, p.Link.Session, at.X + 6f, at.Z, out var parked);
        SessionHarness.UntilNamed(p.Link.Session, victim, "the creature to reach the client's view", parked);
        var rev = Items.ItemsRev(sim, p.Body);
        p.Link.SendCommand(nameof(SetTarget), ClientSays.SetTarget(SessionHarness.NetIdOf(p.Link.Session, victim)));
        p.Link.SendCommand(nameof(SetStance), ClientSays.SetStance(Stances.Pistol));
        SessionHarness.Until(() => Items.Rows(sim).Count > 0, "the kill's drop to be written");
        Harness.Ticks(2);

        var rows = Items.Rows(sim);
        var drop = LootTables.Roll((byte)species, level, DropSource.Kill, victim.RawValue, rows[0].WorldTick);
        var want = new Dictionary<(ushort, byte), int>();
        for (var i = 0; i < drop.Lines; i++)
        {
            want[(drop[i].Template, drop[i].Mk)] = drop[i].Count;
        }

        var stacks = Items.Stacks(sim, p.Character);
        var audit = ItemEndpoints.Audit(sim.Dbe);
        Assert.Multiple(() =>
        {
            Assert.That(rows, Is.Not.Empty);
            Assert.That(rows.All(r => r.Kind == ItemMoveKind.Kill), Is.True, "a row of another kind");
            Assert.That(rows.All(r => r.From == ItemLedger.World && r.To == (long)p.Character.RawValue), Is.True, "not from World to the killer's character");
            Assert.That(rows.All(r => r.Ref == (long)victim.RawValue), Is.True, "a row does not name the victim");
            Assert.That(rows.All(r => r.WorldTick == rows[0].WorldTick), Is.True, "one drop, one tick");
            Assert.That(rows.Sum(r => r.Count), Is.EqualTo(drop.Units), "the units are not the roll's");
            Assert.That(stacks.ToDictionary(s => (s.Stack.Template, s.Stack.Mk), s => s.Stack.Count), Is.EquivalentTo(want), "the stacks are not the roll's");
            Assert.That(rows, Has.Count.EqualTo(drop.Lines), "one row per stack touched");
            Assert.That(Items.ItemsRev(sim, p.Body), Is.GreaterThan(rev), "the body was not told its inventory moved");
            Assert.That(sim.Bridge.ItemDropsBy(DropSource.Kill), Is.EqualTo(1));
            Assert.That(audit.Ok, Is.True, string.Join("; ", audit.Violations));
        });
    }

    /// <summary>IL-3: a wild lair broken by the player drops one cache: one core or add-on and six units, kind <c>lair</c>, one Mk.</summary>
    [Test]
    [CancelAfter(120_000)]
    public void ABrokenWildLair_DropsOneCache_ToItsLastHitter()
    {
        using var sim = Start();
        var (x, z) = OpenGround(sim);
        var p = LogIn(sim, 0x7402, x, z);
        var lairs = Find(sim,
            (tx, id) => tx.Open(id).Read(CreatureLair.Spawner).Mission.IsNull && tx.Open(id).Read(CreatureLair.Vitals).Health > 0,
            (tx, id) => tx.Open(id).Read(CreatureLair.Spawner));
        Assert.That(lairs, Is.Not.Empty, "the premise: a wild lair on the planet");
        var lair = lairs[0].Id;
        Clear(sim, lair, health: 5);
        var at = (X: 0f, Z: 0f);
        sim.WriteInTick(tx =>
        {
            var b = tx.Open(lair).Read(CreatureLair.Bounds);
            at = (b.X, b.Z);
        });
        Worlds.Park(sim, p.Link.Session, at.X + 10f, at.Z, out var parked);
        SessionHarness.UntilNamed(p.Link.Session, lair, "the lair to reach the client's view", parked);
        p.Link.SendCommand(nameof(SetTarget), ClientSays.SetTarget(SessionHarness.NetIdOf(p.Link.Session, lair)));
        p.Link.SendCommand(nameof(SetStance), ClientSays.SetStance(Stances.Pistol));
        SessionHarness.Until(() => Items.Rows(sim).Count > 0, "the lair's cache to be written");
        Harness.Ticks(2);

        var rows = Items.Rows(sim);
        var parts = rows.Where(r => ItemCatalog.TryGet(r.Template, out var t) && t.Category is ItemCategory.Core or ItemCategory.Addon).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(rows.All(r => r.Kind == ItemMoveKind.Lair && r.Ref == (long)lair.RawValue), Is.True, "not lair rows naming the lair");
            Assert.That(parts, Has.Count.EqualTo(1), "a cache holds exactly one core or add-on");
            Assert.That(parts.Sum(r => r.Count), Is.EqualTo(1));
            Assert.That(rows.Sum(r => r.Count) - 1, Is.EqualTo(LootTables.WildCacheUnits), "a wild cache's units");
            Assert.That(rows.Select(r => r.Mk).Distinct().Count(), Is.EqualTo(1), "one Mk per cache");
            Assert.That(rows[0].Mk, Is.InRange(1, 4), "Mk V in the wild");
            Assert.That(sim.Bridge.ItemDropsBy(DropSource.WildCache), Is.EqualTo(1));
            Assert.That(ItemEndpoints.Audit(sim.Dbe).Ok, Is.True);
        });
    }

    // ── IL-2: who gets nothing ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>IL-2: the crowd, a killer farther than the reach, and one in another realm get nothing, nothing is written, and each is counted.</summary>
    [Test]
    [CancelAfter(120_000)]
    public void NobodyWithinReach_OrTheCrowd_GetsNothing()
    {
        using var sim = Start();
        var (x, z) = OpenGround(sim);
        var p = LogIn(sim, 0x7403, x, z);
        var crowd = EntityId.Null;
        sim.WriteInTick(tx =>
        {
            foreach (var cluster in tx.For<Player>().GetClusterEnumerator())
            {
                var controls = cluster.GetReadOnlySpan(Player.Control);
                for (var bits = cluster.OccupancyBits; bits != 0 && crowd.IsNull; bits &= bits - 1)
                {
                    var slot = System.Numerics.BitOperations.TrailingZeroCount(bits);
                    if (controls[slot].Kind == ControllerKind.Scripted)
                    {
                        crowd = cluster.GetEntityId(slot);
                    }
                }
            }
        });
        Assert.That(crowd.IsNull, Is.False, "the premise: a crowd body");

        sim.Bridge.StageKill(Items.Kill(crowd, 0xA001, x, z, CreatureTemplates.Rat));
        sim.Bridge.StageKill(Items.Kill(EntityId.Null, 0xA002, x, z, CreatureTemplates.Rat));
        sim.Bridge.StageKill(Items.Kill(p.Body, 0xA003, x + 80f, z, CreatureTemplates.Rat));
        sim.Bridge.StageKill(Items.Kill(p.Body, 0xA004, x, z, CreatureTemplates.Rat, realm: 1));
        SessionHarness.Until(() => sim.Bridge.LootRefusedTotal >= 4, "the four kills to be refused");

        // And within reach, the same: the premise that the staged path does loot.
        sim.Bridge.StageKill(Items.Kill(p.Body, 0xA005, x + 30f, z, CreatureTemplates.Rat));
        SessionHarness.Until(() => Items.Rows(sim).Count > 0, "the kill within reach to be looted");
        Harness.Ticks(1);
        var rows = Items.Rows(sim);
        Assert.Multiple(() =>
        {
            Assert.That(sim.Bridge.LootRefusedCrowd, Is.EqualTo(2), "the crowd body and no body");
            Assert.That(sim.Bridge.LootRefusedReach, Is.EqualTo(1), "80 m away");
            Assert.That(sim.Bridge.LootRefusedRealm, Is.EqualTo(1), "another realm");
            Assert.That(sim.Bridge.LootRefusedWallet, Is.Zero);
            Assert.That(rows.All(r => r.Ref == 0xA005), Is.True, "a refused kill was looted");
            Assert.That(sim.Bridge.ItemDrops, Is.EqualTo(1));
        });
    }

    // ── IL-4: a mission lair broken by somebody else ────────────────────────────────────────────────────────────────

    /// <summary>
    /// IL-4: a mission lair's own death is never looted from the kill queue — its cache is the desk's, and only for its holder's blow: broken without it, no
    /// cache, no row, the mission still held.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void AMissionLairBrokenBySomebodyElse_DropsNoCache()
    {
        using var sim = Start();
        var terminal = sim.Index.Terminals[0];
        var p = LogIn(sim, 0x7404, terminal.X + 2f, terminal.Z);
        var offers = new List<(EntityId Id, MissionOfferInfo Info)>();
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
                        offers.Add((cluster.GetEntityId(slot), infos[slot]));
                    }
                }
            }
        });
        Assert.That(offers, Is.Not.Empty, "the premise: offers at the terminal");
        SessionHarness.UntilNamed(p.Link.Session, offers[0].Id, "the offer to reach the client's view", p.Parked);
        p.Link.SendCommand(nameof(TakeMission), new RecordValues
        {
            ["offer"] = FieldValue.Of(SessionHarness.NetIdOf(p.Link.Session, offers[0].Id)),
            ["serial"] = FieldValue.Of(offers[0].Info.Serial),
        });
        SessionHarness.Until(() => HeldBy(sim, p.Character).Count == 1 && !HeldBy(sim, p.Character)[0].Mission.Lair.IsNull, "the mission and its lair");
        var held = HeldBy(sim, p.Character)[0];

        // Broken with no blow of its holder's (OwnerHit 0), and its death pushed as the drain would push it - at the holder's feet, so only the skip of a
        // mission lair's death keeps it from being looted.
        sim.WriteInTick(tx =>
        {
            var l = tx.OpenMut(held.Mission.Lair);
            var v = l.Read(CreatureLair.Vitals);
            v.Health = 0;
            l.Set(CreatureLair.Vitals, v);
        });
        sim.Bridge.StageKill(Items.Kill(p.Body, held.Mission.Lair.RawValue, terminal.X + 2f, terminal.Z, (byte)held.Mission.Kind,
            kind: KillVictimKind.Lair, mission: held.Id));
        SessionHarness.Until(() => HeldBy(sim, p.Character)[0].Mission.Lair != held.Mission.Lair, "a new lair to be drawn");
        Harness.Ticks(Worlds.ServedHz / SimBridge.MissionPassHz + 2);
        Assert.Multiple(() =>
        {
            Assert.That(Items.Rows(sim), Is.Empty, "a cache, or the lair looted as a kill");
            Assert.That(HeldBy(sim, p.Character)[0].Id, Is.EqualTo(held.Id), "the mission is no longer held");
            Assert.That(sim.Bridge.ItemDropsBy(DropSource.MissionCache), Is.Zero);
            Assert.That(sim.Bridge.LootRefusedTotal, Is.Zero, "refused rather than skipped");
        });
    }

    // ── IL-5: stacking and the full inventory ───────────────────────────────────────────────────────────────────────

    /// <summary>IL-5: 97 and 4 more of the same template and Mk make 99 and 2 in the lowest free slot: two rows, ToAfter 99 and 2.</summary>
    [Test]
    [CancelAfter(120_000)]
    public void ADrop_StacksUpTo99_ThenOpensASlot()
    {
        using var sim = Start();
        var (x, z) = OpenGround(sim);
        var p = LogIn(sim, 0x7405, x, z);
        Mint(sim, p.Character, 3, (ItemCatalog.Metal, 1, 5), (ItemCatalog.Hide, 2, 97), (ItemCatalog.Bone, 1, 5));
        var hide = Items.Stacks(sim, p.Character).Single(s => s.Stack.Template == ItemCatalog.Hide);

        // A hole at slot 1: the metal given back to World, so the next new stack opens there.
        sim.Bridge.StageTransfer((long)p.Character.RawValue, ItemLedger.World, ItemCatalog.Metal, 1, 5, ItemMoveKind.Kill, 1);
        SessionHarness.Until(() => Items.Rows(sim).Count == 4, "the metal to be destroyed");
        Mint(sim, p.Character, 2, (ItemCatalog.Hide, 2, 4));

        var rows = Items.Rows(sim);
        var stacks = Items.Stacks(sim, p.Character);
        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(6));
            Assert.That(rows[4].ToStack, Is.EqualTo((long)hide.Id.RawValue), "the existing stack is not filled first");
            Assert.That(rows[4].Count, Is.EqualTo(2));
            Assert.That(rows[4].ToAfter, Is.EqualTo(ItemLedger.StackMax));
            Assert.That(rows[5].Count, Is.EqualTo(2));
            Assert.That(rows[5].ToAfter, Is.EqualTo(2));
            Assert.That(stacks.Single(s => s.Stack.Template == ItemCatalog.Hide && s.Stack.Count == 2).Stack.Slot, Is.EqualTo(1), "not the lowest free slot");
            Assert.That(stacks.Single(s => s.Id == hide.Id).Stack.Count, Is.EqualTo(ItemLedger.StackMax));
            Assert.That(ItemEndpoints.InventoryJson(sim.Dbe, p.Character),
                Does.Contain("\"item\":\"mat_metal\",\"name\":\"Metal\",\"category\":\"material\",\"mk\":1,\"count\":5,\"ref\":\"1\",\"dir\":\"out\"}"),
                "the metal given back is not a row out of the inventory");
            Assert.That(ItemEndpoints.Audit(sim.Dbe).Violations.All(v => v.Contains("must go from World", StringComparison.Ordinal)), Is.True,
                "the replay of a mint, a destroy and a fill is clean but for the destroy's kind, which no system makes yet");
        });
    }

    /// <summary>
    /// IL-5: 30 full stacks, then a drop: nothing fits, nothing is minted, no row; the loss is counted, noted in <c>GET /swg/inventory</c> and the body told.
    /// With room for part of it, what fits is kept and the rest lost.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void AFullInventory_LosesTheDrop_AndSaysSo()
    {
        using var sim = Start();
        var (x, z) = OpenGround(sim);
        var p = LogIn(sim, 0x7406, x, z);

        // A frog drops glands only, at Mk I to III for a level-0 frog: three gland stacks at 97, and 27 full stacks of everything else.
        var lines = new List<(ushort, byte, int)> { (ItemCatalog.Gland, 1, 97), (ItemCatalog.Gland, 2, 97), (ItemCatalog.Gland, 3, 97) };
        foreach (var t in ItemCatalog.Templates)
        {
            for (byte mk = 1; mk <= 2 && lines.Count < ItemLedger.Slots; mk++)
            {
                if (t.Lootable && t.Id != ItemCatalog.Gland)
                {
                    lines.Add((t.Id, mk, ItemLedger.StackMax));
                }
            }
        }

        Mint(sim, p.Character, ItemLedger.Slots, [.. lines]);
        Assert.That(Items.Stacks(sim, p.Character), Has.Count.EqualTo(ItemLedger.Slots), "the premise: 30 stacks");

        // Part of it fits: two glands, the rest lost.
        var rev = Items.ItemsRev(sim, p.Body);
        var rows = Items.Rows(sim).Count;
        sim.Bridge.StageKill(Items.Kill(p.Body, 0xB001, x, z, CreatureTemplates.Frog));
        SessionHarness.Until(() => sim.Bridge.ItemsLost > 0, "the drop to be lost in part");
        Harness.Ticks(1);
        var partial = Items.Rows(sim);
        var lostPartial = sim.Bridge.ItemsLost;
        var json = ItemEndpoints.InventoryJson(sim.Dbe, p.Character);

        // Nothing fits now — every gland stack topped up to 99, whatever Mk the first drop had — the whole drop lost, no row.
        for (byte mk = 1; mk <= 3; mk++)
        {
            sim.Bridge.StageTransfer(ItemLedger.World, (long)p.Character.RawValue, ItemCatalog.Gland, mk, 2, ItemMoveKind.Kill, 1);
        }

        SessionHarness.Until(() => Items.Stacks(sim, p.Character).TrueForAll(s => s.Stack.Count == ItemLedger.StackMax), "every stack to be full");
        var full = Items.Rows(sim).Count;
        sim.Bridge.StageKill(Items.Kill(p.Body, 0xB002, x, z, CreatureTemplates.Frog));
        SessionHarness.Until(() => sim.Bridge.ItemsLost > lostPartial, "the drop to be lost");
        Harness.Ticks(1);
        using var doc = JsonDocument.Parse(ItemEndpoints.InventoryJson(sim.Dbe, p.Character));
        var lost = doc.RootElement.GetProperty("lost");
        Assert.Multiple(() =>
        {
            Assert.That(partial, Has.Count.EqualTo(rows + 1), "the two glands that fit are one row");
            Assert.That(partial[^1].Count, Is.EqualTo(2));
            Assert.That(partial[^1].ToAfter, Is.EqualTo(ItemLedger.StackMax));
            Assert.That(lostPartial, Is.GreaterThanOrEqualTo(2), "the glands that did not fit");
            Assert.That(json, Does.Contain("\"lost\":[{\"n\":1,"), "the partial loss is not noted");
            Assert.That(full, Is.EqualTo(rows + 3), "the premise: two of the gland stacks topped up");
            Assert.That(Items.Rows(sim), Has.Count.EqualTo(full), "a row for a drop that did not fit");
            Assert.That(sim.Bridge.ItemsLost - lostPartial, Is.GreaterThanOrEqualTo(LootTables.LootKillUnits), "the whole drop lost");
            Assert.That(lost.GetArrayLength(), Is.GreaterThanOrEqualTo(2));
            Assert.That(lost[lost.GetArrayLength() - 1].GetProperty("item").GetString(), Is.EqualTo("mat_gland").Or.StartsWith("core_").Or.StartsWith("addon_"));
            Assert.That(Items.ItemsRev(sim, p.Body), Is.GreaterThanOrEqualTo(rev + 2), "the body was not told of each loss");
            Assert.That(sim.Bridge.ItemsLostOf(ItemCatalog.Gland, partial[^1].Mk), Is.GreaterThanOrEqualTo(2), "the loss is not counted by template and Mk");
            Assert.That(ItemEndpoints.Audit(sim.Dbe).Ok, Is.True);
        });
    }

    // ── IL-7: the writers ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>IL-7: the schedule's declared writers of the three item components are exactly <c>Loot</c> and <c>Missions</c>, and Loot runs after the
    /// Ledger, which runs after the desk.</summary>
    [Test]
    [CancelAfter(60_000)]
    public void OnlyLootAndTheDeskWriteItems()
    {
        using var sim = Start();
        var systems = sim.Runtime.Systems;
        foreach (var type in new[] { typeof(ItemStack), typeof(ItemLedgerEntry), typeof(ItemLedgerHead) })
        {
            var writers = systems.Where(s => s.Access.Writes.Contains(type) || s.Access.SideWrites.Contains(type)).Select(s => s.Name).OrderBy(n => n);
            Assert.That(writers, Is.EqualTo(new[] { "Loot", "Missions" }), $"the writers of {type.Name}");
        }

        int IndexOf(string name) => Array.FindIndex(systems, s => s.Name == name);
        bool Reaches(int from, int to)
        {
            var seen = new HashSet<int>();
            var stack = new Stack<int>();
            stack.Push(from);
            while (stack.Count > 0)
            {
                foreach (var next in systems[stack.Pop()].Successors)
                {
                    if (next == to)
                    {
                        return true;
                    }

                    if (seen.Add(next))
                    {
                        stack.Push(next);
                    }
                }
            }

            return false;
        }

        Assert.Multiple(() =>
        {
            Assert.That(Reaches(IndexOf("Missions"), IndexOf("Ledger")), Is.True, "the Ledger is not after the desk");
            Assert.That(Reaches(IndexOf("Ledger"), IndexOf("Loot")), Is.True, "Loot is not after the Ledger");
            Assert.That(Reaches(IndexOf("Loot"), IndexOf("Missions")), Is.False, "Loot before the desk");
        });
    }

    // ── IL-8: the audit ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// IL-8: the item audit names each rule a corrupted ledger breaks — a stack count, a duplicate slot, a gap, an orphan stack, Mk V from a kill, a mission's
    /// cache on a second tick, a stack emptied by the replay and still stored — and passes again once each is put back.
    /// </summary>
    [Test]
    [CancelAfter(120_000)]
    public void TheItemAudit_NamesEveryRuleACorruptedLedgerBreaks()
    {
        using var sim = Start();
        var (x, z) = OpenGround(sim);
        var p = LogIn(sim, 0x7407, x, z);
        Mint(sim, p.Character, 3, (ItemCatalog.Metal, 1, 10), (ItemCatalog.Hide, 2, 7), (ItemCatalog.Bone, 3, 4));
        sim.Bridge.StageTransfer((long)p.Character.RawValue, ItemLedger.World, ItemCatalog.Bone, 3, 1, ItemMoveKind.Kill, 1);
        SessionHarness.Until(() => Items.Rows(sim).Count == 4, "the bone to be given back");
        Harness.Ticks(1);
        var owner = (long)p.Character.RawValue;
        var stacks = Items.Stacks(sim, p.Character);
        var metal = stacks.Single(s => s.Stack.Template == ItemCatalog.Metal);
        var hide = stacks.Single(s => s.Stack.Template == ItemCatalog.Hide);
        var rows = Items.Rows(sim, out var rowIds);
        var baseline = ItemEndpoints.Audit(sim.Dbe);

        Corrupt(sim, tx => Edit(tx, metal.Id, ItemStackArch.Stack, (ref s) => s.Count += 3));
        var count = ItemEndpoints.Audit(sim.Dbe);
        Corrupt(sim, tx => Edit(tx, metal.Id, ItemStackArch.Stack, (ref s) => s.Count -= 3));

        Corrupt(sim, tx => Edit(tx, hide.Id, ItemStackArch.Stack, (ref s) => s.Slot = metal.Stack.Slot));
        var slot = ItemEndpoints.Audit(sim.Dbe);
        Corrupt(sim, tx => Edit(tx, hide.Id, ItemStackArch.Stack, (ref s) => s.Slot = hide.Stack.Slot));

        Corrupt(sim, tx => Edit(tx, rowIds[^1], ItemLedgerArch.Entry, (ref e) => e.Seq = 9));
        var gap = ItemEndpoints.Audit(sim.Dbe);
        Corrupt(sim, tx => Edit(tx, rowIds[^1], ItemLedgerArch.Entry, (ref e) => e.Seq = 4));

        var orphan = EntityId.Null;
        Corrupt(sim, tx =>
        {
            var s = new ItemStack { Owner = 424_242, Template = ItemCatalog.Metal, Mk = 1, Slot = 1, Count = 5, Rows = 1 };
            orphan = tx.Spawn<ItemStackArch>(ItemStackArch.Stack.Set(in s));
        });
        var orphaned = ItemEndpoints.Audit(sim.Dbe);
        Corrupt(sim, tx => tx.Destroy(orphan));

        Corrupt(sim, tx => Edit(tx, rowIds[0], ItemLedgerArch.Entry, (ref e) => e.Mk = 5));
        var mkV = ItemEndpoints.Audit(sim.Dbe);
        Corrupt(sim, tx => Edit(tx, rowIds[0], ItemLedgerArch.Entry, (ref e) => e.Mk = 1));

        // The bone given back corrupted into the whole stack: the replay empties it, and it is still stored.
        Corrupt(sim, tx => Edit(tx, rowIds[3], ItemLedgerArch.Entry, (ref e) =>
        {
            e.Count = 4;
            e.FromAfter = 0;
        }));
        var emptied = ItemEndpoints.Audit(sim.Dbe);
        Corrupt(sim, tx => Edit(tx, rowIds[3], ItemLedgerArch.Entry, (ref e) =>
        {
            e.Count = 1;
            e.FromAfter = 3;
        }));

        // Two rows of one mission's cache on two ticks: the first two mints restated as one mission's.
        Corrupt(sim, tx =>
        {
            Edit(tx, rowIds[0], ItemLedgerArch.Entry, (ref e) =>
            {
                e.Kind = ItemMoveKind.Mission;
                e.Ref = 77;
            });
            Edit(tx, rowIds[1], ItemLedgerArch.Entry, (ref e) =>
            {
                e.Kind = ItemMoveKind.Mission;
                e.Ref = 77;
                e.WorldTick = rows[0].WorldTick + 1;
            });
        });
        var twice = ItemEndpoints.Audit(sim.Dbe);
        Corrupt(sim, tx =>
        {
            Edit(tx, rowIds[0], ItemLedgerArch.Entry, (ref e) =>
            {
                e.Kind = ItemMoveKind.Kill;
                e.Ref = 1;
            });
            Edit(tx, rowIds[1], ItemLedgerArch.Entry, (ref e) =>
            {
                e.Kind = ItemMoveKind.Kill;
                e.Ref = 1;
                e.WorldTick = rows[1].WorldTick;
            });
        });

        var clean = ItemEndpoints.Audit(sim.Dbe);
        static bool Only(ItemAuditResult a) => a.Violations.All(v => v.Contains("must go from World", StringComparison.Ordinal));
        Assert.Multiple(() =>
        {
            Assert.That(baseline.ViolationCount, Is.EqualTo(1), "the premise: only the destroy's kind, which no system makes yet");
            Assert.That(Only(baseline), Is.True, string.Join("; ", baseline.Violations));
            Assert.That(count.Violations, Has.Some.StartsWith($"stack {(long)metal.Id.RawValue}: holds 13, the replay says 10"));
            Assert.That(count.Violations, Has.Some.StartsWith("conservation:"));
            Assert.That(slot.Violations, Has.Some.Contains($"slot {metal.Stack.Slot} of {owner} holds another stack too"));
            Assert.That(gap.Violations, Has.Some.StartsWith("seq 9: expected 4"));
            Assert.That(gap.Violations, Has.Some.StartsWith("head: last seq 4"));
            Assert.That(orphaned.Violations, Has.Some.Contains("its owner 424242 is not a character that exists"));
            Assert.That(orphaned.Violations, Has.Some.Contains("no item ledger row ever moved it"));
            Assert.That(mkV.Violations, Has.Some.StartsWith("seq 1: Mk V in a kill row"));
            Assert.That(emptied.Violations, Has.Some.Contains("emptied by the replay and still stored"));
            Assert.That(twice.Violations, Has.Some.StartsWith("seq 2: mission 77's cache minted again"));
            Assert.That(clean.ViolationCount, Is.EqualTo(1), string.Join("; ", clean.Violations));
            Assert.That(Only(clean), Is.True, string.Join("; ", clean.Violations));
        });
    }

    /// <summary>IL-8: the audit, the ledger and the inventory answer as JSON, with the fields the client and the dashboard read.</summary>
    [Test]
    [CancelAfter(60_000)]
    public void TheItemAuditEndpoint_AnswersItsVerdictAsJson()
    {
        using var sim = Start();
        var (x, z) = OpenGround(sim);
        var p = LogIn(sim, 0x7408, x, z);
        Mint(sim, p.Character, 1, (ItemCatalog.Hide, 2, 7));
        var audit = ItemEndpoints.Audit(sim.Dbe).ToJson();
        var ledger = ItemEndpoints.LedgerJson(sim.Dbe, 0, 10);
        var inventory = ItemEndpoints.InventoryJson(sim.Dbe, p.Character);
        var stack = Items.Stacks(sim, p.Character)[0].Id.RawValue;
        Assert.Multiple(() =>
        {
            Assert.That(audit, Does.StartWith("{\"ok\":true,\"entries\":1,\"lastSeq\":1,\"stacks\":1,\"units\":7,\"minted\":7,\"destroyed\":0,\"violationCount\":0,"
                + "\"violations\":[],\"ms\":"));
            Assert.That(ledger, Does.StartWith("{\"rows\":[{\"seq\":1,\"tick\":"));
            Assert.That(ledger, Does.Contain($"\"kind\":\"kill\",\"from\":\"0\",\"to\":\"{p.Character.RawValue}\",\"count\":7,\"fromAfter\":0,\"toAfter\":7,"));
            Assert.That(ledger, Does.Contain($"\"item\":\"mat_hide\",\"mk\":2,\"fromStack\":\"0\",\"toStack\":\"{stack}\"}}"));
            Assert.That(ledger, Does.EndWith("],\"next\":1}"));
            Assert.That(inventory, Does.StartWith(
                $"{{\"open\":true,\"slots\":30,\"items\":[{{\"id\":\"{stack}\",\"slot\":1,\"item\":\"mat_hide\",\"name\":\"Hide\",\"category\":\"material\",\"mk\":2,"
                + "\"count\":7}],\"recent\":[{\"seq\":\"1\",\"tick\":"));
            Assert.That(inventory, Does.EndWith(
                ",\"kind\":\"kill\",\"item\":\"mat_hide\",\"name\":\"Hide\",\"category\":\"material\",\"mk\":2,\"count\":7,\"ref\":\"1\",\"dir\":\"in\"}],\"lost\":[]}"));
        });
    }

    // ── IL-10: the bearer's own ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>IL-10: <c>GET /swg/inventory</c> answers the bearer's own stacks; 401 without a token or with a bad one; an account with no character, empty.</summary>
    [Test]
    [CancelAfter(120_000)]
    public void TheInventoryEndpoint_AnswersItsBearersOwn()
    {
        using var sim = Start();
        var (x, z) = OpenGround(sim);
        var a = LogIn(sim, 0x7409, x, z);
        LogIn(sim, 0x740A, x + 5f, z, 2);
        Mint(sim, a.Character, 1, (ItemCatalog.Venom, 1, 3));

        (int Status, string Body) Get(string authorization)
        {
            var context = new DefaultHttpContext();
            if (authorization != null)
            {
                context.Request.Headers.Authorization = authorization;
            }

            return ItemEndpoints.GetInventoryForChecks(sim.Dbe, context) switch
            {
                ContentHttpResult c => (c.StatusCode ?? 200, c.ResponseContent),
                IStatusCodeHttpResult s => (s.StatusCode ?? 0, null),
                var other => throw new AssertionException($"unexpected result {other}"),
            };
        }

        var mine = Get("Bearer " + AuthToken.Mint(EntityId.FromRawValue(0x7409)));
        var theirs = Get("Bearer " + AuthToken.Mint(EntityId.FromRawValue(0x740A)));
        Assert.Multiple(() =>
        {
            Assert.That(Get(null).Status, Is.EqualTo(401), "no token");
            Assert.That(Get("Bearer not-a-token").Status, Is.EqualTo(401), "a bad token");
            Assert.That(Get("Bearer " + AuthToken.Mint(EntityId.FromRawValue(0x740F))).Body, Is.EqualTo(ItemEndpoints.Empty), "an account with no character");
            Assert.That(mine.Status, Is.EqualTo(200));
            Assert.That(mine.Body, Does.Contain("\"item\":\"mat_venom\""), "the bearer's own stack is missing");
            Assert.That(theirs.Body, Does.StartWith("{\"open\":true,\"slots\":30,\"items\":[],\"recent\":[],\"lost\":[]}"), "another's items, or not open");
        });
    }

    // ── IL-9: across a reopen ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// IL-9: stacks, rows and the head survive a server stopped with its players in the world, and the sequence carries on: the next drop fills the old
    /// stacks, its row the next number, the audit clean on both sides.
    /// </summary>
    [Test]
    [CancelAfter(240_000)]
    public void TheItems_SurviveAStopWithPlayersInTheWorld_AndCarryOn()
    {
        List<(EntityId Id, ItemStack Stack)> before;
        int rowsBefore;
        EntityId character;
        using (var first = Start(persist: true))
        {
            var (x, z) = OpenGround(first);
            var p = LogIn(first, 0x740B, x, z);
            Mint(first, p.Character, 2, (ItemCatalog.Hide, 2, 50), (ItemCatalog.Bone, 1, 3));
            Harness.Ticks(3);
            character = p.Character;
            before = Items.Stacks(first, character);
            rowsBefore = Items.Rows(first).Count;
            Assert.That(ItemEndpoints.Audit(first.Dbe).Ok, Is.True, "the premise: a clean item ledger before the stop");

            // No Close: dropped the way a kill -9 drops it.
            Harness.Release();
        }

        TatooineReplication.ResetSessionAccounting();
        TatooineReplication.ResetIntentAccounting();

        using var second = Start(persist: true);
        Assert.That(second.Reopened, Is.True, "the second run must reopen the first's database, or this proves nothing");
        Harness.Ticks(3);
        var reopened = ItemEndpoints.Audit(second.Dbe);
        var after = Items.Stacks(second, character);
        Assert.Multiple(() =>
        {
            Assert.That(reopened.Ok, Is.True, string.Join("; ", reopened.Violations));
            Assert.That(reopened.Entries, Is.EqualTo(rowsBefore));
            Assert.That(after.Select(s => (s.Id, s.Stack.Slot, s.Stack.Count)), Is.EqualTo(before.Select(s => (s.Id, s.Stack.Slot, s.Stack.Count))));
        });

        var (x2, z2) = OpenGround(second);
        LogIn(second, 0x740B, x2, z2);
        Mint(second, character, 1, (ItemCatalog.Hide, 2, 4));
        var rows = Items.Rows(second);
        var audit = ItemEndpoints.Audit(second.Dbe);
        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Count.EqualTo(rowsBefore + 1));
            Assert.That(rows[^1].Seq, Is.EqualTo(rowsBefore + 1), "the sequence did not carry on");
            Assert.That(rows[^1].ToStack, Is.EqualTo((long)before.Single(s => s.Stack.Template == ItemCatalog.Hide).Id.RawValue), "a stack re-minted");
            Assert.That(rows[^1].ToAfter, Is.EqualTo(54));
            Assert.That(Items.Stacks(second, character), Has.Count.EqualTo(before.Count));
            Assert.That(audit.Ok, Is.True, string.Join("; ", audit.Violations));
        });
    }

    // ── IL-14 ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>IL-14: with <c>ItemsRev</c>, a <c>Player</c> cluster still holds at least 24 bodies (25 measured, 2026-10-26).</summary>
    [Test]
    [CancelAfter(60_000)]
    public void ThePlayerClusterStillHolds24Bodies()
    {
        using var sim = Start();
        var size = 0;
        sim.WriteInTick(tx =>
        {
            foreach (var cluster in tx.For<Player>().GetClusterEnumerator())
            {
                size = cluster.ClusterSize;
                break;
            }
        });
        Assert.That(size, Is.GreaterThanOrEqualTo(24), "ItemsRev took the Player page below 24 bodies: take the fallback of inventory-loot.md § 3.6");
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static List<(EntityId Id, HeldMission Mission)> HeldBy(TatooineSim sim, EntityId holder)
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

    /// <summary>A lair's occupants taken away and its guard count with them, its health set: one blow from broken.</summary>
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

    private static void Corrupt(TatooineSim sim, Action<Transaction> write)
    {
        using var tx = sim.Dbe.CreateQuickTransaction();
        write(tx);
        Assert.That(tx.Commit(), Is.True, "the corruption did not commit");
    }

    private delegate void Mutate<T>(ref T value);

    private static void Edit<T>(Transaction tx, EntityId id, Comp<T> component, Mutate<T> change)
        where T : unmanaged
    {
        var e = tx.OpenMut(id);
        var v = e.Read(component);
        change(ref v);
        e.Set(component, v);
    }
}
