# Typhon Correctness Rules

| Field | Value |
|-------|-------|
| Status | Living |
| Last Updated | 2026-04-25 |

> **Purpose:** A curated database of invariants that define correctness in Typhon.
> These rules are the **source of truth** — not the code, not the reference docs.
> Code and tests must conform to these rules. When rules change, code and tests follow.

> **Where this lives, and why.** `rules/` sits in the Typhon repository, beside the code it constrains, because
> it is checked *against* that code: ~185 `scope:` lines name C# symbols, 70 `[VerifiesRule]` attributes name
> rule ids, and 75 source files cite rule ids in comments. Every one of those is verified by CI here
> (`scripts/check-rule-scopes.py`, `scripts/audit-rule-coverage.py`, `scripts/check-doc-links.py`).
>
> The rest of the knowledge base — `design/`, `adr/`, `overview/`, `research/` — is a **separate private
> repository**. Rules occasionally name a document there for rationale; those references are written as plain
> paths rather than links, because a hyperlink most readers cannot follow is worse than a name they can ask for.

## What This Is

Each rule file covers one **domain** (a bounded area of the engine with cohesive correctness concerns). A domain contains **modules** (the finest-grained unit of atomic behavior). Each module has **rules**: invariants expressed in pseudo-code that must hold for the system to be correct.

### Hierarchy

```
Domain (file)           e.g., durability.md
  └─ Module             e.g., CheckpointManager
       └─ Rule          e.g., CP-01: LSN ordering
```

## Pseudo-Code Conventions

Rules use a minimal pseudo-code designed for token efficiency and precision.

### Keywords

| Keyword | Meaning |
|---------|---------|
| `invariant` | A predicate that must hold at all times (or at all observable points) |
| `pre` | Precondition — must hold before an operation |
| `post` | Postcondition — must hold after an operation |
| `on_violation` | What goes wrong if this rule is broken |
| `scope` | Source files / types that enforce or depend on this rule |
| `never` | An operation or state that must not occur |
| `requires` | A dependency — this rule assumes another rule holds |

### Notation

```
∀x ∈ Set: P(x)          — for all x in Set, P(x) holds
∃x ∈ Set: P(x)          — there exists x in Set where P(x)
A → B                    — A implies B (if A then B)
A ↔ B                    — A if and only if B
¬P                       — not P
A ∧ B                    — A and B
A ∨ B                    — A or B
x.field                  — field access
f(x)                     — function call
x ≤ y                    — less than or equal (also: ≥, <, >, ==, ≠)
[step1] → [step2]        — step1 must happen before step2 (temporal ordering)
```

### Entry Format

```
### XX-NN: Short title
  invariant <predicate>
  scope: File1.cs, File2.cs
  on_violation: <what breaks and how>
  requires: YY-MM (optional: dependency on another rule)
```

### Severity Markers (optional)

Rules may carry a severity tag after the ID:

| Tag | Meaning |
|-----|---------|
| `[fatal]` | Violation causes unrecoverable data loss or corruption |
| `[silent]` | Violation is not immediately detectable — corruption surfaces later |
| `[perf]` | Violation degrades performance but not correctness |
| `[correctness]` | Violation produces wrong results, but detectably (throws, fails a check) |
| `[design]` | Records a deliberate design constraint rather than a failure mode |
| `[strict-mode]` `[opt-in]` | Enforced only when a runtime check gate is enabled (see `CheckConfig`) |
| `[UNBUILT]` | The invariant describes intended behaviour that is **not implemented**. Kept so the ID is not mistaken for an accidentally deleted rule; must state what is missing and where |
| `[RETIRED]` | **Do not use.** Retired rules are DELETED outright — this project does not accumulate struck-through history |

**Rule IDs must be unique across the entire database, not just within a file.** Two modules previously defined
`SQ-01..SQ-05` and `PS-01` with different meanings, which any tool indexing by ID silently conflates. Renamed
2026-07-28: the durability Seqlock module is now `SL-`, and runtime Phase Semantics is now `PH-`.

### Entry keywords

Beyond `invariant` / `never` / `scope` / `on_violation`, the corpus uses: `pre`, `post`, `requires`, `enforce`,
`enforced_by`, `rationale`, `note`, `verified`, `spec`, `impl`, `release_behavior`, `semantic_meaning`.

