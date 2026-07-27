# Pointer → Span Migration Map

**Branch:** `poc/span-t` · **Baseline:** `00-baseline` @ `6548334` (Ryzen 9 7950X, .NET 10.0.10, RELEASE)
**Goal (reframed 2026-07-20):** replace raw-pointer usage/arithmetic in `Typhon.Engine` with **lifetime-checked managed references** — `ref T` / `ref T` fields (legal in `ref struct`) / `Unsafe.Add(ref…)` / `Vector.LoadUnsafe(ref…)` / `MemoryMarshal.*` / `Span<T>`. Not Span-only. *Safer, no perf loss, no added complexity.* **`ref`-based conversions emit identical machine code to pointers** → coverage (~80%+ target) is bounded by refactoring effort, NOT perf. Perf gate = **JitDisasm** (identical codegen / no new bounds check) + quiesced z1d.metal for end-to-end.

> **Live tracker.** Updated after every concluded file. Leave it open. Numbers below refresh on each change.

## Progress

## 🔄 REBASED ONTO main (2026-07-22)
`✅ poc/span-t fast-forwarded 6548334 → main dd17a17 (7 commits) · cascade re-applied as working-tree diff (85 files, uncommitted) · 2 trivial using-block conflicts resolved (BTreeVsHashMapBenchmarks, RawValueHashMapScaleRepro — both: keep System.Runtime.CompilerServices, drop main's dead usings) · 0 engine conflicts · build 0 errors · suite 4019/1-flaky/51-skip (the 1 = pre-existing rotating parallel flaky in untouched RuntimeScheduleTests, passes in isolation — NOT the rebase).`
_Main's 7 commits (StorageMode-per-revision enforcement, page-cache seqlock fix, telemetry catalog/generators, SWG samples, CLI, CI/AWS gate) were orthogonal to the pointer→ref work: 8/85 file overlap, 0 renames/deletes, all overlapping hunks disjoint by line-range. The base commit for the historical A/B (6548334) is now superseded but the measurement stands (it compared engine-diff only)._

**Stress test (`RawValueHashMapScaleRepro.Insert_500K…`, actually 5M inserts, `[Explicit]`, Release):** fails with
`PageCacheBackpressureTimeout` @5000ms (dirty ~27.5K / epoch-protected 32768) in `PagedMMF.AllocateMemoryPageCore`
— **but reproduces BIT-FOR-BIT on clean main** (cascade stashed): same dirty count, same 32768, same stack, ~22s.
→ **Pre-existing on main, NOT the rebase/cascade.** The cascade's `Byte&` ref-API is in the call chain but the
timeout is in the unchanged zero-copy page-cache core (KEEP-annotated); toggling the cascade changes nothing. The
fixture is `[Explicit]` (never runs in CI/merge gate); the backpressure regression, if any, traces to main's own
`7b17bd6` page-cache rework. Rebase fully validated under maximal page-cache stress.

## ✅ CASCADE COMPLETE (2026-07-20)
`✅ 59 files converted + 14 KEEP-annotated (73 files, net −69 lines) · suite 4029/0 (final independent verify) · perf-neutral by construction · every convertible pointer converted, every retained pointer documented`
_All `byte*` helper signatures cascaded to `ref`/`Span` (SpatialNodeHelper, EntityRecordAccessor, EntityMap+callback interfaces, BTree index API, ZoneMapArray, leaf helpers). The **828 residual pointer sites** are the irreducible zero-copy core — raw variable-size hashmaps, SIMD gather, PagedMMF/ChunkAccessor page-cache roots, pinned WAL buffers, allocator intrusive-lists, BTree SIMD — each carrying a `// KEEP(ptr)` rationale (63 blocks). No perf loss (`ref`/`Unsafe.Add` = identical codegen; no `Span[i]` in hot loops); pending z1d.metal end-to-end confirmation._
_See **✅ Converted files** (below, git-sourced) for the authoritative live list. Two passes: **Pass 1** = Span-only mechanical (log directly below). **Pass 2** = ref-centric (Milestones + R-sig plan below) — the reframe that raised the ceiling from ~10% to ~80%. No perf loss anywhere: BTree Control JitDisasm-proven; ref/`Unsafe.Add` = identical codegen; no `Span[i]` in hot loops._

### Conversion log — Pass 1 (Span-only sweeps; kept as history)
> The "piecemeal call-site conversion is blocked / KEEP" conclusion in these entries was the **Span-only** verdict. It was **superseded by the reframe**: with `ref`/`ref` fields/`Unsafe.Add`/helper-signature cascades, those "load-bearing" sites ARE convertible (perf-neutral). See the Reframed plan + Pass-2 milestones above.
- **COLD sweep** — 4029/0 suite green. ✅ TyphonRuntime (9 sites), ComponentRevisionManager, DeltaBuilder, EntitySnapshotReader (B-overlay: `*(T*)ptr`→`MemoryMarshal.Read`/`ref AsRef` over `GetChunkAsSpan`). 🟦 String64, PartitionEntityView, ClusterRangeEntityView (pointer fields / interop). ⏭️ 12 already-safe.
- **WARM batch 1** (Spatial-light + Transactions + Storage-warm, 27 files) — build clean. ✅ SpatialRTree.Query (stackalloc→Span), SpatialBackPointer (3 overlays→`GetChunk<T>`). 🟦 9 (SpatialMaintainer/AabbClusterEnumerator/Transaction.ECS/Transaction/ConcurrencyConflictSolver/UowRegistry/PageAccessor/StringTableSegment/BootstrapDictionary — `byte*` fed to `byte*`-only helpers, pointer fields, or dense serialization cursors). ⏭️ 16 already-safe.
- **🔑 Structural finding:** most warm/hot pointers are load-bearing around `byte*`-based **helper APIs** (`SpatialNodeHelper`, `EntityRecordAccessor`, `ChunkAccessor.GetChunkAddress`). The high-value conversions are the *helper signatures* (`byte*`→`Span`/`ref`) which cascade to call sites — hot, JitDisasm-gated, done manually. Piecemeal call-site conversion is correctly blocked (KEEP).
- **WARM batch 2** (Ecs/Querying/Durability/Memory/Schema, 45 files) — build clean. ✅ WalSegmentManager (`fixed`+`*(Hdr*)dst=h`→`MemoryMarshal.Write`), WalSegmentHeader (2: self-pin `fixed(&this)`→`MemoryMarshal.AsBytes(CreateReadOnlySpan(in this,1))`). 🟦 ~8 annotated (byte* → byte*-only EntityMap/EntityRecordAccessor/BTree.TryGet). ⏭️ rest already-safe. **Confirms:** call-site conversion blocked by `byte*` helper signatures.
- **HOT (mechanical, JitDisasm-proven pattern):** ✅ L32BTree, L64BTree, String64BTree Control accessors → bit-arithmetic (identical to L16 spike; no bounds check by construction). All 4 chunk types now pointer-free on Count/Start/ContentionHint/StateFlags. _Suite validating (BTreeMicro exercises L64)._
- **HOT sweep** (39 files: HashMap family, Ecs hot, Indexing, Concurrency, Storage, heavy-warm) — build clean, suite 4029/0. ✅ ManagedPagedMMF (2 checkpoint-path overlays → `ref MemoryMarshal.AsRef<PageBaseHeader>`). Everything else KEEP: `byte*`→`byte*`-only helpers, pointer fields, SIMD, dense base+offset arithmetic in hot loops, OR **already** using the safe `Unsafe.As`/`Unsafe.AsRef` idiom (census over-counted "unsafe"). Confirms the mechanical surface is exhausted.

