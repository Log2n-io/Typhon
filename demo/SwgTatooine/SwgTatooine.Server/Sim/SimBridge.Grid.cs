using System;
using System.Collections.Generic;
using System.Threading;

namespace SwgTatooine.Server;

/// <summary>
/// The inventory window's own commands (inventory-loot.md § 10), in the Workshop's transaction, anywhere: a stack moved to another slot — split, merged or
/// swapped — a stack or an item thrown away, and the whole grid stacked and sorted.
/// </summary>
/// <remarks>
/// <b>Units moved between two stacks are a row; a slot change is not.</b> A split and a merge change two stacks' counts, so the audit replays them
/// (<see cref="ItemMoveKind.Move"/>, both sides the owner); a whole stack or an item moved to a free slot, or two swapped, changes only where they are drawn —
/// as an equip does, without a row. A discard is a destroy to World (<see cref="ItemMoveKind.Discard"/>).
/// </remarks>
public sealed partial class SimBridge
{
    private readonly List<ClientCommand<MoveItem>> _movesApplied = [];
    private readonly List<ClientCommand<DiscardItem>> _discardsApplied = [];
    private readonly List<ClientCommand<SortItems>> _sortsApplied = [];

    // A sort's grid: its occupied slots, in the order it puts them (reused).
    private readonly List<(int Slot, ItemSlot Item)> _sortScratch = [];

    // The transaction under way's, published when it commits: [0] re-slotted, [1] split, [2] merged, [3] swapped, [4] discarded, [5] sorted.
    private readonly long[] _pendingGrid = new long[6];
    private readonly long[] _grid = new long[6];

    /// <summary>Stacks or items moved whole to a free slot since start.</summary>
    public long GridMoves => Interlocked.Read(ref _grid[0]);

    /// <summary>Stacks split since start.</summary>
    public long GridSplits => Interlocked.Read(ref _grid[1]);

    /// <summary>Stacks merged into another since start, whole or in part.</summary>
    public long GridMerges => Interlocked.Read(ref _grid[2]);

    /// <summary>Two slots swapped since start.</summary>
    public long GridSwaps => Interlocked.Read(ref _grid[3]);

    /// <summary>Stacks or items thrown away since start, whole or in part.</summary>
    public long GridDiscards => Interlocked.Read(ref _grid[4]);

    /// <summary>Grids stacked and sorted since start.</summary>
    public long GridSorts => Interlocked.Read(ref _grid[5]);

