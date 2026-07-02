# Benchmark Regression Report

**Date:** 2026-07-02T22:40:41Z
**Commit:** d49948b (feature/422-strict-mode-checks)
**Environment:** Intel Xeon Platinum 8151 CPU 3.40GHz | Linux Ubuntu 22.04.5 LTS (Jammy Jellyfish) | .NET 10.0.9

## Summary

| Status | Count |
|--------|-------|
| Regression | 0 |
| Improvement | 0 |
| Stable | 0 |
| Noisy (filtered) | 0 |
| Insufficient Data | 91 |

## Regressions

No regressions detected.

## Improvements

No improvements detected.

<details>
<summary>Stable Benchmarks (0)</summary>

No stable benchmarks.

</details>

<details>
<summary>Noisy Benchmarks (0) — filtered from regression detection</summary>

No noisy benchmarks.

</details>

<details>
<summary>Insufficient Data (91)</summary>

| Benchmark | Current |
|-----------|---------|
| AccessControlBenchmarks.ExclusiveLock_Uncontended (ThreadCount=2) | 21.87 ns |
| AccessControlBenchmarks.ExclusiveLock_Uncontended (ThreadCount=4) | 21.82 ns |
| AccessControlBenchmarks.ExclusiveLock_Uncontended (ThreadCount=8) | 21.85 ns |
| AccessControlBenchmarks.Promotion_SharedToExclusive (ThreadCount=2) | 43.88 ns |
| AccessControlBenchmarks.Promotion_SharedToExclusive (ThreadCount=4) | 44.01 ns |
| AccessControlBenchmarks.Promotion_SharedToExclusive (ThreadCount=8) | 44.20 ns |
| AccessControlBenchmarks.SharedLock_Contended (ThreadCount=2) | 1.17 us |
| AccessControlBenchmarks.SharedLock_Contended (ThreadCount=4) | 922.75 ns |
| AccessControlBenchmarks.SharedLock_Contended (ThreadCount=8) | 4.62 us |
| AccessControlBenchmarks.SharedLock_Uncontended (ThreadCount=2) | 18.94 ns |
| AccessControlBenchmarks.SharedLock_Uncontended (ThreadCount=4) | 19.05 ns |
| AccessControlBenchmarks.SharedLock_Uncontended (ThreadCount=8) | 18.75 ns |
| AccessControlSmallBenchmarks.ExclusiveLock_Uncontended | 24.78 ns |
| AccessControlSmallBenchmarks.Promotion_SharedToExclusive | 45.38 ns |
| AccessControlSmallBenchmarks.SharedLock_Uncontended | 16.98 ns |
| BTreeMicroBenchmarks.Delete_Reinsert | 925.86 ns |
| BTreeMicroBenchmarks.Insert_Random | 479.07 ns |
| BTreeMicroBenchmarks.Insert_Sequential | 386.63 ns |
| BTreeMicroBenchmarks.Lookup_Hit | 362.40 ns |
| BTreeMicroBenchmarks.Lookup_Miss | 341.46 ns |
| BTreeMicroBenchmarks.SequentialScan_100 | 273.78 ns |
| CascadeDeleteBenchmarks.CascadeDeleteAll (ParentCount=10&ChildrenPerParent=10) | 2.30 us |
| CascadeDeleteBenchmarks.CascadeDeleteAll (ParentCount=10&ChildrenPerParent=100) | 2.35 us |
| CascadeDeleteBenchmarks.CascadeDeleteAll (ParentCount=100&ChildrenPerParent=10) | 14.94 us |
| CascadeDeleteBenchmarks.CascadeDeleteAll (ParentCount=100&ChildrenPerParent=100) | 14.89 us |
| ChunkAccessorBenchmarks.CommitChanges_AllDirty | 171.46 ns |
| ChunkAccessorBenchmarks.Dispose_16Slots | 166.66 ns |
| ChunkAccessorBenchmarks.Eviction_17Chunks | 5.64 ns |
| ChunkAccessorBenchmarks.MRU_Hit | 3.10 ns |
| ChunkAccessorBenchmarks.SIMD_Hit_4Chunks | 3.08 ns |
| ClusterRegressionBenchmarks.ClusterIteration_MixedSvVersioned | 18.20 us |
| ClusterRegressionBenchmarks.ClusterIteration_SV | 16.69 us |
| ClusterRegressionBenchmarks.ClusterRandomAccess_Mixed | 33.63 us |
| ClusterRegressionBenchmarks.IndexedQuery_1Percent | 11.60 us |
| ClusterRegressionBenchmarks.OrderedQuery_Take100 | 17.96 us |
| ClusterRegressionBenchmarks.VersionedWriteCommit | 151.73 us |
| ComponentTableBenchmarks.CreateEntity_SingleComponent | 6.22 us |
| ComponentTableBenchmarks.ReadComponent_ById | 1.66 us |
| ComponentTableBenchmarks.UpdateComponent_SingleField | 4.25 us |
| EcsQueryBenchmarks.EnableDisable_1000 | 350.65 us |
| EcsQueryBenchmarks.Enabled_Query_Count | 402.35 us |
| EcsQueryBenchmarks.ExactQuery_Count | 192.31 us |
| EcsQueryBenchmarks.PolymorphicQuery_Count | 401.33 us |
| EcsQueryBenchmarks.Query_Any | 1.56 us |
| EcsQueryBenchmarks.WhereField_Count | 706.80 us |
| EpochGuardBenchmarks.EnterExit | 10.96 ns |
| EpochGuardBenchmarks.MinActiveEpoch | 1.12 ns |
| EpochGuardBenchmarks.MinActiveEpoch_WhilePinned | 16.89 ns |
| EpochGuardBenchmarks.NestedThreeLevels | 18.64 ns |
| FindNextUnsetBenchmarks.FindNextUnset_AlmostFull | 5.99 ns |
| FindNextUnsetBenchmarks.FindNextUnset_Dense | 5.99 ns |
| FindNextUnsetBenchmarks.FindNextUnset_Sparse25 | 5.99 ns |
| IndexLookupBenchmarks.DeleteEntity_SingleComponent | 8.58 us |
| IndexLookupBenchmarks.PrimaryKey_BatchRandom | 42.86 us |
| IndexLookupBenchmarks.PrimaryKey_BatchSequential | 36.54 us |
| IndexLookupBenchmarks.PrimaryKey_PointLookup | 1.62 us |
| PagedMMFBenchmarks.CacheHit | 11.04 ns |
| PagedMMFBenchmarks.CacheMiss | 9.52 ns |
| PagedMMFBenchmarks.PageAllocation | 1.56 us |
| RevisionBenchmarks.Read_10Versions | 1.60 us |
| RevisionBenchmarks.Read_50Versions | 1.65 us |
| RevisionBenchmarks.Read_SingleVersion | 1.66 us |
| SpawnBatchBenchmarks.SingleSpawnLoop (EntityCount=100) | 52.75 us |
| SpawnBatchBenchmarks.SingleSpawnLoop (EntityCount=1000) | 538.73 us |
| SpawnBatchBenchmarks.SpawnBatch_SOA (EntityCount=100) | 39.33 us |
| SpawnBatchBenchmarks.SpawnBatch_SOA (EntityCount=1000) | 424.34 us |
| SpawnBatchBenchmarks.SpawnBatch_SharedValues (EntityCount=100) | 46.89 us |
| SpawnBatchBenchmarks.SpawnBatch_SharedValues (EntityCount=1000) | 501.21 us |
| StrictModeOffPathBenchmarks.EmptyMethod (LoopCount=1024) | 467.78 ns |
| StrictModeOffPathBenchmarks.Require_ConditionFalse_OffPath (LoopCount=1024) | 10.07 us |
| StrictModeOffPathBenchmarks.Require_ConditionTrue_OffPath (LoopCount=1024) | 9.53 us |
| String64Benchmarks.Compare_Equal | 4.65 ns |
| String64Benchmarks.Compare_Order | 5.03 ns |
| String64Benchmarks.Construct_FromString | 19.84 ns |
| String64Benchmarks.HashCode | 15.41 ns |
| TransactionBenchmarks.Transaction_BulkRead (EntityCount=100) | 32.90 us |
| TransactionBenchmarks.Transaction_BulkRead (EntityCount=1000) | 341.54 us |
| TransactionBenchmarks.Transaction_BulkUpdate (EntityCount=100) | 119.44 us |
| TransactionBenchmarks.Transaction_BulkUpdate (EntityCount=1000) | 1.20 ms |
| TransactionBenchmarks.Transaction_CreateReadCommit (EntityCount=100) | 6.80 us |
| TransactionBenchmarks.Transaction_CreateReadCommit (EntityCount=1000) | 6.59 us |
| TyphonEventOffPathBenchmarks.BeginBTreeInsert_OffPath (LoopCount=1024) | 2.58 us |
| TyphonEventOffPathBenchmarks.BeginCheckpointCycle_OffPath (LoopCount=1024) | 4.62 us |
| TyphonEventOffPathBenchmarks.BeginEcsSpawn_OffPath (LoopCount=1024) | 4.11 us |
| TyphonEventOffPathBenchmarks.BeginEcsSpawn_WithOptional_OffPath (LoopCount=1024) | 5.15 us |
| TyphonEventOffPathBenchmarks.EmptyMethod (LoopCount=1024) | 484.44 ns |
| WorkloadBenchmarks.CrudLifecycle | 9.26 us |
| WorkloadBenchmarks.MultiComponent_Crud | 8.67 us |
| WorkloadBenchmarks.ReadHeavy_90_10 | 57.48 us |
| WorkloadBenchmarks.WriteHeavy_Batch | 483.84 us |
| WorkloadBenchmarks.WriteHeavy_SvIndexed_Batch | 652.65 us |

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
