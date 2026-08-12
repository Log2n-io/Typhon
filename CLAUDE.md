# CLAUDE.md

Be CONCISE in your answer, each word count. Dont elaborate or explain unless I ask you to or you think it's primordial for my understanding.

## Opinion vs Action

When the user asks for your opinion on a design choice, code approach, or any topic — give your opinion only and challenge the user on their choices. Do NOT edit files or make changes; wait for explicit instructions to proceed.

## Documentation is the source of truth — read it before reasoning

Typhon has comprehensive design documentation in `claude/`. Code reflects intent; docs explain it. For any non-trivial task — designing, analyzing, diagnosing, refactoring, gap-assessing, reviewing, or answering "how does X work?" — read the relevant docs FIRST, then read code to verify.

**When to consult docs (not exhaustive):**
- Understanding any subsystem's mechanics or invariants → `claude/overview/`
- Designing a new feature or fix → `claude/design/<area>/` + relevant `claude/adr/`
- Analyzing existing behavior, doing gap assessments, planning refactors → both
- Checking a correctness invariant → `rules/`
- Estimating CPU cost of an algorithm → `claude/design/cpu-timings.md`
- Glossary / terminology → `claude/design/glossary.md`

**What counts as "trivial" (docs not required):** single-line fixes, typo corrections, well-scoped test additions, mechanical refactors that don't change behavior.

**Red flags that mean STOP and read docs first:**
- "I'll just read the code to figure it out"
- "This feels like a continuation of what I was doing" (task type may have shifted from implement → analyze/design)
- "I have enough context to answer this from memory"
- Any task using words like *analyze, diagnose, design, assess, audit, review, refactor, propose*

Never deviate from established specs without explicitly noting the deviation and getting user approval.

> **Separate git repo:** The `claude/` directory is its own nested git repository. To commit or perform any git operations on documentation files, you must `cd claude/` first. Running `git status` from the Typhon root will not show changes to `claude/` files.

## Project Overview
Typhon is a real-time, low-latency ACID database engine with microsecond-level performance targets, using an ECS architecture with MVCC snapshot isolation.

### Quick Navigation

