---
uid: overview-foundation
title: '01 — Foundation'
description: 'Foundation is the pile of primitives every other engine subsystem stands on: locks, deadlines, epoch-based reclamation, concurrent collections, the memory…'
---

# 01 — Foundation

**Code:** [`src/Typhon.Engine/Foundation/`](https://github.com/Log2n-io/Typhon/tree/main/src/Typhon.Engine/Foundation) (+ [`src/Typhon.Engine/Hosting/`](https://github.com/Log2n-io/Typhon/tree/main/src/Typhon.Engine/Hosting) helpers, folded in §9)

Foundation is the pile of primitives every other engine subsystem stands on: locks, deadlines, epoch-based reclamation, concurrent collections, the memory allocator, and a handful of host-side helpers. Nothing here knows about ECS, MVCC, the WAL, or the scheduler — it's deliberately the bottom of the dependency graph.

You don't need to read this doc front-to-back to *use* Typhon. You'll want it when you're: tuning a workload, writing a new system that touches synchronization, debugging a deadlock or starvation symptom, or reading engine code for the first time. The doc tells you what the primitive is *for*, what it costs, and what guarantees it offers — not how it's implemented bit-for-bit.

<a href="assets/typhon-concurrency-overview.svg">
  <img src="assets/typhon-concurrency-overview.svg" width="1200" alt="Concurrency primitives overview">
</a>
<br>
<sub>Concurrency primitives and their consumers: the three lock types (§1), the wait/cancellation model every blocking entry takes by <code>ref</code> (§2), the timer services that drive deadline-based cancellation (§3), and epoch-based reclamation (§4).</sub>

---

## 1. Synchronization primitives

Three lock types — different bit budgets, different access models. All three encode the holder thread ID in **16 bits**, giving headroom for 65 535 hardware threads (way past current server cores).

### `AccessControl` — 64-bit reader-writer lock

[`Foundation/Concurrency/internals/AccessControl.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Foundation/Concurrency/internals/AccessControl.cs)

Reader-writer with explicit waiter tracking. 8 bytes. Use this when you need both directions and waiter starvation isn't acceptable.

| Bits | Field |
|---|---|
| 0–7 | Shared (reader) count |
| 8–15 | Shared waiters |
| 16–23 | Exclusive waiters |
| 24–31 | Promoter (shared → exclusive) waiters |
| 32–47 | Exclusive holder thread ID (16 bits) |
| 48 | Contention flag (sticky, cleared by `Reset()`) |
| 49–61 | Reserved |
| 62–63 | State: Idle / Shared / Exclusive |

States are mutually exclusive: a lock is either Idle, Shared (one or more readers), or Exclusive (exactly one writer). Promoter waiters get priority over fresh shared waiters to keep upgrade paths from being starved.

**API shape:**

```csharp
ref WaitContext ctx = ref someContext;
if (lock.EnterSharedAccess(ref ctx)) {
    try { /* read */ }
    finally { lock.ExitSharedAccess(); }
}
```

All blocking entries take `ref WaitContext` (see §2). Pass `ref WaitContext.Null` for an infinite, zero-overhead wait. Non-blocking exits and `TryEnter*` don't need a context.

### `AccessControlSmall` — 32-bit, no waiter tracking

[`Foundation/Concurrency/internals/AccessControlSmall.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Foundation/Concurrency/internals/AccessControlSmall.cs)

Compact 4-byte variant. Use this when the lock is small or numerous (per-row, per-bucket) and you don't need waiter accounting.

| Bits | Field |
|---|---|
| 0–14 | Shared counter (max 32 767) |
| 15 | Contention flag |
| 16–31 | Holder thread ID (16 bits) |

No re-entrancy. No promoter logic. Same `WaitContext` integration.

### `ResourceAccessControl` — 3-mode lifecycle lock

[`Foundation/Concurrency/internals/ResourceAccessControl.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Foundation/Concurrency/internals/ResourceAccessControl.cs)

For protecting resources that grow/extend rather than mutate in-place (append-only stores, segment chains). 4 bytes, three modes:

| Mode | Compatible with | Use |
|---|---|---|
| `ACCESSING` (read) | ACCESSING, MODIFY | many readers |
| `MODIFY` (extend) | ACCESSING | one extender + many readers |
| `DESTROY` (terminal) | nothing | one-way transition; never cleared |

Critically: **MODIFY is compatible with ACCESSING**. A writer that's appending to a chain doesn't block readers — only a DESTROY (which is a permanent state, never unwound) does.

| Bits | Field |
|---|---|
| 0–7 | ACCESSING count (max 255) |
| 8–23 | MODIFY holder thread ID (0 = not held) |
| 24 | MODIFY_PENDING (fairness) |
| 25 | DESTROY (terminal) |
| 26 | Contention flag (sticky) |
| 27–31 | Reserved |

### Telemetry

All three primitives emit typed events via [`TyphonEvent.Emit*`](https://github.com/Log2n-io/Typhon/tree/main/src/Typhon.Engine/Observability) calls — the events are JIT-eliminated when gates are off (see [12-observability](12-observability.md)). The hand-rolled `Telemetry` partial of `AccessControl` ([`AccessControl.Telemetry.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Foundation/Concurrency/internals/AccessControl.Telemetry.cs)) still uses `#if TELEMETRY` and is mid-migration.