`requires:` means **"this rule depends on another rule holding"** — not "what the author must do". (Both senses were in
use; the first is now the only correct one.)

`scope:` must name **every** site that has to obey the rule, including the callers — not just the primitive that
implements it. Scoping to the primitive is what lets a scope-driven review walk past a defect in a caller. Symbols
named here must exist: run `python3 scripts/check-rule-scopes.py` (nine dead symbols were found in the 2026-07-28 pass).

`verified:` may name a test only if that test **actually asserts the invariant**. A tag on a test that exercises the
area but never checks the property is a false assurance and scores as coverage in the gate.

## How Rules Are Maintained

1. **AI-primary:** Claude maintains rules as part of the development workflow
2. **On code changes:** Claude cross-references affected modules against the rule database
3. **On rule changes:** Claude identifies affected tests and code that must be updated
4. **Validation:** Periodic `/validate-rules` audit (future skill) to check rule-code alignment

## File Organization

Each file covers one domain. Rules are grouped by module within the domain.

| File | Domain | Modules |
|------|--------|---------|
| [durability.md](durability.md) | WAL, Checkpoint, Recovery | WAL Pipeline, Checkpoint, Rebuild/Suspect-Mode, Seqlock, UoW Registry, Page Safety |
| [runtime-scheduling.md](runtime-scheduling.md) | DagScheduler, RuntimeSchedule, Auto-DAG (RFC 07) | Phase Resolution, Access Conflict Detection, Edge Derivation, Debug-Runtime Write Validation, API Contract Stability |
| [spatial.md](spatial.md) | Spatial R-Tree, Queries, Triggers, Interest, Spatial Tiers | R-Tree Structure, Queries, Fat AABB Updates, Trigger Volumes, Interest Management, Cluster Spatial AABBs, ClusterCellMap, TierClusterIndex, Migration Dirty Bits, Dormancy, Checkerboard Partition, SetCellTier Validation |
| [ecs.md](ecs.md) | Component schema identity, component-type identity, tick-fence dirty bitmaps | SCHEMA (StorageMode fixed per (name, revision); ComponentTypeId is a process-global in-memory handle), CLUSTERWALK, CLUSTERVIS, DIRTY (a spawn sets no dirty bit — `DIRTY-01`), STAGE (a cluster-backed non-Versioned spawn allocates no content chunk — `STAGE-01`), REAP (every deferred-cleanup queue has a production drain — `REAP-01`) |
| [indexing.md](indexing.md) | Secondary-index ownership and scope, ordered index reads | Index Ownership & Scope (`IX-01..05`), Ordered Index Reads (`IXS-01..03`) |
| [concurrency.md](concurrency.md) | UoW cancellation, structural holdoff, thread identity, MVCC snapshot retention, epoch pinning | Cooperative Cancellation ⊗ Structural Holdoff (`CX-01..04` — a coupled pair, see the module note), Thread Identity (`CX-05`), Snapshot Retention (`SNAP-01..02`), Epoch Pinning ⊗ Page Eviction (`EP-01`), SIGNAL (a permit is produced only when there is a consumer for it — `SIGNAL-01`) |

## Roadmap

Domains to add, in priority order (highest-risk invariants first):

- [ ] **`concurrency.md`** *(partial)* — AccessControl state machine, lock ordering, deadlock prevention. The epoch/eviction interaction has its first invariant (`EP-01`, from #838, which cost a P0 self-deadlock to establish); the rest of the epoch protocol is still unwritten. Remains high priority: it spans multiple subsystems and violations cause use-after-free.
- [ ] **`data-engine.md`** — MVCC visibility rules, revision chain integrity, B+Tree structural invariants, schema versioning field-ID stability. High priority: visibility bugs cause silent incorrect query results.
- [ ] **`storage.md`** — Page state machine (partially covered in durability via seqlock/eviction), segment allocation, bitmap consistency. Medium priority: most rules are local to PagedMMF.
- [ ] **`execution.md`** — Commit path ordering, transaction pool, DurabilityMode contracts, holdoff/yield-point placement. Medium priority: partly covered in durability already.
- [ ] **`resources.md`** — Exhaustion policy contracts, budget hierarchy, back-pressure thresholds. Lower priority: violations cause degradation, not corruption.