## Reframed plan — `ref`-centric (ACTIVE)

Pass 1 (Span-only, done): ~13 files converted, suite 4029/0. **Under-scoped** — see reframe below.

**Convert-bucket menu (expanded):** all perf-identical to pointers except Span-with-live-bounds-check:
| # | Pattern | Target |
|---|---------|--------|
| **R-field** | `byte* _f;` in a **ref struct** | `ref byte _f;` (set `=ref Unsafe.AsRef<byte>(p)`; null→`Unsafe.IsNullRef`; arith→`Unsafe.Add(ref _f,i)`) |
| **R-arith** | `*(T*)(base+off)`, `ptr[i]` | `Unsafe.Add(ref b,off)` + `Unsafe.ReadUnaligned<T>`/`Unsafe.As<byte,T>` |
| **R-sig** | helper method `byte*` param | `ref byte`/`ReadOnlySpan<byte>`/`ref T` param (callers pass ref/span) |
| **R-simd** | `Vector###.Load(ptr)` | `Vector###.LoadUnsafe(ref b, n)` |
| A/B/E | (as before) stackalloc→Span, overlay→AsRef, reinterp→Unsafe.As | |

**Tiers (cost = REFACTOR; perf ≈ 0, JitDisasm-proven):**
- **Tier A** mechanical/low: R-simd (14), R-arith, ref-struct fields (EntityRef/ClusterRef/AabbClusterEnumerator/RAC guards), stackalloc/overlay/reinterp leftovers.
- **Tier B** helper-signature cascade (the ~80% unlock): **~185 pointer-param methods** (SpatialNodeHelper, EntityRecordAccessor, EntityMap, BTree.TryGet(byte*), ChunkAccessor.GetChunkAddress→byte*) → ref/span sigs; callers cascade. One helper-cluster at a time, JitDisasm + suite each.
- **Tier C** genuine KEEP (~10-20%, documented as deliberate): class-field base ptrs (PagedMMF `_memPagesAddr` — use-sites still return ref/span); true SIMD gather (`Avx2.Gather(int*)`, 18); hard interop (subset of 83 Marshal/PtrToString — span overloads where they exist); GCHandle/pinning.

**Coverage target ~80%+ at ~0 perf cost.** Execution order: EntityRef/ClusterRef pilot (prove ref-field pattern) → Tier A sweep → Tier B per-helper cascade.

| Milestone | State |
|-----------|-------|
| Census + strategy | ✅ done |
| A/B compare harness | ✅ `benchmark/.local/abcompare.py` |
| **Spike (L16BTree)** | ✅ **methodology validated — see below** |
| COLD sweep (tests-only) | ✅ 4 converted, 3 keep, 12 skip — suite green |
| WARM sweep | ✅ 4 converted (b1+b2), rest load-bearing KEEP — suite green |
| HOT sweep (JitDisasm-gated) | ✅ BTree Control ×4 (JitDisasm-clean) + ManagedPagedMMF; rest KEEP — suite green |
| **Span-only mechanical surface** | ✅ EXHAUSTED — 140 files classified, 4029/0 suite green |
| **Ref-centric pass** | 🔄 ACTIVE (reframed target — see below) |
| — ref-field pilot | ✅ ResourceAccessControl guards `int*`→`ref int` (151 tests green, GC-dangle bug fixed, `unsafe` dropped) |
| — ref-struct field conversions | ✅ 8 types → `ref T` fields, **4029/0** (EntityRef, ClusterRef, AabbClusterEnumerator, PageAccessor, HashMap.PartitionEnumerator, PagedHashMap.ConcurrentEnumerator, VariableSizedBufferAccessor, RAC guards) |
| — Tier B R-accessor migration | ✅ 11 sites (ArchetypeClusterState occupancy ×10 → `GetChunk<ulong>`, PagedHashMap ×1), 4029/0 |
| — Tier B R-sig (helper signatures) | ✅ **COMPLETE** — ZoneMapArray · leaf helpers · SpatialNodeHelper (32+13 sigs) · EntityRecordAccessor+Cluster (14) · EntityMap+`IEntryAction`/`IRawValueUpdater`/`IEntryPredicate` (7 sigs, 13 impl, 17 callers) · BTreeBase/BTree index API (8 sigs, 13 callers) · BTree key-reinterprets (15) · long-tail (11 files) |
| — Genuine-KEEP documentation | ✅ file/type-level `// KEEP(ptr)` rationale on the 828-site zero-copy core (raw-value hashmaps, SIMD, page-cache roots, pinned buffers, allocators, BTree SIMD) |
| **Final independent full-suite** | ✅ **4029/0** — cascade complete |

**R-sig proof — `ZoneMapArray.cs` is now entirely pointer-free** (`Recompute`/`Widen`/`ReadFieldAsOrderedLong` `byte*`→`ref byte`; 4 callers updated in Transaction.ECS/Transaction/ClusterMigration; class `unsafe` dropped). Demonstrates the signature-cascade end-to-end. **Remaining R-sig helpers to convert (staged, one PR each, z1d.metal-validated):** HashMap-internal `GetHeader`/`KeysPtr`/`ValueAt`; `SpatialNodeHelper.*` (66 sites); `EntityRecordAccessor.*`; `KeyBytes8.FromPointer`; `StageClusterCommitWrite`; `index.Add(byte*)`/`BTree.TryGet(byte*)`; `WriteSpatialAabb2F` + `SpatialMaintainer.ReadAndValidate` (removes the last EntityRef/ClusterRef/Aabb bridges); ComponentTable/ClusterMigration/IndexMaintainer/StatisticsRebuilder/SchemaEvolutionEngine bases.

**🔑 The remaining coverage is one thing: `byte*` HELPER SIGNATURES.** The KEPT hot sites almost all share a root — a base ptr reused across offsets that ALSO feeds a `byte*`-signature helper (`ZoneMapArray.ReadFieldAsOrderedLong`/`Recompute`, HashMap `GetHeader`/`KeysPtr`/`ValueAt`, `EntityRecordAccessor.*`, `KeyBytes8.FromPointer`, `index.Add(byte*)`, `SpatialNodeHelper.*`, `StageClusterCommitWrite`). Convert each signature `byte*`→`ref byte`/`ReadOnlySpan<byte>`/`ref T` → all its callers shed pointers at once + the 8 bridges dissolve. Mechanical-but-broad, perf-neutral (`Unsafe.Add`), one helper-cluster per PR (hot → z1d.metal per-helper).

