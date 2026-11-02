using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace SwgTatooine.Server;

/// <summary>
/// Decay and repair (crafting-build.md C3, crafting-catalog.md § 9): the wear the combat path counts on the body (<see cref="PlayerGear"/>), written into
/// the worn items' condition here; falls; an item broken at 0 with a repair left, destroyed with none; repair at a station with a kit and a fee; salvage.
/// </summary>
/// <remarks>
/// <para>
/// <b>The combat path never writes an item</b> (§ 1 rule 3). An attack counts one use of the weapon, a creature's hit adds its damage to the armour's sum
/// — shared among the pieces by their shares when written, the per-hit draw's expectation over the 50 hits between two writes, in 4 bytes on the body
/// instead of 14; the body is queued here when it reaches <see cref="WearFlushUses"/> uses, falls, logs out, or changes what it wears — so a crash loses at
/// most that many uses (C3-2).
/// </para>
/// <para>
/// <b>Written in the Workshop's side transaction, the counters zeroed through the tick's after it commits</b>, like the gear: a failed commit leaves the
/// counters and the condition as they were, and the bodies are queued again.
/// </para>
/// </remarks>
public sealed partial class SimBridge
{
    /// <summary>Uses (attacks and hits) a body counts before its wear is written into its items (§ 9.1): the most a crash can lose.</summary>
    public const int WearFlushUses = 50;

    /// <summary>The armour's sum is written before it can overflow its 16 bits, whatever the uses.</summary>
    public const int WearFlushPoints = 60_000;

    /// <summary>Of every equipped item's max condition: lost when knocked out, and when killed (§ 9.1, "Falling").</summary>
    public const int KnockoutWearPerMille = 30;

    public const int DeathWearPerMille = 100;

    /// <summary>A repair restores the condition to the max, which it takes to this (§ 9.2); at most <see cref="MaxRepairs"/> of them.</summary>
    public const int RepairMaxPerMille = 750;

    public const int MaxRepairs = 2;

    /// <summary>The repair fee: of the item's market value (crafting-build.md C3).</summary>
    public const int RepairFeePerMille = 100;

    /// <summary>Of a crafted item's quality materials, what salvage returns, at the tier of its core (Q10).</summary>
    public const int SalvagePerMille = 250;

    // Bodies due a wear write: from the combat paths (any thread), by the uses they reached or the fall they took.
    private readonly ConcurrentQueue<EntityId> _wearDue = new();
    private readonly ConcurrentQueue<(EntityId Body, int PerMille)> _fallDue = new();

    // This pass's: each body and its fall, the snapshots of bodies gone, the bodies to zero after the commit.
    private readonly Dictionary<EntityId, int> _wearBodies = [];
    private readonly Dictionary<EntityId, EntityId> _wearCharacters = [];
    private readonly List<(EntityId Character, PlayerGear Gear)> _wearRetired = [];
    private readonly List<EntityId> _wearReset = [];
    private readonly List<ClientCommand<RepairItem>> _repairsApplied = [];
    private readonly List<ClientCommand<SalvageItem>> _salvagesApplied = [];

    // Counted in the pass, published when it commits.
    private WearCounts _wearCounts;
    private long _wearWrites;
    private long _wearSkipped;

    /// <summary>Worn-out items that could not be destroyed — never expected — counted and left for the audit to name.</summary>
    public long WearSkipped => Interlocked.Read(ref _wearSkipped);
    private long _itemsBroken;
    private long _itemsDecayed;
    private long _repairs;
    private long _repairFees;
    private long _salvages;
    private readonly long[] _decayedByBand = new long[6];

    private struct WearCounts
    {
        public long Writes;
        public long Broken;
        public long Decayed;
        public long Repairs;
        public long Fees;
        public long Salvages;
        public long Band1;
        public long Band2;
        public long Band3;
        public long Band4;
        public long Band5;
    }

    /// <summary>Bodies whose wear was written into their items since start.</summary>
    public long WearWrites => Interlocked.Read(ref _wearWrites);

    /// <summary>Items broken (0 condition, a repair left) since start.</summary>
    public long ItemsBroken => Interlocked.Read(ref _itemsBroken);

