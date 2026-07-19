# Benchmark Regression Report

**Date:** 2026-07-19T19:18:50Z
**Commit:** e2374c4 (feature/514-source-generated-registry)
**Environment:** Intel Xeon Platinum 8151 CPU 3.40GHz | Linux Ubuntu 22.04.5 LTS (Jammy Jellyfish) | .NET 10.0.10

## Summary

| Status | Count |
|--------|-------|
| Regression | 1 |
| Improvement | 5 |
| Stable | 65 |
| Noisy (filtered) | 20 |
| Insufficient Data | 0 |

## Regressions

> [!WARNING]
> 1 benchmark(s) show performance regression

| Benchmark | Current | Previous | Change | Threshold |
|-----------|---------|----------|--------|-----------|
| FindNextUnsetBenchmarks.FindNextUnset_Sparse25 | 6.67 ns | 5.97 ns | +11.7% | 10% |

![FindNextUnsetBenchmarks.FindNextUnset_Sparse25](charts/FindNextUnsetBenchmarks.FindNextUnset_Sparse25.svg)

## Improvements

| Benchmark | Current | Previous | Change |
|-----------|---------|----------|--------|
| ChunkAccessorBenchmarks.MRU_Hit | 2.58 ns | 3.08 ns | -16.3% |
| PagedMMFBenchmarks.CacheMiss | 10.92 ns | 13.67 ns | -20.1% |
| AccessControlSmallBenchmarks.Promotion_SharedToExclusive | 36.36 ns | 45.54 ns | -20.1% |
| AccessControlSmallBenchmarks.ExclusiveLock_Uncontended | 18.51 ns | 24.51 ns | -24.5% |
| EcsQueryBenchmarks.Query_Any | 956.95 ns | 1.94 us | -50.8% |

<details>
<summary>Stable Benchmarks (65)</summary>

