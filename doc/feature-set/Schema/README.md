---
uid: feature-schema-index
title: 'Schema'
description: 'The typed metadata layer over every component: attribute-driven struct declarations reflect into persisted field/index definitions whose FieldIds survive…'
---

# Schema
> The typed metadata layer over every component: attribute-driven struct declarations reflect into persisted field/index definitions whose FieldIds survive reordering, renames, and reopens. On every database open, the persisted layout is diffed against the running code and classified by compatibility — additions, removals, reorders, and lossless widenings migrate automatically, while genuinely breaking changes require a registered migration function rather than silently losing data. Operational tooling (offline inspection, dry-run validation, progress events, an audit log, tsh shell commands, and Workbench ALC reload) makes every one of those changes observable and verifiable before and during a real deployment.

> 🔬 **Recommended:** read [in-depth-overview/04-schema.md](../../in-depth-overview/04-schema.md) (Chapter 04: Schema) first to understand the overall design and concepts behind this category, before diving into the specific features below.

## Public Features

| Feature | Summary | Status | Level |
|---|---|---|---|
| [Component & Field Declaration](component-field-declaration.md) | Attribute-driven declaration of blittable component structs (`[Component]`, `[Field]`, `[Index]`) reflected into `DBComponentDefinition` at registration time | ✅ Implemented | 🟢 Start Here |
| [FieldId Stability](fieldid-stability.md) | Persistent, name-based FieldId assignment (auto-assign once, match by name on reopen) so adding/removing/reordering fields never breaks index identity; `PreviousName` handles renames | ✅ Implemented | 🔵 Core |
| [Schema Validation (SchemaDiff)](schema-validation.md) | On every reopen, diffs persisted vs. runtime schema, classifies every change by compatibility level, and fails loudly before any user transaction runs on unresolvable mismatches | ✅ Implemented | 🔵 Core |
| [Compatible Schema Evolution (Auto-Migration)](compatible-evolution/README.md) | Automatically migrates entities at startup for field add/remove/reorder and lossless type widenings by allocating a new stride segment while preserving ChunkIds so indexes need no rebuild | ✅ Implemented | 🔵 Core |
| &nbsp;&nbsp;↳ [Migration Execution Strategy](compatible-evolution/migration-execution-strategy.md) | Migration runs eagerly and synchronously at database open — before any user transaction — with progress events and an offline dry-run check | ✅ Implemented | 🟣 Advanced |
| [User-Defined Migration Functions](migration-functions.md) | Register pure transform functions for breaking schema changes, with automatic multi-step chain resolution across revisions | ✅ Implemented | 🟣 Advanced |
| [Offline Schema Inspection & Dry-Run Validation](schema-inspection-dryrun.md) | Read a database's persisted schema, or simulate a code upgrade against it, without opening it for real | ✅ Implemented | 🟣 Advanced |
| [Migration Progress Tracking](migration-progress-tracking.md) | `OnMigrationProgress` event stream (`Analyzing` → `AllocatingSegments` → `MigratingEntities` → … → `Complete`) for observing long-running eager migrations in production | ✅ Implemented | 🟣 Advanced |
| [Schema History Audit Log](schema-history-audit.md) | Append-only audit trail recording every applied schema change for production auditing, queried via `dbe.GetSchemaHistory()` | ✅ Implemented | 🟣 Advanced |

## Internal Features

| Feature | Summary | Status |
|---|---|---|
| [tsh Schema Shell Commands](tsh-schema-commands.md) | Typhon Shell commands (`schema-fields`, `schema-diff`, `schema-validate`, `schema-history`, `schema-export`) exposing persisted-vs-runtime schema comparison and inspection as an interactive CLI on top of the engine schema APIs | ✅ Implemented |
| [System Schema Persistence](system-schema-persistence.md) | Self-referential storage of component/field metadata as engine-internal ECS entities (`ComponentR1`/`FieldR1`) inside the database itself, loaded/saved via a minimal single-threaded CRUD layer (no MVCC/WAL) at engine open/create | ✅ Implemented |
| [Workbench Per-Session Schema Loading & ALC Reload](workbench-schema-loading.md) | Loads schema DLLs into a per-session collectible ALC so Workbench sessions can rebuild/swap binaries without restarting the host, classifying engine schema exceptions into Ready/MigrationRequired/Incompatible and rebinding component IDs by schema name across reloads | ✅ Implemented |
| [Component Family Classification](component-family-classification.md) | Classifies a component into a semantic family (Spatial/Combat/AI/Inventory/Rendering/Networking/Input/Misc) via explicit attribute or name heuristic, for stable Workbench Data Flow grouping | ✅ Implemented |