    /// <summary>Items destroyed by decay (0 condition, no repair left) since start.</summary>
    public long ItemsDecayed => Interlocked.Read(ref _itemsDecayed);

    /// <summary>Items destroyed by decay, by band (Mk 1-5), since start: C3-4's measure.</summary>
    public long ItemsDecayedOfBand(int mk) => Interlocked.Read(ref _decayedByBand[Math.Clamp(mk, 0, 5)]);

    /// <summary>Repairs applied, and the fees they paid, since start.</summary>
    public long Repairs => Interlocked.Read(ref _repairs);

    public long RepairFees => Interlocked.Read(ref _repairFees);

    /// <summary>Items salvaged since start.</summary>
    public long Salvages => Interlocked.Read(ref _salvages);

    /// <summary>A body reached <see cref="WearFlushUses"/> uses: its wear is written on the next Workshop pass. Any thread.</summary>
    internal void WearDue(EntityId body) => _wearDue.Enqueue(body);

    /// <summary>A body fell — knocked out or killed: every item it wears loses that much of its max, with its wear, on the next pass. Any thread.</summary>
    internal void FallDue(EntityId body, bool dead) => _fallDue.Enqueue((body, dead ? DeathWearPerMille : KnockoutWearPerMille));

    /// <summary>Whether wear waits to be written this tick: the Workshop runs for it — the bodies at their uses only on <paramref name="flushDue"/>.</summary>
    private bool WearPending(bool flushDue)
        => (flushDue && !_wearDue.IsEmpty) || !_fallDue.IsEmpty || !_wearStaged.IsEmpty || TatooineReplication.WearRetiredPending;

    // A check's counters for a body, set on the next Workshop pass: the Workshop is the gear's writer in the economy phase, so it sets them.
    private readonly ConcurrentQueue<(EntityId Body, int WeaponUses, int ArmorWear, int Uses, bool Due)> _wearStaged = new();

    /// <summary>For checks: sets a body's wear counters on the next Workshop pass, and makes it due when <paramref name="due"/>. Any thread.</summary>
    internal void StageWear(EntityId body, int weaponUses, int armorWear, int uses, bool due)
        => _wearStaged.Enqueue((body, weaponUses, armorWear, uses, due));

    private void ApplyStagedWear(Transaction tick0)
    {
        while (_wearStaged.TryDequeue(out var s))
        {
            if (!tick0.TryOpenMut(s.Body, out var player))
            {
                continue;
            }

            var g = player.Read(Player.Gear);
            g.WeaponUses = (byte)s.WeaponUses;
            g.ArmorWear = (ushort)s.ArmorWear;
            g.Uses = (byte)s.Uses;
            player.Set(Player.Gear, g);
            if (s.Due)
            {
                _wearBodies.TryAdd(s.Body, 0);
            }
        }
    }

    /// <summary>A body's weapon was used once: counted on the body (any thread, the body's own cluster); due when the uses reach the flush.</summary>
    internal static bool CountAttack(ref PlayerGear gear)
    {
        gear.WeaponUses = (byte)Math.Min(byte.MaxValue, gear.WeaponUses + 1);
        return CountUse(ref gear);
    }

    /// <summary>A creature's hit landed on a body: its damage, before mitigation, added to the armour's sum (§ 9.1).</summary>
    internal static bool CountHit(ref PlayerGear gear, int damage)
    {
        var points = Math.Min(ushort.MaxValue, gear.ArmorWear + Math.Max(0, damage));
        gear.ArmorWear = (ushort)points;
        return CountUse(ref gear) || points >= WearFlushPoints;
    }

    private static bool CountUse(ref PlayerGear gear)
    {
        if (gear.Uses < byte.MaxValue)
        {
            gear.Uses++;
        }

        return gear.Uses >= WearFlushUses;
    }

    /// <summary>
    /// The condition a slot's item loses to the wear counted: the weapon (k 0) its delay per use; an armour piece (1-6) its share of the armour's sum,
    /// which is what the hits drawn by share would have given it on average.
    /// </summary>
    internal static long WearOf(in PlayerGear g, int k, int weaponDelayMs)
        => k == 0 ? (long)g.WeaponUses * Math.Max(1, weaponDelayMs) : (long)g.ArmorWear * EquipSlot.SharePerMille[k] / 1000;

