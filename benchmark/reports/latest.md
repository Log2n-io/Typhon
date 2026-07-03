# Benchmark Regression Report

**Date:** 2026-07-03T05:36:59Z
**Commit:** c3de71a (feature/422-strict-mode-checks)
**Environment:** Intel Xeon Platinum 8151 CPU 3.40GHz | Linux Ubuntu 22.04.5 LTS (Jammy Jellyfish) | .NET 10.0.9

## Summary

| Status | Count |
|--------|-------|
| Regression | 3 |
| Improvement | 5 |
| Stable | 61 |
| Noisy (filtered) | 22 |
| Insufficient Data | 0 |

## Regressions

> [!WARNING]
> 3 benchmark(s) show performance regression

| Benchmark | Current | Previous | Change | Threshold |
|-----------|---------|----------|--------|-----------|
| AccessControlBenchmarks.SharedLock_Contended (ThreadCount=4) | 1.27 us | 922.75 ns | +37.2% | 10% |
| EpochGuardBenchmarks.MinActiveEpoch_WhilePinned | 18.55 ns | 16.89 ns | +9.8% | 8% |
| ChunkAccessorBenchmarks.Eviction_17Chunks | 6.17 ns | 5.64 ns | +9.5% | 8% |

![AccessControlBenchmarks.SharedLock_Contended (ThreadCount=4)](charts/AccessControlBenchmarks.SharedLock_Contended_ThreadCount_4.svg)

![EpochGuardBenchmarks.MinActiveEpoch_WhilePinned](charts/EpochGuardBenchmarks.MinActiveEpoch_WhilePinned.svg)

![ChunkAccessorBenchmarks.Eviction_17Chunks](charts/ChunkAccessorBenchmarks.Eviction_17Chunks.svg)

## Improvements

| Benchmark | Current | Previous | Change |
|-----------|---------|----------|--------|
| String64Benchmarks.Compare_Order | 4.21 ns | 5.03 ns | -16.3% |
| ClusterRegressionBenchmarks.ClusterRandomAccess_Mixed | 26.05 us | 33.63 us | -22.5% |
| EcsQueryBenchmarks.PolymorphicQuery_Count | 262.22 us | 401.33 us | -34.7% |
| EcsQueryBenchmarks.Enabled_Query_Count | 253.82 us | 402.35 us | -36.9% |
| EcsQueryBenchmarks.ExactQuery_Count | 119.35 us | 192.31 us | -37.9% |

<details>
<summary>Stable Benchmarks (61)</summary>

