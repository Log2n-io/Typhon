---
uid: feature-revision-mvcc-visibility-walk
title: 'Chain Walk Correctness Under Compaction'
description: 'The visibility walk scans a Versioned component''s whole revision chain rather than stopping at the first too-new entry — because background revision GC can…'
---

# Chain Walk Correctness Under Compaction
> The visibility walk scans a `Versioned` component's whole revision chain rather than stopping at the first too-new entry — because a chain's physical order is not its commit-stamp order.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Revision](./README.md)

## 🎯 What it solves

[MVCC Snapshot Visibility](./mvcc-snapshot-visibility.md) promises that a read resolves the latest revision
committed at-or-before the reader's TSN. The naive way to implement that — walk the chain in physical order,
stop at the first entry whose TSN exceeds the reader's snapshot — is wrong on Typhon's revision chain: an entry is appended when its writer stages it, but it takes its
commit stamp only at its writer's commit point, so two writers of one entity can publish in either order (a
last-writer-wins commit publishes its entry where it sits), and cleanup compaction rewrites the chain — the
chain's physical order and its commit-stamp order can diverge. A walk that breaks early can therefore skip the
revision a reader was actually entitled to see, returning a stale or wrong value with no error.

## ⚙️ How it works (in brief)

`RevisionChainReader.WalkChain` always processes every live entry in the chain — never breaking on the first
too-new entry it meets — and keeps the entry with the highest commit stamp at-or-below the reader's snapshot
TSN, voided entries skipped along the way. An entry is staged with its writer's TSN and the `IsolationFlag` set;
the writer's publish replaces both with its commit stamp. A still-flagged entry is resolved through its owner's
commit card: visible only if the owner is committing with a stamp at-or-below the snapshot, otherwise it is an
uncommitted write, invisible to every reader — including the writer's own later reads through this same path
(read-your-own-writes is served separately, from a per-transaction cache, not from the chain). One fast path exists: a single-entry, already-committed chain — the
common steady state for an entity not under concurrent write contention — resolves directly off the chain header
without constructing the full walk or taking the chain's read lock at all.

## 💻 Usage

There's no separate API for this — it's the resolution step inside every `Versioned` read (`QueryRead`, `Open`,
`PointInTimeAccessor`; see [MVCC Snapshot Visibility](./mvcc-snapshot-visibility.md) for those call sites). What's
observable from the outside is purely a correctness property: a snapshot read keeps returning the right value
across concurrent writes *and* concurrent cleanup, with no window where compaction running in the background
causes a read to regress to an older or wrong revision.

## ⚠️ Guarantees & limits

- **No break-on-first-too-new** — the walk always covers the full chain; a later-slotted revision with a lower
  commit stamp than an earlier-slotted one is never mistaken for "we've gone too far".
- **Highest commit stamp wins among visible candidates** — ties cannot occur (stamps are unique per commit), so
  resolution is always deterministic.
- **Read cost scales with chain length** — a hot component with many outstanding revisions (e.g. a long-lived
  reader holding old ones alive) makes every read on it walk further; this is exactly what the revision garbage
  collector bounds by reclaiming entries no live snapshot can still need.
- **Single-entry fast path takes no lock** — the steady-state case (no overflow chunk, already committed) reads
  the header and one element directly, skipping `RevisionEnumerator` construction and the shared-lock acquire
  that the general walk needs to stay consistent against concurrent compaction.

## 🧪 Tests

- [ChaosStressTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/ChaosStressTests.cs) — `RevisionChainDepth_DeepChainWithCleanup` and `SentinelRevision_StaggeredReaderRelease` interleave writes with staggered/out-of-order reader release so background compaction runs concurrently with reads, then assert every reader still resolves its own snapshot correctly (no regression to a stale or wrong revision)

## 🔗 Related

- Parent feature: [MVCC Snapshot Visibility](./mvcc-snapshot-visibility.md)
- Sibling: [Revision Garbage Collection & Compaction](./revision-gc-compaction.md) — the background compactor whose relocations this walk must tolerate
- Source: [`RevisionChainReader.WalkChain`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Transactions/internals/RevisionWalker.cs), [`RevisionEnumerator`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Revision/internals/RevisionEnumerator.cs)

<!-- Deep dive: claude/design/Revision/02-mvcc-visibility.md §5 — The chain is not TSN-sorted -->
<!-- ADR: claude/adr/003-mvcc-snapshot-isolation.md -->
