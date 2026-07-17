# Benchmark Regression Report

**Date:** 2026-07-03T09:00:58Z
**Commit:** 2a3e303 (feature/422-strict-mode-checks)
**Environment:** Intel Xeon Platinum 8151 CPU 3.40GHz | Linux Ubuntu 22.04.5 LTS (Jammy Jellyfish) | .NET 10.0.9

## Summary

| Status | Count |
|--------|-------|
| Regression | 2 |
| Improvement | 1 |
| Stable | 66 |
| Noisy (filtered) | 22 |
| Insufficient Data | 0 |

## Regressions

> [!WARNING]
> 2 benchmark(s) show performance regression

| Benchmark | Current | Previous | Change | Threshold |
|-----------|---------|----------|--------|-----------|
| PagedMMFBenchmarks.CacheMiss | 13.67 ns | 9.48 ns | +44.1% | 15% |
| EcsQueryBenchmarks.Query_Any | 1.94 us | 1.57 us | +23.5% | 20% |

![PagedMMFBenchmarks.CacheMiss](charts/PagedMMFBenchmarks.CacheMiss.svg)

![EcsQueryBenchmarks.Query_Any](charts/EcsQueryBenchmarks.Query_Any.svg)

## Improvements

| Benchmark | Current | Previous | Change |
|-----------|---------|----------|--------|
| AccessControlBenchmarks.SharedLock_Contended (ThreadCount=8) | 2.00 us | 4.32 us | -53.7% |

<details>
<summary>Stable Benchmarks (66)</summary>