| Benchmark | Current | Previous | Change |
|-----------|---------|----------|--------|
| AccessControlBenchmarks.SharedLock_Contended (ThreadCount=2) | 1.04 us | 1.17 us | -11.6% |
| AccessControlBenchmarks.SharedLock_Uncontended (ThreadCount=2) | 19.82 ns | 18.94 ns | +4.7% |
| AccessControlSmallBenchmarks.Promotion_SharedToExclusive | 44.37 ns | 45.38 ns | -2.2% |
| BTreeMicroBenchmarks.Delete_Reinsert | 950.27 ns | 925.86 ns | +2.6% |
| BTreeMicroBenchmarks.Insert_Random | 474.29 ns | 479.07 ns | -1.0% |
| BTreeMicroBenchmarks.Insert_Sequential | 394.88 ns | 386.63 ns | +2.1% |
| BTreeMicroBenchmarks.Lookup_Miss | 338.14 ns | 341.46 ns | -1.0% |
| BTreeMicroBenchmarks.SequentialScan_100 | 258.46 ns | 273.78 ns | -5.6% |
| CascadeDeleteBenchmarks.CascadeDeleteAll (ParentCount=10&ChildrenPerParent=10) | 2.38 us | 2.30 us | +3.7% |
| CascadeDeleteBenchmarks.CascadeDeleteAll (ParentCount=10&ChildrenPerParent=100) | 2.36 us | 2.35 us | +0.6% |
| CascadeDeleteBenchmarks.CascadeDeleteAll (ParentCount=100&ChildrenPerParent=10) | 14.38 us | 14.94 us | -3.8% |
| CascadeDeleteBenchmarks.CascadeDeleteAll (ParentCount=100&ChildrenPerParent=100) | 15.09 us | 14.89 us | +1.3% |
| ChunkAccessorBenchmarks.CommitChanges_AllDirty | 178.11 ns | 171.46 ns | +3.9% |
| ChunkAccessorBenchmarks.Dispose_16Slots | 161.95 ns | 166.66 ns | -2.8% |
| ClusterRegressionBenchmarks.ClusterIteration_MixedSvVersioned | 18.55 us | 18.20 us | +1.9% |
| ClusterRegressionBenchmarks.ClusterIteration_SV | 15.72 us | 16.69 us | -5.8% |
| ClusterRegressionBenchmarks.IndexedQuery_1Percent | 11.54 us | 11.60 us | -0.5% |
| ClusterRegressionBenchmarks.OrderedQuery_Take100 | 17.68 us | 17.96 us | -1.6% |
| ClusterRegressionBenchmarks.VersionedWriteCommit | 149.35 us | 151.73 us | -1.6% |
| ComponentTableBenchmarks.CreateEntity_SingleComponent | 6.57 us | 6.22 us | +5.6% |
| ComponentTableBenchmarks.ReadComponent_ById | 1.61 us | 1.66 us | -2.5% |
| ComponentTableBenchmarks.UpdateComponent_SingleField | 4.58 us | 4.25 us | +7.8% |
| EcsQueryBenchmarks.EnableDisable_1000 | 343.66 us | 350.65 us | -2.0% |
| EcsQueryBenchmarks.Query_Any | 1.57 us | 1.56 us | +1.2% |
| EcsQueryBenchmarks.WhereField_Count | 678.78 us | 706.80 us | -4.0% |
| EpochGuardBenchmarks.EnterExit | 10.32 ns | 10.96 ns | -5.8% |
| EpochGuardBenchmarks.NestedThreeLevels | 19.68 ns | 18.64 ns | +5.5% |
| IndexLookupBenchmarks.DeleteEntity_SingleComponent | 9.94 us | 8.58 us | +15.9% |
| IndexLookupBenchmarks.PrimaryKey_BatchRandom | 40.64 us | 42.86 us | -5.2% |
| IndexLookupBenchmarks.PrimaryKey_BatchSequential | 33.90 us | 36.54 us | -7.2% |
| IndexLookupBenchmarks.PrimaryKey_PointLookup | 1.61 us | 1.62 us | -0.9% |
| PagedMMFBenchmarks.CacheHit | 12.45 ns | 11.04 ns | +12.7% |
| PagedMMFBenchmarks.PageAllocation | 1.55 us | 1.56 us | -0.1% |
| RevisionBenchmarks.Read_10Versions | 1.62 us | 1.60 us | +1.2% |
| RevisionBenchmarks.Read_50Versions | 1.58 us | 1.65 us | -4.2% |
| RevisionBenchmarks.Read_SingleVersion | 1.62 us | 1.66 us | -2.3% |
| SpawnBatchBenchmarks.SingleSpawnLoop (EntityCount=100) | 50.95 us | 52.75 us | -3.4% |
| SpawnBatchBenchmarks.SingleSpawnLoop (EntityCount=1000) | 531.52 us | 538.73 us | -1.3% |
| SpawnBatchBenchmarks.SpawnBatch_SOA (EntityCount=100) | 37.93 us | 39.33 us | -3.6% |
| SpawnBatchBenchmarks.SpawnBatch_SOA (EntityCount=1000) | 387.06 us | 424.34 us | -8.8% |
| SpawnBatchBenchmarks.SpawnBatch_SharedValues (EntityCount=100) | 42.36 us | 46.89 us | -9.7% |
| SpawnBatchBenchmarks.SpawnBatch_SharedValues (EntityCount=1000) | 441.32 us | 501.21 us | -11.9% |
| StrictModeOffPathBenchmarks.EmptyMethod (LoopCount=1024) | 484.91 ns | 467.78 ns | +3.7% |
| StrictModeOffPathBenchmarks.Require_ConditionFalse_OffPath (LoopCount=1024) | 9.56 us | 10.07 us | -5.0% |
| StrictModeOffPathBenchmarks.Require_ConditionTrue_OffPath (LoopCount=1024) | 9.78 us | 9.53 us | +2.6% |
| TransactionBenchmarks.Transaction_BulkRead (EntityCount=100) | 31.64 us | 32.90 us | -3.8% |
| TransactionBenchmarks.Transaction_BulkRead (EntityCount=1000) | 325.14 us | 341.54 us | -4.8% |
| TransactionBenchmarks.Transaction_BulkUpdate (EntityCount=100) | 120.22 us | 119.44 us | +0.6% |
| TransactionBenchmarks.Transaction_BulkUpdate (EntityCount=1000) | 1.25 ms | 1.20 ms | +3.8% |
| TransactionBenchmarks.Transaction_CreateReadCommit (EntityCount=100) | 6.64 us | 6.80 us | -2.3% |
| TransactionBenchmarks.Transaction_CreateReadCommit (EntityCount=1000) | 6.67 us | 6.59 us | +1.2% |
| TyphonEventOffPathBenchmarks.BeginBTreeInsert_OffPath (LoopCount=1024) | 2.57 us | 2.58 us | -0.1% |
| TyphonEventOffPathBenchmarks.BeginCheckpointCycle_OffPath (LoopCount=1024) | 4.64 us | 4.62 us | +0.4% |
| TyphonEventOffPathBenchmarks.BeginEcsSpawn_OffPath (LoopCount=1024) | 3.86 us | 4.11 us | -6.2% |
| TyphonEventOffPathBenchmarks.BeginEcsSpawn_WithOptional_OffPath (LoopCount=1024) | 5.14 us | 5.15 us | -0.2% |
| TyphonEventOffPathBenchmarks.EmptyMethod (LoopCount=1024) | 467.55 ns | 484.44 ns | -3.5% |
| WorkloadBenchmarks.CrudLifecycle | 8.87 us | 9.26 us | -4.2% |
| WorkloadBenchmarks.MultiComponent_Crud | 9.26 us | 8.67 us | +6.8% |
| WorkloadBenchmarks.ReadHeavy_90_10 | 55.67 us | 57.48 us | -3.2% |
| WorkloadBenchmarks.WriteHeavy_Batch | 500.30 us | 483.84 us | +3.4% |
| WorkloadBenchmarks.WriteHeavy_SvIndexed_Batch | 677.87 us | 652.65 us | +3.9% |

