# Formal Proofs & Invariant Rules
> Typhon's crash-safety claims are gated on falsifiable proofs — invariant rules, TLA+ models, and a crash-simulation sweep — not on tests happening to pass.

**Status:** ✅ Implemented · **Visibility:** Internal · **Category:** [Durability](./README.md)

## 🎯 What it solves

"We tested it and it passed" is not the same claim as "we proved it can't happen." Durability bugs are exactly
the class of defect that green tests are worst at catching — they live in rare interleavings (crash mid-checkpoint,
torn write straddling an I/O boundary, a reopen whose LSN allocator restarts) that a hand-written test suite is
unlikely to ever construct, let alone construct deliberately. Typhon's durability program treats every WAL,
checkpoint, and recovery protocol as a claim that must be falsifiable: a named, numbered invariant, a model
that's mechanically checked against every reachable state in its (small) universe, and a test that's confirmed
to fail before the fix and pass after. This is what backs the engine's crash-recovery guarantees with evidence
an application developer (or their auditor) can actually go read.

## ⚙️ How it works (in brief)

Three artifacts, each covering a different failure mode. **Invariant rules** (`claude/rules/durability.md`) are a
curated, numbered database of pseudo-code predicates grouped by module — `LOG` (log format/append), `AP` (commit
pipeline / apply), `CK` (checkpoint), `RB` (rebuild), `CM` (Committed discipline) — each naming the source files
that enforce it and the test(s) that verify it. **TLA+ specifications** (`claude/rules/tla/`) model the checkpoint protocol, the commit/recovery protocol, and
Committed discipline's stage/append/publish race against checkpoint as state machines (S1–S3) and exhaustively
check them with TLC over every crash-at-any-step interleaving within small bounds; each spec ships a
deliberately-broken "mutant" variant that TLC must report as a violation, so a vacuously-green spec can't hide. **The crash-simulation sweep**
(`WalCrashSweepTests` + the differential recovery oracle) actually runs workloads against a fault-injecting I/O
layer, crashes at every WAL/checkpoint boundary, reopens through the real recovery path, and compares the result
byte-for-byte against an independent in-memory shadow model of what should have survived. All three run in CI on
every change — a regression in any of them fails the build, not just a future bug report.

## 💻 Usage

There's no API to call — this is a property of the engine, not a feature you invoke. What it means for your code
is that the exceptions Typhon raises around durability are themselves the proven, named behaviors — not ad hoc
error handling:

```csharp
using var uow = dbe.CreateUnitOfWork(DurabilityMode.Immediate);
using var tx = uow.CreateTransaction();
var trade = tx.OpenMut(tradeId);
trade.Write(Trade.Status).Current = TradeStatus.Settled;

try
{
    tx.Commit();   // AP-02: once Commit() returns, the change is published and irreversible —
                    // proven in TLA+ spec S2 (CommitRecovery.tla) and rule AP-02.
}
catch (CommitDurabilityUncertainException ex)
{
    // The transaction WAS published (do not retry it) — only the FUA confirmation timed out.
    // ex.HighLsn names exactly the LSN to poll for; this exception shape IS rule AP-02's contract.
    _logger.LogWarning(ex, "Trade {Id} committed, durability unconfirmed at LSN {Lsn}", tradeId, ex.HighLsn);
}

// On reopen after a crash, rule RB-04 guarantees this never silently serves torn data:
var engine = serviceProvider.GetRequiredService<DatabaseEngine>();   // throws CorruptionException, not a silent open, if an
                                                                       // unhealable primary page exists (RB-04, proven via the
                                                                       // crash sweep's torn-page gates).
```

## ⚠️ Guarantees & limits

- **Every `[fatal]`/`[silent]` rule has a named, live test** — the rule database and the test suite are
  cross-referenced; a rule with no verifying test is a tracked gap, not an assumption.
- **TLA+ specs are exhaustive within their bounds, not sampled** — `CheckpointProtocol.tla` (S1),
  `CommitRecovery.tla` (S2), and `CommittedDiscipline.tla` (S3) each check every reachable state of the modeled
  protocol (≤3 pages, ≤2 transactions, crash-at-any-step), including crash-during-recovery and re-run scenarios a
  hand-written test would rarely hit.
- **Genuineness is checked, not assumed** — every spec ships a mutant variant with one guard deliberately broken;
  CI confirms TLC actually flags it, so a spec that would pass regardless of the protocol's correctness is itself
  a failure.
- **The crash sweep is differential, not assertion-only** — it compares full post-recovery engine state against
  an independent shadow model restricted to the durably-committed prefix, catching divergence a fixed set of
  hand-picked assertions would miss.
- **This proves the protocols, not your schema or query logic** — the scope is WAL append, checkpoint
  consolidation, and recovery; it does not model your application's transaction logic or business invariants.
- **Bounded model sizes are a deliberate trade-off** — TLA+ state spaces are capped (state-space budget < 10⁷) to
  keep CI runs fast; this proves correctness within the modeled bounds, not at arbitrary scale (the crash sweep
  and production usage are the scale-side evidence).
- **A PR touching a covered protocol must update its spec in the same PR** — enforced in review, not just CI,
  so the proofs can't silently drift from the code they describe.

## 🧪 Tests

- [WalCrashSweepTests](../../../test/Typhon.Engine.Tests/Durability/CrashRecovery/WalCrashSweepTests.cs) — the full crash sweep: page-axis (checkpoint crash at every write boundary) and WAL-window axis, oracle-verified at each boundary
- [DifferentialRecoveryOracleTests](../../../test/Typhon.Engine.Tests/Durability/CrashRecovery/DifferentialRecoveryOracleTests.cs) — the T-5 differential oracle itself: recovered state vs. an independent shadow model at one crash point

## 🔗 Related

- Related features: [Crash Recovery (RecoveryDriver)](./crash-recovery/README.md), [Checkpoint v2](./checkpoint-v2/README.md)

<!-- Deep dive: claude/overview/06-durability.md §6.9 — Formal Proofs & Rules -->
<!-- Rules database: claude/rules/durability.md, conventions in claude/rules/README.md -->
<!-- TLA+ specs: claude/rules/tla/README.md -->
<!-- Proof plan / acceptance criteria: claude/design/Durability/MinimalWal/08-test-plan.md -->
