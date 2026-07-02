# Index Diagnostics & Consistency Checking
> Always-on contention counters plus an on-demand structural walk for troubleshooting B+Tree indexes.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Indexing](./README.md)

## 🎯 What it solves

Lock-free index concurrency ([OLC](./olc-concurrency.md)) trades a simple whole-tree lock for retry-driven
correctness: readers and writers can race, restart, and fall back to a slower path under contention. When a
workload feels slower than expected, or a stress run produces a result you don't trust, "is this index
contended?" and "is this index's structure actually sound?" are the two questions you need answered without
attaching a profiler or stepping through engine internals. Typhon answers both cheaply: contention counters
update unconditionally as part of every index operation, and a full structural walk is one shell command away.

## ⚙️ How it works (in brief)

Every B+Tree index instance carries a handful of `long` counters — bumped only on the retry/fallback/structural
paths (version-check failures, optimistic→pessimistic fallbacks, splits, merges) — so the steady-state lookup
and insert paths pay nothing extra. They give a coarse signal of how much retry/fallback traffic an index is
absorbing under concurrent load. Separately, `tsh`'s `btree-validate` command walks an index from root to leaf
under an epoch guard, checking key ordering, parent/child linkage, and B-link sibling chaining, and reports
pass/fail with the first violation found — useful after a stress run, a crash-recovery rebuild, or whenever
you suspect corruption rather than just contention.

## 💻 Usage

Inspect an index and validate its structure from the `tsh` shell:

```
tsh> open game.typhon
tsh> load-schema bin/Game.Components.dll

tsh> btree Player.Name
  B+Tree: Player.Name (unique)
  ──────────────────────────────────────
  Total nodes:     128
  Chunk capacity:  512
  Fill factor:     25.0%
  Node size:       256 bytes

tsh> btree-validate Player.Name
  Validating B+Tree Player.Name...
  Validation passed
```

| Command | Purpose |
|---------|---------|
| `btree <Component.Field>` | Node count, chunk capacity, fill factor, node size |
| `btree-dump <Component.Field> [--level N \| --chunk N]` | Raw node/chunk inspection |
| `btree-validate <Component.Field>` | Full structural walk; reports pass or the first violation |

The contention counters (`OptimisticRestarts`, `PessimisticFallbacks`, `SplitCount`, `MergeCount`, …) aren't
wired to a `tsh` command yet — today they're consumed by the engine's own stress-test and benchmark harnesses,
which read them straight off the index instance to confirm a concurrent workload actually exercised the
retry/fallback paths it was meant to:

```csharp
// Engine-internal / stress-harness code (InternalsVisibleTo), not part of the public Typhon.Engine surface.
TestContext.Out.WriteLine(
    $"Restarts={tree.OptimisticRestarts} Fallbacks={tree.PessimisticFallbacks} " +
    $"Splits={tree.SplitCount} Merges={tree.MergeCount}");
```

## ⚠️ Guarantees & limits

- Counters are always-on and incremented unconditionally — no flag to enable, nothing to forget to turn off —
  but they only move on retry/fallback/structural paths, so they add no cost to the steady-state hot path.
- Counters are per-index-instance and process-lifetime; they reset to zero on engine restart and are not
  persisted, checkpointed, or aggregated across indexes.
- `btree-validate` is read-only and safe to run against a live database; it runs under an epoch guard so it
  observes a consistent epoch, but it is a point-in-time walk, not a continuous monitor.
- `btree-validate` checks structural soundness (key ordering, child linkage, B-link sibling chaining) — it does
  not check that index entries match the component data they're supposed to reference.
- The contention counters and the descent-trace forensic hook used for deep OLC bug investigation are
  engine-internal; they require `InternalsVisibleTo` access (as `tsh`, the test suite, and benchmarks have) and
  are not part of the stable public `Typhon.Engine` API.

## 🧪 Tests

- [OlcBTreeStressTests](../../../test/Typhon.Engine.Tests/Data/OlcBTreeStressTests.cs) — `LogDiagnostics`/`tree.ResetDiagnostics()`: reads `OptimisticRestarts`/`PessimisticFallbacks`/`SplitCount`/`MergeCount`/`ContentionSplitCount` straight off a stressed tree instance, the same counters this feature documents
- [BTreeTests](../../../test/Typhon.Engine.Tests/Data/BTreeTests.cs) — pervasive `tree.CheckConsistency(...)` calls exercise the same structural walk (key ordering, parent/child linkage, B-link sibling chaining) that `tsh btree-validate` runs via `DiagnosticCommandExecutor`

## 🔗 Related

- Sibling feature: [Optimistic Lock Coupling](./olc-concurrency.md)
- Sibling feature: [tsh Schema Shell Commands](../Schema/tsh-schema-commands.md) — same shell, same interactive-diagnostics pattern
- Source: `src/Typhon.Engine/Indexing/internals/BTreeBase.cs`, `src/Typhon.Engine/Indexing/internals/BTree.cs`, `src/Typhon.Engine/Indexing/internals/OlcDescentTrace.cs`, `src/Typhon.Shell/Commands/DiagnosticCommandExecutor.cs`

<!-- Deep dive: claude/design/Indexing/public-api.md#diagnostics -->
<!-- Deep dive: claude/design/Indexing/concurrent-index-scaling.md — OLC restart/fallback paths these counters measure -->
<!-- Deep dive: claude/design/Indexing/latch-coupled-smo.md — Latch-Coupled Split/Merge -->