| Benchmark | Current | Previous | Change |
|-----------|---------|----------|--------|
| AccessControlBenchmarks.Promotion_SharedToExclusive (ThreadCount=8) | 44.34 ns | 43.73 ns | +1.4% |
| AccessControlBenchmarks.SharedLock_Contended (ThreadCount=2) | 841.00 ns | 940.00 ns | -10.5% |
| BTreeMicroBenchmarks.Delete_Reinsert | 914.66 ns | 911.35 ns | +0.4% |
| BTreeMicroBenchmarks.Insert_Random | 477.65 ns | 475.95 ns | +0.4% |
| BTreeMicroBenchmarks.Lookup_Hit | 345.01 ns | 350.38 ns | -1.5% |
| BTreeMicroBenchmarks.Lookup_Miss | 347.11 ns | 340.31 ns | +2.0% |
| BTreeMicroBenchmarks.SequentialScan_100 | 259.03 ns | 261.27 ns | -0.9% |
| CascadeDeleteBenchmarks.CascadeDeleteAll (ParentCount=10&ChildrenPerParent=10) | 2.35 us | 2.38 us | -1.2% |
| CascadeDeleteBenchmarks.CascadeDeleteAll (ParentCount=10&ChildrenPerParent=100) | 2.32 us | 2.40 us | -3.4% |
| CascadeDeleteBenchmarks.CascadeDeleteAll (ParentCount=100&ChildrenPerParent=10) | 15.59 us | 14.67 us | +6.3% |
| CascadeDeleteBenchmarks.CascadeDeleteAll (ParentCount=100&ChildrenPerParent=100) | 14.50 us | 15.16 us | -4.4% |
| ChunkAccessorBenchmarks.CommitChanges_AllDirty | 196.73 ns | 186.08 ns | +5.7% |
| ChunkAccessorBenchmarks.Dispose_16Slots | 172.17 ns | 165.85 ns | +3.8% |
| ClusterRegressionBenchmarks.ClusterIteration_MixedSvVersioned | 18.21 us | 18.53 us | -1.7% |
| ClusterRegressionBenchmarks.ClusterIteration_SV | 15.57 us | 15.89 us | -2.0% |
| ClusterRegressionBenchmarks.ClusterRandomAccess_Mixed | 25.99 us | 26.47 us | -1.8% |
| ClusterRegressionBenchmarks.IndexedQuery_1Percent | 12.13 us | 11.79 us | +2.9% |
| ClusterRegressionBenchmarks.OrderedQuery_Take100 | 17.82 us | 17.65 us | +1.0% |
| ClusterRegressionBenchmarks.VersionedWriteCommit | 143.76 us | 147.21 us | -2.3% |
| ComponentTableBenchmarks.CreateEntity_SingleComponent | 6.14 us | 6.57 us | -6.5% |
| ComponentTableBenchmarks.ReadComponent_ById | 1.53 us | 1.62 us | -5.7% |
| ComponentTableBenchmarks.UpdateComponent_SingleField | 4.23 us | 4.21 us | +0.4% |
| EcsQueryBenchmarks.EnableDisable_1000 | 353.59 us | 342.25 us | +3.3% |
| EcsQueryBenchmarks.Enabled_Query_Count | 259.76 us | 258.57 us | +0.5% |
| EcsQueryBenchmarks.ExactQuery_Count | 116.46 us | 123.21 us | -5.5% |
| EcsQueryBenchmarks.PolymorphicQuery_Count | 237.11 us | 251.43 us | -5.7% |
| EcsQueryBenchmarks.WhereField_Count | 710.83 us | 688.90 us | +3.2% |
| EpochGuardBenchmarks.MinActiveEpoch_WhilePinned | 17.95 ns | 17.01 ns | +5.6% |
| EpochGuardBenchmarks.NestedThreeLevels | 18.93 ns | 18.36 ns | +3.1% |
| IndexLookupBenchmarks.DeleteEntity_SingleComponent | 9.82 us | 9.89 us | -0.7% |
| IndexLookupBenchmarks.PrimaryKey_BatchRandom | 40.59 us | 40.54 us | +0.1% |
| IndexLookupBenchmarks.PrimaryKey_BatchSequential | 33.66 us | 33.70 us | -0.1% |
| IndexLookupBenchmarks.PrimaryKey_PointLookup | 1.50 us | 1.63 us | -7.8% |
| PagedMMFBenchmarks.CacheHit | 10.65 ns | 11.78 ns | -9.6% |
| PagedMMFBenchmarks.PageAllocation | 1.53 us | 1.52 us | +0.6% |
| RevisionBenchmarks.Read_10Versions | 1.50 us | 1.62 us | -7.4% |
| RevisionBenchmarks.Read_50Versions | 1.53 us | 1.64 us | -6.8% |
| RevisionBenchmarks.Read_SingleVersion | 1.53 us | 1.60 us | -4.1% |
| SpawnBatchBenchmarks.SingleSpawnLoop (EntityCount=100) | 51.07 us | 50.59 us | +1.0% |
| SpawnBatchBenchmarks.SingleSpawnLoop (EntityCount=1000) | 530.11 us | 529.67 us | +0.1% |
| SpawnBatchBenchmarks.SpawnBatch_SOA (EntityCount=100) | 37.90 us | 37.41 us | +1.3% |
| SpawnBatchBenchmarks.SpawnBatch_SOA (EntityCount=1000) | 394.39 us | 382.60 us | +3.1% |
| SpawnBatchBenchmarks.SpawnBatch_SharedValues (EntityCount=100) | 42.31 us | 43.31 us | -2.3% |
| SpawnBatchBenchmarks.SpawnBatch_SharedValues (EntityCount=1000) | 433.67 us | 435.91 us | -0.5% |
| StrictModeOffPathBenchmarks.EmptyMethod (LoopCount=1024) | 416.04 ns | 459.32 ns | -9.4% |
| StrictModeOffPathBenchmarks.Require_ConditionFalse_OffPath (LoopCount=1024) | 9.82 us | 9.77 us | +0.5% |
| StrictModeOffPathBenchmarks.Require_ConditionTrue_OffPath (LoopCount=1024) | 9.77 us | 9.82 us | -0.5% |
| String64Benchmarks.Compare_Equal | 4.20 ns | 4.78 ns | -12.0% |
| String64Benchmarks.Construct_FromString | 18.02 ns | 19.29 ns | -6.6% |
| String64Benchmarks.HashCode | 16.23 ns | 15.43 ns | +5.2% |
| TransactionBenchmarks.Transaction_BulkRead (EntityCount=100) | 30.98 us | 30.61 us | +1.2% |
| TransactionBenchmarks.Transaction_BulkRead (EntityCount=1000) | 314.93 us | 330.35 us | -4.7% |
| TransactionBenchmarks.Transaction_BulkUpdate (EntityCount=100) | 113.16 us | 119.89 us | -5.6% |
| TransactionBenchmarks.Transaction_BulkUpdate (EntityCount=1000) | 1.24 ms | 1.22 ms | +1.7% |
| TransactionBenchmarks.Transaction_CreateReadCommit (EntityCount=100) | 6.22 us | 6.63 us | -6.2% |
| TransactionBenchmarks.Transaction_CreateReadCommit (EntityCount=1000) | 6.51 us | 6.43 us | +1.3% |
| TyphonEventOffPathBenchmarks.BeginBTreeInsert_OffPath (LoopCount=1024) | 2.57 us | 2.57 us | +0.1% |
| TyphonEventOffPathBenchmarks.BeginCheckpointCycle_OffPath (LoopCount=1024) | 4.65 us | 4.63 us | +0.4% |
| TyphonEventOffPathBenchmarks.BeginEcsSpawn_WithOptional_OffPath (LoopCount=1024) | 5.14 us | 5.14 us | +0.0% |
| TyphonEventOffPathBenchmarks.EmptyMethod (LoopCount=1024) | 425.90 ns | 463.74 ns | -8.2% |
| WorkloadBenchmarks.CrudLifecycle | 9.33 us | 8.93 us | +4.4% |
| WorkloadBenchmarks.MultiComponent_Crud | 9.11 us | 9.26 us | -1.6% |
| WorkloadBenchmarks.ReadHeavy_90_10 | 56.18 us | 56.15 us | +0.1% |
| WorkloadBenchmarks.WriteHeavy_Batch | 515.80 us | 481.46 us | +7.1% |
| WorkloadBenchmarks.WriteHeavy_SvIndexed_Batch | 685.17 us | 644.86 us | +6.3% |