</details>

<details>
<summary>Noisy Benchmarks (22) — filtered from regression detection</summary>

| Benchmark | Current | Previous | Change | Reason |
|-----------|---------|----------|--------|--------|
| EpochGuardBenchmarks.MinActiveEpoch | 1.00 ns | 1.12 ns | -10.4% | abs delta 0.12ns < 0.5ns threshold |
| AccessControlBenchmarks.SharedLock_Contended (ThreadCount=8) | 4.32 us | 4.62 us | -6.4% | high variance (CoV 44%) |
| String64Benchmarks.Compare_Equal | 4.82 ns | 4.65 ns | +3.7% | abs delta 0.17ns < 0.5ns threshold |
| AccessControlSmallBenchmarks.SharedLock_Uncontended | 17.29 ns | 16.98 ns | +1.9% | abs delta 0.32ns < 0.5ns threshold |
| AccessControlBenchmarks.SharedLock_Uncontended (ThreadCount=4) | 18.74 ns | 19.05 ns | -1.6% | abs delta 0.31ns < 0.5ns threshold |
| AccessControlBenchmarks.ExclusiveLock_Uncontended (ThreadCount=4) | 21.49 ns | 21.82 ns | -1.5% | abs delta 0.33ns < 0.5ns threshold |
| AccessControlBenchmarks.Promotion_SharedToExclusive (ThreadCount=2) | 44.24 ns | 43.88 ns | +0.8% | abs delta 0.36ns < 0.5ns threshold |
| ChunkAccessorBenchmarks.MRU_Hit | 3.07 ns | 3.10 ns | -0.7% | abs delta 0.02ns < 0.5ns threshold |
| AccessControlBenchmarks.Promotion_SharedToExclusive (ThreadCount=8) | 44.49 ns | 44.20 ns | +0.6% | abs delta 0.29ns < 0.5ns threshold |
| AccessControlBenchmarks.ExclusiveLock_Uncontended (ThreadCount=8) | 21.76 ns | 21.85 ns | -0.4% | abs delta 0.09ns < 0.5ns threshold |
| PagedMMFBenchmarks.CacheMiss | 9.48 ns | 9.52 ns | -0.4% | abs delta 0.04ns < 0.5ns threshold |
| AccessControlBenchmarks.ExclusiveLock_Uncontended (ThreadCount=2) | 21.96 ns | 21.87 ns | +0.4% | abs delta 0.09ns < 0.5ns threshold |
| String64Benchmarks.HashCode | 15.47 ns | 15.41 ns | +0.4% | abs delta 0.05ns < 0.5ns threshold |
| AccessControlBenchmarks.Promotion_SharedToExclusive (ThreadCount=4) | 43.86 ns | 44.01 ns | -0.3% | abs delta 0.15ns < 0.5ns threshold |
| String64Benchmarks.Construct_FromString | 19.90 ns | 19.84 ns | +0.3% | abs delta 0.06ns < 0.5ns threshold |
| FindNextUnsetBenchmarks.FindNextUnset_Sparse25 | 6.01 ns | 5.99 ns | +0.3% | abs delta 0.02ns < 0.5ns threshold |
| FindNextUnsetBenchmarks.FindNextUnset_AlmostFull | 5.98 ns | 5.99 ns | -0.2% | abs delta 0.01ns < 0.5ns threshold |
| AccessControlSmallBenchmarks.ExclusiveLock_Uncontended | 24.74 ns | 24.78 ns | -0.2% | abs delta 0.05ns < 0.5ns threshold |
| FindNextUnsetBenchmarks.FindNextUnset_Dense | 6.00 ns | 5.99 ns | +0.1% | abs delta 0.01ns < 0.5ns threshold |
| BTreeMicroBenchmarks.Lookup_Hit | 362.71 ns | 362.40 ns | +0.1% | abs delta 0.31ns < 0.5ns threshold |
| AccessControlBenchmarks.SharedLock_Uncontended (ThreadCount=8) | 18.76 ns | 18.75 ns | +0.1% | abs delta 0.01ns < 0.5ns threshold |
| ChunkAccessorBenchmarks.SIMD_Hit_4Chunks | 3.08 ns | 3.08 ns | +0.1% | abs delta 0.00ns < 0.5ns threshold |

