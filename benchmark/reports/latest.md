# Benchmark Regression Report

**Date:** 2026-07-02T21:43:45Z
**Commit:** cb6caaf (feature/422-strict-mode-checks)
**Environment:** Intel Xeon Platinum 8259CL CPU 2.50GHz | Linux Ubuntu 22.04.5 LTS (Jammy Jellyfish) | .NET 10.0.9

## Summary

| Status | Count |
|--------|-------|
| Regression | 80 |
| Improvement | 1 |
| Stable | 1 |
| Noisy (filtered) | 1 |
| Insufficient Data | 8 |

## Regressions

> [!WARNING]
> 80 benchmark(s) show performance regression

| Benchmark | Current | Previous | Change | Threshold |
|-----------|---------|----------|--------|-----------|
| EcsQueryBenchmarks.ExactQuery_Count | 242.54 us | 44.06 us | +450.5% | 20% |
| EcsQueryBenchmarks.PolymorphicQuery_Count | 497.20 us | 98.23 us | +406.2% | 20% |
| EcsQueryBenchmarks.Enabled_Query_Count | 514.09 us | 105.72 us | +386.3% | 20% |
| AccessControlSmallBenchmarks.SharedLock_Uncontended | 21.63 ns | 4.59 ns | +370.9% | 10% |
| AccessControlSmallBenchmarks.ExclusiveLock_Uncontended | 30.55 ns | 7.48 ns | +308.4% | 10% |
| ChunkAccessorBenchmarks.SIMD_Hit_4Chunks | 4.31 ns | 1.07 ns | +302.5% | 8% |
| AccessControlSmallBenchmarks.Promotion_SharedToExclusive | 55.39 ns | 14.49 ns | +282.2% | 10% |
| EpochGuardBenchmarks.EnterExit | 12.99 ns | 3.51 ns | +269.9% | 5% |
| AccessControlBenchmarks.Promotion_SharedToExclusive (ThreadCount=4) | 55.18 ns | 15.55 ns | +254.8% | 10% |
| AccessControlBenchmarks.Promotion_SharedToExclusive (ThreadCount=2) | 55.17 ns | 15.57 ns | +254.2% | 10% |
| AccessControlBenchmarks.Promotion_SharedToExclusive (ThreadCount=8) | 54.57 ns | 15.42 ns | +254.0% | 10% |
| AccessControlBenchmarks.ExclusiveLock_Uncontended (ThreadCount=8) | 26.91 ns | 7.81 ns | +244.8% | 10% |
| AccessControlBenchmarks.ExclusiveLock_Uncontended (ThreadCount=2) | 27.29 ns | 8.07 ns | +238.3% | 10% |
| AccessControlBenchmarks.SharedLock_Uncontended (ThreadCount=8) | 23.80 ns | 7.09 ns | +235.8% | 10% |
| AccessControlBenchmarks.ExclusiveLock_Uncontended (ThreadCount=4) | 27.33 ns | 8.21 ns | +233.0% | 10% |
| ChunkAccessorBenchmarks.MRU_Hit | 3.85 ns | 1.16 ns | +232.8% | 5% |
| AccessControlBenchmarks.SharedLock_Uncontended (ThreadCount=4) | 23.54 ns | 7.17 ns | +228.5% | 10% |
| EpochGuardBenchmarks.MinActiveEpoch_WhilePinned | 21.48 ns | 6.56 ns | +227.2% | 8% |
| ChunkAccessorBenchmarks.CommitChanges_AllDirty | 229.31 ns | 70.57 ns | +224.9% | 8% |
| AccessControlBenchmarks.SharedLock_Uncontended (ThreadCount=2) | 23.51 ns | 7.41 ns | +217.4% | 10% |
| ChunkAccessorBenchmarks.Eviction_17Chunks | 7.05 ns | 2.29 ns | +208.1% | 8% |
| ClusterRegressionBenchmarks.OrderedQuery_Take100 | 22.98 us | 7.51 us | +205.8% | 15% |
| CascadeDeleteBenchmarks.CascadeDeleteAll (ParentCount=10&ChildrenPerParent=10) | 2.91 us | 1.00 us | +189.9% | 20% |
| WorkloadBenchmarks.CrudLifecycle | 10.19 us | 3.64 us | +179.7% | 20% |
| CascadeDeleteBenchmarks.CascadeDeleteAll (ParentCount=10&ChildrenPerParent=100) | 3.01 us | 1.08 us | +179.3% | 20% |
| CascadeDeleteBenchmarks.CascadeDeleteAll (ParentCount=100&ChildrenPerParent=100) | 17.91 us | 6.44 us | +178.1% | 20% |
| TransactionBenchmarks.Transaction_BulkUpdate (EntityCount=100) | 141.61 us | 51.47 us | +175.1% | 20% |
| EpochGuardBenchmarks.NestedThreeLevels | 24.26 ns | 8.85 ns | +174.2% | 8% |
| CascadeDeleteBenchmarks.CascadeDeleteAll (ParentCount=100&ChildrenPerParent=10) | 18.16 us | 6.66 us | +172.8% | 20% |
| ClusterRegressionBenchmarks.VersionedWriteCommit | 181.37 us | 66.55 us | +172.5% | 15% |
| TransactionBenchmarks.Transaction_BulkUpdate (EntityCount=1000) | 1.41 ms | 528.06 us | +166.4% | 20% |
| SpawnBatchBenchmarks.SpawnBatch_SharedValues (EntityCount=100) | 60.47 us | 22.82 us | +165.0% | 20% |
| ComponentTableBenchmarks.ReadComponent_ById | 2.00 us | 756.24 ns | +164.5% | 15% |
| ChunkAccessorBenchmarks.Dispose_16Slots | 205.01 ns | 77.74 ns | +163.7% | 8% |
| ComponentTableBenchmarks.UpdateComponent_SingleField | 5.19 us | 1.99 us | +160.8% | 15% |
| RevisionBenchmarks.Read_50Versions | 2.01 us | 770.99 ns | +160.6% | 10% |
| RevisionBenchmarks.Read_10Versions | 1.99 us | 768.95 ns | +158.6% | 10% |
| SpawnBatchBenchmarks.SpawnBatch_SOA (EntityCount=1000) | 520.85 us | 201.62 us | +158.3% | 20% |
| String64Benchmarks.Compare_Equal | 5.36 ns | 2.09 ns | +156.0% | 8% |
| IndexLookupBenchmarks.PrimaryKey_PointLookup | 2.02 us | 797.27 ns | +153.7% | 20% |
| SpawnBatchBenchmarks.SpawnBatch_SOA (EntityCount=100) | 49.40 us | 19.62 us | +151.8% | 20% |
| RevisionBenchmarks.Read_SingleVersion | 2.01 us | 801.25 ns | +151.2% | 10% |
| PagedMMFBenchmarks.CacheMiss | 11.88 ns | 4.78 ns | +148.7% | 15% |
| IndexLookupBenchmarks.PrimaryKey_BatchRandom | 52.81 us | 21.28 us | +148.2% | 20% |
| TransactionBenchmarks.Transaction_BulkRead (EntityCount=100) | 40.29 us | 16.29 us | +147.3% | 20% |
| ClusterRegressionBenchmarks.IndexedQuery_1Percent | 13.59 us | 5.53 us | +145.6% | 15% |
| ClusterRegressionBenchmarks.ClusterRandomAccess_Mixed | 41.13 us | 16.81 us | +144.6% | 15% |
| ClusterRegressionBenchmarks.ClusterIteration_MixedSvVersioned | 23.47 us | 9.66 us | +143.1% | 15% |
| SpawnBatchBenchmarks.SpawnBatch_SharedValues (EntityCount=1000) | 593.23 us | 245.33 us | +141.8% | 20% |
| WorkloadBenchmarks.ReadHeavy_90_10 | 71.54 us | 29.60 us | +141.7% | 20% |
| TransactionBenchmarks.Transaction_BulkRead (EntityCount=1000) | 409.47 us | 170.81 us | +139.7% | 20% |
| WorkloadBenchmarks.MultiComponent_Crud | 10.44 us | 4.48 us | +133.1% | 20% |
| String64Benchmarks.Construct_FromString | 24.32 ns | 10.58 ns | +129.8% | 8% |
| EcsQueryBenchmarks.Query_Any | 1.97 us | 868.07 ns | +126.5% | 20% |
| SpawnBatchBenchmarks.SingleSpawnLoop (EntityCount=100) | 65.63 us | 29.17 us | +125.0% | 20% |
| IndexLookupBenchmarks.PrimaryKey_BatchSequential | 45.11 us | 20.14 us | +123.9% | 20% |
| EcsQueryBenchmarks.EnableDisable_1000 | 429.23 us | 195.57 us | +119.5% | 20% |
| BTreeMicroBenchmarks.Lookup_Miss | 419.33 ns | 191.66 ns | +118.8% | 15% |
| SpawnBatchBenchmarks.SingleSpawnLoop (EntityCount=1000) | 654.19 us | 299.58 us | +118.4% | 20% |
| AccessControlBenchmarks.SharedLock_Contended (ThreadCount=4) | 2.09 us | 980.00 ns | +112.9% | 10% |
| PagedMMFBenchmarks.PageAllocation | 1.93 us | 917.14 ns | +110.7% | 15% |
| BTreeMicroBenchmarks.Lookup_Hit | 432.36 ns | 207.98 ns | +107.9% | 15% |
| BTreeMicroBenchmarks.Delete_Reinsert | 1.16 us | 562.28 ns | +106.1% | 15% |
| PagedMMFBenchmarks.CacheHit | 13.22 ns | 6.53 ns | +102.4% | 15% |
| BTreeMicroBenchmarks.SequentialScan_100 | 326.33 ns | 162.36 ns | +101.0% | 15% |
| EcsQueryBenchmarks.WhereField_Count | 856.36 us | 426.49 us | +100.8% | 20% |
| TransactionBenchmarks.Transaction_CreateReadCommit (EntityCount=1000) | 8.20 us | 4.12 us | +99.3% | 20% |
| BTreeMicroBenchmarks.Insert_Sequential | 473.78 ns | 238.34 ns | +98.8% | 15% |
| FindNextUnsetBenchmarks.FindNextUnset_AlmostFull | 7.48 ns | 3.82 ns | +95.9% | 10% |
| FindNextUnsetBenchmarks.FindNextUnset_Dense | 7.50 ns | 3.89 ns | +92.7% | 10% |
| ComponentTableBenchmarks.CreateEntity_SingleComponent | 7.77 us | 4.03 us | +92.7% | 15% |
| TransactionBenchmarks.Transaction_CreateReadCommit (EntityCount=100) | 7.77 us | 4.05 us | +91.9% | 20% |
| FindNextUnsetBenchmarks.FindNextUnset_Sparse25 | 7.48 ns | 3.93 ns | +90.7% | 10% |
| String64Benchmarks.HashCode | 19.38 ns | 10.23 ns | +89.4% | 8% |
| EpochGuardBenchmarks.MinActiveEpoch | 1.43 ns | 0.77 ns | +84.4% | 8% |
| BTreeMicroBenchmarks.Insert_Random | 596.34 ns | 342.41 ns | +74.2% | 15% |
| WorkloadBenchmarks.WriteHeavy_Batch | 403.07 us | 248.35 us | +62.3% | 20% |
| IndexLookupBenchmarks.DeleteEntity_SingleComponent | 10.32 us | 6.74 us | +53.2% | 20% |
| WorkloadBenchmarks.WriteHeavy_SvIndexed_Batch | 814.35 us | 599.02 us | +35.9% | 20% |
| ClusterRegressionBenchmarks.ClusterIteration_SV | 19.56 us | 15.70 us | +24.6% | 15% |

