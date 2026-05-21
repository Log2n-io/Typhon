# 09 — Querying

**Code:** [`src/Typhon.Engine/Querying/`](../../src/Typhon.Engine/Querying/) (+ a short tour of [`src/Typhon.Engine/Subscriptions/`](../../src/Typhon.Engine/Subscriptions/) in §8)

Querying is the read side of Typhon's ECS data model. Application code asks "give me the entities matching these constraints"; the engine turns that into an execution plan, scans the smallest possible amount of state, and streams matching primary keys into a caller-owned container. On top of that one-shot pipeline sits a **view system** — long-lived, incrementally maintained sets of entity IDs that get push-notified by writers and report `Added` / `Removed` / `Modified` deltas back to the consumer.

This doc covers both layers. The query builder ([`EcsQuery<TArchetype>`](../../src/Typhon.Engine/Ecs/public/EcsQuery.cs)) actually lives in `Ecs/` because it's archetype-shaped — but every plumbing piece behind it (`ExpressionParser`, `PlanBuilder`, `ExecutionPlan`, `FieldEvaluator`, `PipelineExecutor`, statistics) lives in `Querying/`. The view types (`IView`, `ViewBase`, `ViewDelta`, `ViewDeltaRingBuffer`, `ViewRegistry`) live in `Querying/` too. `EcsView<TArch>` lives in `Ecs/` for the same reason as `EcsQuery`.

<a href="assets/typhon-query-overview.svg">
  <img src="assets/typhon-query-overview.svg" width="691" alt="Query engine — component overview">
</a>
<br>
<sub>Query engine components: <code>EcsQuery</code> (fluent API) → <code>ExpressionParser</code> → <code>PlanBuilder</code> → <code>PipelineExecutor</code>, feeding the view layer (<code>ViewBase._entityIds</code>, <code>ViewDelta</code> + <code>DeltaKind</code>, <code>ViewRegistry</code> / <code>IndexMaintainer</code>) and reading from ComponentTable / B+Tree indexes / statistics.</sub>

---

## 1. Overview — the two pipelines

There are two distinct query paths and one shared planner.

| Path | Trigger | Lifetime | Output |
|---|---|---|---|
| **One-shot `Execute()`** | `tx.Query<T>()....Execute()` | Runs once, allocates a `HashSet<EntityId>`, returns | Snapshot at `tx.TSN` |
| **View (`.ToView()`)** | `tx.Query<T>()....ToView()` | Registers with `ViewRegistry`, lives until `Dispose()` | Maintained `HashMap<long>` + per-tick `ViewDelta` |

Both go through the same machinery:

1. **`ExpressionParser`** — `Expression<Func<T, bool>>` lambdas are normalized to Disjunctive Normal Form (OR of ANDs), one `FieldPredicate[]` per OR branch.
2. **`QueryResolverHelper.ResolveEvaluators`** — `FieldPredicate` (string field name + op + boxed value) becomes `FieldEvaluator` (16 B: field index, key type, op, byte branch index, 8-byte widened threshold). String64 predicates are **rejected here** ([`QueryResolverHelper.cs:93`](../../src/Typhon.Engine/Querying/internals/QueryResolverHelper.cs)) — `KeyType.String64` exists for the index/statistics layer only; no scalar comparator for it.
3. **`PlanBuilder`** — picks the most selective indexed field as the primary scan (via `AdvancedSelectivityEstimator`), narrows its key range, orders the rest of the evaluators by ascending estimated cardinality (most selective first → short-circuit), builds an `ExecutionPlan`.
4. **`PipelineExecutor`** — iterates the primary B+Tree, walks the MVCC revision chain (or reads SV/Transient component data directly), evaluates non-primary predicates inline, streams matching entity PKs into a caller-provided `HashMap<long>` / `List<long>`.

Views layer on top of (4) by registering an `IView` with the per-`ComponentTable` `ViewRegistry`. When the index maintainer commits a mutation that crosses a registered field's boundary, the registry routes a `ViewDeltaEntry` into each affected view's lock-free MPSC `ViewDeltaRingBuffer`. The view's `Refresh(tx)` drains the ring buffer and reconciles its `HashMap<long>` against the new state.

<a href="assets/typhon-query-pipeline.svg">
  <img src="assets/typhon-query-pipeline.svg" width="1200" alt="Query execution pipeline">
