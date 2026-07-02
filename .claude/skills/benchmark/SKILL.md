---
name: benchmark
description: Run regression benchmarks, track results, and generate trend reports
argument-hint: [--quick] [--report-only] [--list] [--btree-fast] [--btree-medium] [--btree-full]
---

# Benchmark Regression Tracking

Run Typhon regression benchmarks, record results to history, and generate trend reports with regression detection.

> **Local runs are dev-only and DO NOT publish.** The public benchmark artifact
> (`benchmark/reports/{latest.md,charts/}` + `benchmark/history/results.jsonl`) is written **only by
> CI on the `z1d.metal` reference box** — benchmark numbers are hardware-dependent, so mixing a dev
> machine into the tracked trend would corrupt it. This skill therefore writes to the gitignored
> scratch dir `benchmark/.local/` and never commits. To publish reference numbers, run the
> **Benchmark** GitHub workflow (label a PR `run-benchmark`, or dispatch it). See
> `claude/design/Infrastructure/ci-merge-gate.md`.

**Comparison mode:** Each run is compared against the **immediately previous run** (not an averaged baseline). This gives a clear trend view.

**Noise filtering:** Benchmarks are automatically classified as "noisy" (filtered from regressions) when:
- Mean is below `min_measurable_ns` (default: 1.0ns) — below BDN's measurement resolution
- Coefficient of Variation exceeds `max_cov_pct` (default: 30%) — inherently high-variance benchmarks
- Absolute delta is below `min_delta_ns` (default: 0.5ns) — sub-ns shifts on fast micro-benchmarks

## Input

$ARGUMENTS may contain:
- `--quick` — Run with reduced warmup/iterations for fast feedback
- `--report-only` — Skip benchmark execution, regenerate reports from existing history
- `--list` — List all regression-tracked benchmarks with their thresholds
- `--btree-fast` — BTree quick profile: core ops + 2 concurrent scenarios (~3 min)
- `--btree-medium` — BTree medium profile: all key types + concurrent scaling (~15 min)
- `--btree-full` — BTree full profile: everything including tree sizes + enumeration (~50 min)
- (empty) — Full regression benchmark run + report generation

## Workflow

### `/benchmark --list`

List all regression-tracked benchmark classes and methods:

```bash
cd test/Typhon.Benchmark && dotnet run -c Release -- --list --allCategories Regression
```

Then read `benchmark/config.json` and display the configured thresholds per benchmark.

### `/benchmark --report-only`

Skip benchmark execution. Regenerate reports from existing history:

```bash
python3 benchmark/scripts/report_generator.py --history benchmark/.local/results.jsonl --config benchmark/config.json --output-dir benchmark/.local/reports
```

Read `benchmark/.local/reports/latest.md` and display a condensed summary.

### `/benchmark --quick`

Same as default workflow below, but append quick-mode flags to BDN.

**Clean stale artifacts first** (same as Step 2 of default workflow), then run **in the background** (`run_in_background: true`) and poll with `TaskOutput` (`block: true, timeout: 600000`):

```bash
cd test/Typhon.Benchmark && dotnet run -c Release -- --allCategories Regression --exporters json --warmupCount 1 --iterationCount 2
```

Then continue with report generation step.

### `/benchmark --btree-fast`

BTree quick profile (~3 min). Runs benchmarks tagged `BTreeFast`: core single-threaded ops, 2 concurrent scenarios (read scaling + write serialization), secondary index small-delta, and 95/5 mixed workload.

Follow the same Steps 1-6 as the default workflow, but use this BDN command in Step 3:

```bash
cd test/Typhon.Benchmark && dotnet run -c Release --no-build -- --btree-fast --exporters json
```

**Note:** `--btree-fast` is a custom Program.cs switch that maps to `--allCategories BTreeFast`.

### `/benchmark --btree-medium`

BTree medium profile (~15 min). Runs all `BTreeMedium`-tagged benchmarks: all key types (L16/L32/L64/String64), concurrent scaling with more thread counts, secondary index patterns.

Follow the same Steps 1-6 as the default workflow, but use this BDN command in Step 3:

```bash
cd test/Typhon.Benchmark && dotnet run -c Release --no-build -- --btree-medium --exporters json
```

**Note:** `--btree-medium` maps to `--allCategories BTreeMedium`.

### `/benchmark --btree-full`

BTree full profile (~50 min). Runs ALL BTree benchmarks: everything from fast + medium, plus tree depth scaling (100 to 100K entries), enumeration under contention (0-32 writers), and full thread count sweep.

Follow the same Steps 1-6 as the default workflow, but use this BDN command in Step 3:

```bash
cd test/Typhon.Benchmark && dotnet run -c Release --no-build -- --btree-full --exporters json
```

**IMPORTANT:** This can take up to ~50 minutes. Run **in the background** and poll with `TaskOutput` (`block: true, timeout: 3600000`).

**Note:** `--btree-full` maps to `--anyCategories BTreeFast BTreeMedium BTreeFull` (space-separated — BDN requires separate tokens, not comma-separated).

### `/benchmark` (default — full run + report)

#### Step 1: Build in Release

```bash
dotnet build -c Release test/Typhon.Benchmark/Typhon.Benchmark.csproj
```

If build fails, report errors and stop.

#### Step 2: Clean Stale BDN Artifacts

Remove prior BDN result files to prevent exploratory benchmark data from polluting the regression report:

```bash
# Windows
if exist "test\Typhon.Benchmark\BenchmarkDotNet.Artifacts\results" rmdir /s /q "test\Typhon.Benchmark\BenchmarkDotNet.Artifacts\results"
```
```bash
# Unix/macOS
rm -rf test/Typhon.Benchmark/BenchmarkDotNet.Artifacts/results
```

#### Step 3: Run Regression Benchmarks

```bash
cd test/Typhon.Benchmark && dotnet run -c Release --no-build -- --allCategories Regression --exporters json
```

**IMPORTANT:** This step can take up to ~12 minutes, which exceeds the Bash tool's 10-minute max timeout. Run this command **in the background** (`run_in_background: true`) and poll with `TaskOutput` (use `block: true, timeout: 600000`). Let the user know benchmarks are running before starting the background task.

#### Step 4: Generate Report

```bash
python3 benchmark/scripts/report_generator.py --bdn-results test/Typhon.Benchmark/BenchmarkDotNet.Artifacts/results --history benchmark/.local/results.jsonl --config benchmark/config.json --output-dir benchmark/.local/reports
```

#### Step 5: Display Summary

Read `benchmark/.local/reports/latest.md` and display a condensed summary to the user:

- Total benchmarks run
- **Regressions found** (list each with name + % change) — highlight prominently
- **Improvements found** (list each with name + % change)
- Stable benchmark count
- Link to full report: `benchmark/.local/reports/latest.md`

#### Step 6: Do NOT publish

Local benchmark runs are **dev-only**. The results live in the gitignored `benchmark/.local/` and
are **never committed** — the public reference artifact is produced solely by CI on `z1d.metal`
(label a PR `run-benchmark`, or dispatch the **Benchmark** workflow). Do not stage or commit anything
from this run.