</details>

<details>
<summary>Insufficient Data (0)</summary>

No benchmarks with insufficient data.

</details>

## Trend Charts

### Category: EndToEnd
![TransactionBenchmarks.Transaction_BulkRead (EntityCount=100)](charts/TransactionBenchmarks.Transaction_BulkRead_EntityCount_100.svg)

![TransactionBenchmarks.Transaction_BulkRead (EntityCount=1000)](charts/TransactionBenchmarks.Transaction_BulkRead_EntityCount_1000.svg)

![TransactionBenchmarks.Transaction_BulkUpdate (EntityCount=100)](charts/TransactionBenchmarks.Transaction_BulkUpdate_EntityCount_100.svg)

![TransactionBenchmarks.Transaction_BulkUpdate (EntityCount=1000)](charts/TransactionBenchmarks.Transaction_BulkUpdate_EntityCount_1000.svg)

![TransactionBenchmarks.Transaction_CreateReadCommit (EntityCount=100)](charts/TransactionBenchmarks.Transaction_CreateReadCommit_EntityCount_100.svg)

![TransactionBenchmarks.Transaction_CreateReadCommit (EntityCount=1000)](charts/TransactionBenchmarks.Transaction_CreateReadCommit_EntityCount_1000.svg)

### Category: Workload
![WorkloadBenchmarks.CrudLifecycle](charts/WorkloadBenchmarks.CrudLifecycle.svg)

![WorkloadBenchmarks.MultiComponent_Crud](charts/WorkloadBenchmarks.MultiComponent_Crud.svg)

![WorkloadBenchmarks.ReadHeavy_90_10](charts/WorkloadBenchmarks.ReadHeavy_90_10.svg)

![WorkloadBenchmarks.WriteHeavy_Batch](charts/WorkloadBenchmarks.WriteHeavy_Batch.svg)

![WorkloadBenchmarks.WriteHeavy_SvIndexed_Batch](charts/WorkloadBenchmarks.WriteHeavy_SvIndexed_Batch.svg)

### Category: ECS
![CascadeDeleteBenchmarks.CascadeDeleteAll (ParentCount=10&ChildrenPerParent=10)](charts/CascadeDeleteBenchmarks.CascadeDeleteAll_ParentCount_10&ChildrenPerParent_10.svg)

![CascadeDeleteBenchmarks.CascadeDeleteAll (ParentCount=10&ChildrenPerParent=100)](charts/CascadeDeleteBenchmarks.CascadeDeleteAll_ParentCount_10&ChildrenPerParent_100.svg)

