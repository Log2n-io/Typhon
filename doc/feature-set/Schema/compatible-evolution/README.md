# Compatible Schema Evolution (Auto-Migration)
> Reopen with an added, removed, reordered, or safely-widened field and Typhon migrates every entity automatically — no migration code, no index rebuild.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🔵 Core · **Category:** [Schema](../README.md)
**Assumes:** [FieldId Stability & Rename Tracking](../fieldid-stability.md)

## 🎯 What it solves

Struct layouts drift across application versions: a field gets added, an obsolete one removed, fields get
reordered, or a type is widened to a larger compatible one (`int`→`long`, `Float`→`Double`). Without engine
support, every one of these routine changes would need hand-written byte-copy migration code, and a mistake
there risks silently misreading old data through the new layout. Compatible Schema Evolution handles the ~80%
of real-world changes that are structurally safe — pure copy, zero-fill, and well-defined widening — fully
automatically, so you only write a migration function (see Schema Migration Functions) for the changes that
truly need one.

## ⚙️ How it works (in brief)

On reopen, `RegisterComponentFromAccessor<T>()` diffs the persisted field layout against your current struct
(see Schema Validation). If every change is `Compatible` or `CompatibleWidening`, the engine allocates a new
component segment at the new stride, then copies each entity's bytes into it field-by-field — zero-filling new
fields and applying sign-extend/zero-extend/IEEE754-promotion for widened ones. Each entity keeps its original
**ChunkId**, so the primary-key index, surviving secondary indexes, and revision-chain pointers stay valid
without a rebuild; only a newly-`[Index]`-attributed field triggers an O(N) index build. The MVCC revision chain
is collapsed to a single HEAD revision — there are no active transactions at startup, so history isn't needed.
Migration runs eagerly before any user transaction (see Migration Execution Strategy for timing and progress
details).

## 💻 Usage

```csharp
// V1 — first deployment
[Component("Game.Player", 1)]
struct PlayerV1
{
    public int Health;
    public float Speed;
}

using (var dbe = host.Services.GetRequiredService<DatabaseEngine>())
{
    dbe.RegisterComponentFromAccessor<PlayerV1>();
    dbe.InitializeArchetypes();

    using var t = dbe.CreateQuickTransaction(DurabilityMode.Immediate);
    t.Spawn<PlayerArch>(PlayerArch.Comp.Set(new PlayerV1 { Health = 100, Speed = 5.5f }));
    t.Commit();
}

// V2 — next release: Shield added, Health widened to long. Same [Component] name, same Revision.
[Component("Game.Player", 1)]
struct PlayerV2
{
    public long Health;     // int -> long: auto sign-extended
    public int Shield;      // new field: zero-filled
    public float Speed;
}

using (var dbe = host.Services.GetRequiredService<DatabaseEngine>())
{
    dbe.RegisterComponentFromAccessor<PlayerV2>();   // migrates every entity before returning
    dbe.InitializeArchetypes();
    // all entities now readable as PlayerV2; Shield == 0, Health correctly widened
}
```

## ⚠️ Guarantees & limits

- ChunkId is preserved across migration — the primary-key index, surviving secondary indexes, and revision-chain
  pointers need no rebuild. Only a newly-indexed field pays an O(N) B+Tree build.
- Supported widenings are exact and lossless: integer sign/zero-extension, `Float`→`Double`, `PointNF`→`PointND`
  / `QuaternionF`→`QuaternionD` (per-component), `String64`→`String1024`. Anything not lossless (narrowing,
  signed↔unsigned, cross-type, semantic change) is classified `Breaking` and is **not** handled here — it
  throws `SchemaValidationException` unless a migration function is registered.
- Field add/remove/reorder/widen can combine freely in a single reopen — they're computed from one diff and
  applied in one pass.
- MVCC revision history is discarded — only the HEAD revision survives migration. There is no way to query
  pre-migration revisions afterward.
- Migration is crash-safe to retry: it flushes its own segments before WAL replay begins, and only repoints the
  database's root metadata to the new segments on success. A crash mid-migration leaves the old segments
  authoritative; the next open re-runs migration from scratch.
- The component's `[Component(..., Revision)]` number does **not** need to change for a compatible evolution —
  revision bumps are reserved for breaking changes that require a registered migration function.

## 🧪 Tests

- [SchemaEvolutionTests](../../../../test/Typhon.Engine.Tests/Data/Schema/SchemaEvolutionTests.cs) — add/remove/reorder/widen (int→long, float→double, sign extension) individually and combined, surviving indexes, multi-entity and bulk (10K) migration

## 🔗 Related

- Related feature: [Schema Validation on Reopen](../schema-validation.md) (classifies changes before evolution runs), [FieldId Stability & Rename Tracking](../fieldid-stability.md)
- Related feature: [User-Defined Migration Functions](../migration-functions.md) (user-supplied conversion for `Breaking` changes)
- Sub-features: [Migration Execution Strategy](./migration-execution-strategy.md)

<!-- Deep dive: claude/design/Schema/03-compatible-evolution.md, claude/overview/04-data.md §4.10 Schema Evolution -->