    /// <summary>
    /// The pass's wear: every body queued and every body whose commands this tick touch what it wears, its counters read through the tick; the snapshots
    /// of the bodies that logged out. Written into the items through <paramref name="tx"/>; the bodies to zero after the commit in <see cref="_wearReset"/>.
    /// </summary>
    private void WearTick(Transaction tx, Transaction tick0, long worldTick)
    {
        _wearReset.Clear();
        if (_wearBodies.Count == 0 && _wearRetired.Count == 0)
        {
            return;
        }

        TatooineReplication.FillBodyCharacters(_wearCharacters);
        foreach (var (body, fall) in _wearBodies)
        {
            if (!_wearCharacters.TryGetValue(body, out var character) || !tick0.TryOpen(body, out var player))
            {
                continue;   // the crowd, or a body gone: nothing it wears is an item
            }

            var gear = player.Read(Player.Gear);
            if (gear.Uses == 0 && fall == 0)
            {
                continue;
            }

            if (WearItems(tx, (long)character.RawValue, in gear, fall, worldTick))
            {
                _gearScratch.Add((character, body));
            }

            _wearReset.Add(body);
        }

        foreach (var (character, gear) in _wearRetired)
        {
            WearItems(tx, (long)character.RawValue, in gear, 0, worldTick);
        }
    }

    /// <summary>
    /// One character's wear into what it wears: a weapon loses its delay per use, a piece its damage points, every item <paramref name="fallPerMille"/> of its
    /// max. At 0 an item with a repair left is broken; one with none is destroyed, a <see cref="ItemMoveKind.Decay"/> row.
    /// </summary>
    /// <returns>Whether something it wears broke or is gone: its body's gear is due.</returns>
    private bool WearItems(Transaction tx, long character, in PlayerGear gear, int fallPerMille, long worldTick)
    {
        var items = ItemsOf(character);
        var changed = false;
        for (var k = 0; k < EquipSlot.Count; k++)
        {
            var piece = items.Equipped[k];
            if (piece.Count == 0 || !TryStatsOf(tx, piece.Stack, out var stats) || (stats.Flags & ItemFlags.Broken) != 0)
            {
                continue;
            }

            var loss = WearOf(in gear, k, stats.Stat1) + (long)stats.MaxCondition * fallPerMille / 1000;
            if (loss <= 0)
            {
                continue;
            }

            stats.Flags &= unchecked((byte)~ItemFlags.Pristine);
            _wearCounts.Writes++;
            if (stats.Condition > loss)
            {
                stats.Condition -= (int)loss;
                tx.OpenMut(piece.Stack).Set(ItemInstanceArch.Stats, stats);
                continue;
            }

            changed = true;
            stats.Condition = 0;
            if (stats.Repairs < MaxRepairs)
            {
                stats.Flags |= ItemFlags.Broken;
                tx.OpenMut(piece.Stack).Set(ItemInstanceArch.Stats, stats);
                _wearCounts.Broken++;
                continue;
            }

            if (DestroyWorn(tx, character, k, ItemMoveKind.Decay, 0, worldTick))
            {
                _wearCounts.Decayed++;
                switch (piece.Mk)
                {
                    case 1: _wearCounts.Band1++; break;
                    case 2: _wearCounts.Band2++; break;
                    case 3: _wearCounts.Band3++; break;
                    case 4: _wearCounts.Band4++; break;
                    case 5: _wearCounts.Band5++; break;
                }
            }
            else
            {
                // Not the instance it should be — a bug, counted and skipped: aborting would refuse every player's workshop commands, tick after tick,
                // since the body would be queued again to the same refusal (review, 2026-11-02).
                Interlocked.Increment(ref _wearSkipped);
            }
        }

        return changed;
    }

