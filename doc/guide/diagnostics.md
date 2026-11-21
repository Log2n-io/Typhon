---
uid: guide-diagnostics
title: Build diagnostics
description: 'Every compile-time diagnostic the Typhon packages report in your build (TYPHON0xx analyzers, TPH1xxx generators): what it catches, why it matters, how to fix it.'
---

# Build diagnostics

The `Typhon` package ships Roslyn analyzers and source generators that run in **your** compilation: referencing the package is enough, there is
nothing to configure. They catch, at build time, mistakes that Typhon would otherwise report late (at open, on the first spawn) or never.
`Typhon.Protocol`, which `Typhon` and `Typhon.Client` both depend on, ships the replication generator and TYPHON015.

> ⚠️ **Analyzers reach the project that references the package directly.** NuGet does not flow analyzers through a project reference or a
> package dependency (`PrivateAssets` defaults to `contentfiles;analyzers;build`). Declare components, archetypes and systems in the project that
> has `<PackageReference Include="Typhon" … />`, or add the reference to the project that declares them.

Each ID below is the target of the help link your IDE shows next to the diagnostic.

| ID | Severity | Package | Catches |
|---|---|---|---|
| [TYPHON001](#typhon001) | Error | Typhon | a no-copy type passed by value or `in` |
| [TYPHON003](#typhon003) | Error | Typhon | a no-copy type copied |
| [TYPHON004](#typhon004) | Error | Typhon | a must-dispose value never disposed |
| [TYPHON005](#typhon005) | Error | Typhon | a type holding a must-dispose field without a `Dispose()` |
| [TYPHON006](#typhon006) | Error | Typhon | a `Dispose()` that skips a must-dispose field |
| [TYPHON007](#typhon007) | Error | Typhon | an early return in `Dispose()` that skips a must-dispose field |
| [TYPHON009](#typhon009) | Warning | Typhon | a spatial component written through a span instead of `WriteSpatial` |
| [TYPHON010](#typhon010) | Warning | Typhon | a component struct storing avoidable padding |
| [TYPHON011](#typhon011) | Error | Typhon | a component whose managed and marshalled layouts differ (`bool`, `char`) |
| [TYPHON012](#typhon012) | Error | Typhon | a non-readonly `EntityRef`/`EntityRefMut` member called on a read-only receiver |
| [TYPHON013](#typhon013) | Warning | Typhon | a component too large to pass to `Spawn` |
| [TYPHON016](#typhon016) | Warning | Typhon | an `await` inside a transaction's scope |
| [TYPHON017](#typhon017) | Warning | Typhon | `Where(Func)` over an `[Index]` field, which scans every entity |
| [TYPHON015](#typhon015) | Warning | Typhon.Protocol | a command sent by name while its typed struct is in reach |
| [TPH1001](#tph1001)–[TPH1004](#tph1004) | Error | Typhon | invalid cascade graphs and archetype declarations |
| [TPH1101](#tph1101)–[TPH1109](#tph1109) | Error | Typhon.Protocol | invalid replication declarations |

## Ownership: no-copy and must-dispose types

Some Typhon types own pinned pages or an epoch scope: `Transaction`, `UnitOfWork`, `BulkLoadSession`, `ClusterEnumerator<T>`, the collection and
index enumerators. Copying one duplicates what it holds; dropping one undisposed keeps its pages pinned and its epoch held, which stalls page eviction
and the checkpoint. A type of yours that holds one **by value** inherits the rule.

<a id="typhon001"></a>
### TYPHON001 — no-copy parameter not passed by `ref`

A parameter of a no-copy type (for example `ClusterEnumerator<T>`) is taken by value or `in`. By value copies it; `in` makes the compiler copy it
before every non-readonly call. **Fix:** declare the parameter `ref`. A code fix is offered in the IDE.

<a id="typhon003"></a>
### TYPHON003 — no-copy value copied

A no-copy value is assigned to another variable, returned or passed by value. **Fix:** work through a `ref` local or a `ref` parameter.

<a id="typhon004"></a>
### TYPHON004 — must-dispose value never disposed

A `Transaction`, `UnitOfWork`, enumerator or other must-dispose value is created and not disposed on every path. An undisposed transaction keeps its
epoch pinned: memory reclamation and the checkpoint wait behind it. **Fix:** `using var tx = db.CreateQuickTransaction();`. A code fix is offered.

<a id="typhon005"></a>
### TYPHON005 — owner without `Dispose()`

Your type holds a must-dispose value in a field and has no `Dispose()`, so nothing ever releases it. **Fix:** implement `IDisposable` (or, on a ref
struct, a `Dispose()` method) and dispose the field.

<a id="typhon006"></a>
### TYPHON006 — `Dispose()` skips a field

`Dispose()` exists but does not dispose every must-dispose field. **Fix:** dispose each of them.

<a id="typhon007"></a>
### TYPHON007 — early return skips a field

A `return` in `Dispose()` exits before a must-dispose field is disposed. **Fix:** dispose the fields before any early return, or restructure.

## Components and entity handles

<a id="typhon009"></a>
### TYPHON009 — spatial component written through a span

`ClusterRef.GetSpan<T>` / `Get<T>` on a component carrying a `[SpatialIndex]` field. Writes through it bypass the `WriteSpatial` barrier, so the engine
falls back to a scan of every dirty slot to keep the spatial index right. **Fix:** `cluster.WriteSpatial(Arch.Comp, slot, value)`; for a read, use
`GetReadOnlySpan` / `GetReadOnly`.

<a id="typhon010"></a>
### TYPHON010 — avoidable padding

A component column is strided by `sizeof(T)`: alignment padding is stored for every entity, logged to the WAL and checkpointed. A single `long` or
`double` rounds the struct up to a multiple of 8. **Fix:** `[StructLayout(LayoutKind.Sequential, Pack = 4)]`. `Pack` moves field offsets, which are
persisted: on a component that already has data, use `Size` instead (or bump the revision).

<a id="typhon011"></a>
### TYPHON011 — managed and marshalled layouts differ

A `bool` is 1 byte managed and 4 marshalled; a `char` is 2 and 1. When a field's offset differs between the two layouts, every field-addressed path
(index keys, WAL field decode, recovery, schema evolution, tools) reads the wrong bytes, with no error anywhere. **Fix:** `byte` for a flag, `ushort`
for a code unit.

<a id="typhon012"></a>
### TYPHON012 — hidden defensive copy of an entity handle

A non-readonly member of `EntityRef` / `EntityRefMut` (`Set`, `Enable`, …) is called on a read-only receiver (`in` parameter, `readonly` field): the
compiler copies the handle first, and any state the call updates is lost with the copy. **Fix:** pass the handle by `ref`, or hold it in a
non-readonly local.

<a id="typhon013"></a>
### TYPHON013 — component too large to pass to `Spawn`

`Spawn` carries component values inline in 112 bytes. A larger component cannot be passed to it. **Fix:** split the component (a large key and its
payload are usually two components), or spawn without it and write it through `OpenMut` in the same transaction — then suppress the warning with a
reason.

<a id="typhon016"></a>
### TYPHON016 — `await` inside a transaction's scope

An `await` between a `Transaction`'s creation and its disposal, in the same method. A transaction belongs to the thread that created it — it holds
that thread's epoch scope — and the continuation of an `await` resumes on another thread in an ASP.NET host, where its `Commit`, `Rollback` and
`Dispose` throw (rule EP-04). **Fix:** finish the transaction before awaiting, or create it after; do the slow I/O outside its scope.

## Queries

<a id="typhon017"></a>
### TYPHON017 — `Where(Func)` over an indexed field

`tx.Query<PlayerArch>().Where<Player>(p => p.Level > 10)` where `Level` carries `[Index]`. `Where` takes an opaque delegate: the query opens every
entity it admits and calls the predicate on each, so the index is never used. **Fix:** `WhereField<Player>(p => p.Level > 10)` — same lambda, parsed into
an index seek or range. The code fix rewrites the call when the whole predicate is comparisons on `[Index]` fields joined by `&&`; for anything else
(two fields compared, a computed value, a non-indexed field, `||`) narrow with `WhereField` on the indexed part and keep `Where` for the rest. A delegate
passed as a variable or method group is not analyzed.

## Subscriptions clients

<a id="typhon015"></a>
### TYPHON015 — command sent by name

`TyphonClient.SendCommandAsync("MoveCmd", values)` while the `[ReplicatedMessage]` struct `MoveCmd` is in reach. The name-keyed path finds fields by
string at run time and allocates per send. **Fix:** send it typed: `client.Enqueue(in cmd)` then `await client.FlushCommandsAsync()` — fields checked at
compile time, bound to the negotiated catalog once.

## Source generator diagnostics

<a id="tph1001"></a>
### TPH1001 — cascade-delete cycle

An `OnParentDelete = Cascade` graph loops back on itself. **Fix:** break the cycle; cascades must form a tree.

<a id="tph1002"></a>
### TPH1002 — cascade-delete diamond

An archetype is reachable through two cascade paths. **Fix:** keep one cascading path; make the other a plain reference.

<a id="tph1003"></a>
### TPH1003 — unique index with an ambiguous scope

Several archetypes of one inheritance tree declare the component carrying a unique `[Index]`; each would own a separate tree, so uniqueness between
them cannot be enforced. **Fix:** declare the component on their common ancestor, or use `[Index(AllowMultiple = true)]`.

<a id="tph1004"></a>
### TPH1004 — component declared twice in one inheritance chain

An archetype re-registers a component it already inherits, wasting a slot and a cluster column. **Fix:** remove the `Register` call.

<a id="tph1101"></a>
### TPH1101 — replicated type not partial

The replication generator implements members on `[Replicated]` / `[ReplicatedMessage]` types. **Fix:** add `partial`.

<a id="tph1102"></a>
### TPH1102 — more than one position

An archetype marks several `Comp<T>` fields `[Motion]` or `[Position]`. **Fix:** keep one.

<a id="tph1104"></a>
### TPH1104 — no default codec

The field's type has no exact wire form. **Fix:** name a codec, e.g. `[Replicate(CodecKind.Quant, Min = …, Max = …, Bits = 24)]`.

<a id="tph1105"></a>
### TPH1105 — invalid `[Fraction]`

The `[Fraction]` attribute names a missing or incompatible maximum field. **Fix:** follow the message.

<a id="tph1106"></a>
### TPH1106 — misplaced replication attribute

A replication attribute sits where it has no effect. **Fix:** move or remove it.

<a id="tph1107"></a>
### TPH1107 — codec the field cannot carry

The chosen `CodecKind` does not fit the field's type. **Fix:** pick a codec for that type.

<a id="tph1108"></a>
### TPH1108 — both public and owner-only

A field is both `[Replicate]` (sent to every client) and `[Owner]`. **Fix:** drop one; a public `[Fraction]` beside a private `[Owner]` shows a coarse
value to all and the exact one to its owner.

<a id="tph1109"></a>
### TPH1109 — unsupported replicated type

The type cannot be replicated. **Fix:** follow the message.