![EcsQueryBenchmarks.ExactQuery_Count](charts/EcsQueryBenchmarks.ExactQuery_Count.svg)

![EcsQueryBenchmarks.PolymorphicQuery_Count](charts/EcsQueryBenchmarks.PolymorphicQuery_Count.svg)

![EcsQueryBenchmarks.Enabled_Query_Count](charts/EcsQueryBenchmarks.Enabled_Query_Count.svg)

![AccessControlSmallBenchmarks.SharedLock_Uncontended](charts/AccessControlSmallBenchmarks.SharedLock_Uncontended.svg)

![AccessControlSmallBenchmarks.ExclusiveLock_Uncontended](charts/AccessControlSmallBenchmarks.ExclusiveLock_Uncontended.svg)

![ChunkAccessorBenchmarks.SIMD_Hit_4Chunks](charts/ChunkAccessorBenchmarks.SIMD_Hit_4Chunks.svg)

![AccessControlSmallBenchmarks.Promotion_SharedToExclusive](charts/AccessControlSmallBenchmarks.Promotion_SharedToExclusive.svg)

![EpochGuardBenchmarks.EnterExit](charts/EpochGuardBenchmarks.EnterExit.svg)

![AccessControlBenchmarks.Promotion_SharedToExclusive (ThreadCount=4)](charts/AccessControlBenchmarks.Promotion_SharedToExclusive_ThreadCount_4.svg)

