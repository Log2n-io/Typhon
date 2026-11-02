using System.Collections.Generic;
using System.Threading;

namespace SwgTatooine.Server;

/// <summary>
/// Items with stats (crafting-build.md § 2.1): one <see cref="ItemInstanceArch"/> entity each, moved whole through the one item code path.
/// </summary>
/// <remarks>
/// <para>
/// <b>An instance keeps its entity wherever it goes</b>, because its stats are its own: a move between owners rewrites its <see cref="ItemStack.Owner"/> and
/// slot rather than destroying one stack and spawning another, as units do. Its ledger rows are a stack's: <c>FromStack</c> and <c>ToStack</c> name the
/// instance, a count of 1, 0 after on the side it left and 1 on the side it reached — so the audit's replay sees it emptied, then filled again by its new
/// owner, which is how the replay already treats an entity id given again.
/// </para>
/// <para>
/// <b>In escrow it belongs to its listing</b> (bazaar.md § 3.2): its owner is the listing's raw id and its slot 0, the listing's quantity 1. It is out of the
/// world's held units and stacks while it is there, as a listing's units are.
/// </para>
/// <para><b>Never merged</b>: <see cref="SlotToFill"/> and <see cref="RoomFor"/> skip a slot holding an instance, and room for one is a free slot.</para>
/// </remarks>
public sealed partial class SimBridge
{
    // A listing's instance in escrow, by the listing's raw id: read with the stacks, kept by the transfers.
    private readonly Dictionary<long, EntityId> _escrowItemOf = [];
    private ushort _instanceRouting;

    private ushort InstanceRouting => _instanceRouting != 0 ? _instanceRouting : _instanceRouting = Dbe.ArchetypeIdOf<ItemInstanceArch>();

    /// <summary>Whether an entity is an item instance rather than a stack: by its archetype.</summary>
    private bool IsInstance(EntityId id) => !id.IsNull && id.ArchetypeId == InstanceRouting;

    /// <summary>The instance a listing holds in escrow; <see cref="EntityId.Null"/> for a listing of units, or none.</summary>
    internal EntityId EscrowItemOf(long listing) => _escrowItemOf.TryGetValue(listing, out var id) ? id : EntityId.Null;

    /// <summary>
    /// Mints an instance into a character's lowest free grid slot, with <paramref name="stats"/>: one <c>World → character</c> row naming it.
    /// </summary>
    /// <returns>The instance; <see cref="EntityId.Null"/>, having written nothing, when the template has no stats, the Mk does not fit, the character has no
    /// wallet or no free slot.</returns>
    private EntityId MintInstance(Transaction tx, long to, ushort template, byte mk, in ItemInstance stats, byte kind, long reference, long worldTick)
    {
        if (!ItemCatalog.HasStats(template) || !ItemCatalog.TryGet(template, out var t) || !ItemCatalog.MkFits(in t, mk) || to == ItemLedger.World
            || IsListing(to) || !HasWallet(tx, to))
        {
            Interlocked.Increment(ref _itemsRefused);
            return EntityId.Null;
        }

        EnsureItems(tx);
        var items = ItemsOf(to);
        var slot = FreeSlot(items);
        if (slot == 0)
        {
            Interlocked.Increment(ref _itemsRefused);
            return EntityId.Null;
        }

        var seq = _itemHead.LastSeq + 1;
        var stack = new ItemStack { Owner = to, Template = template, Mk = mk, Slot = (byte)slot, Count = 1, LastSeq = seq, Rows = 1 };
        var id = tx.Spawn<ItemInstanceArch>(ItemStackArch.Stack.Set(in stack), ItemInstanceArch.Stats.Set(in stats));
        items.Slots[slot] = new ItemSlot { Stack = id, Template = template, Mk = mk, Count = 1, Instance = true };
        AppendItemRow(tx, ItemLedger.World, to, 0, (long)id.RawValue, 0, 1, 1, template, mk, kind, reference, worldTick);
        _itemHead.Minted++;
        _pendingMinted[KeyOf(template, mk)]++;
        _pendingHeld[KeyOf(template, mk)]++;
        _pendingUnits++;
        _pendingStacks++;
        _itemHeadDirty = true;
        return id;
    }