    /// <summary>The pass ended: counts published and the bodies' counters zeroed — or, not committed, everything queued again.</summary>
    private void EndWear(Transaction tick0, bool committed)
    {
        if (committed)
        {
            foreach (var body in _wearReset)
            {
                if (tick0.TryOpenMut(body, out var player))
                {
                    var g = player.Read(Player.Gear);
                    g.ArmorWear = 0;
                    g.WeaponUses = 0;
                    g.Uses = 0;
                    player.Set(Player.Gear, g);

                    // Its items' condition moved: its client reads the inventory again, as after any change to it.
                    BumpItems(tick0, body);
                }
            }

            Interlocked.Add(ref _wearWrites, _wearCounts.Writes);
            Interlocked.Add(ref _itemsBroken, _wearCounts.Broken);
            Interlocked.Add(ref _itemsDecayed, _wearCounts.Decayed);
            Interlocked.Add(ref _decayedByBand[1], _wearCounts.Band1);
            Interlocked.Add(ref _decayedByBand[2], _wearCounts.Band2);
            Interlocked.Add(ref _decayedByBand[3], _wearCounts.Band3);
            Interlocked.Add(ref _decayedByBand[4], _wearCounts.Band4);
            Interlocked.Add(ref _decayedByBand[5], _wearCounts.Band5);
            Interlocked.Add(ref _repairs, _wearCounts.Repairs);
            Interlocked.Add(ref _repairFees, _wearCounts.Fees);
            Interlocked.Add(ref _salvages, _wearCounts.Salvages);
        }
        else
        {
            // Nothing was written: the counters are still on the bodies, the falls and the snapshots are queued again.
            foreach (var (body, fall) in _wearBodies)
            {
                if (fall > 0)
                {
                    _fallDue.Enqueue((body, fall));
                }
                else
                {
                    _wearDue.Enqueue(body);
                }
            }

            TatooineReplication.RequeueWearRetired(_wearRetired);
        }

        _wearBodies.Clear();
        _wearRetired.Clear();
        _wearCounts = default;
    }

    /// <summary>The pass's bodies: the queued, and those whose commands this tick change what they wear (their wear written before the item moves).</summary>
    private void GatherWear(Transaction tick0, SubscriptionsCommands subs, bool flushDue)
    {
        ApplyStagedWear(tick0);

        // The bodies at their 50 uses, once a second: a write of their items is a side commit, and 2 000 fighting bodies reach 50 uses tens of times a
        // second — gathered, one commit a second instead of one a tick (review, 2026-11-02). A crash loses ≤ 50 uses and that second.
        while (flushDue && _wearDue.TryDequeue(out var body))
        {
            _wearBodies.TryAdd(body, 0);
        }

        while (_fallDue.TryDequeue(out var fall))
        {
            _wearBodies[fall.Body] = (_wearBodies.TryGetValue(fall.Body, out var was) ? was : 0) + fall.PerMille;
        }

        TatooineReplication.DrainWearRetired(_wearRetired);
        if (subs == null)
        {
            return;
        }

        foreach (var c in subs.Commands<Equip>())
        {
            AddWearOf(c.Session);
        }

        foreach (var c in subs.Commands<Unequip>())
        {
            AddWearOf(c.Session);
        }

        foreach (var c in subs.Commands<RepairItem>())
        {
            AddWearOf(c.Session);
        }

        foreach (var c in subs.Commands<SalvageItem>())
        {
            AddWearOf(c.Session);
        }
    }

    private void AddWearOf(SessionId session)
    {
        if (TatooineReplication.TryBodyOfSession(session, out var body, out _))
        {
            _wearBodies.TryAdd(body, 0);
        }
    }

    // ── Repair ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The kit a template is repaired with (§ 9.2): weapons and armour; 0 for anything else.</summary>
    internal static ushort KitOf(ushort template)
    {
        var slot = Equipment.SlotOf(template);
        return slot == 0 ? (ushort)0 : slot == EquipSlot.Weapon ? ItemCatalog.KitWeaponRepair : ItemCatalog.KitArmorRepair;
    }

    /// <summary>A repair's fee, whole credits: <see cref="RepairFeePerMille"/> of the item's market value, rounded down.</summary>
    internal static long RepairFee(ushort template, byte mk) => MarketValues.MilliOf(template, mk) * RepairFeePerMille / 1000 / 1000;