</a>
<br>
<sub>The pipeline executor's five steps: select the most-selective primary index → stream entity IDs via index scan → check secondary predicates → materialize components → add to the view's entity set (<code>ViewBase._entityIds</code>).</sub>

---

## 2. Query API — `EcsQuery<TArchetype>`

[`Ecs/public/EcsQuery.cs`](../../src/Typhon.Engine/Ecs/public/EcsQuery.cs)

Constructed via `tx.Query<TArchetype>()` (polymorphic — includes descendant archetypes) or `tx.QueryExact<TArchetype>()` (only the exact archetype). The struct is mutated fluently; each call returns the modified struct value.

### Three tiers of constraints

| Tier | Method | What it filters | Cost |
|---|---|---|---|
| T1 — Archetype | `With<T>()`, `Without<T>()`, `Exclude<TArch>()` | Mask over `ArchetypeId` (256-bit or large bitmap) | One bit test per archetype |
| T2 — Enabled bits | `Enabled<T>()`, `Disabled<T>()` | Per-entity 16-bit enable mask (max **4** of each kind per query) | Cluster occupancy mask AND |
| T3 — Field predicates | `Where<T>(Func<T,bool>)`, `WhereField<T>(Expression<Func<T,bool>>)`, spatial predicates | Per-entity component values | Index scan + predicate eval |

`Where<T>(Func<T, bool>)` takes a compiled delegate — opaque to the planner, evaluated post-scan via `tx.Open(id).TryRead<T>(...)`. It chains as AND.

`WhereField<T>(Expression<Func<T, bool>>)` is the **planner-visible** form. The lambda gets parsed by `ExpressionParser`, broken into DNF, indexed-field-resolved, and folded into the execution plan. Multiple `WhereField` calls cross-product as **AND of ORs** (DNF normalization at `EcsQuery.cs:299-321`). Required for incremental views.

### Spatial predicates

```csharp
query.WhereInAABB<Position>(minX, minY, minZ, maxX, maxY, maxZ);
query.WhereNearby<Position>(centerX, centerY, centerZ, radius);
query.WhereRay<Position>(originX, originY, originZ, dirX, dirY, dirZ, maxDist);
```

The component type `T` must be tagged `[SpatialIndex]` (see [07-spatial](07-spatial.md)). Only one spatial predicate per query (the inline 7-`double` parameter array is overwritten on repeated calls). At execution, the spatial index produces candidate entity IDs and the rest of the query (archetype mask + visibility + remaining predicates) filters them.

### `OrderBy` / `Skip` / `Take`

```csharp
query.WhereField<Position>(p => p.X >= 0)
     .OrderByField<Position, int>(p => p.Level)
     .Skip(100).Take(50)
     .ExecuteOrdered();
```

`OrderByField`/`OrderByFieldDescending` require a prior `WhereField` to identify the component table. The OrderBy field **must be indexed**. `Skip` / `Take` require `OrderBy` (without it the result set is unordered).

Three ordered execution paths inside `ExecuteOrdered`:

- **Pure non-cluster:** `PipelineExecutor.ExecuteOrdered` does the B+Tree range scan in sort order.
- **Pure cluster archetypes:** K-way merge across each archetype's per-archetype B+Tree (`ExecuteOrderedClustered`). Each stream yields keys in sort order; the merge interleaves them. Early termination once `Skip + Take` entries are collected.
- **Mixed cluster + non-cluster:** `ExecuteTargeted()` then sort by the OrderBy field (`ExecuteOrderedViaSortFallback`). O(n log n) — acceptable for the rare mixed case.

### `EcsNavigationQueryBuilder<TArch, TSource, TTarget>`

[`Ecs/public/EcsNavigationQueryBuilder.cs`](../../src/Typhon.Engine/Ecs/public/EcsNavigationQueryBuilder.cs)

```csharp
tx.Query<Worker>()
  .NavigateField<Worker, Building>(w => w.HomeBuildingId)
  .Where((src, tgt) => src.Active && tgt.Level >= 5)
  .Execute();
```

