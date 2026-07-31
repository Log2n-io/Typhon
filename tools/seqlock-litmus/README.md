# arm64 verification run — what to execute and how to read it

Checklist for running branch `fix/bug-bash-2026-07-30` on an arm64 machine (Apple Silicon / Graviton).

Everything here exists because **x64 cannot verify #579**. On x64 the reader's barriers compile to a plain `mov` and the
conditional fence folds away entirely, so conforming and violating code emit *identical machine code*; on the writer side
the code differs but TSO supplies the missing ordering anyway, so the violating version still passes. A green x64 suite is
not evidence in either direction — see rule `SL-07` in `claude/rules/durability.md`.

## Context

`PagedMMF`'s page seqlock had no memory barriers at all (`grep -c "Volatile\." PagedMMF.cs` returned 0). PR #489
(2026-07-17) made the concurrency and storage primitives arm64-correct and was verified on Apple Silicon, but its scope was
eleven files and `PagedMMF` was not among them. That pass could not have caught this one regardless: everything it fixed had
an *observable* failure mode, whereas a torn page here is CRC-stamped **over the torn bytes**, so it verifies clean on
reload and fails silently.

## Run these, in this order

Value descends down the list. If you only do one thing, do step 1.

### 1. Full engine suite — the thing that actually matters

```bash
dotnet test test/Typhon.Engine.Tests/Typhon.Engine.Tests.csproj -c Debug
```

Fourteen bug fixes on this branch have never executed on a weakly-ordered CPU. Beyond #579: #588/#589 changed R-Tree
traversal, #584 moved spatial counters to `Interlocked`, #585 reordered checkpoint writes, #580/#587 touched WAL paths.

**Expected:** green, or near-green. x64 baseline is **4103 passed / 0 failed / 53 skipped**.

**If a batch of failures appears** that is informative in its own right — it would mean the arm64 surface is wider than
#489's eleven files, and the sweep for other unfenced publication protocols becomes urgent rather than routine. Only 12
files in the whole engine use `Volatile` at all, and 6 of those got it from #489.

### 2. `bench` — the only guaranteed-useful measurement

```bash
dotnet run tools/seqlock-litmus/seqlock-litmus.cs bench 10 6    # run it THREE times
```

This is the one output x64 physically cannot produce. `Volatile`/`Interlocked` are free-ish under TSO; on arm64 they are
`ldar`/`stlr`/`dmb` and the cost is real. The two columns are reported separately on purpose — the writer and reader fixes
use different primitives and do not cost the same.

**Run it at least three times.** Single-run variance is brutal: successive x64 runs gave 3.6%/16.8%, then 0.5%/−0.3%, then
0.2%/1.1%. A negative figure is the tell that you are reading noise. Only a delta that survives repetition is real.

### 3. `selftest` — the gate that makes step 4 mean anything

```bash
dotnet run tools/seqlock-litmus/seqlock-litmus.cs selftest 10 6
```

Strips the seqlock protocol entirely and asserts tearing **is** observed. Without this, a clean `unfenced` run below cannot
be distinguished from a harness that simply never overlapped the writer and readers.

**Must report `TORN COPIES ACCEPTED AS VALID` > 0.** x64 reference: 22.0M of 22.1M.

**If it reports 0 — stop.** Every other result from that machine is worthless. Raise the 4th argument (`quietSpins`, the
writer's pause between passes) and retry: `... selftest 10 6 1200`.

### 4. `unfenced` vs `fenced` — the lottery ticket

```bash
dotnet run tools/seqlock-litmus/seqlock-litmus.cs unfenced 120 6
dotnet run tools/seqlock-litmus/seqlock-litmus.cs fenced   120 6
```

`unfenced` reproduces the pre-#579 protocol; `fenced` reproduces what shipped.

## Reading the results

| Outcome | Meaning | Action |
|---|---|---|
| `selftest` torn = 0 | harness never overlapped writer/reader on this core layout | **stop**, raise `quietSpins`, retry |
| `unfenced` torn > 0 | **genuine reproduction of #579 on real hardware** | paste into issue #579 — this is rare and valuable |
| `unfenced` torn = 0 | expected; says nothing about correctness | none |
| `fenced` torn > 0 | **the fix is wrong** | report immediately |
| `skipped` ≫ `validated` | duty cycle off for this chip | raise `quietSpins` |

**A clean `unfenced` run is not evidence the code was fine.** Weak-memory reorderings are *permitted*, not *mandatory* —
the window has to open, the reordering has to happen, and the tear has to land where it is checked. Apple Silicon in
particular is far more conservative than the ARM spec allows. Only a tear is positive evidence; this is a one-way test.

## Caveat I cannot control portably

Thread placement across P-cores and E-cores. Cross-cluster traffic is where reordering is most likely to become visible,
and the scheduler may or may not spread the threads that way — so a clean run may only mean everything landed on one
cluster. If you want to push on it, pinning threads to specific clusters would be the next lever, but that is
platform-specific and deliberately not attempted here.

## Arguments

```
dotnet run tools/seqlock-litmus/seqlock-litmus.cs <mode> [seconds] [readerThreads] [quietSpins]
```

| Arg | Default | Notes |
|---|---|---|
| `mode` | `selftest` | `selftest` · `unfenced` · `fenced` · `bench` |
| `seconds` | 10 | longer is strictly better for `unfenced` |
| `readerThreads` | 4 | roughly cores − 2 |
| `quietSpins` | 400 | writer pause between passes — **the tuning knob that matters** |

`quietSpins` deserves the emphasis. With the writer looping flat out the counter is odd essentially all the time: readers
skip billions of times and complete almost no copies, so the window this harness exists to probe is never entered. The
first version of this harness did exactly that — 16.6 *billion* skips against 8,883 validated copies. Tune until
`validated` and `retries` are both large.