---

## 2. Wait & deadline model

The whole synchronization layer is built around **monotonic deadlines** rather than relative timeouts. Convert once at the operation entry point, share through nested calls — no time-budget arithmetic, no accumulation, no misuse from re-entry.

### `Deadline`

[`Foundation/Concurrency/public/Deadline.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Foundation/Concurrency/public/Deadline.cs)

`readonly struct Deadline`, 8 bytes. Wraps an absolute `Stopwatch.GetTimestamp()` value. Two sentinels:

- `Deadline.Zero` (= `default`) — **already expired**. Fail-safe default: if you forgot to set one, every wait returns immediately.
- `Deadline.Infinite` — never expires.

Pure integer arithmetic via a precomputed `TickRatio` constant. No floating point anywhere. Throws `PlatformNotSupportedException` at type init if `Stopwatch.Frequency` isn't an integer multiple of `TimeSpan.TicksPerSecond` (Windows x64: 1, Linux: 100 — both supported).

```csharp
var deadline = Deadline.FromTimeout(TimeSpan.FromMilliseconds(500));
while (!deadline.IsExpired) { /* ... */ }
```

`Min(a, b)` returns the tighter deadline — useful when an inner operation has its own budget that must respect the outer one.

### `WaitContext`

[`Foundation/Concurrency/public/WaitContext.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Foundation/Concurrency/public/WaitContext.cs)

`readonly struct WaitContext`, 16 bytes: `Deadline` + `CancellationToken`. This is the *real* parameter passed to every blocking lock primitive.

```csharp
ref WaitContext ctx = ref WaitContext.Null;        // infinite, zero overhead
WaitContext.FromTimeout(TimeSpan.FromMilliseconds(100));
WaitContext.FromToken(cancellationToken);
WaitContext.FromTimeout(timeout, token);
```

`WaitContext.Null` is a `ref` to a null `WaitContext` (via `Unsafe.NullRef`). Lock primitives check `Unsafe.IsNullRef(ref ctx)` once at entry and skip all deadline/cancellation work when null — the fast path costs nothing.

`ShouldStop` returns true when either the deadline expired or the token was cancelled — called once per spin iteration.

### `UnitOfWorkContext`

[`Foundation/Concurrency/public/UnitOfWorkContext.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Foundation/Concurrency/public/UnitOfWorkContext.cs)

`struct UnitOfWorkContext`, 24 bytes. Embeds a `WaitContext` (16 B) + `UowId` (2 B) + holdoff counter (4 B). Passed by `ref` through every operation inside a Unit of Work.

Lock sites pass `ref ctx.WaitContext` directly — no construction cost, the JIT resolves struct field offsets at compile time. The `UowId` is what gets stamped on revision elements for snapshot isolation ([05-revision](05-revision.md)).

`ThrowIfCancelled()` is the cooperative cancellation check — call at yield points. It throws `TyphonTimeoutException` on expired deadline, `OperationCanceledException` on cancelled token. **Holdoff regions suppress it** (see below).

### `HoldoffScope` — defer cancellation across critical sections

[`Foundation/Concurrency/internals/HoldoffScope.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Foundation/Concurrency/internals/HoldoffScope.cs)