FK-join query. Created from `EcsQuery.NavigateField<TSource, TTarget>(fkSelector)`. The FK selector must reference a `long` field on the source component. Wraps a plain [`NavigationQueryBuilder<TSource, TTarget>`](../../src/Typhon.Engine/Querying/public/NavigationQueryBuilder.cs) for the planner-side resolution. `Execute()` scans each target archetype's `EntityMap`, looks up the FK index on the source table, and joins. `ToView()` registers with **both** source and target `ViewRegistry` (deletions to the target invalidate the view, which is why target predicates are mandatory).

---

## 3. Plan building

### `ExpressionParser` — DNF with a 16-branch cap

[`Querying/internals/ExpressionParser.cs`](../../src/Typhon.Engine/Querying/internals/ExpressionParser.cs)

`ParseDnf<T>(Expression<Func<T, bool>>)`:

1. Build an AST from the expression tree.
2. Apply DNF rewrite (distribute `&&` over `||`).
3. Collect leaves into `FieldPredicate[][]` — outer = OR branches, inner = AND predicates per branch.

**Branch cap:** `MaxDnfBranches = 16` ([`ExpressionParser.cs:23`](../../src/Typhon.Engine/Querying/internals/ExpressionParser.cs)). DNF normalization can blow up exponentially — `(A||B) && (C||D) && (E||F)` produces 2×2×2 = 8 branches; five ANDed pairs would explode past 16. Hit the cap → `InvalidOperationException` with a message telling you to split the query or reduce ANDed OR pairs. The cap aligns with the OR-mode view's 16-bit branch bitmap (see §5).

### `PlanBuilder`

[`Querying/internals/PlanBuilder.cs`](../../src/Typhon.Engine/Querying/internals/PlanBuilder.cs)

Singleton (`PlanBuilder.Instance`). Two stages:

1. **`OrderBySelectivity`** — calls `AdvancedSelectivityEstimator.EstimateCardinality(table, fieldIdx, op, threshold)` for each evaluator, sorts evaluators ascending by estimated count.
2. **`BuildPlanWithPrimarySelection`** — picks the most selective indexed evaluator as the primary scan stream, computes its `[scanMin, scanMax]` range, builds the `ExecutionPlan`.

`BuildPlanAttributed` is the production entry point — accepts the originating query's identity and source-location info for trace attribution (Phase 7 observability — see [12-observability](12-observability.md)).

### `ExecutionPlan`

[`Querying/public/ExecutionPlan.cs`](../../src/Typhon.Engine/Querying/public/ExecutionPlan.cs)

`readonly struct` (16-byte fixed fields + two arrays):

| Field | Meaning |
|---|---|
| `PrimaryFieldIndex` | Index into `ComponentTable.IndexedFieldInfos`. `-1` means the planner couldn't pick a secondary index. |
| `PrimaryKeyType` | `KeyType` of the primary index. Valid only when `PrimaryFieldIndex >= 0`. |
| `PrimaryScanMin` / `PrimaryScanMax` | Long-encoded inclusive bounds for the primary scan. |
| `Descending` | Iterate primary index in reverse order. |
| `OrderedEvaluators` | All evaluators, sorted by ascending estimated cardinality (selectivity-first). |
| `EstimatedCounts` | Per-evaluator cardinality (parallel array). |
| `UsesSecondaryIndex` | `PrimaryFieldIndex >= 0`. |

**On the "PK fallback":** When no indexed field is suitable as a primary scan, `PlanBuilder` still produces a plan with `PrimaryFieldIndex = -1`. In the current code this plan is **non-executable** for the one-shot path — `PipelineExecutor.ExecuteCore` checks `plan.UsesSecondaryIndex` and short-circuits when false; `Count` returns 0 directly. The PK B+Tree that the historical full-table-scan fallback used was removed; the engine relies on every query reaching a secondary index. Cluster-storage ordered scans separately handle a `-1` plan by using the OrderBy field's per-archetype B+Tree directly (see `ExecuteOrderedClustered` in `EcsQuery.cs`).

### `FieldEvaluator`

