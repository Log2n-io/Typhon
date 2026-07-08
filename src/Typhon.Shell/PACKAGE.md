# Typhon.Cli

**The `typhon` command-line tool for the Typhon database engine — a terminal shell _and_ a local database GUI.**

`typhon` is an interactive shell (REPL), script runner, and the host for the **Typhon Workbench** (a full local
database GUI) for [Typhon](https://typhondb.io) — a real-time, low-latency ACID database engine built on an ECS
architecture with MVCC snapshot isolation. Use it to create, open, query, and inspect Typhon databases from the
terminal, load component schemas, run `.tsh` scripts — or explore your data visually with `typhon ui`.

> ⚠️ **Pre-alpha.** This package is published as a prerelease. Commands, output, and on-disk formats will
> change without notice until the first stable release. Not for production use yet.

## Workbench UI — `typhon ui`

Beyond the terminal, `typhon` bundles the **Typhon Workbench** — a full local database GUI, launched with a
single command:

```bash
typhon ui                 # open the Workbench in your browser
typhon ui game.typhon     # open it directly on a database
```

Think *DataGrip for Typhon*: browse entities and components, inspect schema and archetypes, author and run
queries, and view profiling and tracing — all in a modern web UI served locally over loopback (127.0.0.1),
single-user, with no setup. The Workbench ships **inside** the tool; there is nothing extra to install.

## Install

As a **global** tool:

```bash
dotnet tool install --global Typhon.Cli --prerelease
```

As a **local** (per-repo, version-pinned) tool:

```bash
dotnet new tool-manifest        # once per repo, if you don't have one
dotnet tool install Typhon.Cli --prerelease
dotnet tool run typhon          # or just `typhon` once restored
```

Prerelease packages are opt-in — the `--prerelease` flag (or checking "Include prerelease" in your IDE) is
required.

## Usage

```bash
typhon --version                 # print the tool version
typhon --help                    # list options
typhon game.typhon               # open a database and drop into the REPL
typhon game.typhon -c "count Player"     # run one command and exit
typhon game.typhon -e script.tsh         # run a script file
typhon -s bin/Game.Components.dll game.typhon   # pre-load a component schema
typhon ui                        # launch the Workbench GUI in your browser
typhon ui game.typhon            # launch the Workbench on a specific database
```

Inside the REPL, type `help` for the full command list and `exit` to quit. Startup commands can be placed in
`~/.typhonrc` (global) or `./.typhonrc` (per-directory); history is kept in `~/.typhon_history`.

## Requirements

- **.NET 10** (`net10.0`) SDK/runtime.

## Links

- Website & docs: <https://typhondb.io>
- Source: <https://github.com/log2n-io/Typhon>

## License

Source-available. See the bundled `LICENSE.md`. Pre-1.0 use is unrestricted.