    private void RepairFromCommand(Transaction tx, Transaction tick0, SubscriptionsCommands subs, in ClientCommand<RepairItem> command, long worldTick)
    {
        if (!CraftActor(tick0, command.Session, out var owner, out var body, out var realm, out var x, out var z))
        {
            RefuseCraft(subs, in command, TatooineReplication.NotInWorld);
            return;
        }

        if (!StationNear(realm, x, z))
        {
            RefuseCraft(subs, in command, TatooineReplication.CraftNotAtStation);
            return;
        }

        var item = EntityId.FromRawValue(command.Value.Item);
        var items = ItemsOf(owner);
        var (template, mk, worn) = HeldOrWorn(items, item);
        var kit = KitOf(template);
        if (template == 0 || kit == 0 || !TryStatsOf(tx, item, out var stats))
        {
            RefuseCraft(subs, in command, TatooineReplication.RepairBadItem);
            return;
        }

        if (stats.Repairs >= MaxRepairs)
        {
            RefuseCraft(subs, in command, TatooineReplication.RepairSpent);
            return;
        }

        // A repair restores to max × 0.75: one that would lower the condition — the item is not worn down that far — is refused, never paid for.
        if ((stats.Flags & ItemFlags.Broken) == 0 && stats.Condition >= (long)stats.MaxCondition * RepairMaxPerMille / 1000)
        {
            RefuseCraft(subs, in command, TatooineReplication.RepairNotWorn);
            return;
        }

        if (UnitsOf(items, kit, 0) < 1)
        {
            RefuseCraft(subs, in command, TatooineReplication.RepairNoKit);
            return;
        }

        var fee = RepairFee(template, mk);
        if (fee > 0 && BalanceOf(tx, owner) < fee)
        {
            RefuseCraft(subs, in command, TatooineReplication.CraftNotEnough);
            return;
        }

        // Validated: the kit consumed, the fee paid, the item restored to a lower max — one commit.
        var reference = (long)item.RawValue;
        if (ItemTransfer(tx, owner, ItemLedger.World, kit, 0, 1, ItemMoveKind.Repair, reference, worldTick) != 1
            || (fee > 0 && !Transfer(tx, owner, EconomyLedger.World, fee, TransferKind.Repair, reference, worldTick)))
        {
            _craftAbort = true;
            _repairsApplied.Add(command);
            return;
        }

        stats.MaxCondition = (int)((long)stats.MaxCondition * RepairMaxPerMille / 1000);
        stats.Condition = stats.MaxCondition;
        stats.Repairs++;
        stats.Flags &= unchecked((byte)~ItemFlags.Broken);
        tx.OpenMut(item).Set(ItemInstanceArch.Stats, stats);
        _wearCounts.Repairs++;
        _wearCounts.Fees += fee;
        if (worn)
        {
            TatooineReplication.TryBodyOfSession(command.Session, out _, out var character);
            _gearScratch.Add((character, body));
        }

        _craftBodies.Add(body);
        if (fee > 0)
        {
            _craftWalletsMoved.Add(body);
        }

        _repairsApplied.Add(command);
    }

    // The template and Mk of an instance in the grid or worn, and whether worn; (0, 0, false) when the character has no such item.
    private static (ushort Template, byte Mk, bool Worn) HeldOrWorn(OwnerItems items, EntityId item)
    {
        var s = SlotOf(items, item);
        if (s != 0 && items.Slots[s].Instance)
        {
            return (items.Slots[s].Template, items.Slots[s].Mk, false);
        }

        for (var k = 0; k < EquipSlot.Count; k++)
        {
            if (items.Equipped[k].Count > 0 && items.Equipped[k].Stack == item)
            {
                return (items.Equipped[k].Template, items.Equipped[k].Mk, true);
            }
        }

        return (0, 0, false);
    }

