using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace SwgTatooine.Server;

/// <summary>
/// Items (inventory-loot.md § 3): stacks per character, a row of the item ledger per stack touched, and the one code path that writes them.
/// </summary>
/// <remarks>
/// <para>
/// <b>One code path, the credit ledger's shape</b> (<see cref="SimBridge.Transfer"/>). Every movement of items — a kill's drop, a wild lair's cache, a mission
/// lair's cache — goes through <see cref="ItemTransfer"/>: the stacks, the rows and the head are written in the transaction that carries it and commit together
/// or not at all. Two serial writers call it, ordered by an edge: the mission desk mints a mission lair's cache in the transaction that pays its reward and
/// ends it; the <c>Loot</c> system mints kill drops and wild caches in its own.
/// </para>
/// <para>
/// <b>In memory: every character's 30 slots, the head and the units held</b>, read whole on the first use, so a reopened world carries on. Never re-queried
/// inside a transaction (a query does not see its own updates). A transaction holding item writes that fails to commit forgets all of it, and the next use
/// reads it again; its counts are dropped with it.
/// </para>
/// <para>
/// <b>An inventory exists only for a character with a wallet</b> (<c>_walletOf</c>): the wallet opens the tick after the bind, once the character is durable,
/// and keying items on the same condition closes the same crash hole.
/// </para>
/// </remarks>
public sealed partial class SimBridge
{
    // One slot of a character's grid, as the transaction under way left it.
    private struct ItemSlot
    {
        public EntityId Stack;
        public ushort Template;
        public byte Mk;
        public int Count;
    }

    // A character's grid: Slots[1..30]; Slots[0] unused, so a slot number indexes it.
    private sealed class OwnerItems
    {
        public readonly ItemSlot[] Slots = new ItemSlot[ItemLedger.Slots + 1];
    }

    // Per (template, Mk): the counters' index.
    private const int MkSpan = 6;
    private static readonly int ItemKeys = (ItemCatalog.Templates.Length + 1) * MkSpan;

    private bool _itemsLoaded;
    private bool _itemsEverLoaded;
    private bool _itemsDirty;
    private EntityId _itemHeadId;
    private ItemLedgerHead _itemHead;
    private IndexRef _stackByOwner;
    private IndexRef _itemHeadByKey;
    private readonly Dictionary<long, OwnerItems> _itemsOf = [];

    // Units held by (template, Mk) across the world — loaded with the stacks, then moved by each commit — and since start: minted, destroyed, lost.
    private readonly long[] _itemsHeld = new long[ItemKeys];
    private readonly long[] _itemsMinted = new long[ItemKeys];
    private readonly long[] _itemsDestroyed = new long[ItemKeys];
    private readonly long[] _itemsLostBy = new long[ItemKeys];

    // The transaction under way's, published only when it commits.
    private readonly long[] _pendingHeld = new long[ItemKeys];
    private readonly long[] _pendingMinted = new long[ItemKeys];
    private readonly long[] _pendingDestroyed = new long[ItemKeys];
    private readonly long[] _pendingLostBy = new long[ItemKeys];
    private readonly List<(EntityId Character, ushort Template, byte Mk, int Count, long Tick)> _pendingLostNotes = [];
    private readonly long[] _pendingDropsBy = new long[4];
    private long _pendingItemRows;
    private long _pendingDrops;
    private long _pendingRolled;
    private bool _itemHeadDirty;
    private long _pendingUnits;
    private long _pendingLost;
    private int _pendingStacks;

    private long _itemRows;
    private long _itemDrops;
    private long _itemsRolled;
    private long _itemUnits;
    private long _itemsLost;
    private long _itemStacks;
    private long _itemCommitsFailed;
    private long _itemsRefused;
    private readonly long[] _itemDropsBy = new long[4];

    // The desk's bodies whose inventory moved, told once its transaction commits.
    private readonly HashSet<EntityId> _deskItemsMoved = [];

    /// <summary>Item ledger rows written since start.</summary>
    public long ItemRows => Interlocked.Read(ref _itemRows);

