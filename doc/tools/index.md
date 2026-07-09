---
uid: tool-index
title: Tools
description: The Typhon toolchain — the Workbench GUI and the typhon CLI — and when to reach for each.
---

# Tools

Typhon is a **complete technology, not just a library**. Beyond the embeddable engine, it ships with a
first-class toolchain for working with a database — visually and from the command line:

| Tool | What it is | Reach for it when… |
|------|------------|--------------------|
| **[Workbench](workbench/index.md)** | A local database + performance-analysis GUI (React SPA served over loopback), launched with `typhon ui`. | You want to *explore* — browse schema, entities, storage layout, and profiling traces interactively. |
| **[CLI](cli/index.md)** | The `typhon` command: an interactive REPL, script runner, and CI probe. | You want to *automate* — script setup, mutate data, or assert invariants in CI. |

Both host the same in-process engine — there is no server, no wire protocol, and no separate client to
install. The Workbench even ships *inside* the CLI: `typhon ui` is the launcher.

## When to use which

The two tools split cleanly along **read vs. write** and **interactive vs. automated**:

- **Workbench = interactive, read-mostly exploration & profiling.** The backend is read-only by design.
  It is the place for visual investigation: navigating archetypes, drilling into byte/cache-line layouts,
  walking the on-disk file map, and analysing profiler timelines and call trees. Think *DataGrip fused with
  a flight recorder*.
- **CLI = automation, scripting, mutation, and CI.** Everything a GUI cannot do: a REPL and `.tsh` scripts,
  entity CRUD inside explicit transactions, machine-readable `json`/`csv` output, exit codes, and CI
  assertions (`btree-validate`, schema checks).

They are complementary, not redundant. A typical loop: script fixtures and mutations with the CLI, then
open the result in the Workbench to see what the engine did with them.

## Next steps

- **[Workbench overview](workbench/index.md)** — the three modes (Open, Trace, Attach) and how to launch it.
- **[CLI overview](cli/index.md)** — install, the REPL, and the command groups.
- **[CLI scripting](cli/scripting.md)** — `.tsh` files, output formats, and CI patterns.
