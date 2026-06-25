# 14 — Errors

**Code:** [`src/Typhon.Engine/Errors/`](../../src/Typhon.Engine/Errors/) (+ [`ResourceExhaustedException`](../../src/Typhon.Engine/Resources/public/ResourceExhaustedException.cs) under `Resources/`, two status enums cited in §5)

Errors is the smallest subsystem in Typhon — one folder, a couple dozen files — but every other subsystem terminates here. This chapter documents the contract: the exception hierarchy, the numeric error codes, the zero-allocation `Result<,>` pattern for hot paths, and a few invariants you'll want to know before you write a `catch` block.

<a href="assets/typhon-error-escalation.svg">
  <img src="assets/typhon-error-escalation.svg" width="1200" alt="Typhon error model — throw, don't retry">
</a>
<br>
<sub>The two-channel error model: routine non-success outcomes (not-found, not-visible) return a zero-cost <code>Result&lt;TValue, TStatus&gt;</code>; exceptional conditions throw via <code>ThrowHelper</code> → <code>TyphonException</code> (carrying <code>ErrorCode</code> + <code>IsTransient</code>). The engine never retries — callers route on <code>IsTransient</code> (transient → backoff/retry/drop; terminal → log/give up; <code>WalWriteException</code> fail-fast (per ADR) → restart).</sub>

---

## 1. Overview — throw, don't retry

The engine layer follows one rule: **throw, never retry**. When a Typhon operation fails — a lock timed out, the page cache is full, the WAL fsync threw `IOException` — the engine propagates the exception to the caller and stops. There is no automatic backoff, no transparent transaction restart, no internal retry loop disguised as a successful return.

Why: retry policy is a *caller* concern. A game server treats a `LockTimeoutException` as "skip this tick"; a batch job treats it as "wait and try again"; a test asserts it never happens. None of those policies belong inside the engine.

To make caller-side retry decisions cheap, every Typhon exception carries a **transience hint** — a `virtual bool IsTransient` property defaulting to `false`. Subclasses opt in:

| Marker | Subclasses | Meaning |
|---|---|---|
| `IsTransient => false` (default) | `TyphonException`, `StorageException`, `CorruptionException`, `WalWriteException`, `WalClaimTooLargeException`, … | Terminal — retrying without action will produce the same result. |
| `IsTransient => true` | `TyphonTimeoutException` (and all four timeout subclasses), `ResourceExhaustedException` | The resource may free up. Retry is *meaningful* — though still the caller's choice. |

```csharp
try
{
    using var tx = uow.CreateTransaction();
    tx.Spawn<Ant>(/* ... */);
    tx.Commit();
}
catch (TyphonException ex) when (ex.IsTransient)
{
    // backoff, retry, or drop — caller's policy
}
catch (TyphonException ex)
{
    // terminal — log, alert, give up
    logger.LogError(ex, "Engine error {Code}", ex.ErrorCode);
    throw;
}
```

Two more rules worth knowing up front:

- **`TyphonTimeoutException` does *not* inherit from `System.TimeoutException`**. Single-inheritance forces a choice; Typhon picks structured `TyphonException` over BCL `TimeoutException` so the error-code / transience contract is uniform. Catch `TyphonTimeoutException` for all engine timeouts.
- **No `#nullable enable`**. Typhon doesn't use C# nullable reference types — exception properties are plain reference types. `null` checks happen at construction (`ArgumentNullException.ThrowIfNull`) where they matter.

---

## 2. Exception hierarchy

All public exception types live in [`Errors/public/`](../../src/Typhon.Engine/Errors/public/) (one exception: `ResourceExhaustedException` lives under `Resources/public/` because it ships with the resource graph it describes).