    /// <summary>Drops minted into an inventory since start — kills, wild caches, mission caches — whole or in part.</summary>
    public long ItemDrops => Interlocked.Read(ref _itemDrops);

    /// <summary>Drops rolled for a character since start, those lost whole to a full inventory included.</summary>
    public long ItemsRolled => Interlocked.Read(ref _itemsRolled);

    /// <summary>Units minted since start.</summary>
    public long ItemUnits => Interlocked.Read(ref _itemUnits);

    /// <summary>Units lost to a full inventory since start: never minted.</summary>
    public long ItemsLost => Interlocked.Read(ref _itemsLost);

    /// <summary>Stacks in the world now.</summary>
    public long ItemStacks => Interlocked.Read(ref _itemStacks);

    /// <summary>Transactions holding item writes that did not commit: every row in them was lost together, and the state was read again.</summary>
    public long ItemCommitsFailed => Interlocked.Read(ref _itemCommitsFailed);

    /// <summary>Transfers <see cref="ItemTransfer"/> refused before writing anything.</summary>
    public long ItemsRefused => Interlocked.Read(ref _itemsRefused);

    /// <summary>Drops by source since start: <see cref="DropSource"/> indexes it.</summary>
    public long ItemDropsBy(DropSource source) => Interlocked.Read(ref _itemDropsBy[(int)source]);

    /// <summary>Units of (<paramref name="template"/>, <paramref name="mk"/>) held now, across the world.</summary>
    public long ItemsHeld(ushort template, byte mk) => Interlocked.Read(ref _itemsHeld[KeyOf(template, mk)]);

    /// <summary>Units of (<paramref name="template"/>, <paramref name="mk"/>) minted since start.</summary>
    public long ItemsMinted(ushort template, byte mk) => Interlocked.Read(ref _itemsMinted[KeyOf(template, mk)]);

    /// <summary>Units of (<paramref name="template"/>, <paramref name="mk"/>) lost since start.</summary>
    public long ItemsLostOf(ushort template, byte mk) => Interlocked.Read(ref _itemsLostBy[KeyOf(template, mk)]);

    private static int KeyOf(ushort template, byte mk) => (template * MkSpan) + Math.Min(mk, (byte)5);

    /// <summary>
    /// A mission lair's cache, minted into its holder's inventory in the desk's own transaction — the one that pays the reward and ends the mission — so the
    /// three commit together (inventory-loot.md § 3.4). What does not fit is lost and said so; the mission ends anyway.
    /// </summary>
    /// <returns><see langword="false"/>, having minted nothing, when the holder has no wallet.</returns>
    private bool MintCache(Transaction tx, EntityId character, EntityId body, EntityId mission, in HeldMission held, long worldTick)
    {
        var key = (long)character.RawValue;
        if (!HasWallet(tx, key))
        {
            return false;
        }

        // The mission's own level and lair kind: every lair it draws is spawned at that level, because the mission promised it.
        var drop = LootTables.Roll(held.Kind, held.Level, DropSource.MissionCache, mission.RawValue, worldTick);
        PlaceDrop(tx, character, in drop, ItemMoveKind.Mission, DropSource.MissionCache, (long)mission.RawValue, worldTick);
        _deskItemsMoved.Add(body);
        return true;
    }

    /// <summary>
    /// Places a drop line by line, the core or add-on first, each through <see cref="ItemTransfer"/>; what does not fit is lost — never minted, so no row —
    /// counted and noted for its character.
    /// </summary>
    /// <returns>The units placed.</returns>
    private int PlaceDrop(Transaction tx, EntityId character, in Drop drop, byte kind, DropSource source, long reference, long worldTick)
    {
        var placed = 0;
        for (var i = 0; i < drop.Lines; i++)
        {
            var line = drop[i];
            var n = ItemTransfer(tx, ItemLedger.World, (long)character.RawValue, line.Template, line.Mk, line.Count, kind, reference, worldTick);
            placed += n;
            if (n < line.Count)
            {
                var lost = line.Count - n;
                _pendingLostBy[KeyOf(line.Template, line.Mk)] += lost;
                _pendingLost += lost;
                _pendingLostNotes.Add((character, line.Template, line.Mk, lost, worldTick));
                _itemsDirty = true;
            }
        }

        // A drop counts when something of it was placed; one lost whole is only rolled (and its loss counted above).
        _pendingRolled++;
        if (placed > 0)
        {
            _pendingDrops++;
            _pendingDropsBy[(int)source]++;
        }

        _itemsDirty = true;
        return placed;
    }

