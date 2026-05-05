# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Opinion vs Action

When the user asks for your opinion on a design choice, code approach, or any topic — give your opinion only and challenge the user on their choices. Do NOT edit files or make changes; wait for explicit instructions to proceed.

## Key Documentation Resources

Typhon maintains comprehensive documentation in the `claude/` directory.
Use these resources to understand architecture, design rationale, and development workflow.
When working on a new idea, always start by reading relevant documents in `claude/overview`, you can also read files in `claude/design` to get more context of existing features and APIs.
Architecture Design Records are located in `claude/adr`, use it when designing new code.
To estimate the CPU time taken by a given algorithm, base yourself on `claude/design/cpu-timings.md`.

> **Separate git repo:** The `claude/` directory is its own nested git repository. To commit or perform any git operations on documentation files, you must `cd claude/` first. Running `git status` from the Typhon root will not show changes to `claude/` files.

## Project Overview
Typhon is a real-time, low-latency ACID database engine with microsecond-level performance targets, using an ECS architecture with MVCC snapshot isolation.

### Design Doc Alignment
Before proposing implementations or design changes, ALWAYS read the relevant existing design documents first (in `claude/design/`) and verify alignment. 
Never deviate from established specs without explicitly noting the deviation and getting user approval.

### Quick Navigation

