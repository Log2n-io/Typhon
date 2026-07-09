---
uid: tool-cli
title: CLI
description: The typhon command-line tool — an interactive REPL, script runner, and host for the Workbench.
---

# CLI

`typhon` is the command-line tool for Typhon — an interactive shell (REPL), a `.tsh` script runner, and the
launcher for the [Workbench](../workbench/index.md) GUI. It hosts a `DatabaseEngine` **in-process** and owns
the database file exclusively, the same way `sqlite3` does: there is no server and no wire protocol.

Its durable niche is what a GUI cannot do — **scripting, CI, and mutation**: a REPL and script files, entity
CRUD inside explicit transactions, machine-readable output, and exit codes.

> ⚠️ **Pre-alpha.** Published as a prerelease. Commands, output, and on-disk formats change without notice
> until the first stable release.

## Install

As a **global** tool:

```bash
dotnet tool install --global Typhon.Cli --prerelease
```

As a **local** (per-repo, version-pinned) tool:

```bash
dotnet new tool-manifest        # once per repo
dotnet tool install Typhon.Cli --prerelease
dotnet tool run typhon          # or just `typhon` once restored
```

Requires the **.NET 10** SDK/runtime. The `--prerelease` flag is required — prerelease packages are opt-in.

## The REPL

Run `typhon` with no arguments for the interactive shell, or pass a database to open (or create) it on
start and drop straight into the REPL:

```bash
typhon                           # start the REPL
typhon game.typhon               # open (or create) a database and drop into the REPL
typhon -s Game.Components.dll game.typhon   # pre-load a component schema assembly
```

Inside the REPL, the prompt encodes session state — `typhon:game>` when a database is open,
`typhon:game[tx:42*]>` when a transaction is active and dirty. Type `help` for the full command list and
`exit` to quit. Startup commands can live in `~/.typhonrc` (global) or `./.typhonrc` (per-directory);
history is kept in `~/.typhon_history`.

Because components are compiled C# structs, the shell loads a **schema assembly** (`-s` /
`load-schema`), discovers `[Component]` types by reflection, and uses that field map to convert your text
input into blittable structs the engine can store.

## Command groups

Type `help` for the authoritative list. The primary groups:

| Group | Commands | Notes |
|-------|----------|-------|
| **Database** | `open <path>`, `close`, `info` | `open` creates the file if it doesn't exist. |
| **Schema** | `load-schema <path>`, `reload-schema`, `schema`, `describe <component>` | Assemblies are additive; `describe` shows the field/byte layout. |
| **Transaction** | `begin`, `commit`, `rollback` | Explicit by default (`auto-commit` is off — a safety net for interactive use). |
| **Data (CRUD)** | `create [#id] <comp> { … }`, `read <id> <comp>`, `update <id> <comp> { … }`, `delete <id> <comp>` | Component name always required on read/delete. Requires an active transaction. |
| **Diagnostics** | `cache-stats`, `btree`, `btree-validate`, `mvcc-stats`, `revisions`, `memory`, `transactions` | In-process engine internals; several are JSON/CI probes (`--format json`). |
| **GUI** | `typhon ui [database]` | Launch the [Workbench](../workbench/index.md). |

Example session:

```text
typhon> open game.typhon
  Opened game.typhon (new database created)
typhon:game> load-schema Game.Components.dll
  Loaded 3 components: PlayerStats, Inventory, Position
typhon:game> begin
  Transaction started (tick 100)
typhon:game[tx:100]> create PlayerStats { PlayerId=1, Health=100.0, Score=0 }
  Entity 7 created
typhon:game[tx:100*]> commit
  Committed: 1 create, 0 updates, 0 deletes
```

## Next

- **[Scripting & CI](scripting.md)** — `.tsh` scripts, single-command mode, output formats, and CI patterns.
- **[Workbench](../workbench/index.md)** — the GUI launched by `typhon ui`.
