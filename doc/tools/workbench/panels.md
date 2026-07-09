---
uid: tool-workbench-panels
title: Workbench Panels
description: The panels and views available in each Workbench mode — inspection, profiling, and observation surfaces.
---

# Panels & views

The Workbench frames every session in a single consistent shell: a **navigator** on the left ("what
exists"), a **workspace** of dockable analysis panels in the center, a universal **Inspector** on the
right ("what's selected"), and a **drawer** at the bottom for logs and top spans. Which panels are
available depends on the [session mode](index.md#the-three-modes).

> 📷 Screenshot to follow.

## Inspect (Open mode)

Surfaces for understanding a `.typhon` database's schema, data, and physical storage. The navigator is
**Archetype-rooted** — the archetype is the Workbench's "table"-equivalent, the hub of three drills:
*structure* (components → fields → byte layout), *data* (entities → component values), and *storage*
(segments → pages → chunks → cells).

| Panel | What it shows |
|-------|---------------|
| **Schema Explorer** | Archetype-rooted browser of the schema; components appear as children of archetypes and via a direct Types entry. The default center panel in Open mode. |
| **Archetype Inspector** | A deep, tabbed view of one archetype: its Components, its Entities (→ Data Browser), its Storage (segments/occupancy), and the indexes it carries. |
| **Component Inspector** | A deep view of one component type: byte/cache-line **Layout**, its **Indexes**, **Storage mode**, **Used in** (which archetypes carry it), and **Relationships** (who reads / who writes). Distinguishes type-global facts from per-archetype ones. |
| **Data Browser** | The entity list for an archetype — the grid of actual data. |
| **Database File Map** | The physical on-disk map: segments, pages, and chunks laid out spatially. |
| **Storage Health** | Segment occupancy, dirty/fill state, and WAL health. |
| **Resource Tree** | The left navigator for Open mode — the engine's resource graph. |

## Profile & Observe (Trace / Attach modes)

Surfaces for finding and explaining where time goes. In these modes the left navigator becomes a
**Systems & Queries navigator**, and a **global time window** (set on the profiler minimap) narrows every
panel simultaneously — the "linked time selection" pattern from every profiler.

| Panel | What it shows |
|-------|---------------|
| **Profiler Timeline** | The center panel — spans over time, tick by tick, with a minimap for selecting the global time window. |
| **Top Spans** | The heaviest spans, in the drawer. |
| **Call Tree** | The flame-graph / call-tree "sandwich" view, scoped to the selected span or system. |
| **System DAG** | The scheduling dependency graph of systems for the tick. |
| **Critical Path** | The longest dependency chain through the tick — where the budget actually went. |
| **Data Flow** | Which systems read/write which components, as a timeline or matrix. |
| **Query Analyzer** | The query catalog with P50/P95/P99 latency and selectivity, plus per-query Plan and Executions tabs. |
| **Source Preview** | The source location (`file:line`) behind a span or frame. |
| **Engine / Live Health** | (Attach only) Consolidated live health metrics with jump-to-anomaly. |

## Query & authoring

| Panel | What it shows |
|-------|---------------|
| **Query Console** | (Open mode) Chip/DSL query authoring with a live cost estimate, a result grid, saved queries, and history. |

## Always available

| Surface | Role |
|---------|------|
| **Inspector** (right rail) | A universal, compact summary of *whatever is currently selected* — any object type, recency-arbitrated, with a containment context stack (e.g. `Archetype ⊃ Component ⊃ Field`). |
| **Logs** (drawer) | Engine/trace events and UI housekeeping, collapsed by default. |
| **Global Context Bar** | The active session identity, environment colour tag, global scope (time window or revision), and a breadcrumb of the current drill path. |
| **Settings** | Preferences (a modal). |

## Next

- **[Keyboard & command palette](keyboard.md)** — how to reach any of these panels and objects by keyboard.
