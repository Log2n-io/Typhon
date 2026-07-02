# MVCC Snapshot Visibility
> Reads resolve to the one revision that was committed at-or-before your transaction's snapshot — never a partial or future write.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Revision](./README.md)

## 🎯 What it solves

A multi-writer, multi-reader database needs a precise answer to "what value does *this* read see?" that holds
regardless of what other transactions are doing concurrently. Without it, readers could observe half-committed
writes, see data appear or disappear depending on timing, or get inconsistent answers across two reads of the
same entity within one transaction. Snapshot visibility gives every read path — `Transaction.QueryRead`,
`EntityAccessor.Open`/`OpenMut`, and the lock-free `PointInTimeAccessor` — one deterministic outcome per call,
expressed as an explicit status rather than an ambiguous null or stale value.

## ⚙️ How it works (in brief)

Every transaction (and every `PointInTimeAccessor` snapshot) is stamped with a TSN at creation. A read for a
`Versioned` component resolves against that fixed TSN: among the component's committed revisions, it picks the
latest one at-or-before the TSN, ignoring anything still mid-commit or committed afterward. Internally this
collapses to four outcomes — found, never created, created-but-not-yet-visible, or tombstoned — but the
higher-level APIs you call (`QueryRead`, `Open`, `Read`/`TryRead`) flatten these into a `bool` or a thrown
`InvalidOperationException`, since most callers don't need to distinguish "doesn't exist" from "not visible yet"
from "deleted". A transaction's own uncommitted writes are invisible through this same path; they're served
instead from a per-transaction write cache, so a transaction always reads back what it just wrote without
waiting for its own commit.

## 💻 Usage

```csharp
using var ro = dbe.CreateReadOnlyTransaction();          // TSN frozen at creation
if (ro.QueryRead<Stats>(heroId.RawValue, out var stats))  // resolves the revision visible at ro.TSN
{
    Evaluate(stats);
}
// false covers: component never existed, exists but not committed by ro.TSN, or tombstoned — callers
// don't need to tell these apart for a plain existence check.

using var uow = dbe.CreateUnitOfWork();
using var tx = uow.CreateTransaction();
ref var s = ref tx.OpenMut(heroId).Write(Hero.Stats);
s.Hp -= 10;                                                // uncommitted — invisible to every other reader
ref readonly var seen = ref tx.Open(heroId).Read(Hero.Stats);
// seen.Hp reflects the write above: read-your-own-writes via tx's local cache, not the chain walk
tx.Commit();
```

```csharp
// PointInTimeAccessor: same visibility rule, no transaction/locking overhead — for parallel query workers
using var snapshot = PointInTimeAccessor.Create(dbe);
var worker = snapshot.GetWorkerAccessor(0);
var entity = worker.Open(heroId);                          // throws if not found/visible at snapshot.TSN
ref readonly var hp = ref entity.Read(Hero.Stats);
```

| API | Not-found / not-yet-visible / deleted |
|-----|----------------------------------------|
| `QueryRead<T>` | returns `false` |
| `TryOpen` / `TryRead<T>` | returns `false` |
| `Open` / `OpenMut` | throws `InvalidOperationException` |

## ⚠️ Guarantees & limits

- **Stable snapshot** — a transaction's (or `PointInTimeAccessor`'s) TSN never advances on its own, so repeated
  reads of the same entity return the same revision for the transaction's whole lifetime, no matter what commits
  in the meantime.
- **No dirty reads** — a revision from a transaction still mid-commit is invisible to every other reader; there
  is no window where a partial write is observable.
- **Read-your-own-writes** — the writing transaction sees its own uncommitted changes via a local cache, while
  every other transaction still sees the prior committed revision.
- **Explicit, not silent, "deleted"** — a tombstoned entity reports as not-found through the collapsed bool/throw
  APIs, same as never having existed; callers needing to distinguish the two (e.g. `UpdateComponent` writing a
  new revision over a tombstone) use the lower-level chain metadata internally — this isn't exposed as public API.
- **`PointInTimeAccessor` reads only** — it can write `SingleVersion`/`Transient` components but throws on
  `Versioned` writes; use a `Transaction` when you need to mutate `Versioned` data.
- Visibility is resolved purely from each revision's commit state and TSN — crash recovery guarantees that by
  the time any transaction runs, only durably-committed revisions exist in the chain, so this rule needs no
  special case for "did this survive the last crash".
- **Correct under compaction** — resolution scans the whole chain rather than stopping at the first too-new
  entry, because background revision GC can reorder entries physically without changing their TSNs; a
  single-entry, all-committed chain (the common steady-state case) short-circuits this scan entirely, resolving
  without taking the chain's read lock.

## 🧪 Tests

- [EcsSpawnMvccTests](../../../test/Typhon.Engine.Tests/Data/ECS/EcsSpawnMvccTests.cs) — `Write_Versioned_OtherTx_SeesOldData` (no dirty reads), `Write_Versioned_ReadAfterWrite_SeesNewData`/`Read_PendingSpawn_UsesDirectLocation` (read-your-own-writes), `Read_Versioned_UsesRevisionChain`
- [PointInTimeAccessorTests](../../../test/Typhon.Engine.Tests/Data/PointInTimeAccessorTests.cs) — same visibility rule through the lock-free `PointInTimeAccessor` path (`ReadVersionedComponent_ReturnsCorrectValue`, `OpenDestroyedEntity_TryOpenReturnsFalse`)
- [ChaosStressTests](../../../test/Typhon.Engine.Tests/Data/ChaosStressTests.cs) — `RevisionChain_RapidUpdatesWithLongReaders` asserts a held snapshot never observes a value change while concurrent writers hammer the same entity

## 🔗 Related

- Related feature: [Revision Chain Storage](./revision-chain-storage.md) (the layout this walks), [Revision Append & Chain Growth](./revision-append-write-path.md)
- Sub-feature: [Chain Walk Correctness Under Compaction](./mvcc-visibility-walk.md) (why the walk can't break on the first too-new entry)
- Sibling: [Storage Mode: Versioned](../Ecs/storage-modes/storage-mode-versioned.md) — Versioned mode is the ECS-facing side of this revision chain
- Source: [`RevisionReadStatus`](../../../src/Typhon.Engine/Revision/public/RevisionReadStatus.cs), [`RevisionChainReader.WalkChain`](../../../src/Typhon.Engine/Transactions/internals/RevisionWalker.cs), [`RevisionEnumerator`](../../../src/Typhon.Engine/Revision/internals/RevisionEnumerator.cs), [`PointInTimeAccessor`](../../../src/Typhon.Engine/Ecs/public/PointInTimeAccessor.cs)

<!-- Deep dive: claude/design/Revision/02-mvcc-visibility.md, claude/overview/04-data.md -->
<!-- ADR: claude/adr/003-mvcc-snapshot-isolation.md -->
