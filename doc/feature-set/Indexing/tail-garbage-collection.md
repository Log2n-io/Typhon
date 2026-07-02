# TAIL Retention / Garbage Collection
> Bounds TAIL version-history growth via boundary-sentinel-preserving pruning — built and tested, not yet auto-triggered.

**Status:** 🚧 Partial · **Visibility:** Internal · **Category:** [Indexing](./README.md)

## 🎯 What it solves

[Versioned Secondary Indexes](./versioned-secondary-indexes.md) append a TAIL entry every time an `AllowMultiple`
indexed field on a `Versioned` component gains or loses a value — that's what makes temporal ("who held this value
at TSN T") queries possible. TAIL is append-only by design, so under sustained churn on a given key (a field that
keeps flipping between a small set of values, for instance) it grows without bound: nothing today reclaims entries
that no transaction can possibly still need. TAIL retention/GC is the reclamation half of that design — it exists
as a tested primitive, but the trigger that would run it automatically isn't wired up yet.

## ⚙️ How it works (in brief)

Given a TAIL buffer and a `retentionTSN` (the oldest TSN any in-flight transaction could still query), pruning
groups entries by chain (the entity/key-value pairing a TAIL sequence belongs to). For each chain it discards every
entry older than the newest one at or below `retentionTSN`, keeping that single newest entry as a **boundary
sentinel** — the fact a reader sitting exactly at the retention edge needs to know whether the chain was Active or
Tombstoned. A chain whose sentinel is a Tombstone with nothing newer than it is dropped entirely, sentinel
included, since there's no longer any reader that could land on it. Pruning rewrites the kept entries into a fresh
buffer rather than mutating in place.

## 💻 Usage

```csharp
[Component("Game.GuildMember", 1)]   // Versioned by default
struct GuildMember
{
    [Index(AllowMultiple = true)]
    public long GuildId;
}

// Every commit that moves an entity between GuildId values appends a TAIL entry for the old
// and new value (see Versioned Secondary Indexes) — no extra call needed for that part:
using (var tx = dbe.CreateQuickTransaction())
{
    ref var m = ref tx.OpenMut(aria).Write(MemberArchetype.M);
    m.GuildId = nextGuildId;   // one more TAIL entry, every commit, indefinitely
    tx.Commit();
}

// There is no application-facing Prune()/Compact() call today. TailGarbageCollector.Prune exists
// and is unit-tested in isolation, but nothing in the commit, checkpoint, or deferred-cleanup path
// invokes it yet — so the entry appended above is never reclaimed for the life of the process.
// Once wired, pruning is designed to run automatically (no API call), the same way HEAD/TAIL
// maintenance itself requires none.
```

## ⚠️ Guarantees & limits

- **Boundary-correct today as a primitive** — for any `retentionTSN` passed in, the kept set always lets a reader
  at that exact TSN resolve Active/Tombstoned correctly; entries with `TSN > retentionTSN` are never discarded.
- **Dead chains collapse fully** — a chain whose last surviving state is a Tombstone with no later activity is
  removed entirely rather than leaving a permanent one-entry remnant.
- **Not yet automatically triggered** — no `MinTSN`-advance hook, checkpoint hook, or background scheduler calls
  it. `DeferredCleanupManager` (which already drives the analogous component-revision and content-chunk reclamation
  on `MinTSN`/tail-transaction-completion) has no TAIL GC integration today.
- **Unbounded TAIL growth in production today** — every value transition on an `AllowMultiple` indexed field of a
  `Versioned` component adds a TAIL entry that nothing currently removes; storage cost scales with field churn over
  the life of the process, not with retention need.
- **Compaction reallocates the buffer** — a successful prune returns a new TAIL buffer ID distinct from the input;
  the caller is responsible for repointing the HEAD buffer's `TailBufferId` to it (the not-yet-built integration
  layer's job, not application code's).
- **Tested in isolation only** — unit tests drive `TailGarbageCollector.Prune` directly against a TAIL buffer; no
  concurrency/stress coverage exists yet for pruning racing live writers or temporal readers.
- **No public API** — this is an internal engine primitive; there is nothing for application code to call, configure, or observe.

## 🧪 Tests

- [VersionedIndexTests](../../../test/Typhon.Engine.Tests/Data/VersionedIndexTests.cs) — Phase 5 `GC Tests` region: `TailGC_PruneOldEntries_KeepsBoundarySentinel`, `TailGC_DeadChain_FullyRemoved`, `TailGC_NoOldEntries_NothingPruned` — drives `TailGarbageCollector.Prune` directly, in isolation from any auto-trigger

## 🔗 Related

- Sibling feature: [Versioned (HEAD/TAIL) Secondary Indexes for MVCC](./versioned-secondary-indexes.md)
- Source: `src/Typhon.Engine/Indexing/internals/TailGarbageCollector.cs`
- Source: `src/Typhon.Engine/Ecs/internals/DeferredCleanupManager.cs` (analogous `MinTSN`-driven cleanup; TAIL GC is not yet integrated here)

<!-- Deep dive: claude/design/Indexing/VersionedSecondaryIndexes.md §10 GC / Retention Integration -->