### Ref-centric conversion log
- **RAC guards** `int*`→`ref int` (+`[UnscopedRef]` factories) — fixed latent GC-dangle; `unsafe` dropped; 151 tests green.
- **8 ref-struct pointer FIELDS → `ref T`** (the core hot entity accessors EntityRef/ClusterRef etc.). No `[UnscopedRef]` needed (refs from `Unsafe.AsRef`, unscoped). **8 `byte*` bridges** `(byte*)Unsafe.AsPointer(ref…)` at still-`byte*` leaf-helper boundaries (StageClusterCommitWrite, KeyBytes8.FromPointer, WriteSpatialAabb2F, SpatialMaintainer.ReadAndValidate, NativeMemory.Free, PageAccessor.Address, VSB walk) — marked `// TODO(cascade)`, removable when those leaves convert. **4029/0** (1 transient flake, reran green). Perf-neutral by construction (`Unsafe.Add`/`Unsafe.As`, no `Span[i]`).

## ⚠️ Benchmark Methodology (revised after spike)

**Finding:** this dev machine has **±5% baseline / up to ±30% spike run-to-run BDN noise** under active use
(thermal/boost drift + core contention; all upward). Two runs of *identical* code differed +28.8%. A ≤3%
gate is **unenforceable run-to-run** here. (Proof: `BTreeMicroBenchmarks` uses `long` keys = L64; the L16
spike change isn't even exercised, yet showed +8–39% deltas — pure noise.)

**Revised gate (what actually proves "no perf loss"):**
1. **Correctness** — unit tests, every file (non-negotiable).
2. **Per-change perf risk = did a Span add a bounds check?** → **JitDisasm** (`DOTNET_TieredCompilation=0
   DOTNET_JitDisasm="*Method*" dotnet run probe.cs`). Deterministic, zero noise. Look for absence of
   `CORINFO_HELP_RNGCHKFAIL`/bounds branch in the hot method. This is the real gate for A/E/B/C conversions.
3. **End-to-end BDN** — kept as the tool, but run **only at phase boundaries, quiesced** (no concurrent
   builds, brief cool-down), using **min-of-N interleaved** (min = least-contended ≈ true speed) vs baseline.
   Effective local resolution ~5%; the frozen `00-baseline` final compare runs quiesced.

**Spike result (L16BTree Control accessors, byte-poke `((byte*)c)[n]` → bit-arithmetic):** 71/71 BTree tests
green; JitDisasm clean (no bounds check, getters inline to register ops, values verified). Neutral by
construction (arithmetic, no memory-layout change). ✅ accepted.

**✅ End-to-end BDN validation (2026-07-20, ECS + EndToEnd categories) — NO REGRESSION.** Method that
defeated the ±30% drift: **same-session A/B** — baseline engine (`git stash` → `6548334`) vs cascade engine
(`stash pop`), each *built and run back-to-back* so cross-run thermal drift cancels; measured classes
(`EcsBenchmarks`, `EpochBenchmarks`) byte-identical vs baseline (only the engine diff varies). Results
(22 ECS/EndToEnd cases): **11 improved, 5 flat, 6 up >3%** — of the 6, four ≤+11% and two (`CreateReadCommit`)
were single-run noise. **`EcsQuery` is a consistent real win −17…−33%** (query-path `ref`/`Unsafe.Add`
improves codegen). The **+34…+56% CascadeDelete/SpawnBatch alarm from a naive cold-baseline-vs-now compare was
100% thermal drift** (baseline captured cold hours earlier) — reproduced and eliminated by the A/B.
**Confirmation (EndToEnd, 3×/side, min-of-3):** `CreateReadCommit` +20/+37% → **−1.4% / −7.6%** (both sides
scatter 4.1–6.5µs run-to-run; single A/B cherry-picked a fast baseline); `BulkUpdate(1000)` +9.3%-by-min →
**~0% by median** (baseline threw a lucky 732µs outlier, range 732–920). **Verdict: the commit path — the one
hot loop holding converted code — is even-or-better; ECS/EndToEnd perf-neutral.** Scripts + raw JSON in
`benchmark/.local/{runs/ab-base,ab-cand,txn-{base,cand}-{1,2,3}}`. z1d.metal still the authority for the
public number, but the local A/B is conclusive for this question.

## Legend

**Status:** ⬜ pending · 🔄 wip · ✅ converted · 🟦 KEEP (documented reason) · ⏭️ skipped (no real pointer) · ⚠️ perf-blocked (reverted → pointer kept)

**🔥 Hotness:** 🔥🔥🔥 inner per-op loop · 🔥🔥 per-tx/query · 🔥 per-commit/setup · ❄️ cold/init/rare

**Convert buckets** (raw pointer → lifetime-checked managed reference — *not Span-only*; supersedes the original Span-only A–E). **All perf ≈ 0** (`Unsafe.Add`/`ref` = identical codegen; the only bounds-check risk is `Span[i]` in a hot loop, which we avoid by using `Unsafe.Add`):
| # | Pattern | Target |
|---|---------|--------|
| **E** | `T* p = stackalloc T[n]` | `Span<T> p = stackalloc T[n]` |
| **B-overlay** | `((H*)addr)->field` | `ref MemoryMarshal.AsRef<H>(span)` / `accessor.GetChunk<H>(id)` |
| **B-reinterp** | `*(T2*)&local` | `Unsafe.As<T1,T2>(ref x)` / `Unsafe.BitCast` |
| **A** | `fixed(T* p=buf)`+`new Span` over a **fixed-size buffer** | `MemoryMarshal.CreateSpan(ref buf[0], n)` (drops the pin) |
| **R-field** | `T* _f;` **in a `ref struct`** | `ref T _f;` (set `=ref Unsafe.AsRef<T>(p)`; null→`Unsafe.IsNullRef`; arith→`Unsafe.Add`; **`[UnscopedRef]` on an escaping factory** when the owner is stable storage) |
| **R-arith** | dense `*(T*)(base+off)`, `ptr[i]` on a local base | `Unsafe.As<byte,T>(ref Unsafe.Add(ref b, off))` |
| **R-sig** | helper method `byte*`/`T*` **param** | `ref byte`/`ReadOnlySpan<byte>`/`ref T` param → all callers shed pointers |
| **R-simd** | `Vector###.Load(ptr)` | `Vector###.LoadUnsafe(ref b, n)` |
| **🟦 KEEP** | genuinely not convertible | (1) pointer field in a **non-ref-struct** class/struct; (2) true SIMD **gather** (`Avx2.Gather` mandates `T*`); (3) hard interop (`PtrToStringUTF8`); (4) `GCHandle`/pinning roots (`PagedMMF._memPagesAddr`); (5) raw variable-size `byte*`+`Unsafe.CopyBlock` (RawValueHashMap by design) |

**Columns** `arrow/fixed/ptr/salloc/unsafe` are raw census counts (ptr = casts+decls). They don't show every indicator (`Unsafe.*`, `MemoryMarshal`, `AsPointer` also scored) — an all-zero row still earned inclusion via those.

---

## ✅ Converted files — live status (source of truth = `git diff src/`; 41 files, suite 4029/0)

**Ref-centric pass:**
- **Ref-struct pointer fields → `ref T`:** `EntityRef`, `ClusterRef`, `AabbClusterEnumerator`, `PageAccessor`, `VariableSizedBufferSegment`, `HashMap` (PartitionEnumerator), `PagedHashMap` (ConcurrentEnumerator), `ResourceAccessControl` (guards `int*`→`ref int`, +latent GC-dangle fix).
- **R-sig (helper signatures → ref/span):**
  - `ZoneMapArray` (100% pointer-free); leaf helpers `StageClusterCommitWrite`(+`StageCommitWriteCore`), `KeyBytes8.FromPointer`→`FromRef` (16 callers), `WriteSpatialAabb2F`, `SpatialMaintainer.ReadAndValidateBoundsFromPtr` — **dissolved 5 of 8 bridges** (3 remain: `PageAccessor.Address` escape hatch, `NativeMemory.Free`, VSB walk).
  - **`SpatialNodeHelper`** — 32 method sigs + 13 private helpers `byte*`→`ref byte`, class `unsafe` dropped; **whole spatial node layer pointer-free**; ~241 caller wraps across 8 SpatialRTree files cleaned to `ref byte` locals (no wrap noise).
  - **`EntityRecordAccessor`** + `ClusterEntityRecordAccessor` — 14 sigs `byte*`→`ref byte`, both classes `unsafe` dropped; 13 caller files (~59 calls, 14 base-locals→ref). Remaining wraps only at genuine `byte*`-interface boundaries (`EntityMap.TryGet`, `IEntryAction`, `IRawValueUpdater`).
- **R-accessor (`*(T*)GetChunkAddress`→`GetChunk<T>`):** `ArchetypeClusterState` (occupancy ×10), `PagedHashMap`.
- 8 `// TODO(cascade)` bridges left at still-`byte*` leaf boundaries (dissolve when those leaves R-sig-cascade).

**BTree Control accessors → bit-arithmetic (JitDisasm-clean):** `L16/L32/L64/String64BTree`.

**Span-only pass (overlay→`MemoryMarshal`/`GetChunk`, stackalloc→Span, serialize→`MemoryMarshal.Write`):** `TyphonRuntime`, `ComponentRevisionManager`, `DeltaBuilder`, `EntitySnapshotReader`, `SpatialBackPointer`, `SpatialRTree.Query`, `WalSegmentHeader`, `WalSegmentManager`, `ManagedPagedMMF`, `RecoveryApplier`, `UowRegistry`.

**KEEP-annotation-only (12 files):** `// KEEP(ptr)` comments documenting deliberate pointers — String64, PartitionEntityView, ClusterRangeEntityView, DatabaseEngine, WalWriter, SpatialMaintainer, StringTableSegment, ChainedBlockAllocatorBase, Basic/AdvancedSelectivityEstimator, ConcurrencyConflictSolver, Transaction.

---

> **Per-file live status.** The `St` column is **regenerated from `git diff`** (`benchmark/.local/livesync-tracker.py`) — re-run
> after each batch to keep it live. ✅ converted · 🟦 KEEP (deliberate pointer) · ⏭️ already-safe/nothing-to-do ·
> ⬜ not yet converted (R-sig cascade target). The `arrow/fixed/ptr/salloc/unsafe` columns are the **original
> pre-conversion census counts** (an inventory metric, not re-counted).

<!-- TABLES:BEGIN -->
### 🔥🔥🔥 HOT — live status
| St | Area | File | 🔥 | arrow | fixed | ptr | salloc | unsafe | Bench filter | Convert bucket |
|----|------|------|----|-------|-------|-----|--------|--------|--------------|----------------|
| ✅ | Foundation | `Collections/internals/RawValueHashMap.cs` | 🔥🔥🔥 | 0 | 0 | 71 | 4 | 2 | `*HashMap*` | ✅ converted — see **Converted files** list |
| ✅ | Ecs | `internals/ArchetypeClusterState.cs` | 🔥🔥🔥 | 1 | 2 | 49 | 2 | 1 | `*Ecs*\|*SpawnBatch*\|*Component*` | ✅ converted — see **Converted files** list |
| ✅ | Foundation | `Collections/internals/PagedHashMap.cs` | 🔥🔥🔥 | 4 | 0 | 43 | 6 | 1 | `*HashMap*` | ✅ converted — see **Converted files** list |
| 🟦 | Ecs | `internals/SimdPredicateEvaluator.cs` | 🔥🔥🔥 | 0 | 0 | 39 | 0 | 1 | `*EcsQuery*\|*QueryView*` | 🟦 KEEP — deliberate pointer (annotated) |
| ✅ | Indexing | `internals/L16BTree.cs` | 🔥🔥🔥 | 0 | 11 | 29 | 0 | 2 | `*BTree*` | ✅ converted — see **Converted files** list |
| ✅ | Indexing | `internals/L32BTree.cs` | 🔥🔥🔥 | 0 | 11 | 29 | 0 | 2 | `*BTree*` | ✅ converted — see **Converted files** list |
| ✅ | Indexing | `internals/String64BTree.cs` | 🔥🔥🔥 | 0 | 16 | 24 | 0 | 2 | `*BTree*` | ✅ converted — see **Converted files** list |
| 🟦 | Foundation | `Collections/internals/HashMapKV.cs` | 🔥🔥🔥 | 0 | 0 | 33 | 0 | 1 | `*HashMap*` | 🟦 KEEP — deliberate pointer (annotated) |
| 🟦 | Foundation | `Collections/internals/ConcurrentHashMapKV.cs` | 🔥🔥🔥 | 0 | 0 | 32 | 0 | 1 | `*HashMap*` | 🟦 KEEP — deliberate pointer (annotated) |
| ✅ | Indexing | `internals/L64BTree.cs` | 🔥🔥🔥 | 0 | 11 | 25 | 0 | 2 | `*BTree*` | ✅ converted — see **Converted files** list |
| ✅ | Foundation | `Collections/internals/HashMap.cs` | 🔥🔥🔥 | 0 | 0 | 28 | 0 | 1 | `*HashMap*` | ✅ converted — see **Converted files** list |
| 🟦 | Storage | `internals/PagedMMF.cs` | 🔥🔥🔥 | 14 | 0 | 11 | 0 | 14 | `*ChunkAccessor*\|*PagedMMF*` | 🟦 KEEP — deliberate pointer (annotated) |
| ✅ | Ecs | `public/EntityRecord.cs` | 🔥🔥🔥 | 0 | 1 | 22 | 0 | 3 | `*Ecs*\|*SpawnBatch*\|*Component*` | ✅ converted — see **Converted files** list |
| ✅ | Ecs | `public/EntityRef.cs` | 🔥🔥🔥 | 0 | 1 | 19 | 0 | 1 | `*Ecs*\|*SpawnBatch*\|*Component*` | ✅ converted — see **Converted files** list |
| 🟦 | Foundation | `Collections/internals/ConcurrentHashMap.cs` | 🔥🔥🔥 | 0 | 0 | 20 | 0 | 1 | `*HashMap*` | 🟦 KEEP — deliberate pointer (annotated) |
| ✅ | Storage | `internals/VariableSizedBufferSegment.cs` | 🔥🔥🔥 | 9 | 0 | 4 | 0 | 17 | `(tests only)` | ✅ converted — see **Converted files** list |
| ✅ | Indexing | `internals/BTree.cs` | 🔥🔥🔥 | 0 | 0 | 11 | 0 | 15 | `*BTree*` | ✅ converted — see **Converted files** list |
| ✅ | Storage | `internals/ManagedPagedMMF.cs` | 🔥🔥🔥 | 2 | 4 | 10 | 6 | 8 | `*ChunkAccessor*\|*PagedMMF*` | ✅ converted — see **Converted files** list |
| ✅ | Storage | `internals/ChunkAccessor.cs` | 🔥🔥🔥 | 0 | 5 | 13 | 0 | 1 | `*ChunkAccessor*\|*PagedMMF*` | ✅ converted — see **Converted files** list |
| ✅ | Ecs | `internals/ZoneMapArray.cs` | 🔥🔥🔥 | 0 | 0 | 17 | 0 | 1 | `*Ecs*\|*SpawnBatch*\|*Component*` | ✅ converted — see **Converted files** list |
| ✅ | Ecs | `public/EntityAccessor.ECS.cs` | 🔥🔥🔥 | 0 | 0 | 14 | 1 | 1 | `*Ecs*\|*SpawnBatch*\|*Component*` | ✅ converted — see **Converted files** list |
| ✅ | Ecs | `public/ClusterRef.cs` | 🔥🔥🔥 | 0 | 0 | 12 | 0 | 2 | `(tests only)` | ✅ converted — see **Converted files** list |
| ✅ | Indexing | `internals/BTreeBase.cs` | 🔥🔥🔥 | 0 | 0 | 10 | 0 | 9 | `*BTree*` | ✅ converted — see **Converted files** list |
| ✅ | Foundation | `Concurrency/internals/ResourceAccessControl.cs` | 🔥🔥🔥 | 0 | 2 | 6 | 0 | 4 | `*AccessControl*` | ✅ converted — see **Converted files** list |
| ⬜ | Foundation | `Collections/internals/ConcurrentBitmapL3All.cs` | 🔥🔥🔥 | 0 | 0 | 5 | 0 | 1 | `*ConcurrentBitmap*` | review — R-sig cascade target |
| ⬜ | Ecs | `internals/KWayMergeHelper.cs` | 🔥🔥🔥 | 0 | 0 | 2 | 0 | 1 | `(tests only)` | review — R-sig cascade target |
| ⬜ | Indexing | `internals/BTree.Remove.cs` | 🔥🔥🔥 | 0 | 0 | 0 | 0 | 0 | `*BTree*` | review — R-sig cascade target |
| ✅ | Ecs | `public/ComponentTable.cs` | 🔥🔥🔥 | 0 | 0 | 1 | 3 | 1 | `*ComponentTable*` | ✅ converted — see **Converted files** list |
| ✅ | Indexing | `internals/TemporalIndexQuery.cs` | 🔥🔥🔥 | 0 | 0 | 2 | 0 | 1 | `(tests only)` | ✅ converted — see **Converted files** list |
| ⬜ | Foundation | `Concurrency/public/WaitContext.cs` | 🔥🔥🔥 | 0 | 0 | 0 | 0 | 0 | `(tests only)` | review — R-sig cascade target |
| ⬜ | Indexing | `internals/VersionedIndexEntry.cs` | 🔥🔥🔥 | 0 | 0 | 1 | 0 | 1 | `(tests only)` | review — R-sig cascade target |
| ⬜ | Foundation | `Collections/internals/PagedHashMapBase.cs` | 🔥🔥🔥 | 0 | 0 | 0 | 0 | 1 | `*HashMap*` | review — R-sig cascade target |
| ⬜ | Foundation | `Collections/internals/PagedHashMapBasse.Structs.cs` | 🔥🔥🔥 | 0 | 0 | 0 | 0 | 3 | `*HashMap*` | review — R-sig cascade target |
| ⬜ | Foundation | `Concurrency/internals/AccessControl.LockData.cs` | 🔥🔥🔥 | 0 | 0 | 0 | 0 | 0 | `*AccessControl*` | review — R-sig cascade target |
| ⬜ | Indexing | `internals/OlcLatch.cs` | 🔥🔥🔥 | 1 | 0 | 0 | 0 | 0 | `(tests only)` | review — R-sig cascade target |
| ⬜ | Foundation | `Concurrency/internals/AccessControlSmall.cs` | 🔥🔥🔥 | 0 | 0 | 0 | 0 | 0 | `*AccessControl*` | review — R-sig cascade target |
| ⬜ | Ecs | `internals/DeferredCleanupManager.cs` | 🔥🔥🔥 | 0 | 0 | 0 | 1 | 0 | `(tests only)` | review — R-sig cascade target |
| ⬜ | Foundation | `Concurrency/internals/AccessControl.Telemetry.cs` | 🔥🔥🔥 | 0 | 0 | 0 | 0 | 0 | `*AccessControl*` | review — R-sig cascade target |
| ⬜ | Storage | `internals/ManagedPagedMMF.BitmapL3.cs` | 🔥🔥🔥 | 0 | 0 | 0 | 0 | 1 | `*ChunkAccessor*\|*PagedMMF*` | review — R-sig cascade target |

### 🔥🔥/🔥 WARM — live status
| St | Area | File | 🔥 | arrow | fixed | ptr | salloc | unsafe | Bench filter | Convert bucket |
|----|------|------|----|-------|-------|-----|--------|--------|--------------|----------------|
| ✅ | Spatial | `internals/SpatialNodeHelper.cs` | 🔥🔥 | 0 | 0 | 66 | 2 | 1 | `*Spatial*` | ✅ converted — see **Converted files** list |
| ✅ | Transactions | `public/Transaction.ECS.cs` | 🔥🔥 | 0 | 0 | 47 | 18 | 1 | `*Transaction*\|*Workload*` | ✅ converted — see **Converted files** list |
| ✅ | Ecs | `public/DatabaseEngine.ClusterMigration.cs` | 🔥🔥 | 0 | 0 | 25 | 1 | 6 | `(tests only)` | ✅ converted — see **Converted files** list |
| ✅ | Spatial | `internals/SpatialRTree.Query.cs` | 🔥🔥 | 0 | 5 | 19 | 10 | 1 | `*Spatial*` | ✅ converted — see **Converted files** list |
| ✅ | Transactions | `public/Transaction.cs` | 🔥🔥 | 0 | 0 | 20 | 6 | 1 | `*Transaction*\|*Workload*` | ✅ converted — see **Converted files** list |
| ✅ | Spatial | `internals/SpatialRTree.cs` | 🔥🔥 | 0 | 0 | 23 | 0 | 1 | `*Spatial*` | ✅ converted — see **Converted files** list |
| ✅ | Spatial | `internals/SpatialRTree.Split.cs` | 🔥🔥 | 0 | 0 | 16 | 15 | 1 | `*Spatial*` | ✅ converted — see **Converted files** list |
| ✅ | Ecs | `public/EcsQuery.cs` | 🔥🔥 | 0 | 0 | 17 | 8 | 1 | `*EcsQuery*\|*QueryView*` | ✅ converted — see **Converted files** list |
| ✅ | Transactions | `internals/IndexMaintainer.cs` | 🔥🔥 | 0 | 0 | 14 | 0 | 1 | `*Transaction*\|*Workload*` | ✅ converted — see **Converted files** list |
| ✅ | Spatial | `internals/SpatialGrid.cs` | 🔥🔥 | 0 | 0 | 14 | 0 | 1 | `*Spatial*` | ✅ converted — see **Converted files** list |
| ✅ | Ecs | `public/DatabaseEngine.cs` | 🔥🔥 | 0 | 0 | 6 | 4 | 11 | `(tests only)` | ✅ converted — see **Converted files** list |
| 🟦 | Transactions | `public/ConcurrencyConflictSolver.cs` | 🔥🔥 | 0 | 0 | 8 | 0 | 1 | `*Transaction*\|*Workload*` | 🟦 KEEP — deliberate pointer (annotated) |
| ⬜ | Ecs | `public/EcsView.cs` | 🔥🔥 | 0 | 0 | 4 | 0 | 2 | `(tests only)` | review — R-sig cascade target |
| ✅ | Spatial | `internals/SpatialBackPointer.cs` | 🔥🔥 | 4 | 0 | 4 | 0 | 1 | `*Spatial*` | ✅ converted — see **Converted files** list |
| ✅ | Spatial | `internals/SpatialMaintainer.cs` | 🔥🔥 | 0 | 0 | 6 | 5 | 1 | `*Spatial*` | ✅ converted — see **Converted files** list |
| ✅ | Spatial | `internals/TreeValidator.cs` | 🔥🔥 | 0 | 0 | 7 | 1 | 1 | `*Spatial*` | ✅ converted — see **Converted files** list |
| ✅ | Spatial | `internals/SpatialInterestSystem.cs` | 🔥🔥 | 0 | 0 | 5 | 3 | 1 | `*Spatial*` | ✅ converted — see **Converted files** list |
| ✅ | Spatial | `internals/SpatialRTree.Insert.cs` | 🔥🔥 | 0 | 0 | 6 | 0 | 1 | `*Spatial*` | ✅ converted — see **Converted files** list |
| ✅ | Spatial | `internals/SpatialRTree.Remove.cs` | 🔥🔥 | 0 | 0 | 6 | 0 | 1 | `*Spatial*` | ✅ converted — see **Converted files** list |
| ⬜ | Storage | `internals/LogicalSegment.cs` | 🔥🔥 | 0 | 0 | 3 | 2 | 5 | `(tests only)` | review — R-sig cascade target |
| ✅ | Storage | `public/PageAccessor.cs` | 🔥🔥 | 0 | 0 | 3 | 0 | 1 | `(tests only)` | ✅ converted — see **Converted files** list |
| 🟦 | Storage | `internals/StringTableSegment.cs` | 🔥🔥 | 0 | 1 | 1 | 2 | 3 | `(tests only)` | 🟦 KEEP — deliberate pointer (annotated) |
| ⬜ | Ecs | `public/DatabaseEngine.TickFence.cs` | 🔥🔥 | 0 | 0 | 3 | 3 | 2 | `(tests only)` | review — R-sig cascade target |
| ✅ | Spatial | `public/AabbClusterEnumerator.cs` | 🔥🔥 | 0 | 0 | 4 | 2 | 1 | `*Spatial*` | ✅ converted — see **Converted files** list |
| ✅ | Transactions | `internals/UowRegistry.cs` | 🔥🔥 | 0 | 0 | 3 | 0 | 1 | `*Transaction*\|*Workload*` | ✅ converted — see **Converted files** list |
| ⬜ | Ecs | `public/ComponentValue.cs` | 🔥🔥 | 0 | 0 | 1 | 0 | 1 | `(tests only)` | review — R-sig cascade target |
| ✅ | Spatial | `internals/SpatialRTree.BulkLoad.cs` | 🔥🔥 | 0 | 0 | 3 | 0 | 1 | `*Spatial*` | ✅ converted — see **Converted files** list |
| ⬜ | Storage | `internals/IPageStore.cs` | 🔥🔥 | 0 | 0 | 3 | 0 | 1 | `(tests only)` | review — R-sig cascade target |
| ⬜ | Storage | `internals/PersistentStore.cs` | 🔥🔥 | 0 | 0 | 3 | 0 | 1 | `(tests only)` | review — R-sig cascade target |
| ⬜ | Storage | `internals/TransientStore.cs` | 🔥🔥 | 0 | 0 | 3 | 0 | 1 | `(tests only)` | review — R-sig cascade target |
| ✅ | Ecs | `public/ArchetypeAccessor.cs` | 🔥🔥 | 0 | 0 | 2 | 1 | 1 | `(tests only)` | ✅ converted — see **Converted files** list |
| ⬜ | Spatial | `internals/MortonKeys.cs` | 🔥🔥 | 2 | 0 | 0 | 0 | 0 | `*Spatial*` | review — R-sig cascade target |
| ⬜ | Spatial | `public/ClusterSpatialQuery.cs` | 🔥🔥 | 0 | 0 | 0 | 0 | 0 | `*Spatial*` | review — R-sig cascade target |
| ✅ | Ecs | `public/EcsNavigationQueryBuilder.cs` | 🔥🔥 | 0 | 0 | 1 | 0 | 1 | `(tests only)` | ✅ converted — see **Converted files** list |
| ⬜ | Ecs | `public/DatabaseEngine.StorageIntrospection.cs` | 🔥🔥 | 0 | 0 | 0 | 0 | 2 | `(tests only)` | review — R-sig cascade target |
| ⬜ | Storage | `public/PageBaseHeader.cs` | 🔥🔥 | 0 | 0 | 0 | 0 | 1 | `(tests only)` | review — R-sig cascade target |
| ⬜ | Spatial | `internals/SpatialTriggerSystem.cs` | 🔥🔥 | 0 | 0 | 0 | 1 | 1 | `*Spatial*` | review — R-sig cascade target |
| ⬜ | Ecs | `public/ArchetypeMask.cs` | 🔥🔥 | 0 | 0 | 0 | 0 | 1 | `(tests only)` | review — R-sig cascade target |
| ⬜ | Ecs | `public/DatabaseEngine.StorageDetail.cs` | 🔥🔥 | 0 | 0 | 0 | 0 | 1 | `(tests only)` | review — R-sig cascade target |
| ⬜ | Ecs | `public/EntityId.cs` | 🔥🔥 | 0 | 0 | 0 | 0 | 0 | `*Ecs*\|*SpawnBatch*\|*Component*` | review — R-sig cascade target |
| ⬜ | Storage | `internals/ChunkBasedSegment.cs` | 🔥🔥 | 0 | 0 | 0 | 0 | 1 | `(tests only)` | review — R-sig cascade target |
| ⬜ | Transactions | `internals/RevisionWalker.cs` | 🔥🔥 | 0 | 0 | 0 | 0 | 0 | `*Transaction*\|*Workload*` | review — R-sig cascade target |
| ✅ | Storage | `internals/BootstrapDictionary.cs` | 🔥 | 0 | 2 | 23 | 0 | 4 | `(tests only)` | ✅ converted — see **Converted files** list |
| 🟦 | Durability | `internals/WalCommitBuffer.cs` | 🔥 | 17 | 0 | 4 | 0 | 1 | `*ClusterRegression*\|*Committed*` | 🟦 KEEP — deliberate pointer (annotated) |
| ✅ | Schema | `internals/SchemaEvolutionEngine.cs` | 🔥 | 0 | 0 | 13 | 5 | 9 | `(tests only)` | ✅ converted — see **Converted files** list |
| ✅ | Querying | `public/NavigationView.cs` | 🔥 | 0 | 0 | 10 | 0 | 1 | `*QueryView*` | ✅ converted — see **Converted files** list |
| ✅ | Querying | `internals/StatisticsRebuilder.cs` | 🔥 | 0 | 1 | 13 | 0 | 2 | `(tests only)` | ✅ converted — see **Converted files** list |
| ⬜ | Querying | `internals/PipelineExecutor.cs` | 🔥 | 0 | 0 | 9 | 0 | 7 | `*EcsQuery*` | review — R-sig cascade target |
| ⬜ | Foundation | `Collections/internals/HashUtils.cs` | 🔥 | 0 | 0 | 9 | 0 | 1 | `*HashMap*` | review — R-sig cascade target |
| ✅ | Durability | `internals/RecoveryApplier.cs` | 🔥 | 0 | 0 | 10 | 4 | 1 | `(tests only)` | ✅ converted — see **Converted files** list |
| ⬜ | Querying | `public/FieldEvaluator.cs` | 🔥 | 0 | 0 | 10 | 0 | 1 | `*EcsQuery*` | review — R-sig cascade target |
| 🟦 | Foundation | `Memory/internals/ChainedBlockAllocator.cs` | 🔥 | 7 | 0 | 0 | 0 | 1 | `(tests only)` | 🟦 KEEP — deliberate pointer (annotated) |
| 🟦 | Foundation | `Memory/internals/ChainedBlockAllocatorBase.cs` | 🔥 | 5 | 1 | 1 | 0 | 1 | `(tests only)` | 🟦 KEEP — deliberate pointer (annotated) |
| ⬜ | Querying | `internals/ViewDeltaRingBuffer.cs` | 🔥 | 0 | 0 | 5 | 0 | 1 | `*QueryView*` | review — R-sig cascade target |
| ⬜ | Durability | `internals/WalRecovery.cs` | 🔥 | 0 | 0 | 3 | 0 | 1 | `*ClusterRegression*\|*Committed*` | review — R-sig cascade target |
| ✅ | Querying | `internals/BasicSelectivityEstimator.cs` | 🔥 | 0 | 0 | 1 | 2 | 1 | `(tests only)` | ✅ converted — see **Converted files** list |
| 🟦 | Durability | `internals/WalWriter.cs` | 🔥 | 0 | 1 | 2 | 0 | 1 | `*ClusterRegression*\|*Committed*` | 🟦 KEEP — deliberate pointer (annotated) |
| ✅ | Querying | `internals/AdvancedSelectivityEstimator.cs` | 🔥 | 0 | 0 | 1 | 1 | 1 | `(tests only)` | ✅ converted — see **Converted files** list |
| ⬜ | Foundation | `Hashing/internals/Crc32CUtil.cs` | 🔥 | 0 | 0 | 0 | 0 | 0 | `(tests only)` | review — R-sig cascade target |
| ⬜ | Foundation | `Memory/internals/StoreSpan.cs` | 🔥 | 0 | 1 | 2 | 0 | 1 | `(tests only)` | review — R-sig cascade target |
| ✅ | Querying | `internals/KeyBytes8.cs` | 🔥 | 0 | 0 | 1 | 0 | 1 | `(tests only)` | ✅ converted — see **Converted files** list |
| ⬜ | Schema | `internals/SystemCrud.cs` | 🔥 | 0 | 0 | 0 | 0 | 1 | `(tests only)` | review — R-sig cascade target |
| ⬜ | Durability | `internals/StagingBuffer.cs` | 🔥 | 0 | 0 | 2 | 0 | 3 | `(tests only)` | review — R-sig cascade target |
| ✅ | Durability | `internals/WalSegmentManager.cs` | 🔥 | 0 | 1 | 1 | 0 | 1 | `*ClusterRegression*\|*Committed*` | ✅ converted — see **Converted files** list |
| ⬜ | Foundation | `Memory/internals/BlockAllocatorBase.cs` | 🔥 | 0 | 0 | 2 | 0 | 1 | `(tests only)` | review — R-sig cascade target |
| ⬜ | Querying | `internals/PlanBuilder.cs` | 🔥 | 0 | 0 | 0 | 3 | 1 | `(tests only)` | review — R-sig cascade target |
| ✅ | Durability | `internals/WalSegmentHeader.cs` | 🔥 | 0 | 2 | 0 | 0 | 1 | `*ClusterRegression*\|*Committed*` | ✅ converted — see **Converted files** list |
| ⬜ | Foundation | `Memory/internals/PinnedMemoryBlock.cs` | 🔥 | 0 | 0 | 2 | 0 | 1 | `(tests only)` | review — R-sig cascade target |
| ⬜ | Schema | `internals/TypedMigrationEntry.cs` | 🔥 | 0 | 0 | 0 | 0 | 0 | `(tests only)` | review — R-sig cascade target |
| ⬜ | Durability | `internals/WalSegmentReader.cs` | 🔥 | 0 | 0 | 0 | 0 | 0 | `*ClusterRegression*\|*Committed*` | review — R-sig cascade target |
| ⬜ | Foundation | `Memory/internals/StructAllocator.cs` | 🔥 | 0 | 0 | 0 | 0 | 1 | `(tests only)` | review — R-sig cascade target |
| ⬜ | Querying | `internals/IndexStatistics.cs` | 🔥 | 0 | 0 | 1 | 0 | 1 | `(tests only)` | review — R-sig cascade target |
| ⬜ | Durability | `internals/StagingBufferPool.cs` | 🔥 | 0 | 0 | 1 | 0 | 1 | `(tests only)` | review — R-sig cascade target |
| ⬜ | Foundation | `Memory/internals/MemoryBlockArray.cs` | 🔥 | 0 | 0 | 1 | 0 | 1 | `(tests only)` | review — R-sig cascade target |
| ⬜ | Durability | `internals/RecordCodec.cs` | 🔥 | 0 | 0 | 0 | 0 | 0 | `(tests only)` | review — R-sig cascade target |
| ⬜ | Foundation | `Memory/internals/UnmanagedStructAllocator.cs` | 🔥 | 0 | 0 | 0 | 0 | 1 | `(tests only)` | review — R-sig cascade target |
| ⬜ | Querying | `internals/ViewRegistry.cs` | 🔥 | 1 | 0 | 0 | 0 | 0 | `*QueryView*` | review — R-sig cascade target |
| ⬜ | Querying | `internals/QueryResolverHelper.cs` | 🔥 | 0 | 0 | 0 | 0 | 0 | `(tests only)` | review — R-sig cascade target |
| ⬜ | Schema | `internals/MigrationRegistry.cs` | 🔥 | 0 | 0 | 0 | 2 | 0 | `(tests only)` | review — R-sig cascade target |
| ⬜ | Foundation | `Memory/internals/BlockAllocator.cs` | 🔥 | 0 | 0 | 0 | 0 | 1 | `(tests only)` | review — R-sig cascade target |
| ⬜ | Revision | `internals/RevisionWalker.cs` | 🔥 | 0 | 0 | 0 | 0 | 1 | `*ClusterRegression*\|*Committed*` | review (cold) — R-sig cascade target |
| ⬜ | Schema | `internals/MigrationChain.cs` | 🔥 | 0 | 0 | 0 | 1 | 0 | `(tests only)` | review — R-sig cascade target |

### ❄️ COLD — live status
| St | Area | File | 🔥 | arrow | fixed | ptr | salloc | unsafe | Convert bucket |
|----|------|------|----|-------|-------|-----|--------|--------|----------------|
| ✅ | Runtime | `public/TyphonRuntime.cs` | ❄️ | 0 | 0 | 18 | 0 | 7 | ✅ converted — see **Converted files** list |
| ✅ | Revision | `internals/ComponentRevisionManager.cs` | ❄️ | 4 | 0 | 1 | 4 | 8 | ✅ converted — see **Converted files** list |
| 🟦 | Hosting | `public/String64.cs` | ❄️ | 0 | 1 | 3 | 0 | 3 | 🟦 KEEP — deliberate pointer (annotated) |
| 🟦 | Runtime | `internals/PartitionEntityView.cs` | ❄️ | 0 | 0 | 4 | 0 | 1 | 🟦 KEEP — deliberate pointer (annotated) |
| 🟦 | Runtime | `internals/ClusterRangeEntityView.cs` | ❄️ | 0 | 0 | 3 | 0 | 1 | 🟦 KEEP — deliberate pointer (annotated) |
| ⏭️ | Profiler | `internals/TyphonEvent.cs` | ❄️ | 0 | 0 | 0 | 4 | 0 | skip: stackallocs already `Span<byte>` |
| ⏭️ | Revision | `internals/RevisionEnumerator.cs` | ❄️ | 0 | 0 | 0 | 0 | 2 | skip: only `Unsafe.NullRef` (safe bridge) |
| ✅ | Subscriptions | `internals/DeltaBuilder.cs` | ❄️ | 0 | 0 | 1 | 0 | 1 | ✅ converted — see **Converted files** list |
| ⏭️ | Hosting | `internals/SpanHelpers.cs` | ❄️ | 0 | 0 | 0 | 0 | 1 | skip: only `MemoryMarshal.Cast` |
| ⏭️ | Observability | `public/TelemetryConfig.cs` | ❄️ | 1 | 0 | 0 | 0 | 0 | skip: no raw ptr |
| ⏭️ | Profiler | `internals/EtwSchedulingPump.cs` | ❄️ | 1 | 0 | 0 | 0 | 0 | skip: no raw ptr |
| ⏭️ | Profiler | `internals/TcpExporter.cs` | ❄️ | 0 | 0 | 0 | 1 | 0 | skip: no raw ptr |
| ⏭️ | Runtime | `public/DagScheduler.Logging.cs` | ❄️ | 1 | 0 | 0 | 0 | 0 | skip: no raw ptr |
| ⏭️ | Errors | `public/Result.cs` | ❄️ | 0 | 0 | 0 | 0 | 0 | skip: `Unsafe.As` already |
| ⏭️ | Observability | `public/CheckConfig.cs` | ❄️ | 0 | 0 | 0 | 0 | 1 | skip: no raw ptr |
| ⏭️ | Profiler | `internals/CpuSampleParser.cs` | ❄️ | 0 | 0 | 0 | 0 | 0 | skip: no raw ptr |
| ⏭️ | Profiler | `internals/GaugeSnapshotEmitter.cs` | ❄️ | 0 | 0 | 0 | 1 | 0 | skip: stackalloc already `Span` |
| ✅ | Subscriptions | `internals/EntitySnapshotReader.cs` | ❄️ | 0 | 0 | 0 | 0 | 1 | ✅ converted — see **Converted files** list |
| ⏭️ | Subscriptions | `internals/SubscriptionOutputPhase.cs` | ❄️ | 0 | 0 | 0 | 1 | 0 | skip: stackalloc already `Span` |
<!-- TABLES:END -->

---

## Strategy (see chat for rationale)

1. **Order:** COLD (tests-only) → WARM (batch-bench) → HOT (per-area bench-gated). Cold-first proves the loop, banks safe wins, defers expensive benchmarking.
2. **Per file:** classify each pointer site into bucket A–E, convert A/B/E, keep C-field / convert C-use-sites, keep D. *No added complexity* is a hard rule — if Span makes it uglier, it's a 🟦 KEEP.
3. **Test gate (every file):** `test-affected.py <file>` green before marking ✅.
4. **Perf gate (HOT/WARM only):** run the file's `Bench filter` (full fidelity, ~1–3 min), diff vs `00-baseline` via `benchmark/.local/abcompare.py`. Within noise → ✅. Regression > threshold → restructure for bounds-check elision; if still slow → ⚠️ revert to pointer + document.
5. **Full suite** once per phase boundary; **full 15-min benchmark** only at the very end (final proof).
6. **No commits** — Loïc commits. This map is the progress record.
