# Variable-Sized Buffer Storage (VSBS)
> Linked-chunk buffer storage for variable-length, ref-counted data — the substrate behind per-entity collections and multi-value indexes.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Storage](./README.md)

## 🎯 What it solves

Components are fixed-size blittable structs stored SoA for cache-friendly iteration — there's no room in that layout for a naturally variable-length, per-entity list (an inventory, a tag set, a pending-event buffer). VSBS is the storage primitive that holds that kind of data outside the fixed-size record: the component embeds only a 4-byte buffer id, and the actual elements live in a pool sized to grow and shrink independently. The same mechanism also backs multi-value secondary index entries (`[Index(AllowMultiple = true)]`), where one key maps to many entity references.

## ⚙️ How it works (in brief)

A buffer is a forward-linked chain of fixed-size chunks holding elements of one uniform type; appending grows the chain by linking in another chunk — there's no copy of existing elements on grow, and no random-access API (only sequential enumeration). Every buffer carries a reference count. For `Versioned` components, overwriting a revision shares its `ComponentCollection<T>` buffer with the previous revision (`RefCounter > 1`) instead of deep-copying it; the first mutation through a shared buffer transparently clones it (copy-on-write), so older snapshots keep seeing the contents they originally referenced. `SingleVersion` components have exactly one owner, so their buffers are always mutated in place. Elements of a given CLR type all share one pool (segment kind `ComponentCollection`) — every `ComponentCollection<T>` field, across every component table, resolves to the same pool by `T` alone.

## 💻 Usage

```csharp
[Component("Game.Player", revision: 1)]
public struct PlayerComponent
{
    public int Health;
    public ComponentCollection<int> Inventory;   // 4-byte buffer id — no inline storage
}

using var t = dbe.CreateQuickTransaction();
var player = new PlayerComponent { Health = 100 };

using (var inv = t.CreateComponentCollectionAccessor(ref player.Inventory))
{
    inv.Add(1001);   // item ids
    inv.Add(1002);
}

var entityId = t.Spawn<PlayerArchetype>(PlayerArchetype.Player.Set(in player));
t.Commit();

// Later: read it back (read-only enumerator, no write lock taken)
using var rt = dbe.CreateQuickTransaction();
var loaded = rt.Open(entityId).Read(PlayerArchetype.Player);

foreach (var itemId in rt.GetReadOnlyCollectionEnumerator(ref loaded.Inventory))
{
    Console.WriteLine(itemId);
}
```

## ⚠️ Guarantees & limits

- Element type must be `unmanaged`, same blittability constraint as components.
- Appending is amortized O(1) — bounded by occasional chunk allocation, never a full-buffer copy.
- No random-element access by index; reading means sequentially enumerating the buffer (`GetAllElements` / the enumerator).
- `Versioned` components get copy-on-write sharing automatically — the application never manages the ref-count directly; divergence cost is paid only on the first write to a shared buffer, not on every revision.
- `SingleVersion` components mutate the buffer in place — cheapest path, no MVCC sharing applies.
- **Crash safety caveat:** buffer *content* reaches disk only at the next checkpoint — collection writes are not WAL-redo-logged. A crash between a collection write and the following checkpoint can lose that write (the component's buffer id itself is recovered; the new elements are not). Collection durability is checkpoint-bounded, not commit-bounded — treat collections as you would deferred-durability data, not as commit-durable state.
- `ComponentCollectionAccessor<T>` and the read-only enumerator are transaction-affine, like all other component access — use them only from the owning transaction's thread, and dispose them (`using`) to release the underlying chunk accessor.
- Persists across reopen — pools reload as ordinary segments and component tables reconnect to existing buffer ids automatically.

## 🧪 Tests

- [ComponentCollectionTests](../../../test/Typhon.Engine.Tests/Data/ComponentCollectionTests.cs) — create/read/update, `RefCounter` sharing, copy-on-write clone-on-first-write, destroy frees the buffer
- [ManagedPagedMMFTests](../../../test/Typhon.Engine.Tests/Storage/ManagedPagedMMFTests.cs) — low-level `VariableSizedBufferSegment` chain allocation, buffer cloning, and delete mechanics

## 🔗 Related

- Related feature: [Segment & Chunk-Based Allocation Engine](segment-chunk-allocation.md) (the chunk substrate VSBS chains are built from)

<!-- Deep dive: claude/overview/03-storage.md §3.7 VariableSizedBufferSegment -->
<!-- Deep dive: claude/overview/04-data.md §4.16 ComponentCollection Storage -->
<!-- ADR: claude/adr/056-cluster-componentcollection-storage.md -->