| When You Need... | Go To | Key Contents |
|------------------|-------|--------------|
| **How the engine works** | `claude/overview/` | 11-part architecture guide covering all subsystems |
| **Why a decision was made** | `claude/adr/` | 30 Architecture Decision Records with rationale |
| **What must always hold** | `claude/rules/` | Correctness invariants by domain (WAL, checkpoint, page safety) |
| **Current priorities** | [GitHub Project](https://github.com/users/nockawa/projects/7) | Work tracking, status, roadmap |
| **Feature designs & docs** | `claude/design/` | Implementation specs and API docs (stay here after implementation) |
| **Deep research** | `claude/research/` | Analysis studies (e.g., timeout patterns, query systems) |
| **Document workflows** | `claude/CLAUDE.md` | Lifecycle, templates, trigger phrases |

### Architecture Overview Series

The `claude/overview/` directory is the **authoritative architectural reference**:

| # | Document | Focus |
|---|----------|-------|
| 01 | [Concurrency](claude/overview/01-concurrency.md) | AccessControl, latches, deadlines, thread safety |
| 02 | [Execution](claude/overview/02-execution.md) | UnitOfWork, durability modes, commit path |
| 03 | [Storage](claude/overview/03-storage.md) | PagedMMF, page cache, segments, I/O |
| 04 | [Data](claude/overview/04-data.md) | MVCC, ComponentTable, indexes, transactions |
| 05 | [Query](claude/overview/05-query.md) | Query parsing, filtering, sorting |
| 06 | [Durability](claude/overview/06-durability.md) | WAL, crash recovery, checkpoints |
| 07 | [Backup](claude/overview/07-backup.md) | Incremental backup, restore |
| 08 | [Resources](claude/overview/08-resources.md) | Memory budgets, resource graph |
| 09 | [Observability](claude/overview/09-observability.md) | Telemetry, metrics, diagnostics |
| 10 | [Errors](claude/overview/10-errors.md) | Error model, exception hierarchy |
| 11 | [Utilities](claude/overview/11-utilities.md) | Allocators, disk management, shared utilities |
| 13 | [Runtime](claude/overview/13-runtime.md) | DagScheduler, system types, parallel dispatch, subscriptions, overload |

### Correctness Rules

The `claude/rules/` directory is a curated database of invariants that define correctness in Typhon. Rules are the **source of truth** — code and tests must conform to them. Each rule file covers one domain (e.g., durability, concurrency), grouped by module, with invariants expressed in pseudo-code. When modifying code, cross-reference affected modules against the rule database to ensure no invariant is violated. See [`claude/rules/README.md`](claude/rules/README.md) for conventions and notation.

### Documentation-Heavy Project
This project is documentation-first. Most work involves creating, updating, or refining markdown design docs, ADRs, and planning documents. When updating docs, preserve existing structure and version headers. Cross-reference related documents. Always check for consistency across the full doc set when making changes.

### D2 Diagrams
Architecture diagrams use the **D2** language. Source files live in `claude/assets/src/*.d2`, rendered SVGs in `claude/assets/*.svg`.

- **Conventions:** See [`claude/d2-conventions.md`](claude/d2-conventions.md) for color palette, shapes, and patterns
- **Render:** `"/c/Program Files/D2/d2.exe" --theme 0 assets/src/name.d2 assets/name.svg`
- **Viewer:** Open `claude/assets/viewer.html` for interactive pan-zoom
- **After adding:** Update the `DIAGRAMS` array in `viewer.html`

## Build & Development Commands

**Build the solution:**
```bash
dotnet build Typhon.slnx
```

**Build specific configurations:**
```bash
dotnet build -c Debug
dotnet build -c Release
```

**Run all tests:**
```bash
dotnet test
```

**Run tests from specific project:**
```bash
dotnet test test/Typhon.Engine.Tests/Typhon.Engine.Tests.csproj
```

**Run a single test:**
```bash
dotnet test --filter "FullyQualifiedName~TransactionTests.CreateComp_SingleTransaction_SuccessfulCommit"
```

**IMPORTANT — Test timeout safety:** Typhon unit tests should complete in under 5 seconds. If tests run longer, it almost certainly means an infinite loop or deadlock. When running tests, ALWAYS use a 15-second timeout and kill the process if it hasn't completed. Use `timeout 15` (on Windows) or equivalent to enforce this.

### Test selection during iteration — use `scripts/test-affected.py`

After editing a file, **default to running only the fixtures that exercise it** instead of the full suite. The full suite takes ~30 s; a fixture-scoped run is typically 1–3 s.

```bash
python3 scripts/test-affected.py src/Typhon.Engine/Concurrency/AccessControlSmall.cs
# → resolves to: dotnet test --filter "FullyQualifiedName~AccessControlSmallTests." --no-build -c Debug
```

The script:
- Reads `coverage/test-affected-map.json` (built by `scripts/build-test-affected-map.py` — periodic refresh) to map src files to the fixtures that empirically cover them.
- Falls back to a naming-convention guess (`Foo.cs` → `FooTests`) when the map is stale or missing.
- Falls back to the **full suite** automatically if the affected set is >50 % of all fixtures (e.g., a cross-cutting type like `WaitContext`), or if no fixture can be inferred.
- Accepts multiple files; unions the affected fixtures.
- For test-side edits, the file IS the fixture — no inversion needed.

**Default workflow when iterating:**
1. Edit a file.
2. `python3 scripts/test-affected.py <file>` — fast feedback (1–3 s typical).
3. Once green, run the **full suite** once before declaring the change done: `dotnet test test/Typhon.Engine.Tests/Typhon.Engine.Tests.csproj -c Debug --no-build`.
4. For perf-claim work (measuring wall-clock impact), run Release × 3 with `--logger trx`.

**Rebuilding the map:** the builder is **incremental**.
- `python3 scripts/build-test-affected-map.py` — re-collects only fixtures whose test source has changed since the cached XML. ~0.3 s when nothing changed; ~5 s per touched fixture.
- `python3 scripts/build-test-affected-map.py --force` — re-collects everything (~25 min). Run this only after a refactor that moves classes between files (where mtime won't catch the change).
- `python3 scripts/build-test-affected-map.py --only Fix1 Fix2` — re-collect specific fixtures (recover from failed runs).

The initial build (no cache) is ~25 min. Steady-state during normal work is seconds.

**Run benchmarks:**
```bash
cd test/Typhon.Benchmark
dotnet run -c Release
```

**Run specific benchmark:**
```bash
cd test/Typhon.Benchmark
dotnet run -c Release --filter '*PagedMemoryFile*'
```

### Quick POC / single-file scripts (.NET 10)

For throwaway experiments, repro cases, or API exploration that don't belong in the test suite, use .NET 10 single-file execution — no `.csproj` required:

```bash
dotnet run poc.cs
```

**File structure** — no `Main`, no namespace, no class boilerplate:

```csharp
// poc.cs  — top-level statements, types below
Console.WriteLine("hello");
```

**Directives** — all must be at the top of the file, before any code:

| Directive | Effect | Example |
|-----------|--------|---------|
| `#:package` | Pull a NuGet package | `#:package BenchmarkDotNet@0.14.0` |
| `#:sdk` | Switch SDK (default: `Microsoft.NET.Sdk`) | `#:sdk Microsoft.NET.Sdk.Web` |
| `#:property` | Set an MSBuild property | `#:property AllowUnsafeBlocks true` |
| `#:project` | Reference an existing `.csproj` | `#:project src/Typhon.Engine/Typhon.Engine.csproj` |

**Typical Typhon POC skeleton** — references the engine directly:

```csharp
#:property AllowUnsafeBlocks true
#:project src/Typhon.Engine/Typhon.Engine.csproj

using Typhon.Engine;

// experiment here — full engine available
```

**Grow it into a proper project** when the POC needs to live on:

```bash
dotnet project convert poc.cs   # generates poc.csproj from directives
```

**Constraints**: single file only; requires .NET 10 SDK (`dotnet --version` ≥ 10.0); not for production. Place throwaway scripts in `scratch/` (gitignored) to avoid polluting the repo root.

## Important Implementation Details

### Performance considerations
- Always try to **control/optimize memory indirection** to reduce CPU cache miss and maximize data locality. 
- **Cache-line aware**: Every memory access fetches an entire cache line. 
- Prefer Structure of Arrays (SOA) layout over Array of Structures.

### Unsafe Code & Performance
- Project uses `<AllowUnsafeBlocks>true` extensively
- Heavy use of pointers, stackalloc, and unmanaged memory for performance
- GCHandle pins page cache to avoid GC moves
- Blittable struct requirements for components ensure zero-copy operations

### Coding Standards
- **Follow `.editorconfig`**: All C# code must follow the formatting rules in `/.editorconfig`. Key rules include:
  - Expression-bodied members for simple methods/properties (`=>` syntax)
  - Braces on new lines (`csharp_new_line_before_open_brace = all`)
  - Always use braces for control flow statements
  - Collection expressions (`[]` instead of `Array.Empty<T>()`)
  - Private fields use `_camelCase` (underscore prefix)
  - Use `ArgumentNullException.ThrowIfNull()` for null checks
- **160 column max line length**: Lines must not exceed 160 characters. When a statement exceeds this limit:
  - Method parameters: Wrap after opening parenthesis, one parameter per line
  - Method arguments: Wrap after opening parenthesis, one argument per line
  - Chained calls: Wrap before the dot
  - Binary expressions: Wrap before the operator
  - Collection initializers: Wrap elements if line is too long
- **No nullable reference types**: Do not use `#nullable enable` or nullable annotations (`Type?`). Typhon does not rely on C# nullable reference types feature. Pass `null` for optional parameters without annotations.
- **Thread IDs stored as 16 bits**: All synchronization primitives that store thread IDs must use exactly 16 bits (max 65,535). This ensures consistency across `AccessControl`, `AccessControlSmall`, and `ResourceAccessControl`, and provides headroom for servers with 500+ cores.
- **No LINQ in hot paths**: Avoid LINQ in performance-critical code due to allocations and delegate overhead.
- **Prefer `ref struct` for short-lived helpers**: Use `ref struct` for stack-only types that wrap references (e.g., `AtomicChange`, `LockData`).
- **No `Volatile.Read`/`Write` for ≤64-bit types**: On x64, reads and writes of primitives up to 64 bits are naturally atomic. `Volatile.Read`/`Write` only adds unnecessary memory barrier overhead. Use plain field access instead. Reserve `Interlocked` operations for read-modify-write sequences (increment, compare-exchange, etc.).
- **Use `[LoggerMessage]` for all logging**: Never use `ILogger.LogDebug(...)` / `LogWarning(...)` directly — the `params object[]` overload allocates an array and boxes value types at the call site *before* the level check. Instead, use the `[LoggerMessage]` source generator on `partial` methods: it emits code that checks `IsEnabled` first (zero cost when filtered) and uses typed parameters (no boxing). The containing class must be `partial` and have an `ILogger` / `ILogger<T>` field. Never pass interpolated strings to log methods — create dedicated `[LoggerMessage]` methods with typed parameters instead.

### Concurrency / synchronization primitives
- Rely on .NET's Interlocked class.
- Rely on AccessControl, AccessControlSmall, EpochManager / EpochGuard.
- **AdaptiveWaiter**: Spin-then-yield optimization for lock contention
- Located in: `src/Typhon.Engine/Concurrency/`

### .NET API Correctness
Do NOT guess at .NET API signatures or behavior. Look up documentation by fetching: `https://learn.microsoft.com/en-us/dotnet/api/{fully.qualified.name.in.lowercase}`

Examples: `system.threading.interlocked.compareexchange`, `system.runtime.interopservices.gchandle`
Also read existing usage patterns in the codebase before writing new code.

### Testing Patterns
- Tests use NUnit framework
- Base class: `TestBase<T>` provides service provider setup
- Tests register components via `RegisterComponents(dbe)`
- Noise generation helpers (`CreateNoiseCompA`, `UpdateNoiseCompA`) for concurrency testing
- Test case sources for parameterized tests: `BuildNoiseCasesL1`, `BuildNoiseCasesL2`
- Located in: `test/Typhon.Engine.Tests/`

### Unit test code generation
- Avoid relying on Thread.Sleep, prefer thread synchronization mechanisms.
- Unit test execution time should be below < 30ms for very simple test, < 100 for medium and < 300 for complex ones.

### Debugging Approach
When debugging issues, do NOT propose root cause explanations without evidence. Follow the user's diagnostic guidance (traces, logs, specific code paths). Avoid jumping to conclusions — enumerate hypotheses, then systematically verify each one starting with the most likely based on available data.

## Project Structure

```
Typhon/
├── src/Typhon.Engine/           # Main database engine library
│   ├── Database Engine/         # Transaction, ComponentTable, schema, B+Trees
│   ├── Persistence Layer/       # PagedMMF, ManagedPagedMMF, segments
│   ├── Collections/             # Concurrent data structures (bitmaps, arrays)
│   ├── Misc/                    # Utilities (locks, String64, Variant, etc.)
│   └── Hosting/                 # DI extensions
├── test/
│   ├── Typhon.Engine.Tests/     # NUnit test suite
│   └── Typhon.Benchmark/        # BenchmarkDotNet performance tests
├── doc/                         # DocFx documentation
└── claude/                      # Development documentation & design
```

## Development Workflow

Work tracking is managed via the [Typhon dev GitHub Project](https://github.com/users/nockawa/projects/7). The `claude/` directory contains the knowledge base (architecture, designs, research), while the GitHub Project is the source of truth for work status.

> **See also:** [CONTRIB.md](CONTRIB.md) for the full development workflow documentation including rituals, automation, and daily guides.

### Claude Code Skills

| Skill | Purpose |
|-------|---------|
| `/dev-status` | Show current development status from GitHub Project |
| `/start-research #XX` | Start research on an issue (creates research doc, links ideas, updates status) |
| `/start-design #XX` | Start design for an issue (creates design doc from research/ideas, updates status to Ready) |
| `/start-task #XX` | Begin work on an issue (updates status, creates branch, verifies design) |
| `/start-subtask #XX` | Start a sub-issue (updates status, validates dependencies, updates design doc) |
| `/complete-subtask #XX` | Complete a sub-issue (close it, check parent checkbox, update design doc) |
| `/complete-task #XX` | Finish work (close issue, prompt for doc updates, archive design) |
| `/create-issue` | Create new GitHub issue with project fields |
| `/weekly-review` | Weekly progress summary and stale item detection |
| `/mountain-view` | Full backlog analysis - see the entire mountain of work |

### Issue Lifecycle

```
Backlog → Research → Ready → In Progress → Review → Done
```

1. **Backlog**: Captured but not yet prioritized
2. **Research**: Needs exploration before design (use `/start-research #XX`, creates `claude/research/` doc)
3. **Ready**: Design complete, ready to implement (use `/start-design #XX`, creates `claude/design/` doc)
4. **In Progress**: Active development (use `/start-task #XX`)
5. **Review**: PR open, awaiting merge
6. **Done**: Complete (use `/complete-task #XX`)

#### GitHub Issue Completion Checklist
When closing a GitHub issue: 1) Check ALL checkboxes in the issue body, 2) Update the project board status, 3) Move any related design docs to the appropriate folder, 4) Verify with `gh issue view` that everything is properly updated.