    /// <summary>
    /// <see cref="ItemTransfer"/> for a template with stats: one instance, whole. From a character it is <paramref name="item"/>, which must sit in its grid
    /// (an equipped item is unequipped first); from a listing it is the one in its escrow. To World it is destroyed; to a listing it goes into escrow; to a
    /// character, into its lowest free slot. Validated whole before the first write.
    /// </summary>
    /// <returns>1 when moved; 0, having written nothing, when refused.</returns>
    private int InstanceTransfer(Transaction tx, long from, long to, ushort template, byte mk, int count, byte kind, long reference, long worldTick,
        EntityId item)
    {
        var fromListing = IsListing(from);
        var toListing = IsListing(to);
        if (count != 1 || from == ItemLedger.World || (fromListing && toListing) || (fromListing && to == ItemLedger.World))
        {
            Interlocked.Increment(ref _itemsRefused);
            return 0;
        }

        // The listing side, checked before anything moves: its book entry, its template and Mk, its quantity.
        BookEntry entry = null;
        if (fromListing || toListing)
        {
            var listingKey = fromListing ? from : to;
            if (!_book.TryGetValue(listingKey, out entry) || entry.L.Template != template || entry.L.Mk != mk
                || entry.L.Quantity != (fromListing ? 1 : 0))
            {
                Interlocked.Increment(ref _itemsRefused);
                return 0;
            }
        }

        // The instance, and where it is now.
        var src = 0;
        OwnerItems giver = null;
        if (fromListing)
        {
            item = EscrowItemOf(from);
        }
        else
        {
            giver = ItemsOf(from);
            src = SlotOf(giver, item);
        }

        if (!IsInstance(item) || (!fromListing && src == 0) || (giver != null && (giver.Slots[src].Template != template || giver.Slots[src].Mk != mk)))
        {
            Interlocked.Increment(ref _itemsRefused);
            return 0;
        }

        OwnerItems taker = null;
        var dst = 0;
        if (to != ItemLedger.World && !toListing)
        {
            taker = ItemsOf(to);
            dst = FreeSlot(taker);
            if (dst == 0)
            {
                Interlocked.Increment(ref _itemsRefused);
                return 0;
            }
        }

        // Validated: the instance's stack, the grids, the escrow, one row.
        var key = (long)item.RawValue;
        var seq = _itemHead.LastSeq + 1;
        var k = KeyOf(template, mk);
        if (giver != null)
        {
            giver.Slots[src] = default;
            _pendingHeld[k]--;
            _pendingStacks--;
        }
        else
        {
            _escrowItemOf.Remove(from);
        }

        if (to == ItemLedger.World)
        {
            tx.Destroy(item);
            AppendItemRow(tx, from, to, key, 0, 0, 0, 1, template, mk, kind, reference, worldTick);
            _itemHead.Destroyed++;
            _pendingDestroyed[k]++;
            _itemHeadDirty = true;
            return 1;
        }

        var e = tx.OpenMut(item);
        var stack = e.Read(ItemStackArch.Stack);
        stack.Owner = to;
        stack.Slot = (byte)dst;
        stack.LastSeq = seq;
        stack.Rows = toListing ? stack.Rows : 1;
        e.Set(ItemStackArch.Stack, stack);
        if (toListing)
        {
            _escrowItemOf[to] = item;
            entry.L.Quantity = 1;
            AppendItemRow(tx, from, to, key, to, 0, 1, 1, template, mk, kind, reference, worldTick);
        }
        else
        {
            taker.Slots[dst] = new ItemSlot { Stack = item, Template = template, Mk = mk, Count = 1, Instance = true };
            _pendingHeld[k]++;
            _pendingStacks++;
            if (fromListing)
            {
                entry.L.Quantity = 0;
            }

            AppendItemRow(tx, from, to, fromListing ? from : key, key, 0, 1, 1, template, mk, kind, reference, worldTick);
        }

        if (entry != null)
        {
            entry.L.LastSeq = _itemHead.LastSeq;
            entry.L.Rows++;
            _bookEscrow += fromListing ? -1 : 1;
            tx.OpenMut(entry.Id).Set(ListingArch.Entry, entry.L);
            MarkCityDirty(in entry.L);
        }

        _itemHeadDirty = true;
        return 1;
    }

    /// <summary>Whether <paramref name="owner"/>'s grid holds the instance <paramref name="item"/>, of that template and Mk.</summary>
    private bool HoldsInstance(long owner, EntityId item, ushort template, byte mk)
    {
        var items = ItemsOf(owner);
        var s = SlotOf(items, item);
        return s != 0 && items.Slots[s].Instance && items.Slots[s].Template == template && items.Slots[s].Mk == mk;
    }

    /// <summary>The grid slot holding <paramref name="item"/>; 0 when it is not in the grid.</summary>
    private static int SlotOf(OwnerItems items, EntityId item)
    {
        if (item.IsNull)
        {
            return 0;
        }

        for (var s = 1; s <= ItemLedger.Slots; s++)
        {
            if (items.Slots[s].Stack == item)
            {
                return s;
            }
        }

        return 0;
    }

    /// <summary>The lowest free grid slot; 0 when the grid is full.</summary>
    private static int FreeSlot(OwnerItems items)
    {
        for (var s = 1; s <= ItemLedger.Slots; s++)
        {
            if (items.Slots[s].Count == 0)
            {
                return s;
            }
        }

        return 0;
    }

    /// <summary>
    /// One instance's stats, as the transaction under way sees them; <see langword="false"/> for anything that is not an instance.
    /// </summary>
    private bool TryStatsOf(Transaction tx, EntityId item, out ItemInstance stats)
    {
        if (IsInstance(item) && tx.TryOpen(item, out var e))
        {
            stats = e.Read(ItemInstanceArch.Stats);
            return true;
        }

        stats = default;
        return false;
    }
}