| When You Need... | Go To | Key Contents                                                    |
|------------------|-------|-----------------------------------------------------------------|
| **How the engine works** | `claude/overview/` | 13-part architecture guide covering all subsystems              |
| **Feature designs & docs** | `claude/design/` | SOURCE OF TRUTH, USE IT!                                        |
| **Why a decision was made** | `claude/adr/` | 50+ Architecture Decision Records with rationale                |
| **What must always hold** | `rules/` | Correctness invariants by domain (WAL, checkpoint, page safety) |
| **Current priorities** | [GitHub Project](https://github.com/users/nockawa/projects/7) | Work tracking, status, roadmap                                  |
| **Deep research** | `claude/research/` | Analysis studies (e.g., timeout patterns, query systems)        |
| **Document workflows** | `claude/CLAUDE.md` | Lifecycle, templates, trigger phrases                           |

### Architecture Overview Series

The `claude/overview/` directory is the **authoritative architectural reference**, use it to asses which other md doc to search and read, which folders of the typhon.engine project you need to read the code from to understand what is already there. 


### Correctness Rules

The `rules/` directory is a curated database of invariants that define correctness in Typhon. Rules are the **source of truth** — code and tests must conform to them. Each rule file covers one domain (e.g., durability, concurrency), grouped by module, with invariants expressed in pseudo-code. When modifying code, cross-reference affected modules against the rule database to ensure no invariant is violated. See [`rules/README.md`](rules/README.md) for conventions and notation.

> **`rules/` is in THIS repo, not in `claude/`** (moved in #747). It is the one part of the knowledge base that CI checks mechanically — `scripts/check-rule-scopes.py` (every `scope:` symbol exists), `scripts/audit-rule-coverage.py` (rule ↔ `[VerifiesRule]` in both directions, ratcheted) and `scripts/check-doc-links.py` (cited paths and links resolve) — so it lives beside the code it constrains. A rule, its verifying test and the code it scopes belong in **one commit**.

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
4. **Before pushing, run `bash scripts/pre-push.sh`** — see below.
5. For perf-claim work (measuring wall-clock impact), run Release × 3 with `--logger trx`.

### Before pushing — `scripts/pre-push.sh`

Steps 1-3 above run the **engine** project in **Debug**. The merge gate runs **both** test projects in **Release**, plus
six policy scripts. That gap is not academic: of the 17 gate failures in the week to 2026-08-12, **nine** were policy
jobs whose scripts live in this repo and cost seconds to run, and **five** were one bug reaching the gate through
`test/Typhon.Workbench.Tests` — a project this workflow never mentioned, so nobody ran it locally. Every one of those
was discovered on a billed c6id instance instead of on the dev box.

```bash
bash scripts/pre-push.sh            # policy checks + both suites (Release) — full gate parity
bash scripts/pre-push.sh --policy   # policy checks only (seconds; covers the nine free-runner failures)
```

Each line names the gate job it corresponds to, so a local failure is the same failure CI would have reported. It is
**not** installed as a git hook — symlink it into `.git/hooks/pre-push` if you want it automatic.

**What it deliberately does NOT reproduce:** the 8-way sharding and the serial `Sensitive` pass. Those change
CONTENTION, which is a real source of gate-only failures. If a test reddens the gate but passes here, that is the first
suspect, and `bench/aws/shard.py run` is the tool for it.

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
- **Thread IDs stored as 16 bits, max 32,767**: All synchronization primitives that store thread IDs must use exactly 16 bits, and must stay within **32,767 simultaneously-live threads** — the binding limit across `AccessControl`, `AccessControlSmall` and `ResourceAccessControl`. (`AccessControlSmall` holds the id in bits 16-31 of a *signed* `int`, so 32,768+ would set the sign bit and `LockedByThreadId` would sign-extend; see `AccessControlSmall.cs:22-29`. The wider types happen to allow more, but design to the tightest.) Managed thread ids are allocated lowest-available-first and recycled on thread death, so this bounds concurrent threads, not threads over the process lifetime — ample headroom for servers with 500+ cores.
- **No LINQ in hot paths**: Avoid LINQ in performance-critical code due to allocations and delegate overhead.
- **Prefer `ref struct` for short-lived helpers**: Use `ref struct` for stack-only types that wrap references (e.g., `AtomicChange`, `LockData`).
- **Memory-ordering discipline (x64 AND arm64)**: code must be correct under the weak arm64 memory model, not just x64 TSO. Use `Volatile.Read`/`Write` for any load/store that participates in cross-thread ORDERING — publication of data written outside a lock, lock-free/optimistic readers, seqlock validation. Acquire/release compile to plain `mov` on x64 and `ldar`/`stlr` on arm64, so this costs nothing where it isn't needed by the hardware. Plain field access remains correct for data only touched under a lock or on one thread — don't sprinkle `Volatile` on those. `Interlocked` for read-modify-write (its full fences on both architectures are what optimistic readers pair with). Seqlock/OLC-style readers must either make EVERY load in the protocol a `Volatile.Read` (volatile loads are program-ordered among themselves — see `RevisionChainReader.TryWalkSingleEntryOptimistic`) or issue an arch-conditional barrier before the validating re-read (`if (!X86Base.IsSupported) { Interlocked.MemoryBarrier(); }` — JIT-folded on x64; see `OlcLatch.ValidateVersion`), because an acquire load alone does not stop earlier plain reads from sinking below it.
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
├── rules/                       # Correctness invariants (+ tla/ formal specs) — CI-enforced, see above
└── claude/                      # Development documentation & design (separate private repo)
```

## Development Workflow

Work tracking is managed via the [Typhon dev GitHub Project](https://github.com/users/nockawa/projects/7). The `claude/` directory contains the knowledge base (architecture, designs, research), while the GitHub Project is the source of truth for work status.

> **See also:** [CONTRIB.md](CONTRIB.md) for the full development workflow documentation including rituals, automation, and daily guides.

### Claude Code Skills

| Skill | Purpose |
|-------|---------|
| `/dev-status` | Show current development status from GitHub Project |
| `/start-research #XX` | Start research on an issue (creates research doc, links ideas, updates status) |
| `/start-design #XX` | Start design for an issue (creates design doc from research/ideas; Status stays Todo) |
| `/start-task #XX` | Begin work on an issue (updates status, creates branch, verifies design) |
| `/start-subtask #XX` | Start a sub-issue (updates status, validates dependencies, updates design doc) |
| `/complete-subtask #XX` | Complete a sub-issue (close it, check parent checkbox, update design doc) |
| `/complete-task #XX` | Finish work (close issue, prompt for doc updates, archive design) |
| `/create-issue` | Create new GitHub issue with project fields |
| `/weekly-review` | Weekly progress summary and stale item detection |
| `/mountain-view` | Full backlog analysis - see the entire mountain of work |

### Issue Lifecycle

The board has exactly **three** Status values. This is the complete set — there is no Backlog, Ready or Review column:

```
Todo → In Progress → Done
```

1. **Todo**: everything not yet under development — captured, being researched, or designed and ready to start.
2. **In Progress**: active development (`/start-task #XX`). **An issue stays here while its PR is open** — there is no Review status.
3. **Done**: work merged and the issue closed (`/complete-task #XX`).

**Research and design are activities, not board columns.** `/start-research #XX` and `/start-design #XX` create their documents under `claude/research/` and `claude/design/` and leave Status at **Todo** — the document's own `Status:` header carries that detail, not the board.

#### GitHub Issue Completion Checklist
When closing a GitHub issue: 1) Check ALL checkboxes in the issue body, 2) Update the project board status, 3) Move any related design docs to the appropriate folder, 4) Verify with `gh issue view` that everything is properly updated.