Some sequences must not be cancelled mid-flight — a B+Tree node split, a chain unlink. Enter a holdoff and `ThrowIfCancelled` becomes a no-op until you exit. Nests.

```csharp
using var holdoff = ctx.EnterHoldoff();
SplitBTreeNode(ref ctx);   // cancellation deferred
// holdoff.Dispose() decrements the counter; cancellation re-enabled
```

`HoldoffScope` is a `ref struct` over a `ref UnitOfWorkContext` field — no heap allocation, no boxing.

### `BackpressureContext`

[`Foundation/Concurrency/internals/BackpressureContext.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Foundation/Concurrency/internals/BackpressureContext.cs)

24-ish-byte struct: resource path + `WaitContext` + retry count. Each bounded resource (WAL ring, page cache, transaction pool) creates one at entry and passes it by ref through retry iterations. `ShouldGiveUp` delegates to `WaitContext.ShouldStop`. Pure plumbing for "wait then retry, give up at deadline".

### `AdaptiveWaiter`

[`Foundation/Concurrency/internals/AdaptiveWaiter.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Foundation/Concurrency/internals/AdaptiveWaiter.cs)

Wraps .NET's `SpinWait` with `WaitContext` integration. Progression:

1. First ~10 iterations: `Thread.SpinWait(N)` with increasing N (PAUSE on x86)
2. Then: `Thread.Yield()` (give up timeslice)
3. Then: `Thread.Sleep(0)` (yield to any ready thread)
4. Then: `Thread.Sleep(1)` (~1 ms — real CPU relief)

Two entry points: `Wait(ref WaitContext)` returns `false` when `ShouldStop`, or `Wait()` for the caller-checks-outer-loop pattern. Emits a `Concurrency:AdaptiveWaiter:YieldOrSleep` event on the transition from spin → yield (once per Wait sequence, not per spin).

Must not be copied after first use — `_spinner.Count` tracks progression.

---

## 3. Timers

[`Foundation/Concurrency/internals/HighResolutionTimerService.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Foundation/Concurrency/internals/HighResolutionTimerService.cs), [`HighResolutionSharedTimerService.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Foundation/Concurrency/internals/HighResolutionSharedTimerService.cs)

Two flavours of high-resolution timer:

| Service | Thread model | Use |
|---|---|---|
| `HighResolutionSharedTimerService` | One background thread multiplexes N callbacks via priority queue | Non-critical periodic tasks (telemetry, watchdogs, cleanup) |
| `HighResolutionTimerService` | One dedicated thread per registration | Safety-critical handlers needing isolation |

Both wake at the nearest next callback, no wasted ticks. The shared service's callback contract: target <100 µs execution, no blocking calls — a slow callback delays every subsequent callback in that cycle. The service tracks a `SlowInvocationCount` per registration (>100 µs).

[`DeadlineWatchdog`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Foundation/Concurrency/internals/DeadlineWatchdog.cs) is the canonical consumer: registered with the shared timer at **200 Hz (5 ms)**, scans a priority queue of `(Deadline, CancellationTokenSource)` pairs, fires cancellation when the deadline passes. No dedicated thread of its own. Registration is lazy — if no deadlines are ever registered, no timer overhead is incurred.

`ITimerRegistration` ([definition](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Foundation/Concurrency/internals/ITimerRegistration.cs)) is the disposable handle: name, interval, invocation count, last/max duration, slow count, IsActive.

---

## 4. Epoch-based reclamation

Typhon uses an epoch model for safe memory and page reclamation. The page cache cannot evict a page that any active thread might still be reading from. The mechanism is per-thread: each thread "pins" the current global epoch when it starts a critical region; pages tagged with an epoch ≥ the minimum pinned epoch are protected.

### `EpochManager`

