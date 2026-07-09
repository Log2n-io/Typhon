---
uid: feature-foundation-index
title: 'Foundation'
description: 'Typhon''s lowest-level building blocks — lock-free synchronization primitives, epoch-based memory safety, monotonic deadline/timeout propagation,…'
---

# Foundation
> Typhon's lowest-level building blocks — lock-free synchronization primitives, epoch-based memory safety, monotonic deadline/timeout propagation, pinned-memory allocators, and high-performance collections — that every other engine layer (storage, ECS, durability, query) is built on. Nearly the entire surface is `internal` engine plumbing (a few types, like `EpochManager` and `IMemoryResource`, are C# `public` but namespaced under `Typhon.Engine.Internals`); the only types meant for direct application use are `Deadline`, `WaitContext`, and `UnitOfWorkContext` — the timeout/cancellation values threaded into every `Commit()`.

> 🔬 **Recommended:** read [in-depth-overview/01-foundation.md](../../in-depth-overview/01-foundation.md) (Chapter 01: Foundation) first to understand the overall design and concepts behind this category, before diving into the specific features below.

## Public Features

| Feature | Summary | Status | Level |
|---|---|---|---|
| [Deadline & Timeout Propagation](./deadline-timeout-propagation.md) | Monotonic absolute-deadline timeouts bundled with cooperative cancellation, threaded through every Unit-of-Work via the 24-byte `UnitOfWorkContext` to eliminate timeout accumulation across nested calls. | ✅ Implemented | 🔵 Core |

## Internal Features

| Feature | Summary | Status |
|---|---|---|
| [Reader-Writer & Resource Lifecycle Locks](./access-control-lock-family/README.md) | CAS-only, allocation-free spin-locks every concurrent engine structure embeds for shared/exclusive or lifecycle access. | ✅ Implemented |
| &nbsp;&nbsp;↳ [AccessControl (full-featured RW lock)](./access-control-lock-family/access-control.md) | 64-bit reader-writer lock with waiter fairness, in-place promotion, and contention tracking. | ✅ Implemented |
| &nbsp;&nbsp;↳ [AccessControlSmall (compact RW lock)](./access-control-lock-family/access-control-small.md) | 32-bit reader-writer lock for thousands of embedded per-node/per-page latches with short critical sections. | ✅ Implemented |
| &nbsp;&nbsp;↳ [ResourceAccessControl (3-mode lifecycle lock)](./access-control-lock-family/resource-access-control.md) | 32-bit Accessing/Modify/Destroy lock where structural growth doesn't block concurrent readers. | ✅ Implemented |
| [Epoch-Based Resource Protection](./epoch-based-resource-protection.md) | Lock-free epoch/grace-period scheme that protects in-flight page-cache pages from eviction with 2 obligations per transaction instead of per-page ref-counting. | ✅ Implemented |
| [High-Resolution Timers](./high-resolution-timers/README.md) | Self-calibrating sub-millisecond periodic timer services (three-phase Sleep→Yield→Spin wait, drift-free metronome scheduling) used for the deadline watchdog, telemetry flush, and epoch advancement, exposing per-tick jitter metrics. | ✅ Implemented |
| &nbsp;&nbsp;↳ [Dedicated Timer (HighResolutionTimerService)](./high-resolution-timers/dedicated-timer.md) | A single periodic callback on its own thread, isolated from every other timer in the engine. | ✅ Implemented |
| &nbsp;&nbsp;↳ [Shared Timer (HighResolutionSharedTimerService)](./high-resolution-timers/shared-timer.md) | One thread multiplexing many periodic callbacks, each at its own rate, waking only when the soonest one is due. | ✅ Implemented |
| [In-Memory Hash Maps](./in-memory-hash-maps/README.md) | Open-addressing, pinned-memory hash set/map types with backward-shift deletion and JIT-specialized hashing that replace .NET `HashSet`/`Dictionary`/`ConcurrentDictionary` on hot paths. | ✅ Implemented |
| &nbsp;&nbsp;↳ [Non-Concurrent HashMap\<TKey[, TValue]\>](./in-memory-hash-maps/non-concurrent-hash-map.md) | Single-threaded open-addressing hash set/map replacing `HashSet`/`Dictionary` on a hot path, zero per-entry GC pressure. | ✅ Implemented |
| &nbsp;&nbsp;↳ [ConcurrentHashMap\<TKey[, TValue]\>](./in-memory-hash-maps/concurrent-hash-map.md) | Striped, lock-free-read hash set/map replacing `ConcurrentDictionary` on a shared hot path, per-stripe CAS writes. | ✅ Implemented |
| [Page-Backed Linear Hash Map](./paged-linear-hash-map.md) | O(1) exact-match key/value index, persisted in fixed-size chunks, with crash-safe rebuild instead of WAL logging. | ✅ Implemented |
| [Memory Allocators](./memory-allocators.md) | Pinned/unmanaged memory primitives that give every page cache, segment, and hash-map structure stable, GC-immune addresses with parent-owned leak tracking. | ✅ Implemented |
| [Concurrent Bitmaps & Collections](./concurrent-bitmaps-collections.md) | Lock-free/CAS-guarded occupancy bitmaps (flat and 3-level) plus a pick/putback slot array for high-contention tracking. | ✅ Implemented |
| [Hardware-Accelerated CRC32C Checksums](./crc32c-checksums.md) | SSE4.2/ARM-intrinsic CRC32C (Castagnoli polynomial) computation with software-table fallback (~1.3µs per 8KB page) — the checksum primitive backing every page-integrity check in storage and durability. | ✅ Implemented |