![CascadeDeleteBenchmarks.CascadeDeleteAll (ParentCount=100&ChildrenPerParent=10)](charts/CascadeDeleteBenchmarks.CascadeDeleteAll_ParentCount_100&ChildrenPerParent_10.svg)

![CascadeDeleteBenchmarks.CascadeDeleteAll (ParentCount=100&ChildrenPerParent=100)](charts/CascadeDeleteBenchmarks.CascadeDeleteAll_ParentCount_100&ChildrenPerParent_100.svg)

![EcsQueryBenchmarks.EnableDisable_1000](charts/EcsQueryBenchmarks.EnableDisable_1000.svg)

![EcsQueryBenchmarks.Enabled_Query_Count](charts/EcsQueryBenchmarks.Enabled_Query_Count.svg)

![EcsQueryBenchmarks.ExactQuery_Count](charts/EcsQueryBenchmarks.ExactQuery_Count.svg)

![EcsQueryBenchmarks.PolymorphicQuery_Count](charts/EcsQueryBenchmarks.PolymorphicQuery_Count.svg)

![EcsQueryBenchmarks.Query_Any](charts/EcsQueryBenchmarks.Query_Any.svg)

![EcsQueryBenchmarks.WhereField_Count](charts/EcsQueryBenchmarks.WhereField_Count.svg)

![SpawnBatchBenchmarks.SingleSpawnLoop (EntityCount=100)](charts/SpawnBatchBenchmarks.SingleSpawnLoop_EntityCount_100.svg)

![SpawnBatchBenchmarks.SingleSpawnLoop (EntityCount=1000)](charts/SpawnBatchBenchmarks.SingleSpawnLoop_EntityCount_1000.svg)

![SpawnBatchBenchmarks.SpawnBatch_SOA (EntityCount=100)](charts/SpawnBatchBenchmarks.SpawnBatch_SOA_EntityCount_100.svg)

![SpawnBatchBenchmarks.SpawnBatch_SOA (EntityCount=1000)](charts/SpawnBatchBenchmarks.SpawnBatch_SOA_EntityCount_1000.svg)

![SpawnBatchBenchmarks.SpawnBatch_SharedValues (EntityCount=100)](charts/SpawnBatchBenchmarks.SpawnBatch_SharedValues_EntityCount_100.svg)

![SpawnBatchBenchmarks.SpawnBatch_SharedValues (EntityCount=1000)](charts/SpawnBatchBenchmarks.SpawnBatch_SharedValues_EntityCount_1000.svg)

### Category: Data
![ComponentTableBenchmarks.CreateEntity_SingleComponent](charts/ComponentTableBenchmarks.CreateEntity_SingleComponent.svg)

![ComponentTableBenchmarks.ReadComponent_ById](charts/ComponentTableBenchmarks.ReadComponent_ById.svg)

![ComponentTableBenchmarks.UpdateComponent_SingleField](charts/ComponentTableBenchmarks.UpdateComponent_SingleField.svg)

### Category: MVCC
![RevisionBenchmarks.Read_10Versions](charts/RevisionBenchmarks.Read_10Versions.svg)

![RevisionBenchmarks.Read_50Versions](charts/RevisionBenchmarks.Read_50Versions.svg)

![RevisionBenchmarks.Read_SingleVersion](charts/RevisionBenchmarks.Read_SingleVersion.svg)

### Category: Epoch
![EpochGuardBenchmarks.EnterExit](charts/EpochGuardBenchmarks.EnterExit.svg)

![EpochGuardBenchmarks.MinActiveEpoch](charts/EpochGuardBenchmarks.MinActiveEpoch.svg)

![EpochGuardBenchmarks.MinActiveEpoch_WhilePinned](charts/EpochGuardBenchmarks.MinActiveEpoch_WhilePinned.svg)

![EpochGuardBenchmarks.NestedThreeLevels](charts/EpochGuardBenchmarks.NestedThreeLevels.svg)

### Category: BTree
![BTreeMicroBenchmarks.Delete_Reinsert](charts/BTreeMicroBenchmarks.Delete_Reinsert.svg)

![BTreeMicroBenchmarks.Insert_Random](charts/BTreeMicroBenchmarks.Insert_Random.svg)

![BTreeMicroBenchmarks.Insert_Sequential](charts/BTreeMicroBenchmarks.Insert_Sequential.svg)