![AccessControlBenchmarks.Promotion_SharedToExclusive (ThreadCount=2)](charts/AccessControlBenchmarks.Promotion_SharedToExclusive_ThreadCount_2.svg)

![AccessControlBenchmarks.Promotion_SharedToExclusive (ThreadCount=8)](charts/AccessControlBenchmarks.Promotion_SharedToExclusive_ThreadCount_8.svg)

![AccessControlBenchmarks.ExclusiveLock_Uncontended (ThreadCount=8)](charts/AccessControlBenchmarks.ExclusiveLock_Uncontended_ThreadCount_8.svg)

![AccessControlBenchmarks.ExclusiveLock_Uncontended (ThreadCount=2)](charts/AccessControlBenchmarks.ExclusiveLock_Uncontended_ThreadCount_2.svg)

![AccessControlBenchmarks.SharedLock_Uncontended (ThreadCount=8)](charts/AccessControlBenchmarks.SharedLock_Uncontended_ThreadCount_8.svg)

![AccessControlBenchmarks.ExclusiveLock_Uncontended (ThreadCount=4)](charts/AccessControlBenchmarks.ExclusiveLock_Uncontended_ThreadCount_4.svg)

![ChunkAccessorBenchmarks.MRU_Hit](charts/ChunkAccessorBenchmarks.MRU_Hit.svg)

