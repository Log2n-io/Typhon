# scripts/

Tooling for fast iteration. Used by both the team and Claude.

## test-affected.py

Run only the test fixtures that actually exercise the files you edited. Default first run during iteration; the full suite is for handoff.

```bash
# Single file
python3 scripts/test-affected.py src/Typhon.Engine/Concurrency/AccessControlSmall.cs

# Multiple files
python3 scripts/test-affected.py src/Typhon.Engine/Foo.cs src/Typhon.Engine/Bar.cs

# Stdin (typical: pipe from git)
git diff --name-only | python3 scripts/test-affected.py -
git diff --name-only HEAD~3 | python3 scripts/test-affected.py - --dry-run

# What would it run? (don't actually run)
python3 scripts/test-affected.py src/X.cs --dry-run
```

Behavior:
- Reads `coverage/test-affected-map.json` (built by `build-test-affected-map.py`) to map src files → test fixtures based on real per-fixture coverage data.
- Falls back to a naming-convention guess (`Foo.cs` ↔ `FooTests`) when the map is missing or doesn't list the file.
- Falls back to the full test suite when:
  - No fixture can be inferred for a file.
  - The union of affected fixtures exceeds 50 % of all fixtures (cross-cutting types like `WaitContext`).

Defaults: Debug config, `--no-build`, runs the same `dotnet test` command you'd run by hand.

## build-test-affected-map.py

(Re)builds `coverage/test-affected-map.json` by running `dotnet-coverage` once per fixture and pivoting the cobertura output. **Slow** (~30 min — one filter run per fixture). Run when:

- The fixture set has changed materially (new test class, large rename, fixture reorg).
- A refactor moved code between source files.
- It's been a while and you want refreshed empirical data.

```bash
python3 scripts/build-test-affected-map.py
```

Reads `coverage/fixtures.txt` for the fixture list (regenerate that with a one-liner extracting class names from the suite if it's stale).

## typhon-dev.ps1

Run the **local** CLI build as if it were the installed `typhon` dotnet tool. Publishes `src/Typhon.Shell` to
`artifacts/typhon-dev` (gitignored) and registers a global `typhon-dev` PowerShell function pointing at it.

```powershell
. scripts/typhon-dev.ps1              # publish (Debug) + register
. scripts/typhon-dev.ps1 -NoBuild     # re-register only — fast, for a fresh shell
. scripts/typhon-dev.ps1 -Configuration Release

typhon-dev --version
typhon-dev ui --open-db               # browse the database in the current directory
typhon-dev ui --open-latest           # open the newest ./captures/*.typhon-trace
```

Named `typhon-dev`, not `typhon`, so the globally-installed `Typhon.Cli` tool keeps working untouched and the two can
be compared side by side. Re-running is idempotent (a re-declared function replaces the previous one) and repoints it
at the fresh publish, so call it again after any CLI change. Registration lasts for the session; the script prints the
one-liner to add to `$PROFILE` if you want it permanent.

> **Why it publishes rather than builds.** `typhon ui` serves the Workbench SPA from `AppContext.BaseDirectory/wwwroot`,
> and that `wwwroot` is injected by a **publish-only** MSBuild target (`_IncludeWorkbenchSpaInPublish`, hooking
> `ComputeResolvedFilesToPublishList`). A plain `dotnet build` output therefore has no SPA next to `typhon.dll`, and
> `typhon ui` answers a bare `404` on `/` with no warning — a build output is not a usable CLI. The script warns if
> `wwwroot` is missing (run `scripts/workbench-bootstrap.ps1` once to build the SPA).