    /// <summary>The desk's transaction ended: its caches counted and their bodies told, or — not committed — the items read again.</summary>
    private void EndDeskItems(Transaction tick0, bool committed)
    {
        if (EndItems(committed))
        {
            foreach (var body in _deskItemsMoved)
            {
                BumpItems(tick0, body);
            }
        }

        _deskItemsMoved.Clear();
    }

    /// <summary>Reads every stack and the head on the first use after a start or a failed commit.</summary>
    private void EnsureItems(Transaction tx)
    {
        if (!_itemsLoaded)
        {
            LoadItems(tx);
            _itemsLoaded = true;
        }
    }

    /// <summary>Every stack by its owner, and the head — created on a world's first use; the units held recounted from the stacks.</summary>
    private void LoadItems(Transaction tx)
    {
        // The losses remembered for the clients are this process's: forgotten on the first load only, as the journal's completions are.
        if (!_itemsEverLoaded)
        {
            ItemNotes.Reset();
            _itemsEverLoaded = true;
        }

        _stackByOwner = Dbe.GetIndexRef<ItemStack, long>(s => s.Owner);
        _itemHeadByKey = Dbe.GetIndexRef<ItemLedgerHead, int>(h => h.Key);
        _itemsOf.Clear();
        var held = new long[ItemKeys];
        var stacks = 0L;
        using (var found = tx.EnumerateIndex<ItemStack, long>(_stackByOwner, long.MinValue, long.MaxValue))
        {
            while (found.MoveNext())
            {
                var s = found.CurrentComponent;
                if (s.Slot is < 1 or > ItemLedger.Slots)
                {
                    // Outside the grid: an item a later feature keeps elsewhere (equipped). Nothing here moves it.
                    continue;
                }

                var items = ItemsOf(s.Owner);
                items.Slots[s.Slot] = new ItemSlot
                {
                    Stack = EntityId.FromRawValue((ulong)found.CurrentEntityPK),
                    Template = s.Template,
                    Mk = s.Mk,
                    Count = s.Count,
                };
                held[KeyOf(s.Template, s.Mk)] += s.Count;
                stacks++;
            }
        }

        for (var k = 0; k < ItemKeys; k++)
        {
            Interlocked.Exchange(ref _itemsHeld[k], held[k]);
        }

        Interlocked.Exchange(ref _itemStacks, stacks);
        using (var heads = tx.EnumerateIndex<ItemLedgerHead, int>(_itemHeadByKey, ItemLedger.HeadKey, ItemLedger.HeadKey))
        {
            if (heads.MoveNext())
            {
                _itemHeadId = EntityId.FromRawValue((ulong)heads.CurrentEntityPK);
                _itemHead = heads.CurrentComponent;
                return;
            }
        }

        _itemHead = new ItemLedgerHead { Key = ItemLedger.HeadKey };
        _itemHeadId = tx.Spawn<ItemLedgerHeadArch>(ItemLedgerHeadArch.Head.Set(in _itemHead));
        _itemsDirty = true;
    }

    private OwnerItems ItemsOf(long owner)
    {
        if (!_itemsOf.TryGetValue(owner, out var items))
        {
            items = new OwnerItems();
            _itemsOf[owner] = items;
        }

        return items;
    }

