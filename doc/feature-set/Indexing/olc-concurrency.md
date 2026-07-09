---
uid: feature-indexing-olc-concurrency
title: 'Optimistic Lock Coupling (per-node concurrency)'
description: 'Lock-free index reads and leaf-only write latching replace whole-tree locking — transparent to your code.'
---

# Optimistic Lock Coupling (per-node concurrency)
> Lock-free index reads and leaf-only write latching replace whole-tree locking — transparent to your code.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Indexing](./README.md)

## 🎯 What it solves

Every index lookup, range scan, insert, update, and delete touches the same B+Tree. A single whole-tree lock
would serialize all of that traffic — one writer at a time, blocking every concurrent reader, while readers
themselves contend on a shared counter just to prove they didn't conflict with anything. At the core counts
Typhon targets, that becomes the dominant scaling bottleneck for any workload that mixes index reads and
writes. Optimistic Lock Coupling (OLC) removes the whole-tree lock so index throughput scales with how many
cores are touching genuinely independent parts of the tree, not with how many cores are touching the tree at
all.

## ⚙️ How it works (in brief)

Each B+Tree (and R-Tree) node carries its own small version counter instead of the tree holding one global
lock. Readers traverse by reading a node's version, reading its data, then re-validating the version was
unchanged — no lock is ever taken, so a reader and a writer can touch the same node at the same instant
without either blocking. Writers descend the same lock-free way, then take an exclusive latch on just the leaf
they're modifying — and, on the rare structural change (split or merge), its parent too, latched bottom-up to
stay deadlock-free. If a version check fails mid-traversal because of a concurrent split, the operation
follows a right-link to the node's new sibling, or restarts from the root if the failure is higher up;
structurally retired nodes are only reclaimed once Typhon's epoch mechanism confirms no in-flight reader can
still see them.

## 💻 Usage

```csharp
// No API surface to call — every index operation already runs through OLC.
// Concurrent readers and writers on the same index never block each other:

var nameIndex = engine.GetIndexRef<Player, String64>(p => p.Name);

// Thread A — range scan, fully lock-free
using var tx = engine.CreateQuickTransaction();
using var range = tx.EnumerateIndex<Player, String64>(nameIndex, "Anna", "Marco");
foreach (var hit in range) { /* ... */ }

// Thread B — concurrent insert on the same index, only latches the leaf it touches
using var tx2 = engine.CreateQuickTransaction();
EntityRef e = tx2.Spawn<PlayerArchetype>();
e.Write(PlayerArchetype.P).Name = "Diego";
tx2.Commit();
```

No tuning knobs — there is nothing to configure. OLC is the only concurrency path for index reads and writes.

## ⚠️ Guarantees & limits

- Readers never acquire a lock and never block a writer; a writer latches only the node(s) it mutates — one
  for a simple insert/remove, up to two during a split or merge, acquired bottom-up to prevent deadlock.
- A reader whose version check fails re-reads just the affected node (or follows a right-link after a
  concurrent split) — it never restarts an entire scan, only the step that raced.
- The same protocol covers both the B+Tree (primary and secondary indexes) and the spatial R-Tree — one
  concurrency model for all index access in Typhon.
- Nodes retired by a merge are kept alive until epoch reclamation confirms no reader still references them —
  no use-after-free under contention.
- After a bounded number of failed optimistic attempts, an operation falls back to pessimistic top-down
  latch-coupling to guarantee forward progress; this is invisible to callers beyond an occasional latency tail.
- Purely an internal concurrency mechanism — there is no API to enable, disable, or tune it. It underlies every
  index lookup, range scan, insert, remove, and the compound Move/MoveValue operations.

## 🧪 Tests

- [OlcLatchTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/OlcLatchTests.cs) — the per-node latch primitive itself: `TryWriteLock`/`WriteUnlock`/`ReadVersion`/`ValidateVersion`/`MarkObsolete`, plus `Concurrent_ReadersAndWriter_VersionConsistency`
- [OlcBTreeTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/OlcBTreeTests.cs) — tree-level optimistic reads racing writers (`TryGet_ConcurrentReadersWithOneWriter_ReadersGetCorrectValues`), concurrent splits/merges, and epoch-deferred node reclamation (`DeferredDeallocation_*`)
- [OlcBTreeStressTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/OlcBTreeStressTests.cs) — `[Explicit]` 32-thread mixed read/write/remove stress variant of the same scenarios, for restart/fallback/split/merge paths that need heavier contention to trigger

## 🔗 Related

- Sibling features: [Lookup and Range-Scan Operations](./lookup-and-range-scan.md), [Compound Move/MoveValue](./compound-move-operations.md)

<!-- Deep dive: claude/design/Indexing/concurrent-index-scaling.md — OLC design, protocols, epoch integration -->
<!-- Deep dive: claude/design/Indexing/latch-coupled-smo.md — bottom-up latch coupling for splits/merges, correctness fixes -->
<!-- Deep dive: claude/overview/04-data.md#concurrency-model-optimistic-lock-coupling-olc — concurrency model summary -->
<!-- Deep dive: claude/overview/01-concurrency.md §1.8 OlcLatch — latch implementation, spin strategy -->
