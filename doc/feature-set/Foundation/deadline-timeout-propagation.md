---
uid: feature-foundation-deadline-timeout-propagation
title: 'Deadline & Timeout Propagation'
description: 'Monotonic absolute deadlines bundled with cooperative cancellation, shared by reference through every nested call so timeouts never accumulate.'
---

# Deadline & Timeout Propagation
> Monotonic absolute deadlines bundled with cooperative cancellation, shared by reference through every nested call so timeouts never accumulate.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Foundation](./README.md)

## 🎯 What it solves

Relative timeouts compound: a 5-second budget handed to three nested calls, each restarting its own 5-second clock, can take 15 seconds in the worst case — the original "limit" was meaningless. Wall-clock timeouts (`DateTime.UtcNow`) compound the problem further: NTP sync or DST can jump the clock backward or forward, making a timeout expire instantly or never. Typhon's lock primitives and transactions operate at microsecond granularity, where this drift is the difference between a sub-millisecond commit and a thread that hangs indefinitely. Deadline propagation converts a caller's relative timeout into an absolute, monotonic endpoint exactly once, then threads it — together with a cooperative cancellation signal — through every nested operation without re-deriving it.

## ⚙️ How it works (in brief)

`Deadline` is an 8-byte readonly struct wrapping a `Stopwatch.GetTimestamp()` value; `Deadline.FromTimeout(timeout)` converts a relative `TimeSpan` to an absolute deadline once, at the operation's entry point. `WaitContext` (16 bytes) bundles a `Deadline` with a `CancellationToken` and is passed `ref` to every blocking `Enter*` call on Typhon's lock primitives; its `ShouldStop` property is the single check performed once per spin iteration. `UnitOfWorkContext` (24 bytes) embeds a `WaitContext` plus a Unit-of-Work identifier and a holdoff counter, and flows by `ref` through an entire Unit of Work or transaction; `ThrowIfCancelled()` is the cooperative yield-point check, and the holdoff counter — incremented/decremented around critical sections — suppresses cancellation mid-section without disabling the deadline itself.

## 💻 Usage

```csharp
using var uow = db.CreateUnitOfWork();
using var tx = uow.CreateTransaction();

// One conversion, at the top: a 5s relative timeout becomes an absolute monotonic deadline.
var ctx = UnitOfWorkContext.FromTimeout(TimeSpan.FromSeconds(5));

UpdateAccountBalance(tx, accountId, newBalance);

try
{
    tx.Commit(ref ctx);   // every lock acquired during commit shares ctx's single deadline
}
catch (TyphonTimeoutException)
{
    // ctx's deadline expired before commit finished — no partial work was left behind.
}
```

| Option | Default | Effect |
|---|---|---|
| `Deadline.FromTimeout(Timeout.InfiniteTimeSpan)` | — | Returns `Deadline.Infinite` — never expires |
| `Deadline.FromTimeout(TimeSpan.Zero)` / negative | — | Returns `Deadline.Zero` — already expired, fails immediately |
| `UnitOfWorkContext.None` | — | Infinite deadline, no cancellation — for internal cleanup/rollback paths only |

## ⚠️ Guarantees & limits

- **No accumulation** — the relative-to-absolute conversion happens once at the entry point; every nested call checks the same absolute endpoint, so total elapsed time is bounded by the original timeout regardless of call depth.
- **Monotonic, not wall-clock** — built on `Stopwatch.GetTimestamp()`; immune to NTP adjustments, DST transitions, and manual clock changes that would make a `DateTime.UtcNow`-based timeout expire early or never.
- **Fail-safe default** — `default(Deadline)`, `default(WaitContext)`, and `default(UnitOfWorkContext)` are all already-expired; a forgotten initialization fails fast instead of hanging forever. Unbounded waits require explicitly opting in via `Deadline.Infinite`, `WaitContext.Null`, or `UnitOfWorkContext.None`.
- **~10-25ns per expiry check** — one `Stopwatch.GetTimestamp()` (effectively a single `RDTSC` on modern x64) plus a comparison; the cancellation check short-circuits to near-free when no token is attached.
- **Holdoff defers, never disables** — `BeginHoldoff()`/`EndHoldoff()` make `ThrowIfCancelled()` a no-op for the duration of a critical section so a timeout can't abort work partway through; the deadline keeps running underneath and is re-checked the instant the holdoff exits.
- **`Deadline.ToCancellationToken()` allocates** — bridges to a `CancellationTokenSource` + timer for interop with non-Typhon async APIs (e.g. `HttpClient`); not for use in spin loops or per-iteration hot paths.
- A background watchdog polls registered deadlines at 200Hz (~5ms resolution) to fire `CancellationToken`s for code paths that need to observe expiry without spinning — an internal mechanism, not directly exposed.

## 🧪 Tests
- [DeadlineTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Concurrency/DeadlineTests.cs) — monotonic expiry: default/zero/infinite states, `FromTimeout` conversion, `Min()` composition.
- [WaitContextTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Concurrency/WaitContextTests.cs) — deadline+cancellation composition, `ShouldStop` short-circuiting, `FromTimeout`/`FromToken` construction.
- [UnitOfWorkContextTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Concurrency/UnitOfWorkContextTests.cs) — 24-byte struct layout, `ThrowIfCancelled`, holdoff begin/end nesting that suppresses cancellation without disabling the deadline.
- [TransactionUnitOfWorkContextTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Concurrency/TransactionUnitOfWorkContextTests.cs) — `Commit(ref ctx)`/`Rollback(ref ctx)` propagation end-to-end, expired-deadline and cancelled-token paths matching the usage sample above.

## 🔗 Related

- Sibling: [High-Resolution Timers](./high-resolution-timers/README.md) — the deadline watchdog polls registered deadlines via the shared timer at 200Hz.
- Sibling: [Deadline & Cooperative Cancellation](../Transactions/deadline-cancellation.md) — applies this deadline/cancellation primitive to transaction commit.
- Sibling: [Timeout Exceptions & Deadline Propagation](../Errors/timeout-exceptions-deadlines.md) — surfaces deadline expiry as typed, catchable exceptions.

<!-- Deep dive: claude/design/Foundation/Concurrency/Deadline.md, claude/design/Foundation/Concurrency/WaitContext.md -->
<!-- Overview: claude/overview/01-concurrency.md §1.1-1.2, §1.6 -->
<!-- ADRs: 031 — Unified Concurrency Patterns (claude/adr/031-unified-concurrency-patterns.md), 034 — UnitOfWorkContext Struct Design (claude/adr/034-unitofworkcontext-struct-design.md) -->