[`Querying/public/FieldEvaluator.cs`](../../src/Typhon.Engine/Querying/public/FieldEvaluator.cs)

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct FieldEvaluator  // exactly 16 bytes
{
    public byte FieldIndex;      // index into IndexedFieldInfos (max 63)
    public byte FieldSize;
    public KeyType KeyType;      // Bool..Double; String64 reserved (see below)
    public CompareOp CompareOp;  // ==, !=, <, <=, >, >=
    public byte ComponentTag;    // 0 = T1, 1 = T2 (multi-component views)
    public byte BranchIndex;     // DNF branch (0 for AND view, 0..15 for OR view)
    public ushort FieldOffset;   // byte offset within component
    public long Threshold;       // 8-byte widened constant; float/double via reinterpret
}
```

`Evaluate(ref FieldEvaluator eval, byte* fieldPtr)` dispatches on `KeyType` to the right primitive comparator and short-circuits on the first false. `[MethodImpl(AggressiveInlining)]` on every step.

**Why no String64 evaluator:** the `Threshold` slot is 8 bytes — not large enough to hold a 64-byte `String64`. Predicates with string fields are rejected at `ResolveEvaluators` time by `QueryResolverHelper.MapFieldTypeToKeyType` (throws `NotSupportedException` for `FieldType.String`). The `String64BTree` still exists as an index — it just can't be used as a scalar predicate target. The `KeyType.String64` tag survives in `IndexStatistics` to flag "no selectivity stats for this index".

---

## 4. Execution — `PipelineExecutor`

[`Querying/internals/PipelineExecutor.cs`](../../src/Typhon.Engine/Querying/internals/PipelineExecutor.cs)

Singleton (`PipelineExecutor.Instance`). Three public methods:

```csharp
void Execute(ExecutionPlan, FieldEvaluator[], ComponentTable, Transaction, HashMap<long> result);
void ExecuteOrdered(ExecutionPlan, FieldEvaluator[], ComponentTable, Transaction, List<long> result, int skip, int take);
int  Count(ExecutionPlan, FieldEvaluator[], ComponentTable, Transaction);
```

The caller owns the `HashMap<long>` / `List<long>` lifecycle. One-shot queries allocate a fresh container; views reuse `ViewBase._entityIds`. There is **no internal buffering** — matching entity PKs are streamed from the B+Tree iterator directly into the caller's container as they pass the filter chain.

### Three storage-mode branches

`ExecuteCoreSecondaryIndex` dispatches on `table.StorageMode`:

- **`Versioned`** → `ExecutePKsTypedVersioned`. The index value is the `compRevFirstChunkId` of the entity's revision chain. The pipeline reads `EntityPK` from `CompRevStorageHeader` (one cache line), checks MVCC visibility via `tx.IsEntityVisible(entityPK)`, and only if non-primary filters exist does it walk the revision chain via `RevisionChainReader.WalkChain(tx.TSN)` to resolve the current component content chunk for predicate evaluation. **No EntityMap re-lookup** — the index entry already points at the chain.
- **`SingleVersion`** → `ExecutePKsTypedSV`. Index values are component chunk IDs. `EntityPK` is read from offset 0 of the chunk's inline overhead. No revision chain; no MVCC — SV reads see the current in-place value.
- **`Transient`** → `ExecutePKsTypedNonVersioned<TKey, TransientStore>`. Same shape as SV but using `TransientStore` accessors. Transient data lives in `TransientComponentSegment` and never persists.

The SV and Transient paths share the `ExecutePKsTypedNonVersioned<TKey, TStore>` generic — `TStore` is the only thing that differs structurally.

### Iterator shape

For an `AllowMultiple = true` index, the B+Tree exposes `EnumerateRangeMultiple(min, max)` / `EnumerateRangeMultipleDescending` — one outer `MoveNextKey` per distinct key, an inner `do { var values = enumerator.CurrentValues; ... } while (NextChunk());` for all chunks of multi-values at that key. For unique indexes it's a plain `foreach (var kv in EnumerateRange(min, max))`. Either way the executor streams entries through the per-entity check function (`ExecuteOneVersioned` / inline-evaluator-loop for SV/Transient).

### `ExecuteFullScan` (it's not on `PipelineExecutor`)

The wrapper `ExecuteFullScan(plan, evaluators, table, tx, HashMap<long> result)` lives on **[`EcsViewFieldReader`](../../src/Typhon.Engine/Ecs/public/EcsView.cs)** (`Ecs/public/EcsView.cs:864`), not on `PipelineExecutor`. The typed subclass `EcsViewFieldReader<T>` simply delegates to `PipelineExecutor.Instance.Execute(...)`. It exists as a thin abstraction so `EcsView` can call the executor without knowing the component type — the `EcsView` is parameterized by `TArchetype`, the field reader carries the component `T`. `ExecuteOrderedScan` and `CountScan` follow the same pattern.

### What's not there (audit clarifications)

There is **no** "batch 256 PKs into a stackalloc buffer" mechanism. There is no `Batch` or `BatchSize` constant in `PipelineExecutor`. Streaming is per-entry, directly from the B+Tree enumerator into the caller's container.

---

## 5. View system

[`Querying/public/IView.cs`](../../src/Typhon.Engine/Querying/public/IView.cs), [`Querying/public/ViewBase.cs`](../../src/Typhon.Engine/Querying/public/ViewBase.cs), [`Ecs/public/EcsView.cs`](../../src/Typhon.Engine/Ecs/public/EcsView.cs)

### `IView` — registry-facing contract

```csharp
public interface IView
{
    int ViewId { get; }                // monotonic per-process
    int[] FieldDependencies { get; }   // indices into ComponentTable.IndexedFieldInfos
    bool IsDisposed { get; }
}
```

The `IView` surface deliberately omits the internal `ViewDeltaRingBuffer` — that's supplied separately at registration time, so registry code can read it directly off a field instead of through interface dispatch.

### `ViewBase` — shared state

```csharp
public abstract class ViewBase : IView, IDisposable, IEnumerable<long>
{
    internal readonly HashMap<long> _entityIds = new();                      // POH-allocated entity-PK set
    private readonly Dictionary<long, DeltaKind> _deltas = new(16);          // per-tick delta map
    protected readonly FieldEvaluator[] Evaluators;
    internal ViewDeltaRingBuffer DeltaBuffer { get; }
    // ViewId, FieldDependencies, IsDisposed, SourceFile/Line/Method (caller-attribute capture)
}
```

The XML doc comment on `ViewBase` (line 10) still references `View<T>`, `View<T1,T2>`, and `OrView<T>` — **those types are deleted**. Don't propagate the names. The single concrete derived type today is `EcsView<TArchetype>`.

`_entityIds` is the maintained working set. `_deltas` accumulates per-tick `DeltaKind` (Added / Removed / Modified) per PK; `ClearDelta()` drops the map. Idempotent compaction is done inline in `CompactDelta`: Added+Removed cancels, Modified+Removed promotes to Removed, Removed+Added becomes Modified.

### `EcsView<TArchetype>`

Five constructors, four modes:

| Mode | Trigger | Behavior |
|---|---|---|
| Pull | `EcsQuery` without `WhereField` | No evaluators, no ViewRegistry registration. `Refresh()` re-executes the full query. |
| Incremental (single AND branch) | One `WhereField` call | Single `ExecutionPlan`, ring buffer driven, registered with `ViewRegistry`. |
| Incremental (cached plan) | Same as above, with plan reuse for descriptor emission | Caches the plan in `ViewBase._cachedPlans` for per-tick `QueryPlan` span emission. |
| OR mode | Multiple DNF branches | One `ExecutionPlan` and one `FieldEvaluator[]` per branch. Per-entity `ushort` branch bitmap (`_branchBitmaps`) records which branches that entity currently satisfies. |

EntityId-facing surface:

- `EntityIdEnumerator GetEntityEnumerator()` — over the internal `HashMap<long>` keys cast to `EntityId`.
- `IReadOnlyList<EntityId> Added`, `Removed` — caches rebuilt from `ViewBase._deltas` on each `Refresh()`, so consumers can `foreach` typed handles instead of raw longs.
- `bool Contains(EntityId)` / `Contains(long)`, `int Count`, `bool HasChanges`.

### `DeltaKind` and `ViewDelta`

[`Querying/internals/DeltaKind.cs`](../../src/Typhon.Engine/Querying/internals/DeltaKind.cs), [`Querying/public/ViewDelta.cs`](../../src/Typhon.Engine/Querying/public/ViewDelta.cs)

```csharp
internal enum DeltaKind : byte { Added = 0, Removed = 1, Modified = 2 }