</details>

<details>
<summary>Noisy Benchmarks (20) — filtered from regression detection</summary>

| Benchmark | Current | Previous | Change | Reason |
|-----------|---------|----------|--------|--------|
| AccessControlBenchmarks.SharedLock_Contended (ThreadCount=8) | 5.04 us | 2.00 us | +151.9% | high variance (CoV 36%) |
| AccessControlBenchmarks.SharedLock_Contended (ThreadCount=4) | 2.64 us | 1.86 us | +42.2% | high variance (CoV 38%) |
| ChunkAccessorBenchmarks.SIMD_Hit_4Chunks | 3.85 ns | 3.46 ns | +11.4% | abs delta 0.39ns < 0.5ns threshold |
| EpochGuardBenchmarks.MinActiveEpoch | 1.18 ns | 1.12 ns | +5.4% | abs delta 0.06ns < 0.5ns threshold |
| AccessControlSmallBenchmarks.SharedLock_Uncontended | 17.30 ns | 17.00 ns | +1.8% | abs delta 0.30ns < 0.5ns threshold |
| ChunkAccessorBenchmarks.Eviction_17Chunks | 5.69 ns | 5.61 ns | +1.4% | abs delta 0.08ns < 0.5ns threshold |
| AccessControlBenchmarks.ExclusiveLock_Uncontended (ThreadCount=8) | 21.69 ns | 21.50 ns | +0.9% | abs delta 0.19ns < 0.5ns threshold |
| AccessControlBenchmarks.Promotion_SharedToExclusive (ThreadCount=4) | 44.30 ns | 44.69 ns | -0.9% | abs delta 0.39ns < 0.5ns threshold |
| AccessControlBenchmarks.SharedLock_Uncontended (ThreadCount=2) | 18.87 ns | 18.95 ns | -0.4% | abs delta 0.08ns < 0.5ns threshold |
| EpochGuardBenchmarks.EnterExit | 10.28 ns | 10.32 ns | -0.4% | abs delta 0.04ns < 0.5ns threshold |
| FindNextUnsetBenchmarks.FindNextUnset_Dense | 6.01 ns | 5.99 ns | +0.4% | abs delta 0.02ns < 0.5ns threshold |
| FindNextUnsetBenchmarks.FindNextUnset_AlmostFull | 5.99 ns | 6.01 ns | -0.3% | abs delta 0.02ns < 0.5ns threshold |
| AccessControlBenchmarks.ExclusiveLock_Uncontended (ThreadCount=2) | 21.64 ns | 21.69 ns | -0.2% | abs delta 0.05ns < 0.5ns threshold |
| AccessControlBenchmarks.SharedLock_Uncontended (ThreadCount=8) | 18.73 ns | 18.77 ns | -0.2% | abs delta 0.04ns < 0.5ns threshold |
| AccessControlBenchmarks.SharedLock_Uncontended (ThreadCount=4) | 18.73 ns | 18.77 ns | -0.2% | abs delta 0.04ns < 0.5ns threshold |
| AccessControlBenchmarks.Promotion_SharedToExclusive (ThreadCount=2) | 44.16 ns | 44.12 ns | +0.1% | abs delta 0.04ns < 0.5ns threshold |
| BTreeMicroBenchmarks.Insert_Sequential | 393.07 ns | 392.87 ns | +0.1% | abs delta 0.20ns < 0.5ns threshold |
| String64Benchmarks.Compare_Order | 4.12 ns | 4.11 ns | +0.0% | abs delta 0.00ns < 0.5ns threshold |
| AccessControlBenchmarks.ExclusiveLock_Uncontended (ThreadCount=4) | 21.81 ns | 21.80 ns | +0.0% | abs delta 0.00ns < 0.5ns threshold |
| TyphonEventOffPathBenchmarks.BeginEcsSpawn_OffPath (LoopCount=1024) | 3.86 us | 3.86 us | +0.0% | abs delta 0.35ns < 0.5ns threshold |

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
![CascadeDeleteBenchmarks.CascadeDeleteAll (ParentCount=10&ChildrenPerParent=10)](charts/CascadeDeleteBenchmarks.CascadeDeleteAll_ParentCount_10_ChildrenPerParent_10.svg)

![CascadeDeleteBenchmarks.CascadeDeleteAll (ParentCount=10&ChildrenPerParent=100)](charts/CascadeDeleteBenchmarks.CascadeDeleteAll_ParentCount_10_ChildrenPerParent_100.svg)

![CascadeDeleteBenchmarks.CascadeDeleteAll (ParentCount=100&ChildrenPerParent=10)](charts/CascadeDeleteBenchmarks.CascadeDeleteAll_ParentCount_100_ChildrenPerParent_10.svg)

![CascadeDeleteBenchmarks.CascadeDeleteAll (ParentCount=100&ChildrenPerParent=100)](charts/CascadeDeleteBenchmarks.CascadeDeleteAll_ParentCount_100_ChildrenPerParent_100.svg)

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
