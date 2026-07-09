---
uid: tool-cli-scripting
title: CLI Scripting & CI
description: Automate Typhon with .tsh scripts, single-command mode, machine-readable output, and CI assertions.
---

# Scripting & CI

Every interactive `typhon` command also runs non-interactively — from a script file, a single flag, or a
pipe. Combined with machine-readable output and meaningful exit codes, this makes the CLI the automation and
CI face of Typhon.

## Running commands non-interactively

```bash
typhon game.typhon -c "info"                 # single command, then exit
typhon game.typhon -e setup.tsh              # run a .tsh script file, then exit
typhon game.typhon -s Game.dll -e setup.tsh  # pre-load a schema, then run a script
echo "info" | typhon game.typhon             # pipe mode — read commands from stdin
```

Mode precedence: `-c` (single command) → `-e` (script file) → pipe (stdin not a terminal) → interactive
REPL. In all non-interactive modes, execution **halts on the first error**.

> `-c` takes exactly **one** command (a single string). For multi-command scenarios, use a `.tsh` script
> with `-e`.

## `.tsh` script files

A `.tsh` script is one command per line — identical to REPL input — following the `sqlite3` model: no
variables, no control flow, no expressions. Complex logic belongs in your host shell (bash/PowerShell) or
in C#. Lines starting with `#` are comments; blank lines are ignored.

The one structural extension is **data block directives** for bulk inserts — `@compact` (positional values
in schema field order) and `@json` (a JSON array or NDJSON), each terminated by `@end`:

```text
# setup.tsh
open test.typhon
load-schema "Game.dll"

begin

# Brace format — one entity at a time
create PlayerStats { PlayerId=1, Health=100.0, Score=0 }

# Compact format — bulk positional data (PlayerId, Health, Score)
@compact PlayerStats
2, 85.5, 12
3, 92.0, 5
@end

# JSON format — machine-generated data
@json Inventory
[
  { "Slots": 20 },
  { "Slots": 30 }
]
@end

commit
close
```

## Output formats

Pick the format for the consumer with `-f` / `--format` (or `set format` in the REPL):

| Format | Best for | Shape |
|--------|----------|-------|
| `table` | Interactive reading (default) | Compact `Entity 1 \| Field=Value  Field=Value` |
| `full-table` | Interactive reading | Bordered table with column headers |
| `json` | CI, pipes, `jq` | JSON array of objects |
| `csv` | Spreadsheets, data export | CSV with a header row |

```bash
typhon game.typhon --format json -c "read 1 PlayerStats"
```

Several diagnostic probes (`cache-stats`, `mvcc-stats`, `memory`, `transactions`, `btree`) emit structured
data under `--format json`, so CI can parse engine internals directly.

## CI assertion patterns

The CLI's exit codes drive CI gating — `0` success, `1` command error, `2` database error, `10` unhandled
exception. In batch mode the exit code reflects the first error encountered.

Validate integrity and schema in a pipeline, letting a non-zero exit fail the job:

```bash
# Fail the build if any B+Tree is structurally invalid
typhon game.typhon -c "btree-validate" || exit 1

# Assert an expected field value with jq
HEALTH=$(typhon game.typhon --format json -c "read 1 PlayerStats" | jq '.[0].Health')
[ "$HEALTH" = "100" ] || { echo "unexpected health: $HEALTH"; exit 1; }

# Seed a fixture database, then check it in one script run
typhon test.typhon --schema Game.dll -e fixtures/setup.tsh
```

Because the CLI is stateless between invocations and composes with ordinary Unix pipes, all
branching/looping stays in the host shell — the CLI just executes and reports.

## Next

- **[CLI overview](index.md)** — install, the REPL, and command groups.