public readonly struct ViewDelta
{
    public readonly DeltaView Added;     // O(1) Count, O(1) Contains
    public readonly DeltaView Removed;
    public readonly DeltaView Modified;
    public bool IsEmpty { get; }
}
```

`ViewDelta` is a zero-allocation read-only handle over `ViewBase._deltas`. `DeltaView` is a filtered IReadOnlyCollection over the same dictionary that only enumerates entries matching its `DeltaKind` filter. Lifetime is "until the next `ClearDelta()`" — do not cache across refresh cycles.

### OR-mode bitmaps

When an OR view has N branches (1 ≤ N ≤ 16), each entity in `_entityIds` also has a `ushort` entry in `_branchBitmaps`: bit *i* set means "entity satisfies branch *i*". An entity is in the view iff its bitmap is non-zero. When an incoming delta clears the last branch, the entity is removed; when a new branch flips on, the bitmap is updated but the view membership doesn't change. This is why the DNF branch cap is 16 — the bitmap is a `ushort`.

### `ViewRegistry`

[`Querying/internals/ViewRegistry.cs`](../../src/Typhon.Engine/Querying/internals/ViewRegistry.cs)

One per `ComponentTable`. Maps `fieldIndex → ViewRegistration[]` (copy-on-write — readers are lock-free, writers take a `Lock`).

```csharp
internal readonly struct ViewRegistration
{
    public readonly IView View;
    public readonly ViewDeltaRingBuffer DeltaBuffer;  // cached here so the hot path doesn't cast
    public readonly byte ComponentTag;                // 0 = T1, 1 = T2 (multi-component views)
}
```

On `RegisterView(IView, DeltaBuffer)` the registry inserts a `ViewRegistration` into each field-dependency bucket. On `DeregisterView`, it sweeps all buckets, removing every registration for that view (some views are registered under multiple component tags).

### `ViewDeltaRingBuffer`

[`Querying/internals/ViewDeltaRingBuffer.cs`](../../src/Typhon.Engine/Querying/internals/ViewDeltaRingBuffer.cs)

MPSC lock-free ring buffer. Per-view. **Default capacity 4096** (must be a power of two). SOA layout — separate arrays for the 24-byte `ViewDeltaEntry`, the 8-byte `tsn`, the 1-byte `flags`, the 1-byte `componentTag`, the 1-byte `written` marker — each starting at a 64-byte cache-line boundary, all backed by a **single `IMemoryAllocator.AllocatePinned` block** so the buffer accounts as one resource node.

Producer hot path is a CAS on a `PaddedLong _tail`; consumer hot path is a plain increment on a `PaddedLong _head` — `PaddedLong` is `[StructLayout(Explicit, Size = 64)]` to keep producer and consumer counters on separate cache lines. On overflow (`tail - head >= capacity`), the producer sets a sticky `_overflow = 1` flag and returns false; the owning view's next `Refresh()` reacts by doing a full re-scan and reporting `HasOverflow`.

`ViewDeltaEntry` is exactly **24 bytes**:

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct ViewDeltaEntry  // 24 bytes
{
    public long EntityPK;        //  8 B offset  0
    public KeyBytes8 BeforeKey;  //  8 B offset  8
    public KeyBytes8 AfterKey;   //  8 B offset 16
}
```

