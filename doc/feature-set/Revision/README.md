# Revision
> The per-component MVCC revision-chain subsystem: stores every live version of a `Versioned`-mode component in a compact on-disk circular buffer, appends rather than overwrites on every write, and resolves snapshot-isolated reads against that chain by transaction TSN. The same chain backs commit-time conflict detection and is reclaimed — by live garbage collection and by post-crash scrub — once no active transaction can still see an old version.

> 🔬 **Recommended:** read [in-depth-overview/05-revision.md](../../in-depth-overview/05-revision.md) (Chapter 05: Revision (MVCC)) first to understand the overall design and concepts behind this category, before diving into the specific features below.

## Public Features

| Feature | Summary | Status | Level |
|---|---|---|---|
| [Revision Append & Chain Growth](revision-append-write-path.md) | Every write to a `Versioned` component creates a new immutable revision instead of overwriting the old one — `Spawn` allocates, `Write<T>()` appends, `Destroy` tombstones | ✅ Implemented | 🔵 Core |
| [MVCC Snapshot Visibility](mvcc-snapshot-visibility.md) | Reads resolve to the latest revision committed at-or-before the reader's transaction TSN, with read-your-own-writes and explicit `RevisionReadStatus` outcomes (Success/NotFound/SnapshotInvisible/Deleted) | ✅ Implemented | 🔵 Core |
| [Write-Conflict Baseline Tracking](optimistic-conflict-baseline.md) | Every chain append records the new and prior revision as the comparison baseline used by commit-time conflict detection and `ConcurrencyConflictHandler`s | ✅ Implemented | 🟣 Advanced |
| [Revision Garbage Collection & Compaction](revision-gc-compaction.md) | Bounded-memory chain cleanup keyed off `MinTSN`, preserving a sentinel for in-flight readers and collapsing fully-dead chains to trigger entity removal | ✅ Implemented | 🟣 Advanced |

## Internal Features

| Feature | Summary | Status |
|---|---|---|
| [Revision Chain Storage](revision-chain-storage.md) | Per-entity, per-component circular-buffer chunk chain holding every live revision of a `Versioned` component, allocated on first write and grown on demand | ✅ Implemented |
| [Chain Walk Correctness Under Compaction](mvcc-visibility-walk.md) (part of [MVCC Snapshot Visibility](mvcc-snapshot-visibility.md)) | The visibility walk scans the whole chain instead of breaking on the first too-new entry, because background GC compaction can reorder entries without changing their TSNs | ✅ Implemented |
| [Crash-Recovery Chain Scrub & Orphan Sweep](crash-recovery-chain-scrub.md) | Post-crash recovery step that collapses every `Versioned` chain to its single committed HEAD and frees unreachable revision-table chunks, guaranteeing pre-crash MVCC history never survives into the recovered base | ✅ Implemented |