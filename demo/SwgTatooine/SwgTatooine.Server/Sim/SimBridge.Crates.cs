using System.Collections.Generic;
using System.Threading;

namespace SwgTatooine.Server;

/// <summary>
/// Pack and unpack (crafting-catalog.md § 11.2; <see cref="Crates"/>), in the Workshop's transaction, anywhere: a crate is one instance whose count is how
/// many identical pristine items it holds. Units never leave their owner, so neither is a mint nor a destroy: a row moves them between two stacks of one
/// owner, and the audit replays both.
/// </summary>
public sealed partial class SimBridge
{
    private readonly List<ClientCommand<PackItem>> _packsApplied = [];
    private readonly List<ClientCommand<UnpackItem>> _unpacksApplied = [];

    // The transaction under way's, published when it commits.
    private long _pendingPacks;
    private long _pendingUnpacked;

    private long _packs;
    private long _unpacked;

    /// <summary>Packs applied since start: items or crates put into a crate.</summary>
    public long Packs => Interlocked.Read(ref _packs);

    /// <summary>Items taken out of crates since start.</summary>
    public long Unpacked => Interlocked.Read(ref _unpacked);

    private void PackFromCommand(Transaction tx, Transaction tick0, SubscriptionsCommands subs, in ClientCommand<PackItem> command, long worldTick)
    {
        if (!CraftActor(tick0, command.Session, out var owner, out var body, out _, out _, out _))
        {
            RefuseWorkshop(subs, in command, TatooineReplication.NotInWorld);
            return;
        }

        var item = EntityId.FromRawValue(command.Value.Item);
        var into = EntityId.FromRawValue(command.Value.Into);
        var items = ItemsOf(owner);
        var src = SlotOf(items, item);
        var dst = SlotOf(items, into);
        if (src == 0 || dst == 0 || src == dst || !items.Slots[src].Instance || !items.Slots[dst].Instance
            || items.Slots[src].Template != items.Slots[dst].Template || items.Slots[src].Mk != items.Slots[dst].Mk
            || items.Slots[src].Count + items.Slots[dst].Count > Crates.Capacity
            || !TryStatsOf(tx, item, out var a) || !TryStatsOf(tx, into, out var b)
            || !Crates.Packable(items.Slots[src].Template, in a) || !Crates.Packable(items.Slots[dst].Template, in b) || !Crates.Identical(in a, in b))
        {
            RefuseWorkshop(subs, in command, TatooineReplication.PackBad);
            return;
        }

        // Validated: the absorbed entity's units into the crate, one row, the absorbed entity gone.
        var moved = items.Slots[src].Count;
        var total = items.Slots[dst].Count + moved;
        var (template, mk) = (items.Slots[dst].Template, items.Slots[dst].Mk);
        var crateKey = (long)into.RawValue;
        AppendItemRow(tx, owner, owner, (long)item.RawValue, crateKey, 0, total, moved, template, mk, ItemMoveKind.Pack, crateKey, worldTick);
        var crate = tx.OpenMut(into);
        var stack = crate.Read(ItemStackArch.Stack);
        stack.Count = total;
        stack.LastSeq = _itemHead.LastSeq;
        stack.Rows++;
        crate.Set(ItemStackArch.Stack, stack);
        tx.Destroy(item);
        items.Slots[dst].Count = total;
        items.Slots[src] = default;
        _pendingStacks--;
        _itemHeadDirty = true;
        _pendingPacks++;
        _craftBodies.Add(body);
        _packsApplied.Add(command);
    }

    private void UnpackFromCommand(Transaction tx, Transaction tick0, SubscriptionsCommands subs, in ClientCommand<UnpackItem> command, long worldTick)
    {
        if (!CraftActor(tick0, command.Session, out var owner, out var body, out _, out _, out _))
        {
            RefuseWorkshop(subs, in command, TatooineReplication.NotInWorld);
            return;
        }

        var item = EntityId.FromRawValue(command.Value.Item);
        var n = (int)command.Value.Count;
        var items = ItemsOf(owner);
        var src = SlotOf(items, item);
        if (src == 0 || !items.Slots[src].Instance || n < 1 || n >= items.Slots[src].Count || FreeSlots(items) < n || !TryStatsOf(tx, item, out var stats))
        {
            RefuseWorkshop(subs, in command, TatooineReplication.UnpackBad);
            return;
        }

        // Validated: each item a new instance with the crate's stats, in the lowest free slot, a row from the crate to it.
        var (template, mk) = (items.Slots[src].Template, items.Slots[src].Mk);
        var crateKey = (long)item.RawValue;
        var left = items.Slots[src].Count;
        for (var i = 0; i < n; i++)
        {
            var slot = FreeSlot(items);
            left--;
            var seq = _itemHead.LastSeq + 1;
            var single = new ItemStack { Owner = owner, Template = template, Mk = mk, Slot = (byte)slot, Count = 1, LastSeq = seq, Rows = 1 };
            var id = tx.Spawn<ItemInstanceArch>(ItemStackArch.Stack.Set(in single), ItemInstanceArch.Stats.Set(in stats));
            items.Slots[slot] = new ItemSlot { Stack = id, Template = template, Mk = mk, Count = 1, Instance = true };
            AppendItemRow(tx, owner, owner, crateKey, (long)id.RawValue, left, 1, 1, template, mk, ItemMoveKind.Unpack, crateKey, worldTick);
            _pendingStacks++;
        }

        var crate = tx.OpenMut(item);
        var stack = crate.Read(ItemStackArch.Stack);
        stack.Count = left;
        stack.LastSeq = _itemHead.LastSeq;
        stack.Rows += n;
        crate.Set(ItemStackArch.Stack, stack);
        items.Slots[src].Count = left;
        _itemHeadDirty = true;
        _pendingUnpacked += n;
        _craftBodies.Add(body);
        _unpacksApplied.Add(command);
    }

    /// <summary>How many grid slots are free.</summary>
    private static int FreeSlots(OwnerItems items)
    {
        var n = 0;
        for (var s = 1; s <= ItemLedger.Slots; s++)
        {
            n += items.Slots[s].Count == 0 ? 1 : 0;
        }

        return n;
    }

    /// <summary>The Workshop's transaction ended: its packs and unpacks counted, or refused because nothing of them was written.</summary>
    private void EndCrates(SubscriptionsCommands subs, bool committed)
    {
        if (committed)
        {
            Interlocked.Add(ref _packs, _pendingPacks);
            Interlocked.Add(ref _unpacked, _pendingUnpacked);
        }
        else
        {
            foreach (var c in _packsApplied)
            {
                RefuseWorkshop(subs, in c, TatooineReplication.CraftFailed);
            }

            foreach (var c in _unpacksApplied)
            {
                RefuseWorkshop(subs, in c, TatooineReplication.CraftFailed);
            }
        }

        _packsApplied.Clear();
        _unpacksApplied.Clear();
        _pendingPacks = 0;
        _pendingUnpacked = 0;
    }
}
