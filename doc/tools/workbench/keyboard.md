---
uid: tool-workbench-keyboard
title: Workbench Keyboard & Command Palette
description: The Workbench command palette prefixes, the Inspector, and reversible Back/Forward navigation.
---

# Keyboard & command palette

The Workbench is **keyboard-first**: every primary journey is doable without the mouse. The spine is a
single, prefix-scoped **command palette**, backed by a shared selection bus and complete navigation
history so every move is reversible.

> 📷 Screenshot to follow.

## The command palette

One entry point to everything — open or switch a session, run an action, go to any object, or jump to a
point in time. Open it with **`Ctrl+K`**, double-shift (**`Shift Shift`**), or by clicking the palette
chip in the menu bar. `Esc` closes it.

What the palette does is scoped by the **prefix** you type:

| Prefix | Mode | Examples |
|--------|------|----------|
| *(none)* | **Open / switch** a session or view (recent-first) | `Position`, `slowest tick`, `Data Browser` |
| `>` | **Run an action** (commands) | `> Reset Layout`, `> Save as Replay`, `> Toggle Off-CPU` |
| `@` | **Go to an object** in the current session | `@ Position`, `@ 2001`, `@ Lookup#4` |
| `#` | **Go to an object workspace-wide** (any object type) | `# Position`, `# system:Movement`, `# entity:104656996` |
| `:` | **Jump to a time or address** | `:00:04.321`, `:tick 8412`, `:page 1024` |
| `?` | **Prefix help** — lists the prefixes | `?` |

Results are grouped in a fixed order: recent sessions, open sessions, views in the current session,
contextual actions for the focused panel, then global commands. Actions are filtered by session kind, so a
Trace session never shows a wall of database-only commands.

## The Inspector

The right-rail **Inspector** always shows *what's currently selected* — any object type, chosen by
recency. It renders the object together with its **containment context stack** (a stack of collapsible
sections, e.g. `Archetype ⊃ Component ⊃ Field` or `Segment ⊃ Page ⊃ Chunk ⊃ Cell`), so you always see where
the selected thing sits. The Global Context Bar's breadcrumb mirrors the same chain.

## Selection is shared and reversible

Selecting an object anywhere writes a single **selection bus** that every panel reacts to — clicking a
span in the profiler lights up the owning system in the DAG, the Critical Path, and the Call Tree at once.
Every selection and view transition is recorded in navigation history, so:

- **Back / Forward** — `Alt+←` / `Alt+→` (and the mouse thumb buttons) replay the bus and restore the view.
- **Handoff verbs** — "Open in…", "Reveal in…", "Go to…", and "Jump to…" move you between panels and land
  focus inside the destination.

## Focus & navigation shortcuts

- **Between panels:** `F6` / `Shift+F6` cycle focusable panels; `Ctrl+1`…`9` focus a panel by index.
- **Straight to a destination:** `G`-chords — e.g. `G S` Schema, `G D` Data, `G M` Storage Map,
  `G P` Profiler, `G Q` Queries, `G C` Call Tree, `G G` DAG.
- **Within a panel:** arrow keys move the focused element; type-ahead speed-search filters lists and trees.
- **Back out:** `Esc` steps out (close popover → clear filter → blur to panel).

## Next

- **[Panels & views](panels.md)** — what each destination shows.
