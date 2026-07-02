# ResourceAccessControl (3-mode lifecycle lock)
> 32-bit Accessing/Modify/Destroy lock where structural growth doesn't block concurrent readers.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Foundation](../README.md)

## 🎯 What it solves
Append-only and extend-only structures (chained block allocators, enumerable segment chains) have an access pattern a plain reader-writer lock handles badly: appending a new block doesn't invalidate anything an in-flight enumerator is reading, so a writer-style exclusive lock would block readers for no reason — but tearing the structure down still must wait for every reader and every in-flight modification to finish. ResourceAccessControl encodes that compatibility relationship directly instead of forcing it through two RW locks that don't agree with each other.

## ⚙️ How it works (in brief)
Three modes, not two: **Accessing** (many concurrent holders, keeps the resource alive), **Modify** (single holder, but compatible with concurrent Accessing — it only excludes other Modify/Destroy), and **Destroy** (terminal, drains everything, never released). A `MODIFY_PENDING` flag stops new Accessing holders from starving a Modify (or a promote) once it starts waiting for the count to drain; `TryPromoteToModify`/`DemoteFromModify` let an Accessing holder upgrade in place. Once `EnterDestroy` succeeds the instance is permanently dead — there is no `ExitDestroy`.

## 💻 Usage
```csharp
private ResourceAccessControl _access;

// Reader/enumerator: keeps the resource alive, doesn't block appenders
_access.EnterAccessing(ref WaitContext.Null);
try { /* traverse the chain */ }
finally { _access.ExitAccessing(); }

// Same, via the scoped guard (auto-exits on Dispose)
using (_access.EnterAccessingScoped(ref WaitContext.Null))
{
    /* traverse the chain */
}

// Append/extend: compatible with concurrent Accessing, exclusive vs. other Modify
_access.EnterModify(ref WaitContext.Null);
try { /* append a new block */ }
finally { _access.ExitModify(); }

// Teardown: terminal, waits for every Accessing/Modify holder to drain first
var ctx = WaitContext.FromTimeout(TimeSpan.FromSeconds(30));
if (!_access.EnterDestroy(ref ctx))
{
    throw new TimeoutException("Could not acquire destroy lock");
}
// _access is now permanently dead — no further Enter* call will ever succeed
```

| Mode | Concurrent with Accessing | Concurrent with Modify | Concurrent with Destroy |
|---|---|---|---|
| Accessing | Yes (many) | Yes | No |
| Modify | Yes | No (single holder) | No |
| Destroy | No | No | No (terminal) |

## ⚠️ Guarantees & limits
- 4 bytes per instance.
- Modify being compatible with Accessing is the type's whole point — don't reach for it as a generic RW-lock substitute where modification must exclude readers.
- Destroy is one-way: once acquired, the instance cannot be reused; there's no `Reset()`-and-retry within the same lifetime.
- Max 255 concurrent Accessing holders (8-bit counter); overflow throws `InvalidOperationException`.
- `MODIFY_PENDING` is the only fairness mechanism — it protects a waiting Modify/promote from new Accessing holders, not from another Modify.
- `internal` type — engine plumbing, not callable from application code.

## 🧪 Tests
- [ResourceAccessControlTests](../../../../test/Typhon.Engine.Tests/Concurrency/ResourceAccessControlTests.cs) — Accessing/Modify compatibility, `MODIFY_PENDING` blocking new Accessing while a Modify/promote drains, promote/demote, Destroy's terminal drain-then-die semantics.

## 🔗 Related
- Parent feature: [Reader-Writer & Resource Lifecycle Locks](./README.md)
- Sibling: [AccessControl](./access-control.md) — full-featured RW lock with waiter fairness, for low-instance-count high-contention resources.
- Sibling: [AccessControlSmall](./access-control-small.md) — compact 4-byte variant for thousands of embedded per-node/per-page latches.

<!-- Deep dive: claude/design/Foundation/Concurrency/ResourceAccessControl.md, claude/adr/016-three-mode-resource-access-control.md -->