    // ── Salvage ───────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What salvaging a crafted item returns (Q10): <see cref="SalvagePerMille"/> of the quality materials its schematic takes, its parts' included, at
    /// the tier of its core (Mk I for a part, which has none). Empty for anything not made by a schematic.
    /// </summary>
    internal static List<(ushort Template, byte Mk, int Count)> SalvageOf(ushort template, byte mk)
    {
        var counts = new Dictionary<ushort, int>();
        var id = template is >= ItemCatalog.CompositeHelmet and < ItemCatalog.CompositeHelmet + 6
            ? (ushort)(template - ItemCatalog.CompositeHelmet + ItemCatalog.PaddedHelmet)
            : template;
        Collect(id, counts, 0);
        var tier = (byte)Math.Max(1, (int)mk);
        var result = new List<(ushort, byte, int)>();
        foreach (var (material, count) in counts)
        {
            var n = count * SalvagePerMille / 1000;
            if (n > 0)
            {
                result.Add((material, tier, n));
            }
        }

        result.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return result;

        static void Collect(ushort id, Dictionary<ushort, int> counts, int depth)
        {
            if (depth > 3 || !Schematics.TryGet(id, out var s))
            {
                return;
            }

            foreach (var slot in s.Slots)
            {
                if (slot.Kind == SlotKind.Quality)
                {
                    counts[slot.Template] = (counts.TryGetValue(slot.Template, out var c) ? c : 0) + slot.Count;
                }
                else if (slot.Kind == SlotKind.Part)
                {
                    for (var n = 0; n < slot.Count; n++)
                    {
                        Collect(slot.Template == 0 ? Schematics.PaddedSegment : slot.Template, counts, depth + 1);
                    }
                }
            }
        }
    }

    // Whether the materials fit: the room left in the stacks of each, then whole new slots — the grid's free ones and the one the item leaves — shared.
    private static bool RoomForSalvage(OwnerItems items, List<(ushort Template, byte Mk, int Count)> back)
    {
        var free = 1;
        for (var s = 1; s <= ItemLedger.Slots; s++)
        {
            free += items.Slots[s].Count == 0 ? 1 : 0;
        }

        foreach (var (material, tier, count) in back)
        {
            var have = 0;
            for (var s = 1; s <= ItemLedger.Slots; s++)
            {
                var slot = items.Slots[s];
                have += slot.Count > 0 && !slot.Instance && slot.Template == material && slot.Mk == tier ? ItemLedger.StackMax - slot.Count : 0;
            }

            if (have < count)
            {
                free -= (count - have + ItemLedger.StackMax - 1) / ItemLedger.StackMax;
            }
        }

        return free >= 0;
    }

    private void SalvageFromCommand(Transaction tx, Transaction tick0, SubscriptionsCommands subs, in ClientCommand<SalvageItem> command, long worldTick)
    {
        if (!CraftActor(tick0, command.Session, out var owner, out var body, out var realm, out var x, out var z))
        {
            RefuseCraft(subs, in command, TatooineReplication.NotInWorld);
            return;
        }

        if (!StationNear(realm, x, z))
        {
            RefuseCraft(subs, in command, TatooineReplication.CraftNotAtStation);
            return;
        }

        // From the grid only: what is worn is taken off first. And only what a crafter made: loot and standard issue have no recipe to give back.
        var item = EntityId.FromRawValue(command.Value.Item);
        var items = ItemsOf(owner);
        var s = SlotOf(items, item);
        if (s == 0 || !items.Slots[s].Instance || !TryStatsOf(tx, item, out var stats) || stats.Crafter == 0)
        {
            RefuseCraft(subs, in command, TatooineReplication.SalvageBadItem);
            return;
        }

        var (template, mk) = (items.Slots[s].Template, items.Slots[s].Mk);
        var back = SalvageOf(template, mk);
        if (!RoomForSalvage(items, back))
        {
            RefuseCraft(subs, in command, TatooineReplication.CraftNoRoom);
            return;
        }

        var reference = (long)item.RawValue;
        if (ItemTransfer(tx, owner, ItemLedger.World, template, mk, 1, ItemMoveKind.Salvage, reference, worldTick, item) != 1)
        {
            _craftAbort = true;
            _salvagesApplied.Add(command);
            return;
        }

        foreach (var (material, tier, count) in back)
        {
            if (ItemTransfer(tx, ItemLedger.World, owner, material, tier, count, ItemMoveKind.Salvage, reference, worldTick) != count)
            {
                _craftAbort = true;
                break;
            }
        }

        _wearCounts.Salvages++;
        _craftBodies.Add(body);
        _salvagesApplied.Add(command);
    }
}