| Benchmark | Current | Previous | Change |
|-----------|---------|----------|--------|
| AccessControlBenchmarks.Promotion_SharedToExclusive (ThreadCount=4) | 44.69 ns | 43.86 ns | +1.9% |
| AccessControlBenchmarks.Promotion_SharedToExclusive (ThreadCount=8) | 43.73 ns | 44.49 ns | -1.7% |
| AccessControlBenchmarks.SharedLock_Contended (ThreadCount=2) | 940.00 ns | 1.04 us | -9.5% |
| AccessControlBenchmarks.SharedLock_Uncontended (ThreadCount=2) | 18.95 ns | 19.82 ns | -4.4% |
| AccessControlSmallBenchmarks.Promotion_SharedToExclusive | 45.54 ns | 44.37 ns | +2.6% |
| BTreeMicroBenchmarks.Delete_Reinsert | 911.35 ns | 950.27 ns | -4.1% |
| BTreeMicroBenchmarks.Insert_Random | 475.95 ns | 474.29 ns | +0.4% |
| BTreeMicroBenchmarks.Insert_Sequential | 392.87 ns | 394.88 ns | -0.5% |
| BTreeMicroBenchmarks.Lookup_Hit | 350.38 ns | 362.71 ns | -3.4% |
| BTreeMicroBenchmarks.Lookup_Miss | 340.31 ns | 338.14 ns | +0.6% |
| BTreeMicroBenchmarks.SequentialScan_100 | 261.27 ns | 258.46 ns | +1.1% |
| CascadeDeleteBenchmarks.CascadeDeleteAll (ParentCount=10&ChildrenPerParent=100) | 2.40 us | 2.36 us | +1.5% |
| CascadeDeleteBenchmarks.CascadeDeleteAll (ParentCount=100&ChildrenPerParent=10) | 14.67 us | 14.38 us | +2.0% |
| CascadeDeleteBenchmarks.CascadeDeleteAll (ParentCount=100&ChildrenPerParent=100) | 15.16 us | 15.09 us | +0.5% |
| ChunkAccessorBenchmarks.CommitChanges_AllDirty | 186.08 ns | 178.11 ns | +4.5% |
| ChunkAccessorBenchmarks.Dispose_16Slots | 165.85 ns | 161.95 ns | +2.4% |
| ChunkAccessorBenchmarks.Eviction_17Chunks | 5.61 ns | 6.17 ns | -9.1% |
| ClusterRegressionBenchmarks.ClusterIteration_MixedSvVersioned | 18.53 us | 18.55 us | -0.1% |
| ClusterRegressionBenchmarks.ClusterIteration_SV | 15.89 us | 15.72 us | +1.1% |
| ClusterRegressionBenchmarks.ClusterRandomAccess_Mixed | 26.47 us | 26.05 us | +1.6% |
| ClusterRegressionBenchmarks.IndexedQuery_1Percent | 11.79 us | 11.54 us | +2.2% |
| ClusterRegressionBenchmarks.OrderedQuery_Take100 | 17.65 us | 17.68 us | -0.2% |
| ClusterRegressionBenchmarks.VersionedWriteCommit | 147.21 us | 149.35 us | -1.4% |
| ComponentTableBenchmarks.CreateEntity_SingleComponent | 6.57 us | 6.57 us | -0.0% |
| ComponentTableBenchmarks.ReadComponent_ById | 1.62 us | 1.61 us | +0.2% |
| ComponentTableBenchmarks.UpdateComponent_SingleField | 4.21 us | 4.58 us | -8.2% |
| EcsQueryBenchmarks.EnableDisable_1000 | 342.25 us | 343.66 us | -0.4% |
| EcsQueryBenchmarks.Enabled_Query_Count | 258.57 us | 253.82 us | +1.9% |
| EcsQueryBenchmarks.ExactQuery_Count | 123.21 us | 119.35 us | +3.2% |
| EcsQueryBenchmarks.PolymorphicQuery_Count | 251.43 us | 262.22 us | -4.1% |
| EcsQueryBenchmarks.WhereField_Count | 688.90 us | 678.78 us | +1.5% |
| EpochGuardBenchmarks.MinActiveEpoch_WhilePinned | 17.01 ns | 18.55 ns | -8.3% |
| EpochGuardBenchmarks.NestedThreeLevels | 18.36 ns | 19.68 ns | -6.7% |
| IndexLookupBenchmarks.DeleteEntity_SingleComponent | 9.89 us | 9.94 us | -0.5% |
| IndexLookupBenchmarks.PrimaryKey_BatchRandom | 40.54 us | 40.64 us | -0.2% |
| IndexLookupBenchmarks.PrimaryKey_BatchSequential | 33.70 us | 33.90 us | -0.6% |
| IndexLookupBenchmarks.PrimaryKey_PointLookup | 1.63 us | 1.61 us | +1.2% |
| PagedMMFBenchmarks.CacheHit | 11.78 ns | 12.45 ns | -5.4% |
| PagedMMFBenchmarks.PageAllocation | 1.52 us | 1.55 us | -2.0% |
| RevisionBenchmarks.Read_10Versions | 1.62 us | 1.62 us | +0.1% |
| RevisionBenchmarks.Read_50Versions | 1.64 us | 1.58 us | +3.7% |
| RevisionBenchmarks.Read_SingleVersion | 1.60 us | 1.62 us | -1.4% |
| SpawnBatchBenchmarks.SingleSpawnLoop (EntityCount=100) | 50.59 us | 50.95 us | -0.7% |
| SpawnBatchBenchmarks.SingleSpawnLoop (EntityCount=1000) | 529.67 us | 531.52 us | -0.3% |
| SpawnBatchBenchmarks.SpawnBatch_SOA (EntityCount=100) | 37.41 us | 37.93 us | -1.4% |
| SpawnBatchBenchmarks.SpawnBatch_SOA (EntityCount=1000) | 382.60 us | 387.06 us | -1.2% |
| SpawnBatchBenchmarks.SpawnBatch_SharedValues (EntityCount=100) | 43.31 us | 42.36 us | +2.2% |
| SpawnBatchBenchmarks.SpawnBatch_SharedValues (EntityCount=1000) | 435.91 us | 441.32 us | -1.2% |
| StrictModeOffPathBenchmarks.EmptyMethod (LoopCount=1024) | 459.32 ns | 484.91 ns | -5.3% |
| StrictModeOffPathBenchmarks.Require_ConditionFalse_OffPath (LoopCount=1024) | 9.77 us | 9.56 us | +2.2% |
| StrictModeOffPathBenchmarks.Require_ConditionTrue_OffPath (LoopCount=1024) | 9.82 us | 9.78 us | +0.5% |
| String64Benchmarks.Construct_FromString | 19.29 ns | 19.90 ns | -3.1% |
| TransactionBenchmarks.Transaction_BulkRead (EntityCount=100) | 30.61 us | 31.64 us | -3.3% |
| TransactionBenchmarks.Transaction_BulkRead (EntityCount=1000) | 330.35 us | 325.14 us | +1.6% |
| TransactionBenchmarks.Transaction_BulkUpdate (EntityCount=100) | 119.89 us | 120.22 us | -0.3% |
| TransactionBenchmarks.Transaction_BulkUpdate (EntityCount=1000) | 1.22 ms | 1.25 ms | -2.5% |
| TransactionBenchmarks.Transaction_CreateReadCommit (EntityCount=100) | 6.63 us | 6.64 us | -0.2% |
| TransactionBenchmarks.Transaction_CreateReadCommit (EntityCount=1000) | 6.43 us | 6.67 us | -3.6% |
| TyphonEventOffPathBenchmarks.BeginBTreeInsert_OffPath (LoopCount=1024) | 2.57 us | 2.57 us | -0.0% |
| TyphonEventOffPathBenchmarks.BeginCheckpointCycle_OffPath (LoopCount=1024) | 4.63 us | 4.64 us | -0.2% |
| TyphonEventOffPathBenchmarks.EmptyMethod (LoopCount=1024) | 463.74 ns | 467.55 ns | -0.8% |
| WorkloadBenchmarks.CrudLifecycle | 8.93 us | 8.87 us | +0.7% |
| WorkloadBenchmarks.MultiComponent_Crud | 9.26 us | 9.26 us | +0.0% |
| WorkloadBenchmarks.ReadHeavy_90_10 | 56.15 us | 55.67 us | +0.9% |
| WorkloadBenchmarks.WriteHeavy_Batch | 481.46 us | 500.30 us | -3.8% |
| WorkloadBenchmarks.WriteHeavy_SvIndexed_Batch | 644.86 us | 677.87 us | -4.9% |

