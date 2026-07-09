---
uid: feature-indexing-secondary-index-storage-modes-index
title: 'Secondary Index Storage Modes'
description: 'Unique vs AllowMultiple: the per-field choice that decides an index''s on-disk value shape and per-entity storage cost.'
---

# Secondary Index Storage Modes
> Unique vs AllowMultiple: the per-field choice that decides an index's on-disk value shape and per-entity storage cost.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Indexing](../README.md)
**Assumes:** [Component & Field Schema Declaration](../../Schema/component-field-declaration.md)

## 🎯 What it solves

Modeling a field as `[Index]` always builds a B+Tree, but a 1:1 field (a player ID, a SKU) and a 1:N field (a guild
ID, a status) cannot use the same on-disk value shape — one entity per key needs no more than a pointer, many
entities per key need a growable set. The storage mode chosen for an indexed field — unique or `AllowMultiple` —
isn't just a uniqueness constraint at the application level; it picks a different B+Tree value representation,
adds (or doesn't add) a hidden per-entity bookkeeping field, and routes commits through a different compound
B+Tree operation. Picking the right mode keeps both storage footprint and commit cost proportional to what the
field actually models.

## ⚙️ How it works (in brief)

A unique index's B+Tree value is the entity's component chunk-id itself — direct, no indirection. An
`AllowMultiple` index's value is a HEAD buffer ID: a `VariableSizedBufferSegment` holding the current set of
chunk-ids that share the key. To remove or relocate a single entity's slot inside a HEAD buffer in O(1) instead of
scanning it, every `AllowMultiple`-indexed field adds a hidden 4-byte `ElementId` to the component's storage
overhead — unique fields add nothing. The mode also picks the commit-time B+Tree operation: unique fields use
`Add`/`Move`/`Remove`; `AllowMultiple` fields use `Add`/`MoveValue`/`RemoveValue`, and on `Versioned` components
additionally maintain a per-key TAIL history buffer. Both shapes share the identical OLC concurrency model and the
same read API (`Transaction.EnumerateIndex`) — the difference is invisible to a caller and shows up only in
storage footprint and commit cost.

## Sub-features

| Sub-feature | Declared as | On-disk value | Extra per-entity cost |
|-------------|-------------|----------------|------------------------|
| [Unique (single-value) secondary index](./unique-secondary-index.md) | `[Index]` (default) | Key → chunk-id, directly | none |
| [Multi-value secondary index (AllowMultiple)](./multi-value-secondary-index.md) | `[Index(AllowMultiple = true)]` | Key → HEAD buffer of chunk-ids (+ TAIL on `Versioned`) | +4 bytes (`ElementId`) |

## ⚠️ Guarantees & limits

- The mode is fixed per field at schema registration; switching between unique and `AllowMultiple` is a schema
  change, not a runtime toggle — it bumps the table's index-layout version and invalidates every previously
  resolved `IndexRef`.
- The 4-byte `ElementId` overhead applies to every `AllowMultiple`-indexed field on every component instance,
  regardless of storage mode (`SingleVersion`/`Versioned`/`Transient`) — it is what lets the commit path remove an
  entity from its HEAD buffer without a linear scan.
- The TAIL history segment is allocated once per table, only if at least one `AllowMultiple` index exists on it,
  and each key's TAIL buffer is populated lazily on that key's first mutation — a table with only unique indexes
  never allocates it.
- Unique-field commits (`Move`) are a single tree traversal that write-locks at most two leaves. `AllowMultiple`
  commits (`MoveValue`/`RemoveValue`) do that same traversal plus splice the entity into/out of a HEAD buffer (and,
  on `Versioned` tables, append TAIL entries) — strictly more work per commit for the same field shape.
- Both modes expose the identical read surface — `Transaction.EnumerateIndex` over ascending key order — so
  choosing a mode never changes how query code is written, only what it costs underneath.
- The mode applies uniformly across all three component storage modes (`SingleVersion`/`Versioned`/`Transient`);
  only `SingleVersion`/`Versioned` indexes are queried via `Transaction.EnumerateIndex` — `Transient`-indexed
  fields are built and maintained the same way but are read through the ECS query pipeline (`WhereField`)
  instead.

## 🧪 Tests

- [BtreeTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/BTreeTests.cs) — unique vs. `AllowMultiple` B+Tree value representation side by side (`CheckTree`/`CheckRemove` vs. `CheckMultipleTree`/`CheckByteMultipleTree`/`CheckFloatMultipleTree`)
- [BulkEnumerateTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/BulkEnumerateTests.cs) — `SecondaryIndex_UniqueField`/`SecondaryIndex_AllowMultiple` show both modes read through the identical `Transaction.EnumerateIndex` surface

## 🔗 Related

- See also: [Specialized B+Tree Key-Size Variants](../btree-key-variants.md), [IndexRef — Resolve-Once Index Handle](../index-ref-resolution.md)
- See also: [Indexed Field Predicates (WhereField)](../../Querying/fluent-query-api/wherefield-indexed-predicate.md) — the query-planner path for `Transient`-indexed fields
- Sub-features: [Unique (single-value) secondary index](./unique-secondary-index.md), [Multi-value secondary index (AllowMultiple)](./multi-value-secondary-index.md)

<!-- Deep dive: claude/overview/04-data.md §4.7 B+Tree Indexes — Single vs Multiple, Versioned Secondary Indexes (HEAD/TAIL) -->
<!-- Deep dive: claude/design/Indexing/public-api.md -->
