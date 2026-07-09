---
uid: feature-storage-index
title: 'Storage'
description: 'The persistence layer underlying every Typhon data structure: a memory-mapped, 8 KiB-page cache with clock-sweep eviction and epoch-based concurrency…'
---

# Storage
> The persistence layer underlying every Typhon data structure: a memory-mapped, 8 KiB-page cache with clock-sweep eviction and epoch-based concurrency safety, layered on allocation abstractions that go file-page occupancy → multi-page segments → fixed-size chunks. Pluggable persistent and transient backends share that one substrate, and integrity (CRC32C + A/B pairing), file locking, and read-only introspection round out what makes the on-disk file safe and inspectable.

> 🔬 **Recommended:** read [in-depth-overview/02-storage.md](../../in-depth-overview/02-storage.md) (Chapter 02: Storage) first to understand the overall design and concepts behind this category, before diving into the specific features below.

## Public Features

| Feature | Summary | Status | Level |
|---|---|---|---|
| [Transient Store (heap-backed)](pluggable-storage-backend/transient-store.md) *(part of [Pluggable Storage Backend](pluggable-storage-backend/README.md))* | Pinned heap blocks standing in for the page cache, so Transient components get raw-memory speed through the same segment code — tune via `TransientOptions` | ✅ Implemented | 🔵 Core |
| [Database File Locking & Lifecycle](file-locking-lifecycle.md) | Two-layer protection against concurrent multi-process opens — OS `FileShare.Read` plus an advisory `.lock` sidecar with stale/live/cross-machine PID detection — plus create/open/delete lifecycle handling | ✅ Implemented | 🔵 Core |
| [Memory-Mapped Page Cache & Clock-Sweep Eviction](page-cache.md) | 8 KiB pages, 4-state lifecycle, clock-sweep eviction with sequential-allocation optimization, async I/O, and backpressure handling | ✅ Implemented | 🟣 Advanced |
| [Page Integrity — CRC32C, Seqlock Snapshots & A/B Page Pairing](page-integrity.md) | Hardware CRC32C page checksums, seqlock-protected checkpoint snapshots, and A/B slot pairing for structural pages that can't be rebuilt | ✅ Implemented | 🟣 Advanced |
| [Variable-Sized Buffer Storage (VSBS)](vsbs.md) | Linked-chunk-chain storage for variable-length, reference-counted buffers — backs multi-value B+Tree index entries and per-element-type `ComponentCollection<T>` pools | ✅ Implemented | 🟣 Advanced |
| [Storage Introspection & Integrity Diagnostics](storage-introspection.md) | Read-only APIs exposing segment/page topology and auditing occupancy-vs-segment consistency, powering the Workbench Database File Map | ✅ Implemented | 🟣 Advanced |
| [Page Compression (Future)](page-compression-future.md) | Planned LZ4-style compression adapter for cold/historical data, string-heavy tables, and backups — deliberately not implemented in v1 so hot real-time paths stay within microsecond latency targets | 📋 Planned | 🟣 Advanced |

## Internal Features

> Engine machinery that makes the features above work. Application code never calls these directly — documented here for engine contributors.

| Feature | Summary | Status |
|---|---|---|
| [Epoch-Based Page Protection & Dirty-Page Tracking](epoch-dirty-tracking.md) | Epoch-tagged eviction safety plus the ChangeSet/DirtyCounter/ActiveChunkWriters/SlotRefCount protocol that pins modified or pointer-referenced pages until checkpoint write-back | ✅ Implemented |
| [Page Allocation & Occupancy Tracking](page-allocation-occupancy.md) | A 3-level bitmap that allocates and tracks every 8 KiB page in the database file, growing the file automatically as needed | ✅ Implemented |
| [Segment & Chunk-Based Allocation Engine](segment-chunk-allocation.md) | Multi-page directories and fixed-size slot allocation — the substrate every component, index, and revision chain is built from | ✅ Implemented |
| [Pluggable Storage Backend (Persistent vs Transient)](pluggable-storage-backend/README.md) | One set of segment/index code, JIT-specialized per backend, so Transient components get heap speed for free | ✅ Implemented |
| &nbsp;&nbsp;↳ [Persistent Store (MMF-backed)](pluggable-storage-backend/persistent-store.md) | The default backend — every durable component's segments run through the memory-mapped page cache at zero abstraction cost | ✅ Implemented |
| [String Table Storage](string-table.md) | UTF-8 string storage spread across linked fixed-size chunks, for strings too long to hold inline | ✅ Implemented |