![AccessControlBenchmarks.SharedLock_Uncontended (ThreadCount=4)](charts/AccessControlBenchmarks.SharedLock_Uncontended_ThreadCount_4.svg)

![EpochGuardBenchmarks.MinActiveEpoch_WhilePinned](charts/EpochGuardBenchmarks.MinActiveEpoch_WhilePinned.svg)

![ChunkAccessorBenchmarks.CommitChanges_AllDirty](charts/ChunkAccessorBenchmarks.CommitChanges_AllDirty.svg)

![AccessControlBenchmarks.SharedLock_Uncontended (ThreadCount=2)](charts/AccessControlBenchmarks.SharedLock_Uncontended_ThreadCount_2.svg)

![ChunkAccessorBenchmarks.Eviction_17Chunks](charts/ChunkAccessorBenchmarks.Eviction_17Chunks.svg)

![ClusterRegressionBenchmarks.OrderedQuery_Take100](charts/ClusterRegressionBenchmarks.OrderedQuery_Take100.svg)

![CascadeDeleteBenchmarks.CascadeDeleteAll (ParentCount=10&ChildrenPerParent=10)](charts/CascadeDeleteBenchmarks.CascadeDeleteAll_ParentCount_10&ChildrenPerParent_10.svg)

![WorkloadBenchmarks.CrudLifecycle](charts/WorkloadBenchmarks.CrudLifecycle.svg)

![CascadeDeleteBenchmarks.CascadeDeleteAll (ParentCount=10&ChildrenPerParent=100)](charts/CascadeDeleteBenchmarks.CascadeDeleteAll_ParentCount_10&ChildrenPerParent_100.svg)

![CascadeDeleteBenchmarks.CascadeDeleteAll (ParentCount=100&ChildrenPerParent=100)](charts/CascadeDeleteBenchmarks.CascadeDeleteAll_ParentCount_100&ChildrenPerParent_100.svg)

![TransactionBenchmarks.Transaction_BulkUpdate (EntityCount=100)](charts/TransactionBenchmarks.Transaction_BulkUpdate_EntityCount_100.svg)

![EpochGuardBenchmarks.NestedThreeLevels](charts/EpochGuardBenchmarks.NestedThreeLevels.svg)

![CascadeDeleteBenchmarks.CascadeDeleteAll (ParentCount=100&ChildrenPerParent=10)](charts/CascadeDeleteBenchmarks.CascadeDeleteAll_ParentCount_100&ChildrenPerParent_10.svg)

![ClusterRegressionBenchmarks.VersionedWriteCommit](charts/ClusterRegressionBenchmarks.VersionedWriteCommit.svg)

![TransactionBenchmarks.Transaction_BulkUpdate (EntityCount=1000)](charts/TransactionBenchmarks.Transaction_BulkUpdate_EntityCount_1000.svg)

![SpawnBatchBenchmarks.SpawnBatch_SharedValues (EntityCount=100)](charts/SpawnBatchBenchmarks.SpawnBatch_SharedValues_EntityCount_100.svg)

![ComponentTableBenchmarks.ReadComponent_ById](charts/ComponentTableBenchmarks.ReadComponent_ById.svg)

![ChunkAccessorBenchmarks.Dispose_16Slots](charts/ChunkAccessorBenchmarks.Dispose_16Slots.svg)