    /// <summary>
    /// A transaction that may hold item writes ended. Committed, its counts are published and its losses noted; not, and it held any, everything this code
    /// remembers is read again on the next use.
    /// </summary>
    /// <returns>Whether it committed.</returns>
    private bool EndItems(bool committed)
    {
        if (_itemsDirty)
        {
            if (committed)
            {
                for (var k = 0; k < ItemKeys; k++)
                {
                    if (_pendingHeld[k] != 0)
                    {
                        Interlocked.Add(ref _itemsHeld[k], _pendingHeld[k]);
                    }

                    if (_pendingMinted[k] != 0)
                    {
                        Interlocked.Add(ref _itemsMinted[k], _pendingMinted[k]);
                    }

                    if (_pendingDestroyed[k] != 0)
                    {
                        Interlocked.Add(ref _itemsDestroyed[k], _pendingDestroyed[k]);
                    }

                    if (_pendingLostBy[k] != 0)
                    {
                        Interlocked.Add(ref _itemsLostBy[k], _pendingLostBy[k]);
                    }
                }

                for (var s = 0; s < _pendingDropsBy.Length; s++)
                {
                    Interlocked.Add(ref _itemDropsBy[s], _pendingDropsBy[s]);
                }

                Interlocked.Add(ref _itemRows, _pendingItemRows);
                Interlocked.Add(ref _itemDrops, _pendingDrops);
                Interlocked.Add(ref _itemsRolled, _pendingRolled);
                Interlocked.Add(ref _itemUnits, _pendingUnits);
                Interlocked.Add(ref _itemsLost, _pendingLost);
                Interlocked.Add(ref _itemStacks, _pendingStacks);
                foreach (var (character, template, mk, count, tick) in _pendingLostNotes)
                {
                    ItemNotes.NoteLost(character, template, mk, count, tick);
                }
            }
            else
            {
                Interlocked.Increment(ref _itemCommitsFailed);
                _itemsLoaded = false;
                _itemsOf.Clear();
            }
        }

        _itemsDirty = false;
        Array.Clear(_pendingHeld);
        Array.Clear(_pendingMinted);
        Array.Clear(_pendingDestroyed);
        Array.Clear(_pendingLostBy);
        Array.Clear(_pendingDropsBy);
        _pendingLostNotes.Clear();
        _pendingItemRows = 0;
        _pendingDrops = 0;
        _pendingRolled = 0;
        _itemHeadDirty = false;
        _pendingUnits = 0;
        _pendingLost = 0;
        _pendingStacks = 0;
        return committed;
    }

