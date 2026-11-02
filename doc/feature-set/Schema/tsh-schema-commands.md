---
uid: feature-schema-tsh-schema-commands
title: 'tsh Schema Shell Commands'
description: 'Inspect, diff, validate, and export a database''s persisted schema interactively, without writing code.'
---

# tsh Schema Shell Commands
> Inspect, diff, validate, and export a database's persisted schema interactively, without writing code.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Schema](./README.md)

## 🎯 What it solves

Checking schema state today means writing a throwaway program against `DatabaseSchema.Inspect`/`ValidateEvolution`, or stepping through code in a debugger. During day-to-day development and ops triage — "what FieldId did Score actually get?", "will my new build open this file?", "what changed last Tuesday?" — that overhead is disproportionate to the question. The Typhon Shell (`tsh`) exposes the same persisted-schema-vs-runtime-schema comparison as five interactive commands, so these questions get answered in seconds at a prompt.

## ⚙️ How it works (in brief)

The commands read two things `tsh` already has once a database is open: the **persisted** schema (component/field metadata stored in the file, surfaced via `DatabaseEngine.PersistedComponents` / `PersistedFieldsByComponent`) and the **runtime** schema (component structs loaded into the shell via `load-schema`). `schema-diff` and `schema-validate` compute a `SchemaDiff` between the two and report the compatibility level Typhon would use on next open. `schema-fields` and `schema-export` read persisted metadata only — no runtime type needed. `schema-history` reads the `Typhon.Schema.History` system component, an append-only audit trail of every schema change ever applied. Output is color-coded markup in the console, or structured JSON/CSV via `set format` for piping into other tools.

`schema-validate` and `migrate` also report **archetypes** whose stored component set differs from the loaded one ([Archetype Component-Set Evolution](./archetype-set-evolution.md)). `schema-validate` previews: it runs the open's archetype checks on in-memory state only and writes nothing. Each archetype is listed as `MIGRATE` (a component added, inserted or reordered with a higher revision, or a metadata-only revision bump / names refresh), `SKIPPED` (one of its components is not registered in this session, so the open would not open it — with what it would do once registered), or `REFUSED` (the open would throw `ArchetypeSchemaException`: a set change without a revision increase, a downgrade, a removed component, an unreadable stored list, a component migrating under an archetype the open skips). Refusals that depend on the stored entities — an added uniquely indexed component, an added spatial field or realm key, an added Versioned component with leftover revision chains — are listed as `UNCHECKED`: only the open reads the entities. `migrate` builds the archetypes, so it applies the migrations and reports each one with its entity count and time, or the refusal that stopped the open.

## 💻 Usage

```
tsh> open game.typhon
tsh> load-schema bin/Game.Components.dll

tsh> schema-fields Player
  FieldId  Name      Type    Offset  Size  Index
        0  Health    Int32        0     4
        1  Mana      Int32        4     4  Unique
        2  Armor     Int32        8     4
        4  Shield    Int32       12     4   ← FieldId 3 (Legacy) was removed

tsh> schema-diff Player
  Level: Compatible  Summary: +1 field, -1 field
  + Shield (Added, FieldId=4)
  - Legacy (Removed, FieldId=3)
  Stride change: 24 → 28 bytes
  Entity count: 15000

tsh> schema-validate
  OK       Game.Inventory   — identical
  MIGRATE  Game.Equipment   — Durability Int32→Float (migration registered)
  All 2 component(s) valid.

  Archetypes
  MIGRATE Game.Player — rev 3→4, set change: +Game.PlayerGear at slot 3
  SKIPPED Game.Npc — its component NpcMood is not registered with this engine (RegisterComponentFromAccessor<NpcMood>()); once registered, rev 1→2 would migrate: +Game.NpcMood at slot 2
  UNCHECKED Archetype 'Game.Player' gains components whose refusal depends on its entities ('Game.PlayerGear.SerialNo' is a unique, non-sparse index), which only the open reads: not checked by this preview.
  1 archetype(s) would migrate at open ('migrate' applies them), 1 would be skipped, 0 would refuse the open. Nothing was changed.

tsh> migrate
  ...
  Archetypes
  MIGRATED Game.Player — rev 3→4, +Game.PlayerGear at slot 3 (15000 entities, 41.7 ms)
  1 archetype set(s) migrated.

tsh> schema-history
  2026-02-20 14:30:05  Game.Player  rev 1→2 (Compatible)  15000  0ms

tsh> set format json
tsh> schema-export Player
```

| Command | Purpose |
|---------|---------|
| `schema-fields <component>` | Persisted FieldId → name/type/offset/index mapping |
| `schema-diff <component>` | Persisted vs. runtime diff, with compatibility level |
| `schema-validate` | Dry-run diff for every loaded component against the open database, and a preview of the archetype set changes the next open would migrate, skip or refuse |
| `schema-history` | Append-only audit trail of past schema changes |
| `schema-export [component]` | Persisted schema as table/JSON/CSV (`set format`) |

## ⚠️ Guarantees & limits

- Read-only: none of these commands register a component, allocate storage, or migrate a single entity — `schema-diff`/`schema-validate` reuse the same dry-run diff machinery the engine uses on real open, but never apply it. The archetype preview builds nothing and commits nothing, including the component migrations the session's registration staged; `migrate` is the command that applies them.
- The archetype preview cannot load the stored clusters, so it never reports a stored cluster that cannot be read, and it decides the entity-dependent refusals only for an archetype that never held an entity (`UNCHECKED` otherwise).
- Component name resolution accepts a short name (suffix match, case-insensitive) or the full registered `[Component]` name — no need to type the fully-qualified name.
- `schema-diff`/`schema-validate` need a runtime type loaded via `load-schema` to compare against; `schema-fields`/`schema-export`/`schema-history` work from persisted metadata alone.
- `schema-validate` reports `MIGRATE` (not `FAIL`) only when a breaking change has a registered migration chain reachable from the persisted revision to the runtime revision; otherwise it reports `FAIL` with no path to open the database as-is.
- `schema-history` is empty until at least one schema change (compatible evolution or migration) has actually been applied to the database.
- All five commands require an open database (`open <path>`); there is no offline/file-only mode in the shell — for that, use `DatabaseSchema.Inspect`/`ValidateEvolution` directly (see Related).

## 🔗 Related

- Related feature: [Offline Schema Inspection & Dry-Run Validation](./schema-inspection-dryrun.md) (`schema-fields`/`schema-diff`/`schema-validate` are interactive wrappers over `DatabaseSchema.Inspect`/`ValidateEvolution`'s underlying diff engine)
- Sibling: [Schema History Audit Log](schema-history-audit.md) — `schema-history` reads the same append-only audit trail this feature persists
- Source: `src/Typhon.Shell/Commands/SchemaCommandExecutor.cs`

<!-- Design: claude/design/Schema/05-operational-tooling.md §6 tsh Shell Integration -->