```
System.Exception
└─ TyphonException                          (base; ErrorCode, virtual IsTransient => false)
   ├─ TyphonTimeoutException                (WaitDuration; IsTransient => true)
   │  ├─ LockTimeoutException               (ResourceName)
   │  ├─ TransactionTimeoutException        (TransactionId)
   │  ├─ PageCacheBackpressureTimeoutException (DirtyPageCount, EpochProtectedCount)
   │  └─ WalBackPressureTimeoutException    (RequestedBytes)            ← NOT under DurabilityException
   ├─ StorageException
   │  ├─ CorruptionException                (ComponentName, PageIndex)
   │  │  └─ PageCorruptionException         (ExpectedCrc, ComputedCrc)
   │  └─ DatabaseLockedException            (OwnerPid, OwnerMachine, StartedAt)
   ├─ DurabilityException
   │  ├─ WalWriteException                  (fail-fast per ADR; engine stops accepting durable commits)
   │  ├─ WalClaimTooLargeException          (RequestedBytes, BufferCapacity)
   │  └─ WalSegmentException                (SegmentPath)
   ├─ ResourceExhaustedException            (direct subclass; IsTransient => true)
   ├─ UniqueConstraintViolationException    (parameterless ctor only — currently)
   ├─ SchemaValidationException             (Diff: SchemaDiff)
   ├─ SchemaMigrationException              (ComponentName, IReadOnlyList<MigrationFailure>)
   ├─ SchemaDowngradeException              (ComponentName, PersistedRevision, RuntimeRevision)
   └─ InvalidAccessException                (sealed; DEBUG-only declared-access enforcement)
```

### Base — `TyphonException`

[`Errors/public/TyphonException.cs`](../../src/Typhon.Engine/Errors/public/TyphonException.cs)

```csharp
public class TyphonException : Exception
{
    public TyphonErrorCode ErrorCode { get; }
    public virtual bool    IsTransient => false;   // subclasses opt in
}
```

Every engine exception derives from this and carries an `ErrorCode`. The numeric code lets logs and telemetry classify failures without string-matching message text.

### Timeout family — `TyphonTimeoutException`

[`Errors/public/TyphonTimeoutException.cs`](../../src/Typhon.Engine/Errors/public/TyphonTimeoutException.cs)

`WaitDuration` is on the **base** `TyphonTimeoutException` (not redeclared on subclasses). `IsTransient => true` once, inherited by every subclass.

| Subclass | Extra fields | Thrown when |
|---|---|---|
| [`LockTimeoutException`](../../src/Typhon.Engine/Errors/public/LockTimeoutException.cs) | `string ResourceName` | A reader/writer/access-control entry exceeded its deadline — see [01-foundation §1](01-foundation.md). |
| [`TransactionTimeoutException`](../../src/Typhon.Engine/Errors/public/TransactionTimeoutException.cs) | `long TransactionId` | A transaction exceeded its overall deadline. |
| [`PageCacheBackpressureTimeoutException`](../../src/Typhon.Engine/Errors/public/PageCacheBackpressureTimeoutException.cs) | `int DirtyPageCount`, `int EpochProtectedCount` | Page-cache allocation timed out waiting for dirty pages to flush — see [02-storage](02-storage.md). |
| [`WalBackPressureTimeoutException`](../../src/Typhon.Engine/Errors/public/WalBackPressureTimeoutException.cs) | `int RequestedBytes` | WAL claim ring is full; producer waited past its deadline. |

> **Note:** `WalBackPressureTimeoutException` is a **timeout**, not a durability error — it lives under `TyphonTimeoutException`, **not** under `DurabilityException`. The hierarchy classification follows "what does the caller want to do?", and the answer here is "retry later", which is the timeout-family contract.

### Storage family — `StorageException`

[`Errors/public/StorageException.cs`](../../src/Typhon.Engine/Errors/public/StorageException.cs)

I/O errors, page faults, segment-level problems.

- [`CorruptionException`](../../src/Typhon.Engine/Errors/public/CorruptionException.cs) — generic integrity violation (`ComponentName`, `PageIndex`). Never transient.
- [`PageCorruptionException`](../../src/Typhon.Engine/Errors/public/CorruptionException.cs) — CRC32C mismatch on a data page; carries `ExpectedCrc` / `ComputedCrc`. Thrown on on-load verification failure during normal operation. During recovery a torn page is instead recorded *suspect* and either healed by rebuild (derived structures) or fails the open loudly (primary data, RB-04) — there is no FPI repair. See [02-storage §7](02-storage.md) for CRC checks and [11-durability §6](11-durability.md) for torn-page safety.
- [`DatabaseLockedException`](../../src/Typhon.Engine/Errors/public/DatabaseLockedException.cs) — the `.lock` file is held by another process; carries `OwnerPid`, `OwnerMachine`, `StartedAt`. The message instructs to close the other process or delete the `.lock` file if it crashed.

### Durability family — `DurabilityException`

[`Errors/public/DurabilityException.cs`](../../src/Typhon.Engine/Errors/public/DurabilityException.cs)

