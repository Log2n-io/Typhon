# IsTransient Retry Hint
> A virtual flag on every Typhon exception telling callers "this might succeed if you retry" — without the engine ever retrying for you.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Errors](./README.md)

## 🎯 What it solves

When an operation fails, the caller's next move depends entirely on *why* it failed. A duplicate key in a unique index will fail again no matter how many times you retry it; a lock acquisition that timed out under contention might well succeed a moment later. Without a uniform way to tell these apart, callers either retry everything (wasting time on failures that can never succeed) or retry nothing (giving up on contention that would have cleared in milliseconds). Typhon needs a single, consistent signal across its entire exception hierarchy — and it must never tempt the engine itself into silently retrying a transaction, because only the caller knows what side effects already happened.

## ⚙️ How it works (in brief)

`TyphonException` declares `virtual bool IsTransient => false`. Subclasses override it only when retrying is actually meaningful: `TyphonTimeoutException` (and everything under it — lock timeouts, transaction timeouts, page-cache and WAL back-pressure) overrides it to `true`, since a timeout means a resource was busy, not broken. `ResourceExhaustedException` likewise overrides it to `true` — pools and bounded resources drain over time. Everything else (corruption, schema mismatches, unique-constraint violations, WAL write failures) keeps the `false` default: retrying changes nothing because the cause isn't time-dependent. The flag is purely informational — Typhon never reads it internally to drive a retry loop.

## 💻 Usage

```csharp
int attempts = 0;
while (true)
{
    try
    {
        using var tx = dbe.CreateQuickTransaction();
        var id = tx.Spawn<PlayerArch>(PlayerArch.Position.Set(in pos));
        tx.Commit();

        // Side effects AFTER commit only — never re-executed on retry.
        networkLayer.BroadcastSpawn(id);
        break;
    }
    catch (TyphonException ex) when (ex.IsTransient && ++attempts < 3)
    {
        // Caller decides: how many attempts, how long to wait, what to undo.
        Thread.Sleep(50 * attempts);
    }
}
```

| Exception | `IsTransient` | Typical retry response |
|---|---|---|
| `TyphonTimeoutException` (lock, transaction, page-cache/WAL back-pressure) | `true` | Retry with backoff |
| `ResourceExhaustedException` | `true` | Retry, or wait for the resource to drain |
| `StorageException`, `DurabilityException`, schema/`UniqueConstraintViolationException` | `false` | Don't retry — surface or escalate |

## ⚠️ Guarantees & limits

- **Caller-owned retry, always** — the engine never loops on a failed transaction internally; it throws once and returns control. Retrying is entirely the caller's decision and implementation.
- **Default is non-transient** — a new exception type that forgets to override `IsTransient` fails safe (no retry suggested) rather than encouraging callers to hammer a permanent failure.
- **Only opt-in subclasses are transient** — `TyphonTimeoutException` and `ResourceExhaustedException` are the sole overrides today; the flag is fixed per exception type, not computed from runtime state.
- **Not a substitute for idempotency analysis** — `IsTransient == true` only means "the resource might be free now," not "it's safe to blindly retry your lambda." Side effects performed before the failure point (network calls, external writes) are never undone by a transaction retry; place them after `Commit()`.
- **Zero runtime cost** — a single virtual property read (no allocation, no dictionary lookup); the type hierarchy itself is the classification, there's no separate `ErrorCategory` enum to keep in sync.

## 🧪 Tests

- [TyphonExceptionTests](../../../test/Typhon.Engine.Tests/Errors/TyphonExceptionTests.cs) — default-`false` on the base `TyphonException`, opt-in `true` overrides on `LockTimeoutException`/`ResourceExhaustedException`, and non-transient assertions on `StorageException`/`CorruptionException`.

## 🔗 Related

- Parent feature: [Errors](./README.md)
- Sibling: [TyphonException Hierarchy & Catalog](./exception-hierarchy.md) — defines the base `IsTransient` flag this feature documents
- Sibling: [Timeout Exceptions & Deadline Propagation](./timeout-exceptions-deadlines.md) — the primary `IsTransient=true` exception family
- Sibling: [Resource Exhaustion Handling](./resource-exhaustion-handling.md) — the other exception family that overrides `IsTransient=true`

<!-- Deep dive: claude/design/Errors/01-exception-hierarchy.md, claude/design/Errors/05-public-exception-catalog.md -->
<!-- Overview: claude/overview/10-errors.md §10.3 -->
