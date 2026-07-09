---
uid: tool-workbench
title: Workbench
description: The Typhon Workbench — a local database + performance-analysis GUI launched with typhon ui.
---

# Workbench

The **Typhon Workbench** is a professional-grade local developer tool for understanding a Typhon system —
its data *and* its performance. It is **DataGrip fused with a flight recorder / profiler**: open a database
to inspect schema and data, open a captured trace to find why a tick blew its budget, or attach to a live
engine to watch it run — all in one keyboard-first UI, without switching tools or losing context.

> 📷 Screenshot to follow.

## Launch it

The Workbench is served by the CLI — there is nothing extra to install:

```bash
typhon ui                 # open the Workbench in your browser
typhon ui game.typhon     # open it directly on a database
```

`typhon ui` starts a local **Kestrel host on `127.0.0.1:5200`** (loopback only, never a routable
interface) and opens your browser at the **React single-page app**. It is a single-user local dev tool: a
bootstrap token gates every API call, and each session carries its own session token. See the
[keyboard & command model](keyboard.md) for how to drive it.

## The three modes

A session in the Workbench is one of three kinds. Each is a different lens on a Typhon system, and the UI
adapts its navigator, default panels, and available commands to the mode you are in.

| Mode | Source | Pillar | What it is for |
|------|--------|--------|----------------|
| **Open** | a `.typhon` database directory on disk | Inspect | Browse schema, archetypes, entities, indexes, and physical storage layout. |
| **Trace** | a `.typhon-trace` capture file | Profile | Replay a recorded run to find and explain where time went, tick by tick. |
| **Attach** | a live, running engine | Observe | Watch an engine execute in real time and capture it for later analysis. |

- **Open** hosts the engine in-process against a `.typhon` bundle (a directory). It is read-only —
  inspection never mutates the database. Use the [CLI](../cli/index.md) when you need to write.
- **Trace** loads a profiler capture: no live engine, just the recorded spans, systems, and query
  executions, browsable across the whole captured time window.
- **Attach** connects to a running engine and streams its state live, with the same profiling surfaces as
  Trace plus live-health panels.

The Workbench's real differentiator is **Navigate**: selecting an object in any panel drives every other
panel through a shared selection bus, and every transition is reversible with Back/Forward — so you can
move between *inspect*, *profile*, and *observe* concerns in a single gesture.

## Next steps

- **[Panels & views](panels.md)** — the surfaces available in each mode.
- **[Keyboard & command palette](keyboard.md)** — the prefix-scoped palette, the Inspector, and navigation.