Things that go wrong below the commit boundary.

- [`WalWriteException`](../../src/Typhon.Engine/Errors/public/WalWriteException.cs) — **fail-fast (per ADR).** A fatal WAL write I/O failure. After this exception, the engine cannot accept durable commits; an engine restart is required. Not transient.
- [`WalClaimTooLargeException`](../../src/Typhon.Engine/Errors/public/WalClaimTooLargeException.cs) — a single claim exceeds the entire WAL ring capacity; carries `RequestedBytes` and `BufferCapacity`. The claim can never succeed without reconfiguring the buffer — not transient.
- [`WalSegmentException`](../../src/Typhon.Engine/Errors/public/WalSegmentException.cs) — segment file operation failed (creation, rotation, header validation); carries `SegmentPath`.

### Resource exhaustion

[`Resources/public/ResourceExhaustedException.cs`](../../src/Typhon.Engine/Resources/public/ResourceExhaustedException.cs) — **direct subclass of `TyphonException`**, not under any "Resource" family. Carries `ResourcePath`, `ResourceType`, `CurrentUsage`, `Limit`, and a computed `Utilization` ratio. `IsTransient => true` — the resource may self-heal via eviction or pool drain. Thrown by components configured with `ExhaustionPolicy.FailFast` — see [13-resources](13-resources.md).

### Index — `UniqueConstraintViolationException`

[`Errors/public/UniqueConstraintViolationException.cs`](../../src/Typhon.Engine/Errors/public/UniqueConstraintViolationException.cs)

Thrown when an insert / update would create a duplicate key in a unique secondary index. **Parameterless constructor only** at present — there's no `IndexName` / `Key` / `EntityId` payload yet. Adding context properties is on the roadmap; for now the call site is responsible for logging the context.

### Schema family

Three exceptions, all direct subclasses of `TyphonException`:

- [`SchemaValidationException`](../../src/Typhon.Engine/Errors/public/SchemaValidationException.cs) — the runtime struct definition is incompatible with what's persisted. Carries the full `SchemaDiff` for programmatic inspection (which field changed, what type, what attribute). See [04-schema](04-schema.md).
- [`SchemaMigrationException`](../../src/Typhon.Engine/Errors/public/SchemaMigrationException.cs) — one or more entities failed during a schema migration. Carries `ComponentName` and `IReadOnlyList<MigrationFailure>` (see §6). Old segments remain untouched — the user can fix the migration function and re-run.
- [`SchemaDowngradeException`](../../src/Typhon.Engine/Errors/public/SchemaDowngradeException.cs) — the database was written by a newer application version (`PersistedRevision > RuntimeRevision`). The engine refuses to open it to prevent corruption.

> **Worth calling out:** `SchemaDowngradeException` **reuses `TyphonErrorCode.SchemaValidation`** (3001), not a dedicated downgrade code. If you're routing on error code, downgrade and runtime-vs-persisted mismatches look identical at the wire level — disambiguate by the exception type.

### `InvalidAccessException`

[`Errors/public/InvalidAccessException.cs`](../../src/Typhon.Engine/Errors/public/InvalidAccessException.cs)

`sealed class`. Thrown when a system tries to mutate a component it didn't declare via `SystemBuilder.Writes<T>()` / `SideWrites<T>()`. **DEBUG builds only** — the `SystemAccessValidator` compiles out in `RELEASE`. Indicates declaration drift; fix by adding the missing `Writes<T>` call. See [10-runtime](10-runtime.md) for the access-declaration model.

---

## 3. Error codes

[`Errors/public/TyphonErrorCode.cs`](../../src/Typhon.Engine/Errors/public/TyphonErrorCode.cs)

A flat `enum TyphonErrorCode` organized into numeric ranges by subsystem. Codes are assigned sequentially within a range; gaps are intentional so codes can be inserted later without renumbering.

