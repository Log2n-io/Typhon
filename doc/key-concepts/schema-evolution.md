---
uid: concept-schema-evolution
title: 'Schema evolution'
description: 'The schema lives in the database, so reopening with a changed component struct is a real, migrated operation: change the struct, bump the [Component] revision, reopen — the engine migrates stored data before your code runs.'
---

# Schema evolution

> **In one line:** the schema lives **in** the database, so changing a [component](xref:concept-component) is a real, versioned, migrated operation — not undefined behaviour.

The model from your side is deliberately simple: **change the struct** (add a field, widen `int`→`long`, …), **bump the `[Component]` revision** (`("Skirmish.Health", 1)` → `2`), and **reopen**. The engine compares the persisted schema against the runtime schema and migrates the stored data **before your code runs**. For changes it can't infer (a field computed from old data) you supply a migration function.

Reopening with a changed struct and no revision bump — or with an *older* app than wrote the data — is caught, not silently accepted (`SchemaValidationException` / `SchemaDowngradeException`). Only [`Versioned`](xref:concept-storage-mode) components carry the history migration relies on.

## How it relates

- **[Component](xref:concept-component)** — evolution is versioned per component, keyed on its `[Component]` revision.
- **[Storage mode](xref:concept-storage-mode)** — migration is a `Versioned`-layout operation.
- **[Archetype](xref:concept-archetype)** — archetype membership is part of the persisted schema.
- **[DatabaseEngine](xref:concept-database-engine)** — migration runs at `Open`, before the first read.

## In the API

- [`SchemaChangeKind`](xref:Typhon.Engine.SchemaChangeKind) — the classification of a detected schema change.
- [`SchemaHistoryR1`](xref:Typhon.Engine.SchemaHistoryR1) — the persisted schema-history record.

## Learn & use

- **Narrative:** [Guide ch.2 §3 — evolution](xref:guide-modeling)
- **Feature detail:** [schema](xref:feature-schema-index) · [compatible evolution](xref:feature-schema-compatible-evolution-index) · [migration functions](xref:feature-schema-migration-functions)
