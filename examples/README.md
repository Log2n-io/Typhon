# Examples

Standalone, illustrative projects that show how to build on Typhon. Unlike `test/`, these are **not** part of the test suite and are **not** bundled into any shipped artifact — they exist to be read and, optionally, loaded on demand.

## `Typhon.ARPG.Shell`

The canonical example of a **`tsh` extension command**. It defines an ARPG-flavoured schema (characters, items, monsters, crafting…) and an `arpg-generate` command that populates a database with configurable test scenarios.

It demonstrates the extension mechanism shipped in `Typhon.Shell.Extensibility`:

- Subclass `ShellCommand` (see `ArpgGenerateCommand.cs`), declare `Name` / `Description` / `DetailedHelp`, and implement `Execute`.
- `tsh` discovers such commands automatically when the containing assembly (or a sibling in the same directory) is loaded via `load-schema` / `-s`.

Load it into `tsh` on demand:

```bash
tsh mydb.typhon -s path/to/Typhon.ARPG.Shell.dll -c "arpg-generate full"
```

> **History:** this project used to live under `test/` and was hard-referenced by `src/Typhon.Shell`, which meant the shipped `tsh` bundled a test fixture generator. As part of the tsh rationalization ([#454](https://github.com/log2n-io/Typhon/issues/454)) that reference was dropped and the project moved here — the extension *mechanism* stays first-class in `tsh`; this is just its sample consumer. Its schema (`test/Typhon.ARPG.Schema`) stays in `test/` because the benchmark suite depends on it.