![ComponentTableBenchmarks.UpdateComponent_SingleField](charts/ComponentTableBenchmarks.UpdateComponent_SingleField.svg)

![RevisionBenchmarks.Read_50Versions](charts/RevisionBenchmarks.Read_50Versions.svg)

![RevisionBenchmarks.Read_10Versions](charts/RevisionBenchmarks.Read_10Versions.svg)

![SpawnBatchBenchmarks.SpawnBatch_SOA (EntityCount=1000)](charts/SpawnBatchBenchmarks.SpawnBatch_SOA_EntityCount_1000.svg)

![String64Benchmarks.Compare_Equal](charts/String64Benchmarks.Compare_Equal.svg)

![IndexLookupBenchmarks.PrimaryKey_PointLookup](charts/IndexLookupBenchmarks.PrimaryKey_PointLookup.svg)

![SpawnBatchBenchmarks.SpawnBatch_SOA (EntityCount=100)](charts/SpawnBatchBenchmarks.SpawnBatch_SOA_EntityCount_100.svg)

![RevisionBenchmarks.Read_SingleVersion](charts/RevisionBenchmarks.Read_SingleVersion.svg)

![PagedMMFBenchmarks.CacheMiss](charts/PagedMMFBenchmarks.CacheMiss.svg)

![IndexLookupBenchmarks.PrimaryKey_BatchRandom](charts/IndexLookupBenchmarks.PrimaryKey_BatchRandom.svg)

![TransactionBenchmarks.Transaction_BulkRead (EntityCount=100)](charts/TransactionBenchmarks.Transaction_BulkRead_EntityCount_100.svg)

![ClusterRegressionBenchmarks.IndexedQuery_1Percent](charts/ClusterRegressionBenchmarks.IndexedQuery_1Percent.svg)

![ClusterRegressionBenchmarks.ClusterRandomAccess_Mixed](charts/ClusterRegressionBenchmarks.ClusterRandomAccess_Mixed.svg)

![ClusterRegressionBenchmarks.ClusterIteration_MixedSvVersioned](charts/ClusterRegressionBenchmarks.ClusterIteration_MixedSvVersioned.svg)

![SpawnBatchBenchmarks.SpawnBatch_SharedValues (EntityCount=1000)](charts/SpawnBatchBenchmarks.SpawnBatch_SharedValues_EntityCount_1000.svg)

![WorkloadBenchmarks.ReadHeavy_90_10](charts/WorkloadBenchmarks.ReadHeavy_90_10.svg)

![TransactionBenchmarks.Transaction_BulkRead (EntityCount=1000)](charts/TransactionBenchmarks.Transaction_BulkRead_EntityCount_1000.svg)

![WorkloadBenchmarks.MultiComponent_Crud](charts/WorkloadBenchmarks.MultiComponent_Crud.svg)

![String64Benchmarks.Construct_FromString](charts/String64Benchmarks.Construct_FromString.svg)

![EcsQueryBenchmarks.Query_Any](charts/EcsQueryBenchmarks.Query_Any.svg)

![SpawnBatchBenchmarks.SingleSpawnLoop (EntityCount=100)](charts/SpawnBatchBenchmarks.SingleSpawnLoop_EntityCount_100.svg)

![IndexLookupBenchmarks.PrimaryKey_BatchSequential](charts/IndexLookupBenchmarks.PrimaryKey_BatchSequential.svg)

![EcsQueryBenchmarks.EnableDisable_1000](charts/EcsQueryBenchmarks.EnableDisable_1000.svg)

![BTreeMicroBenchmarks.Lookup_Miss](charts/BTreeMicroBenchmarks.Lookup_Miss.svg)

![SpawnBatchBenchmarks.SingleSpawnLoop (EntityCount=1000)](charts/SpawnBatchBenchmarks.SingleSpawnLoop_EntityCount_1000.svg)

![AccessControlBenchmarks.SharedLock_Contended (ThreadCount=4)](charts/AccessControlBenchmarks.SharedLock_Contended_ThreadCount_4.svg)

![PagedMMFBenchmarks.PageAllocation](charts/PagedMMFBenchmarks.PageAllocation.svg)

![BTreeMicroBenchmarks.Lookup_Hit](charts/BTreeMicroBenchmarks.Lookup_Hit.svg)

![BTreeMicroBenchmarks.Delete_Reinsert](charts/BTreeMicroBenchmarks.Delete_Reinsert.svg)