![BTreeMicroBenchmarks.Lookup_Hit](charts/BTreeMicroBenchmarks.Lookup_Hit.svg)

![BTreeMicroBenchmarks.Lookup_Miss](charts/BTreeMicroBenchmarks.Lookup_Miss.svg)

![BTreeMicroBenchmarks.SequentialScan_100](charts/BTreeMicroBenchmarks.SequentialScan_100.svg)

### Category: Index
![IndexLookupBenchmarks.DeleteEntity_SingleComponent](charts/IndexLookupBenchmarks.DeleteEntity_SingleComponent.svg)

![IndexLookupBenchmarks.PrimaryKey_BatchRandom](charts/IndexLookupBenchmarks.PrimaryKey_BatchRandom.svg)

![IndexLookupBenchmarks.PrimaryKey_BatchSequential](charts/IndexLookupBenchmarks.PrimaryKey_BatchSequential.svg)

![IndexLookupBenchmarks.PrimaryKey_PointLookup](charts/IndexLookupBenchmarks.PrimaryKey_PointLookup.svg)

### Category: Storage
![PagedMMFBenchmarks.CacheHit](charts/PagedMMFBenchmarks.CacheHit.svg)

![PagedMMFBenchmarks.CacheMiss](charts/PagedMMFBenchmarks.CacheMiss.svg)

![PagedMMFBenchmarks.PageAllocation](charts/PagedMMFBenchmarks.PageAllocation.svg)

### Category: ChunkAccessor
![ChunkAccessorBenchmarks.CommitChanges_AllDirty](charts/ChunkAccessorBenchmarks.CommitChanges_AllDirty.svg)

![ChunkAccessorBenchmarks.Dispose_16Slots](charts/ChunkAccessorBenchmarks.Dispose_16Slots.svg)

![ChunkAccessorBenchmarks.Eviction_17Chunks](charts/ChunkAccessorBenchmarks.Eviction_17Chunks.svg)

![ChunkAccessorBenchmarks.MRU_Hit](charts/ChunkAccessorBenchmarks.MRU_Hit.svg)

![ChunkAccessorBenchmarks.SIMD_Hit_4Chunks](charts/ChunkAccessorBenchmarks.SIMD_Hit_4Chunks.svg)

### Category: Collections
![FindNextUnsetBenchmarks.FindNextUnset_AlmostFull](charts/FindNextUnsetBenchmarks.FindNextUnset_AlmostFull.svg)

![FindNextUnsetBenchmarks.FindNextUnset_Dense](charts/FindNextUnsetBenchmarks.FindNextUnset_Dense.svg)

![FindNextUnsetBenchmarks.FindNextUnset_Sparse25](charts/FindNextUnsetBenchmarks.FindNextUnset_Sparse25.svg)

### Category: Primitives
![String64Benchmarks.Compare_Equal](charts/String64Benchmarks.Compare_Equal.svg)

![String64Benchmarks.Compare_Order](charts/String64Benchmarks.Compare_Order.svg)

![String64Benchmarks.Construct_FromString](charts/String64Benchmarks.Construct_FromString.svg)

![String64Benchmarks.HashCode](charts/String64Benchmarks.HashCode.svg)

### Category: Concurrency
![AccessControlBenchmarks.ExclusiveLock_Uncontended (ThreadCount=2)](charts/AccessControlBenchmarks.ExclusiveLock_Uncontended_ThreadCount_2.svg)

![AccessControlBenchmarks.ExclusiveLock_Uncontended (ThreadCount=4)](charts/AccessControlBenchmarks.ExclusiveLock_Uncontended_ThreadCount_4.svg)

![AccessControlBenchmarks.ExclusiveLock_Uncontended (ThreadCount=8)](charts/AccessControlBenchmarks.ExclusiveLock_Uncontended_ThreadCount_8.svg)

![AccessControlBenchmarks.Promotion_SharedToExclusive (ThreadCount=2)](charts/AccessControlBenchmarks.Promotion_SharedToExclusive_ThreadCount_2.svg)

![AccessControlBenchmarks.Promotion_SharedToExclusive (ThreadCount=4)](charts/AccessControlBenchmarks.Promotion_SharedToExclusive_ThreadCount_4.svg)

![AccessControlBenchmarks.Promotion_SharedToExclusive (ThreadCount=8)](charts/AccessControlBenchmarks.Promotion_SharedToExclusive_ThreadCount_8.svg)