    /// <summary>
    /// Moves <paramref name="count"/> units of (<paramref name="template"/>, <paramref name="mk"/>) from one side to the other and appends one row per stack
    /// touched: the one place a stack changes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// World → character mints: it fills the character's stacks of that key up to <see cref="ItemLedger.StackMax"/>, lowest slot first, then opens the lowest
    /// free slot, and places what fits. Character → World destroys, highest slot first, all of it or nothing. Character → character moves, all of it or
    /// nothing.
    /// </para>
    /// <para><b>Never throws</b>: a transfer the validation refuses — a bad count or side, an unknown template, a Mk its rule refuses, a side with no wallet, a
    /// debit it does not cover — writes nothing, is counted, and returns 0.</para>
    /// </remarks>
    /// <returns>The units placed; what did not fit is the caller's to report.</returns>
    private int ItemTransfer(Transaction tx, long from, long to, ushort template, byte mk, int count, byte kind, long reference, long worldTick)
    {
        if (count <= 0 || from == to || !ItemCatalog.TryGet(template, out var t) || !ItemCatalog.MkFits(in t, mk)
            || (from != ItemLedger.World && !HasWallet(tx, from)) || (to != ItemLedger.World && !HasWallet(tx, to)))
        {
            Interlocked.Increment(ref _itemsRefused);
            return 0;
        }

        EnsureItems(tx);
        if (from != ItemLedger.World)
        {
            var source = ItemsOf(from);
            if (UnitsOf(source, template, mk) < count || (to != ItemLedger.World && RoomFor(ItemsOf(to), template, mk) < count))
            {
                Interlocked.Increment(ref _itemsRefused);
                return 0;
            }
        }

        var remaining = count;
        if (from == ItemLedger.World)
        {
            var target = ItemsOf(to);
            while (remaining > 0)
            {
                var slot = SlotToFill(target, template, mk);
                if (slot == 0)
                {
                    break;
                }

                var add = Math.Min(remaining, ItemLedger.StackMax - target.Slots[slot].Count);
                var toAfter = Credit(tx, to, target, slot, template, mk, add, out var toStack);
                AppendItemRow(tx, from, to, 0, toStack, 0, toAfter, add, template, mk, kind, reference, worldTick);
                remaining -= add;
            }

            var placed = count - remaining;
            _itemHead.Minted += placed;
            _pendingMinted[KeyOf(template, mk)] += placed;
            _pendingUnits += placed;
            _itemHeadDirty |= placed > 0;
            return placed;
        }

        var giver = ItemsOf(from);
        var taker = to == ItemLedger.World ? null : ItemsOf(to);
        while (remaining > 0)
        {
            var src = SlotToTake(giver, template, mk);
            var take = Math.Min(remaining, giver.Slots[src].Count);
            if (taker != null)
            {
                var dst = SlotToFill(taker, template, mk);
                take = Math.Min(take, ItemLedger.StackMax - taker.Slots[dst].Count);
                var fromAfter = Debit(tx, giver, src, take, out var fromStack);
                var toAfter = Credit(tx, to, taker, dst, template, mk, take, out var toStack);
                AppendItemRow(tx, from, to, fromStack, toStack, fromAfter, toAfter, take, template, mk, kind, reference, worldTick);
            }
            else
            {
                var fromAfter = Debit(tx, giver, src, take, out var fromStack);
                AppendItemRow(tx, from, to, fromStack, 0, fromAfter, 0, take, template, mk, kind, reference, worldTick);
                _itemHead.Destroyed += take;
                _pendingDestroyed[KeyOf(template, mk)] += take;
            }

            remaining -= take;
        }

        _itemHeadDirty = true;
        return count;
    }

    private static int UnitsOf(OwnerItems items, ushort template, byte mk)
    {
        var n = 0;
        for (var s = 1; s <= ItemLedger.Slots; s++)
        {
            ref readonly var slot = ref items.Slots[s];
            if (slot.Count > 0 && slot.Template == template && slot.Mk == mk)
            {
                n += slot.Count;
            }
        }

        return n;
    }

    private static int RoomFor(OwnerItems items, ushort template, byte mk)
    {
        var n = 0;
        for (var s = 1; s <= ItemLedger.Slots; s++)
        {
            ref readonly var slot = ref items.Slots[s];
            if (slot.Count == 0)
            {
                n += ItemLedger.StackMax;
            }
            else if (slot.Template == template && slot.Mk == mk)
            {
                n += ItemLedger.StackMax - slot.Count;
            }
        }

        return n;
    }

    /// <summary>The lowest slot holding the key with room, else the lowest free slot, else 0.</summary>
    private static int SlotToFill(OwnerItems items, ushort template, byte mk)
    {
        for (var s = 1; s <= ItemLedger.Slots; s++)
        {
            ref readonly var slot = ref items.Slots[s];
            if (slot.Count > 0 && slot.Count < ItemLedger.StackMax && slot.Template == template && slot.Mk == mk)
            {
                return s;
            }
        }

        for (var s = 1; s <= ItemLedger.Slots; s++)
        {
            if (items.Slots[s].Count == 0)
            {
                return s;
            }
        }

        return 0;
    }

    /// <summary>The highest slot holding the key. The caller checked the units are there.</summary>
    private static int SlotToTake(OwnerItems items, ushort template, byte mk)
    {
        for (var s = ItemLedger.Slots; s >= 1; s--)
        {
            ref readonly var slot = ref items.Slots[s];
            if (slot.Count > 0 && slot.Template == template && slot.Mk == mk)
            {
                return s;
            }
        }

        return 0;
    }