    private void MoveFromCommand(Transaction tx, Transaction tick0, SubscriptionsCommands subs, in ClientCommand<MoveItem> command, long worldTick)
    {
        if (!CraftActor(tick0, command.Session, out var owner, out var body, out _, out _, out _))
        {
            RefuseWorkshop(subs, in command, TatooineReplication.NotInWorld);
            return;
        }

        var items = ItemsOf(owner);
        var src = SlotOf(items, EntityId.FromRawValue(command.Value.Item));
        var dst = command.Value.Slot == 0 ? FreeSlot(items) : command.Value.Slot;
        var n = command.Value.Count;
        if (src == 0 || dst is < 1 or > ItemLedger.Slots || dst == src || n < 1 || n > (uint)items.Slots[src].Count)
        {
            RefuseWorkshop(subs, in command, TatooineReplication.MoveBad);
            return;
        }

        ref var from = ref items.Slots[src];
        ref var to = ref items.Slots[dst];
        var whole = n == (uint)from.Count;
        var units = (int)n;
        if (to.Count == 0 && whole)
        {
            // Moved whole: where it is drawn, nothing more.
            SetSlot(tx, from.Stack, (byte)dst);
            to = from;
            from = default;
            _pendingGrid[0]++;
        }
        else if (to.Count == 0 && !from.Instance)
        {
            // A split: a new stack in the free slot.
            var (template, mk) = (from.Template, from.Mk);
            var fromAfter = Debit(tx, items, src, units, out var fromStack);
            var toAfter = Credit(tx, owner, items, dst, template, mk, units, out var toStack);
            AppendItemRow(tx, owner, owner, fromStack, toStack, fromAfter, toAfter, units, template, mk, ItemMoveKind.Move, 0, worldTick);
            _itemHeadDirty = true;
            _pendingGrid[1]++;
        }
        else if (to.Count > 0 && !from.Instance && !to.Instance && to.Template == from.Template && to.Mk == from.Mk)
        {
            // A merge, as much as the stack it lands on holds: the rest stays where it was.
            var add = Math.Min(units, ItemLedger.StackMax - to.Count);
            if (add == 0)
            {
                RefuseWorkshop(subs, in command, TatooineReplication.MoveBad);
                return;
            }

            var (template, mk) = (from.Template, from.Mk);
            var fromAfter = Debit(tx, items, src, add, out var fromStack);
            var toAfter = Credit(tx, owner, items, dst, template, mk, add, out var toStack);
            AppendItemRow(tx, owner, owner, fromStack, toStack, fromAfter, toAfter, add, template, mk, ItemMoveKind.Move, 0, worldTick);
            _itemHeadDirty = true;
            _pendingGrid[2]++;
        }
        else if (to.Count > 0 && whole)
        {
            // Onto something else — another item, an item with stats, a full crate: the two swap slots.
            SetSlot(tx, from.Stack, (byte)dst);
            SetSlot(tx, to.Stack, (byte)src);
            (from, to) = (to, from);
            _pendingGrid[3]++;
        }
        else
        {
            // Part of an item with stats (a crate splits by an unpack), or part of a stack onto another item.
            RefuseWorkshop(subs, in command, TatooineReplication.MoveBad);
            return;
        }

        _craftBodies.Add(body);
        _movesApplied.Add(command);
    }

    private void DiscardFromCommand(Transaction tx, Transaction tick0, SubscriptionsCommands subs, in ClientCommand<DiscardItem> command, long worldTick)
    {
        if (!CraftActor(tick0, command.Session, out var owner, out var body, out _, out _, out _))
        {
            RefuseWorkshop(subs, in command, TatooineReplication.NotInWorld);
            return;
        }

        var items = ItemsOf(owner);
        var src = SlotOf(items, EntityId.FromRawValue(command.Value.Item));
        var n = command.Value.Count;
        // An item with stats, or a crate, goes whole: there is no part of one to keep.
        if (src == 0 || n < 1 || n > (uint)items.Slots[src].Count || (items.Slots[src].Instance && n != (uint)items.Slots[src].Count))
        {
            RefuseWorkshop(subs, in command, TatooineReplication.DiscardBad);
            return;
        }

        var (template, mk) = (items.Slots[src].Template, items.Slots[src].Mk);
        var units = (int)n;
        var fromAfter = Debit(tx, items, src, units, out var fromStack);
        AppendItemRow(tx, owner, ItemLedger.World, fromStack, 0, fromAfter, 0, units, template, mk, ItemMoveKind.Discard, 0, worldTick);
        _itemHead.Destroyed += units;
        _pendingDestroyed[KeyOf(template, mk)] += units;
        _itemHeadDirty = true;
        _pendingGrid[4]++;
        _craftBodies.Add(body);
        _discardsApplied.Add(command);
    }