| Range | Subsystem | Codes |
|---|---|---|
| 0 | Unspecified | `Unspecified = 0` |
| 1xxx | Transaction | `TransactionTimeout = 1002` |
| 2xxx | Storage | `DataCorruption = 2003`, `StorageCapacityExceeded = 2004`, `PageChecksumMismatch = 2005`, `PageCacheBackpressureTimeout = 2006`, `DatabaseLocked = 2007` |
| 3xxx | Schema / Component | `SchemaValidation = 3001`, `SchemaMigration = 3002` |
| 4xxx | Index | `UniqueConstraintViolation = 4001` |
| 5xxx | Query | (reserved) |
| 6xxx | Resource | `ResourceExhausted = 6001`, `LockTimeout = 6003` |
| 7xxx | Durability | `WalBackPressureTimeout = 7001`, `WalClaimTooLarge = 7002`, `WalWriteFailure = 7003`, `WalSegmentError = 7004` |
| 8xxx | Runtime / Scheduler | `InvalidSystemAccess = 8001` |

**Notes:**
- `LockTimeout` lives in the **6xxx Resource** range, not 1xxx — locks are resource contention, not transaction logic.
- `SchemaDowngradeException` reuses `SchemaValidation` (3001) — see §2.
- Only Tier 1 codes are defined; reserved tiers (§8) extend the enum without renumbering existing values.

---

## 4. Result pattern — hot-path success / failure

[`Errors/public/Result.cs`](../../src/Typhon.Engine/Errors/public/Result.cs)

Exceptions are appropriate for failures, but in the engine's hottest loops — B+Tree lookups, MVCC revision-chain reads — "not found" or "not visible at this snapshot" aren't failures, they're routine outcomes. Throwing on them would burn cycles on stack unwinding and frame allocation for what should be a register-level branch.

For those paths, Typhon uses `Result<TValue, TStatus>`:

```csharp
[StructLayout(LayoutKind.Sequential)]
public readonly struct Result<TValue, TStatus>
    where TValue  : unmanaged
    where TStatus : unmanaged, Enum
{
    public readonly TValue  Value;
    public readonly TStatus Status;

    public bool IsSuccess
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Unsafe.As<TStatus, byte>(ref Unsafe.AsRef(in Status)) == 0;
    }
}
```

Three constructors:

| Constructor | Use |
|---|---|
| `Result(TValue value)` | Successful result with default (zero) status. |
| `Result(TStatus status)` | Failure with default value. |
| `Result(TValue value, TStatus status)` | Both fields set — useful for cases like `Deleted` that carry revision metadata alongside a non-success status. |

**The `IsSuccess` trick.** Every status enum in Typhon follows the convention **`Success = 0`**. So `IsSuccess` reduces to: reinterpret the first byte of `Status` as a `byte`, compare to zero. No boxing, no virtual dispatch, no `Enum.Equals` — one `movzx` and one `test` on x64. The JIT inlines it everywhere.

Usage:

```csharp
var r = btree.Lookup(key);
if (r.IsSuccess) { /* use r.Value */ }
else { /* r.Status == NotFound */ }
```

---

## 5. Status enums

Two status enums are exposed publicly today. Both are `byte`-backed (smallest possible) and both follow the `Success = 0` convention.

### `BTreeLookupStatus`

[`Indexing/public/BTree.LookupStatus.cs`](../../src/Typhon.Engine/Indexing/public/BTree.LookupStatus.cs)

```csharp
public enum BTreeLookupStatus : byte
{
    Success  = 0,
    NotFound = 1,
}
```

Lives in the `Indexing/public/` folder — *not* `Data/Index/...` (an older path that no longer exists). Two values: the key was either found or it wasn't.

### `RevisionReadStatus`

[`Revision/public/RevisionReadStatus.cs`](../../src/Typhon.Engine/Revision/public/RevisionReadStatus.cs)

```csharp
public enum RevisionReadStatus : byte
{
    Success           = 0,
    NotFound          = 1,   // entity has no chain at all
    SnapshotInvisible = 2,   // chain exists, but no element visible at reader's TSN
    Deleted           = 3,   // tombstoned at or before reader's snapshot tick
}
```

Lives in `Revision/public/` (again, not the old `Data/Revision/` path). Four values reflect the four cases that arise from MVCC visibility — see [05-revision](05-revision.md) for the visibility predicate. `Deleted` is particularly useful: a caller often wants to distinguish "never existed" from "existed but tombstoned" without comparing `BornTSN` / `DeadTSN` themselves.

---

## 6. `MigrationFailure` — per-entity migration diagnostics

[`Errors/public/SchemaMigrationException.cs`](../../src/Typhon.Engine/Errors/public/SchemaMigrationException.cs) (same file)

When a `SchemaMigrationException` is thrown, the `Failures` array describes which entities failed and why:

