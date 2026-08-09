using System;
using System.Numerics;
using BenchmarkDotNet.Attributes;
using Typhon.Schema.Definition;

namespace Typhon.Benchmark;

// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════
// STORAGE-MODE COST PROOF
//
// This class exists to make Typhon's published per-operation numbers falsifiable. A figure like "~40 ns per write" is
// meaningless without a stated scope: is the entity already open, or does the number include finding it? Is it a single
// random entity, or one element of a bulk sweep? Does it include making the change durable?
//
// So every number here is measured at ONE of three explicitly-named scopes, and a published figure must cite which:
//
//   L1 — BULK / SYSTEM LOOP.  Per-entity cost inside a cluster sweep. The entity is NOT resolved per element: the loop
//        walks clusters, takes one SoA span per component, and indexes it. This is what an ECS system tick does, and it
//        is the scope where single-digit-nanosecond field costs are real.
//        NOTE: there is no meaningful L1 mutable-span scope for Versioned. The cluster slot holds only the HEAD cache;
//        the MVCC-correct value lives in the revision chain, and HEAD is written back at commit. A span write would
//        bypass TSN stamping, the isolation flag and the WAL record, and be visible to concurrent snapshot readers.
//        ClusterRef.GetSpan<T> does guard against it — but that guard is gated on CheckConfig.Enabled, which defaults
//        to FALSE, so it is an opt-in diagnostic, not an enforced invariant. The honest statement is "no code path
//        makes such a span MVCC-correct", not "the engine prevents it". That absence is a result, so it is recorded.
//
//   L2 — POINT ACCESS.  Cost of touching ONE entity addressed by EntityId: resolve (EntityMap probe + MVCC visibility +
//        cluster slot) THEN the field op. Reported as an absolute; subtract L2_Resolve_Only to get the field-op cost
//        alone. This is the scope for "I have an id, give me/change this component".
//
//   L3 — TRANSACTION ROUND TRIP.  Point write PLUS commit, i.e. the cost of making the change visible/durable under the
//        mode's discipline. This is the scope that answers "what does a durable write cost".
//
// Methodology
//   • OperationsPerInvoke divides by the op count, so every reported mean is PER OPERATION (per entity, or per write).
//     Transaction create/dispose therefore amortises to ~0 and does not inflate the per-op figure.
//   • L2 write benchmarks Rollback() — they isolate the write path from commit. L3 benchmarks Commit().
//   • The engine runs its real WAL + checkpoint pipeline against an in-memory WAL backend (zero disk I/O), so these are
//     CPU costs, not disk costs. L3 durability is Deferred: the commit is ordered and recoverable, the fsync is async.
//   • MemoryDiagnoser is on: the Allocated column is part of the proof (most of these paths are allocation-free).
//
// Everything is measured on the SAME fixture, same engine, same run — so the numbers are comparable to each other,
// which is the entire point.
// ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════════

[SimpleJob(warmupCount: 3, iterationCount: 10)]
[MemoryDiagnoser]
[BenchmarkCategory("StorageMode", "Regression")]
public class StorageModeProofBenchmarks : IDisposable
{
    /// <summary>Entities per archetype; the L1 sweeps walk all of them.</summary>
    private const int EntityCount = 10_000;

    /// <summary>Entities touched by the L2/L3 point benchmarks (a subset, so a Versioned COW run stays bounded).</summary>
    private const int PointOps = 1_000;

    private EcsOpFixture _f;
    private DatabaseEngine _dbe;