</details>

<details>
<summary>Noisy Benchmarks (22) — filtered from regression detection</summary>

| Benchmark | Current | Previous | Change | Reason |
|-----------|---------|----------|--------|--------|
| AccessControlBenchmarks.SharedLock_Contended (ThreadCount=4) | 1.86 us | 1.27 us | +46.7% | high variance (CoV 32%) |
| ChunkAccessorBenchmarks.SIMD_Hit_4Chunks | 3.46 ns | 3.08 ns | +12.4% | abs delta 0.38ns < 0.5ns threshold |
| EpochGuardBenchmarks.MinActiveEpoch | 1.12 ns | 1.00 ns | +11.6% | abs delta 0.12ns < 0.5ns threshold |
| String64Benchmarks.Compare_Order | 4.11 ns | 4.21 ns | -2.3% | abs delta 0.10ns < 0.5ns threshold |
| AccessControlSmallBenchmarks.SharedLock_Uncontended | 17.00 ns | 17.29 ns | -1.7% | abs delta 0.29ns < 0.5ns threshold |
| AccessControlBenchmarks.ExclusiveLock_Uncontended (ThreadCount=4) | 21.80 ns | 21.49 ns | +1.4% | abs delta 0.31ns < 0.5ns threshold |
| AccessControlBenchmarks.ExclusiveLock_Uncontended (ThreadCount=2) | 21.69 ns | 21.96 ns | -1.2% | abs delta 0.27ns < 0.5ns threshold |
| AccessControlBenchmarks.ExclusiveLock_Uncontended (ThreadCount=8) | 21.50 ns | 21.76 ns | -1.2% | abs delta 0.26ns < 0.5ns threshold |
| AccessControlSmallBenchmarks.ExclusiveLock_Uncontended | 24.51 ns | 24.74 ns | -0.9% | abs delta 0.22ns < 0.5ns threshold |
| String64Benchmarks.Compare_Equal | 4.78 ns | 4.82 ns | -0.9% | abs delta 0.04ns < 0.5ns threshold |
| FindNextUnsetBenchmarks.FindNextUnset_Sparse25 | 5.97 ns | 6.01 ns | -0.6% | abs delta 0.03ns < 0.5ns threshold |
| FindNextUnsetBenchmarks.FindNextUnset_AlmostFull | 6.01 ns | 5.98 ns | +0.5% | abs delta 0.03ns < 0.5ns threshold |
| ChunkAccessorBenchmarks.MRU_Hit | 3.08 ns | 3.07 ns | +0.3% | abs delta 0.01ns < 0.5ns threshold |
| AccessControlBenchmarks.Promotion_SharedToExclusive (ThreadCount=2) | 44.12 ns | 44.24 ns | -0.3% | abs delta 0.11ns < 0.5ns threshold |
| String64Benchmarks.HashCode | 15.43 ns | 15.47 ns | -0.3% | abs delta 0.04ns < 0.5ns threshold |
| FindNextUnsetBenchmarks.FindNextUnset_Dense | 5.99 ns | 6.00 ns | -0.2% | abs delta 0.01ns < 0.5ns threshold |
| AccessControlBenchmarks.SharedLock_Uncontended (ThreadCount=4) | 18.77 ns | 18.74 ns | +0.1% | abs delta 0.03ns < 0.5ns threshold |
| AccessControlBenchmarks.SharedLock_Uncontended (ThreadCount=8) | 18.77 ns | 18.76 ns | +0.1% | abs delta 0.01ns < 0.5ns threshold |
| EpochGuardBenchmarks.EnterExit | 10.32 ns | 10.32 ns | +0.0% | abs delta 0.00ns < 0.5ns threshold |
| CascadeDeleteBenchmarks.CascadeDeleteAll (ParentCount=10&ChildrenPerParent=10) | 2.38 us | 2.38 us | -0.0% | abs delta 0.22ns < 0.5ns threshold |
| TyphonEventOffPathBenchmarks.BeginEcsSpawn_WithOptional_OffPath (LoopCount=1024) | 5.14 us | 5.14 us | -0.0% | abs delta 0.42ns < 0.5ns threshold |
| TyphonEventOffPathBenchmarks.BeginEcsSpawn_OffPath (LoopCount=1024) | 3.86 us | 3.86 us | +0.0% | abs delta 0.03ns < 0.5ns threshold |

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
![CascadeDeleteBenchmarks.CascadeDeleteAll (ParentCount=10&ChildrenPerParent=10)](charts/CascadeDeleteBenchmarks.CascadeDeleteAll_ParentCount_10%26ChildrenPerParent_10.svg)

![CascadeDeleteBenchmarks.CascadeDeleteAll (ParentCount=10&ChildrenPerParent=100)](charts/CascadeDeleteBenchmarks.CascadeDeleteAll_ParentCount_10%26ChildrenPerParent_100.svg)

![CascadeDeleteBenchmarks.CascadeDeleteAll (ParentCount=100&ChildrenPerParent=10)](charts/CascadeDeleteBenchmarks.CascadeDeleteAll_ParentCount_100%26ChildrenPerParent_10.svg)

![CascadeDeleteBenchmarks.CascadeDeleteAll (ParentCount=100&ChildrenPerParent=100)](charts/CascadeDeleteBenchmarks.CascadeDeleteAll_ParentCount_100%26ChildrenPerParent_100.svg)

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
