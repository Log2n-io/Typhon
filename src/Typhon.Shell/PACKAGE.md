# Typhon.Cli

**The `typhon` command-line tool for the Typhon database engine — project scaffolder, terminal shell, and a local database GUI.**

`typhon` scaffolds runnable starter projects, opens an interactive shell (REPL) and script runner, and hosts the
**Typhon Workbench** (a full local database GUI) for [Typhon](https://typhondb.io) — a real-time, low-latency ACID
database engine built on an ECS architecture with MVCC snapshot isolation.

> ⚠️ **Pre-alpha.** This package is published as a prerelease. Commands, output, and on-disk formats will
> change without notice until the first stable release. Not for production use yet.

---

## Quickstart — from nothing to a profiled, running app

Five commands. No code to write, nothing else to install.

```bash
# 1. install the tool
dotnet tool install --global Typhon.Cli --prerelease

# 2. scaffold a runnable starter project (pins a matching engine version for you)
typhon new MyApp
cd MyApp

# 3. run it — restores the engine, deploys a world shard, ticks the runtime
#    (Release: Typhon is a performance engine, and a Debug build is not representative)
dotnet run -c Release

# 4. look at the profile you just captured
typhon ui --open-latest

# 5. browse the database itself
typhon ui --open-db
```

**What you get.** `typhon new` emits a small **real-time world shard** — a planet of characters that roam,
regenerate their HAM pools, and trade credits — across four files:

| File | What it is |
|------|------------|
| `Character.cs` | The data model: the `Character` archetype and its components, each in the storage mode its access pattern needs (SingleVersion hot state + spatial + index, one Versioned wallet, Transient scratch). |
| `Systems.cs` | The tick-loop systems: spawn, move and regenerate lock-free, keep the spatial index coherent, settle credit trades as atomic Versioned transactions. |
| `Program.cs` | Opens the engine, walks the API (spawn / read / transact / query / view), then runs the runtime. |
| `typhon.telemetry.json` | Turns profiling on. The engine self-wires it — **no profiling code in your app.** |

Step 3 writes a non-empty `./captures/*.typhon-trace` with **zero edits**, and step 4 opens it in the Workbench
profiler: per-tick spans, system costs, CPU samples, and gauges.

---

## Tracing — turning it on, off, and up

Profiling is **config-driven**: `typhon.telemetry.json` in the working directory decides what the engine emits.
Your application code never changes. Edit the file by hand, or drive it from the CLI:

```bash
typhon telemetry list                 # every flag: default / explicit / effective state
typhon telemetry effective            # just what would actually emit right now
typhon telemetry enable  Durability   # turn a subtree on
typhon telemetry disable Spatial:Query   # turn one leaf off
typhon telemetry reset   Durability   # drop the explicit setting (back to inherited)
typhon telemetry trace captures/run.typhon-trace   # where the trace is written
typhon telemetry trace --clear        # stop writing a trace file
typhon telemetry preset               # list curated bundles
typhon telemetry preset durability    # apply one
typhon telemetry edit                 # interactive full-screen tri-state tree
```

Presets: `concurrency`, `durability`, `wal`, `query`, `query-plan`, `spatial`, `scheduler`, `storage`.

> **The flag tree is parent-implies-children.** A leaf whose parent is off stays off *regardless of its own
> setting* — enabling only the leaf is a silent no-op. `typhon telemetry effective` is the way to check what you
> actually turned on; it resolves the whole tree for you.

**To turn tracing off entirely**, set `Typhon:Profiler:Enabled` to `false` (or `typhon telemetry disable` the
root). The gates compile out to near-nothing when disabled, so leaving the file in place costs you nothing.

---

## Workbench UI — `typhon ui`

`typhon` bundles the **Typhon Workbench**, a full local database GUI — served over loopback (127.0.0.1),
single-user, nothing extra to install:

```bash
typhon ui                       # open the Workbench
typhon ui game.typhon           # open it directly on a database
typhon ui --open-db             # open the *.typhon database in the current directory
typhon ui --open-latest         # open the newest capture under ./captures
typhon ui --trace run.typhon-trace     # open a specific trace
typhon ui --schema bin/Game.dll game.typhon   # interpret a database with your schema assembly
typhon ui --port 5300           # bind a different loopback port (default 5200)
typhon ui --no-browser          # start the host and just print the URL
```

Think *DataGrip for Typhon*: browse entities and components, inspect schema and archetypes, author and run
queries, and explore profiles and traces.

---

## Shell and scripting

```bash
typhon game.typhon                            # open a database and drop into the REPL
typhon game.typhon -c "count Player"          # run one command and exit
typhon game.typhon -e script.tsh              # run a script file
typhon -s bin/Game.Components.dll game.typhon # pre-load a component schema
typhon docs                                   # open the documentation site
typhon docs guide/getting-started             # open a specific page
```

Inside the REPL, `help` lists every command and `exit` quits. Startup commands can live in `~/.typhonrc`
(global) or `./.typhonrc` (per-directory); history is kept in `~/.typhon_history`.

## Install

Global tool:

```bash
dotnet tool install --global Typhon.Cli --prerelease
```

Local (per-repo, version-pinned):

```bash
dotnet new tool-manifest        # once per repo, if you don't have one
dotnet tool install Typhon.Cli --prerelease
dotnet tool run typhon          # or just `typhon` once restored
```

Prerelease packages are opt-in — the `--prerelease` flag (or "Include prerelease" in your IDE) is required.

## Requirements

- **.NET 10** (`net10.0`) SDK/runtime.

## Links

- Documentation: <https://doc.typhondb.io>
- Getting started: <https://doc.typhondb.io/latest/guides/getting-started.html>
- Website: <https://typhondb.io>
- Source: <https://github.com/log2n-io/Typhon>

## License

Source-available. See the bundled `LICENSE.md`. Pre-1.0 use is unrestricted.