`KeyBytes8` is an 8-byte opaque field key — sized to fit any indexed scalar exactly. Larger key types (String64) aren't supported as view predicates, so 8 B is sufficient.

---

## 6. Statistics — selectivity estimation

The planner can't pick the most selective index without per-field cardinality estimates. Typhon maintains three sketches per indexed field, rebuilt by a background worker.

### `StatisticsWorker`

[`Querying/internals/StatisticsWorker.cs`](../../src/Typhon.Engine/Querying/internals/StatisticsWorker.cs)

Dedicated background thread. Wakes every `PollIntervalMs`. For each `ComponentTable`: if `MutationsSinceRebuild` exceeds `MutationThreshold` and entity count ≥ `MinEntitiesForRebuild`, it triggers a `StatisticsRebuilder.RebuildAll(...)` pass. Page-granularity sampling kicks in above `SamplingMinEntities`.

### `StatisticsOptions`

[`Querying/public/StatisticsOptions.cs`](../../src/Typhon.Engine/Querying/public/StatisticsOptions.cs)

| Property | Default | Meaning |
|---|---|---|
| `MutationThreshold` | 1000 | Index mutations before a rebuild triggers |
| `PollIntervalMs` | 5000 | Worker poll cadence (floor-clamped to 100) |
| `MinEntitiesForRebuild` | 100 | Skip tables smaller than this |
| `SamplingMinEntities` | 10000 | Above this, page-sample instead of full-scan |
| `Enabled` | `true` | Master switch |