    /// <summary>Adds units to a slot's stack, spawning it when the slot is free.</summary>
    /// <returns>The stack's count after.</returns>
    private int Credit(Transaction tx, long owner, OwnerItems items, int s, ushort template, byte mk, int add, out long stackKey)
    {
        ref var slot = ref items.Slots[s];
        var seq = _itemHead.LastSeq + 1;
        if (slot.Count == 0)
        {
            var stack = new ItemStack { Owner = owner, Template = template, Mk = mk, Slot = (byte)s, Count = add, LastSeq = seq, Rows = 1 };
            slot = new ItemSlot { Stack = tx.Spawn<ItemStackArch>(ItemStackArch.Stack.Set(in stack)), Template = template, Mk = mk, Count = add };
            _pendingStacks++;
        }
        else
        {
            var e = tx.OpenMut(slot.Stack);
            var stack = e.Read(ItemStackArch.Stack);
            stack.Count += add;
            stack.LastSeq = seq;
            stack.Rows++;
            e.Set(ItemStackArch.Stack, stack);
            slot.Count = stack.Count;
        }

        _pendingHeld[KeyOf(template, mk)] += add;
        stackKey = (long)slot.Stack.RawValue;
        return slot.Count;
    }

    /// <summary>Takes units from a slot's stack, destroying it when emptied.</summary>
    /// <returns>The stack's count after: 0 for one destroyed.</returns>
    private int Debit(Transaction tx, OwnerItems items, int s, int take, out long stackKey)
    {
        ref var slot = ref items.Slots[s];
        stackKey = (long)slot.Stack.RawValue;
        _pendingHeld[KeyOf(slot.Template, slot.Mk)] -= take;
        if (take >= slot.Count)
        {
            tx.Destroy(slot.Stack);
            slot = default;
            _pendingStacks--;
            return 0;
        }

        var e = tx.OpenMut(slot.Stack);
        var stack = e.Read(ItemStackArch.Stack);
        stack.Count -= take;
        stack.LastSeq = _itemHead.LastSeq + 1;
        stack.Rows++;
        e.Set(ItemStackArch.Stack, stack);
        slot.Count = stack.Count;
        return slot.Count;
    }

    private void AppendItemRow(Transaction tx, long from, long to, long fromStack, long toStack, int fromAfter, int toAfter, int count, ushort template, byte mk,
        byte kind, long reference, long worldTick)
    {
        _itemHead.LastSeq++;
        var row = new ItemLedgerEntry
        {
            Seq = _itemHead.LastSeq,
            WorldTick = worldTick,
            From = from,
            To = to,
            FromStack = fromStack,
            ToStack = toStack,
            FromAfter = fromAfter,
            ToAfter = toAfter,
            Count = count,
            Ref = reference,
            Template = template,
            Mk = mk,
            Kind = kind,
        };
        tx.Spawn<ItemLedgerArch>(ItemLedgerArch.Entry.Set(in row));
        _pendingItemRows++;
        _itemsDirty = true;
    }

    /// <summary>Writes the head once, before the transaction that moved it commits: every row of a drop, or of a tick, moves it in memory only.</summary>
    private void FlushItemHead(Transaction tx)
    {
        if (!_itemHeadDirty)
        {
            return;
        }

        tx.OpenMut(_itemHeadId).Set(ItemLedgerHeadArch.Head, _itemHead);
        _itemHeadDirty = false;
        _itemsDirty = true;
    }

    /// <summary>
    /// Whether a character has a wallet: from memory while the economy is loaded; from the wallet index when a failed commit of the <c>Ledger</c> or the desk
    /// dropped it, so this tick's drops are not refused for a wallet that exists.
    /// </summary>
    private bool HasWallet(Transaction tx, long key)
    {
        if (_economyLoaded)
        {
            return _walletOf.ContainsKey(key);
        }

        using var found = tx.EnumerateIndex<Wallet, long>(Dbe.GetIndexRef<Wallet, long>(w => w.Owner), key, key);
        return found.MoveNext();
    }

