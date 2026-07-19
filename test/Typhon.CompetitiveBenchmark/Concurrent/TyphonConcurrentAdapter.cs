using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Typhon.CompetitiveBenchmark.Adapters;
using Typhon.Engine;
using Typhon.Schema.Definition;

namespace Typhon.CompetitiveBenchmark.Concurrent;

/// <summary>
/// Typhon — lock-free OLC reads (MVCC snapshot, no reader locking) and per-tick SoA writes. Single-thread it pays the
/// lock-free machinery; concurrency is where it should pull ahead. D0 (in-mem WAL) so we measure the engine, not fsync.
/// </summary>
public sealed class TyphonConcurrentAdapter : IConcurrentAdapter
{
    private readonly string _dbName = $"cbm_{Environment.ProcessId}";
    private ServiceProvider _sp;
    private DatabaseEngine _dbe;
    private EntityId[] _ids;

    public string Name => "Typhon SV";

    public void Load(int totalCount)
    {
        var sc = new ServiceCollection();
        sc.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical))
          .AddResourceRegistry().AddMemoryAllocator().AddEpochManager()
          .AddHighResolutionSharedTimer().AddDeadlineWatchdog()
          .AddScopedManagedPagedMemoryMappedFile(o =>
          {
              o.DatabaseName = _dbName;
              o.DatabaseCacheSize = (ulong)(65536 * PagedMMF.PageSize); // 512 MB
              o.PagesDebugPattern = false;
          })
          .AddSingleton<IWalFileIO>(_ => new InMemoryWalFileIO())
          .AddScopedDatabaseEngine(o => { o.Wal = new WalWriterOptions { UseFUA = false }; o.Resources.CheckpointIntervalMs = int.MaxValue; });
        _sp = sc.BuildServiceProvider();
        _sp.EnsureFileDeleted<ManagedPagedMMFOptions>();
        _dbe = _sp.GetRequiredService<DatabaseEngine>();
        _dbe.RegisterComponentFromAccessor<SvVal>();
        _dbe.InitializeArchetypes();

        _ids = new EntityId[totalCount];
        const int LoadBatch = 8192;
        var t = _dbe.CreateQuickTransaction();
        for (int i = 0; i < totalCount; i++)
        {
            var c = new SvVal { Value = i };
            _ids[i] = t.Spawn<SvValArch>(SvValArch.Data.Set(in c));
            if ((i + 1) % LoadBatch == 0) { t.Commit(); t.Dispose(); t = _dbe.CreateQuickTransaction(); }
        }
        t.Commit();
        t.Dispose();
        _dbe.WriteTickFence(1);
    }

    // CreateWorker() is invoked ON the worker thread by the runner — so a per-thread PTA stays thread-confined
    // (snapshot + per-worker EntityAccessor created and disposed on the same thread; no transaction-chain / pool /
    // epoch churn per op — the lock-free OLC read/SV-write path Typhon actually uses for parallel dispatch).
    public IWorker CreateWorker() => new Worker(_dbe, _ids);

    public void Dispose()
    {
        _dbe?.Dispose();
        _sp?.Dispose();
        try { File.Delete($"{_dbName}.bin"); } catch { }
    }

    private sealed class Worker : IWorker
    {
        private readonly EntityId[] _ids;
        private readonly PointInTimeAccessor _pta;
        private readonly EntityAccessor _acc;

        public Worker(DatabaseEngine dbe, EntityId[] ids)
        {
            _ids = ids;
            _pta = PointInTimeAccessor.Create(dbe, workerCount: 1); // frozen MVCC snapshot, this thread only
            _acc = _pta.GetWorkerAccessor(0);
        }

        public long ReadBatch(int startKey, int count)
        {
            long sum = 0;
            for (int i = 0; i < count; i++)
            {
                sum += _acc.Open(_ids[startKey + i]).Read(SvValArch.Data).Value;
            }
            return sum;
        }

        public void UpdateBatch(int startKey, int count, long seed)
        {
            // SV in-place write (last-writer-wins) — no commit, no WAL fsync (durability is at tick fence). Lock-free.
            for (int i = 0; i < count; i++)
            {
                _acc.OpenMut(_ids[startKey + i]).Write(SvValArch.Data).Value = seed + i;
            }
        }

        // Read-modify-write: read the current SV value, write +1 in place. Workers own disjoint partitions (each key one
        // thread), so the lean SV read+write is correct without a transaction or conflict detection.
        public void RmwBatch(int startKey, int count)
        {
            for (int i = 0; i < count; i++)
            {
                var id = _ids[startKey + i];
                long v = _acc.Open(id).Read(SvValArch.Data).Value;
                _acc.OpenMut(id).Write(SvValArch.Data).Value = v + 1;
            }
        }

        // Pending: Typhon's EntityMap is a hash (unordered). A fair YCSB-E range scan needs a B+Tree secondary index on the
        // key — wired once the index range-scan path is confirmed.
        public long RangeScan(int startKey, int length) =>
            throw new NotSupportedException("Typhon range scan pending B+Tree index wiring.");

        public void Dispose() => _pta.Dispose();
    }
}