### Project Fields

**Board field** (project-level, set with `gh project item-edit`):

- **Status**: Todo · In Progress · Done

**Issue fields** (organization-level, set with the `setIssueFieldValue` GraphQL mutation — *not* `gh project item-edit`; the board columns of the same name are derived from these). Recipe in [`.claude/skills/_helpers.md`](.claude/skills/_helpers.md):

- **Priority**: P0-Critical · P1-High · P2-Medium · P3-Low
- **Estimate**: XS · S · M · L · XL
- **Area**: Concurrency · Execution · Storage · Durability · MVCC · Schema · Spatial · Query · Runtime · Observability · Resources · Errors · Utilities · Workbench · Indexes
- **Product**: Engine · Workbench · CLI · CI · User adoption
- **Bug status**: New · Confirmed · Fixing · Fixed · Verified · Duplicate · Won't fix · Can't reproduce
- **Start date** / **Target date**: dates
- **Claude Code Discussion**: the `https://claude.ai/code/session_…` URL

> **`Area` — `Execution` vs `Runtime` is the one people get wrong.** `Execution` is the Unit-of-Work / transaction-commit layer ([`claude/overview/02-execution.md`](claude/overview/02-execution.md)). The tick loop, `DagScheduler` and system dispatch are **`Runtime`** ([`claude/overview/13-runtime.md`](claude/overview/13-runtime.md)). External filers routinely pick `Execution` for scheduler work — reclassify and say so in the reply.

Issue **Type** (Task / Bug / Feature / Epic) and **Milestone** are separate again — see `_helpers.md`. Hierarchy is always **Epic → Feature → Task**, never Epic → Task directly.

## Working with Claude

### Tools
Python3 is installed; you can use it to run complex scripts.

### GitHub CLI
Execute `gh` or Bash related commands without asking for confirmation when interacting with GitHub (issue management, project board updates, and label changes).

### Commit messages

Plain prose. Subject line prefixed with `#issue` when there is one; no Conventional Commits prefixes (`feat:`, `fix:`), no hard-wrapping of the body — one paragraph per idea, let the terminal wrap.

**No trailers.** Do not append `Co-Authored-By:`, `Claude-Session:`, `Generated with…` or any other footer, even if a tool default asks for it. Nothing in this repository's history carries them and they are not wanted; `git log` is the authority on the convention.

### Clarification-First Workflow

For complex/ambiguous requests, ask clarifying questions via AskUserQuestion before proceeding. Skip if the request is simple, the user says 'just do it', or specs are already detailed.

### Document Lifecycle Integration

This project uses a structured document lifecycle in `claude/`. Documents progress through stages:

```
ideas/ → research/ → design/ → archive/
```

**When creating documents**, Claude asks for the category location (e.g., `Ecs/`, `Spatial/`, `Indexing/`) unless specified explicitly. Categories use PascalCase mirroring `src/Typhon.Engine/` — see [`claude/CLAUDE.md`](claude/CLAUDE.md) for the convention.

For trigger phrases, templates, directory conventions, and workflows, see [`claude/CLAUDE.md`](claude/CLAUDE.md).