[`Foundation/Concurrency/internals/EpochManager.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Foundation/Concurrency/internals/EpochManager.cs) — one per `DatabaseEngine`.

Holds the `GlobalEpoch` (monotonic `long`, starts at 1 — `0` means "not pinned"). Exposes:

- `MinActiveEpoch` — pages tagged ≥ this value cannot be evicted.
- `GlobalEpoch` — current value, advanced when the outermost scope on any thread exits.
- `EnterScope() / ExitScope() / RefreshScope()` — scope management.
- `ExitScopeUnordered()` — used by `Transaction`, whose scopes can be disposed in any order (not LIFO).

Implements `IResource` + `IMetricSource` — exposes capacity (`_activeSlotCount / MaxSlots`).

### `EpochGuard`

[`Foundation/Concurrency/internals/EpochGuard.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Foundation/Concurrency/internals/EpochGuard.cs)

`ref struct` RAII handle. Always use in a `using` block.

```csharp
using var guard = EpochGuard.Enter(epochManager);
// guard.Epoch is the global epoch captured atomically at entry
// pages tagged ≥ guard.Epoch are now protected
```

Outermost `Dispose` advances the global epoch. Nested scopes don't — they share the outermost pin.

### `EpochThreadRegistry` + `PaddedEpochSlot`

[`Foundation/Concurrency/internals/EpochThreadRegistry.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Foundation/Concurrency/internals/EpochThreadRegistry.cs)

Fixed array of 256 per-thread slots. Each slot is a `PaddedEpochSlot` — **64 bytes wide** (one cache line) to eliminate false sharing between threads pinning concurrently:

```csharp
[StructLayout(LayoutKind.Explicit, Size = 64)]
internal struct PaddedEpochSlot {
    [FieldOffset(0)]  public long PinnedEpoch;  // hot
    [FieldOffset(8)]  public int  Depth;         // warm
    [FieldOffset(12)] public int  SlotState;     // CAS target
    // bytes 16-63: padding
}
```

256 × 64 B = 16 KB total. Slots are claimed lazily (on first use per thread) via a `[ThreadStatic]` slot index. An `EpochSlotHandle` rooted in `[ThreadStatic]` (`CriticalFinalizerObject`) releases the slot on thread death.

A thread can only be registered in one registry at a time — encountering a different registry (e.g., in tests with multiple `DatabaseEngine` instances) triggers re-registration. Production has a single `DatabaseEngine` per process (per ADR), so this is test-only.

---

## 5. False-sharing avoidance

[`Foundation/Concurrency/internals/CacheLinePadded.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Foundation/Concurrency/internals/CacheLinePadded.cs)

```csharp
[StructLayout(LayoutKind.Explicit, Size = 64)]
internal struct CacheLinePaddedInt   { [FieldOffset(0)] public int  Value; }
internal struct CacheLinePaddedLong  { [FieldOffset(0)] public long Value; }
```

Use these wherever multiple threads atomically modify adjacent fields and you'd otherwise see cache-line ping-pong. For `Interlocked` ops, pass `ref field.Value`. The 64-byte width matches the modern x86 cache line; ARM/Apple Silicon also fits within this bound.

This is non-negotiable in hot-path code: any time you put independently-mutated atomic state in an array, each element pads to ≥ 64 B.

---

## 6. Collections