![PagedMMFBenchmarks.CacheHit](charts/PagedMMFBenchmarks.CacheHit.svg)

![BTreeMicroBenchmarks.SequentialScan_100](charts/BTreeMicroBenchmarks.SequentialScan_100.svg)

![EcsQueryBenchmarks.WhereField_Count](charts/EcsQueryBenchmarks.WhereField_Count.svg)

![TransactionBenchmarks.Transaction_CreateReadCommit (EntityCount=1000)](charts/TransactionBenchmarks.Transaction_CreateReadCommit_EntityCount_1000.svg)

![BTreeMicroBenchmarks.Insert_Sequential](charts/BTreeMicroBenchmarks.Insert_Sequential.svg)

![FindNextUnsetBenchmarks.FindNextUnset_AlmostFull](charts/FindNextUnsetBenchmarks.FindNextUnset_AlmostFull.svg)

![FindNextUnsetBenchmarks.FindNextUnset_Dense](charts/FindNextUnsetBenchmarks.FindNextUnset_Dense.svg)

![ComponentTableBenchmarks.CreateEntity_SingleComponent](charts/ComponentTableBenchmarks.CreateEntity_SingleComponent.svg)

![TransactionBenchmarks.Transaction_CreateReadCommit (EntityCount=100)](charts/TransactionBenchmarks.Transaction_CreateReadCommit_EntityCount_100.svg)

![FindNextUnsetBenchmarks.FindNextUnset_Sparse25](charts/FindNextUnsetBenchmarks.FindNextUnset_Sparse25.svg)

![String64Benchmarks.HashCode](charts/String64Benchmarks.HashCode.svg)

![EpochGuardBenchmarks.MinActiveEpoch](charts/EpochGuardBenchmarks.MinActiveEpoch.svg)

![BTreeMicroBenchmarks.Insert_Random](charts/BTreeMicroBenchmarks.Insert_Random.svg)

![WorkloadBenchmarks.WriteHeavy_Batch](charts/WorkloadBenchmarks.WriteHeavy_Batch.svg)

![IndexLookupBenchmarks.DeleteEntity_SingleComponent](charts/IndexLookupBenchmarks.DeleteEntity_SingleComponent.svg)

![WorkloadBenchmarks.WriteHeavy_SvIndexed_Batch](charts/WorkloadBenchmarks.WriteHeavy_SvIndexed_Batch.svg)

![ClusterRegressionBenchmarks.ClusterIteration_SV](charts/ClusterRegressionBenchmarks.ClusterIteration_SV.svg)

## Improvements

| Benchmark | Current | Previous | Change |
|-----------|---------|----------|--------|
| String64Benchmarks.Compare_Order | 5.18 ns | 7.95 ns | -34.8% |

<details>
<summary>Stable Benchmarks (1)</summary>

| Benchmark | Current | Previous | Change |
|-----------|---------|----------|--------|
| AccessControlBenchmarks.SharedLock_Contended (ThreadCount=2) | 1.07 us | 1.20 us | -10.9% |

</details>

<details>
<summary>Noisy Benchmarks (1) — filtered from regression detection</summary>

| Benchmark | Current | Previous | Change | Reason |
|-----------|---------|----------|--------|--------|
| AccessControlBenchmarks.SharedLock_Contended (ThreadCount=8) | 6.14 us | 1.06 us | +479.4% | high variance (CoV 39%) |

</details>

<details>
<summary>Insufficient Data (8)</summary>

| Benchmark | Current |
|-----------|---------|
| StrictModeOffPathBenchmarks.EmptyMethod (LoopCount=1024) | 579.93 ns |
| StrictModeOffPathBenchmarks.Require_ConditionFalse_OffPath (LoopCount=1024) | 12.33 us |
| StrictModeOffPathBenchmarks.Require_ConditionTrue_OffPath (LoopCount=1024) | 12.24 us |
| TyphonEventOffPathBenchmarks.BeginBTreeInsert_OffPath (LoopCount=1024) | 3.55 us |
| TyphonEventOffPathBenchmarks.BeginCheckpointCycle_OffPath (LoopCount=1024) | 5.83 us |
| TyphonEventOffPathBenchmarks.BeginEcsSpawn_OffPath (LoopCount=1024) | 4.84 us |
| TyphonEventOffPathBenchmarks.BeginEcsSpawn_WithOptional_OffPath (LoopCount=1024) | 6.45 us |
| TyphonEventOffPathBenchmarks.EmptyMethod (LoopCount=1024) | 578.13 ns |

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