    /// <summary>Moves the body's items revision, so its client re-reads <c>GET /swg/inventory</c>.</summary>
    private static void BumpItems(Transaction tick0, EntityId body)
    {
        if (!tick0.TryOpenMut(body, out var player))
        {
            return;
        }

        var state = player.Read(Player.State);
        state.ItemsRev++;
        player.Set(Player.State, state);
        TatooineReplication.Replicate(in player);
    }

    /// <summary>
    /// The item counters as the JSON object <c>/swg/stats</c> carries under <c>items</c> (inventory-loot.md § 8), the rates given by the caller.
    /// </summary>
    internal void AppendItemsJson(StringBuilder j, string rates)
    {
        j.Append("{\"rows\":").Append(ItemRows)
            .Append(",\"drops\":").Append(ItemDrops)
            .Append(",\"rolled\":").Append(ItemsRolled)
            .Append(",\"units\":").Append(ItemUnits)
            .Append(",\"lost\":").Append(ItemsLost)
            .Append(",\"stacks\":").Append(ItemStacks)
            .Append(",\"commitsFailed\":").Append(ItemCommitsFailed)
            .Append(",\"refused\":").Append(ItemsRefused + LootRefusedTotal)
            .Append(",\"refusedBy\":{\"crowd\":").Append(Interlocked.Read(ref _lootRefusedCrowd))
            .Append(",\"noWallet\":").Append(Interlocked.Read(ref _lootRefusedWallet))
            .Append(",\"realm\":").Append(Interlocked.Read(ref _lootRefusedRealm))
            .Append(",\"reach\":").Append(Interlocked.Read(ref _lootRefusedReach))
            .Append(",\"transfer\":").Append(ItemsRefused)
            .Append("},\"queueOverflow\":").Append(LootQueueOverflow)
            .Append(",\"bySource\":{\"kill\":").Append(ItemDropsBy(DropSource.Kill))
            .Append(",\"lair\":").Append(ItemDropsBy(DropSource.WildCache))
            .Append(",\"mission\":").Append(ItemDropsBy(DropSource.MissionCache)).Append('}');
        ByTemplate(j, "minted", _itemsMinted);
        ByTemplate(j, "held", _itemsHeld);
        ByTemplate(j, "destroyed", _itemsDestroyed);
        ByTemplate(j, "lostBy", _itemsLostBy);
        j.Append(rates).Append('}');
    }

    // {"mat_hide":[i,ii,iii,iv,v],…}: only the templates with a non-zero count.
    private static void ByTemplate(StringBuilder j, string name, long[] counts)
    {
        j.Append(",\"").Append(name).Append("\":{");
        var first = true;
        foreach (var t in ItemCatalog.Templates)
        {
            var any = false;
            for (var mk = 0; mk < MkSpan; mk++)
            {
                any |= Interlocked.Read(ref counts[(t.Id * MkSpan) + mk]) != 0;
            }

            if (!any)
            {
                continue;
            }

            j.Append(first ? "\"" : ",\"").Append(t.Asset).Append("\":[");
            first = false;
            if (t.HasMk)
            {
                for (var mk = 1; mk <= 5; mk++)
                {
                    j.Append(mk == 1 ? "" : ",").Append(Interlocked.Read(ref counts[(t.Id * MkSpan) + mk]));
                }
            }
            else
            {
                j.Append(Interlocked.Read(ref counts[t.Id * MkSpan]));
            }

            j.Append(']');
        }

        j.Append('}');
    }

    /// <summary>The item counters for the exit line: rows, drops, units, lost, stacks, failed commits, refused.</summary>
    public string ItemCountersJson()
        => FormattableString.Invariant(
            $"{{\"rows\":{ItemRows},\"drops\":{ItemDrops},\"units\":{ItemUnits},\"lost\":{ItemsLost},\"stacks\":{ItemStacks},\"commitsFailed\":{ItemCommitsFailed},\"refused\":{ItemsRefused + LootRefusedTotal}}}");
}