All collections live under [`Foundation/Collections/internals/`](https://github.com/Log2n-io/Typhon/tree/main/src/Typhon.Engine/Foundation/Collections/internals). They're `internal` types — exposed to engine code, not part of the public surface — but worth knowing if you're reading engine internals.

### Hash maps

| Type | Shape | Use |
|---|---|---|
| `HashMap<TKey>` | Set of unmanaged keys, open addressing + linear probing | Fast in-memory set lookup; entries on Pinned Object Heap (no GC moves) |
| `HashMap<TKey, TValue>` | Same with values | KV variant |
| `ConcurrentHashMap<TKey>` | Lock-free / lock-striped variant | Multi-writer maps |
| `ConcurrentHashMap<TKey, TValue>` | Same KV | Multi-writer KV |
| `PagedHashMap<…>` / `PagedHashMapBase<TStore>` | Hash map backed by Typhon's paged store ([02-storage](02-storage.md)) | Persistent in-DB hash maps (component fields, indexes) |
| `RawValueHashMap` + `IRawValueUpdater` | Hash map where values are raw byte ranges with custom update logic | Specialized internal use |

`HashMap<T>` uses POH (Pinned Object Heap) allocation + software prefetch on resize, backward-shift deletion to avoid tombstones, 0.75 max load factor. `Capacity` is rounded up to a power of two.

### Concurrent containers

| Type | Use |
|---|---|
| `ConcurrentArray<T>` (class) | Fixed-size array where workers exclusively own elements (`Pick` reserves, `PutBack`/`Remove` releases). Producer adds, consumers iterate. |
| `ConcurrentCollection<T>` | General-purpose growable concurrent collection |

### Bitmaps

| Type | Use |
|---|---|
| `BitmapL3Any` (non-concurrent) | 3-level summarized bitmap; query "is any bit set?" in O(1) at the top level |
| `ConcurrentBitmap` | Basic concurrent bit set |
| `ConcurrentBitmapL3Any` | Concurrent 3-level "any" bitmap |
| `ConcurrentBitmapL3All` | Concurrent 3-level "all" bitmap (implements `IResource` + `IMetricSource`) |

The L3 bitmaps are the underlying mechanism for fast occupancy queries in [02-storage](02-storage.md) (free-page tracking).

### Utilities

- [`HashUtils`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Foundation/Collections/internals/HashUtils.cs) — common hashing helpers.

---

## 7. Memory allocator

[`Foundation/Memory/internals/`](https://github.com/Log2n-io/Typhon/tree/main/src/Typhon.Engine/Foundation/Memory/internals)

The allocator is a tracked, observable resource — every allocation goes through `MemoryAllocator` and gets accounted for in the resource graph ([13-resources](13-resources.md)).

### `MemoryAllocator`

[`MemoryAllocator.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Foundation/Memory/internals/MemoryAllocator.cs)

Two primary entry points:

```csharp
MemoryBlockArray AllocateArray(string id, IResource parent, int size, bool zeroed, ushort sourceTag);
PinnedMemoryBlock AllocatePinned(string id, IResource parent, int size, bool zeroed, int alignment, ushort sourceTag);
```

`MemoryBlockArray` is a managed `byte[]` exposed as `Memory<byte>`. `PinnedMemoryBlock` is unmanaged (`NativeMemory.Alloc` + optional alignment), survives GC, suitable for memory-mapped files and FFI.

Tracks `_totalAllocatedBytes`, `_peakAllocatedBytes`, `_cumulativeAllocations`, `_cumulativeDeallocations` (grand totals) and `_pinnedBytes`, `_peakPinnedBytes`, `_pinnedLiveBlocks` (unmanaged-only). The grand totals feed the resource graph; the pinned-only counters back the `MemoryUnmanagedTotalBytes / PeakBytes / LiveBlocks` gauges.

`IMemoryResource` ([interface](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Foundation/Memory/internals/IMemoryResource.cs)) is what owners implement to report their own `EstimatedMemorySize` — a separate channel from the allocator's bookkeeping (used by types that aren't allocator-tracked, like in-engine pools).

### Block & struct allocators

Layered on top of `MemoryAllocator`:

- [`BlockAllocator`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Foundation/Memory/internals/BlockAllocator.cs) / [`BlockAllocatorBase`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Foundation/Memory/internals/BlockAllocatorBase.cs) — fixed-size block pools
- [`ChainedBlockAllocator`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Foundation/Memory/internals/ChainedBlockAllocator.cs) — chain of blocks for variable-size sequences
- [`StructAllocator`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Foundation/Memory/internals/StructAllocator.cs) / [`UnmanagedStructAllocator`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Foundation/Memory/internals/UnmanagedStructAllocator.cs) — typed slab allocators for `unmanaged` value types
- [`MemoryBlockArray`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Foundation/Memory/internals/MemoryBlockArray.cs) / [`PinnedMemoryBlock`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Foundation/Memory/internals/PinnedMemoryBlock.cs) — block handles
- [`StoreSpan`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Foundation/Memory/internals/StoreSpan.cs) — span helpers over the above

These are the building blocks Typhon's persistence layer ([02-storage](02-storage.md)) uses to materialize page-backed data structures in unmanaged memory.

---

## 8. Hosting helpers

[`Hosting/`](https://github.com/Log2n-io/Typhon/tree/main/src/Typhon.Engine/Hosting) — small, used everywhere.

### `SpanStream` and span helpers

[`Hosting/public/String64.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Hosting/public/String64.cs) (file also hosts `MathHelpers`, `StringExtensions`)

`ref struct SpanStream` — zero-alloc cursor over a `Span<byte>` for reading/writing serialized payloads. `PopSpan(n)`, `PopRef<T>()`, `Pop<T>()`. Used in WAL serialization, schema persistence, network encode paths.

`StringExtensions.StoreString / LoadString` — fixed-width string encode/decode.

### `MathExtensions`

[`Hosting/public/MathExtensions.cs`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Engine/Hosting/public/MathExtensions.cs)

Friendly formatters: `FriendlySize(bytes)`, `FriendlyAmount(count)`, `FriendlyTime(timespan)`, `Bandwidth(bytes, duration)`, `TicksToSeconds`, `TotalSeconds`. Plus power-of-two helpers (`IsPowerOf2`, `NextPowerOf2`).

### Ownership attributes

[`Hosting/internals/`](https://github.com/Log2n-io/Typhon/tree/main/src/Typhon.Engine/Hosting/internals) — `[NoCopy]`, `[AllowCopy]`, `[TransfersOwnership]`. Markers that document ownership invariants on struct types (e.g., `UnitOfWorkContext` is `[NoCopy]`). Not enforced by the compiler, but used by code review and the engine's own analyzers.

---

## 9. Schema definition types (sibling project)

Some types Typhon engine users *will* touch don't live in `Typhon.Engine` — they live in the sibling [`Typhon.Schema.Definition`](https://github.com/Log2n-io/Typhon/tree/main/src/Typhon.Schema.Definition) project. This is the package you reference when defining components.

| Type | What it is |
|---|---|
| [`String64`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Schema.Definition/String64.cs) | 64-byte fixed-width inline string (no GC, blittable) |
| [`Variant`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Schema.Definition/Variant.cs) | Tagged-union value for runtime-typed fields |
| [`PackedDateTime`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Schema.Definition/PackedDateTime.cs) | Compact 8-byte timestamp |
| [`SpatialTypes`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Schema.Definition/SpatialTypes.cs) (`Point2F/3F/4F/2D/3D/4D`, `QuaternionF/D`, AABB/BSphere variants) | Geometric primitive types |
| [`ISpatialBox`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Schema.Definition/ISpatialBox.cs) | Marker interface for spatial query payloads |
| [`FieldType`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Schema.Definition/FieldType.cs) | Enum of supported component field kinds (full set incl. `Unsigned` / `DoubleFloat` flags and AABB/BSphere variants) |
| [`Attributes`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Schema.Definition/Attributes.cs) | Component / field attributes (`[SpatialIndex]`, `[Unique]`, etc.) |
| [`StorageMode`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Schema.Definition/StorageMode.cs) | Per-component storage policy: Versioned / SingleVersion / Transient |
| [`DurabilityDiscipline`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Schema.Definition/DurabilityDiscipline.cs) | Per-transaction escalation for `SingleVersion` components: `TickFence` (default, batched) or `Commit` (atomic, zero-loss, O(1) rollback) — see [06-ecs §8](06-ecs.md) |
| [`MurmurHash2`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Schema.Definition/MurmurHash2.cs) | Hash algorithm used in keying / partitioning |
| [`ComponentCollection`](https://github.com/Log2n-io/Typhon/blob/main/src/Typhon.Schema.Definition/ComponentCollection.cs) | Container type for component metadata |

These exist in a separate project because schema definitions need to be referenced by both the engine and external code-generators / tooling without pulling in the full engine surface. See [04-schema](04-schema.md) for how the engine consumes them.

---

## See also

- [02-storage](02-storage.md) — page cache, segments, dirty tracking (consumers of Memory + EpochManager)
- [05-revision](05-revision.md) — MVCC mechanics (consumers of `UowId` from `UnitOfWorkContext`)
- [08-transactions](08-transactions.md) — UoW lifecycle (creates the `UnitOfWorkContext`)
- [13-resources](13-resources.md) — resource graph (how `MemoryAllocator`, `EpochManager`, timer services register themselves)
