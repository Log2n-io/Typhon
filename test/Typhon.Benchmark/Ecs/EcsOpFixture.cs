using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;

namespace Typhon.Benchmark;

/// <summary>
/// Shared setup for the ECS op-matrix benchmarks (Resolve / Read / Write / ...). Builds one engine via
/// <see cref="BenchmarkEngine.BuildEcsEngine"/> and pre-spawns one entity set per (StorageMode × storage-shape) cell, so every
/// op class measures the same population against the same engine configuration.
///
/// The sets deliberately cover BOTH shapes, because the shape — not just the StorageMode — selects the code path:
///   Sv              AaBenchAnt            SV, CLUSTER   (SoA, the default path real workloads take)
///   Mixed           AaBenchMixedCluster   SV+Versioned, CLUSTER  (Versioned HEAD cached in the cluster slot)
///   VersionedLegacy AaBenchVersionedUnit  pure Versioned, LEGACY (flat path — same mode, different code)
///   Transient       AaBenchTransientUnit  Transient, CLUSTER
///   Indexed         AaBenchIdxUnit        SV + indexed field (shadow capture on write)
/// </summary>
internal sealed class EcsOpFixture : IDisposable
{
    private const int SpawnBatchSize = 1000;   // keeps each commit under the WAL claim limit

    public ServiceProvider Sp;
    public DatabaseEngine Dbe;

    public EntityId[] Sv;
    public EntityId[] Mixed;
    public EntityId[] VersionedLegacy;
    public EntityId[] Transient;
    public EntityId[] SvTransient;
    public EntityId[] Indexed;

    private readonly string _dbFile;

    public EcsOpFixture(int count, string nameStem, int cachePages = 200 * 1024)
    {
        _dbFile = $"{nameStem}_{Environment.ProcessId}.bin";
        Sp = BenchmarkEngine.BuildEcsEngine(cachePages, nameStem);
        Dbe = Sp.GetRequiredService<DatabaseEngine>();

        Dbe.RegisterComponentFromAccessor<AaBenchPosition>();
        Dbe.RegisterComponentFromAccessor<AaBenchMovement>();
        Dbe.RegisterComponentFromAccessor<AaVcHealth>();
        Dbe.RegisterComponentFromAccessor<AaBenchIdxData>();
        Dbe.RegisterComponentFromAccessor<AaBenchTransientData>();
        Dbe.InitializeArchetypes();

        Sv = new EntityId[count];
        Mixed = new EntityId[count];
        VersionedLegacy = new EntityId[count];
        Transient = new EntityId[count];
        SvTransient = new EntityId[count];
        Indexed = new EntityId[count];

        Batch(count, (tx, i) =>
        {
            var pos = new AaBenchPosition(i, i);
            var mov = new AaBenchMovement(1, 1);
            Sv[i] = tx.Spawn<AaBenchAnt>(AaBenchAnt.Position.Set(in pos), AaBenchAnt.Movement.Set(in mov));
        });

        Batch(count, (tx, i) =>
        {
            var pos = new AaBenchPosition(i, i);
            var mov = new AaBenchMovement(1, 1);
            var health = new AaVcHealth { Current = 100, Max = 100 };
            Mixed[i] = tx.Spawn<AaBenchMixedCluster>(
                AaBenchMixedCluster.Position.Set(in pos),
                AaBenchMixedCluster.Movement.Set(in mov),
                AaBenchMixedCluster.Health.Set(in health));
        });

        Batch(count, (tx, i) =>
        {
            var health = new AaVcHealth { Current = 100, Max = 100 };
            VersionedLegacy[i] = tx.Spawn<AaBenchVersionedUnit>(AaBenchVersionedUnit.Health.Set(in health));
        });

        Batch(count, (tx, i) =>
        {
            var data = new AaBenchTransientData(i, i);
            Transient[i] = tx.Spawn<AaBenchTransientUnit>(AaBenchTransientUnit.Data.Set(in data));
        });

        Batch(count, (tx, i) =>
        {
            var pos = new AaBenchPosition(i, i);
            var data = new AaBenchTransientData(i, i);
            SvTransient[i] = tx.Spawn<AaBenchSvTransientUnit>(
                AaBenchSvTransientUnit.Position.Set(in pos), AaBenchSvTransientUnit.Data.Set(in data));
        });

        Batch(count, (tx, i) =>
        {
            var pos = new AaBenchPosition(i, 0);
            var data = new AaBenchIdxData(i, 0);
            Indexed[i] = tx.Spawn<AaBenchIdxUnit>(AaBenchIdxUnit.Position.Set(in pos), AaBenchIdxUnit.Data.Set(in data));
        });

        // Populate cluster indexes and zone maps so reads/queries measure the steady state, not a cold index.
        Dbe.WriteTickFence(0);
    }

    private void Batch(int count, Action<Transaction, int> spawnOne)
    {
        int offset = 0;
        while (offset < count)
        {
            int batch = Math.Min(SpawnBatchSize, count - offset);
            using var tx = Dbe.CreateQuickTransaction();
            for (int i = 0; i < batch; i++)
            {
                spawnOne(tx, offset + i);
            }
            tx.Commit();
            offset += batch;
        }
    }

    public void Dispose()
    {
        Dbe?.Dispose();
        Sp?.Dispose();
        Dbe = null;
        Sp = null;
        try { File.Delete(_dbFile); } catch { /* best effort */ }
    }
}
