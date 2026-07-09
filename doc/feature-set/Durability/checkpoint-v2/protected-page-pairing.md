---
uid: feature-durability-checkpoint-v2-protected-page-pairing
title: 'A/B Protected-Page Slot-Pairing'
description: 'Doublewrite-free torn-write protection for the meta page and segment-directory pages that crash recovery can''t re-derive.'
---

# A/B Protected-Page Slot-Pairing
> Doublewrite-free torn-write protection for the meta page and segment-directory pages that crash recovery can't re-derive.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Durability](../README.md)

## 🎯 What it solves

Most data pages survive a torn write because they're either rebuilt from primary data on the next crash recovery, or detected as corrupt and the open fails loudly. Two kinds of page are neither: the database's root metadata and each segment's page directory describe *where everything else lives* — there's no "primary data" to rebuild them from. A power cut mid-write to the only copy of such a page would corrupt structure recovery depends on, with nothing to recover it from. The earlier fix (a before-image written on every checkpoint cycle) protected these pages but cost an extra write per protected page on every single cycle, whether or not the page had changed.

## ⚙️ How it works (in brief)

Every protected page — the meta page and each segment's directory pages — is backed by two physical slots. A write always lands on whichever slot isn't the currently-trusted one, tagged with a higher generation number and its own checksum; only after that write is confirmed durable does Typhon start treating it as current. The previously-current slot is never touched by the write, so a crash can tear at most the *other* copy. At open, or whenever a torn write is suspected, both slots are read and the highest-generation one that passes its checksum is trusted; if neither does, the open fails loudly rather than risk silently serving corrupt structure.

## 💻 Usage

Entirely automatic — there is no API to opt into. It engages whenever the engine writes the meta page (schema/bootstrap changes) or grows a segment's directory (an archetype acquiring more storage), both ordinary side effects of normal writes and of every checkpoint cycle:

```csharp
services
    .AddScopedManagedPagedMemoryMappedFile(o =>
    {
        o.DatabaseName = "skirmish";
        o.DatabaseDirectory = ".";
    })
    .AddScopedDatabaseEngine();

// Nothing extra to call — spawning enough entities to grow an archetype's segment, or any
// schema mutation, is already protected the moment the engine persists it.
using var t = dbe.CreateQuickTransaction();
var id = t.Spawn<Soldier>(Unit.Health.Set(new Health { Current = 100 }));
t.Commit();
```

## ⚠️ Guarantees & limits

- **At least one valid copy always exists** — the trusted slot is never overwritten by a protected-page write, so torn-write exposure is limited to the alternate slot only.
- **Both-slots-corrupt is a loud failure, never a silent fallback** — unlike ordinary data pages, these have no primary data to rebuild from; a confirmed double failure means restoring from backup.
- **Doublewrite-free** — unlike the retired Full-Page-Image mechanism, this costs one extra page write only when a protected page actually changes (segment creation/growth, schema mutation) — not on every checkpoint cycle regardless of change.
- **Cold path only** — segment-directory writes happen at segment create/grow time, not on steady-state entity reads/writes, so normal-operation hot paths pay zero extra cost from this mechanism.
- **Generation is monotonic** — selection at open/recovery is unambiguous: the highest valid generation number wins, never a heuristic or timestamp comparison.
- **Tied to the on-disk format version** — a database file written before this mechanism cannot be opened by an engine that expects it; older files are refused outright, not auto-migrated.

## 🧪 Tests

- [MetaPairTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Durability/CrashRecovery/MetaPairTests.cs) — meta-page A/B slot alternation, torn-slot recovery, generation monotonicity
- [DirectoryPairTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Storage/DirectoryPairTests.cs) — segment-directory twin slot-pairing, same corruption/reopen style for directory pages
- [ProtectedPairTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Durability/CrashRecovery/ProtectedPairTests.cs) — falsifiable proof via a write-log that the current-valid slot is never physically written

## 🔗 Related

- Parent feature: [Checkpoint v2](./README.md)
- Sibling: [Page Integrity — CRC32C, Seqlock Snapshots & A/B Page Pairing](../../Storage/page-integrity.md) — storage-layer description of the same A/B slot-pairing mechanism

<!-- Deep dive: claude/design/Durability/MinimalWal/04-checkpoint.md §4, claude/overview/06-durability.md §6.4 -->
<!-- Rules: claude/rules/durability.md — rule CK-05 -->