    [GlobalSetup]
    public void Setup()
    {
        _f = new EcsOpFixture(EntityCount, "StorageModeProof");
        _dbe = _f.Dbe;
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // L1 — BULK / SYSTEM LOOP.  Per-entity cost in a cluster sweep. No per-entity resolve.
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>L1 read, SingleVersion: SoA span index inside a cluster sweep.</summary>
    [Benchmark(OperationsPerInvoke = EntityCount, Baseline = true)]
    public float L1_Read_SingleVersion()
    {
        using var tx = _dbe.CreateQuickTransaction();
        var accessor = tx.For<AaBenchAnt>();
        float sum = 0;
        foreach (var cluster in accessor.GetClusterEnumerator())
        {
            var positions = cluster.GetReadOnlySpan(AaBenchAnt.Position);
            ulong bits = cluster.OccupancyBits;
            while (bits != 0)
            {
                int idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                sum += positions[idx].X;
            }
        }
        accessor.Dispose();
        return sum;
    }

    /// <summary>L1 read, Transient: same shape, transient segment.</summary>
    [Benchmark(OperationsPerInvoke = EntityCount)]
    public long L1_Read_Transient()
    {
        using var tx = _dbe.CreateQuickTransaction();
        var accessor = tx.For<AaBenchTransientUnit>();
        long sum = 0;
        foreach (var cluster in accessor.GetClusterEnumerator())
        {
            var data = cluster.GetReadOnlySpan(AaBenchTransientUnit.Data);
            ulong bits = cluster.OccupancyBits;
            while (bits != 0)
            {
                int idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                sum += data[idx].Value;
            }
        }
        accessor.Dispose();
        return sum;
    }

    /// <summary>
    /// L1 read, Versioned: reads the HEAD revision, which lives in the cluster slot — so a bulk versioned READ costs
    /// what an SV read costs. The MVCC price is paid on WRITE (copy-on-write) and on point-resolve, not on bulk read.
    /// </summary>
    [Benchmark(OperationsPerInvoke = EntityCount)]
    public long L1_Read_Versioned()
    {
        using var tx = _dbe.CreateQuickTransaction();
        var accessor = tx.For<AaBenchMixedCluster>();
        long sum = 0;
        foreach (var cluster in accessor.GetClusterEnumerator())
        {
            var healths = cluster.GetReadOnlySpan(AaBenchMixedCluster.Health);
            ulong bits = cluster.OccupancyBits;
            while (bits != 0)
            {
                int idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                sum += healths[idx].Current;
            }
        }
        accessor.Dispose();
        return sum;
    }

    /// <summary>L1 write, SingleVersion: in-place store through a mutable SoA span.</summary>
    [Benchmark(OperationsPerInvoke = EntityCount)]
    public void L1_Write_SingleVersion()
    {
        using var tx = _dbe.CreateQuickTransaction(DurabilityMode.Deferred);
        var accessor = tx.For<AaBenchAnt>();
        foreach (var cluster in accessor.GetClusterEnumerator())
        {
            var positions = cluster.GetSpan(AaBenchAnt.Position);
            ulong bits = cluster.OccupancyBits;
            while (bits != 0)
            {
                int idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                positions[idx].X += 1f;
            }
        }
        accessor.Dispose();
        tx.Rollback();
    }

    /// <summary>L1 write, Transient: in-place store, no dirty tracking (never persisted).</summary>
    [Benchmark(OperationsPerInvoke = EntityCount)]
    public void L1_Write_Transient()
    {
        using var tx = _dbe.CreateQuickTransaction(DurabilityMode.Deferred);
        var accessor = tx.For<AaBenchTransientUnit>();
        foreach (var cluster in accessor.GetClusterEnumerator())
        {
            var data = cluster.GetSpan(AaBenchTransientUnit.Data);
            ulong bits = cluster.OccupancyBits;
            while (bits != 0)
            {
                int idx = BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                data[idx].Value += 1;
            }
        }
        accessor.Dispose();
        tx.Rollback();
    }

    // NOTE: no L1_Write_Versioned — deliberately. A versioned write must allocate a new revision (copy-on-write), which
    // cannot be expressed as a store into the live SoA span: the span addresses the HEAD cache, not the chain. Writing
    // through it would skip TSN stamping / isolation flag / WAL and be seen by concurrent snapshot readers. Versioned
    // writes are therefore L2/L3 operations only. See L2_Write_Versioned / L3_WriteCommit_Versioned below.
    //
    // NOTE: no L1_Write_SingleVersion_Committed either, for the same class of reason. The whole CommitDiscipline.Commit
    // write path lives in EntityRef.Write<T> (EntityRef.cs:296-307) — CM-02 escalation via ResolveCommitDiscipline, the
    // shadow-index skip, then WriteEcsComponentData. ClusterRef.GetSpan<T> does none of it: it returns a raw Span<T> over
    // HEAD, and its only guard concerns Versioned. So a bulk span write inside a Commit-discipline transaction bypasses the
    // commit staging entirely and the discipline's atomicity does not apply to it.
    //   Such a benchmark would be trivial to write and would report ~0.74 ns — identical to L1_Write_SingleVersion, because
    // it IS that code path. That number would read as "Committed bulk writes are nearly free" when in fact the write was
    // never Committed. Measuring the wrong operation is worse than leaving the cell empty, so it stays empty.
    //   Worth flagging: unlike Versioned, this case has NO guard at all — not even a CheckConfig-gated one.

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // L2 — POINT ACCESS.  Resolve by EntityId, then the field op. Subtract L2_Resolve_Only for the field cost alone.
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// L2 baseline: resolve ONLY — EntityMap probe + MVCC visibility + cluster slot — with no field touched.
    /// Every other L2/L3 number includes this cost; subtract it to isolate the operation itself.
    /// </summary>
    [Benchmark(OperationsPerInvoke = PointOps)]
    public ulong L2_Resolve_Only()
    {
        using var tx = _dbe.CreateQuickTransaction();
        ulong sink = 0;
        for (int i = 0; i < PointOps; i++)
        {
            sink += tx.Open(_f.Sv[i]).Id.RawValue;
        }
        return sink;
    }

    [Benchmark(OperationsPerInvoke = PointOps)]
    public float L2_Read_SingleVersion()
    {
        using var tx = _dbe.CreateQuickTransaction();
        float sink = 0;
        for (int i = 0; i < PointOps; i++)
        {
            sink += tx.Open(_f.Sv[i]).Read(AaBenchAnt.Position).X;
        }
        return sink;
    }

    [Benchmark(OperationsPerInvoke = PointOps)]
    public long L2_Read_Transient()
    {
        using var tx = _dbe.CreateQuickTransaction();
        long sink = 0;
        for (int i = 0; i < PointOps; i++)
        {
            sink += tx.Open(_f.Transient[i]).Read(AaBenchTransientUnit.Data).Value;
        }
        return sink;
    }

    [Benchmark(OperationsPerInvoke = PointOps)]
    public long L2_Read_Versioned()
    {
        using var tx = _dbe.CreateQuickTransaction();
        long sink = 0;
        for (int i = 0; i < PointOps; i++)
        {
            sink += tx.Open(_f.Mixed[i]).Read(AaBenchMixedCluster.Health).Current;
        }
        return sink;
    }

    /// <summary>L2 write, SingleVersion under the default TickFence discipline: resolve + in-place store + SetDirty.</summary>
    [Benchmark(OperationsPerInvoke = PointOps)]
    public void L2_Write_SingleVersion()
    {
        using var tx = _dbe.CreateQuickTransaction(DurabilityMode.Deferred);
        for (int i = 0; i < PointOps; i++)
        {
            tx.OpenMut(_f.Sv[i]).Write(AaBenchAnt.Position).X = i;
        }
        tx.Rollback();
    }

    [Benchmark(OperationsPerInvoke = PointOps)]
    public void L2_Write_Transient()
    {
        using var tx = _dbe.CreateQuickTransaction(DurabilityMode.Deferred);
        for (int i = 0; i < PointOps; i++)
        {
            tx.OpenMut(_f.Transient[i]).Write(AaBenchTransientUnit.Data).Value = i;
        }
        tx.Rollback();
    }

    /// <summary>L2 write, Versioned: resolve + copy-on-write into a new revision. The MVCC price, isolated from commit.</summary>
    [Benchmark(OperationsPerInvoke = PointOps)]
    public void L2_Write_Versioned()
    {
        using var tx = _dbe.CreateQuickTransaction(DurabilityMode.Deferred);
        for (int i = 0; i < PointOps; i++)
        {
            tx.OpenMut(_f.Mixed[i]).Write(AaBenchMixedCluster.Health).Current = i;
        }
        tx.Rollback();
    }

    /// <summary>L2 write, SingleVersion under the Commit discipline: staged into the commit arena, HEAD untouched.</summary>
    [Benchmark(OperationsPerInvoke = PointOps)]
    public void L2_Write_SingleVersion_Committed()
    {
        using var tx = _dbe.CreateQuickTransaction(DurabilityMode.Deferred, CommitDiscipline.Commit);
        for (int i = 0; i < PointOps; i++)
        {
            tx.OpenMut(_f.Sv[i]).Write(AaBenchAnt.Position).X = i;
        }
        tx.Rollback();
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // L3 — TRANSACTION ROUND TRIP.  Point write + commit under the mode's discipline (Deferred durability).
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>L3, SingleVersion / TickFence: commit does no per-component durability work — the tick fence does it later.</summary>
    [Benchmark(OperationsPerInvoke = PointOps)]
    public void L3_WriteCommit_SingleVersion_TickFence()
    {
        using var tx = _dbe.CreateQuickTransaction(DurabilityMode.Deferred);
        for (int i = 0; i < PointOps; i++)
        {
            tx.OpenMut(_f.Sv[i]).Write(AaBenchAnt.Position).X = i;
        }
        tx.Commit();
    }

    /// <summary>L3, SingleVersion / Commit discipline: stage + build + append + publish to HEAD.</summary>
    [Benchmark(OperationsPerInvoke = PointOps)]
    public void L3_WriteCommit_SingleVersion_Committed()
    {
        using var tx = _dbe.CreateQuickTransaction(DurabilityMode.Deferred, CommitDiscipline.Commit);
        for (int i = 0; i < PointOps; i++)
        {
            tx.OpenMut(_f.Sv[i]).Write(AaBenchAnt.Position).X = i;
        }
        tx.Commit();
    }

    /// <summary>L3, Versioned: copy-on-write + revision chain stamp + WAL. The full MVCC durable write.</summary>
    [Benchmark(OperationsPerInvoke = PointOps)]
    public void L3_WriteCommit_Versioned()
    {
        using var tx = _dbe.CreateQuickTransaction(DurabilityMode.Deferred);
        for (int i = 0; i < PointOps; i++)
        {
            tx.OpenMut(_f.Mixed[i]).Write(AaBenchMixedCluster.Health).Current = i;
        }
        tx.Commit();
    }

    /// <summary>L3, Transient: committing changes nothing for transient data — it is never persisted. Shown for completeness.</summary>
    [Benchmark(OperationsPerInvoke = PointOps)]
    public void L3_WriteCommit_Transient()
    {
        using var tx = _dbe.CreateQuickTransaction(DurabilityMode.Deferred);
        for (int i = 0; i < PointOps; i++)
        {
            tx.OpenMut(_f.Transient[i]).Write(AaBenchTransientUnit.Data).Value = i;
        }
        tx.Commit();
    }

    public void Dispose()
    {
        _f?.Dispose();
        _f = null;
        GC.SuppressFinalize(this);
    }
}