![AccessControlBenchmarks.SharedLock_Contended (ThreadCount=2)](charts/AccessControlBenchmarks.SharedLock_Contended_ThreadCount_2.svg)

![AccessControlBenchmarks.SharedLock_Contended (ThreadCount=4)](charts/AccessControlBenchmarks.SharedLock_Contended_ThreadCount_4.svg)

![AccessControlBenchmarks.SharedLock_Contended (ThreadCount=8)](charts/AccessControlBenchmarks.SharedLock_Contended_ThreadCount_8.svg)

![AccessControlBenchmarks.SharedLock_Uncontended (ThreadCount=2)](charts/AccessControlBenchmarks.SharedLock_Uncontended_ThreadCount_2.svg)

![AccessControlBenchmarks.SharedLock_Uncontended (ThreadCount=4)](charts/AccessControlBenchmarks.SharedLock_Uncontended_ThreadCount_4.svg)

![AccessControlBenchmarks.SharedLock_Uncontended (ThreadCount=8)](charts/AccessControlBenchmarks.SharedLock_Uncontended_ThreadCount_8.svg)

![AccessControlSmallBenchmarks.ExclusiveLock_Uncontended](charts/AccessControlSmallBenchmarks.ExclusiveLock_Uncontended.svg)

![AccessControlSmallBenchmarks.Promotion_SharedToExclusive](charts/AccessControlSmallBenchmarks.Promotion_SharedToExclusive.svg)

![AccessControlSmallBenchmarks.SharedLock_Uncontended](charts/AccessControlSmallBenchmarks.SharedLock_Uncontended.svg)

### Category: Uncategorized
![ClusterRegressionBenchmarks.ClusterIteration_MixedSvVersioned](charts/ClusterRegressionBenchmarks.ClusterIteration_MixedSvVersioned.svg)

![ClusterRegressionBenchmarks.ClusterIteration_SV](charts/ClusterRegressionBenchmarks.ClusterIteration_SV.svg)

![ClusterRegressionBenchmarks.ClusterRandomAccess_Mixed](charts/ClusterRegressionBenchmarks.ClusterRandomAccess_Mixed.svg)

![ClusterRegressionBenchmarks.IndexedQuery_1Percent](charts/ClusterRegressionBenchmarks.IndexedQuery_1Percent.svg)

![ClusterRegressionBenchmarks.OrderedQuery_Take100](charts/ClusterRegressionBenchmarks.OrderedQuery_Take100.svg)

![ClusterRegressionBenchmarks.VersionedWriteCommit](charts/ClusterRegressionBenchmarks.VersionedWriteCommit.svg)

![StrictModeOffPathBenchmarks.EmptyMethod (LoopCount=1024)](charts/StrictModeOffPathBenchmarks.EmptyMethod_LoopCount_1024.svg)

![StrictModeOffPathBenchmarks.Require_ConditionFalse_OffPath (LoopCount=1024)](charts/StrictModeOffPathBenchmarks.Require_ConditionFalse_OffPath_LoopCount_1024.svg)

![StrictModeOffPathBenchmarks.Require_ConditionTrue_OffPath (LoopCount=1024)](charts/StrictModeOffPathBenchmarks.Require_ConditionTrue_OffPath_LoopCount_1024.svg)

![TyphonEventOffPathBenchmarks.BeginBTreeInsert_OffPath (LoopCount=1024)](charts/TyphonEventOffPathBenchmarks.BeginBTreeInsert_OffPath_LoopCount_1024.svg)

![TyphonEventOffPathBenchmarks.BeginCheckpointCycle_OffPath (LoopCount=1024)](charts/TyphonEventOffPathBenchmarks.BeginCheckpointCycle_OffPath_LoopCount_1024.svg)

![TyphonEventOffPathBenchmarks.BeginEcsSpawn_OffPath (LoopCount=1024)](charts/TyphonEventOffPathBenchmarks.BeginEcsSpawn_OffPath_LoopCount_1024.svg)

![TyphonEventOffPathBenchmarks.BeginEcsSpawn_WithOptional_OffPath (LoopCount=1024)](charts/TyphonEventOffPathBenchmarks.BeginEcsSpawn_WithOptional_OffPath_LoopCount_1024.svg)

![TyphonEventOffPathBenchmarks.EmptyMethod (LoopCount=1024)](charts/TyphonEventOffPathBenchmarks.EmptyMethod_LoopCount_1024.svg)