    /// <summary>
    /// "Sort &amp; stack" (Loïc, 2026-11-04): first every stack of the same units (template and mark, never an item with stats or a crate) merged into the
    /// first of them as far as it holds, a <see cref="ItemMoveKind.Move"/> row each as a dragged merge writes; then the grid ordered by type, name and mark
    /// from slot 1 with no gap, which changes only where each is drawn, without a row.
    /// </summary>
    private void SortFromCommand(Transaction tx, Transaction tick0, SubscriptionsCommands subs, in ClientCommand<SortItems> command, long worldTick)
    {
        if (!CraftActor(tick0, command.Session, out var owner, out var body, out _, out _, out _))
        {
            RefuseWorkshop(subs, in command, TatooineReplication.NotInWorld);
            return;
        }

        // One order so far: another is a client this server does not know, not a sort to guess at (review, 2026-11-04).
        if (command.Value.Order != 0)
        {
            RefuseWorkshop(subs, in command, TatooineReplication.MoveBad);
            return;
        }

        var items = ItemsOf(owner);
        for (var s = 1; s <= ItemLedger.Slots; s++)
        {
            ref var into = ref items.Slots[s];
            if (into.Count == 0 || into.Instance)
            {
                continue;
            }

            for (var t = s + 1; t <= ItemLedger.Slots && into.Count < ItemLedger.StackMax; t++)
            {
                ref var from = ref items.Slots[t];
                if (from.Count == 0 || from.Instance || from.Template != into.Template || from.Mk != into.Mk)
                {
                    continue;
                }

                var add = Math.Min(from.Count, ItemLedger.StackMax - into.Count);
                var (template, mk) = (from.Template, from.Mk);
                var fromAfter = Debit(tx, items, t, add, out var fromStack);
                var toAfter = Credit(tx, owner, items, s, template, mk, add, out var toStack);
                AppendItemRow(tx, owner, owner, fromStack, toStack, fromAfter, toAfter, add, template, mk, ItemMoveKind.Move, 0, worldTick);
                _itemHeadDirty = true;
                _pendingGrid[2]++;
            }
        }

        var order = _sortScratch;
        order.Clear();
        for (var s = 1; s <= ItemLedger.Slots; s++)
        {
            if (items.Slots[s].Count > 0)
            {
                order.Add((s, items.Slots[s]));
            }
        }

        order.Sort(SortOrder);
        Array.Clear(items.Slots, 1, ItemLedger.Slots);
        for (var i = 0; i < order.Count; i++)
        {
            var (was, item) = order[i];
            items.Slots[i + 1] = item;
            if (was != i + 1)
            {
                SetSlot(tx, item.Stack, (byte)(i + 1));
            }
        }

        _pendingGrid[5]++;
        _craftBodies.Add(body);
        _sortsApplied.Add(command);
    }

    /// <summary>
    /// The sort's order: the catalogue's type (its category), then the name, then the mark; the bigger stack first, then the slot it was in, so equal items
    /// keep their order. An unknown template goes last.
    /// </summary>
    private static int SortOrder((int Slot, ItemSlot Item) a, (int Slot, ItemSlot Item) b)
    {
        var knownA = ItemCatalog.TryGet(a.Item.Template, out var ta);
        var knownB = ItemCatalog.TryGet(b.Item.Template, out var tb);
        var c = knownB.CompareTo(knownA);
        if (c == 0 && knownA)
        {
            c = ta.Category.CompareTo(tb.Category);
            c = c != 0 ? c : string.CompareOrdinal(ta.Name, tb.Name);
        }

        c = c != 0 ? c : a.Item.Template.CompareTo(b.Item.Template);
        c = c != 0 ? c : a.Item.Mk.CompareTo(b.Item.Mk);
        c = c != 0 ? c : b.Item.Count.CompareTo(a.Item.Count);
        return c != 0 ? c : a.Slot.CompareTo(b.Slot);
    }

    /// <summary>The Workshop's transaction ended: its moves, discards and sorts counted, or refused because nothing of them was written.</summary>
    private void EndGrid(SubscriptionsCommands subs, bool committed)
    {
        for (var i = 0; i < _pendingGrid.Length; i++)
        {
            if (committed)
            {
                Interlocked.Add(ref _grid[i], _pendingGrid[i]);
            }

            _pendingGrid[i] = 0;
        }

        if (!committed)
        {
            foreach (var c in _movesApplied)
            {
                RefuseWorkshop(subs, in c, TatooineReplication.CraftFailed);
            }

            foreach (var c in _discardsApplied)
            {
                RefuseWorkshop(subs, in c, TatooineReplication.CraftFailed);
            }

            foreach (var c in _sortsApplied)
            {
                RefuseWorkshop(subs, in c, TatooineReplication.CraftFailed);
            }
        }

        _movesApplied.Clear();
        _discardsApplied.Clear();
        _sortsApplied.Clear();
    }
}