### `HyperLogLog`

[`Querying/internals/HyperLogLog.cs`](../../src/Typhon.Engine/Querying/internals/HyperLogLog.cs)

Cardinality estimator. **Precision 12 → 4096 registers → 4 KB per sketch**. Hashes input via the Murmur3 64-bit finalizer (3 xor-shifts + 2 multiplies — keys are already long-encoded). `EstimateCardinality()` applies the Flajolet et al. harmonic-mean estimator with LinearCounting small-range correction (no 32-bit large-range correction — the 64-bit hash space doesn't need it). Immutable after build; rebuilds produce a fresh instance and atomic-swap onto `IndexStatistics`.

### `MostCommonValues` (MCV)

[`Querying/internals/MostCommonValues.cs`](../../src/Typhon.Engine/Querying/internals/MostCommonValues.cs)

Top-100 values by count. Entries are sorted by value (not by count) for **O(log K) binary search** on equality probes. `TotalEntities` and `RemainingEntries` let the estimator interpolate counts for values not in the top-K. ~1.8 KB per field. Immutable after build.

### `Histogram`

[`Querying/internals/Histogram.cs`](../../src/Typhon.Engine/Querying/internals/Histogram.cs)

Equi-width: **100 buckets** spanning `[MinValue, MaxValue]`. ~1.6 KB per field. `BucketCount[i]` is the number of entries falling into bucket *i*. O(1) bucket lookup. Handles ranges that span the signed-long boundary (e.g., order-preserving float encoding) via unsigned subtraction.

### `AdvancedSelectivityEstimator`

[`Querying/internals/AdvancedSelectivityEstimator.cs`](../../src/Typhon.Engine/Querying/internals/AdvancedSelectivityEstimator.cs)

Singleton (`AdvancedSelectivityEstimator.Instance`). The priority chain:

| Op | Primary | Fallback |
|---|---|---|
| `==` | MCV exact lookup (O(log K)) | B+Tree point seek (O(log N)) |
| `!=` | `total − Equal(threshold)` | same |
| `<`, `<=`, `>`, `>=` | Histogram range estimation | uniform distribution over `[min, max]` |

Floats/doubles skip the ±1 boundary adjustment for `>` / `<` (bit-level ±1 can cross NaN boundaries); the approximation error is at most 1 entity. Replaces the older `BasicSelectivityEstimator` and the removed `HistogramSelectivityEstimator`. When no stats exist, it degrades gracefully to a B+Tree point-seek (equality) or a uniform-distribution guess (range).

---

## 7. Pending-spawn visibility

A transaction can `Spawn` an entity and then immediately query — the query must see its own uncommitted writes. But pending spawns are held on `Transaction.PendingSpawns` (a list); they have no entry in the `EntityMap` yet, and crucially **no entry in any secondary index**, because indexes are populated by the commit pipeline.

`EcsQuery` handles this in two places:

- **Targeted scan** (`WhereField` path): after the index-driven scan, `ExecuteTargeted` calls `CollectPendingSpawnsWithFieldFilter(result)` ([`EcsQuery.cs:1983`](../../src/Typhon.Engine/Ecs/public/EcsQuery.cs)). For each entry in `tx.PendingSpawns`: skip if in `PendingDestroys`, skip if not in archetype mask, then evaluate the **compiled predicate** (`_pendingSpawnFieldFilter`) via `tx.Open(id).TryRead<T>(out value) && predicate(value)`. The compiled `Func<T,bool>` is deferred — it's only `.Compile()`d on first invocation (Expression.Compile costs ~100+ µs) and cached in a closure.
- **Broad scan** (opaque `Where<T>(Func<T,bool>)`): pending spawns are reached by `CollectMatching`, which enumerates archetype `EntityMap`s — but `tx.Open(id)` resolves to the pending-spawn data, so opaque `Where` filters work transparently for pending entities.

`PendingDestroys` is checked first in both paths — an entity that was spawned and then destroyed in the same transaction is invisible to its own queries.

---

## 8. Subscriptions — pushing views to external clients

**Code:** [`src/Typhon.Engine/Subscriptions/`](../../src/Typhon.Engine/Subscriptions/)

Subscriptions are how Typhon ships view state to external clients (game clients, web dashboards, observer processes). The subscription layer reuses the same `ViewBase` infrastructure — a "published" view is just an `IView` with extra subscriber tracking.

### `PublishedView` and `PublishedViewRegistry`

[`Subscriptions/public/PublishedView.cs`](../../src/Typhon.Engine/Subscriptions/public/PublishedView.cs), [`Subscriptions/public/PublishedViewRegistry.cs`](../../src/Typhon.Engine/Subscriptions/public/PublishedViewRegistry.cs)

Two flavours:

- **Shared** — one `ViewBase` instance for all subscribers; the delta is computed once and the serialized payload is memcpy'd to each client's send buffer.
- **Per-client** — a `Func<ClientContext, ViewBase>` factory creates one View per subscriber (e.g., player-specific filtering).

`PublishedViewRegistry` is the published-view catalog. Lookup by name (`Dictionary<string, PublishedView>`) or by `PublishedId` (`Dictionary<ushort, PublishedView>`). Iteration during the Output phase uses a copy-on-write snapshot. A `ViewBase` can only be published once and can't be both a system input and a published view (asserted at registration).

### `ClientConnection` and the I/O pipeline

[`Subscriptions/internals/ClientConnection.cs`](../../src/Typhon.Engine/Subscriptions/internals/ClientConnection.cs)

One per connected TCP client. Holds:

- `ClientContext` — public connection identity (connection ID + user data).
- An SPSC `SendBuffer` — Output phase writes, I/O thread reads.
- `Dictionary<ushort, ViewSubscriptionState> ViewStates` — per-View incremental sync state.
- `HashSet<PublishedView> ActiveSubscriptions` — the current subscription set.
- `_pendingSubscriptions` — atomic-swap slot for "I want to change my subscription set" requests from game systems.

`SetSubscriptions(params PublishedView[])` is the client-API entry point — it `Interlocked.Exchange`s the pending set; the next tick's Output phase applies it.

### `TcpSubscriptionServer` and `SubscriptionOutputPhase`

[`TcpSubscriptionServer.cs`](../../src/Typhon.Engine/Subscriptions/internals/TcpSubscriptionServer.cs), [`SubscriptionOutputPhase.cs`](../../src/Typhon.Engine/Subscriptions/internals/SubscriptionOutputPhase.cs)

The TCP listener spawns one accept loop and per-connection read tasks. The **Output phase** runs once per scheduler tick (after all systems complete) and walks each `ClientConnection`:

1. Apply any pending subscription set changes (compute Subscribed/Unsubscribed events).
2. For each `ActiveSubscriptions`: incrementally serialize the View's `ViewDelta` (or do a full snapshot via `EntitySnapshotReader` if the client is new or the view overflowed).
3. Write the serialized payload into the client's `SendBuffer`.

`SubscriptionServerOptions` defaults: TCP port 9000, send buffer 256 KB, backpressure warning at 75% fill, sync batch size 200, published-view buffer capacity 8192.

This part of the engine is the smallest of the subsystems documented in this series — it's a thin shell on top of the view system. The interesting work happens in `ViewDeltaRingBuffer`, `ViewRegistry`, and the index maintainer that fills the ring buffers; subscriptions just consume the deltas.

---

## See also

- [01-foundation](01-foundation.md) — `HashMap<T>` (POH allocation, used as `ViewBase._entityIds`), `EpochManager` / `EpochGuard` (pinning during scans)
- [03-indexing](03-indexing.md) — B+Tree iterators (`EnumerateRange`, `EnumerateRangeMultiple`) that the executor streams from
- [06-ecs](06-ecs.md) — where `EcsQuery` and `EcsView` live, `EntityRef` enumeration, archetype masks, `Transaction.Open` / `TryRead`
- [07-spatial](07-spatial.md) — how `WhereInAABB` / `WhereNearby` / `WhereRay` translate to R-Tree queries
- [12-observability](12-observability.md) — `BeginQueryPlan` / `BeginQueryExecuteIndexScan` / `BeginQueryExecuteIterate` / `BeginQueryExecuteFilter` / `BeginQueryExecutePagination` typed event kinds; `QueryDefinitionDescribe` for caller attribution