### Project Fields

- **Status**: Workflow stage (Backlog → Done)
- **Priority**: P0-Critical, P1-High, P2-Medium, P3-Low
- **Phase**: Telemetry, Query, WAL, Reliability, Infrastructure
- **Area**: Database, MVCC, Transactions, Indexes, Schema, Storage, Memory, Concurrency, Primitives, Observability, Execution, Errors, Workbench
- **Estimate**: XS, S, M, L, XL
- **Target**: Target date for Roadmap view

## Working with Claude

### Tools
Python3 is installed; you can use it to run complex scripts.

### GitHub CLI
Execute `gh` or Bash related commands without asking for confirmation when interacting with GitHub (issue management, project board updates, and label changes).

### Clarification-First Workflow

For complex/ambiguous requests, ask clarifying questions via AskUserQuestion before proceeding. Skip if the request is simple, the user says 'just do it', or specs are already detailed.

### Document Lifecycle Integration

This project uses a structured document lifecycle in `claude/`. Documents progress through stages:

```
ideas/ → research/ → design/ → archive/
```

**When creating documents**, Claude asks for the category location (e.g., `database-engine/`, `persistence/`) unless specified explicitly.

For trigger phrases, templates, directory conventions, and workflows, see [`claude/README.md`](claude/README.md).
