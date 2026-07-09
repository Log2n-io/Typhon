---
uid: feature-querying-index
title: 'Querying'
description: 'The query and view engine: a single archetype-rooted fluent API (tx.Query()) parses C# lambda predicates — indexed-field, opaque, OR-disjunctive, spatial,…'
---

# Querying
> The query and view engine: a single archetype-rooted fluent API (`tx.Query<T>()`) parses C# lambda predicates — indexed-field, opaque, OR-disjunctive, spatial, or FK-join — into selectivity-driven execution plans run through a batched streaming pipeline. Persistent Views cache a query's matching entity set and refresh it in microseconds via lock-free commit-time change capture instead of full re-query; point-in-time history and parameterized view pooling are still on the roadmap.

> 🔬 **Recommended:** read [in-depth-overview/09-querying.md](../../in-depth-overview/09-querying.md) (Chapter 09: Querying, which also carries a short Subscriptions section) first to understand the overall design and concepts behind this category, before diving into the specific features below.

## Public Features

| Feature | Summary | Status | Level |
|---|---|---|---|
| [Fluent Query API & Predicate Parsing](fluent-query-api/README.md) | Archetype-rooted fluent builder that parses C# lambdas into index-driven plans, with structural/enabled-bit constraints, OR disjunction, and FK navigation joins | ✅ Implemented | 🟢 Start Here |
| &nbsp;&nbsp;↳ [Indexed Field Predicates (WhereField)](fluent-query-api/wherefield-indexed-predicate.md) | Expression-parsed predicate that drives a targeted B+Tree scan and powers incrementally-maintained reactive views | ✅ Implemented | 🔵 Core |
| &nbsp;&nbsp;↳ [Opaque Post-Filter Predicates (Where)](fluent-query-api/where-opaque-postfilter.md) | Arbitrary per-entity C# delegate evaluated after a broad archetype scan, for logic the index system can't express | ✅ Implemented | 🟢 Start Here |
| &nbsp;&nbsp;↳ [OR Disjunction (DNF Predicates)](fluent-query-api/or-disjunction.md) | `\|\|` in a `WhereField` predicate, normalized to Disjunctive Normal Form and evaluated as independent branches | ✅ Implemented | 🟣 Advanced |
| &nbsp;&nbsp;↳ [Foreign-Key Navigation Joins (L4)](fluent-query-api/fk-navigation-joins.md) | Join across an entity-reference field — filter source entities by predicates on the target entity they point to | ✅ Implemented | 🟣 Advanced |
| [Result Ordering & Pagination](ordering-pagination.md) | Sorted, paged query results driven directly off a B+Tree index scan — no full-scan-then-sort | ✅ Implemented | 🔵 Core |
| [Persistent Views — Incremental Refresh & Delta Tracking](persistent-views.md) | TSN-anchored persistent Views (`ToView()`) refreshed via lock-free MPSC ring-buffer change capture at commit time, exposing Added/Removed/Modified deltas | 🚧 Partial | 🔵 Core |
| [Spatial Query Predicates](spatial-predicates.md) | R-Tree-backed AABB, radius, and ray filters attached directly to a fluent ECS query | ✅ Implemented | 🟣 Advanced |
| [Execution Planning & Pipeline Execution](execution-planning-pipeline.md) | Picks the most selective index as the scan driver and streams results into the caller's collection | ✅ Implemented | 🟣 Advanced |
| [Statistics Infrastructure (HLL / MCV / Histogram)](statistics-infrastructure.md) | Background-maintained per-field statistics feeding the selectivity estimator, refreshed by a tunable polling worker thread | ✅ Implemented | 🟣 Advanced |
| [ViewFactory — Parameterized Queries & View Pooling](view-factory-pooling.md) | Reusable query templates with a Rent/Return view pool, to remove per-session view setup cost | 📋 Planned | 🟣 Advanced |
| [Temporal Queries (Point-in-Time Read & Revision History)](temporal-queries.md) | Opt-in per-component history retention enabling reads of past state and full revision timelines | 📋 Planned | 🟣 Advanced |

## Internal Features

*No internal-only features in this category — every feature above is reached directly through `tx.Query<T>()`/`IView`, `[Index]`/`[SpatialIndex]` schema attributes, or a documented config knob (`StatisticsOptions`), so nothing here is engine-only machinery.*