```csharp
public readonly struct MigrationFailure
{
    public int       ChunkId    { get; init; }   // logical entity identifier
    public string    OldDataHex { get; init; }   // hex dump of the pre-migration bytes
    public Exception Exception  { get; init; }   // the migration function's exception
}
```

`SchemaMigrationException.Failures` is the full list; the formatted message includes the first 10 entries and a `... and N more` tail. The hex dump is what lets you reproduce the failure offline — feed it back through the migration function in a unit test.

---

## 7. `ThrowHelper` — keeping the throw out of the hot path

[`Errors/internals/ThrowHelper.cs`](../../src/Typhon.Engine/Errors/internals/ThrowHelper.cs)

`internal static class ThrowHelper` is the engine's throw-call delegating layer. Every method follows the same shape:

```csharp
[MethodImpl(MethodImplOptions.NoInlining)]
[DoesNotReturn]
public static void ThrowLockTimeout(string resourceName, TimeSpan waitDuration)
    => throw new LockTimeoutException(resourceName, waitDuration);
```

Two attributes matter:

- **`[MethodImpl(MethodImplOptions.NoInlining)]`** — the JIT is *told* not to inline this. Why: if the throw site were inlined into the caller, the hot path would carry the object construction, the throw machinery, and (often) the cold message-formatting branch — bloating the caller's IL and pushing useful code out of the L1i cache. Keeping the throw in its own method puts the cold code in cold memory; the hot caller is left with just a `call` instruction.
- **`[DoesNotReturn]`** — informs the C# nullability flow analysis and the JIT that control doesn't return. Lets callers omit `return default` after `ThrowXxx` without compiler complaints.

Pattern in practice:

```csharp
public void TryAcquire(ref WaitContext ctx)
{
    if (!Lock(ref ctx)) ThrowHelper.ThrowLockTimeout(_name, ctx.WaitDuration);
    // hot code below — JIT'd in the same method, no throw machinery in sight
}
```

The hot method's IL stays compact; the throw lives in `ThrowHelper`'s body, never inlined.

`ThrowHelper` currently has helpers for every Tier 1 exception listed in §2 plus a couple of `ArgumentException` / `InvalidOperationException` wrappers (e.g., the `EnumerateRange` API-misuse helpers for B+Trees). New throw sites should add a helper here rather than throwing inline.

---

## 8. Reserved tier — declared but not implemented

The error model was designed in tiers. **Tier 1 is what ships today**, and §2 lists every type that exists in `Errors/public/`. Several exceptions named in the original design — Tier 2 / Tier 3 — are reserved in the documentation but **not present in code yet**:

| Reserved exception | Intended for |
|---|---|
| `ComponentNotFoundException` | Reading a component slot that isn't registered. |
| `TransactionConflictException` | MVCC write-write conflict on commit. |
| `CapacityExceededException` | Bounded data structure over its hard cap (distinct from `ResourceExhaustedException`'s soft policy). |
| `ComponentSchemaException` | Schema-related errors that don't fit `SchemaValidation` / `SchemaMigration` / `SchemaDowngrade`. |
| `EpochVoidedException` | An epoch-protected operation outlived its epoch. |

If your code path conceptually wants one of these, throw a `TyphonException` with an appropriate `TyphonErrorCode` for now (or add the type and update this doc). Don't catch them speculatively — the catch will be dead code until the type ships.

---

## See also

- [01-foundation](01-foundation.md) — `WaitContext`, `Deadline`. All `TyphonTimeoutException` instances originate at a deadline expiry detected here.
- [02-storage](02-storage.md) — `PageCorruptionException` and `CorruptionException` are thrown from the page-cache CRC verification path.
- [04-schema](04-schema.md) — the schema exception family (`SchemaValidationException`, `SchemaMigrationException`, `SchemaDowngradeException`) and the `SchemaDiff` / migration model.
- [06-ecs](06-ecs.md) — `UniqueConstraintViolationException` propagates here from index inserts during `Spawn` / `OpenMut`.
- [08-transactions](08-transactions.md) — `TransactionTimeoutException` and the conflict-handler model (the reserved `TransactionConflictException` slot in §8).
- [11-durability](11-durability.md) — `WalWriteException` (fail-fast, per ADR) and `WalClaimTooLargeException` semantics.
- [13-resources](13-resources.md) — `ResourceExhaustedException` and `ExhaustionPolicy`.
