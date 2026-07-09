---
uid: feature-querying-statistics-infrastructure
title: 'Statistics Infrastructure (HLL / MCV / Histogram)'
description: 'Background-maintained per-field statistics that let the query planner see data skew instead of guessing uniform distribution.'
---

# Statistics Infrastructure (HLL / MCV / Histogram)
> Background-maintained per-field statistics that let the query planner see data skew instead of guessing uniform distribution.

**Status:** ✅ Implemented · **Visibility:** Public · **Level:** 🟣 Advanced · **Category:** [Querying](./README.md)

## 🎯 What it solves

Execution planning needs to know roughly how many entities a predicate will match before running it, so it can pick the most selective index as the scan driver. Without real distribution data, the planner can only assume every value is equally likely — fine for uniform data, wrong for the skew that's the norm in game data (most players are low level, one faction has 80% of the population, item rarity is heavily weighted toward "common"). A skewed field with no statistics gets the same cardinality estimate for a hot value and a rare one, leading the planner to pick the wrong primary scan stream. Statistics Infrastructure gives the planner real distribution data — refreshed automatically, off the commit path — so plan quality holds even on skewed fields.

## ⚙️ How it works (in brief)

Each `[Index]`-annotated field optionally carries three structures: a HyperLogLog sketch (approximate distinct-value count), a Most-Common-Values table (exact frequencies for the top 100 values), and a 100-bucket equi-width histogram (range distribution). A dedicated low-priority background thread polls periodically, and for each table whose mutation count has crossed a threshold since the last rebuild, it runs a single chunk-based scan that (re)builds all three structures and atomically swaps them in — concurrent queries never see a torn read. Large tables are page-sampled and the counts scaled back up, bounding scan time regardless of table size. Until the first rebuild happens (or if the worker is disabled), the planner transparently falls back to exact B+Tree counts and uniform-distribution math — query correctness never depends on statistics existing.

## 💻 Usage

```csharp
// Opt in at engine startup — null (default) means no worker thread, no overhead
services.AddDatabaseEngine(o => o.Statistics = new StatisticsOptions
{
    MutationThreshold = 1000,    // rebuild a table after this many index mutations
    PollIntervalMs = 5000,       // worker poll interval
    MinEntitiesForRebuild = 100, // skip tiny tables
    SamplingMinEntities = 10000, // full scan below this; page-sampled above
});

[Component("Game.Player", 1)]
public struct Player
{
    [Index] public int Level;   // statistics build automatically once mutations accrue
    [Index] public int Faction;
}

// No statistics-specific API to call — once enabled, AdvancedSelectivityEstimator
// picks up MCV/Histogram automatically inside execution planning:
var veterans = tx.Query<PlayerArch>()
    .WhereField<Player>(p => p.Faction == 1 && p.Level > 50)
    .Execute();                                   // → HashSet<EntityId>, plan benefits silently
```

| Option | Default | Effect |
|---|---|---|
| `MutationThreshold` | 1000 | Index mutations on a table before the next poll triggers a rebuild |
| `PollIntervalMs` | 5000 | Background worker poll interval; floor-clamped to 100ms |
| `MinEntitiesForRebuild` | 100 | Tables smaller than this are skipped (not worth the scan) |
| `SamplingMinEntities` | 10000 | Below this: full scan; above: page-granularity sampling |
| `Enabled` | `true` | Master on/off switch (separate from leaving `Statistics` null) |

## ⚠️ Guarantees & limits

- **Opt-in, zero default overhead.** `DatabaseEngineOptions.Statistics` is `null` by default — no worker thread, no scans, no memory cost unless explicitly configured.
- **Zero commit-path cost.** The only hook on the write path is a non-atomic `int` increment per mutation; the rebuild scan itself runs entirely on the background thread.
- **No public API to read statistics directly.** HLL/MCV/Histogram are internal to the selectivity estimator — there is nothing to query or inspect from application code; their only observable effect is plan quality (see [Execution Planning & Pipeline Execution](./execution-planning-pipeline.md)).
- **Staleness is bounded, not zero.** Statistics reflect the data as of the last rebuild — up to `MutationThreshold` mutations plus one `PollIntervalMs` tick stale. A stale estimate degrades plan quality, never correctness.
- **No incremental maintenance.** Rebuilds are full (or sampled) re-scans, not deltas — HyperLogLog has no removal operation, so incremental updates would accumulate ghost values from deleted/updated entities.
- **Per-field memory cost when built:** ~4KB (HLL) + ~1.8KB (MCV) + ~0.4KB (Histogram) ≈ 6.2KB per indexed field, allocated lazily on first rebuild.
- **One table's rebuild failure doesn't block others** — the worker logs the last exception (diagnostic only) and keeps polling the rest of the registered tables.
- `String64`-keyed indexes are excluded from statistics — only fixed-width numeric key types are supported.

## 🧪 Tests

- [StatisticsRebuildTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/Query/StatisticsRebuildTests.cs) — background worker lifecycle, mutation-threshold-triggered rebuild, atomic torn-read-free swap, sampling, and the `String64`-field skip
- [HyperLogLogTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/Query/HyperLogLogTests.cs) — HLL cardinality estimate accuracy (unique, skewed, and merged sketches)
- [MostCommonValuesTests](https://github.com/Log2n-io/Typhon/blob/main/test/Typhon.Engine.Tests/Data/Query/MostCommonValuesTests.cs) — MCV top-100 identification on Zipf-skewed data, exact-count lookup hit/miss

## 🔗 Related

- Related feature: [Execution Planning & Pipeline Execution](./execution-planning-pipeline.md)

<!-- Deep dive: claude/overview/05-query.md §5.11 Statistics Infrastructure -->
<!-- Deep dive: claude/design/Querying/ViewSystem/04-query-planning.md — selectivity estimator chain, statistics structures, rebuild/worker algorithms -->
<!-- ADR: claude/adr/043-advanced-statistics-selectivity.md — why HLL/MCV/histogram, why background rebuild over inline/incremental maintenance -->
