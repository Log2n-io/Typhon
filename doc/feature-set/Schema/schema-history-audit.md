# Schema History Audit Log
> Every schema change ever applied to the database, permanently recorded and queryable.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Schema](./README.md)

## 🎯 What it solves

Schema changes in production are easy to apply and hard to reconstruct after the fact. When a field was added, when a type was widened, how many entities a migration touched, how long it took — none of that is visible from the current schema alone, yet it's exactly what you need when auditing a deployment, debugging a regression that correlates with a schema change, or proving compliance with a change-management process. The audit log answers "what changed, and when" without requiring you to instrument your own deployment pipeline.

## ⚙️ How it works (in brief)

Every time a component's schema changes on reopen — a compatible evolution (fields added/removed/widened) or a breaking migration — Typhon appends one entry to a built-in `SchemaHistoryR1` audit trail before the new schema becomes active. Each entry captures what changed (field counts by kind), how many entities were touched, and how long it took. Entries are never updated or deleted, and a component that reopens identical to its persisted schema produces no entry. The trail is itself an ordinary system component, so it survives reopen and is queried like any other audit data.

## 💻 Usage

```csharp
// After opening the database and registering components (some of which may have evolved)
var history = dbe.GetSchemaHistory();

foreach (var entry in history)
{
    Console.WriteLine(
        $"{new DateTime(entry.Timestamp, DateTimeKind.Utc):u} " +
        $"{entry.ComponentName} R{entry.FromRevision}->R{entry.ToRevision} " +
        $"{entry.Kind} (+{entry.FieldsAdded}/-{entry.FieldsRemoved}/~{entry.FieldsTypeChanged} fields, " +
        $"{entry.EntitiesMigrated} entities, {entry.ElapsedMilliseconds}ms)");
}
```

Equivalent from the shell:

```
tsh> schema-history

  Timestamp            Component        Change             Entities   Time
  ──────────────────── ──────────────── ────────────────── ────────── ──────
  2026-02-20 14:30:05  Game.Player      R1→R2 compatible     15,000  0.3ms
  2026-02-22 09:15:32  Game.Inventory   R1→R2 migration        8,500  12ms
```

| `Kind` value | Recorded when |
|--------------|----------------|
| `Compatible` | Auto-resolved evolution — fields added/removed, lossless type widenings |
| `Migration` | A registered migration function ran for a breaking type change |
| `SystemUpgrade` | Reserved for future internal system-schema revisions — not currently emitted |

## ⚠️ Guarantees & limits

- Append-only: entries are never modified or deleted by Typhon; `GetSchemaHistory()` returns the full trail in chronological (insertion) order.
- One entry per component per reopen where a real schema difference is detected — identical reopens (no field/type/index changes) write nothing.
- `EntitiesMigrated`/`ElapsedMilliseconds` are populated only for `Migration` entries; `Compatible` entries report zero/the field-count deltas.
- The history append happens immediately after the schema change is persisted, in the same registration call — but as a separate write, not one atomic unit with it.
- The trail itself has no built-in retention/pruning — it grows for the life of the database. Plan for this if a database undergoes very frequent schema churn.

## 🧪 Tests

- [OperationalToolingTests](../../../test/Typhon.Engine.Tests/Data/Schema/OperationalToolingTests.cs) — `SchemaHistory_RecordedOnFieldAdd` (entry recorded with correct `Kind`/field-count deltas), `SchemaHistory_SparseKeys_ReturnsAll` (history readable at a high PK offset)

## 🔗 Related

- Sibling: [tsh Schema Shell Commands](tsh-schema-commands.md) — `schema-history` reads the same audit trail this feature writes
- Sibling: [Migration Progress Tracking](migration-progress-tracking.md) — the live counterpart; this feature records the permanent trail after the fact

<!-- Deep dive: claude/design/Schema/05-operational-tooling.md §5 Schema History Log, claude/overview/04-data.md §4.10 Schema Evolution -->
