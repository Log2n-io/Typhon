# Write-Conflict Baseline Tracking
> Every revision append remembers the value it replaced, so commit-time conflict detection and resolution have something to compare against.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Revision](./README.md)

## 🎯 What it solves

Typhon's optimistic concurrency model (no locks during execution, ADR-003) needs a cheap way to answer two
questions at commit time: "did anyone else commit a newer value for this entity's component since I read mine?"
and, if so, "what was my baseline, what did the other transaction write, and what was I about to write?" Without
a tracked baseline, conflict detection would have no fixed point of comparison, and a `ConcurrencyConflictHandler`
that wants to rebase a delta (rather than just overwrite) would have nothing to rebase from. Background revision
GC also physically reshuffles chain entries during compaction, so a transaction's cached position can go stale
between read and commit — that staleness has to be caught, not silently produce a wrong result.

## ⚙️ How it works (in brief)

Every chain append (`AddCompRev`) records two coordinates: the new revision just written (`Cur`) and the revision
it superseded (`Prev`), each as a `(chunk id, revision index)` pair. At commit, this `Cur` baseline is compared
against the chain's authoritative state — a monotonic per-entity commit counter, immune to index reshuffling —
to decide whether a conflict occurred. If a `ConcurrencyConflictHandler` is registered and a conflict fires, the
baseline pair resolves the four data views (`ReadData`, `CommittedData`, `CommittingData`, `ToCommitData`) handed
to the handler. Because compaction can shift indices between read and commit, the cached index is re-validated
against its (unique) content-chunk id — `FindRevisionIndexByChunkId` — and repaired before it's trusted; without
a handler, conflicts fall back to plain index-order comparison.

## 💻 Usage

The baseline itself is internal — application code interacts with it only through `Transaction.Commit`'s optional
handler, which receives the resolved comparison views:

```csharp
using var t1 = dbe.CreateQuickTransaction();
t1.Open(entityId).Read(CompAArch.A);                       // baseline snapshot
ref var w1 = ref t1.OpenMut(entityId).Write(CompAArch.A);
w1 = new CompA(90);                                         // intended write, delta = -10

// Meanwhile another transaction read the same baseline, set A=130, and committed first.

void ConflictHandler(ref ConcurrencyConflictSolver solver)
{
    var read = solver.ReadData<CompA>();           // 100 — our original snapshot
    var committed = solver.CommittedData<CompA>();  // 130 — the other transaction's committed value
    var committing = solver.CommittingData<CompA>(); // 90  — our intended write

    var delta = committing.A - read.A;              // -10
    solver.ToCommitData<CompA>().A = committed.A + delta; // rebase: 130 + (-10) = 120
}

t1.Commit(ConflictHandler);   // result: A == 120, not 90 (ours) or 130 (theirs)
```

| Resolution helper | Effect |
|---|---|
| `TakeRead<T>()` | Discard all changes, revert to the read snapshot |
| `TakeCommitted<T>()` | Accept the other transaction's value |
| `TakeCommitting<T>()` | Keep your own value (also the default if the handler is a no-op) |
| (write `ToCommitData<T>()` directly) | Custom resolution, e.g. delta rebase as above |

## ⚠️ Guarantees & limits

- Without a handler, conflict resolution is "last writer wins" — the committing transaction's value always
  replaces the prior one; no exception is raised.
- With a handler, conflict detection uses a monotonic per-entity commit counter, not revision indices — it cannot
  be fooled by background compaction reordering the chain between your read and your commit.
- Stale cached positions caused by concurrent compaction are transparently repaired before use; if an entry is
  ever truly unrecoverable (a hard invariant violation, not expected in practice) commit throws rather than
  resolving against wrong data.
- The handler runs under the entity's per-component revision-chain exclusive lock, so detection, resolution, and
  publish are one atomic region — no second writer can interleave between "you see the conflict" and "your
  resolution becomes visible".
- Baseline tracking is per `(transaction, entity, component)` and only meaningful across the lifetime of one
  transaction — it is not a general-purpose diff/version API.
- Applies only to `StorageMode.Versioned` components — `SingleVersion`/`Committed`-discipline writes have no
  revision chain to compare against (see [Committed Durability Discipline](../Ecs/storage-modes/storage-mode-committed.md)).

## 🧪 Tests

- [ConcurrencyConflictTests](../../../test/Typhon.Engine.Tests/Data/ConcurrencyConflictTests.cs) — the canonical suite: no-conflict pass-through, no-handler last-wins, handler-based delta rebase/`TakeCommitted`/`TakeRead`, multi-entity (all-conflict and partial-conflict) handler dispatch, concurrent-thread rebase race

## 🔗 Related

- Related feature: [Revision Append & Chain Growth](./revision-append-write-path.md) (where Prev/Cur are first set), [MVCC Snapshot Visibility](./mvcc-snapshot-visibility.md)
- Sibling: [Optimistic Conflict Resolution](../Transactions/optimistic-conflict-resolution.md) — the `Transaction.Commit` handler surface that consumes this baseline
- Source: [`ComponentRevisionManager.AddCompRev`/`FindRevisionIndexByChunkId`](../../../src/Typhon.Engine/Revision/internals/ComponentRevisionManager.cs), [`Transaction.DetectAndResolveConflict`](../../../src/Typhon.Engine/Transactions/public/Transaction.cs), [`ConcurrencyConflictSolver`](../../../src/Typhon.Engine/Transactions/public/ConcurrencyConflictSolver.cs)

<!-- Deep dive: claude/design/Revision/01-revision-chain-storage.md, claude/design/Revision/03-revision-gc-compaction.md §3 -->
<!-- ADR: claude/adr/003-mvcc-snapshot-isolation.md -->
